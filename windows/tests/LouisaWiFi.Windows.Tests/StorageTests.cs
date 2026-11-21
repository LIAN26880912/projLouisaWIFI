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

    [Fact]
    public void AutoConnectSettingDefaultsToEnabledAndPersistsChanges()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "settings.json");
        try
        {
            var settings = new SettingsStore(path);
            Assert.True(settings.AutoConnect);
            settings.SetAutoConnect(false);
            Assert.False(new SettingsStore(path).AutoConnect);
            settings.SetAutoConnect(true);
            Assert.True(new SettingsStore(path).AutoConnect);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DamagedDataFilesAreBackedUpReportedAndReplacedByDefaults()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            while (JsonFile.Recovered.TryDequeue(out _)) { }
            foreach (var name in new[] { "settings.json", "learned.json", "usage.json", "secrets.bin" })
                File.WriteAllText(Path.Combine(dir, name), "{invalid");

            Assert.True(new SettingsStore(Path.Combine(dir, "settings.json")).AutoConnect);
            Assert.Empty(new LearnedStore(Path.Combine(dir, "learned.json")).All);
            Assert.Empty(new UsageTracker(Path.Combine(dir, "usage.json")).Snapshot());
            var secrets = new SecretStore(Path.Combine(dir, "secrets.bin"));
            Assert.Null(secrets.Get("aa:bb:cc:dd:ee:01"));
            secrets.Set("aa:bb:cc:dd:ee:01", "new");
            Assert.Equal("new", new SecretStore(Path.Combine(dir, "secrets.bin")).Get("aa:bb:cc:dd:ee:01"));

            foreach (var name in new[] { "settings.json", "learned.json", "usage.json", "secrets.bin" })
                Assert.Equal("{invalid", File.ReadAllText(Path.Combine(dir, name + ".bad")));
            Assert.Equal(4, JsonFile.Recovered.Count);
        }
        finally
        {
            while (JsonFile.Recovered.TryDequeue(out _)) { }
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void OnlyKnownSettingPersistsIndependently()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "settings.json");
        try
        {
            var settings = new SettingsStore(path);
            Assert.False(settings.OnlyKnown);
            settings.SetOnlyKnown(true);
            settings.SetAutoConnect(false);
            var loaded = new SettingsStore(path);
            Assert.True(loaded.OnlyKnown);
            Assert.False(loaded.AutoConnect);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FailedSettingsWriteDoesNotChangeTheInMemorySetting()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "existing-directory");
        try
        {
            Directory.CreateDirectory(path);
            var settings = new SettingsStore(path);
            var error = Record.Exception(() => settings.SetAutoConnect(false));

            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.True(settings.AutoConnect);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
