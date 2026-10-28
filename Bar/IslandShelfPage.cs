using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MacShell.Native;
using MacShell.Themes;

namespace MacShell.Bar;

// Island page: a shelf to park files on for a moment (like the Mac notch apps' "tray"). Drop files on the island,
// then drag them out again where you need them; click one to open it. Only the paths are kept, in shelf.txt next
// to the exe (so the shelf survives a restart): nothing is copied or moved by MacShell. A file a drag-out *moved*
// (Explorer with Shift, another drive...) leaves the shelf; missing files drop off when the shelf is shown.
internal sealed class IslandShelfPage : IslandPage
{
    private const int MaxShown = 3;   // tiles in the row; more are counted ("+2")
    private const double TileSize = 70;
    private static readonly string StoreFile = System.IO.Path.Combine(AppContext.BaseDirectory, "shelf.txt");
    private static readonly SolidColorBrush DropZone = Frozen.Brush(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF));
    private static readonly Geometry FileIcon = Frozen.Geometry("M14,3 H7 A2,2 0 0 0 5,5 V19 A2,2 0 0 0 7,21 H17 A2,2 0 0 0 19,19 V8 Z M14,3 V8 H19");
    private static readonly Geometry DropIcon = Frozen.Geometry("M12,4 V15 M7,10 L12,15 L17,10 M5,20 H19");
    private static readonly Geometry CloseIcon = Frozen.Geometry("M6,6 L18,18 M18,6 L6,18");

    private readonly List<string> _files = Load();
    private readonly StackPanel _tiles = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _hint;
    private Point _pressAt;
    private string? _pressed;

    // True while a file is being dragged out (the island stays open meanwhile).
    public event Action<bool>? Dragging;

    public IslandShelfPage()
    {
        var clear = new Button
        {
            Style = (Style)Application.Current.FindResource("Shell.DockTile"),
            Content = Text("Clear", 12, Muted),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        clear.Click += (_, _) =>
        {
            _files.Clear();
            Changed();
        };
        DockPanel.SetDock(clear, Dock.Right);
        var header = new DockPanel { Children = { clear, Text("Shelf", 15, weight: FontWeights.SemiBold) } };
        _hint = Text("", 11, Muted);
        _hint.Margin = new Thickness(0, 10, 0, 0);
        _tiles.Margin = new Thickness(0, 10, 0, 0);
        View = new StackPanel { Width = ContentWidth, Children = { header, _tiles, _hint } };
        Build();
    }

    public override FrameworkElement View { get; }

    public int Count => _files.Count;

    public override PillInfo? Pill => _files.Count == 0 ? null
        : new PillInfo(FileIcon, Brushes.White, _files.Count == 1 ? "1 on the shelf" : $"{_files.Count} on the shelf");

    public override void Shown()
    {
        int before = _files.Count;
        _files.RemoveAll(f => !File.Exists(f) && !Directory.Exists(f));
        if (_files.Count != before) Changed();
    }

    // Files dropped on the island (newest first, no duplicates).
    public void Add(IEnumerable<string> paths)
    {
        foreach (string path in paths.Reverse())
        {
            _files.RemoveAll(f => f.Equals(path, StringComparison.OrdinalIgnoreCase));
            _files.Insert(0, path);
        }
        Changed();
    }

    private void Changed()
    {
        Save();
        Build();
        RaisePillChanged();
    }

    private void Build()
    {
        _tiles.Children.Clear();
        foreach (string file in _files.Take(MaxShown)) _tiles.Children.Add(TileFor(file));
        int more = _files.Count - MaxShown;
        var zone = new Border
        {
            Width = ContentWidth - Math.Min(_files.Count, MaxShown) * (TileSize + 8),
            Height = TileSize,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1.5),
            BorderBrush = DropZone,
            Child = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { Centered(Icon(DropIcon, 18, Muted, 2)), Centered(Text(more > 0 ? $"+{more} more · drop here" : "Drop here", 10, Muted)) },
            },
        };
        _tiles.Children.Add(zone);
        _hint.Text = _files.Count == 0
            ? "Drop files on the island to keep them here for a moment."
            : "Drag a file out to use it, click to open it. Files stay where they are.";
    }

    // One file: its own icon and name; × on hover removes it. Click = open, drag = drag the file out.
    private FrameworkElement TileFor(string path)
    {
        string name = System.IO.Path.GetFileName(path.TrimEnd('\\'));
        var icon = AppCatalog.Icon(new AppCatalog.App(name, path, IsFile: true), 64);
        FrameworkElement image = icon is null ? Icon(FileIcon, 28) : new Image { Source = icon, Width = 32, Height = 32 };
        var label = Text(name, 10, Frozen.Brush(Color.FromRgb(0xC8, 0xC8, 0xCE)));
        label.MaxWidth = TileSize - 8;
        label.HorizontalAlignment = HorizontalAlignment.Center;
        var remove = new Border
        {
            Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = Track,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 3, 3, 0), Visibility = Visibility.Hidden, Cursor = Cursors.Hand,
            Child = Icon(CloseIcon, 9, Brushes.White, 3),
        };
        var tile = new Grid
        {
            Width = TileSize, Height = TileSize, Margin = new Thickness(0, 0, 8, 0),
            Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = path,
            Children =
            {
                new Border { CornerRadius = new CornerRadius(10), Background = Tile },
                new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { Centered(image), label } },
                remove,
            },
        };
        label.Margin = new Thickness(0, 6, 0, 0);
        tile.MouseEnter += (_, _) => remove.Visibility = Visibility.Visible;
        tile.MouseLeave += (_, _) => remove.Visibility = Visibility.Hidden;
        remove.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _files.Remove(path);
            Changed();
        };
        tile.MouseLeftButtonDown += (_, e) =>
        {
            _pressed = path;
            _pressAt = e.GetPosition(tile);
            e.Handled = true;   // not the island's own click (pin / lyrics)
        };
        tile.MouseLeftButtonUp += (_, e) =>
        {
            if (_pressed == path) Shell.Open(path);
            _pressed = null;
            e.Handled = true;
        };
        tile.MouseMove += (_, e) =>
        {
            if (_pressed != path || e.LeftButton != MouseButtonState.Pressed) return;
            var d = e.GetPosition(tile) - _pressAt;
            if (Math.Abs(d.X) < 4 && Math.Abs(d.Y) < 4) return;
            _pressed = null;
            DragOut(tile, path);
        };
        return tile;
    }

    // A normal Windows file drag: the drop target copies, moves or links it like any dragged file.
    private void DragOut(DependencyObject source, string path)
    {
        Dragging?.Invoke(true);
        try
        {
            var data = new DataObject(DataFormats.FileDrop, new[] { path });
            var effect = DragDrop.DoDragDrop(source, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
            if (effect == DragDropEffects.Move && !File.Exists(path) && !Directory.Exists(path))
            {
                _files.Remove(path);   // it was moved away: the shortcut would point at nothing
                Changed();
            }
        }
        finally { Dragging?.Invoke(false); }
    }

    private static T Centered<T>(T e) where T : FrameworkElement
    {
        e.HorizontalAlignment = HorizontalAlignment.Center;
        return e;
    }

    private static List<string> Load()
    {
        try { return File.Exists(StoreFile) ? File.ReadAllLines(StoreFile).Where(l => l.Length > 0).ToList() : []; }
        catch (Exception) { return []; }
    }

    private void Save()
    {
        try { File.WriteAllLines(StoreFile, _files); }
        catch (Exception) { }   // read-only folder: the shelf just isn't remembered
    }
}
