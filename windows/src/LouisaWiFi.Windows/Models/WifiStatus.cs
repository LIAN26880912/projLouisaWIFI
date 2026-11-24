namespace LouisaWiFi.Windows.Models;

public enum WifiStatus
{
    Idle,
    Connecting,
    Connected,
    Failed
}

public sealed record WifiResult(WifiStatus Status, string Message, string? StoreName = null)
{
    /// Auto-connect now skips this base station for 10 minutes (like macOS); a user action retries at once.
    public bool Snoozed { get; init; }
    /// Windows is still joining this other network; auto-connect leaves it unless the user asks to switch.
    public string? JoiningOther { get; init; }

    public static WifiResult Idle(string message) => new(WifiStatus.Idle, message);
    public static WifiResult Joining(string ssid) => new(WifiStatus.Idle, $"Windows 正在連線 {ssid}。") { JoiningOther = ssid };
    public static WifiResult Connecting(string store) => new(WifiStatus.Connecting, $"連線中：{store}…", store);
    public static WifiResult Connected(string store) => new(WifiStatus.Connected, $"已連上：{store}", store);
    public static WifiResult Failed(string message) => new(WifiStatus.Failed, message);
}
