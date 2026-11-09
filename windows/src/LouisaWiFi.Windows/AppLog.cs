using System.Globalization;

namespace LouisaWiFi.Windows;

/// %LOCALAPPDATA%\LouisaWiFi\LouisaWiFi.log, like macOS ~/Library/Logs/LouisaWiFi.log.
/// Status text only: never log passwords, BSSIDs, coordinates or profile XML.
public static class AppLog
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object gate = new();

    public static string? PathOverride { get; set; }
    public static string FilePath => PathOverride ?? System.IO.Path.Combine(Services.StoragePaths.Root, "LouisaWiFi.log");

    public static void Write(string message)
    {
        try
        {
            lock (gate)
            {
                var path = FilePath;
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                    File.Move(path, path + ".old", overwrite: true);
                var line = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture)
                           + " " + message.ReplaceLineEndings(" ") + Environment.NewLine;
                File.AppendAllText(path, line);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
