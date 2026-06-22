using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using MesControlCenter.Core.Models;

namespace MesControlCenter.Core.Services;

public sealed class GitDeployRequest
{
    public required ScriptEntry Entry { get; init; }
    public required Action<string> Log { get; init; }
    public required Func<Task<bool>> StopScriptAsync { get; init; }
    public required Func<Task<bool>> StartScriptAsync { get; init; }
    public required Func<Task<bool>> WaitForHealthyAsync { get; init; }
}

public sealed class GitDeployResult
{
    public bool Succeeded { get; init; }
    public bool RolledBack { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? PreviousCommit { get; init; }
    public string? TargetCommit { get; init; }
}

public sealed class GitDeployService
{
    private const int DefaultCommandTimeoutSeconds = 60;
    private const int FetchTimeoutSeconds = 180;
    private const int MergeTimeoutSeconds = 180;
    private const int PostPullTimeoutSeconds = 600;

    public async Task<string> GetCurrentCommitSummaryAsync(
        ScriptEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (!entry.GitDeployEnabled || entry.IsPsCommand || entry.IsNpmCommand)
            return string.Empty;

        var repoDir = entry.GitRepoDir.Trim();
        if (string.IsNullOrWhiteSpace(repoDir))
            return "Repo not configured";

        if (!Directory.Exists(repoDir))
            return "Repo not found";

        await EnsureGitAvailableAsync(repoDir, cancellationToken);
        await EnsureRepositoryAsync(repoDir, cancellationToken);

        var currentBranch = await ReadGitValueAsync(repoDir, "rev-parse --abbrev-ref HEAD", cancellationToken);
        var branchText = string.Equals(currentBranch, "HEAD", StringComparison.OrdinalIgnoreCase)
            ? "detached HEAD"
            : currentBranch;

        var latestCommit = await ReadGitValueAsync(repoDir, "log -1 --format=%h%x09%s", cancellationToken);
        if (string.IsNullOrWhiteSpace(latestCommit))
            return branchText;

        var parts = latestCommit.Split('\t', 2);
        return parts.Length == 2
            ? $"{branchText} @ {parts[0]} - {parts[1]}"
            : $"{branchText} @ {latestCommit}";
    }

    public async Task<GitDeployResult> DeployAsync(GitDeployRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Entry);

        var entry = request.Entry;
        var repoDir = entry.GitRepoDir.Trim();
        var branch = entry.GitBranch.Trim();

        if (!entry.GitDeployEnabled)
            return Failure("Git deploy is not enabled for this script.");

        if (entry.IsPsCommand || entry.IsNpmCommand)
            return Failure("Git deploy is only supported for regular script entries.");

        if (string.IsNullOrWhiteSpace(repoDir))
            return Failure("Git repository directory is required.");

        if (!Directory.Exists(repoDir))
            return Failure($"Repository directory does not exist: {repoDir}");

        if (string.IsNullOrWhiteSpace(branch))
            return Failure("Git branch is required.");

        string? previousCommit = null;
        string? targetCommit = null;

        try
        {
            request.Log($"Validating git environment in {repoDir}");

            await EnsureGitAvailableAsync(repoDir, cancellationToken);
            await EnsureRepositoryAsync(repoDir, cancellationToken);

            var currentBranch = await ReadGitValueAsync(repoDir, "rev-parse --abbrev-ref HEAD", cancellationToken);
            if (string.Equals(currentBranch, "HEAD", StringComparison.OrdinalIgnoreCase))
                return Failure("Repository is in detached HEAD state. Checkout the configured branch before deploying.");

            if (!string.Equals(currentBranch, branch, StringComparison.OrdinalIgnoreCase))
            {
                return Failure(
                    $"Checked out branch '{currentBranch}' does not match configured branch '{branch}'.");
            }

            var status = await ReadGitValueAsync(repoDir, "status --porcelain", cancellationToken);
            if (!string.IsNullOrWhiteSpace(status))
                return Failure("Repository has local changes. Commit or discard them before deploying.");

            previousCommit = await ReadGitValueAsync(repoDir, "rev-parse HEAD", cancellationToken);
            request.Log($"Current commit: {previousCommit}");

            request.Log($"Fetching origin/{branch}");
            await RunGitAsync(repoDir, $"fetch origin \"{branch}\"", FetchTimeoutSeconds, cancellationToken);

            targetCommit = await ReadGitValueAsync(
                repoDir,
                $"rev-parse --verify \"refs/remotes/origin/{branch}\"",
                cancellationToken);

            request.Log($"Remote commit: {targetCommit}");

            if (string.Equals(previousCommit, targetCommit, StringComparison.OrdinalIgnoreCase))
            {
                return new GitDeployResult
                {
                    Succeeded = true,
                    RolledBack = false,
                    Message = $"No changes detected on origin/{branch}.",
                    PreviousCommit = previousCommit,
                    TargetCommit = targetCommit
                };
            }

            request.Log("Stopping service before applying update");
            if (!await request.StopScriptAsync())
                return Failure("Service could not be stopped cleanly.", previousCommit, targetCommit);

            try
            {
                request.Log($"Fast-forwarding local branch '{branch}'");
                await RunGitAsync(repoDir, $"merge --ff-only \"origin/{branch}\"", MergeTimeoutSeconds, cancellationToken);

                if (!string.IsNullOrWhiteSpace(entry.GitPostPullCommand))
                {
                    request.Log("Running post-pull command");
                    await RunShellCommandAsync(repoDir, entry.GitPostPullCommand, PostPullTimeoutSeconds, cancellationToken);
                }

                request.Log("Starting updated service");
                if (!await request.StartScriptAsync())
                    throw new GitDeployException("Updated service failed to start.");

                request.Log("Waiting for service validation");
                if (!await request.WaitForHealthyAsync())
                    throw new GitDeployException("Updated service did not pass startup validation.");

                request.Log("Deploy completed successfully");
                return new GitDeployResult
                {
                    Succeeded = true,
                    RolledBack = false,
                    Message = $"Deploy completed successfully to {targetCommit}.",
                    PreviousCommit = previousCommit,
                    TargetCommit = targetCommit
                };
            }
            catch (Exception ex) when (ex is GitDeployException || ex is InvalidOperationException)
            {
                request.Log($"Deploy failed after stop: {ex.Message}");

                if (!entry.GitRollbackOnFailure)
                {
                    return Failure(
                        $"Deploy failed and rollback is disabled: {ex.Message}",
                        previousCommit,
                        targetCommit);
                }

                var rollback = await TryRollbackAsync(request, repoDir, branch, previousCommit, cancellationToken);
                return new GitDeployResult
                {
                    Succeeded = false,
                    RolledBack = rollback.RepoRestored,
                    Message = rollback.Message,
                    PreviousCommit = previousCommit,
                    TargetCommit = targetCommit
                };
            }
        }
        catch (Exception ex) when (ex is GitDeployException || ex is InvalidOperationException)
        {
            request.Log($"Deploy aborted: {ex.Message}");
            return Failure(ex.Message, previousCommit, targetCommit);
        }
    }

    private static GitDeployResult Failure(string message, string? previousCommit = null, string? targetCommit = null)
        => new()
        {
            Succeeded = false,
            RolledBack = false,
            Message = message,
            PreviousCommit = previousCommit,
            TargetCommit = targetCommit
        };

    private async Task EnsureGitAvailableAsync(string repoDir, CancellationToken cancellationToken)
    {
        await RunGitAsync(repoDir, "--version", DefaultCommandTimeoutSeconds, cancellationToken);
    }

    private async Task EnsureRepositoryAsync(string repoDir, CancellationToken cancellationToken)
    {
        var insideWorkTree = await ReadGitValueAsync(repoDir, "rev-parse --is-inside-work-tree", cancellationToken);
        if (!string.Equals(insideWorkTree, "true", StringComparison.OrdinalIgnoreCase))
            throw new GitDeployException("Configured folder is not a git repository.");
    }

    private async Task<string> ReadGitValueAsync(string repoDir, string arguments, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(repoDir, arguments, DefaultCommandTimeoutSeconds, cancellationToken);
        return result.StdOut.Trim();
    }

    private async Task<RollbackOutcome> TryRollbackAsync(
        GitDeployRequest request,
        string repoDir,
        string branch,
        string previousCommit,
        CancellationToken cancellationToken)
    {
        try
        {
            request.Log($"Rolling back branch '{branch}' to {previousCommit}");
            await RunGitAsync(repoDir, $"checkout --detach {previousCommit}", DefaultCommandTimeoutSeconds, cancellationToken);
            await RunGitAsync(repoDir, $"branch -f \"{branch}\" {previousCommit}", DefaultCommandTimeoutSeconds, cancellationToken);
            await RunGitAsync(repoDir, $"checkout \"{branch}\"", DefaultCommandTimeoutSeconds, cancellationToken);

            if (!string.IsNullOrWhiteSpace(request.Entry.GitPostPullCommand))
            {
                request.Log("Running post-pull command for rollback");
                await RunShellCommandAsync(
                    repoDir,
                    request.Entry.GitPostPullCommand,
                    PostPullTimeoutSeconds,
                    cancellationToken);
            }

            request.Log("Starting rolled back service");
            if (!await request.StartScriptAsync())
            {
                return new RollbackOutcome(
                    RepoRestored: true,
                    Message: "Deploy failed. Repository was rolled back, but the previous service failed to start.");
            }

            request.Log("Waiting for rolled back service validation");
            if (!await request.WaitForHealthyAsync())
            {
                return new RollbackOutcome(
                    RepoRestored: true,
                    Message: "Deploy failed. Repository was rolled back, but the previous service did not become healthy.");
            }

            request.Log("Rollback completed successfully");
            return new RollbackOutcome(
                RepoRestored: true,
                Message: "Deploy failed, but rollback restored the previous version successfully.");
        }
        catch (Exception ex) when (ex is GitDeployException || ex is InvalidOperationException)
        {
            request.Log($"Rollback failed: {ex.Message}");
            return new RollbackOutcome(
                RepoRestored: false,
                Message: $"Deploy failed and rollback could not be completed: {ex.Message}");
        }
    }

    private static async Task<CommandResult> RunGitAsync(
        string repoDir,
        string arguments,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var result = await RunProcessAsync("git", arguments, repoDir, timeoutSeconds, cancellationToken);
        if (result.ExitCode != 0)
            throw new GitDeployException(BuildFailureMessage($"git {arguments}", result));

        return result;
    }

    private static async Task RunShellCommandAsync(
        string repoDir,
        string command,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var result = await RunProcessAsync("cmd.exe", $"/c {command}", repoDir, timeoutSeconds, cancellationToken);
        if (result.ExitCode != 0)
            throw new GitDeployException(BuildFailureMessage(command, result));
    }

    private static async Task<CommandResult> RunProcessAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };

        try
        {
            if (!process.Start())
                throw new GitDeployException($"Could not start process: {fileName}");
        }
        catch (Win32Exception ex)
        {
            throw new GitDeployException($"Could not start '{fileName}': {ex.Message}");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            throw new GitDeployException($"Command timed out after {timeoutSeconds}s: {fileName} {arguments}");
        }

        var stdout = (await stdoutTask).Trim();
        var stderr = (await stderrTask).Trim();

        return new CommandResult(process.ExitCode, stdout, stderr);
    }

    private static string BuildFailureMessage(string command, CommandResult result)
    {
        var parts = new List<string> { $"Command failed: {command}" };
        if (!string.IsNullOrWhiteSpace(result.StdOut))
            parts.Add($"stdout: {result.StdOut}");
        if (!string.IsNullOrWhiteSpace(result.StdErr))
            parts.Add($"stderr: {result.StdErr}");
        return string.Join(Environment.NewLine, parts);
    }

    private sealed record CommandResult(int ExitCode, string StdOut, string StdErr);

    private sealed record RollbackOutcome(bool RepoRestored, string Message);

    private sealed class GitDeployException : InvalidOperationException
    {
        public GitDeployException(string message) : base(message)
        {
        }
    }
}
