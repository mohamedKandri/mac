using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MacShell.Themes;

// A thin line icon drawn on a 24x24 grid (same as the SVGs in the design canvas), shown at 16x16 by default.
// Stroke color = the current text color, so icons follow the theme and hover states automatically.
// Usage: <t:LineIcon Data="{StaticResource Icon.Lock}" />
public sealed class LineIcon : Viewbox
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(Geometry), typeof(LineIcon),
        new PropertyMetadata(null, (d, e) => ((LineIcon)d)._stroke.Data = (Geometry?)e.NewValue));

    // Optional solid parts (e.g. the dot in the logo), filled with the text color.
    public static readonly DependencyProperty FillDataProperty = DependencyProperty.Register(
        nameof(FillData), typeof(Geometry), typeof(LineIcon),
        new PropertyMetadata(null, (d, e) => ((LineIcon)d)._fill.Data = (Geometry?)e.NewValue));

    private readonly Path _stroke = new()
    {
        StrokeThickness = 2,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
    };
    private readonly Path _fill = new();

    public Geometry? Data
    {
        get => (Geometry?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public Geometry? FillData
    {
        get => (Geometry?)GetValue(FillDataProperty);
        set => SetValue(FillDataProperty, value);
    }

    public LineIcon()
    {
        Width = 16;
        Height = 16;
        // TextElement.Foreground is the inherited "current text color" (like CSS currentColor).
        var textColor = new Binding { Path = new PropertyPath(TextElement.ForegroundProperty), Source = this };
        _stroke.SetBinding(Shape.StrokeProperty, textColor);
        _fill.SetBinding(Shape.FillProperty, textColor);
        Child = new Canvas { Width = 24, Height = 24, Children = { _stroke, _fill } };
    }
}
