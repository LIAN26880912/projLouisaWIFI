using System.Text.Json;

namespace LouisaWiFi.Windows.Services;

public sealed record AppSettings(bool AutoConnect = true);

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string path;
    private AppSettings settings;

    public SettingsStore(string? path = null)
    {
        this.path = path ?? Path.Combine(StoragePaths.Root, "settings.json");
        settings = File.Exists(this.path)
            ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(this.path), json)
                ?? throw new InvalidDataException("settings.json is empty.")
            : new AppSettings();
    }

    public bool AutoConnect => settings.AutoConnect;

    public void SetAutoConnect(bool enabled)
    {
        var next = settings with { AutoConnect = enabled };
        AtomicWrite.Write(path, JsonSerializer.Serialize(next, json));
        settings = next;
    }
}
