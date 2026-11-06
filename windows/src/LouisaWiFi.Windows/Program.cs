using LouisaWiFi.Windows.UI;

namespace LouisaWiFi.Windows;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
    }
}
