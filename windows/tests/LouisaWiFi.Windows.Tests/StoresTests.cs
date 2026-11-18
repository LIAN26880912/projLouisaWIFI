using LouisaWiFi.Windows.Models;
using LouisaWiFi.Windows.Services;

namespace LouisaWiFi.Windows.Tests;

public class StoresTests
{
    private static Store S(string name, string address, double lat = 0, double lng = 0) =>
        new(name, address, "02-0000-0000", lat, lng, ["00000000"]);

    [Fact]
    public void EmbeddedDataLoads()
    {
        Assert.True(Stores.All.Count > 400);
        Assert.All(Stores.All, s => Assert.NotEmpty(s.Passwords));
        Assert.Equal(Stores.All.Count, Stores.All.Select(s => s.Name).Distinct().Count());
    }

    [Fact]
    public void KnownStoreHasExpectedFields()
    {
        // Verified on site: the first candidate joined the real network.
        var s = Stores.Named("建成圓環門市")!;
        Assert.Equal("25523613", s.Passwords[0]);
        Assert.Equal("台北市", s.City);
        Assert.True(s.HasLocation);
    }

    [Fact]
    public void CityNormalizesAndFallsBack()
    {
        Assert.Equal("台中市", S("a", "臺中市 西區 路1號").City);
        Assert.Equal("其他", S("a", "").City);
    }

    [Fact]
    public void NearestSkipsStoresWithoutCoordinates()
    {
        var stores = new[] { S("遠", "x", 25.1, 121.6), S("無座標", "x"), S("近", "x", 25.0537, 121.5150) };
        var near = Stores.Nearest(25.05378, 121.51508, 3, stores);
        Assert.Equal(["近", "遠"], near.Select(n => n.Store.Name));
        Assert.True(near[0].Meters < 20);
    }

    [Fact]
    public void NearestFindsTheStoreWeMeasuredOnSite()
    {
        // Windows Wi-Fi location fix taken inside 建成圓環門市.
        var near = Stores.Nearest(25.05378, 121.51508, 1);
        Assert.Equal("建成圓環門市", near[0].Store.Name);
    }

    [Fact]
    public void DistanceMatchesKnownValue()
    {
        // One degree of latitude is about 111.2 km.
        Assert.InRange(Stores.Distance(25, 121, 26, 121), 111_000, 111_400);
        Assert.Equal(0, Stores.Distance(25, 121, 25, 121), 6);
    }

    [Fact]
    public void ByCityFollowsTaiwanOrderThenName()
    {
        var stores = new[] { S("b", "臺北市 x"), S("a", "台北市 x"), S("c", "高雄市 x"), S("d", "新北市 x"), S("e", "火星 x") };
        var groups = Stores.ByCity(stores);
        Assert.Equal(["台北市", "新北市", "高雄市", "火星"], groups.Select(g => g.City));
        Assert.Equal(["a", "b"], groups[0].Stores.Select(s => s.Name));
    }

    [Fact]
    public void SearchPrefersExactNameWithOrWithoutSuffix()
    {
        var stores = new[] { S("延平門市", "台北市 延平北路"), S("延平北路門市", "台北市 延平北路") };
        Assert.Equal(["延平門市"], Stores.Search("延平", stores).Select(s => s.Name));
        Assert.Equal(["延平門市"], Stores.Search("延平門市", stores).Select(s => s.Name));
    }

    [Fact]
    public void SearchRequiresEveryWordInNameOrAddress()
    {
        var stores = new[] { S("大安門市", "台北市 大安區 復興南路"), S("復興門市", "桃園市 復興區"), S("Abc門市", "x") };
        Assert.Equal(["大安門市"], Stores.Search("大安 復興", stores).Select(s => s.Name));
        Assert.Equal(["Abc門市"], Stores.Search("abc", stores).Select(s => s.Name));
        Assert.Empty(Stores.Search("   ", stores));
        Assert.Empty(Stores.Search("不存在", stores));
    }

    [Fact]
    public void SuggestionsListNearestFiveThenLearnedStoresByDistance()
    {
        var stores = Enumerable.Range(1, 7).Select(i => S($"近{i}", "x", 25 + i * 0.001, 121))
            .Append(S("遠", "x", 26, 121)).Append(S("更遠", "x", 27, 121)).Append(S("無座標", "x"))
            .ToList();
        var known = new HashSet<string> { "更遠", "無座標", "遠", "近2", "近7" };

        Assert.Equal(["近1", "近2", "近3", "近4", "近5", "近7", "遠", "更遠", "無座標"],
            Stores.Suggestions(25, 121, known, stores).Select(s => s.Name));
        Assert.Equal(known.Order(StringComparer.Ordinal),
            Stores.Suggestions(null, null, known, stores).Select(s => s.Name));
    }
}
