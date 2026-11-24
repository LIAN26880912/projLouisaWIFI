using LouisaWiFi.Windows.Models;
using LouisaWiFi.Windows.Platform;
using System.Security.Cryptography;
using System.Text.Json;

namespace LouisaWiFi.Windows.Services;

public sealed record PasswordCandidate(string Password, int? PasswordIndex);
public sealed record StorePasswordCandidate(Store Store, PasswordCandidate Candidate);

public static class ConnectionPlanner
{
    public static IReadOnlyList<PasswordCandidate> Plan(
        Store store,
        WifiScan scan,
        IReadOnlyDictionary<string, LearnedEntry> learned,
        ISecretStore secrets,
        string? explicitPassword = null)
    {
        if (explicitPassword is not null) return [new PasswordCandidate(explicitPassword, null)];

        var candidates = new List<PasswordCandidate>();
        foreach (var point in scan.AccessPoints)
        {
            if (!learned.TryGetValue(LearnedStore.Normalize(point.Bssid), out var entry) ||
                entry.StoreName != store.Name) continue;

            if (entry.PasswordIndex is int index && index >= 0 && index < store.Passwords.Count)
                candidates.Add(new PasswordCandidate(store.Passwords[index], index));
            else if (secrets.Get(point.Bssid) is { } password)
                candidates.Add(new PasswordCandidate(password, null));
        }

        for (var i = 0; i < store.Passwords.Count; i++)
            candidates.Add(new PasswordCandidate(store.Passwords[i], i));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        return candidates.Where(c => seen.Add(c.Password)).ToList();
    }

    public static IReadOnlyList<StorePasswordCandidate> Learned(
        WifiScan scan,
        IReadOnlyList<Store> stores,
        IReadOnlyDictionary<string, LearnedEntry> learned,
        ISecretStore secrets)
    {
        var candidates = new List<StorePasswordCandidate>();
        foreach (var point in scan.AccessPoints)
        {
            if (!learned.TryGetValue(LearnedStore.Normalize(point.Bssid), out var entry)) continue;
            var store = Stores.Named(entry.StoreName, stores);
            if (store is null) continue;

            if (entry.PasswordIndex is int index && index >= 0 && index < store.Passwords.Count)
                candidates.Add(new StorePasswordCandidate(store, new PasswordCandidate(store.Passwords[index], index)));
            else if (entry.PasswordIndex is null && secrets.Get(point.Bssid) is { } password)
                candidates.Add(new StorePasswordCandidate(store, new PasswordCandidate(password, null)));
        }
        return candidates;
    }

    public static IReadOnlyList<StorePasswordCandidate> Nearby(LocationFix location, IReadOnlyList<Store> stores) =>
        Interleave(Stores.Nearest(location.Latitude, location.Longitude, 3, stores)
            .Where(item => item.Meters < 5000)
            .Select(item => item.Store)
            .ToList());

    /// Every store's first password, then every second one, and so on; duplicates are skipped when tried.
    public static IReadOnlyList<StorePasswordCandidate> Interleave(IReadOnlyList<Store> stores)
    {
        var candidates = new List<StorePasswordCandidate>();
        var maxPasswords = stores.Count == 0 ? 0 : stores.Max(store => store.Passwords.Count);
        for (var index = 0; index < maxPasswords; index++)
            foreach (var store in stores.Where(store => index < store.Passwords.Count))
                candidates.Add(new StorePasswordCandidate(store, new PasswordCandidate(store.Passwords[index], index)));
        return candidates;
    }
}

public sealed class Connector
{
    private readonly IWifi wifi;
    private readonly ILearnedStore learned;
    private readonly ISecretStore secrets;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly object gate = new();
    private readonly Dictionary<string, DateTimeOffset> snoozed = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? active;
    private bool activeAutomatic;
    private DateTimeOffset quietUntil = DateTimeOffset.MinValue;

    public Connector(IWifi wifi, ILearnedStore learned, ISecretStore secrets, TimeProvider? clock = null)
    {
        this.wifi = wifi;
        this.learned = learned;
        this.secrets = secrets;
        this.clock = clock ?? TimeProvider.System;
    }

    public event Action<WifiResult>? StatusChanged;
    /// Decision details for the log: store names and counts only, never passwords, BSSIDs or coordinates.
    public event Action<string>? Trace;

    public async Task<WifiResult> ConnectAsync(Store store, string? password = null, CancellationToken cancellationToken = default)
    {
        return await RunAsync(false, async token => await ConnectCoreAsync(store, password, token), cancellationToken)
            ?? throw new InvalidOperationException("手動連線未執行。");
    }

    /// location null: don't use Windows location; recent: stores to try after learned (and nearby) ones.
    public Task<WifiResult?> AutoConnectAsync(
        ILocationProvider? location,
        IReadOnlyList<Store> stores,
        IReadOnlyList<Store>? recent = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(true, token => AutoConnectCoreAsync(location, stores, recent, manual: false, token), cancellationToken);

    /// Menu/CLI "nearest store": same search as auto-connect, but a user action, so it ignores snoozes and the switch,
    /// and interrupts another network Windows is still joining.
    public async Task<WifiResult> ConnectNearestAsync(
        ILocationProvider location,
        IReadOnlyList<Store> stores,
        IReadOnlyList<Store>? recent = null,
        CancellationToken cancellationToken = default) =>
        await RunAsync(false, token => AutoConnectCoreAsync(location, stores, recent, manual: true, token), cancellationToken)
            ?? throw new InvalidOperationException("手動連線未執行。");

    /// Store names that have worked here before (learned base stations).
    public IReadOnlySet<string> KnownStores() =>
        learned.All.Values.Select(entry => entry.StoreName).ToHashSet(StringComparer.Ordinal);

    /// Learned store for the Louisa network we're on; "" when on Louisa Wi-Fi but unknown; null otherwise.
    public string? CurrentStore()
    {
        var current = wifi.Current;
        if (!LouisaNetwork.IsLouisa(current?.Ssid)) return null;
        return current.Bssid is not null && learned.All.TryGetValue(LearnedStore.Normalize(current.Bssid), out var entry)
            ? entry.StoreName
            : "";
    }

    public const string NotConnected = "未連上路易莎 Wi-Fi。";

    /// For the menu, like macOS currentStatus().orFailure(previous): the live Louisa link wins;
    /// otherwise a failure or note stays, but a "connected"/"connecting" left from earlier does not.
    public WifiResult CurrentStatus(WifiResult? previous)
    {
        var current = wifi.Current;
        if (LouisaNetwork.IsLouisa(current?.Ssid)) return LouisaStatus(current);
        return previous is { Status: WifiStatus.Failed or WifiStatus.Idle } ? previous : WifiResult.Idle(NotConnected);
    }

    public void CancelAutomatic()
    {
        lock (gate)
        {
            if (activeAutomatic) active?.Cancel();
        }
    }

    public async Task CancelAndWaitAsync()
    {
        lock (gate) active?.Cancel();
        await serial.WaitAsync();
        serial.Release();
    }

    private async Task<WifiResult?> RunAsync(
        bool automatic,
        Func<CancellationToken, Task<WifiResult?>> work,
        CancellationToken cancellationToken)
    {
        CancellationTokenSource current;
        lock (gate)
        {
            if (automatic && active is not null) return null;
            if (!automatic) active?.Cancel();
            current = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            active = current;
            activeAutomatic = automatic;
        }

        try
        {
            await serial.WaitAsync(current.Token);
            try { return await work(current.Token); }
            finally { serial.Release(); }
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(active, current))
                {
                    active = null;
                    activeAutomatic = false;
                }
            }
            current.Dispose();
        }
    }

    private async Task<WifiResult> ConnectCoreAsync(Store store, string? password, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = wifi.Current;
        if (LouisaNetwork.IsLouisa(current?.Ssid))
            return CurrentLouisa(current);
        // Like macOS: choosing a store leaves another Wi-Fi (e.g. a hotspot); only keep a non-Wi-Fi internet link.
        if (current is null && wifi.HasInternet)
            return Report(WifiResult.Idle("目前已可上網，保留現有連線。"));
        // A join in progress can make the scan fail; the user chose a store, so stop it first.
        if (current is { Joining: true }) StopJoining(current);

        WifiScan? scan = null;
        if (wifi.CanReadBssid)
        {
            try { scan = await wifi.ScanAsync(cancellationToken); }
            catch (UnauthorizedAccessException) { }
        }
        if (scan is { AccessPoints.Count: 0 })
            return Report(WifiResult.Failed("附近找不到路易莎 Wi-Fi。"));

        var candidates = ConnectionPlanner.Plan(store, scan ?? new WifiScan([]), learned.All, secrets, password)
            .Select(candidate => new StorePasswordCandidate(store, candidate));
        if (!candidates.Any())
            return Report(WifiResult.Failed("沒有可用的密碼，請輸入店內公告的 Wi-Fi 密碼。"));

        var attempt = new Attempt(scan, automatic: false);
        try
        {
            var result = await TryCandidatesAsync(candidates, attempt, cancellationToken);
            if (result is not null) return result;
            result = ExistingConnection(attempt);
            if (result is not null) return result;
            if (scan is null)
                return Report(WifiResult.Failed($"無法在未讀取基地台資訊的情況下連上{store.Name}，請確認密碼或開啟定位權限。"));
            return Report(WifiResult.Failed(password is null
                ? $"無法連上{store.Name}，請確認門市與密碼。"
                : $"密碼無法連上{store.Name}，請確認店內公告的密碼。"));
        }
        finally
        {
            FinishAttempt(attempt);
        }
    }

    private async Task<WifiResult?> AutoConnectCoreAsync(
        ILocationProvider? location,
        IReadOnlyList<Store> stores,
        IReadOnlyList<Store>? recent,
        bool manual,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = clock.GetUtcNow();
        if (manual) snoozed.Clear();
        else if (now < quietUntil) return null;
        var current = wifi.Current;
        if (LouisaNetwork.IsLouisa(current?.Ssid))
            return CurrentLouisa(current);
        if (wifi.HasInternet)
            return Report(WifiResult.Idle("目前已可上網，保留現有連線。"));
        if (current is { Joining: true })
        {
            // Never interrupt on our own: the user may be joining a hotspot on purpose.
            if (!manual) return Report(WifiResult.Joining(current.Ssid));
            StopJoining(current);
        }
        else if (current is not null)
            return Report(WifiResult.Idle("目前已連線，保留現有 Wi-Fi。"));
        if (!wifi.CanReadBssid)
            return Report(WifiResult.Idle("Windows 未授權 Wi-Fi 定位資訊，請開啟定位設定或手動選店。"));

        WifiScan scan;
        try { scan = await wifi.ScanAsync(cancellationToken); }
        catch (UnauthorizedAccessException)
        {
            return Report(WifiResult.Idle("Windows 未授權 Wi-Fi 定位資訊，請開啟定位設定或手動選店。"));
        }
        if (scan.AccessPoints.Count == 0)
            return Report(WifiResult.Idle("附近找不到路易莎 Wi-Fi。"));

        var key = LearnedStore.Normalize(scan.AccessPoints[0].Bssid);
        foreach (var expired in snoozed.Where(item => item.Value <= now).Select(item => item.Key).ToList())
            snoozed.Remove(expired);
        if (snoozed.TryGetValue(key, out var until) && until > now)
        {
            Trace?.Invoke("此基地台先前自動連線失敗，暫停嘗試中。");
            return null;
        }
        Trace?.Invoke($"掃描到 {scan.AccessPoints.Count} 個路易莎基地台：{string.Join("、", scan.AccessPoints.Select(p => p.Ssid).Distinct())}");

        var attempt = new Attempt(scan, automatic: true);
        try
        {
            var known = ConnectionPlanner.Learned(scan, stores, learned.All, secrets);
            var result = await TryCandidatesAsync(known, attempt, cancellationToken);
            if (result is not null) return result;

            string? unusable = null;
            if (location is null)
                unusable = "未使用定位";
            else
            {
                var position = await location.GetLocationAsync(cancellationToken);
                if (position.Fix is { } fix && fix.IsUsable(clock.GetUtcNow()))
                {
                    var nearby = ConnectionPlanner.Nearby(fix, stores);
                    Trace?.Invoke($"定位可用，5 公里內 {nearby.Select(c => c.Store.Name).Distinct().Count()} 家門市");
                    if (nearby.Count == 0) unusable = "5 公里內找不到路易莎門市，請手動選店。";
                    result = await TryCandidatesAsync(nearby, attempt, cancellationToken);
                    if (result is not null) return result;
                }
                else
                {
                    unusable = position.UnavailableReason ?? (position.Fix is null
                        ? "無法辨識門市，請手動選店。"
                        : "定位已過期或誤差達 500 公尺以上，請手動選店。");
                    Trace?.Invoke($"定位不可用：{unusable}");
                }
            }

            if (recent is { Count: > 0 })
            {
                Trace?.Invoke($"改試最近去過的門市：{string.Join("、", recent.Select(store => store.Name))}");
                result = await TryCandidatesAsync(ConnectionPlanner.Interleave(recent), attempt, cancellationToken);
                if (result is not null) return result;
            }
            else if (location is null)
                Trace?.Invoke("未使用定位，也沒有最近去過的門市");

            result = ExistingConnection(attempt);
            if (result is not null) return result;
            // Like macOS runAuto: nothing to try also notifies and snoozes this base station.
            if (attempt.Tried.Count == 0)
                return Snooze(key, location is null ? "還沒有最近去過的門市，請手動選店。" : unusable ?? "無法辨識門市，請手動選店。");

            return Snooze(key, "自動連線失敗，請手動選擇門市。");
        }
        finally
        {
            FinishAttempt(attempt);
        }
    }

    private async Task<WifiResult?> TryCandidatesAsync(
        IEnumerable<StorePasswordCandidate> candidates,
        Attempt attempt,
        CancellationToken cancellationToken)
    {
        foreach (var choice in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existing = ExistingConnection(attempt);
            if (existing is not null) return existing;
            if (!attempt.Tried.Add(choice.Candidate.Password)) continue;

            if (!attempt.ProfileCaptured)
            {
                attempt.OriginalProfile = wifi.CaptureLouisaProfile(attempt.Ssid);
                attempt.ProfileCaptured = true;
            }
            Report(WifiResult.Connecting(choice.Store.Name));
            bool connected;
            try
            {
                connected = await wifi.ConnectAsync(
                    attempt.Ssid, choice.Candidate.Password, attempt.Scan?.AccessPoints[0].Bssid, cancellationToken);
            }
            finally
            {
                quietUntil = clock.GetUtcNow().AddSeconds(15);
            }
            Trace?.Invoke($"嘗試 {choice.Store.Name}（{attempt.Ssid}，" +
                (choice.Candidate.PasswordIndex is { } index ? $"第 {index + 1} 組密碼" : "自訂密碼") +
                $"）：{(connected ? "成功" : "失敗")}");
            if (!connected) continue;

            attempt.Connected = true;
            if (attempt.Scan is null)
                return Report(new WifiResult(
                    WifiStatus.Connected,
                    $"已連上{choice.Store.Name}，但未取得基地台資訊，無法記住門市。",
                    choice.Store.Name));

            // Only the network we joined: a different Louisa SSID nearby may be another store.
            var bssids = attempt.Scan.AccessPoints.Where(p => p.Ssid == attempt.Ssid && p.Rssi > -75).Select(p => p.Bssid)
                .Append(wifi.Current?.Bssid ?? attempt.Scan.AccessPoints[0].Bssid)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            try
            {
                if (choice.Candidate.PasswordIndex is null)
                    foreach (var bssid in bssids) secrets.Set(bssid, choice.Candidate.Password);
                learned.Remember(bssids, choice.Store.Name, choice.Candidate.PasswordIndex);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
            {
                return Report(new WifiResult(
                    WifiStatus.Connected,
                    $"已連上{choice.Store.Name}，但無法記住門市資訊：{error.Message}",
                    choice.Store.Name));
            }
            return Report(WifiResult.Connected(choice.Store.Name));
        }
        return null;
    }

    private void StopJoining(WifiConnection joining)
    {
        Trace?.Invoke($"中斷 Windows 正在連線的 {joining.Ssid}，改連路易莎 Wi-Fi。");
        wifi.Disconnect();
    }

    private WifiResult? ExistingConnection(Attempt attempt)
    {
        if (wifi.Current is { } current)
        {
            if (LouisaNetwork.IsLouisa(current.Ssid))
                return ConnectedDuringAttempt(attempt, current);
            if (attempt.Automatic)
                return Report(current.Joining ? WifiResult.Joining(current.Ssid) : WifiResult.Idle("目前已連線，保留現有 Wi-Fi。"));
            return null;
        }
        return wifi.HasInternet ? Report(WifiResult.Idle("目前已可上網，保留現有連線。")) : null;
    }

    private WifiResult ConnectedDuringAttempt(Attempt attempt, WifiConnection current)
    {
        if (!attempt.ProfileCaptured) return CurrentLouisa(current);
        attempt.Connected = true;
        // Windows joined by itself mid-attempt, so the store behind this link is unknown.
        return Report(new WifiResult(WifiStatus.Connected, $"已連上 {current.Ssid}，尚未辨識門市。"));
    }

    private void FinishAttempt(Attempt attempt)
    {
        if (!attempt.ProfileCaptured || attempt.Connected) return;
        if (wifi.Current is { } current && LouisaNetwork.IsLouisa(current.Ssid))
            ConnectedDuringAttempt(attempt, current);
        else
            wifi.RestoreLouisaProfile(attempt.Ssid, attempt.OriginalProfile);
    }

    private WifiResult CurrentLouisa(WifiConnection current) => Report(LouisaStatus(current));

    private WifiResult LouisaStatus(WifiConnection current) =>
        current.Bssid is not null &&
        learned.All.TryGetValue(LearnedStore.Normalize(current.Bssid), out var existing)
            ? WifiResult.Connected(existing.StoreName)
            : new WifiResult(WifiStatus.Connected, $"已連上 {current.Ssid}，尚未辨識門市。");

    private sealed class Attempt(WifiScan? scan, bool automatic)
    {
        public WifiScan? Scan { get; } = scan;
        /// The strongest Louisa SSID; every candidate password is tried on this one network.
        public string Ssid { get; } = scan?.AccessPoints.FirstOrDefault()?.Ssid ?? LouisaNetwork.DefaultSsid;
        public bool Automatic { get; } = automatic;
        public HashSet<string> Tried { get; } = new(StringComparer.Ordinal);
        public WifiProfileBackup? OriginalProfile { get; set; }
        public bool ProfileCaptured { get; set; }
        public bool Connected { get; set; }
    }

    private WifiResult Report(WifiResult result)
    {
        StatusChanged?.Invoke(result);
        return result;
    }

    private WifiResult Snooze(string key, string message)
    {
        snoozed[key] = clock.GetUtcNow().AddMinutes(10);
        return Report(WifiResult.Failed(message) with { Snoozed = true });
    }
}
