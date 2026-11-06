namespace LouisaWiFi.Windows.UI;

sealed class PasswordDialog : Form
{
    private readonly TextBox password = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };

    private PasswordDialog()
    {
        Text = "輸入 Wi-Fi 密碼";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(360, 105);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "請輸入店內公告的 Wi-Fi 密碼：", AutoSize = true });
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

    public static string? Ask(IWin32Window owner)
    {
        using var dialog = new PasswordDialog();
        return dialog.ShowDialog(owner) == DialogResult.OK && dialog.password.Text.Length > 0
            ? dialog.password.Text
            : null;
    }
}
