using LouisaWiFi.Windows.Models;
using LouisaWiFi.Windows.Services;

namespace LouisaWiFi.Windows.UI;

sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon icon;
    private readonly ContextMenuStrip menu;
    private readonly ToolStripMenuItem status;
    private readonly Connector connector;
    private readonly SynchronizationContext uiContext;
    private bool disposed;

    public TrayApp(Connector connector)
    {
        this.connector = connector;
        uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        menu = new ContextMenuStrip();
        status = new ToolStripMenuItem("就緒") { Enabled = false };
        menu.Items.Add(status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("連接門市…", null, async (_, _) => await ChooseStoreAsync());
        menu.Items.Add($"門市資料：{Stores.All.Count} 家").Enabled = false;
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("結束", null, (_, _) => ExitThread());

        icon = new NotifyIcon { Icon = AppIcon.Load(), Text = "Louisa Wi-Fi", ContextMenuStrip = menu, Visible = true };
        connector.StatusChanged += OnStatusChanged;
    }

    private async Task ChooseStoreAsync()
    {
        var (store, password) = StorePicker.Choose();
        if (store is null) return;
        try
        {
            await connector.ConnectAsync(store, password);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            OnStatusChanged(WifiResult.Failed(error.Message));
        }
    }

    private void OnStatusChanged(WifiResult result)
    {
        void Update()
        {
            if (disposed) return;
            status.Text = result.Message;
            icon.Text = result.Message.Length > 63 ? result.Message[..60] + "..." : result.Message;
            if (result.Status is WifiStatus.Connected or WifiStatus.Failed)
            {
                icon.BalloonTipTitle = "Louisa Wi-Fi";
                icon.BalloonTipText = result.Message;
                icon.ShowBalloonTip(3000);
            }
        }

        uiContext.Post(_ => Update(), null);
    }

    protected override void ExitThreadCore()
    {
        connector.StatusChanged -= OnStatusChanged;
        disposed = true;
        icon.Visible = false;
        icon.Dispose();
        menu.Dispose();
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
        var handle = small.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
