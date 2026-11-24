using LouisaWiFi.Windows.Models;
using ManagedNativeWifi;
using System.Net.NetworkInformation;
using Windows.Networking.Connectivity;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace LouisaWiFi.Windows.Platform;

public sealed class NativeWifiAdapter : IWifi
{
    private readonly AppCapability wifiCapability = AppCapability.Create("wiFiControl");

    public WifiConnection? Current
    {
        get
        {
            var radio = NativeWifi.EnumerateInterfaceConnections().FirstOrDefault(i => i.IsRadioOn);
            if (radio is null) return null;
            if (CanReadBssid)
            {
                var result = NativeWifi.GetCurrentConnection(radio.Id);
                if (result.Item2 is { } connection && !string.IsNullOrEmpty(connection.Ssid.ToString()))
                {
                    // Windows reports the SSID while still authenticating (e.g. retrying an old store's
                    // password); that must not stop us trying this store's. Another network mid-join is
                    // reported as Joining so the user can be asked before it is interrupted.
                    var joiningSsid = connection.Ssid.ToString();
                    var joining = connection.InterfaceState is
                        InterfaceState.Associating or InterfaceState.Discovering or InterfaceState.Authenticating;
                    if (connection.InterfaceState != InterfaceState.Connected && LouisaNetwork.IsLouisa(joiningSsid))
                        return null;
                    return new WifiConnection(joiningSsid, connection.Bssid.ToString(), joining);
                }
            }
            if (!radio.IsConnected)
            {
                // While associating Windows reports no current connection at all, only the interface state.
                var state = NativeWifi.EnumerateInterfaces().FirstOrDefault(i => i.Id == radio.Id)?.State;
                return state is InterfaceState.Associating or InterfaceState.Discovering or InterfaceState.Authenticating
                    ? new WifiConnection("", null, Joining: true)
                    : null;
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
            .Where(n => LouisaNetwork.IsLouisa(n.Ssid.ToString()))
            .Select(n => new WifiAccessPoint(n.Bssid.ToString(), n.Rssi, n.Ssid.ToString()))
            .OrderByDescending(n => n.Rssi)
            .ToList();
        return new WifiScan(points);
    }

    public WifiProfileBackup? CaptureLouisaProfile(string ssid)
    {
        var id = InterfaceId();
        var profile = NativeWifi.EnumerateProfiles()
            .FirstOrDefault(p => p.InterfaceInfo.Id == id && p.Name == ssid);
        return profile is null ? null : new WifiProfileBackup(profile.Document.Xml, profile.Position);
    }

    public async Task<bool> ConnectAsync(string ssid, string password, string? bssid, CancellationToken cancellationToken)
    {
        if (!LouisaNetwork.IsLouisa(ssid)) throw new ArgumentException("不是路易莎的 Wi-Fi。", nameof(ssid));
        var id = InterfaceId();
        // Windows' "connect automatically" belongs to the user: keep the stored profile's choice, new ones stay manual.
        var autoConnect = NativeWifi.EnumerateProfiles()
            .FirstOrDefault(p => p.InterfaceInfo.Id == id && p.Name == ssid)?.Document.IsAutoConnectEnabled == true;
        var xml = ProfileXml.Build(ssid, password, autoConnect);
        if (!NativeWifi.SetProfile(id, ProfileType.AllUser, xml, null, overwrite: true))
            throw new InvalidOperationException($"Windows 無法更新 {ssid} 的 Wi-Fi 設定。");

        var target = bssid is null ? null : (NetworkIdentifier)PhysicalAddress.Parse(bssid);
        using var watcher = AcmConnectionWatcher.Register(id, ssid);
        if (!NativeWifi.ConnectNetwork(id, ssid, BssType.Infrastructure, target)) return false;
        return await watcher.WaitAsync(TimeSpan.FromSeconds(35), cancellationToken);
    }

    public void RestoreLouisaProfile(string ssid, WifiProfileBackup? original)
    {
        var id = InterfaceId();
        if (original is null)
        {
            if (!NativeWifi.DeleteProfile(id, ssid))
                throw new InvalidOperationException($"無法移除本次建立的 {ssid} Wi-Fi 設定。");
            return;
        }

        if (!NativeWifi.SetProfile(id, ProfileType.AllUser, original.Xml, null, overwrite: true))
            throw new InvalidOperationException($"無法還原原本的 {ssid} Wi-Fi 設定。");
        if (!NativeWifi.SetProfilePosition(id, ssid, original.Position))
            throw new InvalidOperationException("已還原 Wi-Fi 設定，但無法還原原本的連線優先順序。");
    }

    public void Disconnect()
    {
        if (!NativeWifi.DisconnectNetwork(InterfaceId()))
            throw new InvalidOperationException("Windows 無法中斷目前的 Wi-Fi 連線。");
    }

    private static Guid InterfaceId() =>
        NativeWifi.EnumerateInterfaceConnections().FirstOrDefault(i => i.IsRadioOn)?.Id
        ?? throw new InvalidOperationException("請先開啟 Wi-Fi。");
}
