using System.Security.Cryptography;
using System.Text;

namespace MesControlCenter.Core.Services;

/// <summary>
/// Resolves the WebSocket server URL the agent/dashboard connect to. The MySQL
/// credentials no longer live on client machines — only this URL does.
/// Resolution order:
///   1. Environment variable MESCC_SERVER_URL.
///   2. Encrypted file ~/.script_control_center/server_url.dat (DPAPI, per-user).
/// </summary>
public static class ClientConfig
{
    public const string EnvVarName = "MESCC_SERVER_URL";
    public const string AdminTokenEnvVarName = "MESCC_ADMIN_TOKEN";

    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".script_control_center");

    private static readonly string ConfigFile = Path.Combine(ConfigDir, "server_url.dat");
    private static readonly string AdminTokenFile = Path.Combine(ConfigDir, "admin_token.dat");

    /// <summary>Returns the WS server URL, or throws if none is configured.</summary>
    public static string ResolveServerUrl()
    {
        var fromEnv = Environment.GetEnvironmentVariable(EnvVarName);
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv.Trim();

        var fromFile = TryLoadFromFile();
        if (!string.IsNullOrWhiteSpace(fromFile))
            return fromFile;

        throw new InvalidOperationException(
            $"No server URL configured. Set '{EnvVarName}' (e.g. ws://host:8092/ws) " +
            $"or run the installer to create '{ConfigFile}'.");
    }

    public static bool IsConfigured()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvVarName)))
            return true;
        return !string.IsNullOrWhiteSpace(TryLoadFromFile());
    }

    public static bool SaveServerUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;
        try
        {
            Directory.CreateDirectory(ConfigDir);
            var plain = Encoding.UTF8.GetBytes(url.Trim());
            var encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(ConfigFile, encrypted);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Admin token used by the dashboard to authenticate against the WS server.</summary>
    public static string? ResolveAdminToken()
    {
        var fromEnv = Environment.GetEnvironmentVariable(AdminTokenEnvVarName);
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv.Trim();
        return TryDecryptFile(AdminTokenFile);
    }

    public static bool SaveAdminToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;
        try
        {
            Directory.CreateDirectory(ConfigDir);
            var plain = Encoding.UTF8.GetBytes(token.Trim());
            var encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(AdminTokenFile, encrypted);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? TryLoadFromFile() => TryDecryptFile(ConfigFile);

    private static string? TryDecryptFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var encrypted = File.ReadAllBytes(path);
            var plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;
        }
    }
}
