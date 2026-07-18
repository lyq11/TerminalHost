using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using TerminalHost.Api;
using TerminalHost.Core;
using TerminalHost.Infrastructure;

namespace TerminalHost;

public partial class MainWindow : Window
{
    private sealed class TerminalTabState
    {
        internal TabItem Item { get; init; } = null!;
        internal WebView2 WebView { get; init; } = null!;
        internal string Name { get; set; } = string.Empty;
        internal string Shell { get; set; } = "pwsh";
        internal string WorkingDirectory { get; set; } = string.Empty;
        internal string? SessionId { get; set; }
        internal bool WebReady { get; set; }
    }

    private const uint GmemMoveable = 0x0002;
    private const uint CfUnicodeText = 13;

    private static readonly string DisplayVersion =
        typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    private AppSettings _settings = AppSettings.LoadOrCreate();
    private readonly TerminalSessionManager _manager = new();
    private readonly List<TerminalTabState> _tabs = new();
    private TerminalWebSocketServer? _server;
    private BundledMcpHost? _mcpHost;
    private bool _closing;
    private bool _exitRequested;
    private System.Windows.Forms.NotifyIcon? _trayIcon;

    private TerminalTabState? ActiveTab => TerminalTabs.SelectedItem is TabItem item
        ? _tabs.FirstOrDefault(x => ReferenceEquals(x.Item, item))
        : null;
    private string? _activeSessionId
    {
        get => ActiveTab?.SessionId;
        set { if (ActiveTab is not null) ActiveTab.SessionId = value; }
    }
    private bool _webReady
    {
        get => ActiveTab?.WebReady == true;
        set { if (ActiveTab is not null) ActiveTab.WebReady = value; }
    }
    private WebView2 TerminalWebView => ActiveTab?.WebView
        ?? throw new InvalidOperationException("没有活动终端标签页。");

    public MainWindow()
    {
        InitializeComponent();
        Title = $"TerminalHost v{DisplayVersion}";
        VersionText.Text = $"v{DisplayVersion}";
        RemoveUnavailableShells();
        SelectShell(_settings.DefaultShell);
        WorkingDirectoryTextBox.Text = _settings.DefaultWorkingDirectory;
        McpToggle.IsChecked = _settings.McpEnabled;
        McpToggle.IsEnabled = _settings.McpTransport == "http";
        McpToggle.ToolTip = $"静默启动本机 HTTP MCP 服务（127.0.0.1:{_settings.McpPort}）";
        McpToggle.Checked += McpToggle_Changed;
        McpToggle.Unchecked += McpToggle_Changed;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        _manager.OutputReceived += Manager_OutputReceived;
        _manager.SessionExited += Manager_SessionExited;
        ApiText.Text = $"ws://127.0.0.1:{_settings.ApiPort}/ws";
        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;
        ConfigureTrayIcon();
    }

    private void ConfigureTrayIcon()
    {
        if (_trayIcon is null)
        {
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("打开 TerminalHost", null, (_, _) => Dispatcher.Invoke(RestoreFromTray));
            menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(() =>
            {
                _exitRequested = true;
                Close();
            }));
            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Text = "TerminalHost",
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!),
                ContextMenuStrip = menu
            };
            _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);
        }
        _trayIcon.Visible = _settings.MinimizeToTray;
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void RemoveUnavailableShells()
    {
        var executables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["pwsh"] = "pwsh.exe",
            ["powershell"] = "powershell.exe",
            ["cmd"] = "cmd.exe"
        };

        foreach (var item in ShellComboBox.Items.OfType<ComboBoxItem>().ToArray())
        {
            var tag = item.Tag?.ToString();
            if (tag is null || !executables.TryGetValue(tag, out var executable) || !IsExecutableAvailable(executable))
                ShellComboBox.Items.Remove(item);
        }

        if (ShellComboBox.Items.Count > 0)
            ShellComboBox.SelectedIndex = 0;
        else
        {
            ShellComboBox.Items.Add(new ComboBoxItem { Content = "未找到可用终端", IsEnabled = false });
            ShellComboBox.SelectedIndex = 0;
            StartButton.IsEnabled = false;
        }
    }

    private static bool IsExecutableAvailable(string executable)
    {
        var pathDirectories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var searchDirectories = new[]
        {
            AppContext.BaseDirectory,
            Environment.CurrentDirectory,
            Environment.SystemDirectory,
            Environment.GetEnvironmentVariable("WINDIR")
        }.Concat(pathDirectories);

        foreach (var directory in searchDirectories)
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            try
            {
                if (File.Exists(Path.Combine(directory.Trim().Trim('"'), executable)))
                    return true;
            }
            catch (Exception) when (directory.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
            }
        }

        return false;
    }

    private void SelectShell(string shell)
    {
        var item = ShellComboBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(x => string.Equals(x.Tag?.ToString(), shell, StringComparison.OrdinalIgnoreCase));
        if (item is not null) ShellComboBox.SelectedItem = item;
    }

    private async Task<TerminalTabState> CreateTabAsync(SessionProfile profile)
    {
        var header = new TextBlock { Text = profile.Name };
        var webView = new WebView2();
        var item = new TabItem { Header = header, Content = webView };
        var tab = new TerminalTabState
        {
            Item = item,
            WebView = webView,
            Name = profile.Name,
            Shell = profile.Shell,
            WorkingDirectory = Directory.Exists(profile.WorkingDirectory)
                ? profile.WorkingDirectory
                : _settings.DefaultWorkingDirectory
        };

        header.MouseLeftButtonDown += (_, args) =>
        {
            if (args.ClickCount == 2) RenameTab(tab);
        };
        var contextMenu = new ContextMenu();
        var renameItem = new MenuItem { Header = "重命名" };
        renameItem.Click += (_, _) => RenameTab(tab);
        var closeItem = new MenuItem { Header = "关闭标签" };
        closeItem.Click += async (_, _) => await CloseTabAsync(tab, confirm: true);
        contextMenu.Items.Add(renameItem);
        contextMenu.Items.Add(closeItem);
        item.ContextMenu = contextMenu;

        _tabs.Add(tab);
        TerminalTabs.Items.Add(item);
        TerminalTabs.SelectedItem = item;
        await InitializeWebViewAsync(tab);
        return tab;
    }

    private async Task RestoreTabsAsync()
    {
        SessionProfile[] profiles = _settings.RestoreSessions && _settings.SavedSessions.Length > 0
            ? _settings.SavedSessions
            : new[] { new SessionProfile("终端 1", _settings.DefaultShell, _settings.DefaultWorkingDirectory) };
        foreach (var profile in profiles)
            await CreateTabAsync(profile);
        if (_tabs.Count > 0) TerminalTabs.SelectedItem = _tabs[0].Item;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _server = new TerminalWebSocketServer(_manager, _settings);
            await _server.StartAsync();
            if (_settings.McpEnabled)
                SetMcpEnabled(true, persist: false);
            await RestoreTabsAsync();
            StatusText.Text = "API 已启动，终端标签页已就绪";
        }
        catch (Exception ex)
        {
            StatusText.Text = "初始化失败";
            MessageBox.Show(this, ex.ToString(), "TerminalHost 初始化失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task InitializeWebViewAsync(TerminalTabState tab)
    {
        await tab.WebView.EnsureCoreWebView2Async();
        var core = tab.WebView.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.AreDevToolsEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.WebMessageReceived += Core_WebMessageReceived;
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith("https://terminal.local/", StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
        };

        var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        core.SetVirtualHostNameToFolderMapping(
            "terminal.local",
            webRoot,
            CoreWebView2HostResourceAccessKind.Allow);
        core.Navigate("https://terminal.local/index.html");
    }

    private async void Core_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var tab = _tabs.FirstOrDefault(x => ReferenceEquals(x.WebView.CoreWebView2, sender));
            if (tab is null) return;
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();
            switch (type)
            {
                case "ready":
                    tab.WebReady = true;
                    ApplyTerminalAppearance(tab);
                    await StartNewSessionAsync(tab);
                    break;
                case "input":
                    if (tab.SessionId is not null && root.TryGetProperty("data", out var data))
                        await _manager.WriteAsync(tab.SessionId, data.GetString() ?? string.Empty);
                    break;
                case "resize":
                    if (tab.SessionId is not null)
                    {
                        var cols = root.GetProperty("cols").GetInt32();
                        var rows = root.GetProperty("rows").GetInt32();
                        _manager.Resize(tab.SessionId, cols, rows);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private async Task StartNewSessionAsync(TerminalTabState? target = null)
    {
        var tab = target ?? ActiveTab;
        if (tab is null || !tab.WebReady) return;
        SetControlsEnabled(false);
        try
        {
            if (tab.SessionId is not null)
            {
                try { await _manager.StopAsync(tab.SessionId, graceful: true, remove: true); } catch { }
                tab.SessionId = null;
                if (ReferenceEquals(tab, ActiveTab)) UpdateSessionId(null);
            }

            PostToTerminal(tab, new { type = "clear" });
            bool isActive = ReferenceEquals(tab, ActiveTab);
            var shell = isActive ? ((ComboBoxItem)ShellComboBox.SelectedItem).Tag?.ToString() ?? "pwsh" : tab.Shell;
            var workingDirectory = isActive ? WorkingDirectoryTextBox.Text : tab.WorkingDirectory;
            var info = await _manager.CreateAsync(new TerminalCreateRequest(
                shell,
                workingDirectory,
                120,
                30));
            tab.SessionId = info.Id;
            tab.Shell = info.Shell;
            tab.WorkingDirectory = info.WorkingDirectory;
            if (isActive) UpdateSessionId(info.Id);
            _manager.Begin(info.Id);
            _settings = _settings with
            {
                RecentDirectories = new[] { info.WorkingDirectory }
                    .Concat(_settings.RecentDirectories)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToArray()
            };
            _settings.Save();
            if (isActive) StatusText.Text = $"运行中：{info.Shell} | {info.WorkingDirectory}";
            PostToTerminal(tab, new { type = "focus" });
        }
        catch (Exception ex)
        {
            tab.SessionId = null;
            if (ReferenceEquals(tab, ActiveTab)) UpdateSessionId(null);
            StatusText.Text = "启动失败";
            PostToTerminal(tab, new { type = "output", data = $"\r\n\x1b[31mTerminalHost 启动失败：{ex.Message}\x1b[0m\r\n" });
        }
        finally
        {
            SetControlsEnabled(true);
        }
    }

    private void Manager_OutputReceived(object? sender, TerminalOutputEvent e)
    {
        if (_closing) return;
        Dispatcher.BeginInvoke(() =>
        {
            var tab = _tabs.FirstOrDefault(x => string.Equals(x.SessionId, e.SessionId, StringComparison.OrdinalIgnoreCase));
            if (tab is not null) PostToTerminal(tab, new { type = "output", data = e.Data });
        });
    }

    private void Manager_SessionExited(object? sender, TerminalExitEvent e)
    {
        if (_closing) return;
        Dispatcher.BeginInvoke(() =>
        {
            var tab = _tabs.FirstOrDefault(x => string.Equals(x.SessionId, e.SessionId, StringComparison.OrdinalIgnoreCase));
            if (tab is null) return;
            if (ReferenceEquals(tab, ActiveTab)) StatusText.Text = $"会话已退出，ExitCode={e.ExitCode}";
            PostToTerminal(tab, new { type = "output", data = $"\r\n\x1b[90m[TerminalHost] process exited: {e.ExitCode}\x1b[0m\r\n" });
        });
    }

    private void PostToTerminal(object message)
    {
        if (ActiveTab is not null) PostToTerminal(ActiveTab, message);
    }

    private static void PostToTerminal(TerminalTabState tab, object message)
    {
        if (!tab.WebReady || tab.WebView.CoreWebView2 is null) return;
        tab.WebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e) => await StartNewSessionAsync();

    private async void NewTabButton_Click(object sender, RoutedEventArgs e)
    {
        int number = 1;
        while (_tabs.Any(x => x.Name == $"终端 {number}")) number++;
        await CreateTabAsync(new SessionProfile($"终端 {number}",
            ((ComboBoxItem)ShellComboBox.SelectedItem).Tag?.ToString() ?? _settings.DefaultShell,
            WorkingDirectoryTextBox.Text));
    }

    private void TerminalTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var tab = ActiveTab;
        if (tab is null) return;
        SelectShell(tab.Shell);
        WorkingDirectoryTextBox.Text = tab.WorkingDirectory;
        UpdateSessionId(tab.SessionId);
        if (tab.SessionId is not null)
        {
            var info = _manager.List().FirstOrDefault(x => x.Id.Equals(tab.SessionId, StringComparison.OrdinalIgnoreCase));
            StatusText.Text = info is null ? tab.Name : $"{tab.Name} | {(info.IsRunning ? "运行中" : "已退出")} | {info.WorkingDirectory}";
        }
        PostToTerminal(tab, new { type = "focus" });
    }

    private async Task CloseTabAsync(TerminalTabState tab, bool confirm)
    {
        if (confirm && tab.SessionId is not null)
        {
            var info = _manager.List().FirstOrDefault(x => x.Id.Equals(tab.SessionId, StringComparison.OrdinalIgnoreCase));
            if (info?.IsRunning == true && MessageBox.Show(this,
                    $"标签“{tab.Name}”中的终端仍在运行，确定关闭吗？", "关闭终端标签",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
        }

        if (tab.SessionId is not null)
        {
            try { await _manager.StopAsync(tab.SessionId, graceful: true, remove: true); } catch { }
        }
        _tabs.Remove(tab);
        TerminalTabs.Items.Remove(tab.Item);
        tab.WebView.Dispose();
        AppLog.Info($"终端标签已关闭：{tab.Name}");

        if (_tabs.Count == 0 && !_closing)
            await CreateTabAsync(new SessionProfile("终端 1", _settings.DefaultShell, _settings.DefaultWorkingDirectory));
    }

    private void RenameTab(TerminalTabState tab)
    {
        var input = new TextBox { Text = tab.Name, Margin = new Thickness(12), MinWidth = 280 };
        var ok = new Button { Content = "确定", IsDefault = true, MinWidth = 80, Margin = new Thickness(6) };
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 80, Margin = new Thickness(6) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "标签名称", Margin = new Thickness(12, 12, 12, 0) });
        panel.Children.Add(input);
        panel.Children.Add(buttons);
        var dialog = new Window
        {
            Title = "重命名终端标签", Owner = this, Content = panel, SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(26, 29, 36)),
            Foreground = System.Windows.Media.Brushes.White
        };
        ok.Click += (_, _) => dialog.DialogResult = true;
        input.SelectAll();
        input.Focus();
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(input.Text))
        {
            tab.Name = input.Text.Trim();
            if (tab.Item.Header is TextBlock header) header.Text = tab.Name;
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeSessionId is null) return;
        SetControlsEnabled(false);
        try
        {
            await _manager.StopAsync(_activeSessionId, graceful: false);
            StatusText.Text = "会话已停止";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            SetControlsEnabled(true);
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e) => PostToTerminal(new { type = "clear" });

    private void McpToggle_Changed(object sender, RoutedEventArgs e)
    {
        SetMcpEnabled(McpToggle.IsChecked == true, persist: true);
    }

    private void SetMcpEnabled(bool enabled, bool persist)
    {
        try
        {
            if (enabled && _mcpHost is null)
            {
                if (_settings.McpTransport == "stdio")
                {
                    StatusText.Text = "MCP stdio 模式由外部客户端按需启动";
                }
                else
                {
                _mcpHost = BundledMcpHost.TryStartHttp(_settings);
                if (_mcpHost is null)
                    throw new FileNotFoundException("未找到内置 MCP 运行环境，请使用完整发布包。");
                StatusText.Text = $"MCP 已启用：http://127.0.0.1:{_settings.McpPort}/mcp";
                }
            }
            else if (!enabled && _mcpHost is not null)
            {
                _mcpHost.Dispose();
                _mcpHost = null;
                StatusText.Text = "MCP 已关闭";
            }

            if (persist && _settings.McpEnabled != enabled)
            {
                _settings = _settings with { McpEnabled = enabled };
                _settings.Save();
            }
        }
        catch (Exception ex)
        {
            _mcpHost?.Dispose();
            _mcpHost = null;
            McpToggle.Checked -= McpToggle_Changed;
            McpToggle.Unchecked -= McpToggle_Changed;
            McpToggle.IsChecked = false;
            McpToggle.Checked += McpToggle_Changed;
            McpToggle.Unchecked += McpToggle_Changed;
            _settings = _settings with { McpEnabled = false };
            _settings.Save();
            StatusText.Text = $"MCP 启动失败：{ex.Message}";
        }
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _exitRequested = true;
        Close();
    }

    private async void SettingsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settings) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.UpdatedSettings is null) return;

        AppSettings previous = _settings;
        _settings = dialog.UpdatedSettings;
        _settings.Save();
        try
        {
        ConfigureTrayIcon();
        McpToggle.Checked -= McpToggle_Changed;
        McpToggle.Unchecked -= McpToggle_Changed;
        McpToggle.IsChecked = _settings.McpEnabled;
        McpToggle.IsEnabled = _settings.McpTransport == "http";
        McpToggle.ToolTip = $"静默启动本机 HTTP MCP 服务（127.0.0.1:{_settings.McpPort}）";
        McpToggle.Checked += McpToggle_Changed;
        McpToggle.Unchecked += McpToggle_Changed;

        if (_server is not null) await _server.DisposeAsync();
        _server = new TerminalWebSocketServer(_manager, _settings);
        await _server.StartAsync();
        ApiText.Text = $"ws://127.0.0.1:{_settings.ApiPort}/ws";

        bool mcpChanged = previous.McpPort != _settings.McpPort ||
                          previous.McpTransport != _settings.McpTransport ||
                          previous.McpAuthToken != _settings.McpAuthToken ||
                          previous.McpEnabled != _settings.McpEnabled;
        if (mcpChanged)
        {
            _mcpHost?.Dispose();
            _mcpHost = null;
            SetMcpEnabled(_settings.McpEnabled, persist: false);
        }

        ApplyTerminalAppearance();
        AppLog.Info("应用设置已更新。");
        }
        catch (Exception ex)
        {
            AppLog.Error("应用新设置失败，正在恢复原设置。", ex);
            _settings = previous;
            _settings.Save();
            ConfigureTrayIcon();
            McpToggle.IsChecked = _settings.McpEnabled;
            McpToggle.IsEnabled = _settings.McpTransport == "http";
            ApiText.Text = $"ws://127.0.0.1:{_settings.ApiPort}/ws";
            try
            {
                if (_server is not null) await _server.DisposeAsync();
                _server = new TerminalWebSocketServer(_manager, _settings);
                await _server.StartAsync();
            }
            catch (Exception recoveryError)
            {
                AppLog.Error("恢复 WebSocket API 失败。", recoveryError);
            }
            MessageBox.Show(this, ex.Message, "设置应用失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DiagnosticsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        new DiagnosticsWindow(BuildDiagnosticReport) { Owner = this }.ShowDialog();
    }

    private string BuildDiagnosticReport()
    {
        var report = new StringBuilder();
        report.AppendLine($"TerminalHost {DisplayVersion}");
        report.AppendLine($"OS: {Environment.OSVersion}");
        report.AppendLine($".NET: {Environment.Version}");
        report.AppendLine($"Process: {Environment.ProcessPath}");
        report.AppendLine($"Settings: {AppSettings.FilePath}");
        report.AppendLine($"Log: {AppLog.CurrentLogPath}");
        report.AppendLine($"WebSocket: ws://127.0.0.1:{_settings.ApiPort}/ws (token redacted)");
        report.AppendLine($"MCP: enabled={_settings.McpEnabled}, transport={_settings.McpTransport}, port={_settings.McpPort}, authToken={(string.IsNullOrEmpty(_settings.McpAuthToken) ? "none" : "configured")}");
        report.AppendLine($"MCP tools: {string.Join(", ", _settings.McpAllowedTools)}");
        report.AppendLine($"Allowed directories: {(_settings.McpAllowedDirectories.Length == 0 ? "unrestricted" : string.Join("; ", _settings.McpAllowedDirectories))}");
        report.AppendLine();
        report.AppendLine("Sessions:");
        foreach (var session in _manager.List())
            report.AppendLine($"- {session.Id} | {session.Shell} | running={session.IsRunning} | {session.WorkingDirectory}");
        report.AppendLine();
        report.AppendLine("Recent log:");
        report.AppendLine(AppLog.GetRecentText());
        return report.ToString();
    }

    private void ApplyTerminalAppearance()
    {
        foreach (var tab in _tabs) ApplyTerminalAppearance(tab);
    }

    private void ApplyTerminalAppearance(TerminalTabState tab)
    {
        if (!tab.WebReady) return;
        PostToTerminal(tab, new
        {
            type = "configure",
            fontFamily = _settings.TerminalFontFamily,
            fontSize = _settings.TerminalFontSize,
            theme = _settings.TerminalTheme
        });
    }

    private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            this,
            $"TerminalHost v{DisplayVersion}\n\n" +
            "Windows 本地终端宿主\n" +
            "ConPTY + WebView2 + xterm.js\n" +
            "WebSocket API 协议版本：1\n" +
            $".NET Runtime：{Environment.Version}",
            "关于 TerminalHost",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private async void CopyApiButton_Click(object sender, RoutedEventArgs e)
    {
        await CopyToClipboardAsync(
            $"ws://127.0.0.1:{_settings.ApiPort}/ws?token={_settings.ApiToken}",
            "WebSocket 地址和令牌已复制");
    }

    private async void CopySessionIdButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeSessionId is null) return;
        await CopyToClipboardAsync(_activeSessionId, "Session ID 已复制");
    }

    private async Task CopyToClipboardAsync(string text, string successMessage)
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            if (TrySetClipboardText(text))
            {
                StatusText.Text = successMessage;
                return;
            }

            if (attempt < 5)
                await Task.Delay(attempt * 80);
        }

        StatusText.Text = "复制失败：剪贴板正被其他程序占用，请稍后重试";
    }

    private bool TrySetClipboardText(string text)
    {
        var owner = new WindowInteropHelper(this).Handle;
        if (!OpenClipboard(owner)) return false;

        var memory = IntPtr.Zero;
        try
        {
            if (!EmptyClipboard()) return false;

            memory = GlobalAlloc(GmemMoveable, new UIntPtr((uint)((text.Length + 1) * sizeof(char))));
            if (memory == IntPtr.Zero) return false;

            var target = GlobalLock(memory);
            if (target == IntPtr.Zero) return false;
            try
            {
                var characters = text.ToCharArray();
                Marshal.Copy(characters, 0, target, characters.Length);
                Marshal.WriteInt16(target, characters.Length * sizeof(char), 0);
            }
            finally
            {
                GlobalUnlock(memory);
            }

            if (SetClipboardData(CfUnicodeText, memory) == IntPtr.Zero) return false;
            memory = IntPtr.Zero;
            return true;
        }
        finally
        {
            if (memory != IntPtr.Zero) GlobalFree(memory);
            CloseClipboard();
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr newOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr memory);

    private void UpdateSessionId(string? sessionId)
    {
        SessionIdText.Text = sessionId is null ? "Session ID：—" : $"Session ID：{sessionId}";
        CopySessionIdButton.IsEnabled = sessionId is not null;
    }

    private void SetControlsEnabled(bool enabled)
    {
        NewTabButton.IsEnabled = enabled;
        StartButton.IsEnabled = enabled;
        StopButton.IsEnabled = enabled;
        ShellComboBox.IsEnabled = enabled;
        WorkingDirectoryTextBox.IsEnabled = enabled;
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_exitRequested && _settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            _trayIcon?.ShowBalloonTip(1500, "TerminalHost", "程序仍在后台运行。", System.Windows.Forms.ToolTipIcon.Info);
            return;
        }
        if (_closing) return;
        _closing = true;
        e.Cancel = true;
        SetControlsEnabled(false);
        try
        {
            var profiles = _tabs.Select(x => new SessionProfile(x.Name, x.Shell, x.WorkingDirectory)).ToArray();
            _settings = _settings with { WindowWidth = Width, WindowHeight = Height, SavedSessions = profiles };
            _settings.Save();
            _mcpHost?.Dispose();
            _mcpHost = null;
            if (_server is not null) await _server.DisposeAsync();
            await _manager.DisposeAsync();
            foreach (var tab in _tabs) tab.WebView.Dispose();
            _tabs.Clear();
        }
        finally
        {
            if (_trayIcon is not null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }
            Closing -= MainWindow_Closing;
            Close();
        }
    }
}
