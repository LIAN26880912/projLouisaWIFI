using ManagedNativeWifi;
using System.Net.NetworkInformation;
using Windows.Networking.Connectivity;

namespace LouisaWiFi.Windows.Platform;

public sealed class NativeWifiAdapter : IWifi
{
    private const string LouisaSsid = "LouisaCoffee";

    public WifiConnection? Current
    {
        get
        {
            var result = NativeWifi.GetCurrentConnection(InterfaceId());
            return result.Item2 is { } connection && !string.IsNullOrEmpty(connection.Ssid.ToString())
                ? new WifiConnection(connection.Ssid.ToString(), connection.Bssid.ToString())
                : null;
        }
    }

    public bool HasInternet =>
        NetworkInformation.GetInternetConnectionProfile()?.GetNetworkConnectivityLevel()
        == NetworkConnectivityLevel.InternetAccess;

    public async Task<WifiScan> ScanAsync(CancellationToken cancellationToken)
    {
        _ = InterfaceId();
        await NativeWifi.ScanNetworksAsync(TimeSpan.FromSeconds(10), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var points = NativeWifi.EnumerateBssNetworks()
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

    public async Task<bool> ConnectAsync(string password, string bssid, CancellationToken cancellationToken)
    {
        var id = InterfaceId();
        var xml = ProfileXml.Build(LouisaSsid, password, autoConnect: false);
        if (!NativeWifi.SetProfile(id, ProfileType.AllUser, xml, null, overwrite: true))
            throw new InvalidOperationException("Windows 無法更新 LouisaCoffee 的 Wi-Fi 設定。");

        NetworkIdentifier targetBssid = PhysicalAddress.Parse(bssid);
        var connected = await NativeWifi.ConnectNetworkAsync(
            id, LouisaSsid, BssType.Infrastructure, targetBssid,
            TimeSpan.FromSeconds(35), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!connected) return false;

        var autoXml = ProfileXml.Build(LouisaSsid, password, autoConnect: true);
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        for (var i = 0; i < 5; i++)
        {
            if (IsAutoConnectEnabled(id)) break;
            if (!NativeWifi.SetProfile(id, ProfileType.AllUser, autoXml, null, overwrite: true))
                throw new InvalidOperationException("連線成功，但 Windows 無法恢復 Wi-Fi 自動連線設定。");
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
        if (!IsAutoConnectEnabled(id))
            throw new InvalidOperationException("已連線，但 Windows 未保留 Wi-Fi 自動連線設定。");

        for (var i = 0; i < 30; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Current?.Ssid.Equals(LouisaSsid, StringComparison.OrdinalIgnoreCase) == true)
                return true;
            await Task.Delay(500, cancellationToken);
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
