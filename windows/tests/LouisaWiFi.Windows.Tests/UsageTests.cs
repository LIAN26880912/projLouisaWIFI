using LouisaWiFi.Windows.Services;

namespace LouisaWiFi.Windows.Tests;

public class UsageTests
{
    [Fact]
    public void TrackerUsesTheMacFormatAndAccumulatesPerDayAndStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "usage.json");
        try
        {
            var tracker = new UsageTracker(path);
            var changed = 0;
            tracker.Changed += () => changed++;
            tracker.Add(new DateTime(2026, 11, 9, 23, 59, 0), "金城延和門市");
            tracker.Add(new DateTime(2026, 11, 9, 10, 0, 0), "金城延和門市");
            tracker.Add(new DateTime(2026, 11, 10, 0, 1, 0), "");

            var json = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, double>>>(File.ReadAllText(path))!;
            Assert.Equal(120, json["2026-11-09"]["金城延和門市"]);
            Assert.Equal(60, json["2026-11-10"][UsageTracker.UnknownStore]);
            Assert.Equal(3, changed);
            Assert.Equal(120, new UsageTracker(path).Snapshot()["2026-11-09"]["金城延和門市"]);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TrackerReadsAFileWrittenByTheMacApp()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "usage.json");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, """{"2025-01-02":{"延平門市":3600,"未知門市":60}}""");
            Assert.Equal(3600, new UsageTracker(path).Snapshot()["2025-01-02"]["延平門市"]);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void PastYearPeriodEndsTodayAndAlignsToMonday()
    {
        var today = new DateOnly(2026, 11, 9); // Monday
        var period = StatsPeriod.For(null, today);
        Assert.Equal(today.AddDays(-364), period.First);
        Assert.Equal(today, period.Last);
        Assert.Equal(DayOfWeek.Monday, period.GridStart.DayOfWeek);
        Assert.True(period.GridStart <= period.First);
        Assert.Equal(365, period.Days().Count());
        Assert.Equal(53, period.Weeks);
    }

    [Fact]
    public void YearPeriodStopsCountingAtToday()
    {
        var period = StatsPeriod.For(2026, new DateOnly(2026, 3, 1));
        Assert.Equal(new DateOnly(2026, 12, 31), period.End);
        Assert.Equal(new DateOnly(2026, 3, 1), period.Last);
        Assert.True(period.Shows(new DateOnly(2026, 12, 31)));
        Assert.False(period.Contains(new DateOnly(2026, 3, 2)));
        Assert.Equal(new DateOnly(2025, 12, 29), period.GridStart);
    }

    [Fact]
    public void ArrowKeysStartAtTheLastDayAndStayInsideThePeriod()
    {
        var period = StatsPeriod.For(2026, new DateOnly(2026, 3, 1));
        Assert.Equal(period.Last, period.Step(null, 7));
        Assert.Equal(new DateOnly(2026, 2, 28), period.Step(period.Last, -1));
        Assert.Equal(period.Last, period.Step(period.Last, 1));                  // never into the future
        Assert.Equal(new DateOnly(2026, 1, 3), period.Step(new DateOnly(2026, 1, 3), -7)); // never before January
        Assert.Equal("3月1日（星期日）", UsageStats.DayLabel(period.Last));
    }

    [Fact]
    public void StoresByRecencyPutsLatestVisitFirstAndSkipsUnknownAndBadDays()
    {
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> usage = new Dictionary<string, IReadOnlyDictionary<string, double>>
        {
            ["2026-01-01"] = new Dictionary<string, double> { ["甲"] = 9000, ["乙"] = 60 },
            ["2026-01-03"] = new Dictionary<string, double> { ["丙"] = 60, ["丁"] = 600, [UsageTracker.UnknownStore] = 900, ["戊"] = 0 },
            ["bad-key"] = new Dictionary<string, double> { ["己"] = 60 }
        };

        Assert.Equal(["丁", "丙", "甲", "乙"], UsageStats.StoresByRecency(usage));
    }

    [Fact]
    public void TotalsStreakAndFormattingMatchTheMacApp()
    {
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> usage = new Dictionary<string, IReadOnlyDictionary<string, double>>
        {
            ["2026-01-01"] = new Dictionary<string, double> { ["甲"] = 3600, ["乙"] = 60 },
            ["2026-01-02"] = new Dictionary<string, double> { ["甲"] = 120 },
            ["2025-12-31"] = new Dictionary<string, double> { ["乙"] = 7200 },
            ["bad-key"] = new Dictionary<string, double> { ["乙"] = 60 }
        };
        var period = StatsPeriod.For(2026, new DateOnly(2026, 1, 5));

        Assert.Equal([("甲", 3720.0), ("乙", 60.0)], UsageStats.StoreTotals(usage, period));
        var perDay = UsageStats.PerDay(usage, null);
        Assert.Equal(3660, perDay["2026-01-01"]);
        Assert.Equal(0, UsageStats.PerDay(usage, "乙")["2026-01-02"]);
        Assert.Equal([(2026, 3780.0), (2025, 7200.0)], UsageStats.YearTotals(perDay));

        var months = UsageStats.MonthTotals(period, perDay);
        Assert.Equal(12, months.Count);
        Assert.Equal(("1月", 3780.0), months[0]);
        Assert.Equal(("12月", 0.0), months[^1]);

        Assert.Equal(3, UsageStats.LongestStreak([1, 1, 0, 1, 1, 1, 0]));
        Assert.Equal("59 分", UsageStats.Duration(3599));
        Assert.Equal("1 小時 1 分", UsageStats.Duration(3660));
        Assert.Equal([0, 1, 2, 3, 4], new double[] { 0, 1799, 1800, 7199, 7200 }.Select(UsageStats.Level));
    }
}
