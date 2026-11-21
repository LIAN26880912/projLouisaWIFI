using LouisaWiFi.Windows.Platform;
using System.Xml.Linq;

namespace LouisaWiFi.Windows.Tests;

public class ProfileXmlTests
{
    [Fact]
    public void ProfileEscapesXmlAndUsesUtf8SsidHex()
    {
        var xml = ProfileXml.Build("Louisa & Coffee", "pass<&>123", autoConnect: true);
        var document = System.Xml.Linq.XDocument.Parse(xml);
        XNamespace ns = "http://www.microsoft.com/networking/WLAN/profile/v1";

        Assert.Equal("4C6F75697361202620436F66666565", (string?)document.Root!.Element(ns + "SSIDConfig")!
            .Element(ns + "SSID")!.Element(ns + "hex"));
        Assert.Equal("pass<&>123", (string?)document.Descendants(ns + "keyMaterial").Single());
        Assert.Equal("auto", (string?)document.Root.Element(ns + "connectionMode"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("1234567890123456789012345678901234567890123456789012345678901234")]
    public void RejectsInvalidWpaPassphraseLengths(string password) =>
        Assert.Throws<ArgumentException>(() => ProfileXml.Build("LouisaCoffee", password, autoConnect: false));
}
