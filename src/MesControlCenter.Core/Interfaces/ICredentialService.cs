using MesControlCenter.Core.Models;

namespace MesControlCenter.Core.Interfaces;

public interface ICredentialService
{
    string GeneratePcKey(string pcName);
    string GenerateApiSecret();
    string HashApiSecret(string apiSecret);
    byte[] EncryptSecret(string secret);
    string DecryptSecret(byte[] encrypted);
    bool SaveConfig(string pcKey, string apiSecret, string pcName, List<string> scripts);
    PcMonitorConfig? LoadConfig();
    bool ConfigExists();
}
