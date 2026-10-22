using System.Windows;
using MacShell.Native;

namespace MacShell.Bar;

// macOS magnification and the app name label while hovering (dock step 4), drawn by Bar/DockMagnifier.
public partial class DockWindow
{
    // ---------------- Magnification + app name (dock step 4) ----------------

    private const double LabelSpace = 34;        // room above a magnified tile for its name
    private const double Smoothing = 0.55;       // per frame: how far tiles move towards their target size (0.3 felt laggy)
    private static readonly TimeSpan BounceTime = TimeSpan.FromMilliseconds(280);

    private DockMagnifier? _magnifier;
    private List<DockMagnifier.Item> _items = [];
    private readonly List<FrameworkElement> _hiddenWhileMagnified = [];
    private double[] _scales = [];
    private bool _hovering, _looping;
    private int _framesShown;
    private double _mouseX;
    private int _bounceIndex = -1;
    private DateTime _bounceStart;

    private double Scale => (Monitors.Find(DeviceName)?.Dpi ?? 96) / 96.0;

    // For the `--open dock-hover` developer switch: magnify as if the mouse sat on tile `index`
    // (it stays until the real mouse enters and leaves the dock).
    public void SimulateHover(int index)
    {
        BeginHover();
        if (_items.Count == 0) return;
        _mouseX = _items[Math.Clamp(index, 0, _items.Count - 1)].CenterX;
        StartLoop();
    }

    private void BeginHover()
    {
        _hovering = true;
        _magnifier ??= new DockMagnifier(_config.Dock.MagnifiedSize - _config.Dock.TileSize + LabelSpace);
        LoadMagnifier();
        _magnifier.PlaceOver(WindowStyles.RectOf(new System.Windows.Interop.WindowInteropHelper(this).Handle), Scale);
        _framesShown = 0;
    }

    // Copies the current tiles into the overlay, with their positions in panel coordinates.
    private void LoadMagnifier()
    {
        if (_magnifier is null) return;
        _items.Clear();
        _hiddenWhileMagnified.Clear();
        foreach (var tile in AppTiles.Children.OfType<DockAppTile>().Concat(RunningTiles.Children.OfType<DockAppTile>()))
            Add(tile.IconElement, tile.App.Name, (tile.IconElement as System.Windows.Controls.Image)?.Source, isTrash: false);
        Add(TrashTile, _binFull ? "Trash (full)" : "Trash", null, isTrash: true);
        _scales = Enumerable.Repeat(1.0, _items.Count).ToArray();
        _magnifier.Load(_items);
        _magnifier.Render(_scales, -1);

        void Add(FrameworkElement element, string name, System.Windows.Media.ImageSource? icon, bool isTrash)
        {
            Point bottomCenter = element.TranslatePoint(new Point(element.ActualWidth / 2, element.ActualHeight), Root);
            _items.Add(new DockMagnifier.Item(icon, isTrash, name, bottomCenter.X, bottomCenter.Y, element.ActualWidth, isTrash && _binFull));
            _hiddenWhileMagnified.Add(element);
        }
    }

    // Per-frame animation, only while needed: CompositionTarget.Rendering fires once per screen refresh while
    // subscribed (like requestAnimationFrame), so nothing runs when the mouse is elsewhere.
    private void StartLoop()
    {
        if (_looping || _magnifier is null) return;
        _looping = true;
        System.Windows.Media.CompositionTarget.Rendering += OnFrame;
    }

    // Settled while the mouse rests on a tile: stop drawing frames, keep the overlay. The next MouseMove resumes.
    private void PauseLoop()
    {
        _looping = false;
        System.Windows.Media.CompositionTarget.Rendering -= OnFrame;
    }

    // Mouse gone and tiles back to normal: show the real icons again and close the overlay. Closing (rather than
    // hiding) frees its ~12 MB of layered-window pixels; re-creating it on the next hover takes a few ms.
    private void StopLoop()
    {
        PauseLoop();
        foreach (var element in _hiddenWhileMagnified) element.Opacity = 1;
        _magnifier?.Close();
        _magnifier = null;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (_magnifier is null || _items.Count == 0)
        {
            StopLoop();
            return;
        }

        double maxScale = _config.Dock.MagnifiedSize / _config.Dock.TileSize;
        double reach = _config.Dock.TileSize * 1.8;   // how far the effect spreads: neighbours reach ~60 px at default sizes
        int hovered = -1;
        double nearest = double.MaxValue;
        bool settled = true;
        var shown = new double[_items.Count];

        for (int i = 0; i < _items.Count; i++)
        {
            double distance = Math.Abs(_mouseX - _items[i].CenterX);
            if (_hovering && distance < _items[i].Size / 2 + TileGap / 2 && distance < nearest)
            {
                hovered = i;
                nearest = distance;
            }

            // Cosine falloff: full size under the mouse, fading to normal size at `reach`.
            double target = _hovering && distance < reach ? 1 + (maxScale - 1) * Math.Cos(distance / reach * Math.PI / 2) : 1;
            _scales[i] += (target - _scales[i]) * Smoothing;
            if (Math.Abs(_scales[i] - target) > 0.002 || (!_hovering && Math.Abs(_scales[i] - 1) > 0.002)) settled = false;
            shown[i] = _scales[i];
        }

        // Click feedback on the magnified copy (the real tile's own bounce is hidden underneath).
        var sinceBounce = DateTime.Now - _bounceStart;
        if (_bounceIndex >= 0 && _bounceIndex < shown.Length && sinceBounce < BounceTime)
        {
            shown[_bounceIndex] *= 1 - 0.12 * Math.Sin(Math.PI * sinceBounce / BounceTime);
            settled = false;
        }

        _magnifier.Render(shown, hovered);

        // Hide the real icons only once the overlay has drawn its copies (a couple of frames), so nothing blinks.
        if (++_framesShown == 2)
            foreach (var element in _hiddenWhileMagnified) element.Opacity = 0;

        if (settled && _framesShown >= 2)
        {
            if (_hovering) PauseLoop();
            else StopLoop();
        }
    }
}
