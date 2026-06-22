using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MesControlCenter.Core.Models;
using MySqlConnector;

namespace MesControlCenter.Core.Services;

public sealed class LocalBackupService : IDisposable
{
    private static readonly Regex TimeRegex = new(@"^([01]\d|2[0-3]):[0-5]\d$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".script_control_center");
    private static readonly string StateFile = Path.Combine(ConfigDir, "backup_config.dat");

    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private LocalBackupState _state;
    private Timer? _scheduleTimer;
    private DateTime? _nextRunAt;
    private bool _disposed;

    public event Action<BackupStatus>? BackupUpdated;

    public LocalBackupService()
    {
        Directory.CreateDirectory(ConfigDir);
        _state = LoadState();
        MarkStaleRunningAsFailed();
        Reschedule();
    }

    public Task<BackupStatus> GetStatusAsync()
    {
        lock (_stateLock)
            return Task.FromResult(BuildStatus());
    }

    public Task<IReadOnlyList<BackupRun>> GetRunsAsync(int limit = 50)
    {
        lock (_stateLock)
        {
            var runs = _state.Runs
                .OrderByDescending(r => r.Id)
                .Take(Math.Max(1, limit))
                .Select(CloneRun)
                .ToList();
            return Task.FromResult<IReadOnlyList<BackupRun>>(runs);
        }
    }

    public async Task<DatabaseHealthStatus> CheckDatabaseHealthAsync(TimeSpan? timeout = null)
    {
        BackupConfig config;
        lock (_stateLock)
            config = CloneConfig(_state.Config);

        if (string.IsNullOrWhiteSpace(config.DbHost)
            || string.IsNullOrWhiteSpace(config.DbUser)
            || string.IsNullOrWhiteSpace(config.DbDatabase))
        {
            return new DatabaseHealthStatus
            {
                IsConfigured = false,
                IsOnline = false,
                Message = "Database connection is not configured.",
                CheckedAt = DateTime.Now
            };
        }

        try
        {
            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(4));
            var builder = new MySqlConnectionStringBuilder
            {
                Server = config.DbHost,
                Port = (uint)config.DbPort,
                UserID = config.DbUser,
                Password = config.DbPassword,
                Database = config.DbDatabase,
                ConnectionTimeout = (uint)Math.Ceiling((timeout ?? TimeSpan.FromSeconds(4)).TotalSeconds),
                DefaultCommandTimeout = 4,
                SslMode = MySqlSslMode.Preferred
            };

            await using var connection = new MySqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cts.Token);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            await command.ExecuteScalarAsync(cts.Token);

            return new DatabaseHealthStatus
            {
                IsConfigured = true,
                IsOnline = true,
                Message = $"Connected to {config.DbHost}:{config.DbPort}/{config.DbDatabase}.",
                CheckedAt = DateTime.Now
            };
        }
        catch (Exception ex)
        {
            return new DatabaseHealthStatus
            {
                IsConfigured = true,
                IsOnline = false,
                Message = CompactError(ex.Message),
                CheckedAt = DateTime.Now
            };
        }
    }

    public Task<BackupConfig> UpdateConfigAsync(BackupConfig config)
    {
        var normalized = NormalizeConfig(config);
        ValidateConfig(normalized);

        lock (_stateLock)
        {
            _state.Config = normalized;
            SaveState();
        }

        Reschedule();
        EmitUpdate();
        return Task.FromResult(CloneConfig(normalized));
    }

    public async Task<bool> StartManualBackupAsync()
    {
        if (!await _runLock.WaitAsync(0))
        {
            AddSkippedRun("manual", "A backup is already running.");
            EmitUpdate();
            return false;
        }

        _ = Task.Run(() => RunBackupCoreAsync("manual"));
        return true;
    }

    private async Task StartScheduledBackupAsync()
    {
        if (!await _runLock.WaitAsync(0))
        {
            AddSkippedRun("scheduled", "A backup is already running.");
            EmitUpdate();
            return;
        }

        await RunBackupCoreAsync("scheduled");
    }

    private async Task RunBackupCoreAsync(string runType)
    {
        var startedAt = DateTime.Now;
        BackupRun run;
        BackupConfig config;

        lock (_stateLock)
        {
            config = CloneConfig(_state.Config);
            run = new BackupRun
            {
                Id = _state.NextRunId++,
                RunType = runType,
                Status = "running",
                StartedAt = startedAt
            };
            _state.Runs.Add(run);
            TrimRuns();
            SaveState();
        }

        EmitUpdate();

        try
        {
            if (runType == "scheduled" && !config.Enabled)
                throw new InvalidOperationException("Backups are disabled.");

            ValidateConfig(config);
            var result = await CreateDumpAsync(config);
            var finishedAt = DateTime.Now;

            lock (_stateLock)
            {
                var stored = _state.Runs.First(r => r.Id == run.Id);
                stored.Status = "success";
                stored.FinishedAt = finishedAt;
                stored.DurationMs = (int)Math.Min(int.MaxValue, (finishedAt - startedAt).TotalMilliseconds);
                stored.FilePath = result.FilePath;
                stored.FileSizeBytes = result.FileSizeBytes;
                stored.ErrorMessage = null;
                SaveState();
            }

            ApplyRetention(config, result.FilePath);
        }
        catch (Exception ex)
        {
            var finishedAt = DateTime.Now;
            lock (_stateLock)
            {
                var stored = _state.Runs.First(r => r.Id == run.Id);
                stored.Status = "failed";
                stored.FinishedAt = finishedAt;
                stored.DurationMs = (int)Math.Min(int.MaxValue, (finishedAt - startedAt).TotalMilliseconds);
                stored.ErrorMessage = CompactError(ex.Message);
                SaveState();
            }
        }
        finally
        {
            _runLock.Release();
            EmitUpdate();
            Reschedule();
        }
    }

    private async Task<DumpResult> CreateDumpAsync(BackupConfig config)
    {
        var backupDir = ResolveBackupDir(config.BackupDir);
        Directory.CreateDirectory(backupDir);

        var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
        var safeDb = SanitizeFilePart(config.DbDatabase);
        var filePath = Path.Combine(backupDir, $"{safeDb}_{stamp}.sql.gz");
        var tempPath = $"{filePath}.tmp";

        var psi = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(config.MysqldumpPath) ? "mysqldump" : config.MysqldumpPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        psi.ArgumentList.Add($"--host={config.DbHost}");
        psi.ArgumentList.Add($"--port={config.DbPort}");
        psi.ArgumentList.Add($"--user={config.DbUser}");
        psi.ArgumentList.Add("--single-transaction");
        psi.ArgumentList.Add("--quick");
        psi.ArgumentList.Add("--routines");
        psi.ArgumentList.Add("--events");
        psi.ArgumentList.Add("--triggers");
        psi.ArgumentList.Add("--databases");
        psi.ArgumentList.Add(config.DbDatabase);

        if (!string.IsNullOrEmpty(config.DbPassword))
            psi.Environment["MYSQL_PWD"] = config.DbPassword;

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Could not start mysqldump.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not start mysqldump: {ex.Message}", ex);
        }

        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await using (var output = File.Create(tempPath))
            await using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
            {
                await process.StandardOutput.BaseStream.CopyToAsync(gzip);
            }

            await process.WaitForExitAsync();
            var stderr = await stderrTask;
            if (process.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr)
                    ? $"mysqldump exited with code {process.ExitCode}."
                    : stderr);

            File.Move(tempPath, filePath, overwrite: true);
            var info = new FileInfo(filePath);
            if (!info.Exists || info.Length == 0)
                throw new InvalidOperationException("mysqldump produced an empty backup file.");

            return new DumpResult(filePath, info.Length);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private void ApplyRetention(BackupConfig config, string currentFilePath)
    {
        var backupDir = ResolveBackupDir(config.BackupDir);
        if (!Directory.Exists(backupDir))
            return;

        var cutoff = DateTime.Now.AddDays(-Math.Max(1, config.RetentionDays));
        var safeDb = SanitizeFilePart(config.DbDatabase);
        var currentFullPath = Path.GetFullPath(currentFilePath);

        foreach (var file in Directory.EnumerateFiles(backupDir, $"{safeDb}_*.sql.gz"))
        {
            try
            {
                if (Path.GetFullPath(file).Equals(currentFullPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                var info = new FileInfo(file);
                if (info.LastWriteTime < cutoff)
                    info.Delete();
            }
            catch
            {
                // Retention cleanup is best-effort; failed deletes should not fail the backup.
            }
        }
    }

    private void Reschedule()
    {
        _scheduleTimer?.Dispose();
        _scheduleTimer = null;

        BackupConfig config;
        lock (_stateLock)
            config = CloneConfig(_state.Config);

        _nextRunAt = ComputeNextRunAt(config);
        if (!config.Enabled || _nextRunAt == null || _disposed)
            return;

        var due = _nextRunAt.Value - DateTime.Now;
        if (due < TimeSpan.Zero)
            due = TimeSpan.Zero;

        _scheduleTimer = new Timer(async _ =>
        {
            await StartScheduledBackupAsync();
        }, null, due, Timeout.InfiniteTimeSpan);
    }

    private DateTime? ComputeNextRunAt(BackupConfig config)
    {
        if (!config.Enabled || !TimeRegex.IsMatch(config.BackupTime))
            return null;

        var parts = config.BackupTime.Split(':');
        var hour = int.Parse(parts[0]);
        var minute = int.Parse(parts[1]);
        var now = DateTime.Now;
        var next = new DateTime(now.Year, now.Month, now.Day, hour, minute, 0);
        if (next <= now)
            next = next.AddDays(1);
        return next;
    }

    private BackupStatus BuildStatus()
    {
        return new BackupStatus
        {
            Config = CloneConfig(_state.Config),
            IsRunning = _state.Runs.Any(r => string.Equals(r.Status, "running", StringComparison.OrdinalIgnoreCase)),
            NextRunAt = _nextRunAt,
            LastRun = _state.Runs
                .Where(r => !string.Equals(r.Status, "running", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.Id)
                .Select(CloneRun)
                .FirstOrDefault()
        };
    }

    private void EmitUpdate()
    {
        BackupStatus status;
        lock (_stateLock)
            status = BuildStatus();
        BackupUpdated?.Invoke(status);
    }

    private void AddSkippedRun(string runType, string error)
    {
        lock (_stateLock)
        {
            var now = DateTime.Now;
            _state.Runs.Add(new BackupRun
            {
                Id = _state.NextRunId++,
                RunType = runType,
                Status = "skipped",
                StartedAt = now,
                FinishedAt = now,
                DurationMs = 0,
                ErrorMessage = error
            });
            TrimRuns();
            SaveState();
        }
    }

    private void MarkStaleRunningAsFailed()
    {
        lock (_stateLock)
        {
            var changed = false;
            foreach (var run in _state.Runs.Where(r => string.Equals(r.Status, "running", StringComparison.OrdinalIgnoreCase)))
            {
                run.Status = "failed";
                run.FinishedAt = DateTime.Now;
                run.ErrorMessage = "Application closed before backup finished.";
                changed = true;
            }

            if (changed)
                SaveState();
        }
    }

    private LocalBackupState LoadState()
    {
        if (!File.Exists(StateFile))
            return new LocalBackupState { Config = CreateDefaultConfig() };

        try
        {
            var encrypted = File.ReadAllBytes(StateFile);
            var plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            var state = JsonSerializer.Deserialize<LocalBackupState>(plain, JsonOptions);
            if (state == null)
                return new LocalBackupState { Config = CreateDefaultConfig() };

            state.Config = NormalizeConfig(state.Config ?? CreateDefaultConfig());
            state.Runs ??= new List<BackupRun>();
            state.NextRunId = Math.Max(state.NextRunId, state.Runs.Count == 0 ? 1 : state.Runs.Max(r => r.Id) + 1);
            return state;
        }
        catch
        {
            return new LocalBackupState { Config = CreateDefaultConfig() };
        }
    }

    private void SaveState()
    {
        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.SerializeToUtf8Bytes(_state, JsonOptions);
        var encrypted = ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(StateFile, encrypted);
    }

    private static BackupConfig CreateDefaultConfig()
    {
        return NormalizeConfig(new BackupConfig
        {
            Enabled = ReadBool("BACKUP_ENABLED", true),
            BackupTime = ReadString("BACKUP_TIME", "22:00"),
            RetentionDays = ReadInt("BACKUP_RETENTION_DAYS", 7),
            BackupDir = ReadString("BACKUP_DIR", Path.Combine(ConfigDir, "backups")),
            Timezone = TimeZoneInfo.Local.Id,
            DbHost = ReadString("DB_HOST", "localhost"),
            DbPort = ReadInt("DB_PORT", 3306),
            DbUser = ReadString("DB_USER", string.Empty),
            DbPassword = ReadString("DB_PASSWORD", string.Empty),
            DbDatabase = ReadString("DB_DATABASE", string.Empty),
            MysqldumpPath = ReadString("MYSQLDUMP_PATH", "mysqldump")
        });
    }

    private static BackupConfig NormalizeConfig(BackupConfig config)
    {
        return new BackupConfig
        {
            Enabled = config.Enabled,
            BackupTime = string.IsNullOrWhiteSpace(config.BackupTime) ? "22:00" : config.BackupTime.Trim(),
            RetentionDays = Math.Max(1, config.RetentionDays),
            BackupDir = string.IsNullOrWhiteSpace(config.BackupDir) ? Path.Combine(ConfigDir, "backups") : config.BackupDir.Trim(),
            Timezone = string.IsNullOrWhiteSpace(config.Timezone) ? TimeZoneInfo.Local.Id : config.Timezone.Trim(),
            DbHost = string.IsNullOrWhiteSpace(config.DbHost) ? "localhost" : config.DbHost.Trim(),
            DbPort = config.DbPort <= 0 ? 3306 : config.DbPort,
            DbUser = config.DbUser.Trim(),
            DbPassword = config.DbPassword,
            DbDatabase = config.DbDatabase.Trim(),
            MysqldumpPath = string.IsNullOrWhiteSpace(config.MysqldumpPath) ? "mysqldump" : config.MysqldumpPath.Trim()
        };
    }

    private static void ValidateConfig(BackupConfig config)
    {
        if (!TimeRegex.IsMatch(config.BackupTime))
            throw new InvalidOperationException("Backup time must use HH:mm format.");
        if (config.RetentionDays < 1)
            throw new InvalidOperationException("Retention days must be a positive number.");
        if (string.IsNullOrWhiteSpace(config.BackupDir))
            throw new InvalidOperationException("Backup folder is required.");
        if (string.IsNullOrWhiteSpace(config.DbHost))
            throw new InvalidOperationException("Database host is required.");
        if (config.DbPort <= 0 || config.DbPort > 65535)
            throw new InvalidOperationException("Database port must be between 1 and 65535.");
        if (string.IsNullOrWhiteSpace(config.DbUser))
            throw new InvalidOperationException("Database user is required.");
        if (string.IsNullOrWhiteSpace(config.DbDatabase))
            throw new InvalidOperationException("Database name is required.");
        if (string.IsNullOrWhiteSpace(config.MysqldumpPath))
            throw new InvalidOperationException("mysqldump path is required.");
    }

    private static BackupConfig CloneConfig(BackupConfig config) => new()
    {
        Enabled = config.Enabled,
        BackupTime = config.BackupTime,
        RetentionDays = config.RetentionDays,
        BackupDir = config.BackupDir,
        Timezone = config.Timezone,
        DbHost = config.DbHost,
        DbPort = config.DbPort,
        DbUser = config.DbUser,
        DbPassword = config.DbPassword,
        DbDatabase = config.DbDatabase,
        MysqldumpPath = config.MysqldumpPath
    };

    private static BackupRun CloneRun(BackupRun run) => new()
    {
        Id = run.Id,
        RunType = run.RunType,
        Status = run.Status,
        StartedAt = run.StartedAt,
        FinishedAt = run.FinishedAt,
        DurationMs = run.DurationMs,
        FileSizeBytes = run.FileSizeBytes,
        FilePath = run.FilePath,
        ErrorMessage = run.ErrorMessage
    };

    private void TrimRuns()
    {
        const int maxRuns = 200;
        if (_state.Runs.Count <= maxRuns)
            return;

        _state.Runs = _state.Runs
            .OrderByDescending(r => r.Id)
            .Take(maxRuns)
            .OrderBy(r => r.Id)
            .ToList();
    }

    private static string ResolveBackupDir(string backupDir)
    {
        return Path.IsPathRooted(backupDir)
            ? Path.GetFullPath(backupDir)
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, backupDir));
    }

    private static string SanitizeFilePart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var sanitized = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "database" : sanitized;
    }

    private static string CompactError(string message)
        => Regex.Replace(message, @"\s+", " ").Trim()[..Math.Min(1024, Regex.Replace(message, @"\s+", " ").Trim().Length)];

    private static string ReadString(string name, string fallback)
        => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))
            ? fallback
            : Environment.GetEnvironmentVariable(name)!.Trim();

    private static int ReadInt(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;

    private static bool ReadBool(string name, bool fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        return value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }

    public void Dispose()
    {
        _disposed = true;
        _scheduleTimer?.Dispose();
        _runLock.Dispose();
    }

    private sealed class LocalBackupState
    {
        public BackupConfig Config { get; set; } = new();
        public List<BackupRun> Runs { get; set; } = new();
        public long NextRunId { get; set; } = 1;
    }

    private sealed record DumpResult(string FilePath, long FileSizeBytes);
}
