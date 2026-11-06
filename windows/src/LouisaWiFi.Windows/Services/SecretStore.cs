using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LouisaWiFi.Windows.Services;

public interface ISecretStore
{
    string? Get(string bssid);
    void Set(string bssid, string password);
}

public sealed class SecretStore : ISecretStore
{
    private static readonly byte[] entropy = Encoding.UTF8.GetBytes("LouisaWiFi");
    private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web);
    private readonly string path;
    private readonly object gate = new();

    public SecretStore(string? path = null) =>
        this.path = path ?? Path.Combine(StoragePaths.Root, "secrets.bin");

    public string? Get(string bssid)
    {
        lock (gate) return Read().GetValueOrDefault(LearnedStore.Normalize(bssid));
    }

    public void Set(string bssid, string password)
    {
        lock (gate)
        {
            var values = Read();
            values[LearnedStore.Normalize(bssid)] = password;
            var plain = JsonSerializer.SerializeToUtf8Bytes(values, json);
            try
            {
                var encrypted = ProtectedData.Protect(plain, entropy, DataProtectionScope.CurrentUser);
                AtomicWrite.Write(path, encrypted);
                CryptographicOperations.ZeroMemory(encrypted);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
    }

    private Dictionary<string, string> Read()
    {
        if (!File.Exists(path)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var encrypted = File.ReadAllBytes(path);
        var plain = ProtectedData.Unprotect(encrypted, entropy, DataProtectionScope.CurrentUser);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(plain, json)
                ?? throw new InvalidDataException("secrets.bin is empty.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }
}
