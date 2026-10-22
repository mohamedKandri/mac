using System.Windows;
using System.Windows.Controls;
using MacShell.Native;
using MacShell.Themes;
using MacShell.Widgets;

namespace MacShell.Bar;

// The right-click menu of a dock app, like the taskbar's: the app's open windows (click one to switch to it),
// then New window / Open, Pin to dock or Unpin from dock, and Close window(s).
// Same glass popup as the bar's menus (PopupWindow), opened above the tile.
internal sealed class DockMenu : PopupWindow
{
    public sealed record Actions(Action Launch, Action? Pin, Action? Unpin);

    private const int MaxTitle = 42;

    public DockMenu(ThemeManager theme, DockAppTile tile, Actions actions) : base(theme)
    {
        Title = "MacShell Dock Menu";
        var items = new StackPanel();
        var windows = tile.Windows;

        var header = new TextBlock { Text = tile.App.Name, FontSize = 12, Margin = new Thickness(10, 4, 10, 4), TextTrimming = TextTrimming.CharacterEllipsis };
        header.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.Muted);
        items.Children.Add(header);

        // The app's windows, front to back; the one in front gets a check mark.
        IntPtr front = WindowTracker.Foreground;
        foreach (var window in windows)
        {
            string title = WindowTracker.Title(window.Handle);
            if (title.Length > MaxTitle) title = title[..(MaxTitle - 1)] + "…";
            items.Children.Add(Item(title, window.Handle == front ? "Icon.Check" : null, () => WindowTracker.Activate(window.Handle)));
        }
        if (windows.Count > 0) items.Children.Add(Separator());

        items.Children.Add(Item(windows.Count > 0 ? "New window" : "Open", null, actions.Launch));
        if (actions.Pin is not null) items.Children.Add(Item("Pin to dock", null, actions.Pin));
        if (actions.Unpin is not null) items.Children.Add(Item("Unpin from dock", null, actions.Unpin));
        if (windows.Count > 0)
        {
            items.Children.Add(Separator());
            var handles = windows.Select(w => w.Handle).ToList();
            items.Children.Add(Item(windows.Count == 1 ? "Close window" : "Close all windows", null, () => handles.ForEach(WindowTracker.Close)));
        }

        var panel = new Border { Padding = new Thickness(6), MinWidth = 220, MaxWidth = 340, Child = items };
        panel.SetResourceReference(Border.BackgroundProperty, ThemeKeys.PanelFill);
        Content = panel;
    }

    // A menu row (Shell.MenuItem style): optional icon at the right, closes the menu, then runs `action`.
    private Button Item(string text, string? rightIcon, Action action)
    {
        var row = new DockPanel();
        if (rightIcon is not null)
        {
            var icon = new LineIcon { Data = (System.Windows.Media.Geometry)FindResource(rightIcon), Width = 14, Height = 14, Margin = new Thickness(12, 0, 0, 0) };
            DockPanel.SetDock(icon, Dock.Right);
            row.Children.Add(icon);
        }
        row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        var button = new Button
        {
            Style = (Style)FindResource("Shell.MenuItem"),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = row,
        };
        button.Click += (_, _) =>
        {
            SafeClose();
            action();
        };
        return button;
    }

    private static Border Separator()
    {
        var line = new Border { Height = 1, Margin = new Thickness(6, 4, 6, 4) };
        line.SetResourceReference(Border.BackgroundProperty, ThemeKeys.Hairline);
        return line;
    }
}
