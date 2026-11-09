using LouisaWiFi.Windows.UI;
using LouisaWiFi.Windows.Platform;
using LouisaWiFi.Windows.Services;

namespace LouisaWiFi.Windows;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        try
        {
            using var location = new LocationService();
            var wifi = new NativeWifiAdapter();
            var connector = new Connector(wifi, new LearnedStore(), new SecretStore());
            Application.Run(new TrayApp(connector, wifi, location, new SettingsStore()));
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "Louisa Wi-Fi", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
