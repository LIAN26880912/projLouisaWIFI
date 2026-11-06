using System.Text.Json;
using LouisaWiFi.Windows.Models;

namespace LouisaWiFi.Windows.Services;

public sealed record LearnedEntry(string StoreName, int? PasswordIndex);

public interface ILearnedStore
{
    IReadOnlyDictionary<string, LearnedEntry> All { get; }
    void Remember(IEnumerable<string> bssids, string storeName, int? passwordIndex);
}

public sealed class LearnedStore : ILearnedStore
{
    private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string path;
    private readonly object gate = new();
    private Dictionary<string, LearnedEntry> entries;

    public LearnedStore(string? path = null)
    {
        this.path = path ?? Path.Combine(StoragePaths.Root, "learned.json");
        entries = File.Exists(this.path)
            ? JsonSerializer.Deserialize<Dictionary<string, LearnedEntry>>(File.ReadAllText(this.path), json)
                ?? throw new InvalidDataException("learned.json is empty.")
            : new Dictionary<string, LearnedEntry>(StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyDictionary<string, LearnedEntry> All
    {
        get { lock (gate) return new Dictionary<string, LearnedEntry>(entries, StringComparer.OrdinalIgnoreCase); }
    }

    public void Remember(IEnumerable<string> bssids, string storeName, int? passwordIndex)
    {
        lock (gate)
        {
            foreach (var bssid in bssids)
                entries[Normalize(bssid)] = new LearnedEntry(storeName, passwordIndex);
            AtomicWrite.Write(path, JsonSerializer.Serialize(entries, json));
        }
    }

    public static string Normalize(string bssid) =>
        string.Join(':', bssid.Split(':').Select(part => part.PadLeft(2, '0').ToLowerInvariant()));
}

public static class StoragePaths
{
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LouisaWiFi");
}

internal static class AtomicWrite
{
    public static void Write(string path, byte[] contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, contents);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public static void Write(string path, string contents) =>
        Write(path, System.Text.Encoding.UTF8.GetBytes(contents));
}
