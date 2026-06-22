using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MesControlCenter.Core.Interfaces;
using MesControlCenter.Core.Models;

namespace MesControlCenter.Core.Services;

public class CredentialService : ICredentialService
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".script_control_center");

    private static readonly string ConfigFile = Path.Combine(ConfigDir, "pc_monitor_config.json");

    public string GeneratePcKey(string pcName)
    {
        var baseName = string.IsNullOrWhiteSpace(pcName)
            ? Environment.MachineName.ToUpper()
            : pcName.ToUpper();

        var uniqueId = Guid.NewGuid().ToString();
        var pcKey = $"{baseName}-{uniqueId}";

        Log($"[CRED] Generated PC key: {pcKey}");
        return pcKey;
    }

    public string GenerateApiSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var secret = Convert.ToHexString(bytes).ToLower();
        Log("[CRED] Generated API secret (64 chars)");
        return secret;
    }

    public string HashApiSecret(string apiSecret)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(apiSecret));
        return Convert.ToHexString(hash).ToLower();
    }

    public byte[] EncryptSecret(string secret)
    {
        try
        {
            var plainBytes = Encoding.UTF8.GetBytes(secret);
            var encrypted = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
            Log("[CRED] Secret encrypted with DPAPI");
            return encrypted;
        }
        catch (Exception ex)
        {
            Log($"[CRED] [ERROR] DPAPI encryption failed: {ex.Message}, using base64 fallback");
            return Encoding.UTF8.GetBytes(Convert.ToBase64String(Encoding.UTF8.GetBytes(secret)));
        }
    }

    public string DecryptSecret(byte[] encrypted)
    {
        try
        {
            var plainBytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            // Fallback: try base64
            try
            {
                var base64Str = Encoding.UTF8.GetString(encrypted);
                return Encoding.UTF8.GetString(Convert.FromBase64String(base64Str));
            }
            catch (Exception ex)
            {
                Log($"[CRED] [ERROR] Decryption failed: {ex.Message}");
                return string.Empty;
            }
        }
    }

    public bool SaveConfig(string pcKey, string apiSecret, string pcName, List<string> scripts)
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);

            var encryptedSecret = EncryptSecret(apiSecret);

            var config = new Dictionary<string, object>
            {
                ["pc_key"] = pcKey,
                ["api_secret_encrypted"] = Convert.ToHexString(encryptedSecret).ToLower(),
                ["pc_name"] = pcName,
                ["scripts"] = scripts,
                ["created_at"] = DateTime.Now.ToString("o")
            };

            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigFile, json, Encoding.UTF8);

            Log($"[CRED] Configuration saved to: {ConfigFile}");
            return true;
        }
        catch (Exception ex)
        {
            Log($"[CRED] [ERROR] Failed to save config: {ex.Message}");
            return false;
        }
    }

    public PcMonitorConfig? LoadConfig()
    {
        try
        {
            if (!File.Exists(ConfigFile))
            {
                Log($"[CRED] Config file not found: {ConfigFile}");
                return null;
            }

            var json = File.ReadAllText(ConfigFile, Encoding.UTF8);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var encryptedHex = root.GetProperty("api_secret_encrypted").GetString() ?? "";
            var encryptedBytes = Convert.FromHexString(encryptedHex);
            var apiSecret = DecryptSecret(encryptedBytes);

            var scripts = new List<string>();
            if (root.TryGetProperty("scripts", out var scriptsEl) && scriptsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in scriptsEl.EnumerateArray())
                {
                    var s = item.GetString();
                    if (s != null) scripts.Add(s);
                }
            }

            Log("[CRED] Configuration loaded successfully");

            return new PcMonitorConfig
            {
                PcKey = root.GetProperty("pc_key").GetString() ?? "",
                ApiSecret = apiSecret,
                PcName = root.GetProperty("pc_name").GetString() ?? "",
                Scripts = scripts,
                CreatedAt = root.TryGetProperty("created_at", out var ca) ? ca.GetString() : null
            };
        }
        catch (Exception ex)
        {
            Log($"[CRED] [ERROR] Failed to load config: {ex.Message}");
            return null;
        }
    }

    public bool ConfigExists() => File.Exists(ConfigFile);

    private static void Log(string message)
    {
        var ts = DateTime.Now.ToString("HH:mm:ss");
        Console.WriteLine($"[{ts}] {message}");
    }
}
