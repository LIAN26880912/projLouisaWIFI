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
        entries = new Dictionary<string, LearnedEntry>(
            JsonFile.Load<Dictionary<string, LearnedEntry>>(this.path, json) ?? [],
            StringComparer.OrdinalIgnoreCase);
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

/// A damaged data file must not stop the app: keep a copy as *.bad, start from defaults and report it.
internal static class JsonFile
{
    public static readonly System.Collections.Concurrent.ConcurrentQueue<string> Recovered = new();

    public static T? Load<T>(string path, JsonSerializerOptions options) where T : class
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), options)
                ?? throw new JsonException("empty document");
        }
        catch (JsonException)
        {
            Quarantine(path);
            return null;
        }
    }

    public static void Quarantine(string path)
    {
        File.Move(path, path + ".bad", overwrite: true);
        Recovered.Enqueue($"{Path.GetFileName(path)} 已損毀，已備份為 {Path.GetFileName(path)}.bad 並改用預設值。");
    }
}
