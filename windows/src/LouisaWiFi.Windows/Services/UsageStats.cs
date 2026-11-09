using System.Globalization;

namespace LouisaWiFi.Windows.Services;

using Usage = IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>;

/// The days a range covers and the Monday-aligned grid that shows them (port of StatsView.swift's Period).
public sealed record StatsPeriod(DateOnly First, DateOnly End, DateOnly Last, DateOnly GridStart, int Weeks)
{
    /// year null = the past year ending today.
    public static StatsPeriod For(int? year, DateOnly today)
    {
        var first = year is { } y ? new DateOnly(y, 1, 1) : today.AddDays(-364);
        var end = year is { } y2 ? new DateOnly(y2, 12, 31) : today;
        var last = end < today ? end : today;
        var gridStart = Monday(first);
        var weeks = (Monday(end).DayNumber - gridStart.DayNumber) / 7 + 1;
        return new StatsPeriod(first, end, last, gridStart, weeks);
    }

    public bool Contains(DateOnly day) => day >= First && day <= Last;
    public bool Shows(DateOnly day) => day >= First && day <= End;

    public IEnumerable<DateOnly> Days()
    {
        for (var d = First; d <= Last; d = d.AddDays(1)) yield return d;
    }

    private static DateOnly Monday(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
}

/// Pure calculations behind the stats window, ported from StatsView.swift.
public static class UsageStats
{
    public static string Key(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// Seconds per day, limited to one store if given.
    public static Dictionary<string, double> PerDay(Usage usage, string? store) =>
        usage.ToDictionary(day => day.Key,
            day => store is null ? day.Value.Values.Sum() : day.Value.GetValueOrDefault(store));

    public static IReadOnlyList<(string Name, double Seconds)> StoreTotals(Usage usage, StatsPeriod period)
    {
        var totals = new Dictionary<string, double>();
        foreach (var day in period.Days())
            if (usage.TryGetValue(Key(day), out var stores))
                foreach (var (name, seconds) in stores) totals[name] = totals.GetValueOrDefault(name) + seconds;
        return totals.Select(t => (t.Key, t.Value)).OrderByDescending(t => t.Value).ToList();
    }

    /// The 12 months ending with the period's last month.
    public static IReadOnlyList<(string Label, double Seconds)> MonthTotals(StatsPeriod period, IReadOnlyDictionary<string, double> perDay)
    {
        var lastMonth = new DateOnly(period.End.Year, period.End.Month, 1);
        return Enumerable.Range(0, 12).Reverse().Select(back =>
        {
            var month = lastMonth.AddMonths(-back);
            var prefix = Key(month)[..7];
            return ($"{month.Month}月", perDay.Where(d => d.Key.StartsWith(prefix, StringComparison.Ordinal)).Sum(d => d.Value));
        }).ToList();
    }

    public static IReadOnlyList<(int Year, double Seconds)> YearTotals(IReadOnlyDictionary<string, double> perDay) =>
        perDay.Where(d => d.Value > 0 && d.Key.Length >= 4 && int.TryParse(d.Key[..4], out _))
            .GroupBy(d => int.Parse(d.Key[..4], CultureInfo.InvariantCulture))
            .Select(g => (g.Key, g.Sum(d => d.Value)))
            .OrderByDescending(y => y.Key)
            .ToList();

    public static int LongestStreak(IEnumerable<double> days)
    {
        int best = 0, run = 0;
        foreach (var seconds in days)
        {
            run = seconds > 0 ? run + 1 : 0;
            best = Math.Max(best, run);
        }
        return best;
    }

    /// Heatmap color step: 0 empty, then <30 min, <1 h, <2 h, more.
    public static int Level(double seconds) => seconds switch
    {
        < 1 => 0,
        < 1800 => 1,
        < 3600 => 2,
        < 7200 => 3,
        _ => 4
    };

    public static string Duration(double seconds)
    {
        var minutes = (int)(seconds / 60);
        return minutes >= 60 ? $"{minutes / 60} 小時 {minutes % 60} 分" : $"{minutes} 分";
    }
}
