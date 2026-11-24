using LouisaWiFi.Windows.Models;
using LouisaWiFi.Windows.Platform;
using LouisaWiFi.Windows.Services;

namespace LouisaWiFi.Windows.Tests;

public class ConnectorTests
{
    private sealed class FakeWifi : IWifi
    {
        public WifiConnection? Current { get; set; }
        public bool HasInternet { get; set; }
        public bool CanReadBssid { get; set; } = true;
        public int Scans { get; private set; }
        public int Restores { get; private set; }
        public WifiProfileBackup? Restored { get; private set; }
        public List<string> Tried { get; } = [];
        public bool Succeed { get; set; }
        public WifiScan Result { get; } = new([
            new WifiAccessPoint("AA:BB:CC:DD:EE:01", -50),
            new WifiAccessPoint("AA:BB:CC:DD:EE:02", -70),
            new WifiAccessPoint("AA:BB:CC:DD:EE:03", -80)
        ]);

        public Task<WifiScan> ScanAsync(CancellationToken cancellationToken)
        {
            Scans++;
            return Task.FromResult(Result);
        }

        public WifiProfileBackup? CaptureLouisaProfile(string ssid) => new("<old />", 9);

        public Task<bool> ConnectAsync(string ssid, string password, string? bssid, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Tried.Add(password);
            if (Succeed && password == "correct")
                Current = new WifiConnection(ssid, bssid);
            return Task.FromResult(Succeed && password == "correct");
        }

        public void RestoreLouisaProfile(string ssid, WifiProfileBackup? original)
        {
            Restores++;
            Restored = original;
        }

        public int Disconnects { get; private set; }
        public void Disconnect()
        {
            Disconnects++;
            Current = null;
        }
    }

    private sealed class MemoryLearned : ILearnedStore
    {
        private readonly Dictionary<string, LearnedEntry> entries = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyDictionary<string, LearnedEntry> All => entries;
        public void Remember(IEnumerable<string> bssids, string storeName, int? passwordIndex)
        {
            foreach (var bssid in bssids) entries[LearnedStore.Normalize(bssid)] = new(storeName, passwordIndex);
        }
    }

    private sealed class FailingLearned : ILearnedStore
    {
        public IReadOnlyDictionary<string, LearnedEntry> All => new Dictionary<string, LearnedEntry>();
        public void Remember(IEnumerable<string> bssids, string storeName, int? passwordIndex) =>
            throw new IOException("test storage failure");
    }

    private sealed class MemorySecrets : ISecretStore
    {
        public readonly Dictionary<string, string> Values = new(StringComparer.OrdinalIgnoreCase);
        public string? Get(string bssid) => Values.GetValueOrDefault(LearnedStore.Normalize(bssid));
        public void Set(string bssid, string password) => Values[LearnedStore.Normalize(bssid)] = password;
    }

    private static Store TestStore(params string[] passwords) =>
        new("測試門市", "台北市", "", 25, 121, passwords);

    [Fact]
    public async Task InternetWithoutWifiIsNeverSwitched()
    {
        var wifi = new FakeWifi { HasInternet = true };
        var connector = new Connector(wifi, new MemoryLearned(), new MemorySecrets());

        var result = await connector.ConnectAsync(TestStore("candidate"));

        Assert.Equal(WifiStatus.Idle, result.Status);
        Assert.Equal(0, wifi.Scans);
        Assert.Empty(wifi.Tried);
    }

    [Fact]
    public async Task ChosenStoreReplacesAnotherOnlineWifiLikeMacOS()
    {
        var wifi = new FakeWifi { Current = new WifiConnection("Hotspot", "00:00:00:00:00:01"), HasInternet = true, Succeed = true };
        var connector = new Connector(wifi, new MemoryLearned(), new MemorySecrets());

        var result = await connector.ConnectAsync(TestStore("wrong", "correct"));

        Assert.Equal(WifiStatus.Connected, result.Status);
        Assert.Equal(["wrong", "correct"], wifi.Tried);
    }

    [Fact]
    public async Task ExistingUnrecognizedLouisaConnectionDoesNotClaimSelectedStore()
    {
        var wifi = new FakeWifi { Current = new WifiConnection("LouisaCoffee", "00:00:00:00:00:01") };
        var connector = new Connector(wifi, new MemoryLearned(), new MemorySecrets());

        var result = await connector.ConnectAsync(TestStore("candidate"));

        Assert.Equal(WifiStatus.Connected, result.Status);
        Assert.Null(result.StoreName);
        Assert.Equal("已連上 LouisaCoffee，尚未辨識門市。", result.Message);
        Assert.Equal(0, wifi.Scans);
    }

    [Fact]
    public async Task FailedAttemptsRestoreOriginalProfile()
    {
        var wifi = new FakeWifi();
        var connector = new Connector(wifi, new MemoryLearned(), new MemorySecrets());

        var result = await connector.ConnectAsync(TestStore("wrong-a", "wrong-b"));

        Assert.Equal(WifiStatus.Failed, result.Status);
        Assert.Equal(["wrong-a", "wrong-b"], wifi.Tried);
        Assert.Equal(1, wifi.Restores);
        Assert.Equal("<old />", wifi.Restored?.Xml);
    }

    [Fact]
    public async Task SuccessfulCandidateRemembersOnlyNearbyAndConnectedBssids()
    {
        var wifi = new FakeWifi { Succeed = true };
        var learned = new MemoryLearned();
        var secrets = new MemorySecrets();
        var connector = new Connector(wifi, learned, secrets);

        var result = await connector.ConnectAsync(TestStore("wrong", "correct"));

        Assert.Equal(WifiStatus.Connected, result.Status);
        Assert.Equal(["wrong", "correct"], wifi.Tried);
        Assert.Equal(0, wifi.Restores);
        Assert.All(learned.All.Values, entry => Assert.Equal(1, entry.PasswordIndex));
        Assert.Equal(2, learned.All.Count);
        Assert.Empty(secrets.Values);
    }

    [Fact]
    public async Task SuccessfulCustomPasswordIsStoredThroughSecretStore()
    {
        var wifi = new FakeWifi { Succeed = true };
        var learned = new MemoryLearned();
        var secrets = new MemorySecrets();
        var connector = new Connector(wifi, learned, secrets);

        var result = await connector.ConnectAsync(TestStore("other"), "correct");

        Assert.Equal(WifiStatus.Connected, result.Status);
        Assert.All(learned.All.Values, entry => Assert.Null(entry.PasswordIndex));
        Assert.Equal(2, secrets.Values.Count);
        Assert.All(secrets.Values.Values, value => Assert.Equal("correct", value));
    }

    [Fact]
    public async Task StorageFailureDoesNotMisreportSuccessfulConnectionAsFailed()
    {
        var wifi = new FakeWifi { Succeed = true };
        var connector = new Connector(wifi, new FailingLearned(), new MemorySecrets());

        var result = await connector.ConnectAsync(TestStore("correct"));

        Assert.Equal(WifiStatus.Connected, result.Status);
        Assert.Contains("無法記住門市資訊", result.Message);
        Assert.Equal(0, wifi.Restores);
    }

    [Fact]
    public async Task NewRequestCancelsCurrentAttemptBeforeStarting()
    {
        var wifi = new BlockingWifi();
        var connector = new Connector(wifi, new MemoryLearned(), new MemorySecrets());
        var first = connector.ConnectAsync(TestStore("first"));
        await wifi.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = connector.ConnectAsync(TestStore("second"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(WifiStatus.Connected, (await second).Status);
        Assert.Equal(["first", "second"], wifi.Tried);
    }

    private sealed class BlockingWifi : IWifi
    {
        public WifiConnection? Current { get; private set; }
        public bool HasInternet => false;
        public bool CanReadBssid => true;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Tried { get; } = [];
        public Task<WifiScan> ScanAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new WifiScan([new WifiAccessPoint("AA:BB:CC:DD:EE:01", -50)]));
        public WifiProfileBackup? CaptureLouisaProfile(string ssid) => null;
        public async Task<bool> ConnectAsync(string ssid, string password, string? bssid, CancellationToken cancellationToken)
        {
            Tried.Add(password);
            if (password == "first")
            {
                Started.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            if (password == "second") Current = new WifiConnection("LouisaCoffee", bssid);
            return password == "second";
        }
        public void RestoreLouisaProfile(string ssid, WifiProfileBackup? original) { }
        public void Disconnect() { }
    }
}
