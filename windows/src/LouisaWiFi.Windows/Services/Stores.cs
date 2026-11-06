using System.Text.Json;
using LouisaWiFi.Windows.Models;

namespace LouisaWiFi.Windows.Services;

/// Port of app/Sources/Stores.swift; search follows raycast/louisa-wifi.js.
public static class Stores
{
    private static readonly Lazy<IReadOnlyList<Store>> all = new(() =>
    {
        using var stream = typeof(Stores).Assembly.GetManifestResourceStream("stores.json")
            ?? throw new InvalidOperationException("stores.json is not embedded");
        return Parse(stream);
    });

    public static IReadOnlyList<Store> All => all.Value;

    public static IReadOnlyList<Store> Parse(Stream json) =>
        JsonSerializer.Deserialize<List<Store>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("stores.json is empty");

    public static Store? Named(string name, IEnumerable<Store>? stores = null) =>
        (stores ?? All).FirstOrDefault(s => s.Name == name);

    public static IReadOnlyList<(Store Store, double Meters)> Nearest(double lat, double lng, int limit, IEnumerable<Store>? stores = null) =>
        (stores ?? All).Where(s => s.HasLocation)
            .Select(s => (Store: s, Meters: Distance(lat, lng, s.Lat, s.Lng)))
            .OrderBy(x => x.Meters)
            .Take(limit)
            .ToList();

    private static readonly string[] cityOrder = ["台北市", "新北市", "基隆市", "桃園市", "新竹市", "新竹縣", "苗栗縣", "台中市",
                                                  "彰化縣", "南投縣", "雲林縣", "嘉義市", "嘉義縣", "台南市", "高雄市", "屏東縣",
                                                  "宜蘭縣", "花蓮縣", "台東縣", "澎湖縣", "金門縣", "連江縣"];

    public static IReadOnlyList<(string City, IReadOnlyList<Store> Stores)> ByCity(IEnumerable<Store>? stores = null)
    {
        int Rank(string city) => Array.IndexOf(cityOrder, city) is var i and >= 0 ? i : cityOrder.Length;
        return (stores ?? All).GroupBy(s => s.City)
            .OrderBy(g => Rank(g.Key)).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (City: g.Key, Stores: (IReadOnlyList<Store>)g.OrderBy(s => s.Name, StringComparer.Ordinal).ToList()))
            .ToList();
    }

    /// Exact name (with or without 門市) wins; otherwise every word must appear in name or address.
    public static IReadOnlyList<Store> Search(string keyword, IEnumerable<Store>? stores = null)
    {
        var list = (stores ?? All).ToList();
        keyword = keyword.Trim();
        if (keyword.Length == 0) return [];
        var exact = list.Where(s => s.Name == keyword || s.Name == keyword + "門市").ToList();
        if (exact.Count > 0) return exact;
        var words = keyword.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return list.Where(s => words.All(w => $"{s.Name} {s.Address}".Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    /// Great-circle distance in meters (haversine, mean Earth radius).
    public static double Distance(double lat1, double lng1, double lat2, double lng2)
    {
        const double r = 6371008.8;
        static double Rad(double d) => d * Math.PI / 180;
        var a = Math.Pow(Math.Sin(Rad(lat2 - lat1) / 2), 2)
              + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Pow(Math.Sin(Rad(lng2 - lng1) / 2), 2);
        return 2 * r * Math.Asin(Math.Sqrt(a));
    }
}
