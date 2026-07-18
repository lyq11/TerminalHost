using System.Windows;
using System.Windows.Controls;
using System.Net;
using System.Net.Sockets;
using TerminalHost.Infrastructure;

namespace TerminalHost;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    public AppSettings? UpdatedSettings { get; private set; }

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        ApiPortBox.Text = settings.ApiPort.ToString();
        DefaultDirectoryBox.Text = settings.DefaultWorkingDirectory;
        FontFamilyBox.Text = settings.TerminalFontFamily;
        FontSizeBox.Text = settings.TerminalFontSize.ToString();
        RestoreSessionsBox.IsChecked = settings.RestoreSessions;
        MinimizeToTrayBox.IsChecked = settings.MinimizeToTray;
        McpEnabledBox.IsChecked = settings.McpEnabled;
        McpPortBox.Text = settings.McpPort.ToString();
        McpTokenBox.Password = settings.McpAuthToken;
        AllowedDirectoriesBox.Text = string.Join(Environment.NewLine, settings.McpAllowedDirectories);
        ConfirmDangerousBox.IsChecked = settings.McpConfirmDangerousCommands;
        SelectByTag(DefaultShellBox, settings.DefaultShell);
        SelectByTag(ThemeBox, settings.TerminalTheme);
        SelectByTag(McpTransportBox, settings.McpTransport);
        UpdateMcpTransportControls();

        foreach (string tool in AppSettings.AllMcpTools)
        {
            ToolListPanel.Children.Add(new CheckBox
            {
                Content = tool,
                Tag = tool,
                IsChecked = settings.McpAllowedTools.Contains(tool, StringComparer.OrdinalIgnoreCase),
                Width = 220
            });
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(ApiPortBox.Text, out int apiPort) || apiPort is < 1024 or > 65535 ||
            !int.TryParse(McpPortBox.Text, out int mcpPort) || mcpPort is < 1024 or > 65535 ||
            !int.TryParse(FontSizeBox.Text, out int fontSize) || fontSize is < 9 or > 32)
        {
            MessageBox.Show(this, "端口或字号超出允许范围。", "设置无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (apiPort == mcpPort && SelectedTag(McpTransportBox) == "http")
        {
            MessageBox.Show(this, "WebSocket API 与 MCP HTTP 不能使用同一个端口。", "端口冲突", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if ((apiPort != _settings.ApiPort && !CanBind(apiPort)) ||
            (SelectedTag(McpTransportBox) == "http" && mcpPort != _settings.McpPort && !CanBind(mcpPort)))
        {
            MessageBox.Show(this, "选择的端口已被其他程序占用。", "端口冲突", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string directory = Environment.ExpandEnvironmentVariables(DefaultDirectoryBox.Text.Trim().Trim('"'));
        if (!Directory.Exists(directory))
        {
            MessageBox.Show(this, "默认工作目录不存在。", "设置无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string[] allowedDirectories = AllowedDirectoriesBox.Text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => Environment.ExpandEnvironmentVariables(x.Trim().Trim('"')))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string? invalidDirectory = allowedDirectories.FirstOrDefault(x => !Directory.Exists(x));
        if (invalidDirectory is not null)
        {
            MessageBox.Show(this, $"允许目录不存在：{invalidDirectory}", "设置无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string[] tools = ToolListPanel.Children.OfType<CheckBox>()
            .Where(x => x.IsChecked == true).Select(x => (string)x.Tag).ToArray();
        UpdatedSettings = _settings with
        {
            ApiPort = apiPort,
            DefaultShell = SelectedTag(DefaultShellBox),
            DefaultWorkingDirectory = Path.GetFullPath(directory),
            TerminalFontFamily = string.IsNullOrWhiteSpace(FontFamilyBox.Text) ? "Consolas, monospace" : FontFamilyBox.Text.Trim(),
            TerminalFontSize = fontSize,
            TerminalTheme = SelectedTag(ThemeBox),
            RestoreSessions = RestoreSessionsBox.IsChecked == true,
            MinimizeToTray = MinimizeToTrayBox.IsChecked == true,
            McpEnabled = McpEnabledBox.IsChecked == true,
            McpTransport = SelectedTag(McpTransportBox),
            McpPort = mcpPort,
            McpAuthToken = McpTokenBox.Password.Trim(),
            McpAllowedTools = tools,
            McpAllowedDirectories = allowedDirectories,
            McpConfirmDangerousCommands = ConfirmDangerousBox.IsChecked == true
        };
        DialogResult = true;
    }

    private void McpTransportBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (McpEnabledBox is not null) UpdateMcpTransportControls();
    }

    private void UpdateMcpTransportControls()
    {
        bool isHttp = McpTransportBox.SelectedItem is ComboBoxItem item && item.Tag?.ToString() == "http";
        McpEnabledBox.IsEnabled = isHttp;
        McpPortBox.IsEnabled = isHttp;
        McpTokenBox.IsEnabled = isHttp;
        if (!isHttp) McpEnabledBox.IsChecked = false;
    }

    private static void SelectByTag(ComboBox comboBox, string tag)
    {
        comboBox.SelectedItem = comboBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(x => string.Equals(x.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase));
        if (comboBox.SelectedItem is null) comboBox.SelectedIndex = 0;
    }

    private static string SelectedTag(ComboBox comboBox)
        => ((ComboBoxItem)comboBox.SelectedItem).Tag?.ToString() ?? string.Empty;

    private static bool CanBind(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        try { listener.Start(); return true; }
        catch (SocketException) { return false; }
        finally { listener.Stop(); }
    }
}
