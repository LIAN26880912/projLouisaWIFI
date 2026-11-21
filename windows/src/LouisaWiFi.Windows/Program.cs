using LouisaWiFi.Windows.UI;
using LouisaWiFi.Windows.Platform;
using LouisaWiFi.Windows.Services;

namespace LouisaWiFi.Windows;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        using var instance = SingleInstance.TryAcquire();
        if (instance is null)
        {
            // Already running: hand over the command (or just "show") and quit.
            if (!SingleInstance.Send(args.Length == 0 ? ["--show"] : args))
                MessageBox.Show("路易莎 Wi-Fi 已在執行，但無法傳送指令。", "路易莎 Wi-Fi", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => AppLog.Write($"未預期的錯誤：{e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Write($"程式終止：{e.ExceptionObject}");
        try
        {
            using var location = new LocationService();
            var wifi = new NativeWifiAdapter();
            var connector = new Connector(wifi, new LearnedStore(), new SecretStore());
            Application.Run(new TrayApp(connector, wifi, location, new SettingsStore(), new UsageTracker(),
                new LoginItem(), new Updater(), instance, args));
        }
        catch (Exception error)
        {
            AppLog.Write($"無法啟動：{error}");
            MessageBox.Show(error.Message, "路易莎 Wi-Fi", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
