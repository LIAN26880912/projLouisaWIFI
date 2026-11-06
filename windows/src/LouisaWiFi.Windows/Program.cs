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
            var connector = new Connector(new NativeWifiAdapter(), new LearnedStore(), new SecretStore());
            Application.Run(new TrayApp(connector));
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "Louisa Wi-Fi", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
