using System.Text.Json;

namespace LouisaWiFi.Windows.Services;

public sealed record AppSettings(bool AutoConnect = true, bool OnlyKnown = false);

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

    public void SetAutoConnect(bool enabled) => Save(settings with { AutoConnect = enabled });
    public void SetOnlyKnown(bool enabled) => Save(settings with { OnlyKnown = enabled });

    private void Save(AppSettings next)
    {
        AtomicWrite.Write(path, JsonSerializer.Serialize(next, json));
        settings = next;
    }
}
