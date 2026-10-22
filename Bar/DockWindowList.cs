using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using MacShell.Native;
using MacShell.Themes;

namespace MacShell.Bar;

// The list of an app's windows shown above its dock tile while hovering (apps with 2+ windows), like the
// taskbar's hover previews but titles only: click a title to switch to it, × to close that window.
// Black like the dock. Never takes focus (WS_EX_NOACTIVATE), so the app you're in stays active.
internal sealed class DockWindowList : Window
{
    private const int MaxTitle = 44;
    private static readonly SolidColorBrush RowHover = Frozen(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush Muted = Frozen(Color.FromRgb(0x9A, 0x9A, 0xA2));
    private static readonly Geometry CloseIcon = FrozenGeometry("M6,6 L18,18 M18,6 L6,18");

    public DockWindowList(string appName, IReadOnlyList<WindowTracker.AppWindow> windows, Action<IntPtr> activate, Action<IntPtr> close)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        UseLayoutRounding = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Foreground = Brushes.White;
        SetResourceReference(FontFamilyProperty, ThemeKeys.FontFamily);

        var rows = new StackPanel();
        rows.Children.Add(new TextBlock { Text = appName, FontSize = 11, Foreground = Muted, Margin = new Thickness(10, 2, 10, 4) });
        IntPtr front = WindowTracker.Foreground;
        foreach (var window in windows)
        {
            string title = WindowTracker.Title(window.Handle);
            if (title.Length > MaxTitle) title = title[..(MaxTitle - 1)] + "…";
            rows.Children.Add(Row(title, window.Handle == front, () => activate(window.Handle), () => close(window.Handle)));
        }
        Content = new Border
        {
            Background = Brushes.Black,
            BorderBrush = Frozen(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(5),
            MinWidth = 200,
            MaxWidth = 360,
            Child = rows,
        };
        SourceInitialized += (_, _) => WindowStyles.MakeToolWindow(new WindowInteropHelper(this).Handle, noActivate: true);
    }

    // Bottom edge at `bottomPx`, centered on `centerXPx` (physical pixels).
    public void PlaceAbove(int centerXPx, int bottomPx, double scale)
    {
        var content = (FrameworkElement)Content;
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        int w = (int)Math.Round(content.DesiredSize.Width * scale), h = (int)Math.Round(content.DesiredSize.Height * scale);
        if (!IsVisible) Show();
        WindowStyles.PlaceOnTop(new WindowInteropHelper(this).Handle, new Int32Rect(centerXPx - w / 2, bottomPx - h, w, h));
    }

    // One window: its title (bold for the one in front), a × that closes it. The whole row switches to it.
    private static Border Row(string title, bool inFront, Action activate, Action close)
    {
        var closeButton = new Border
        {
            Width = 22, Height = 22,
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Margin = new Thickness(8, 0, 0, 0),
            Child = new Viewbox { Width = 10, Height = 10, Child = new Canvas { Width = 24, Height = 24, Children = { new Path { Data = CloseIcon, Stroke = Brushes.White, StrokeThickness = 2.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round } } } },
            Visibility = Visibility.Hidden,   // shown while the row is hovered, like the taskbar
            ToolTip = null,
        };
        DockPanel.SetDock(closeButton, Dock.Right);
        closeButton.MouseEnter += (_, _) => closeButton.Background = RowHover;
        closeButton.MouseLeave += (_, _) => closeButton.Background = Brushes.Transparent;
        closeButton.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            close();
        };

        var text = new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = inFront ? FontWeights.SemiBold : FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var row = new Border
        {
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10, 5, 5, 5),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = new DockPanel { Children = { closeButton, text } },
        };
        row.MouseEnter += (_, _) =>
        {
            row.Background = RowHover;
            closeButton.Visibility = Visibility.Visible;
        };
        row.MouseLeave += (_, _) =>
        {
            row.Background = Brushes.Transparent;
            closeButton.Visibility = Visibility.Hidden;
        };
        row.MouseLeftButtonUp += (_, _) => activate();
        return row;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Geometry FrozenGeometry(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }
}
