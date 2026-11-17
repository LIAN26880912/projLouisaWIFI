using LouisaWiFi.Windows.Models;

namespace LouisaWiFi.Windows.Platform;

/// Ssid is the Louisa network the access point broadcasts (see LouisaNetwork).
public sealed record WifiAccessPoint(string Bssid, int Rssi, string Ssid = LouisaNetwork.DefaultSsid);
public sealed record WifiConnection(string Ssid, string? Bssid);
public sealed record WifiScan(IReadOnlyList<WifiAccessPoint> AccessPoints);
public sealed record WifiProfileBackup(string Xml, int Position);

/// The password worked and Wi-Fi is connected, but Windows kept the profile on manual connect.
public sealed class AutoConnectNotKeptException()
    : InvalidOperationException("Windows 未穩定保留 Wi-Fi 自動連線設定。");

public interface IWifi
{
    /// Null while Windows is still joining a Louisa network (it may be retrying an old password).
    WifiConnection? Current { get; }
    bool HasInternet { get; }
    bool CanReadBssid { get; }
    /// Access points of Louisa networks only, strongest first.
    Task<WifiScan> ScanAsync(CancellationToken cancellationToken);
    WifiProfileBackup? CaptureLouisaProfile(string ssid);
    Task<bool> ConnectAsync(string ssid, string password, string? bssid, CancellationToken cancellationToken);
    void RestoreLouisaProfile(string ssid, WifiProfileBackup? original);
}
