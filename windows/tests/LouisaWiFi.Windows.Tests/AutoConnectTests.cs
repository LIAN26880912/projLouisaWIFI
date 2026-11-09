using LouisaWiFi.Windows.Models;
using LouisaWiFi.Windows.Platform;
using LouisaWiFi.Windows.Services;

namespace LouisaWiFi.Windows.Tests;

public class AutoConnectTests
{
    private static readonly DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const string Bssid = "AA:BB:CC:DD:EE:01";

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeLocation : ILocationProvider
    {
        public LocationResult Result { get; set; } = new(null, "沒有可用定位。");
        public Action? OnGet { get; set; }
        public int Calls { get; private set; }
        public Task<LocationResult> GetLocationAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            OnGet?.Invoke();
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeWifi : IWifi
    {
        public WifiConnection? Current { get; set; }
        public bool HasInternet { get; set; }
        public bool CanReadBssid { get; set; } = true;
        public bool DenyScan { get; set; }
        public WifiScan Scan { get; set; } = new([new WifiAccessPoint(Bssid, -50)]);
        public string? SuccessPassword { get; set; }
        public Func<string, CancellationToken, Task<bool>>? ConnectHandler { get; set; }
        public List<(string Password, string? Bssid)> Tried { get; } = [];
        public int Scans { get; private set; }
        public int Restores { get; private set; }
        public WifiProfileBackup? Restored { get; private set; }

        public Task<WifiScan> ScanAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Scans++;
            if (DenyScan) throw new UnauthorizedAccessException("Wi-Fi access denied");
            return Task.FromResult(Scan);
        }

        public WifiProfileBackup? CaptureLouisaProfile() => new("<original />", 2);

        public async Task<bool> ConnectAsync(string password, string? bssid, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Tried.Add((password, bssid));
            var success = ConnectHandler is null
                ? password == SuccessPassword
                : await ConnectHandler(password, cancellationToken);
            if (success) Current = new WifiConnection("LouisaCoffee", bssid);
            return success;
        }

        public void RestoreLouisaProfile(WifiProfileBackup? original)
        {
            Restores++;
            Restored = original;
        }
    }

    private sealed class MemoryLearned : ILearnedStore
    {
        private readonly Dictionary<string, LearnedEntry> entries = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyDictionary<string, LearnedEntry> All => entries;
        public void Remember(IEnumerable<string> bssids, string storeName, int? passwordIndex)
        {
            foreach (var bssid in bssids)
                entries[LearnedStore.Normalize(bssid)] = new LearnedEntry(storeName, passwordIndex);
        }
    }

    private sealed class MemorySecrets : ISecretStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? Get(string bssid) => Values.GetValueOrDefault(LearnedStore.Normalize(bssid));
        public void Set(string bssid, string password) => Values[LearnedStore.Normalize(bssid)] = password;
    }

    private static Store StoreAt(string name, double lat, params string[] passwords) =>
        new(name, "台北市 測試", "", lat, 121, passwords);

    private static LocationFix Fix(DateTimeOffset timestamp) => new(25, 121, 100, timestamp);

    [Fact]
    public void LocationFixEnforcesExactAgeAndAccuracyLimits()
    {
        var fix = new LocationFix(25, 121, 500, start.AddMinutes(-5));

        Assert.True(fix.IsUsable(start));
        Assert.False((fix with { Timestamp = fix.Timestamp.AddTicks(-1) }).IsUsable(start));
        Assert.False((fix with { Accuracy = 500.001 }).IsUsable(start));
        Assert.False((fix with { Accuracy = -1 }).IsUsable(start));
        Assert.False((fix with { Accuracy = double.NaN }).IsUsable(start));
        Assert.False((fix with { Latitude = 91 }).IsUsable(start));
        Assert.False((fix with { Longitude = double.PositiveInfinity }).IsUsable(start));
        Assert.False((fix with { Timestamp = start.AddTicks(1) }).IsUsable(start));
    }

    [Fact]
    public void NearbyCandidatesRespectFiveKilometers()
    {
        const double radius = 6371008.8;
        static double LatitudeAt(double meters) => 25 + meters / radius * 180 / Math.PI;
        var stores = new[]
        {
            StoreAt("內側", LatitudeAt(4999.9), "inside"),
            StoreAt("外側", LatitudeAt(5000.1), "outside")
        };

        var candidates = ConnectionPlanner.Nearby(Fix(start), stores);

        Assert.Equal(["內側"], candidates.Select(candidate => candidate.Store.Name));
    }

    [Fact]
    public async Task LearnedPasswordWorksWithoutLocationAndIsTriedImmediately()
    {
        var store = StoreAt("已學習", 24, "wrong", "known-good");
        var wifi = new FakeWifi { SuccessPassword = "known-good" };
        var learned = new MemoryLearned();
        learned.Remember([Bssid], store.Name, 1);
        var location = new FakeLocation();
        var connector = new Connector(wifi, learned, new MemorySecrets(), new Clock());

        var result = await connector.AutoConnectAsync(location, [store]);

        Assert.Equal(WifiStatus.Connected, result?.Status);
        Assert.Equal(["known-good"], wifi.Tried.Select(item => item.Password));
        Assert.Equal(0, location.Calls);
        Assert.Equal(0, wifi.Restores);
    }

    [Fact]
    public async Task AutomaticCandidatesUseLearnedFirstThenInterleaveNearestThree()
    {
        var known = StoreAt("已學習", 24, "unused", "known");
        var first = StoreAt("一", 25.001, "a0", "known");
        var second = StoreAt("二", 25.002, "b0", "b1");
        var third = StoreAt("三", 25.003, "c0", "c1");
        var fourth = StoreAt("四", 25.004, "d0");
        var wifi = new FakeWifi();
        var learned = new MemoryLearned();
        learned.Remember([Bssid], known.Name, 1);
        var location = new FakeLocation { Result = new LocationResult(Fix(start), null) };
        var connector = new Connector(wifi, learned, new MemorySecrets(), new Clock());

        var result = await connector.AutoConnectAsync(location, [fourth, known, third, second, first]);

        Assert.Equal(WifiStatus.Failed, result?.Status);
        Assert.Equal(["known", "a0", "b0", "c0", "b1", "c1"], wifi.Tried.Select(item => item.Password));
        Assert.All(wifi.Tried, item => Assert.Equal(Bssid, item.Bssid));
        Assert.Equal(1, wifi.Restores);
        Assert.Equal("<original />", wifi.Restored?.Xml);
    }

    [Fact]
    public async Task NoValidLocationDoesNotGuessOrSnoozeBeforeAnyPasswordAttempt()
    {
        var clock = new Clock();
        var store = StoreAt("附近", 25.001, "correct");
        var wifi = new FakeWifi { SuccessPassword = "correct" };
        var location = new FakeLocation { Result = new LocationResult(Fix(start.AddMinutes(-6)), null) };
        var connector = new Connector(wifi, new MemoryLearned(), new MemorySecrets(), clock);

        var first = await connector.AutoConnectAsync(location, [store]);
        Assert.Equal(WifiStatus.Idle, first?.Status);
        Assert.Contains("過期", first?.Message);
        Assert.Empty(wifi.Tried);
        Assert.Equal(0, wifi.Restores);

        location.Result = new LocationResult(Fix(start), null);
        var second = await connector.AutoConnectAsync(location, [store]);
        Assert.Equal(WifiStatus.Connected, second?.Status);
        Assert.Equal(["correct"], wifi.Tried.Select(item => item.Password));
    }

    [Fact]
    public async Task FailedBssidIsSuppressedForTenMinutes()
    {
        var clock = new Clock();
        var store = StoreAt("附近", 25.001, "wrong");
        var wifi = new FakeWifi();
        var location = new FakeLocation { Result = new LocationResult(Fix(clock.Now), null) };
        var connector = new Connector(wifi, new MemoryLearned(), new MemorySecrets(), clock);

        Assert.Equal(WifiStatus.Failed, (await connector.AutoConnectAsync(location, [store]))?.Status);
        clock.Now = start.AddSeconds(15);
        Assert.Null(await connector.AutoConnectAsync(location, [store]));
        clock.Now = start.AddMinutes(10).AddTicks(-1);
        Assert.Null(await connector.AutoConnectAsync(location, [store]));
        Assert.Single(wifi.Tried);

        clock.Now = start.AddMinutes(10);
        location.Result = new LocationResult(Fix(clock.Now), null);
        Assert.Equal(WifiStatus.Failed, (await connector.AutoConnectAsync(location, [store]))?.Status);
        Assert.Equal(2, wifi.Tried.Count);
        Assert.Equal(2, wifi.Restores);
    }

    [Fact]
    public async Task ConnectionEventsAreQuietForFifteenSeconds()
    {
        var clock = new Clock();
        var store = StoreAt("附近", 25.001, "wrong");
        var wifi = new FakeWifi();
        var location = new FakeLocation { Result = new LocationResult(Fix(start), null) };
        var connector = new Connector(wifi, new MemoryLearned(), new MemorySecrets(), clock);

        await connector.ConnectAsync(store);
        Assert.Equal(1, wifi.Scans);
        clock.Now = start.AddSeconds(15).AddTicks(-1);
        Assert.Null(await connector.AutoConnectAsync(location, [store]));
        Assert.Equal(1, wifi.Scans);

        clock.Now = start.AddSeconds(15);
        await connector.AutoConnectAsync(location, [store]);
        Assert.Equal(2, wifi.Scans);
    }

    [Fact]
    public async Task ExistingInternetOrOtherWifiIsNotInterruptedAutomatically()
    {
        var store = StoreAt("附近", 25.001, "candidate");
        var wifi = new FakeWifi { HasInternet = true };
        var connector = new Connector(wifi, new MemoryLearned(), new MemorySecrets(), new Clock());
        var location = new FakeLocation();

        Assert.Equal(WifiStatus.Idle, (await connector.AutoConnectAsync(location, [store]))?.Status);
        Assert.Equal(WifiStatus.Idle, (await connector.ConnectAsync(store)).Status);
        Assert.Equal(0, wifi.Scans);
        Assert.Empty(wifi.Tried);

        wifi.HasInternet = false;
        wifi.Current = new WifiConnection("OtherWiFi", Bssid);
        Assert.Equal(WifiStatus.Idle, (await connector.AutoConnectAsync(location, [store]))?.Status);
        Assert.Equal(0, wifi.Scans);
    }

    [Fact]
    public async Task NewWifiConnectionDuringLocationLookupIsNotInterrupted()
    {
        var store = StoreAt("附近", 25.001, "candidate");
        var wifi = new FakeWifi();
        var location = new FakeLocation
        {
            Result = new LocationResult(Fix(start), null),
            OnGet = () => wifi.Current = new WifiConnection("OtherWiFi", Bssid)
        };
        var connector = new Connector(wifi, new MemoryLearned(), new MemorySecrets(), new Clock());

        var result = await connector.AutoConnectAsync(location, [store]);

        Assert.Equal(WifiStatus.Idle, result?.Status);
        Assert.Empty(wifi.Tried);
        Assert.Equal(0, wifi.Restores);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NewLouisaConnectionDuringLocationLookupIsNotRolledBack(bool nearbyCandidateIsDuplicate)
    {
        var store = StoreAt("已學習", nearbyCandidateIsDuplicate ? 25.001 : 24, "candidate");
        var wifi = new FakeWifi();
        var learned = new MemoryLearned();
        learned.Remember([Bssid], store.Name, 0);
        var location = new FakeLocation
        {
            Result = new LocationResult(Fix(start), null),
            OnGet = () => wifi.Current = new WifiConnection("LouisaCoffee", Bssid)
        };
        var connector = new Connector(wifi, learned, new MemorySecrets(), new Clock());

        var result = await connector.AutoConnectAsync(location, [store]);

        Assert.Equal(WifiStatus.Connected, result?.Status);
        Assert.Null(result?.StoreName);
        Assert.Contains("請確認 Wi-Fi 自動連線設定", result?.Message);
        Assert.Single(wifi.Tried);
        Assert.Equal(0, wifi.Restores);
    }

    [Fact]
    public async Task CancellationAfterWindowsConnectsDoesNotRestoreOverTheActiveConnection()
    {
        using var cancellation = new CancellationTokenSource();
        var store = StoreAt("附近", 25.001, "candidate");
        var wifi = new FakeWifi();
        wifi.ConnectHandler = (_, token) =>
        {
            wifi.Current = new WifiConnection("LouisaCoffee", Bssid);
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(false);
        };
        var connector = new Connector(wifi, new MemoryLearned(), new MemorySecrets(), new Clock());
        var reported = new List<WifiResult>();
        connector.StatusChanged += reported.Add;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            connector.ConnectAsync(store, cancellationToken: cancellation.Token));

        Assert.Equal(0, wifi.Restores);
        Assert.Contains(reported, result => result.Status == WifiStatus.Connected &&
            result.Message.Contains("請確認 Wi-Fi 自動連線設定"));
    }

    [Fact]
    public async Task ManualConnectionAppearingAfterFailedCandidateDoesNotTryAnotherPassword()
    {
        var store = StoreAt("附近", 25.001, "first", "second");
        var wifi = new FakeWifi();
        wifi.ConnectHandler = (_, _) =>
        {
            wifi.Current = new WifiConnection("LouisaCoffee", Bssid);
            return Task.FromResult(false);
        };
        var connector = new Connector(wifi, new MemoryLearned(), new MemorySecrets(), new Clock());

        var result = await connector.ConnectAsync(store);

        Assert.Equal(WifiStatus.Connected, result.Status);
        Assert.Null(result.StoreName);
        Assert.Contains("請確認 Wi-Fi 自動連線設定", result.Message);
        Assert.Equal(["first"], wifi.Tried.Select(item => item.Password));
        Assert.Equal(0, wifi.Restores);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task ManualConnectionCanTryWithoutBssidWhenAccessIsDenied(bool canReadBssid, bool denyScan)
    {
        var store = StoreAt("手動", 25, "unused");
        var wifi = new FakeWifi { CanReadBssid = canReadBssid, DenyScan = denyScan, SuccessPassword = "manual-password" };
        var learned = new MemoryLearned();
        var secrets = new MemorySecrets();
        var connector = new Connector(wifi, learned, secrets, new Clock());

        var result = await connector.ConnectAsync(store, "manual-password");

        Assert.Equal(WifiStatus.Connected, result.Status);
        Assert.Contains("無法記住", result.Message);
        Assert.Equal(denyScan ? 1 : 0, wifi.Scans);
        Assert.Null(Assert.Single(wifi.Tried).Bssid);
        Assert.Empty(learned.All);
        Assert.Empty(secrets.Values);
    }

    [Fact]
    public async Task FailedManualConnectionWithoutBssidExplainsThePermissionLimitation()
    {
        var store = StoreAt("手動", 25, "wrong");
        var wifi = new FakeWifi { CanReadBssid = false };
        var connector = new Connector(wifi, new MemoryLearned(), new MemorySecrets(), new Clock());

        var result = await connector.ConnectAsync(store);

        Assert.Equal(WifiStatus.Failed, result.Status);
        Assert.Contains("開啟定位權限", result.Message);
        Assert.Null(Assert.Single(wifi.Tried).Bssid);
        Assert.Equal(1, wifi.Restores);
    }

    [Fact]
    public async Task AutomaticConnectionReportsDeniedWifiAccessWithoutAttemptingPasswords()
    {
        var store = StoreAt("附近", 25, "candidate");
        var wifi = new FakeWifi { CanReadBssid = false };
        var connector = new Connector(wifi, new MemoryLearned(), new MemorySecrets(), new Clock());

        var result = await connector.AutoConnectAsync(new FakeLocation(), [store]);

        Assert.Equal(WifiStatus.Idle, result?.Status);
        Assert.Contains("未授權", result?.Message);
        Assert.Equal(0, wifi.Scans);
        Assert.Empty(wifi.Tried);
    }

    [Fact]
    public async Task ManualRequestCancelsAnInProgressAutomaticAttempt()
    {
        var known = StoreAt("已學習", 24, "automatic");
        var manual = StoreAt("手動", 25, "manual");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wifi = new FakeWifi
        {
            ConnectHandler = async (password, token) =>
            {
                if (password == "automatic")
                {
                    started.TrySetResult();
                    await Task.Delay(Timeout.Infinite, token);
                }
                return password == "manual";
            }
        };
        var learned = new MemoryLearned();
        learned.Remember([Bssid], known.Name, 0);
        var connector = new Connector(wifi, learned, new MemorySecrets(), new Clock());

        var automatic = connector.AutoConnectAsync(new FakeLocation(), [known, manual]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var manualResult = connector.ConnectAsync(manual);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => automatic);
        Assert.Equal(WifiStatus.Connected, (await manualResult).Status);
        Assert.Equal(["automatic", "manual"], wifi.Tried.Select(item => item.Password));
        Assert.Equal(1, wifi.Restores);
    }

    [Fact]
    public async Task AutomaticTriggersDoNotCancelAnInProgressManualAttempt()
    {
        var store = StoreAt("手動", 25, "manual");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wifi = new FakeWifi
        {
            ConnectHandler = async (_, token) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                return false;
            }
        };
        var connector = new Connector(wifi, new MemoryLearned(), new MemorySecrets(), new Clock());
        using var cancel = new CancellationTokenSource();

        var manual = connector.ConnectAsync(store, cancellationToken: cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(await connector.AutoConnectAsync(new FakeLocation(), [store]));
        connector.CancelAutomatic();
        Assert.False(manual.IsCompleted);

        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manual);
        Assert.Equal(1, wifi.Scans);
        Assert.Equal(1, wifi.Restores);
    }
}
