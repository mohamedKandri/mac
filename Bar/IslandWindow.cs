using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using MacShell.Core;
using MacShell.Native;
using MacShell.Themes;

namespace MacShell.Bar;

// The black "island" centered in the menu bar, like the iPhone's Dynamic Island / a MacBook notch.
// Collapsed: a bar-high pill showing the time, or what's playing (Native/MediaSessions). Hover (or click) and it
// grows down into a card: the clock, or the track with previous / play-pause / next. It shrinks back 300 ms after
// the mouse leaves. A click keeps it open until the next click; with a track playing, a click opens the lyrics
// card instead (synced lyrics from LRCLIB via Core/LyricsService, following the song), and the next click closes it.
// Scrolling the open island flips pages, like swiping on a phone: now playing / clock, calendar, then the
// Home (glowing tiles: music, focus timer, weather), then the IslandPage pages: weather, file shelf, quick toggles, network. Closing always returns to the first page.
//
// One layered window (AllowsTransparency) sized for the OPEN card; only the black shape is drawn, and fully
// transparent pixels let clicks through to the bar below. It is owned by the menu bar window, so it always stays
// above the bar. Black with 10 px concave flares where it meets the top edge, like the notch and the dock.
internal sealed class IslandWindow : OverlayWindow
{
    // Open card: 128 high for the clock, 178 with a track (artwork, progress bar, controls).
    // Lyrics card (click while a track plays): wider, not taller, than the track card. Cover + controls on the left,
    // title + four lines of lyrics on the right.
    private const double Flare = 10, CollapsedWidth = 168, OpenWidth = 340, OpenHeight = 128, MediaHeight = 178, Radius = 22;
    private const double LyricsWidth = 460, LyricsHeight = 178, LyricsCover = 112, LyricsViewport = 104;
    // Pages (mouse wheel while open). The calendar page fits six week rows.
    private const int HomePage = 1, CalendarPage = 2, FirstExtraPage = 3;
    private const double CalendarHeight = 214, DayCell = 24;
    private static readonly SolidColorBrush DotOff = Frozen.Brush(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush LyricDim = Frozen.Brush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
    private const double ContentWidth = OpenWidth - 44;   // inside the card's 22 px side margins
    private static readonly Duration Motion = TimeSpan.FromMilliseconds(220);
    private static readonly SolidColorBrush Muted = Frozen.Brush(Color.FromRgb(0x9A, 0x9A, 0xA2));
    private static readonly SolidColorBrush Playing = Frozen.Brush(Color.FromRgb(0x34, 0xC7, 0x59));
    private static readonly SolidColorBrush Track = Frozen.Brush(Color.FromRgb(0x2A, 0x2A, 0x2E));
    private static readonly Brush ArtPlaceholder = FrozenGradient(Color.FromRgb(0xFF, 0x6B, 0x8B), Color.FromRgb(0x7C, 0x6B, 0xFF));
    private static readonly Geometry NoteIcon = Frozen.Geometry("M9,18 V6 L20,4 V16 M9,18 A2.5,2.5 0 1 1 4,18 A2.5,2.5 0 1 1 9,18 M20,16 A2.5,2.5 0 1 1 15,16 A2.5,2.5 0 1 1 20,16");
    // Solid media glyphs on a 24 px grid.
    private static readonly Geometry PlayIcon = Frozen.Geometry("M7,4 L20,12 L7,20 Z");
    private static readonly Geometry PauseIcon = Frozen.Geometry("M6,4 H10 V20 H6 Z M14,4 H18 V20 H14 Z");
    private static readonly Geometry PreviousIcon = Frozen.Geometry("M5,5 H7.5 V19 H5 Z M19,5 L9,12 L19,19 Z");
    private static readonly Geometry NextIcon = Frozen.Geometry("M16.5,5 H19 V19 H16.5 Z M5,5 L15,12 L5,19 Z");

    private readonly double _collapsedHeight;   // = the menu bar height, so the pill sits inside the bar
    private readonly Border _body;
    private readonly FrameworkElement _collapsed, _open;
    private readonly TextBlock _smallTime, _bigTime, _date;
    private readonly MediaSessions _media = new();
    private readonly FrameworkElement _pillNowPlaying, _clockCard, _mediaCard;
    private readonly TextBlock _pillTitle, _title, _artist, _app, _elapsed, _total;
    private readonly Path _playPauseIcon;
    private readonly Border _art, _pillArt, _progressFill;
    private readonly FrameworkElement _artIcon, _progress;
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly FrameworkElement _lyricsCard;
    private readonly Border _lyricsArt;
    private readonly TextBlock _lyricsTitle, _lyricsArtist, _lyricsStatus;
    private readonly StackPanel _lyricLines = new();
    private readonly TranslateTransform _lyricScroll = new();
    private readonly Path _lyricsPlayPauseIcon;
    private readonly LyricsService _lyricsService = new();
    private LyricsService.Lyrics _lyrics = LyricsService.Lyrics.None;
    private string _lyricsKey = "";
    private int _lyricLine = -2;
    private DateTime _manualScrollUntil;
    private bool _lyricsOpen;
    private readonly FrameworkElement _calendarCard;
    private readonly TextBlock _calendarTitle, _calendarToday;
    private readonly System.Windows.Controls.Primitives.UniformGrid _calendarDays = new() { Columns = 7 };
    private readonly StackPanel _pageDots = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, -14, 0) };
    private int _page;                 // 0 = now playing / clock, 1 = home, 2 = calendar, 3+ = _pages[_page - 3]
    private readonly List<IslandPage> _pages = [];
    private readonly IslandShelfPage _shelf;
    private readonly IslandWeatherPage _weather;
    private readonly IslandHomePage _home;
    private readonly StackPanel _pillInfo = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed };
    private int PageCount => FirstExtraPage + _pages.Count;
    private double _wheel;             // wheel movement not yet turned into a page flip (touchpads send small steps)
    private DateTime _wheelQuietUntil; // one flip per gesture
    private readonly DispatcherTimer _clock = new();
    private readonly DispatcherTimer _closeTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private bool _isOpen, _pinned, _hiddenForFullscreen;

    public string DeviceName { get; }

    public IslandWindow(double barHeight, Core.IslandConfig config, string deviceName, IntPtr owner) : base(clickThrough: false)
    {
        DeviceName = deviceName;
        _collapsedHeight = barHeight;
        // Room for the widest and the tallest card; the rest of the window is transparent (click-through).
        Width = Math.Max(OpenWidth, LyricsWidth) + 2 * Flare;
        Height = Math.Max(Math.Max(MediaHeight, LyricsHeight), CalendarHeight);

        // Collapsed content: the time, or (while something plays) a tiny artwork square, the title, three green bars.
        _smallTime = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _pillTitle = new TextBlock { FontSize = 12, FontWeight = FontWeights.Medium, MaxWidth = 106, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        _pillArt = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 7, 0), Background = ArtPlaceholder, VerticalAlignment = VerticalAlignment.Center };
        _pillNowPlaying = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Children = { _pillArt, _pillTitle, Bars() },
        };
        _collapsed = new Grid { Children = { _smallTime, _pillNowPlaying, _pillInfo } };

        // Open content, nothing playing: big time, the date.
        _bigTime = new TextBlock { FontSize = 34, FontWeight = FontWeights.SemiBold };
        _date = new TextBlock { FontSize = 13, Foreground = Muted, Margin = new Thickness(0, 2, 0, 0) };
        var nothing = new TextBlock { Text = "Nothing playing", FontSize = 12, Foreground = Muted, Margin = new Thickness(0, 10, 0, 0) };
        _clockCard = new StackPanel { Children = { _bigTime, _date, nothing } };

        // Open content, playing: artwork + title, artist, app; a progress bar; previous / play-pause / next.
        _title = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        _artist = new TextBlock { FontSize = 13, Foreground = Muted, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
        _app = new TextBlock { FontSize = 11, Foreground = Muted, Margin = new Thickness(0, 2, 0, 0) };
        _playPauseIcon = new Path { Fill = Brushes.White, Data = PlayIcon };
        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
            Children =
            {
                ControlButton(new Path { Fill = Brushes.White, Data = PreviousIcon }, 18, "Previous", () => _media.Previous()),
                ControlButton(_playPauseIcon, 22, "Play or pause", () => _media.TogglePlayPause()),
                ControlButton(new Path { Fill = Brushes.White, Data = NextIcon }, 18, "Next", () => _media.Next()),
            },
        };
        _artIcon = new Viewbox { Width = 28, Height = 28, Child = new Canvas { Width = 24, Height = 24, Children = { new Path { Data = NoteIcon, Stroke = Brushes.White, StrokeThickness = 1.8, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round } } } };
        _art = new Border { Width = 72, Height = 72, CornerRadius = new CornerRadius(12), Background = ArtPlaceholder, Child = _artIcon };
        DockPanel.SetDock(_art, Dock.Left);
        var text = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Children = { _title, _artist, _app } };
        var top = new DockPanel { Children = { _art, text } };

        _progressFill = new Border { Height = 4, Width = 0, CornerRadius = new CornerRadius(2), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left };
        _elapsed = new TextBlock { FontSize = 11, Foreground = Muted };
        _total = new TextBlock { FontSize = 11, Foreground = Muted };
        DockPanel.SetDock(_total, Dock.Right);
        _progress = new StackPanel
        {
            Margin = new Thickness(0, 12, 0, 0),
            Children =
            {
                new Grid { Height = 4, Children = { new Border { CornerRadius = new CornerRadius(2), Background = Track }, _progressFill } },
                new DockPanel { Margin = new Thickness(0, 5, 0, 0), Children = { _total, _elapsed } },
            },
        };
        _mediaCard = new StackPanel { Visibility = Visibility.Collapsed, Children = { top, _progress, controls } };

        // Lyrics card: the cover with small controls under it on the left; title, artist and the lyrics on the right.
        _lyricsArt = new Border { Width = LyricsCover, Height = LyricsCover, CornerRadius = new CornerRadius(12), Background = ArtPlaceholder };
        _lyricsPlayPauseIcon = new Path { Fill = Brushes.White, Data = PlayIcon };
        var lyricsControls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
            Children =
            {
                ControlButton(new Path { Fill = Brushes.White, Data = PreviousIcon }, 14, "Previous", () => _media.Previous(), 36, 28),
                ControlButton(_lyricsPlayPauseIcon, 18, "Play or pause", () => _media.TogglePlayPause(), 36, 28),
                ControlButton(new Path { Fill = Brushes.White, Data = NextIcon }, 14, "Next", () => _media.Next(), 36, 28),
            },
        };
        var left = new StackPanel { Width = LyricsCover, Children = { _lyricsArt, lyricsControls } };
        DockPanel.SetDock(left, Dock.Left);

        _lyricsTitle = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        _lyricsArtist = new TextBlock { FontSize = 12, Foreground = Muted, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
        _lyricsStatus = new TextBlock { FontSize = 13, Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        _lyricLines.RenderTransform = _lyricScroll;
        // The lines sit on a Canvas: a panel taller than its slot gets clipped to that slot by WPF *before* its
        // RenderTransform moves it, so scrolling a StackPanel directly showed an empty area once the song was past
        // the first lines. A Canvas lets the lines keep their full height; only the viewport clips.
        var linesCanvas = new Canvas { Children = { _lyricLines } };
        var viewport = new Grid
        {
            Height = LyricsViewport,
            Margin = new Thickness(0, 8, 0, 0),
            ClipToBounds = true,
            Background = Brushes.Transparent,   // the whole area takes the mouse wheel
            Children = { _lyricsStatus, linesCanvas },
        };
        viewport.SizeChanged += (_, e) => _lyricLines.Width = e.NewSize.Width;   // lines wrap at the viewport's width
        viewport.MouseWheel += (_, e) =>
        {
            // Scroll by hand; following the song resumes 4 s later.
            _manualScrollUntil = DateTime.UtcNow.AddSeconds(4);
            double max = Math.Max(0, _lyricLines.ActualHeight - LyricsViewport / 2);
            _lyricScroll.BeginAnimation(TranslateTransform.YProperty, null);
            _lyricScroll.Y = Math.Clamp(_lyricScroll.Y + e.Delta / 4.0, -max, 0);
            e.Handled = true;
        };
        var right = new StackPanel { Margin = new Thickness(16, 0, 0, 0), Children = { _lyricsTitle, _lyricsArtist, viewport } };
        _lyricsCard = new DockPanel { Visibility = Visibility.Collapsed, Children = { left, right } };

        // Calendar page: "Wednesday 28 October" above the month, today circled. Week start and names follow Windows.
        _calendarTitle = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold };
        _calendarToday = new TextBlock { FontSize = 12, Foreground = Muted, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 2) };
        DockPanel.SetDock(_calendarToday, Dock.Right);
        var weekdays = new System.Windows.Controls.Primitives.UniformGrid { Columns = 7, Margin = new Thickness(0, 8, 0, 2) };
        var culture = CultureInfo.CurrentCulture.DateTimeFormat;
        for (int i = 0; i < 7; i++)
        {
            var day = (DayOfWeek)(((int)culture.FirstDayOfWeek + i) % 7);
            weekdays.Children.Add(new TextBlock { Text = culture.GetShortestDayName(day).ToUpper(CultureInfo.CurrentCulture), FontSize = 10, Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Center });
        }
        _calendarCard = new StackPanel
        {
            Visibility = Visibility.Collapsed,
            Children = { new DockPanel { Children = { _calendarToday, _calendarTitle } }, weekdays, _calendarDays },
        };
        // The other pages, in scroll order. Each starts / stops its own work when shown / hidden.
        _weather = new IslandWeatherPage(config.WeatherCity);
        _shelf = new IslandShelfPage();
        _pages.AddRange([_weather, _shelf, new IslandTogglesPage(), new IslandNetworkPage()]);
        _home = new IslandHomePage(_weather, () => _media.TogglePlayPause());
        _home.PillChanged += UpdatePill;
        _shelf.Dragging += dragging =>
        {
            // Dragging a file out: stay open meanwhile, then close as usual if the mouse has left.
            _pinned = dragging;
            if (!dragging && !IsMouseOver) _closeTimer.Start();
        };
        foreach (var page in _pages) page.PillChanged += UpdatePill;
        for (int i = 0; i < PageCount; i++)
            _pageDots.Children.Add(new Border { Width = 4, Height = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 3, 0, 3), Background = DotOff });

        _open = new Grid { Margin = new Thickness(22, 16, 22, 10), Opacity = 0, Children = { _clockCard, _mediaCard, _lyricsCard, _calendarCard, _pageDots } };
        foreach (var page in _pages.Prepend(_home))
        {
            page.View.Visibility = Visibility.Collapsed;
            page.View.HorizontalAlignment = HorizontalAlignment.Left;
            page.View.VerticalAlignment = VerticalAlignment.Top;
            var openGrid = (Grid)_open;
            openGrid.Children.Insert(openGrid.Children.Count - 1, page.View);   // below the page dots
        }

        _body = new Border
        {
            Background = Brushes.Black,
            CornerRadius = new CornerRadius(0, 0, 10, 10),
            Width = CollapsedWidth,
            Height = _collapsedHeight,
            ClipToBounds = true,
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = new Grid { Children = { _collapsed, _open } },
        };
        // Concave flares joining the body to the top edge (the notch's curve, turned sideways and shrunk).
        var leftFlare = new Path { Data = Geometry.Parse($"M0,0 L{Flare},0 L{Flare},{Flare} Q{Flare},0 0,0 Z"), Fill = Brushes.Black, VerticalAlignment = VerticalAlignment.Top };
        var rightFlare = new Path { Data = Geometry.Parse($"M{Flare},0 L0,0 L0,{Flare} Q0,0 {Flare},0 Z"), Fill = Brushes.Black, VerticalAlignment = VerticalAlignment.Top };
        Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Children = { leftFlare, _body, rightFlare },
        };

        _body.MouseEnter += (_, _) =>
        {
            _closeTimer.Stop();
            Expand(pin: false);
        };
        _body.MouseLeave += (_, _) =>
        {
            if (!_pinned) _closeTimer.Start();
        };
        _body.MouseWheel += OnWheel;   // the lyrics viewport handles its own wheel first (lyrics scrolling)

        // Files dragged onto the island (even the closed pill) open the shelf page and land there when dropped.
        AllowDrop = true;
        _body.DragEnter += (_, e) =>
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            _closeTimer.Stop();
            Expand(pin: false);
            if (_page != ShelfPage) SetPage(ShelfPage);
        };
        _body.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        _body.DragLeave += (_, _) =>
        {
            if (!_pinned) _closeTimer.Start();
        };
        _body.Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0) _shelf.Add(paths);
            e.Handled = true;
        };
        _body.MouseLeftButtonUp += (_, _) =>
        {
            // With a track: the lyrics card, opened and closed by clicks.
            if (HasTrack)
            {
                if (_lyricsOpen) CloseLyrics();
                else OpenLyrics();
                return;
            }
            _pinned = !_pinned;
            if (_pinned) Expand(pin: true);
            else if (!_body.IsMouseOver) Collapse();
        };
        _closeTimer.Tick += (_, _) =>
        {
            _closeTimer.Stop();
            Collapse();
        };

        // The clock ticks once a minute, right after the minute changes (no polling in between).
        _clock.Tick += (_, _) => UpdateTime();
        UpdateTime();
        _media.Changed += UpdateMedia;
        _progressTimer.Tick += (_, _) => UpdateProgress();

        new WindowInteropHelper(this).Owner = owner;   // stays above the menu bar
        DpiChanged += (_, _) => Dispatcher.InvokeAsync(Place, DispatcherPriority.Loaded);
    }

    // Top center of the monitor, over the menu bar (physical pixels).
    public void Place()
    {
        if (Monitors.Find(DeviceName) is not Monitors.Info monitor) return;
        double scale = monitor.Dpi / 96.0;
        int w = (int)Math.Round(Width * scale), h = (int)Math.Round(Height * scale);
        var m = monitor.Bounds;
        if (!IsVisible && !_hiddenForFullscreen) Show();
        WindowStyles.PlaceOnTop(new WindowInteropHelper(this).Handle, new Int32Rect(m.Left + (m.Right - m.Left - w) / 2, m.Top, w, h));
    }

    // Opens the card. `pin` keeps it open until the next click (also used by `--open island`).
    public void Expand(bool pin)
    {
        if (pin) _pinned = true;
        if (_isOpen) return;
        _isOpen = true;
        Animate(OpenWidth, OpenCardHeight, Radius, openOpacity: 1);
        UpdateProgress();
        SyncProgressTimer();
    }

    private bool HasTrack => _media.Current.Active && !string.IsNullOrEmpty(_media.Current.Title);
    private int ShelfPage => FirstExtraPage + _pages.IndexOf(_shelf);
    private IslandPage? CurrentExtraPage => _page == HomePage ? _home : _page >= FirstExtraPage ? _pages[_page - FirstExtraPage] : null;
    private double OpenCardHeight => CurrentExtraPage is IslandPage p ? p.Height + 26 : _page == CalendarPage ? CalendarHeight
        : _lyricsOpen ? LyricsHeight : HasTrack ? MediaHeight : OpenHeight;   // +26 = the card's top and bottom margins

    // ---------------- Pages (mouse wheel) ----------------

    // Wheel down = next page, up = previous. One flip per gesture: deltas add up to a notch (120), then a short
    // quiet time, so a touchpad swipe doesn't race through every page.
    private void OnWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (!_isOpen || _lyricsOpen || DateTime.UtcNow < _wheelQuietUntil) return;
        _wheel += e.Delta;
        if (Math.Abs(_wheel) < 120) return;
        int page = Math.Clamp(_page + (_wheel < 0 ? 1 : -1), 0, PageCount - 1);
        _wheel = 0;
        _wheelQuietUntil = DateTime.UtcNow.AddMilliseconds(350);
        if (page != _page) SetPage(page);
    }

    // For the `--open island-calendar` / `island-weather` / ... developer switches: open, pinned, on that page
    // (1 = home, 2 = calendar, 3 = weather, 4 = shelf, 5 = toggles, 6 = network).
    internal void OpenPageForTest(int page)
    {
        Expand(pin: true);
        SetPage(Math.Clamp(page, 0, PageCount - 1));
    }

    // For the `--open island-focus` developer switch: Home page with a 5-second focus timer running.
    internal void StartFocusForTest()
    {
        OpenPageForTest(HomePage);
        _home.StartFocusForTest();
    }

    private void SetPage(int page)
    {
        CurrentExtraPage?.Hidden();
        _page = page;
        CurrentExtraPage?.Shown();
        if (page == CalendarPage) BuildCalendar();
        ShowCards();
        Animate(OpenWidth, OpenCardHeight, Radius, openOpacity: 1);
        _open.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(180)));
    }

    // This month as a 7-column grid; blanks before the 1st; today in a white circle.
    private void BuildCalendar()
    {
        var today = DateTime.Today;
        var culture = CultureInfo.CurrentCulture;
        string month = today.ToString("MMMM yyyy", culture);
        _calendarTitle.Text = month.Length > 0 ? char.ToUpper(month[0], culture) + month[1..] : month;
        string date = today.ToString("dddd d", culture);
        _calendarToday.Text = date.Length > 0 ? char.ToUpper(date[0], culture) + date[1..] : date;

        _calendarDays.Children.Clear();
        var first = new DateTime(today.Year, today.Month, 1);
        int blanks = ((int)first.DayOfWeek - (int)culture.DateTimeFormat.FirstDayOfWeek + 7) % 7;
        for (int i = 0; i < blanks; i++) _calendarDays.Children.Add(new Border { Height = DayCell });
        for (int d = 1; d <= DateTime.DaysInMonth(today.Year, today.Month); d++)
        {
            bool isToday = d == today.Day;
            _calendarDays.Children.Add(new Border
            {
                Height = DayCell, Width = DayCell, CornerRadius = new CornerRadius(DayCell / 2),
                Background = isToday ? Brushes.White : Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = new TextBlock
                {
                    Text = d.ToString(culture), FontSize = 12, FontWeight = isToday ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = isToday ? Brushes.Black : Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            });
        }
    }

    // For the `--open island-lyrics` developer switch: the lyrics card (needs something playing; else the clock card).
    internal void OpenLyricsForTest(int attempt = 0)
    {
        // At startup the media session answers a moment later: wait for the track (up to ~5 s).
        if (!HasTrack && attempt < 20)
        {
            Dispatcher.InvokeAsync(async () => { await Task.Delay(250); OpenLyricsForTest(attempt + 1); });
            return;
        }
        if (HasTrack) OpenLyrics();
        else Expand(pin: true);
    }

    // The lyrics card stays open (pinned) until the next click.
    private void OpenLyrics()
    {
        _page = 0;
        _lyricsOpen = true;
        _pinned = true;
        _isOpen = true;
        ShowCards();
        Animate(LyricsWidth, LyricsHeight, Radius, openOpacity: 1);
        LoadLyrics();
        UpdateProgress();
        SyncProgressTimer();
    }

    // Looks the current track up (cached per track); ignored if the track changed before the answer came.
    private async void LoadLyrics()
    {
        var m = _media.Current;
        string key = $"{m.App}|{m.Artist}|{m.Title}";
        if (key == _lyricsKey) return;
        _lyricsKey = key;
        ShowLyrics(LyricsService.Lyrics.None, "Looking for lyrics…");
        var found = await _lyricsService.FindAsync(m.Title, m.Artist, m.Album, TimeSpan.FromTicks(m.Duration));
        if (key != _lyricsKey) return;
        string status = found.Lines.Count > 0 ? "" : found.Instrumental ? "Instrumental"
            : found.Unavailable ? "Lyrics service is busy, try again in a moment." : "No lyrics found for this song.";
        if (found.Unavailable) _lyricsKey = "";   // so reopening the card tries again
        ShowLyrics(found, status);
    }

    // One TextBlock per line: dimmed, the current one white (synced lyrics). Plain lyrics are all readable.
    private void ShowLyrics(LyricsService.Lyrics lyrics, string status)
    {
        _lyrics = lyrics;
        _lyricLine = -2;
        _lyricLines.Children.Clear();
        _lyricScroll.BeginAnimation(TranslateTransform.YProperty, null);
        _lyricScroll.Y = 0;
        _lyricsStatus.Text = status;
        _lyricsStatus.Visibility = status.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var line in lyrics.Lines)
        {
            _lyricLines.Children.Add(new TextBlock
            {
                Text = line.Text.Length > 0 ? line.Text : "♪",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Foreground = lyrics.Synced ? LyricDim : Brushes.White,
                Margin = new Thickness(0, 0, 0, 6),
            });
        }
        UpdateLyricLine();
    }

    // Highlights the line being sung and scrolls it a quarter of the way down (one line of context above). Only touches the UI when the
    // line changes (every few seconds), since each change redraws the layered window.
    private void UpdateLyricLine()
    {
        var m = _media.Current;
        if (!_lyricsOpen || !_lyrics.Synced || _lyricLines.Children.Count == 0) return;
        long position = m.Position + (m.Playing ? (DateTime.UtcNow - m.PositionAt).Ticks : 0);
        int line = LyricsService.LineAt(_lyrics, TimeSpan.FromTicks(position));
        if (line == _lyricLine) return;
        if (_lyricLine >= 0 && _lyricLine < _lyricLines.Children.Count)
            ((TextBlock)_lyricLines.Children[_lyricLine]).Foreground = LyricDim;
        _lyricLine = line;
        if (line < 0) return;
        var current = (TextBlock)_lyricLines.Children[line];
        current.Foreground = Brushes.White;
        if (DateTime.UtcNow < _manualScrollUntil) return;
        _lyricLines.UpdateLayout();
        double top = current.TranslatePoint(new Point(0, 0), _lyricLines).Y;
        double target = Math.Min(0, LyricsViewport / 4 - top);
        _lyricScroll.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(target, TimeSpan.FromMilliseconds(300)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    // Back to the compact card while the mouse is still on the island, else closed.
    private void CloseLyrics()
    {
        _lyricsOpen = false;
        _pinned = false;
        ShowCards();
        if (_body.IsMouseOver) Animate(OpenWidth, OpenCardHeight, Radius, openOpacity: 1);
        else Collapse();
        UpdateProgress();
    }

    // Which open card shows: the calendar page, else the clock (nothing playing), the compact track card or the
    // lyrics card. The page dots show on the pages, not on the lyrics card (its wheel scrolls the lyrics).
    private void ShowCards()
    {
        bool active = HasTrack, calendar = _page == CalendarPage, first = _page == 0;
        _calendarCard.Visibility = calendar ? Visibility.Visible : Visibility.Collapsed;
        _clockCard.Visibility = first && !active ? Visibility.Visible : Visibility.Collapsed;
        _mediaCard.Visibility = first && active && !_lyricsOpen ? Visibility.Visible : Visibility.Collapsed;
        _lyricsCard.Visibility = first && active && _lyricsOpen ? Visibility.Visible : Visibility.Collapsed;
        _home.View.Visibility = _page == HomePage ? Visibility.Visible : Visibility.Collapsed;
        for (int i = 0; i < _pages.Count; i++)
            _pages[i].View.Visibility = _page == FirstExtraPage + i ? Visibility.Visible : Visibility.Collapsed;
        _pageDots.Visibility = _lyricsOpen ? Visibility.Collapsed : Visibility.Visible;
        for (int i = 0; i < _pageDots.Children.Count; i++)
            ((Border)_pageDots.Children[i]).Background = i == _page ? Brushes.White : DotOff;
    }

    // For the `--soak` test: unpin and close, as if clicked again.
    internal void ResetForTest()
    {
        _pinned = false;
        _lyricsOpen = false;
        ShowCards();
        Collapse();
    }

    private void Collapse()
    {
        if (!_isOpen || _pinned) return;
        _isOpen = false;
        CurrentExtraPage?.Hidden();   // stops its sampling (network) while closed
        _page = 0;          // closing always returns to the first page
        _wheel = 0;
        ShowCards();
        Animate(CollapsedWidth, _collapsedHeight, 10, openOpacity: 0);
        SyncProgressTimer();
    }

    // Grows or shrinks the pill with an ease-out, cross-fading the small time and the card.
    private void Animate(double width, double height, double radius, double openOpacity)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _body.BeginAnimation(WidthProperty, new DoubleAnimation(width, Motion) { EasingFunction = ease });
        _body.BeginAnimation(HeightProperty, new DoubleAnimation(height, Motion) { EasingFunction = ease });
        _body.CornerRadius = new CornerRadius(0, 0, radius, radius);
        _open.BeginAnimation(OpacityProperty, new DoubleAnimation(openOpacity, Motion));
        _collapsed.BeginAnimation(OpacityProperty, new DoubleAnimation(1 - openOpacity, Motion));
    }

    // Shows the playing track (or the clock again when nothing is playing).
    private void UpdateMedia()
    {
        var m = _media.Current;
        bool active = HasTrack;
        if (!active && _lyricsOpen)
        {
            _lyricsOpen = false;      // the track ended: no lyrics to show
            _pinned = false;
            if (_isOpen) Animate(OpenWidth, OpenHeight, Radius, openOpacity: 1);
        }
        if (_isOpen && Math.Abs(_body.ActualHeight - OpenCardHeight) > 0.5)
            _body.BeginAnimation(HeightProperty, new DoubleAnimation(OpenCardHeight, Motion) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        _pillNowPlaying.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        _home.SetMedia(m, _media.Artwork);
        UpdatePill();
        ShowCards();
        if (!active) return;
        _pillTitle.Text = m.Title;
        _pillNowPlaying.Opacity = m.Playing ? 1 : 0.6;   // paused: dimmed
        _title.Text = m.Title;
        _artist.Text = m.Artist.Length > 0 ? m.Artist : " ";
        _app.Text = m.App;
        _playPauseIcon.Data = m.Playing ? PauseIcon : PlayIcon;
        _lyricsTitle.Text = _title.Text;
        _lyricsArtist.Text = _artist.Text;
        _lyricsPlayPauseIcon.Data = _playPauseIcon.Data;
        if (_lyricsOpen) LoadLyrics();   // the track changed while the lyrics are open

        // Artwork, or the gradient + note placeholder when the app gives none.
        var art = _media.Artwork;
        Brush artBrush = art is null ? ArtPlaceholder : new ImageBrush(art) { Stretch = Stretch.UniformToFill };
        _art.Background = artBrush;
        _pillArt.Background = artBrush;
        _lyricsArt.Background = artBrush;
        _artIcon.Visibility = art is null ? Visibility.Visible : Visibility.Collapsed;

        _progress.Visibility = m.Duration > 0 ? Visibility.Visible : Visibility.Collapsed;   // live streams: no timeline
        UpdateProgress();
        SyncProgressTimer();
    }

    // The bar and times. Windows only says "position X at time T", so while playing: X + (now - T).
    private void UpdateProgress()
    {
        var m = _media.Current;
        if (!_isOpen || m.Duration <= 0) return;
        long position = m.Position + (m.Playing ? (DateTime.UtcNow - m.PositionAt).Ticks : 0);
        position = Math.Clamp(position, 0, m.Duration);
        _progressFill.Width = ContentWidth * position / m.Duration;
        _elapsed.Text = FormatTime(position);
        _total.Text = FormatTime(m.Duration);
        UpdateLyricLine();
    }

    // Ticks once a second only while the card is open and something with a timeline plays (each tick redraws
    // the layered window, so never while closed).
    private void SyncProgressTimer()
    {
        var m = _media.Current;
        bool run = _isOpen && HasTrack && m.Playing && m.Duration > 0;
        // Twice a second while synced lyrics show (to switch lines on time), else once.
        _progressTimer.Interval = TimeSpan.FromMilliseconds(_lyricsOpen && _lyrics.Synced ? 500 : 1000);
        if (run && !_progressTimer.IsEnabled) _progressTimer.Start();
        else if (!run) _progressTimer.Stop();
    }

    private static string FormatTime(long ticks)
    {
        var t = TimeSpan.FromTicks(ticks);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    // Three short green bars, like a frozen equalizer. Not animated on purpose: an animation would redraw this
    // layered window every frame (tens of ms of CPU each), all the time music plays.
    private static FrameworkElement Bars()
    {
        var bars = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        foreach (double h in new[] { 6.0, 10, 7 })
            bars.Children.Add(new Border { Width = 2.5, Height = h, CornerRadius = new CornerRadius(1.25), Background = Playing, Margin = new Thickness(0, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center });
        return bars;
    }

    // A borderless media button (44×32 target by default) holding a solid glyph scaled to `size`.
    private static Button ControlButton(Path icon, double size, string name, Action click, double width = 44, double height = 32)
    {
        var button = new Button
        {
            Style = (Style)Application.Current.FindResource("Shell.DockTile"),
            Width = width, Height = height,
            Content = new Border
            {
                Background = Brushes.Transparent,   // the whole button area is clickable, not just the glyph
                Child = new Viewbox { Width = size, Height = size, Child = new Canvas { Width = 24, Height = 24, Children = { icon } } },
            },
            ToolTip = null,
        };
        System.Windows.Automation.AutomationProperties.SetName(button, name);
        button.Click += (_, _) => click();
        return button;
    }

    private void UpdateTime()
    {
        var now = DateTime.Now;
        _smallTime.Text = now.ToString("HH:mm", CultureInfo.CurrentCulture);
        _bigTime.Text = _smallTime.Text;
        string date = now.ToString("dddd d MMMM", CultureInfo.CurrentCulture);
        _date.Text = date.Length > 0 ? char.ToUpper(date[0], CultureInfo.CurrentCulture) + date[1..] : date;
        _clock.Stop();
        _clock.Interval = TimeSpan.FromSeconds(60 - now.Second) - TimeSpan.FromMilliseconds(now.Millisecond) + TimeSpan.FromMilliseconds(20);
        _clock.Start();
    }

    public void SetHiddenForFullscreen(bool hidden)
    {
        if (hidden == _hiddenForFullscreen) return;
        _hiddenForFullscreen = hidden;
        if (hidden)
        {
            _pinned = false;
            _lyricsOpen = false;
            ShowCards();
            _isOpen = true;   // force the collapse below to run
            Collapse();
            Hide();
        }
        else Place();
    }

    // The closed pill: what's playing, else the focus timer, else files waiting on the shelf, else the weather, else the time.
    private void UpdatePill()
    {
        if (HasTrack)
        {
            _smallTime.Visibility = _pillInfo.Visibility = Visibility.Collapsed;
            return;
        }
        var info = _home.Pill ?? _shelf.Pill ?? _weather.Pill;
        _smallTime.Visibility = info is null ? Visibility.Visible : Visibility.Collapsed;
        _pillInfo.Visibility = info is null ? Visibility.Collapsed : Visibility.Visible;
        if (info is null) return;
        _pillInfo.Children.Clear();
        _pillInfo.Children.Add(new Viewbox
        {
            Width = 13, Height = 13, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new Canvas
            {
                Width = 24, Height = 24,
                Children = { new Path { Data = info.Icon, Stroke = info.Color, StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round } },
            },
        });
        _pillInfo.Children.Add(new TextBlock { Text = info.Text, FontSize = 12, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 130, TextTrimming = TextTrimming.CharacterEllipsis });
    }

    protected override void OnClosed(EventArgs e)
    {
        foreach (var page in _pages) page.Dispose();
        _home.Dispose();
        _clock.Stop();
        _closeTimer.Stop();
        _progressTimer.Stop();
        _media.Dispose();
        base.OnClosed(e);
    }

    private static Brush FrozenGradient(Color from, Color to)
    {
        var brush = new LinearGradientBrush(from, to, 45);
        brush.Freeze();
        return brush;
    }
}
