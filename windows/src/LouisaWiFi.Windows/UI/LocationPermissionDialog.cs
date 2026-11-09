using LouisaWiFi.Windows.Platform;
using Windows.System;

namespace LouisaWiFi.Windows.UI;

sealed class LocationPermissionDialog : Form
{
    private readonly LocationService location;
    private readonly Label explanation = new() { Dock = DockStyle.Fill };
    private readonly Button request = new() { AutoSize = true };
    private readonly Button settings = new() { Text = "開啟 Windows 定位設定", AutoSize = true };

    public LocationPermissionDialog(LocationService location)
    {
        this.location = location;
        Text = "定位與 Wi-Fi 權限";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(520, 200);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 2 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(explanation);

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var later = new Button { Text = "稍後", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.Add(later);
        buttons.Controls.Add(request);
        buttons.Controls.Add(settings);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        CancelButton = later;

        request.Click += async (_, _) => await RequestAsync();
        settings.Click += async (_, _) => await OpenSettingsAsync();
        Activated += (_, _) => UpdatePermission();
        UpdatePermission();
    }

    private void UpdatePermission()
    {
        var permission = location.Permission;
        explanation.Text = permission switch
        {
            LocationPermission.Allowed =>
                "Windows 已允許定位與 Wi-Fi 資訊。若要調整權限，可開啟 Windows 定位設定。",
            LocationPermission.NeedsPrompt =>
                "自動辨識門市需要定位及附近的 Wi-Fi 資訊。請按「要求授權」，並在 Windows 提示時選擇是否允許。",
            LocationPermission.DeniedBySystem =>
                "Windows 定位服務或系統原則可能限制存取。請開啟定位設定，檢查定位服務及桌面應用程式的定位權限。",
            LocationPermission.DeniedByUser =>
                "Windows 已拒絕定位或 Wi-Fi 資訊存取。請開啟定位設定，允許定位及桌面應用程式使用位置。",
            _ =>
                "Windows 目前無法提供完整的定位或 Wi-Fi 權限。請檢查定位服務及桌面應用程式的定位設定。"
        };
        request.Text = permission == LocationPermission.Allowed ? "完成" : "要求授權";
        request.Enabled = permission is LocationPermission.Allowed or LocationPermission.NeedsPrompt;
    }

    private async Task RequestAsync()
    {
        request.Enabled = false;
        try
        {
            var permission = await location.RequestAccessAsync();
            if (permission == LocationPermission.Allowed)
            {
                DialogResult = DialogResult.OK;
                return;
            }
            MessageBox.Show(this, "Windows 尚未允許所需的權限，請檢查定位設定。", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, $"無法要求定位權限：{error.Message}", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UpdatePermission();
        }
    }

    private async Task OpenSettingsAsync()
    {
        try
        {
            if (!await Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-location")))
                MessageBox.Show(this, "無法開啟 Windows 定位設定，請從 Windows 設定中開啟。", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, $"無法開啟 Windows 定位設定：{error.Message}", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
