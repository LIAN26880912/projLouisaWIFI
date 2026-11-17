using LouisaWiFi.Windows.Models;
using LouisaWiFi.Windows.Platform;
using LouisaWiFi.Windows.Services;
using Microsoft.Win32;
using System.Diagnostics;
using System.Net.NetworkInformation;

namespace LouisaWiFi.Windows.UI;

/// Tray icon and menu; port of app/Sources/MenuController.swift and main.swift.
sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon icon;
    private readonly ContextMenuStrip menu;
    private readonly Connector connector;
    private readonly IWifi wifi;
    private readonly LocationService location;
    private readonly SettingsStore settings;
    private readonly UsageTracker usage;
    private readonly LoginItem loginItem;
    private readonly Updater updater;
    private readonly SingleInstance? instance;
    private readonly SynchronizationContext uiContext;
    private readonly System.Windows.Forms.Timer timer;
    private readonly System.Windows.Forms.Timer usageTimer;
    private readonly System.Windows.Forms.Timer updateTimer;
    private readonly CancellationTokenSource lifetime = new();
    private ToolStripMenuItem? status;
    private StatsForm? statsForm;
    private AppRelease? available;
    private WifiResult? lastStatus;
    private string? lastTrace;
    private string? lastBalloon;
    private long lastBalloonAt;
    private bool automaticRunning;
    private bool manualRunning;
    private bool permissionNoticeShown;
    private bool locationReady;
    private bool closing;
    private bool disposed;

    public TrayApp(Connector connector, IWifi wifi, LocationService location, SettingsStore settings,
        UsageTracker usage, LoginItem loginItem, Updater updater, SingleInstance? instance, string[] startupArgs)
    {
        this.connector = connector;
        this.wifi = wifi;
        this.location = location;
        this.settings = settings;
        this.usage = usage;
        this.loginItem = loginItem;
        this.updater = updater;
        this.instance = instance;
        uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        menu = new ContextMenuStrip();
        menu.Opening += (_, e) =>
        {
            BuildMenu();
            e.Cancel = false;
        };
        BuildMenu();

        icon = new NotifyIcon { Icon = AppIcon.Load(), Text = "路易莎 Wi-Fi", ContextMenuStrip = menu, Visible = true };
        connector.StatusChanged += OnStatusChanged;
        connector.Trace += OnTrace;
        location.Changed += OnLocationChanged;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        timer = new System.Windows.Forms.Timer { Interval = 15_000, Enabled = true };
        timer.Tick += (_, _) => QueueAutomatic();
        // Like macOS Tracker: a minute on Louisa Wi-Fi per tick; timers don't run while asleep.
        usageTimer = new System.Windows.Forms.Timer { Interval = 60_000, Enabled = true };
        usageTimer.Tick += (_, _) => RecordUsage();
        // Like macOS: check soon after start, then every 6 hours; dev builds only check from the menu.
        updateTimer = new System.Windows.Forms.Timer { Interval = 30_000, Enabled = Updater.IsPublished };
        updateTimer.Tick += (_, _) =>
        {
            updateTimer.Interval = 6 * 60 * 60 * 1000;
            _ = CheckForUpdatesAsync(manual: false);
        };

        _ = instance?.ListenAsync(args => uiContext.Post(_ => _ = RunCommandAsync(args), null), lifetime.Token);
        ReportRecoveredFiles();
        AppLog.Write($"started: {Stores.All.Count} stores");
        if (startupArgs.Length > 0) uiContext.Post(_ => _ = RunCommandAsync(startupArgs), null);
        QueueAutomatic();
    }

    // MARK: - Menu

    private void BuildMenu()
    {
        foreach (var old in menu.Items.Cast<ToolStripItem>().ToList()) old.Dispose();
        menu.Items.Clear();
        var items = menu.Items;

        status = Disabled(lastStatus?.Message ?? "就緒");
        items.Add(status);

        IReadOnlySet<string> known;
        try { known = connector.KnownStores(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { known = new HashSet<string>(); }
        var onlyKnown = settings.OnlyKnown;
        var stores = onlyKnown ? Stores.All.Where(s => known.Contains(s.Name)).ToList() : Stores.All;
        var here = location.LastUsableFix;

        // For when the password is posted on the wall: the nearest store's passwords, or type one.
        if (here is not null && Stores.Nearest(here.Latitude, here.Longitude, 1) is [var (nearest, _)])
        {
            var passwords = new ToolStripMenuItem("用密碼連線");
            passwords.DropDownItems.Add(Disabled(nearest.Name));
            AddPasswordItems(passwords.DropDownItems, nearest);
            items.Add(passwords);
        }
        items.Add(new ToolStripSeparator());

        if (onlyKnown && stores.Count == 0)
            items.Add(Disabled("還沒有連過任何門市"));
        else if (here is not null)
        {
            items.Add(Disabled(onlyKnown ? "連過的門市" : "附近門市"));
            foreach (var (store, meters) in Stores.Nearest(here.Latitude, here.Longitude, 6, stores))
                items.Add(Item($"{store.Name}  ·  {Distance(meters)}", () => ConnectAsync(store, null), Tip(store)));
        }
        else
            items.Add(Item("連線到最近的門市", ConnectNearestAsync));

        var all = new ToolStripMenuItem(Escape(onlyKnown ? "所有連過的門市" : "所有門市")) { Enabled = stores.Count > 0 };
        foreach (var (city, cityStores) in Stores.ByCity(stores))
        {
            var cityItem = new ToolStripMenuItem(Escape($"{city}（{cityStores.Count}）"));
            Lazy(cityItem, list =>
            {
                foreach (var store in cityStores) list.Add(StoreItem(store));
            });
            all.DropDownItems.Add(cityItem);
        }
        items.Add(all);
        items.Add(Item("搜尋門市…", ChooseStoreAsync));
        items.Add(new ToolStripSeparator());

        items.Add(Item("連線統計…", () => { ShowStats(); return Task.CompletedTask; }));
        items.Add(new ToolStripSeparator());

        items.Add(Item("定位權限設定…", ManagePermissionAsync));
        items.Add(Toggle("自動連線", settings.AutoConnect, ToggleAutoConnect));
        items.Add(Toggle("只顯示連過的門市", onlyKnown, ToggleOnlyKnown));
        var login = Toggle("登入時開啟", SafeLoginEnabled(), ToggleLogin);
        login.Enabled = loginItem.Supported;
        if (!loginItem.Supported) login.ToolTipText = "請直接執行 LouisaWiFi.exe 才能設定";
        items.Add(login);
        items.Add(new ToolStripSeparator());
        if (available is { } release)
            items.Add(Item($"更新到 {Updater.Display(release.Version)}", InstallUpdateAsync));
        items.Add(Item($"檢查更新（目前 {Updater.Display(updater.Current)}）", () => CheckForUpdatesAsync(manual: true)));
        items.Add(Item("結束", ExitAsync));
    }

    /// A store under 所有門市: try every candidate, or pick one.
    private ToolStripMenuItem StoreItem(Store store)
    {
        var item = new ToolStripMenuItem(Escape(store.Name)) { ToolTipText = Tip(store) };
        Lazy(item, list =>
        {
            list.Add(Item("自動嘗試", () => ConnectAsync(store, null)));
            list.Add(new ToolStripSeparator());
            AddPasswordItems(list, store);
        });
        return item;
    }

    private void AddPasswordItems(ToolStripItemCollection list, Store store)
    {
        foreach (var password in store.Passwords)
            list.Add(Item(password, () => ConnectAsync(store, password)));
        list.Add(Item("輸入密碼…", () => EnterPasswordAsync(store)));
    }

    /// 400+ stores: fill submenus only when opened.
    private static void Lazy(ToolStripMenuItem item, Action<ToolStripItemCollection> fill)
    {
        item.DropDownItems.Add(Disabled("…"));
        var filled = false;
        item.DropDownOpening += (_, _) =>
        {
            if (filled) return;
            filled = true;
            foreach (var placeholder in item.DropDownItems.Cast<ToolStripItem>().ToList()) placeholder.Dispose();
            item.DropDownItems.Clear();
            fill(item.DropDownItems);
        };
    }

    private static ToolStripMenuItem Disabled(string text) => new(Escape(text)) { Enabled = false };

    private static ToolStripMenuItem Item(string text, Func<Task> run, string? tip = null)
    {
        var item = new ToolStripMenuItem(Escape(text)) { ToolTipText = tip };
        item.Click += async (_, _) => await run();
        return item;
    }

    private static ToolStripMenuItem Toggle(string text, bool on, Action toggle)
    {
        var item = new ToolStripMenuItem(Escape(text)) { Checked = on };
        item.Click += (_, _) => toggle();
        return item;
    }

    // "&" would otherwise become a keyboard mnemonic and vanish from passwords.
    private static string Escape(string text) => text.Replace("&", "&&");

    private static string Tip(Store store) => $"{store.Address}\n{store.Phone}";

    private static string Distance(double meters) =>
        meters < 1000 ? $"{(int)(meters / 10) * 10} m" : $"{meters / 1000:0.0} km";

    // MARK: - Actions

    private Task ConnectAsync(Store store, string? password) =>
        RunManualAsync(token => connector.ConnectAsync(store, password, token));

    private Task ConnectNearestAsync() =>
        RunManualAsync(token => connector.ConnectNearestAsync(location, Stores.All, token));

    private async Task ChooseStoreAsync()
    {
        if (!BeginDialog()) return;
        (Store? Store, string? Password) choice;
        try { choice = StorePicker.Choose(); }
        finally { manualRunning = false; }
        if (choice.Store is { } store) await ConnectAsync(store, choice.Password);
        else QueueAutomatic();
    }

    private async Task EnterPasswordAsync(Store store)
    {
        if (!BeginDialog()) return;
        string? password;
        try { password = PasswordDialog.Ask(null, store.Name); }
        finally { manualRunning = false; }
        if (password is not null) await ConnectAsync(store, password);
        else QueueAutomatic();
    }

    /// Holds off auto-connect while the user is choosing.
    private bool BeginDialog()
    {
        if (manualRunning || closing) return false;
        manualRunning = true;
        connector.CancelAutomatic();
        return true;
    }

    /// A user action: supersedes auto-connect and always reports its outcome.
    private async Task RunManualAsync(Func<CancellationToken, Task<WifiResult>> work)
    {
        if (closing) return;
        if (manualRunning)
        {
            Notify("正在進行其他連線操作，請稍候。");
            return;
        }
        manualRunning = true;
        connector.CancelAutomatic();
        try
        {
            if (!wifi.HasInternet) await EnsureLocationAccessAsync(userInitiated: true);
            if (closing) return;
            var result = await work(lifetime.Token);
            if (!closing) Notify(result.Message);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            OnStatusChanged(WifiResult.Failed(error.Message));
        }
        finally
        {
            manualRunning = false;
            ReportRecoveredFiles();
            QueueAutomatic();
        }
    }

    private async Task RunCommandAsync(IReadOnlyList<string> args)
    {
        if (closing) return;
        var (command, error) = CommandLine.Parse(args);
        if (error is not null)
        {
            Notify(error);
            return;
        }
        switch (command?.Kind)
        {
            case CommandKind.Nearest:
                await ConnectNearestAsync();
                break;
            case CommandKind.Connect:
                var name = command.Store!;
                if ((Stores.Named(name) ?? Stores.Named(name + "門市")) is { } store) await ConnectAsync(store, null);
                else Notify($"找不到門市：{name}");
                break;
            case CommandKind.Stats:
                ShowStats();
                break;
            case CommandKind.Updated:
                Notify($"已更新到 {Updater.Display(updater.Current)}");
                // The previous exe is still exiting for a few seconds.
                await Task.Delay(TimeSpan.FromSeconds(20));
                CleanupUpdate();
                break;
            case CommandKind.UpdateFailed:
                Notify("更新失敗，已還原為原本的版本。");
                CleanupUpdate();
                break;
            default:
                Notify(lastStatus?.Message ?? "路易莎 Wi-Fi 正在背景執行，請從系統匣圖示操作。");
                break;
        }
    }

    private void ShowStats()
    {
        if (closing) return;
        if (statsForm is null)
        {
            statsForm = new StatsForm(usage);
            statsForm.FormClosed += (_, _) => statsForm = null;
        }
        statsForm.Show();
        if (statsForm.WindowState == FormWindowState.Minimized) statsForm.WindowState = FormWindowState.Normal;
        statsForm.Activate();
    }

    private void RecordUsage()
    {
        try
        {
            if (connector.CurrentStore() is { } store) usage.Add(DateTime.Now, store);
        }
        catch (Exception error)
        {
            // A missing Wi-Fi adapter or a locked file must not stop the tray app.
            AppLog.Write($"無法記錄連線時間：{error.Message}");
        }
    }

    private void ToggleAutoConnect()
    {
        if (closing) return;
        try
        {
            settings.SetAutoConnect(!settings.AutoConnect);
            if (settings.AutoConnect) QueueAutomatic();
            else
            {
                connector.CancelAutomatic();
                location.SetWatching(false);
                OnStatusChanged(WifiResult.Idle("自動連線已關閉，仍可手動選店。"));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            OnStatusChanged(WifiResult.Failed($"無法更新自動連線設定：{error.Message}"));
        }
    }

    private void ToggleOnlyKnown()
    {
        try { settings.SetOnlyKnown(!settings.OnlyKnown); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Notify($"無法更新設定：{error.Message}");
        }
    }

    private bool SafeLoginEnabled()
    {
        try { return loginItem.Enabled; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private void ToggleLogin()
    {
        try { loginItem.Set(!SafeLoginEnabled()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
                                          or System.Security.SecurityException or InvalidOperationException)
        {
            Notify($"無法設定登入時開啟：{error.Message}");
        }
    }

    private async Task ManagePermissionAsync()
    {
        if (!BeginDialog()) return;
        try { await EnsureLocationAccessAsync(userInitiated: true, showSettings: true); }
        catch (Exception error) { OnStatusChanged(WifiResult.Failed(error.Message)); }
        finally
        {
            manualRunning = false;
            QueueAutomatic();
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

    // MARK: - Auto-connect

    private void QueueAutomatic()
    {
        if (closing || !settings.AutoConnect) return;
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
            ReportRecoveredFiles();
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
        if (args.Mode is PowerModes.Suspend or PowerModes.Resume) AppLog.Write($"power: {args.Mode}");
        if (args.Mode == PowerModes.Resume) QueueAutomatic();
    }

    // MARK: - Status

    /// The 15 s timer repeats the same decisions; log a line only when it differs from the last.
    private void OnTrace(string message) => uiContext.Post(_ =>
    {
        if (message == lastTrace) return;
        lastTrace = message;
        AppLog.Write(message);
    }, null);

    private void OnStatusChanged(WifiResult result)
    {
        void Update()
        {
            if (disposed) return;
            var changed = lastStatus != result;
            lastStatus = result;
            if (status is not null && !status.IsDisposed) status.Text = Escape(result.Message);
            icon.Text = result.Message.Length > 63 ? result.Message[..60] + "..." : result.Message;
            if (!changed) return;
            AppLog.Write(result.Message);
            if (result.Status is WifiStatus.Connected or WifiStatus.Failed) Notify(result.Message);
        }

        uiContext.Post(_ => Update(), null);
    }

    /// Balloon (a toast on Windows 10/11); the same text within 10 s is shown once.
    private void Notify(string message)
    {
        if (disposed) return;
        var now = Environment.TickCount64;
        if (message == lastBalloon && now - lastBalloonAt < 10_000) return;
        lastBalloon = message;
        lastBalloonAt = now;
        icon.ShowBalloonTip(3000, "路易莎 Wi-Fi", message, ToolTipIcon.None);
    }

    private void ReportRecoveredFiles()
    {
        while (JsonFile.Recovered.TryDequeue(out var message))
        {
            AppLog.Write(message);
            Notify(message);
        }
    }

    // MARK: - Updates

    private static string UpdateFolder => Path.Combine(StoragePaths.Root, "update");

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (closing) return;
        try
        {
            var release = await updater.CheckAsync(lifetime.Token);
            if (release is null)
            {
                if (manual) Notify($"目前已是最新版本（{Updater.Display(updater.Current)}）");
                return;
            }
            var version = Updater.Display(release.Version);
            if (manual || available?.Version != release.Version)
                Notify($"有新版本 {version}，請從選單選擇「更新到 {version}」");
            available = release;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            // Offline (e.g. before joining the café Wi-Fi) or rate-limited: try again next time.
            AppLog.Write($"檢查更新失敗：{error.Message}");
            if (manual) Notify("檢查更新失敗，請稍後再試。");
        }
    }

    private async Task InstallUpdateAsync()
    {
        if (available is not { } release || closing) return;
        if (automaticRunning || !BeginDialog())
        {
            Notify("正在連線，請稍後再更新。");
            return;
        }

        var exe = Environment.ProcessPath;
        var folder = exe is null ? null : Path.GetDirectoryName(exe);
        if (!Updater.IsPublished || folder is null || !Updater.CanWrite(folder))
        {
            // Never elevate: e.g. under Program Files, just send the user to the download page.
            manualRunning = false;
            Process.Start(new ProcessStartInfo(release.Page.AbsoluteUri) { UseShellExecute = true })?.Dispose();
            Notify("無法自動更新這個位置的程式，已開啟下載頁。");
            return;
        }

        string staging;
        try
        {
            Notify($"正在下載 {Updater.Display(release.Version)}…");
            var zip = await updater.DownloadAsync(release, UpdateFolder, lifetime.Token);
            staging = Updater.Extract(zip, Path.Combine(UpdateFolder, "staging"), release.Version);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
        catch (Exception error)
        {
            manualRunning = false;
            AppLog.Write($"更新失敗：{error.Message}");
            Notify($"更新失敗：{error.Message}");
            return;
        }

        AppLog.Write($"updating to {Updater.Display(release.Version)}");
        await StopAsync();
        try
        {
            Updater.Swap(folder, staging);
            // Hand the single-instance lock to the new version before starting it.
            instance?.Dispose();
            using var started = Process.Start(new ProcessStartInfo(exe!, "--updated") { UseShellExecute = false })
                ?? throw new InvalidOperationException("無法啟動新版本。");
            if (await Task.Run(() => started.WaitForExit(8000)))
                throw new InvalidOperationException($"新版本啟動後立即結束（代碼 {started.ExitCode}）。");
        }
        catch (Exception error)
        {
            AppLog.Write($"更新失敗，還原原本版本：{error}");
            try
            {
                Updater.Rollback(folder);
                Process.Start(new ProcessStartInfo(exe!, "--update-failed") { UseShellExecute = false })?.Dispose();
            }
            catch (Exception rollback)
            {
                AppLog.Write($"無法還原：{rollback}");
            }
        }
        ExitThread();
    }

    private void CleanupUpdate()
    {
        if (Environment.ProcessPath is { } exe && Path.GetDirectoryName(exe) is { } folder)
            Updater.Cleanup(folder, UpdateFolder);
    }

    // MARK: - Exit

    private async Task ExitAsync()
    {
        if (closing) return;
        await StopAsync();
        ExitThread();
    }

    private async Task StopAsync()
    {
        closing = true;
        timer.Stop();
        usageTimer.Stop();
        updateTimer.Stop();
        location.SetWatching(false);
        lifetime.Cancel();
        await connector.CancelAndWaitAsync();
    }

    protected override void ExitThreadCore()
    {
        closing = true;
        lifetime.Cancel();
        timer.Stop();
        timer.Dispose();
        usageTimer.Stop();
        usageTimer.Dispose();
        updateTimer.Stop();
        updateTimer.Dispose();
        statsForm?.Close();
        location.SetWatching(false);
        location.Changed -= OnLocationChanged;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        connector.StatusChanged -= OnStatusChanged;
        connector.Trace -= OnTrace;
        disposed = true;
        icon.Visible = false;
        icon.Dispose();
        menu.Dispose();
        AppLog.Write("exited");
        base.ExitThreadCore();
    }
}

static class AppIcon
{
    public static Icon Load()
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("AppIcon.ico")!;
        return new Icon(stream, SystemInformation.SmallIconSize);
    }
}
