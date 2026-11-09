namespace LouisaWiFi.Windows.UI;

sealed class PasswordDialog : Form
{
    private readonly TextBox password = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };

    private PasswordDialog(string? storeName)
    {
        Text = "輸入 Wi-Fi 密碼";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        TopMost = true;
        ClientSize = new Size(360, 105);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = $"請輸入{storeName}店內公告的 Wi-Fi 密碼：", AutoSize = true });
        layout.Controls.Add(password);

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        var connect = new Button { Text = "連線", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.Add(connect);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        AcceptButton = connect;
        CancelButton = cancel;
    }

    public static string? Ask(IWin32Window? owner, string? storeName = null)
    {
        using var dialog = new PasswordDialog(storeName);
        if (owner is null) dialog.StartPosition = FormStartPosition.CenterScreen;
        if (dialog.ShowDialog(owner) != DialogResult.OK) return null;
        // Same as macOS: whitespace around a copied password is never part of it.
        var password = dialog.password.Text.Trim();
        return password.Length > 0 ? password : null;
    }
}
