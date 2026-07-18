using System.Windows;

namespace TerminalHost;

public partial class DiagnosticsWindow : Window
{
    private readonly Func<string> _reportFactory;

    public DiagnosticsWindow(Func<string> reportFactory)
    {
        InitializeComponent();
        _reportFactory = reportFactory;
        RefreshReport();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshReport();

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(ReportBox.Text);
        Title = "TerminalHost 诊断信息（已复制）";
    }

    private void RefreshReport() => ReportBox.Text = _reportFactory();
}
