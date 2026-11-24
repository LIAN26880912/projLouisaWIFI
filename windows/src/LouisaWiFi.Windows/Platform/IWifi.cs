using LouisaWiFi.Windows.Models;

namespace LouisaWiFi.Windows.Platform;

/// Ssid is the Louisa network the access point broadcasts (see LouisaNetwork).
public sealed record WifiAccessPoint(string Bssid, int Rssi, string Ssid = LouisaNetwork.DefaultSsid);
/// Joining: Windows is still associating/authenticating to this (non-Louisa) network.
public sealed record WifiConnection(string Ssid, string? Bssid, bool Joining = false);
public sealed record WifiScan(IReadOnlyList<WifiAccessPoint> AccessPoints);
public sealed record WifiProfileBackup(string Xml, int Position);

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
    /// Stops whatever Windows is joining; only after the user asks to switch to Louisa.
    void Disconnect();
}
