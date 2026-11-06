using System.Security;
using System.Text;

namespace LouisaWiFi.Windows.Platform;

public static class ProfileXml
{
    public static string Build(string ssid, string password, bool autoConnect)
    {
        if (string.IsNullOrWhiteSpace(ssid)) throw new ArgumentException("SSID is required.", nameof(ssid));
        if (password.Length is < 8 or > 63)
            throw new ArgumentException("A WPA2 passphrase must be between 8 and 63 characters.", nameof(password));

        var escapedSsid = SecurityElement.Escape(ssid);
        var escapedPassword = SecurityElement.Escape(password);
        var hexSsid = Convert.ToHexString(Encoding.UTF8.GetBytes(ssid));
        var mode = autoConnect ? "auto" : "manual";

        return $"""
            <?xml version="1.0"?>
            <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
              <name>{escapedSsid}</name>
              <SSIDConfig><SSID><hex>{hexSsid}</hex><name>{escapedSsid}</name></SSID></SSIDConfig>
              <connectionType>ESS</connectionType>
              <connectionMode>{mode}</connectionMode>
              <autoSwitch>false</autoSwitch>
              <MSM><security>
                <authEncryption><authentication>WPA2PSK</authentication><encryption>AES</encryption><useOneX>false</useOneX></authEncryption>
                <sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>{escapedPassword}</keyMaterial></sharedKey>
              </security></MSM>
            </WLANProfile>
            """;
    }
}
