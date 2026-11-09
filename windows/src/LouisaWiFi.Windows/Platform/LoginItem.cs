using Microsoft.Win32;

namespace LouisaWiFi.Windows.Platform;

/// "登入時開啟" via HKCU\...\Run, the per-user switch Task Manager also shows.
public sealed class LoginItem(
    string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run",
    string approvedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
    string? executable = null)
{
    private const string Name = "LouisaWiFi";
    private readonly string? executable = executable ?? Environment.ProcessPath;

    /// Only the real app exe can be registered (not dotnet.exe hosting a dll).
    public bool Supported => executable is not null &&
        Path.GetFileName(executable).Equals("LouisaWiFi.exe", StringComparison.OrdinalIgnoreCase);

    // Quoted so a path with spaces can't be read as another program.
    private string Command => $"\"{executable}\"";

    public bool Enabled
    {
        get
        {
            using var run = Registry.CurrentUser.OpenSubKey(runKey);
            if (!Supported || run?.GetValue(Name) as string != Command) return false;
            // Task Manager "Disabled" stores an odd first byte here.
            using var approved = Registry.CurrentUser.OpenSubKey(approvedKey);
            return approved?.GetValue(Name) is not byte[] { Length: > 0 } state || (state[0] & 1) == 0;
        }
    }

    public void Set(bool enabled)
    {
        using var run = Registry.CurrentUser.CreateSubKey(runKey);
        if (!enabled)
        {
            run.DeleteValue(Name, throwOnMissingValue: false);
            return;
        }
        if (!Supported) throw new InvalidOperationException("請直接執行 LouisaWiFi.exe 後再設定登入時開啟。");
        run.SetValue(Name, Command);
        // Turning it on here overrides an earlier "Disabled" in Task Manager.
        using var approved = Registry.CurrentUser.OpenSubKey(approvedKey, writable: true);
        approved?.DeleteValue(Name, throwOnMissingValue: false);
    }
}
