using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using MacShell.Native;
using MacShell.Themes;

namespace MacShell.Bar;

// Transparent, click-through window laid over the dock and the space above it. While the mouse is on the dock it
// draws the tiles magnified (and the app name above the hovered one); the dock hides its own icons meanwhile.
// Needed because the dock window IS the glass panel, and anything drawn outside a window is clipped.
//
// Coordinates: same WPF units as the dock panel, shifted down by `Headroom` (the overlay starts that much higher).
internal sealed class DockMagnifier : Window
{
    // One tile as the dock laid it out: icon (or the Trash glyph), name, horizontal center and bottom edge of the
    // icon in panel coordinates, and its normal size.
    public sealed record Item(ImageSource? Icon, bool IsTrash, string Name, double CenterX, double Bottom, double Size, bool TrashFull = false);

    private readonly Canvas _canvas = new();
    private readonly Border _label;
    private readonly TextBlock _labelText;
    private readonly List<(Item Item, FrameworkElement View, ScaleTransform Scale)> _views = [];

    // How far above the dock panel the overlay reaches: magnified tiles + the name label.
    public double Headroom { get; }

    public DockMagnifier(double headroom)
    {
        Headroom = headroom;
        WindowStyle = WindowStyle.None;
        // Not AllowsTransparency: see WindowStyles.MakeFastClickThroughOverlay (transparency comes from DWM instead).
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        UseLayoutRounding = true;
        SetResourceReference(ForegroundProperty, ThemeKeys.Foreground);
        SetResourceReference(FontFamilyProperty, ThemeKeys.FontFamily);

        _labelText = new TextBlock { FontSize = 12, FontWeight = FontWeights.Medium };
        _label = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 3, 8, 4),
            BorderThickness = new Thickness(1),
            Child = _labelText,
            Visibility = Visibility.Collapsed,
        };
        _label.SetResourceReference(Border.BackgroundProperty, ThemeKeys.LabelFill);
        _label.SetResourceReference(Border.BorderBrushProperty, ThemeKeys.Hairline);
        Panel.SetZIndex(_label, 1000);
        Content = _canvas;

        SourceInitialized += (_, _) => WindowStyles.MakeFastClickThroughOverlay(this);
    }

    // Copies the dock's tiles (called when hovering starts, or when tiles change while hovering).
    public void Load(IReadOnlyList<Item> items)
    {
        _canvas.Children.Clear();
        _views.Clear();
        foreach (var item in items)
        {
            FrameworkElement view = item.IsTrash ? TrashView(item.Size, item.TrashFull) : new Image
            {
                Source = item.Icon,
                Width = item.Size,
                Height = item.Size,
            };
            RenderOptions.SetBitmapScalingMode(view, BitmapScalingMode.HighQuality);
            var scale = new ScaleTransform();
            view.RenderTransform = scale;
            view.RenderTransformOrigin = new Point(0.5, 1);   // grow upwards from the bottom center
            Canvas.SetLeft(view, item.CenterX - item.Size / 2);
            Canvas.SetTop(view, Headroom + item.Bottom - item.Size);
            _canvas.Children.Add(view);
            _views.Add((item, view, scale));
        }
        _canvas.Children.Add(_label);
    }

    // Applies one frame: a scale per tile (1 = normal), and the label over `hovered` (-1 = none).
    public void Render(IReadOnlyList<double> scales, int hovered)
    {
        for (int i = 0; i < _views.Count && i < scales.Count; i++)
        {
            var (_, view, scale) = _views[i];
            scale.ScaleX = scale.ScaleY = scales[i];
            Panel.SetZIndex(view, (int)(scales[i] * 100));   // bigger tiles in front of the neighbours they overlap
        }

        if (hovered < 0 || hovered >= _views.Count)
        {
            _label.Visibility = Visibility.Collapsed;
            return;
        }
        var item = _views[hovered].Item;
        _labelText.Text = item.Name;
        _label.Visibility = Visibility.Visible;
        _label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double tileTop = Headroom + item.Bottom - item.Size * scales[hovered];
        Canvas.SetLeft(_label, item.CenterX - _label.DesiredSize.Width / 2);
        Canvas.SetTop(_label, tileTop - _label.DesiredSize.Height - 6);
    }

    // Covers the dock (physical pixels) plus the headroom above it.
    public void PlaceOver(Int32Rect dock, double scale)
    {
        int extra = (int)Math.Round(Headroom * scale);
        var rect = new Int32Rect(dock.X, dock.Y - extra, dock.Width, dock.Height + extra);
        if (!IsVisible) Show();
        WindowStyles.PlaceOnTop(new WindowInteropHelper(this).Handle, rect);
    }

    // Like the dock's Trash tile (DockWindow.xaml), but on the panel color: there's no glass under the overlay.
    private static FrameworkElement TrashView(double size, bool full)
    {
        var tile = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size * 0.23),   // same as the dock's Trash tile
            BorderThickness = new Thickness(1),
            Child = new LineIcon
            {
                Data = (Geometry)Application.Current.FindResource(full ? "Icon.TrashFull" : "Icon.Trash"),
                FillData = full ? (Geometry)Application.Current.FindResource("Icon.TrashPaper") : null,
                Width = size * 24 / 52,
                Height = size * 24 / 52,
            },
        };
        tile.SetResourceReference(Border.BackgroundProperty, ThemeKeys.LabelFill);
        tile.SetResourceReference(Border.BorderBrushProperty, ThemeKeys.Hairline);
        return tile;
    }
}
