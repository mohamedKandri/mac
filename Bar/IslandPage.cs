using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using MacShell.Themes;

namespace MacShell.Bar;

// A short live text for the island's closed pill: "22°  Casablanca", "3 on the shelf".
internal sealed record PillInfo(Geometry Icon, Brush Color, string Text);

// One of the island's scroll pages after "now playing" and the calendar (weather, file shelf, toggles, network).
// A page builds its own content, ContentWidth wide, and starts / stops its work in Shown / Hidden, so it costs
// nothing while you're not looking at it. Like the rest of the island it lives in a layered window, so it should
// change its visuals only when something actually changed.
internal abstract class IslandPage : IDisposable
{
    public const double ContentWidth = 296;   // the open card's width (340) minus its 22 px side margins
    protected static readonly SolidColorBrush Muted = Frozen.Brush(Color.FromRgb(0x9A, 0x9A, 0xA2));
    protected static readonly SolidColorBrush Track = Frozen.Brush(Color.FromRgb(0x2A, 0x2A, 0x2E));
    protected static readonly SolidColorBrush Tile = Frozen.Brush(Color.FromRgb(0x1C, 0x1C, 0x1F));

    public abstract FrameworkElement View { get; }

    // The open card's width on this page (most use the standard 340; Home is wider).
    public virtual double Width => ContentWidth + 44;

    // The open card's height on this page.
    public virtual double Height => 178;

    public virtual void Shown() { }
    public virtual void Hidden() { }

    // What the closed pill can show for this page (null = nothing); PillChanged says when it changes.
    public virtual PillInfo? Pill => null;
    public event Action? PillChanged;
    protected void RaisePillChanged() => PillChanged?.Invoke();

    public virtual void Dispose() { }

    // A line icon (24 px grid, white 1.8 px strokes by default) scaled to `size`.
    protected static FrameworkElement Icon(Geometry data, double size, Brush? stroke = null, double thickness = 1.8) =>
        new Viewbox
        {
            Width = size, Height = size,
            Child = new Canvas
            {
                Width = 24, Height = 24,
                Children =
                {
                    new Path
                    {
                        Data = data, Stroke = stroke ?? Brushes.White, StrokeThickness = thickness,
                        StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                    },
                },
            },
        };

    protected static TextBlock Text(string text, double size, Brush? color = null, FontWeight? weight = null) =>
        new() { Text = text, FontSize = size, Foreground = color ?? Brushes.White, FontWeight = weight ?? FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis };
}
