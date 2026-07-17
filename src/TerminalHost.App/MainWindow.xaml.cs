using Microsoft.Web.WebView2.Core;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using TerminalHost.Api;
using TerminalHost.Core;
using TerminalHost.Infrastructure;

namespace TerminalHost;

public partial class MainWindow : Window
{
    private static readonly string DisplayVersion =
        typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    private readonly AppSettings _settings = AppSettings.LoadOrCreate();
    private readonly TerminalSessionManager _manager = new();
    private TerminalWebSocketServer? _server;
    private string? _activeSessionId;
    private bool _webReady;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        Title = $"TerminalHost v{DisplayVersion}";
        VersionText.Text = $"v{DisplayVersion}";
        RemoveUnavailableShells();
        WorkingDirectoryTextBox.Text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        _manager.OutputReceived += Manager_OutputReceived;
        _manager.SessionExited += Manager_SessionExited;
        ApiText.Text = $"ws://127.0.0.1:{_settings.ApiPort}/ws";
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

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _server = new TerminalWebSocketServer(_manager, _settings);
            await _server.StartAsync();
            await InitializeWebViewAsync();
            StatusText.Text = "API 已启动，等待终端页面就绪…";
        }
        catch (Exception ex)
        {
            StatusText.Text = "初始化失败";
            MessageBox.Show(this, ex.ToString(), "TerminalHost 初始化失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task InitializeWebViewAsync()
    {
        await TerminalWebView.EnsureCoreWebView2Async();
        var core = TerminalWebView.CoreWebView2;
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
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();
            switch (type)
            {
                case "ready":
                    _webReady = true;
                    await StartNewSessionAsync();
                    break;
                case "input":
                    if (_activeSessionId is not null && root.TryGetProperty("data", out var data))
                        await _manager.WriteAsync(_activeSessionId, data.GetString() ?? string.Empty);
                    break;
                case "resize":
                    if (_activeSessionId is not null)
                    {
                        var cols = root.GetProperty("cols").GetInt32();
                        var rows = root.GetProperty("rows").GetInt32();
                        _manager.Resize(_activeSessionId, cols, rows);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private async Task StartNewSessionAsync()
    {
        if (!_webReady) return;
        SetControlsEnabled(false);
        try
        {
            if (_activeSessionId is not null)
            {
                try { await _manager.StopAsync(_activeSessionId, graceful: true, remove: true); } catch { }
                _activeSessionId = null;
                UpdateSessionId(null);
            }

            PostToTerminal(new { type = "clear" });
            var shell = ((ComboBoxItem)ShellComboBox.SelectedItem).Tag?.ToString() ?? "pwsh";
            var info = await _manager.CreateAsync(new TerminalCreateRequest(
                shell,
                WorkingDirectoryTextBox.Text,
                120,
                30));
            _activeSessionId = info.Id;
            UpdateSessionId(info.Id);
            _manager.Begin(info.Id);
            StatusText.Text = $"运行中：{info.Shell} | {info.WorkingDirectory}";
            PostToTerminal(new { type = "focus" });
        }
        catch (Exception ex)
        {
            _activeSessionId = null;
            UpdateSessionId(null);
            StatusText.Text = "启动失败";
            PostToTerminal(new { type = "output", data = $"\r\n\x1b[31mTerminalHost 启动失败：{ex.Message}\x1b[0m\r\n" });
        }
        finally
        {
            SetControlsEnabled(true);
        }
    }

    private void Manager_OutputReceived(object? sender, TerminalOutputEvent e)
    {
        if (e.SessionId != _activeSessionId || _closing) return;
        Dispatcher.BeginInvoke(() => PostToTerminal(new { type = "output", data = e.Data }));
    }

    private void Manager_SessionExited(object? sender, TerminalExitEvent e)
    {
        if (e.SessionId != _activeSessionId || _closing) return;
        Dispatcher.BeginInvoke(() =>
        {
            StatusText.Text = $"会话已退出，ExitCode={e.ExitCode}";
            PostToTerminal(new { type = "output", data = $"\r\n\x1b[90m[TerminalHost] process exited: {e.ExitCode}\x1b[0m\r\n" });
        });
    }

    private void PostToTerminal(object message)
    {
        if (!_webReady || TerminalWebView.CoreWebView2 is null) return;
        TerminalWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e) => await StartNewSessionAsync();

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

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e) => Close();

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

    private void CopyApiButton_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText($"ws://127.0.0.1:{_settings.ApiPort}/ws?token={_settings.ApiToken}");
        StatusText.Text = "WebSocket 地址和令牌已复制";
    }

    private void CopySessionIdButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeSessionId is null) return;
        Clipboard.SetText(_activeSessionId);
        StatusText.Text = "Session ID 已复制";
    }

    private void UpdateSessionId(string? sessionId)
    {
        SessionIdText.Text = sessionId is null ? "Session ID：—" : $"Session ID：{sessionId}";
        CopySessionIdButton.IsEnabled = sessionId is not null;
    }

    private void SetControlsEnabled(bool enabled)
    {
        StartButton.IsEnabled = enabled;
        StopButton.IsEnabled = enabled;
        ShellComboBox.IsEnabled = enabled;
        WorkingDirectoryTextBox.IsEnabled = enabled;
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_closing) return;
        _closing = true;
        e.Cancel = true;
        SetControlsEnabled(false);
        try
        {
            if (_server is not null) await _server.DisposeAsync();
            await _manager.DisposeAsync();
        }
        finally
        {
            Closing -= MainWindow_Closing;
            Close();
        }
    }
}
