using LouisaWiFi.Windows.Models;
using LouisaWiFi.Windows.Platform;
using LouisaWiFi.Windows.Services;
using Microsoft.Win32;
using System.Net.NetworkInformation;

namespace LouisaWiFi.Windows.UI;

sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon icon;
    private readonly ContextMenuStrip menu;
    private readonly ToolStripMenuItem status;
    private readonly ToolStripMenuItem autoConnect;
    private readonly Connector connector;
    private readonly IWifi wifi;
    private readonly LocationService location;
    private readonly SettingsStore settings;
    private readonly SynchronizationContext uiContext;
    private readonly System.Windows.Forms.Timer timer;
    private readonly CancellationTokenSource lifetime = new();
    private WifiResult? lastStatus;
    private bool automaticRunning;
    private bool manualRunning;
    private bool permissionNoticeShown;
    private bool locationReady;
    private bool closing;
    private bool disposed;

    public TrayApp(Connector connector, IWifi wifi, LocationService location, SettingsStore settings)
    {
        this.connector = connector;
        this.wifi = wifi;
        this.location = location;
        this.settings = settings;
        uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        menu = new ContextMenuStrip();
        status = new ToolStripMenuItem("就緒") { Enabled = false };
        menu.Items.Add(status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("連接門市…", null, async (_, _) => await ChooseStoreAsync());
        autoConnect = new ToolStripMenuItem("自動連線") { Checked = settings.AutoConnect };
        autoConnect.Click += (_, _) => ToggleAutoConnect();
        menu.Items.Add(autoConnect);
        menu.Items.Add("定位權限設定…", null, async (_, _) => await ManagePermissionAsync());
        menu.Items.Add($"門市資料：{Stores.All.Count} 家").Enabled = false;
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("結束", null, async (_, _) => await ExitAsync());

        icon = new NotifyIcon { Icon = AppIcon.Load(), Text = "Louisa Wi-Fi", ContextMenuStrip = menu, Visible = true };
        connector.StatusChanged += OnStatusChanged;
        location.Changed += OnLocationChanged;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        timer = new System.Windows.Forms.Timer { Interval = 15_000, Enabled = true };
        timer.Tick += (_, _) => QueueAutomatic();
        QueueAutomatic();
    }

    private async Task ChooseStoreAsync()
    {
        if (manualRunning || closing) return;
        manualRunning = true;
        connector.CancelAutomatic();
        try
        {
            var (store, password) = StorePicker.Choose();
            if (store is null || closing) return;
            if (!wifi.HasInternet) await EnsureLocationAccessAsync(userInitiated: true);
            if (!closing) await connector.ConnectAsync(store, password, lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            OnStatusChanged(WifiResult.Failed(error.Message));
        }
        finally
        {
            manualRunning = false;
            if (!closing && settings.AutoConnect) QueueAutomatic();
        }
    }

    private void ToggleAutoConnect()
    {
        if (closing) return;
        try
        {
            settings.SetAutoConnect(!settings.AutoConnect);
            autoConnect.Checked = settings.AutoConnect;
            if (settings.AutoConnect) QueueAutomatic();
            else
            {
                connector.CancelAutomatic();
                location.SetWatching(false);
                OnStatusChanged(WifiResult.Idle("自動連線已關閉，仍可手動選店。"));
            }
        }
        catch (Exception error)
        {
            OnStatusChanged(WifiResult.Failed($"無法更新自動連線設定：{error.Message}"));
        }
    }

    private async Task ManagePermissionAsync()
    {
        if (manualRunning || closing) return;
        manualRunning = true;
        connector.CancelAutomatic();
        try { await EnsureLocationAccessAsync(userInitiated: true, showSettings: true); }
        catch (Exception error) { OnStatusChanged(WifiResult.Failed(error.Message)); }
        finally
        {
            manualRunning = false;
            if (!closing && settings.AutoConnect) QueueAutomatic();
        }
    }

    private async Task EnsureLocationAccessAsync(bool userInitiated, bool showSettings = false)
    {
        var permission = location.Permission;
        if (permission != LocationPermission.Allowed || showSettings)
        {
            locationReady = false;
            location.SetWatching(false);
            if (!userInitiated && permissionNoticeShown) return;
            permissionNoticeShown = true;
            using var dialog = new LocationPermissionDialog(location);
            if (dialog.ShowDialog() != DialogResult.OK || closing) return;
        }

        if (!locationReady)
        {
            permission = await location.RequestAccessAsync();
            if (closing) return;
            if (permission != LocationPermission.Allowed)
            {
                OnStatusChanged(WifiResult.Idle("Windows 尚未允許定位或 Wi-Fi 資訊，請檢查定位設定。"));
                return;
            }
            locationReady = true;
        }
        location.SetWatching(settings.AutoConnect);
    }

    private void QueueAutomatic()
    {
        uiContext.Post(async _ => await RunAutomaticAsync(), null);
    }

    private async Task RunAutomaticAsync()
    {
        if (closing || manualRunning || automaticRunning || !settings.AutoConnect) return;
        automaticRunning = true;
        try
        {
            await EnsureLocationAccessAsync(userInitiated: false);
            if (!closing && !manualRunning && settings.AutoConnect)
                await connector.AutoConnectAsync(location, Stores.All, lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            OnStatusChanged(WifiResult.Failed(error.Message));
        }
        finally
        {
            automaticRunning = false;
        }
    }

    private void OnLocationChanged()
    {
        uiContext.Post(_ =>
        {
            if (closing) return;
            if (location.Permission != LocationPermission.Allowed)
            {
                locationReady = false;
                location.SetWatching(false);
            }
            QueueAutomatic();
        }, null);
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs args) => QueueAutomatic();
    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs args) => QueueAutomatic();
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs args)
    {
        if (args.Mode == PowerModes.Resume) QueueAutomatic();
    }

    private async Task ExitAsync()
    {
        if (closing) return;
        closing = true;
        timer.Stop();
        location.SetWatching(false);
        lifetime.Cancel();
        await connector.CancelAndWaitAsync();
        ExitThread();
    }

    private void OnStatusChanged(WifiResult result)
    {
        void Update()
        {
            if (disposed) return;
            var changed = lastStatus != result;
            lastStatus = result;
            status.Text = result.Message;
            icon.Text = result.Message.Length > 63 ? result.Message[..60] + "..." : result.Message;
            if (changed && result.Status is (WifiStatus.Connected or WifiStatus.Failed))
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
        closing = true;
        lifetime.Cancel();
        timer.Stop();
        timer.Dispose();
        location.SetWatching(false);
        location.Changed -= OnLocationChanged;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        connector.StatusChanged -= OnStatusChanged;
        disposed = true;
        icon.Visible = false;
        icon.Dispose();
        menu.Dispose();
        lifetime.Dispose();
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
