namespace LouisaWiFi.Windows.Platform;

public sealed record WifiAccessPoint(string Bssid, int Rssi);
public sealed record WifiConnection(string Ssid, string? Bssid);
public sealed record WifiScan(IReadOnlyList<WifiAccessPoint> AccessPoints);
public sealed record WifiProfileBackup(string Xml, int Position);

/// The password worked and Wi-Fi is connected, but Windows kept the profile on manual connect.
public sealed class AutoConnectNotKeptException()
    : InvalidOperationException("Windows 未穩定保留 Wi-Fi 自動連線設定。");

public interface IWifi
{
    WifiConnection? Current { get; }
    bool HasInternet { get; }
    bool CanReadBssid { get; }
    Task<WifiScan> ScanAsync(CancellationToken cancellationToken);
    WifiProfileBackup? CaptureLouisaProfile();
    Task<bool> ConnectAsync(string password, string? bssid, CancellationToken cancellationToken);
    void RestoreLouisaProfile(WifiProfileBackup? original);
}
