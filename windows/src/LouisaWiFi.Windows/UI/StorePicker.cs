using LouisaWiFi.Windows.Models;
using LouisaWiFi.Windows.Services;

namespace LouisaWiFi.Windows.UI;

sealed class StorePicker : Form
{
    private readonly TextBox search = new() { Dock = DockStyle.Top, PlaceholderText = "輸入門市名稱或地址" };
    private readonly ListBox results = new() { Dock = DockStyle.Fill, IntegralHeight = false, FormattingEnabled = true };
    private readonly Button connect = new() { Text = "自動嘗試", AutoSize = true };
    private readonly Button manual = new() { Text = "輸入密碼…", AutoSize = true };
    private readonly List<Store> stores = Stores.All.ToList();

    private StorePicker()
    {
        Text = "選擇路易莎門市";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(480, 420);
        Size = new Size(560, 540);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(manual);
        buttons.Controls.Add(connect);
        Controls.Add(results);
        Controls.Add(buttons);
        Controls.Add(search);

        search.TextChanged += (_, _) => RefreshResults();
        results.Format += (_, e) =>
        {
            if (e.ListItem is Store store) e.Value = $"{store.Name}　{store.Address}";
        };
        results.DoubleClick += (_, _) => { if (SelectedStore is not null) DialogResult = DialogResult.OK; };
        results.SelectedIndexChanged += (_, _) => UpdateButtons();
        connect.Click += (_, _) => { if (SelectedStore is not null) DialogResult = DialogResult.OK; };
        manual.Click += (_, _) =>
        {
            if (SelectedStore is null) return;
            Password = PasswordDialog.Ask(this, SelectedStore.Name);
            if (Password is not null) DialogResult = DialogResult.OK;
        };
        CancelButton = cancel;
        AcceptButton = connect;
        RefreshResults();
    }

    public Store? SelectedStore => results.SelectedItem as Store;
    public string? Password { get; private set; }

    public static (Store? Store, string? Password) Choose()
    {
        using var picker = new StorePicker();
        return picker.ShowDialog() == DialogResult.OK
            ? (picker.SelectedStore, picker.Password)
            : (null, null);
    }

    private void RefreshResults()
    {
        var selected = SelectedStore?.Name;
        results.BeginUpdate();
        results.Items.Clear();
        foreach (var store in Stores.Search(search.Text, stores)) results.Items.Add(store);
        var selectedIndex = selected is null ? -1 : results.Items.Cast<Store>().ToList().FindIndex(s => s.Name == selected);
        if (selectedIndex >= 0) results.SelectedIndex = selectedIndex;
        else if (results.Items.Count > 0) results.SelectedIndex = 0;
        results.EndUpdate();
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        connect.Enabled = manual.Enabled = SelectedStore is not null;
    }
}
