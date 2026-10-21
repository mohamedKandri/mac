using System.Windows;
using System.Windows.Controls;
using MacShell.Native;

namespace MacShell.Widgets;

public partial class ActiveAppWidget : UserControl, IWidget
{
    private readonly ForegroundWatcher _watcher = new();

    public FrameworkElement View => this;

    public ActiveAppWidget()
    {
        InitializeComponent();
        _watcher.Changed += Show;
        Show(ForegroundWatcher.Current);
    }

    private void Show(IntPtr hwnd)
    {
        // Null = ignore this window (taskbar, Start menu, our own popups): keep the previous name.
        if (AppInfo.GetAppName(hwnd) is string name)
            AppName.Text = name;
    }

    public void Dispose() => _watcher.Dispose();
}
