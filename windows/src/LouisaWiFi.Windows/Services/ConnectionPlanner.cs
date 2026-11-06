using LouisaWiFi.Windows.Models;
using LouisaWiFi.Windows.Platform;
using System.Security.Cryptography;
using System.Text.Json;

namespace LouisaWiFi.Windows.Services;

public sealed record PasswordCandidate(string Password, int? PasswordIndex);

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
}

public sealed class Connector
{
    private const string LouisaSsid = "LouisaCoffee";
    private readonly IWifi wifi;
    private readonly ILearnedStore learned;
    private readonly ISecretStore secrets;
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly object gate = new();
    private CancellationTokenSource? active;

    public Connector(IWifi wifi, ILearnedStore learned, ISecretStore secrets)
    {
        this.wifi = wifi;
        this.learned = learned;
        this.secrets = secrets;
    }

    public event Action<WifiResult>? StatusChanged;

    public async Task<WifiResult> ConnectAsync(Store store, string? password = null, CancellationToken cancellationToken = default)
    {
        CancellationTokenSource current;
        lock (gate)
        {
            active?.Cancel();
            current = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            active = current;
        }

        try
        {
            await serial.WaitAsync(current.Token);
            try { return await ConnectCoreAsync(store, password, current.Token); }
            finally { serial.Release(); }
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(active, current)) active = null;
            }
            current.Dispose();
        }
    }

    private async Task<WifiResult> ConnectCoreAsync(Store store, string? password, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (wifi.Current is { } current &&
            current.Ssid.Equals(LouisaSsid, StringComparison.OrdinalIgnoreCase))
        {
            return learned.All.TryGetValue(LearnedStore.Normalize(current.Bssid), out var existing)
                ? Report(WifiResult.Connected(existing.StoreName))
                : Report(new WifiResult(WifiStatus.Connected, "已連上 LouisaCoffee，尚未辨識門市。"));
        }
        if (wifi.Current is not null && wifi.HasInternet)
            return Report(WifiResult.Idle("目前已可上網，保留現有連線。"));

        var scan = await wifi.ScanAsync(cancellationToken);
        if (scan.AccessPoints.Count == 0)
            return Report(WifiResult.Failed("附近找不到 LouisaCoffee Wi-Fi。"));

        var candidates = ConnectionPlanner.Plan(store, scan, learned.All, secrets, password);
        if (candidates.Count == 0)
            return Report(WifiResult.Failed("沒有可用的密碼，請輸入店內公告的 Wi-Fi 密碼。"));

        var originalProfile = wifi.CaptureLouisaProfile();
        var connected = false;
        try
        {
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Report(WifiResult.Connecting(store.Name));
                if (!await wifi.ConnectAsync(candidate.Password, scan.AccessPoints[0].Bssid, cancellationToken))
                    continue;

                connected = true;
                var bssids = scan.AccessPoints.Where(p => p.Rssi > -75).Select(p => p.Bssid)
                    .Append(wifi.Current?.Bssid ?? scan.AccessPoints[0].Bssid)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                try
                {
                    learned.Remember(bssids, store.Name, candidate.PasswordIndex);
                    if (candidate.PasswordIndex is null)
                        foreach (var bssid in bssids) secrets.Set(bssid, candidate.Password);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
                {
                    return Report(new WifiResult(
                        WifiStatus.Connected,
                        $"已連上{store.Name}，但無法記住門市資訊：{error.Message}",
                        store.Name));
                }
                return Report(WifiResult.Connected(store.Name));
            }

            return Report(WifiResult.Failed(password is null
                ? $"無法連上{store.Name}，請確認門市與密碼。"
                : $"密碼無法連上{store.Name}，請確認店內公告的密碼。"));
        }
        finally
        {
            if (!connected) wifi.RestoreLouisaProfile(originalProfile);
        }
    }

    private WifiResult Report(WifiResult result)
    {
        StatusChanged?.Invoke(result);
        return result;
    }
}
