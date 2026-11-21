using System.Globalization;
using System.Text.Json;

namespace LouisaWiFi.Windows.Services;

/// Port of app/Sources/Tracker.swift. usage.json keeps the macOS format:
/// "yyyy-MM-dd" (local date) → store name → seconds.
public sealed class UsageTracker
{
    public const string UnknownStore = "未知門市";
    private static readonly JsonSerializerOptions json = new();
    private readonly string path;
    private readonly object gate = new();
    private readonly Dictionary<string, Dictionary<string, double>> usage;

    public UsageTracker(string? path = null)
    {
        this.path = path ?? Path.Combine(StoragePaths.Root, "usage.json");
        usage = JsonFile.Load<Dictionary<string, Dictionary<string, double>>>(this.path, json) ?? [];
    }

    public event Action? Changed;

    public static string DayKey(DateTime local) => local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> Snapshot()
    {
        lock (gate)
            return usage.ToDictionary(
                day => day.Key,
                day => (IReadOnlyDictionary<string, double>)new Dictionary<string, double>(day.Value));
    }

    public void Add(DateTime local, string? store, double seconds = 60)
    {
        lock (gate)
        {
            var day = DayKey(local);
            if (!usage.TryGetValue(day, out var stores)) usage[day] = stores = [];
            var name = string.IsNullOrEmpty(store) ? UnknownStore : store;
            stores[name] = stores.GetValueOrDefault(name) + seconds;
            AtomicWrite.Write(path, JsonSerializer.Serialize(usage, json));
        }
        Changed?.Invoke();
    }
}
