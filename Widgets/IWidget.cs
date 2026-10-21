using System.Windows;
using MacShell.Core;
using MacShell.Themes;

namespace MacShell.Widgets;

// One item on the bar (and later the dock). In practice a widget is a UserControl (XAML + class) that also
// implements this interface and returns itself as View.
//
// Contract:
//   - Built from its config entry by WidgetRegistry; a settings change rebuilds it (no in-place updates).
//   - Colors and fonts only through {DynamicResource Theme.*}, so theme switches need no widget code.
//   - Dispose() removes every hook, timer and popup the widget started.
public interface IWidget : IDisposable
{
    FrameworkElement View { get; }
}

// What a widget gets from its host.
public sealed class WidgetContext(Window host, ThemeManager theme, Action<string, string> notify, AppCommands commands)
{
    public Window Host { get; } = host;                 // for placing popups below the bar
    public ThemeManager Theme { get; } = theme;         // for popups' native glass
    public Action<string, string> Notify { get; } = notify;  // (title, text) tray toast, for errors
    public AppCommands Commands { get; } = commands;    // MacShell's own Reload / Quit / Start with Windows
}
