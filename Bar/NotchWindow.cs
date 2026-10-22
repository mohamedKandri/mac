using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MacShell.Core;
using MacShell.Native;
using MacShell.Themes;

namespace MacShell.Bar;

// The solid black notch on the right edge of the primary monitor (canvas artboard "Edge notch"): one ring per
// `notch.rings` entry, and a black speech-bubble popup with details while the mouse is on a ring.
// Drag it up or down along the edge; where it was left is remembered in `notch-position.txt` next to the exe.
//
// A layered window (AllowsTransparency) so the curved flares where it meets the screen edge are truly see-through
// and pass clicks to what's behind. Solid black on purpose: it reads as hardware, in front of the glass bar and dock.
internal sealed class NotchWindow : OverlayWindow
{
    // Canvas geometry, WPF units: 16 px flares, 76 px wide body, 16 px padding, 60 px per ring, 12 px between.
    private const double Flare = 16, TotalWidth = 92, Inset = 16, ItemHeight = 60, ItemGap = 12, RingSize = 44, Stroke = 4;
    private static readonly SolidColorBrush Track = Frozen.Brush(Color.FromRgb(0x2A, 0x2A, 0x2E));

    private readonly List<NotchRing> _rings;
    private readonly List<Item> _items = [];
    private readonly DispatcherTimer _timer = new();
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly Canvas _root = new();
    private NotchPopup? _popup;
    private NotchRing? _popupRing;
    private int _tick;
    private bool _hiddenForFullscreen;

    // Vertical position: 0 = top of the free area (below the menu bar), 1 = bottom (above the dock), 0.5 = centered.
    // A fraction rather than pixels, so it still makes sense after a resolution or scale change.
    private static readonly string PositionFile = System.IO.Path.Combine(AppContext.BaseDirectory, "notch-position.txt");
    private const double DragThreshold = 4;   // px the mouse must move before a press becomes a drag (else it's a hover)
    private double _position = LoadPosition();
    private bool _pressed, _dragging;
    private double _pressY;                   // cursor y (physical px) at press
    private int _pressTop;                    // window top (physical px) at press

    public string DeviceName { get; }

    public NotchWindow(NotchConfig config, string deviceName) : base(clickThrough: false)
    {
        DeviceName = deviceName;
        Content = _root;

        _rings = config.Rings.Select(NotchRing.Create).ToList();
        foreach (var ring in _rings) ring.Update(0);
        Build();

        _timer.Interval = TimeSpan.FromSeconds(config.RefreshSeconds);
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            ClosePopup();
        };
        MouseLeave += (_, _) => _hideTimer.Start();
        MouseLeftButtonDown += OnPress;
        MouseMove += OnDrag;
        MouseLeftButtonUp += (_, _) => EndDrag();
        LostMouseCapture += (_, _) => EndDrag();
        DpiChanged += (_, _) => Dispatcher.InvokeAsync(Place, DispatcherPriority.Loaded);
    }

    // (Re)builds the shape and the rings for the rings that are available (no battery = no battery ring).
    private void Build()
    {
        _root.Children.Clear();
        _items.Clear();
        var shown = _rings.Where(r => r.Available).ToList();
        double height = 2 * Flare + 2 * Inset + shown.Count * ItemHeight + Math.Max(0, shown.Count - 1) * ItemGap;
        Width = TotalWidth;
        Height = height;

        // Body from x 16 to 92 (the screen edge); concave 16 px flares where it meets the edge, 24 px outer corners.
        string path = string.Format(CultureInfo.InvariantCulture,
            "M92,0 Q92,16 76,16 L40,16 Q16,16 16,40 L16,{0} Q16,{1} 40,{1} L76,{1} Q92,{1} 92,{2} Z",
            height - 40, height - 16, height);
        _root.Children.Add(new Path { Data = Geometry.Parse(path), Fill = Brushes.Black });

        double y = Flare + Inset;
        foreach (var ring in shown)
        {
            var item = BuildRing(ring, out var arc, out var value);
            Canvas.SetLeft(item, Flare + (TotalWidth - Flare - RingSize) / 2);
            Canvas.SetTop(item, y);
            _root.Children.Add(item);
            _items.Add(new Item(ring, item, arc, value));
            y += ItemHeight + ItemGap;
            item.MouseEnter += (_, _) => OpenPopup(ring, item);
        }
        UpdateRings();
        if (IsLoaded) Place();
    }

    private static FrameworkElement BuildRing(NotchRing ring, out Ellipse arc, out TextBlock value)
    {
        var track = new Ellipse { Width = RingSize, Height = RingSize, Stroke = Track, StrokeThickness = Stroke };
        arc = new Ellipse
        {
            Width = RingSize, Height = RingSize, StrokeThickness = Stroke,
            Stroke = Frozen.Brush(ring.Color), StrokeDashCap = PenLineCap.Round,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(-90),   // start at the top
        };
        var icon = new LineIcon
        {
            Data = (Geometry)Application.Current.FindResource(ring.IconKey),
            Width = 18, Height = 18,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var circle = new Grid { Width = RingSize, Height = RingSize, Children = { track, arc, icon } };
        value = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
        // Transparent background: the whole ring area reacts to the mouse, not just the drawn strokes.
        return new StackPanel { Width = RingSize, Background = Brushes.Transparent, Children = { circle, value } };
    }

    private void Tick()
    {
        _tick++;
        bool availabilityChanged = false;
        foreach (var ring in _rings)
        {
            bool before = ring.Available;
            ring.Update(_tick);
            availabilityChanged |= before != ring.Available;
        }
        if (availabilityChanged) Build();
        else UpdateRings();
        if (_popup is not null && _popupRing is not null) _popup.Show(_popupRing.Details(), _popupRing.Color);
    }

    // Only rings whose number changed are touched. Every visual change makes WPF redraw the whole layered window
    // (measured: ~40 ms of CPU each time, in software rendering), so an unchanged ring must not cause one.
    private void UpdateRings()
    {
        // A dash pattern measured in stroke widths: the arc's length, then a gap of a full circle (so it never repeats).
        double circumference = Math.PI * (RingSize - Stroke);
        foreach (var item in _items)
        {
            int percent = Math.Clamp(item.Ring.Percent, 0, 100);
            if (percent == item.Shown) continue;
            item.Shown = percent;
            double length = circumference * percent / 100;
            var dashes = new DoubleCollection { length / Stroke, circumference / Stroke };
            dashes.Freeze();
            item.Arc.StrokeDashArray = dashes;
            item.Value.Text = $"{percent}%";
        }
    }

    // A ring's visuals, plus the number they currently show (-1 = not drawn yet).
    private sealed class Item(NotchRing ring, FrameworkElement view, Ellipse arc, TextBlock value)
    {
        public NotchRing Ring { get; } = ring;
        public FrameworkElement View { get; } = view;
        public Ellipse Arc { get; } = arc;
        public TextBlock Value { get; } = value;
        public int Shown { get; set; } = -1;
    }

    // Pinned to the right edge of the monitor, at `_position` within the work area (physical pixels).
    public void Place()
    {
        if (Monitors.Find(DeviceName) is not Monitors.Info monitor) return;
        double scale = monitor.Dpi / 96.0;
        int w = (int)Math.Round(TotalWidth * scale), h = (int)Math.Round(Height * scale);
        var (top, range) = DragRange(monitor, h);
        if (!IsVisible && !_hiddenForFullscreen) Show();
        WindowStyles.PlaceOnTop(new WindowInteropHelper(this).Handle,
            new Int32Rect(monitor.Bounds.Right - w, top + (int)Math.Round(range * _position), w, h));
    }

    // Where the notch's top may go: from the work area's top (below the menu bar) over `range` pixels, so its
    // bottom never goes below the work area (above the dock).
    private static (int Top, int Range) DragRange(Monitors.Info monitor, int height)
    {
        var area = monitor.WorkArea;
        return (area.Top, Math.Max(0, area.Bottom - area.Top - height));
    }

    // ---------------- Dragging up and down ----------------
    // Like dragging a window by its title bar, done by hand: on press, capture the mouse (all mouse moves come to
    // us even outside the notch); past a few px of movement it's a drag and the window follows the cursor.

    private void OnPress(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _pressed = true;
        _pressY = PointToScreen(e.GetPosition(this)).Y;   // PointToScreen gives physical pixels
        _pressTop = WindowStyles.RectOf(new WindowInteropHelper(this).Handle).Y;
        CaptureMouse();
        e.Handled = true;
    }

    private void OnDrag(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_pressed) return;
        double dy = PointToScreen(e.GetPosition(this)).Y - _pressY;
        if (!_dragging)
        {
            if (Math.Abs(dy) < DragThreshold) return;
            _dragging = true;
            _hideTimer.Stop();
            ClosePopup();
            Cursor = System.Windows.Input.Cursors.SizeNS;
        }
        if (Monitors.Find(DeviceName) is not Monitors.Info monitor) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        var rect = WindowStyles.RectOf(hwnd);
        var (top, range) = DragRange(monitor, rect.Height);
        int y = Math.Clamp(_pressTop + (int)Math.Round(dy), top, top + range);
        _position = range > 0 ? (double)(y - top) / range : 0.5;
        WindowStyles.MoveTo(hwnd, rect.X, y);
    }

    private void EndDrag()
    {
        if (!_pressed) return;
        _pressed = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (!_dragging) return;
        _dragging = false;
        Cursor = null;
        SavePosition(_position);
        if (!IsMouseOver) _hideTimer.Start();
    }

    private static double LoadPosition()
    {
        try
        {
            if (double.TryParse(System.IO.File.ReadAllText(PositionFile).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double p))
                return Math.Clamp(p, 0, 1);
        }
        catch (Exception) { }   // no file yet (never dragged) or unreadable: centered
        return 0.5;
    }

    private static void SavePosition(double position)
    {
        try { System.IO.File.WriteAllText(PositionFile, position.ToString("0.####", CultureInfo.InvariantCulture)); }
        catch (Exception) { }   // read-only folder: the position just isn't remembered
    }

    private void OpenPopup(NotchRing ring, FrameworkElement item)
    {
        if (_dragging) return;
        _hideTimer.Stop();
        _popupRing = ring;
        _popup ??= new NotchPopup();
        _popup.Show(ring.Details(), ring.Color);

        // Pointer at the ring's center; popup's right edge just left of the notch body.
        Point center = item.PointToScreen(new Point(RingSize / 2, RingSize / 2));
        Point bodyLeft = PointToScreen(new Point(Flare, 0));
        _popup.PlaceLeftOf((int)bodyLeft.X, (int)center.Y, (Monitors.Find(DeviceName)?.Dpi ?? 96) / 96.0);
    }

    // For the `--open notch` developer switch: the first ring's popup, as if hovered.
    public void OpenFirstPopup()
    {
        if (_items.Count > 0) OpenPopup(_items[0].Ring, _items[0].View);
    }

    private void ClosePopup()
    {
        _popup?.Close();   // closed, not hidden: a layered window keeps its pixel buffer while hidden
        _popup = null;
        _popupRing = null;
    }

    public void SetHiddenForFullscreen(bool hidden)
    {
        if (hidden == _hiddenForFullscreen) return;
        _hiddenForFullscreen = hidden;
        ClosePopup();
        if (hidden) Hide();
        else Place();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        Place();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _hideTimer.Stop();
        ClosePopup();
        foreach (var ring in _rings) ring.Dispose();
        base.OnClosed(e);
    }
}
