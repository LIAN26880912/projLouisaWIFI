using System.Text.Json;

namespace LouisaWiFi.Windows.Services;

/// UseLocation off (default): auto-connect skips Windows location and tries recently visited stores instead.
public sealed record AppSettings(bool AutoConnect = true, bool OnlyKnown = false, bool UseLocation = false);

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string path;
    private AppSettings settings;

    public SettingsStore(string? path = null)
    {
        this.path = path ?? Path.Combine(StoragePaths.Root, "settings.json");
        settings = JsonFile.Load<AppSettings>(this.path, json) ?? new AppSettings();
    }

    public bool AutoConnect => settings.AutoConnect;
    public bool OnlyKnown => settings.OnlyKnown;
    public bool UseLocation => settings.UseLocation;

    public void SetAutoConnect(bool enabled) => Save(settings with { AutoConnect = enabled });
    public void SetOnlyKnown(bool enabled) => Save(settings with { OnlyKnown = enabled });
    public void SetUseLocation(bool enabled) => Save(settings with { UseLocation = enabled });

    private void Save(AppSettings next)
    {
        AtomicWrite.Write(path, JsonSerializer.Serialize(next, json));
        settings = next;
    }
}
