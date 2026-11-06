using LouisaWiFi.Windows.Services;

namespace LouisaWiFi.Windows.Tests;

public class StorageTests
{
    [Fact]
    public void LearnedStorePersistsNormalizedBssids()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "learned.json");
        try
        {
            var store = new LearnedStore(path);
            store.Remember(["AA:BB:CC:DD:EE:01"], "門市", 2);

            var loaded = new LearnedStore(path);
            Assert.Equal(new LearnedEntry("門市", 2), loaded.All["aa:bb:cc:dd:ee:01"]);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SecretStoreUsesCurrentUserEncryptionAndRoundTrips()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "secrets.bin");
        const string password = "manual-password";
        try
        {
            var store = new SecretStore(path);
            store.Set("AA:BB:CC:DD:EE:01", password);

            Assert.DoesNotContain(System.Text.Encoding.UTF8.GetBytes(password), File.ReadAllBytes(path));
            Assert.Equal(password, new SecretStore(path).Get("aa:bb:cc:dd:ee:01"));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
