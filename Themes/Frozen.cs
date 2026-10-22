using System.Windows.Media;

namespace MacShell.Themes;

// Frozen (read-only) brushes and shapes for code-built UI. Frozen WPF objects are cheaper: WPF doesn't watch them
// for changes, and they can be shared between windows. (Like a `val` that can never be mutated.)
internal static class Frozen
{
    public static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    // Path markup ("M0,0 L10,0 Z") -> geometry.
    public static System.Windows.Media.Geometry Geometry(string data)
    {
        var geometry = System.Windows.Media.Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }
}
