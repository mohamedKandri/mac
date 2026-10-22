using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using MacShell.Native;
using MacShell.Themes;

namespace MacShell.Bar;

// The black speech bubble left of the notch (canvas artboard "Edge notch"): title, one main bar, a short list.
// Click-through and never focused: it only shows information while the mouse is on a ring.
internal sealed class NotchPopup : OverlayWindow
{
    private const double BubbleWidth = 300, PointerCenter = 44;   // pointer: 14×22 px, centered 44 px down
    private static readonly SolidColorBrush Muted = Frozen.Brush(Color.FromRgb(0x9A, 0x9A, 0xA2));
    private static readonly SolidColorBrush Soft = Frozen.Brush(Color.FromRgb(0xC8, 0xC8, 0xCE));
    private static readonly SolidColorBrush Track = Frozen.Brush(Color.FromRgb(0x2A, 0x2A, 0x2E));

    private readonly StackPanel _content = new() { Width = BubbleWidth - 36 };
    private bool _animated;
    private string _shownKey = "";

    public NotchPopup() : base(clickThrough: true)
    {
        SizeToContent = SizeToContent.WidthAndHeight;

        var bubble = new Border
        {
            Width = BubbleWidth,
            CornerRadius = new CornerRadius(18),
            Background = Brushes.Black,
            Padding = new Thickness(18, 16, 18, 16),
            Child = _content,
        };
        // The shadow is its own element with a bitmap cache (like a pre-rendered image), so refreshing the text
        // doesn't recompute the 30 px blur, which is expensive in software rendering.
        var shadow = new Border
        {
            CornerRadius = new CornerRadius(18),
            Background = Brushes.Black,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Opacity = 0.45, Direction = 270 },
            CacheMode = new BitmapCache(),
        };
        var pointer = new Path { Data = Geometry.Parse("M0,0 L14,11 L0,22 Z"), Fill = Brushes.Black, Margin = new Thickness(0, PointerCenter - 11, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        // Margin around everything leaves room for the shadow inside the (transparent) window.
        Content = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 8, 0, 24), Children = { new Grid { Children = { shadow, bubble } }, pointer } };

    }

    // Fills in (or refreshes) the details; shows the window with a short fade the first time.
    // Unchanged details (as displayed, after rounding) are skipped: any change redraws the whole layered window.
    public void Show(NotchDetails details, Color accent)
    {
        string key = $"{accent}|{details.Title}|{details.Subtitle}|{details.MainLabel}|{details.MainPercent:F0}|{details.MainValue}|{details.ListTitle}|"
            + string.Join("|", details.Rows.Select(r => $"{r.Name}:{r.Percent:F0}:{r.Value}"));
        if (key == _shownKey && IsVisible) return;
        _shownKey = key;

        _content.Children.Clear();
        var accentBrush = Frozen.Brush(accent);

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var subtitle = new TextBlock { Text = details.Subtitle, FontSize = 11, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(subtitle, Dock.Right);
        header.Children.Add(subtitle);
        header.Children.Add(new TextBlock { Text = details.Title, FontSize = 15, FontWeight = FontWeights.SemiBold });
        _content.Children.Add(header);

        _content.Children.Add(Row(details.MainLabel, details.MainValue, Soft, 12));
        _content.Children.Add(Bar(details.MainPercent, accentBrush, 8, new Thickness(0, 6, 0, 0)));

        if (details.Rows.Count > 0)
        {
            _content.Children.Add(new TextBlock
            {
                Text = details.ListTitle.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.Medium,
                Foreground = Muted, Margin = new Thickness(0, 14, 0, 6),
            });
            foreach (var row in details.Rows)
            {
                var line = new DockPanel { Margin = new Thickness(0, 0, 0, 7) };
                var name = new TextBlock { Text = row.Name, FontSize = 12, Width = 84, TextTrimming = TextTrimming.CharacterEllipsis };
                var value = new TextBlock { Text = row.Value, FontSize = 12, Foreground = Soft, MinWidth = 44, TextAlignment = TextAlignment.Right };
                DockPanel.SetDock(name, Dock.Left);
                DockPanel.SetDock(value, Dock.Right);
                line.Children.Add(name);
                line.Children.Add(value);
                line.Children.Add(Bar(row.Percent, accentBrush, 5, new Thickness(8, 0, 8, 0)));
                _content.Children.Add(line);
            }
        }
        else if (details.ListTitle.Length > 0)
        {
            _content.Children.Add(new TextBlock { Text = "Measuring…", FontSize = 12, Foreground = Muted, Margin = new Thickness(0, 14, 0, 0) });
        }

        if (!IsVisible) Show();
        if (!_animated)
        {
            _animated = true;
            ((FrameworkElement)Content).BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
        }
    }

    // Right edge (incl. pointer) at `rightPx`, pointer tip at `centerYPx` (physical pixels).
    public void PlaceLeftOf(int rightPx, int centerYPx, double scale)
    {
        UpdateLayout();
        var size = ((FrameworkElement)Content).DesiredSize;   // includes the shadow margins (24 left, 8 top, 24 bottom)
        int w = (int)Math.Round(size.Width * scale), h = (int)Math.Round(size.Height * scale);
        int top = centerYPx - (int)Math.Round((8 + PointerCenter) * scale);
        WindowStyles.PlaceOnTop(new WindowInteropHelper(this).Handle, new Int32Rect(rightPx - w, top, w, h));
    }

    private static DockPanel Row(string left, string right, Brush color, double size)
    {
        var row = new DockPanel();
        var value = new TextBlock { Text = right, FontSize = size, Foreground = color };
        DockPanel.SetDock(value, Dock.Right);
        row.Children.Add(value);
        row.Children.Add(new TextBlock { Text = left, FontSize = size, Foreground = color });
        return row;
    }

    private static Grid Bar(double percent, Brush fill, double height, Thickness margin)
    {
        var bar = new Grid { Height = height, Margin = margin, VerticalAlignment = VerticalAlignment.Center };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Clamp(percent, 0, 100), GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - Math.Clamp(percent, 0, 100), GridUnitType.Star) });
        var track = new Border { Background = Track, CornerRadius = new CornerRadius(height / 2) };
        Grid.SetColumnSpan(track, 2);
        bar.Children.Add(track);
        bar.Children.Add(new Border { Background = fill, CornerRadius = new CornerRadius(height / 2) });
        return bar;
    }
}
