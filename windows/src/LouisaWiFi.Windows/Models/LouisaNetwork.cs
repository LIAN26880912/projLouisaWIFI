using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace LouisaWiFi.Windows.Models;

/// Which SSIDs count as Louisa store Wi-Fi. Self-contained so app/Sources/Joiner.swift
/// (exact "LouisaCoffee" today) can adopt the same rule upstream.
public static partial class LouisaNetwork
{
    /// Profile name used when the SSID can't be read (no location permission).
    public const string DefaultSsid = "LouisaCoffee";

    /// "LouisaCoffee", digit-suffixed stores such as "LouisaCoffee879", or "Louisa Coffee".
    /// Case-sensitive on purpose: e.g. "louisacoffee" has been seen as an unrelated WPA3 network.
    [GeneratedRegex(@"\A(?:LouisaCoffee[0-9]*|Louisa Coffee)\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static bool IsLouisa([NotNullWhen(true)] string? ssid) => ssid is not null && Pattern().IsMatch(ssid);
}
