using LouisaWiFi.Windows.Services;

namespace LouisaWiFi.Windows.UI;

/// WinForms port of app/Sources/StatsView.swift: totals, a GitHub-style day grid and a breakdown list.
sealed class StatsForm : Form
{
    private const string PastYear = "過去一年";
    private readonly UsageTracker tracker;
    private readonly Label heading = new() { AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 12, FontStyle.Bold) };
    private readonly Button showAll = new() { Text = "顯示全部門市", AutoSize = true, Visible = false };
    private readonly Label tiles = new() { AutoSize = true, Margin = new Padding(3, 8, 3, 8) };
    private readonly HeatmapPanel grid = new();
    private readonly Label caption = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly RadioButton byStore = new() { Text = "門市", Checked = true, AutoSize = true };
    private readonly RadioButton byMonth = new() { Text = "月份", AutoSize = true };
    private readonly RadioButton byYear = new() { Text = "年份", AutoSize = true };
    private readonly ListView list = new()
    {
        View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, OwnerDraw = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable, Activation = ItemActivation.OneClick, Size = new Size(840, 220)
    };
    private readonly ListBox ranges = new() { Width = 110, IntegralHeight = false, Dock = DockStyle.Fill };
    private int? year;
    private string? store;
    private DateOnly? selected;
    private bool refreshing;

    public StatsForm(UsageTracker tracker)
    {
        this.tracker = tracker;
        Text = "連線統計";
        Icon = AppIcon.Load();
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        list.Columns.Add("名稱", 220);
        list.Columns.Add("", 470);
        list.Columns.Add("連線時間", 130, HorizontalAlignment.Right);
        list.DrawColumnHeader += (_, e) => e.DrawDefault = true;
        list.DrawSubItem += (_, e) =>
        {
            if (e.ColumnIndex != 1 || e.SubItem?.Tag is not double ratio) { e.DrawDefault = true; return; }
            e.DrawBackground();
            if (e.Item!.Selected) e.Graphics.FillRectangle(SystemBrushes.Highlight, e.Bounds);
            var height = Math.Max(4, e.Bounds.Height / 2);
            var width = Math.Max(2, (int)((e.Bounds.Width - 8) * ratio));
            using var bar = new SolidBrush(Color.FromArgb(0x2A, 0x78, 0xD6));
            e.Graphics.FillRectangle(bar, e.Bounds.X + 4, e.Bounds.Y + (e.Bounds.Height - height) / 2, width, height);
        };

        var title = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        title.Controls.AddRange([heading, showAll]);
        var modes = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(3, 12, 3, 3) };
        modes.Controls.AddRange([byStore, byMonth, byYear]);
        var left = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        left.Controls.AddRange([title, tiles, grid, caption, modes, list]);

        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, RowCount = 1 };
        layout.Controls.Add(left, 0, 0);
        layout.Controls.Add(ranges, 1, 0);
        Controls.Add(layout);

        showAll.Click += (_, _) => { store = null; RefreshView(); };
        foreach (var mode in new[] { byStore, byMonth, byYear })
            mode.CheckedChanged += (_, _) => { if (mode.Checked) RefreshView(); };
        list.ItemActivate += (_, _) => OnListActivated();
        list.Click += (_, _) => OnListActivated();
        ranges.SelectedIndexChanged += (_, _) =>
        {
            if (refreshing || ranges.SelectedItem is not string range) return;
            year = range == PastYear ? null : int.Parse(range);
            selected = null;
            RefreshView();
        };
        grid.DayClicked += day => { selected = day; RefreshView(); };
        tracker.Changed += OnUsageChanged;
        RefreshView();
    }

    private void OnUsageChanged()
    {
        if (IsHandleCreated && !IsDisposed) BeginInvoke(RefreshView);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        tracker.Changed -= OnUsageChanged;
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Icon?.Dispose();
        base.Dispose(disposing);
    }

    private void OnListActivated()
    {
        if (list.SelectedItems.Count == 0 || list.SelectedItems[0].Tag is not { } tag) return;
        if (tag is string name) store = store == name ? null : name;
        else if (tag is int y) { year = y; selected = null; }
        RefreshView();
    }

    private void RefreshView()
    {
        refreshing = true;
        try
        {
            var usage = tracker.Snapshot();
            var today = DateOnly.FromDateTime(DateTime.Now);
            var period = StatsPeriod.For(year, today);
            var perDay = UsageStats.PerDay(usage, store);
            var days = period.Days().Select(d => perDay.GetValueOrDefault(UsageStats.Key(d))).ToList();
            var total = days.Sum();
            var visits = days.Count(s => s > 0);
            var stores = UsageStats.StoreTotals(usage, period);
            var rangeName = year is { } y ? $"{y} 年" : PastYear;

            heading.Text = $"{(store is null ? "" : store + "：")}{rangeName}累計連線 {UsageStats.Duration(total)}";
            showAll.Visible = store is not null;
            tiles.Text = $"到訪天數 {visits} 天　｜　到訪門市 {stores.Count(s => s.Name != UsageTracker.UnknownStore)} 家　｜　" +
                         $"最長連續 {UsageStats.LongestStreak(days)} 天　｜　平均每天 {(visits > 0 ? UsageStats.Duration(total / visits) : "—")}";
            grid.Show(period, perDay, selected);
            caption.Text = Caption(usage);

            list.BeginUpdate();
            list.Items.Clear();
            if (byStore.Checked)
            {
                var peak = stores.Count == 0 ? 0 : stores.Max(s => s.Seconds);
                foreach (var (name, seconds) in stores)
                    list.Items.Add(Row(name, seconds, peak, name, name == store));
                if (stores.Count == 0) list.Items.Add("這段期間沒有紀錄。連上路易莎 Wi-Fi 後會自動計時。");
            }
            else if (byMonth.Checked)
            {
                var months = UsageStats.MonthTotals(period, perDay);
                var peak = months.Count == 0 ? 0 : months.Max(m => m.Seconds);
                foreach (var (label, seconds) in months)
                    list.Items.Add(Row(label, seconds, peak, null, false));
            }
            else
            {
                var years = UsageStats.YearTotals(perDay);
                var peak = years.Count == 0 ? 0 : years.Max(y2 => y2.Seconds);
                foreach (var (y2, seconds) in years)
                    list.Items.Add(Row($"{y2} 年", seconds, peak, y2, y2 == year));
                if (years.Count == 0) list.Items.Add("還沒有紀錄。");
            }
            list.EndUpdate();

            var rangeItems = usage.Keys.Where(k => k.Length >= 4 && int.TryParse(k[..4], out _))
                .Select(k => int.Parse(k[..4])).Append(today.Year).Distinct().OrderDescending()
                .Select(v => v.ToString()).Prepend(PastYear).ToArray();
            if (!ranges.Items.Cast<string>().SequenceEqual(rangeItems))
            {
                ranges.Items.Clear();
                ranges.Items.AddRange(rangeItems);
            }
            ranges.SelectedItem = year?.ToString() ?? PastYear;
        }
        finally
        {
            refreshing = false;
        }
    }

    private static ListViewItem Row(string label, double seconds, double peak, object? tag, bool selected)
    {
        var item = new ListViewItem(label) { Tag = tag, Selected = selected };
        item.SubItems.Add(new ListViewItem.ListViewSubItem(item, "") { Tag = peak > 0 ? seconds / peak : 0d });
        item.SubItems.Add(UsageStats.Duration(seconds));
        return item;
    }

    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Close();
            return true;
        }
        return base.ProcessDialogKey(keyData);
    }

    private string Caption(IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> usage)
    {
        if (selected is not { } day) return "點選格子，查看當天的門市與連線時間";
        var weekday = "日一二三四五六"[(int)day.DayOfWeek];
        var stores = usage.GetValueOrDefault(UsageStats.Key(day))?.Where(s => s.Value > 0).OrderByDescending(s => s.Value).ToList() ?? [];
        var detail = stores.Count == 0 ? "無紀錄" : string.Join("・", stores.Select(s => $"{s.Key} {UsageStats.Duration(s.Value)}"));
        return $"{day.Month}月{day.Day}日（星期{weekday}）　{detail}";
    }

    private sealed class HeatmapPanel : Control
    {
        private const int Cell = 13, Gap = 3, LabelWidth = 22, HeaderHeight = 16, LegendHeight = 20;
        private static readonly Color[] palette =
            [Color.FromArgb(0xF0, 0xEF, 0xEC), Color.FromArgb(0xB7, 0xD3, 0xF6), Color.FromArgb(0x6D, 0xA7, 0xEC),
             Color.FromArgb(0x2A, 0x78, 0xD6), Color.FromArgb(0x10, 0x42, 0x81)];
        // Like macOS: days shown outside the period are the empty colour at half opacity.
        private static readonly Color outside = Color.FromArgb(0x80, palette[0]);
        private StatsPeriod? period;
        private IReadOnlyDictionary<string, double> perDay = new Dictionary<string, double>();
        private DateOnly? selected;

        public HeatmapPanel()
        {
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
            // The macOS empty colour is nearly the WinForms window grey, so draw on white like the macOS card.
            BackColor = Color.White;
            Size = SizeFor(53);
        }

        private static Size SizeFor(int weeks) =>
            new(LabelWidth + weeks * (Cell + Gap), HeaderHeight + 7 * (Cell + Gap) + LegendHeight);

        public event Action<DateOnly>? DayClicked;

        public void Show(StatsPeriod period, IReadOnlyDictionary<string, double> perDay, DateOnly? selected)
        {
            this.period = period;
            this.perDay = perDay;
            this.selected = selected;
            Size = SizeFor(period.Weeks);
            Invalidate();
        }

        private DateOnly DayAt(int week, int row) => period!.GridStart.AddDays(week * 7 + row);

        protected override void OnPaint(PaintEventArgs e)
        {
            if (period is null) return;
            var g = e.Graphics;
            using var text = new SolidBrush(SystemColors.GrayText);
            using var small = new Font(Font.FontFamily, 7.5f);
            string[] rows = ["一", "", "三", "", "五", "", ""];
            for (var r = 0; r < 7; r++) g.DrawString(rows[r], small, text, 0, HeaderHeight + r * (Cell + Gap) - 2);
            var lastMonth = -1;
            for (var w = 0; w < period.Weeks; w++)
            {
                var monday = DayAt(w, 0);
                var firstShown = Enumerable.Range(0, 7).Select(r => DayAt(w, r)).FirstOrDefault(period.Shows);
                if (period.Shows(firstShown) && firstShown.Month != lastMonth && firstShown.Day <= 7)
                {
                    g.DrawString($"{firstShown.Month}月", small, text, LabelWidth + w * (Cell + Gap), 0);
                    lastMonth = firstShown.Month;
                }
                for (var r = 0; r < 7; r++)
                {
                    var day = monday.AddDays(r);
                    if (!period.Shows(day)) continue;
                    var rect = new Rectangle(LabelWidth + w * (Cell + Gap), HeaderHeight + r * (Cell + Gap), Cell, Cell);
                    using var fill = new SolidBrush(period.Contains(day)
                        ? palette[UsageStats.Level(perDay.GetValueOrDefault(UsageStats.Key(day)))]
                        : outside);
                    g.FillRectangle(fill, rect);
                    if (day == selected) g.DrawRectangle(SystemPens.Highlight, rect.X - 1, rect.Y - 1, Cell + 1, Cell + 1);
                }
            }

            // 少 ▪▪▪▪▪ 多, right-aligned under the grid.
            var y = HeaderHeight + 7 * (Cell + Gap) + 4;
            var more = g.MeasureString("多", small);
            var x = Width - (int)more.Width;
            g.DrawString("多", small, text, x, y - 2);
            for (var level = palette.Length - 1; level >= 0; level--)
            {
                x -= 11 + Gap;
                using var fill = new SolidBrush(palette[level]);
                g.FillRectangle(fill, x, y, 11, 11);
            }
            var less = g.MeasureString("少", small);
            g.DrawString("少", small, text, x - less.Width - Gap, y - 2);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (period is null || e.X < LabelWidth || e.Y < HeaderHeight) return;
            var week = (e.X - LabelWidth) / (Cell + Gap);
            var row = (e.Y - HeaderHeight) / (Cell + Gap);
            if (week >= period.Weeks || row >= 7) return;
            var day = DayAt(week, row);
            if (period.Shows(day)) DayClicked?.Invoke(day);
        }
    }
}
