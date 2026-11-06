using LouisaWiFi.Windows.Services;

namespace LouisaWiFi.Windows.UI;

/// Minimal tray shell; menus arrive in later phases.
sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon icon;

    public TrayApp()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add($"門市資料：{Stores.All.Count} 家").Enabled = false;
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("結束", null, (_, _) => ExitThread());

        icon = new NotifyIcon { Icon = AppIcon.Load(), Text = "Louisa Wi-Fi", ContextMenuStrip = menu, Visible = true };
    }

    protected override void ExitThreadCore()
    {
        icon.Visible = false;
        icon.Dispose();
        base.ExitThreadCore();
    }
}

static class AppIcon
{
    // shortcut: icon built from the shared 1024px PNG at runtime; ship a multi-size .ico in Phase 6.
    public static Icon Load()
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("AppIcon.png")!;
        using var png = new Bitmap(stream);
        using var small = new Bitmap(png, SystemInformation.SmallIconSize);
        return Icon.FromHandle(small.GetHicon());
    }
}
