using ManagedNativeWifi;
using System.Net.NetworkInformation;
using Windows.Networking.Connectivity;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace LouisaWiFi.Windows.Platform;

public sealed class NativeWifiAdapter : IWifi
{
    private const string LouisaSsid = "LouisaCoffee";
    private readonly AppCapability wifiCapability = AppCapability.Create("wiFiControl");

    public WifiConnection? Current
    {
        get
        {
            var id = NativeWifi.EnumerateInterfaceConnections().FirstOrDefault(i => i.IsRadioOn)?.Id;
            if (id is null) return null;
            if (CanReadBssid)
            {
                var result = NativeWifi.GetCurrentConnection(id.Value);
                if (result.Item2 is { } connection && !string.IsNullOrEmpty(connection.Ssid.ToString()))
                    return new WifiConnection(connection.Ssid.ToString(), connection.Bssid.ToString());
            }

            var ssid = NetworkInformation.GetConnectionProfiles()
                .Where(profile => profile.IsWlanConnectionProfile)
                .Select(profile => profile.WlanConnectionProfileDetails?.GetConnectedSsid())
                .FirstOrDefault(name => !string.IsNullOrEmpty(name));
            return ssid is null ? null : new WifiConnection(ssid, null);
        }
    }

    public bool HasInternet =>
        NetworkInformation.GetInternetConnectionProfile()?.GetNetworkConnectivityLevel()
        == NetworkConnectivityLevel.InternetAccess;

    public bool CanReadBssid => wifiCapability.CheckAccess() is
        AppCapabilityAccessStatus.Allowed or AppCapabilityAccessStatus.NotDeclaredByApp;

    public async Task<WifiScan> ScanAsync(CancellationToken cancellationToken)
    {
        var id = InterfaceId();
        if (!CanReadBssid)
            throw new UnauthorizedAccessException("Windows 未授權讀取附近的 Wi-Fi 基地台。");

        var scanned = await NativeWifi.ScanNetworksAsync(TimeSpan.FromSeconds(10), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!scanned.Contains(id))
        {
            if (!CanReadBssid)
                throw new UnauthorizedAccessException("Windows 未授權讀取附近的 Wi-Fi 基地台。");
            throw new InvalidOperationException("Windows 無法完成 Wi-Fi 掃描，請確認無線網卡與定位權限。");
        }

        var (result, networks) = NativeWifi.EnumerateBssNetworks(id);
        if (result != ActionResult.Success || networks is null)
        {
            if (!CanReadBssid)
                throw new UnauthorizedAccessException("Windows 未授權讀取附近的 Wi-Fi 基地台。");
            throw new InvalidOperationException("Windows 無法讀取 Wi-Fi 掃描結果。");
        }
        var points = networks
            .Where(n => n.Ssid.ToString().Equals(LouisaSsid, StringComparison.OrdinalIgnoreCase))
            .Select(n => new WifiAccessPoint(n.Bssid.ToString(), n.Rssi))
            .OrderByDescending(n => n.Rssi)
            .ToList();
        return new WifiScan(points);
    }

    public WifiProfileBackup? CaptureLouisaProfile()
    {
        var id = InterfaceId();
        var profile = NativeWifi.EnumerateProfiles()
            .FirstOrDefault(p => p.InterfaceInfo.Id == id && p.Name == LouisaSsid);
        return profile is null ? null : new WifiProfileBackup(profile.Document.Xml, profile.Position);
    }

    public async Task<bool> ConnectAsync(string password, string? bssid, CancellationToken cancellationToken)
    {
        var id = InterfaceId();
        var xml = ProfileXml.Build(LouisaSsid, password, autoConnect: false);
        if (!NativeWifi.SetProfile(id, ProfileType.AllUser, xml, null, overwrite: true))
            throw new InvalidOperationException("Windows 無法更新 LouisaCoffee 的 Wi-Fi 設定。");

        using (var watcher = AcmConnectionWatcher.Register(id, LouisaSsid))
        {
            var target = bssid is null ? null : (NetworkIdentifier)PhysicalAddress.Parse(bssid);
            if (!NativeWifi.ConnectNetwork(id, LouisaSsid, BssType.Infrastructure, target)) return false;
            if (!await watcher.WaitAsync(TimeSpan.FromSeconds(35), cancellationToken)) return false;
        }

        var autoXml = ProfileXml.Build(LouisaSsid, password, autoConnect: true);
        var stable = await WaitForStableAutoConnectAsync(
            () => Current?.Ssid.Equals(LouisaSsid, StringComparison.OrdinalIgnoreCase) == true,
            () => IsAutoConnectEnabled(id),
            () =>
            {
                if (!NativeWifi.SetProfile(id, ProfileType.AllUser, autoXml, null, overwrite: true))
                    throw new InvalidOperationException("連線成功，但 Windows 無法恢復 Wi-Fi 自動連線設定。");
            },
            token => Task.Delay(TimeSpan.FromSeconds(1), token),
            cancellationToken);
        if (!stable && Current?.Ssid.Equals(LouisaSsid, StringComparison.OrdinalIgnoreCase) == true)
            throw new AutoConnectNotKeptException();
        return stable;
    }

    internal static async Task<bool> WaitForStableAutoConnectAsync(
        Func<bool> isConnected,
        Func<bool> isAutoConnectEnabled,
        Action enableAutoConnect,
        Func<CancellationToken, Task> wait,
        CancellationToken cancellationToken)
    {
        var connectedPolls = 0;
        var automaticPolls = 0;
        var profileUpdated = false;
        for (var i = 0; i < 30; i++)
        {
            await wait(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!isConnected())
            {
                connectedPolls = 0;
                automaticPolls = 0;
                continue;
            }
            connectedPolls++;
            if (isAutoConnectEnabled())
            {
                if (++automaticPolls == 5) return true;
                continue;
            }
            automaticPolls = 0;
            // Rewriting a connected profile can force Windows to disconnect.
            if (connectedPolls >= 5 && !profileUpdated)
            {
                enableAutoConnect();
                profileUpdated = true;
            }
        }
        return false;
    }

    private static bool IsAutoConnectEnabled(Guid id) =>
        NativeWifi.EnumerateProfiles().FirstOrDefault(p => p.InterfaceInfo.Id == id && p.Name == LouisaSsid)
            ?.Document.IsAutoConnectEnabled == true;

    public void RestoreLouisaProfile(WifiProfileBackup? original)
    {
        var id = InterfaceId();
        if (original is null)
        {
            if (!NativeWifi.DeleteProfile(id, LouisaSsid))
                throw new InvalidOperationException("無法移除本次建立的 LouisaCoffee Wi-Fi 設定。");
            return;
        }

        if (!NativeWifi.SetProfile(id, ProfileType.AllUser, original.Xml, null, overwrite: true))
            throw new InvalidOperationException("無法還原原本的 LouisaCoffee Wi-Fi 設定。");
        if (!NativeWifi.SetProfilePosition(id, LouisaSsid, original.Position))
            throw new InvalidOperationException("已還原 Wi-Fi 設定，但無法還原原本的連線優先順序。");
    }

    private static Guid InterfaceId() =>
        NativeWifi.EnumerateInterfaceConnections().FirstOrDefault(i => i.IsRadioOn)?.Id
        ?? throw new InvalidOperationException("請先開啟 Wi-Fi。");
}
