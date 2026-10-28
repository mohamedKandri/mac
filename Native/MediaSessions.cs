using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MacShell.Native;

// "What's playing": the same source as the media flyout of Windows' volume keys. Any app that reports its media
// to Windows shows up (Spotify, the Media Player, YouTube or Netflix in Chrome/Edge/Firefox...).
// API: Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager (WinRT, called by hand: see WinRt).
// Event-driven: the manager says when the current session changes, the session says when its track or play state
// changes. `Changed` is raised on the UI thread; `Current` is the latest state.
internal sealed class MediaSessions : IDisposable
{
    // Position/Duration: TimeSpan ticks. PositionAt: when Windows measured Position (UTC); while playing the real
    // position is Position + (now - PositionAt). Duration 0 = the app doesn't report a timeline (live streams...).
    public readonly record struct State(bool Active, string Title, string Artist, string App, bool Playing,
        long Position = 0, long Duration = 0, DateTime PositionAt = default, string Album = "")
    {
        // Nothing playing. Not `default`: a default struct has null strings, and the title is read before the
        // async track lookup has answered (that crashed MacShell at start while something was paused).
        public static readonly State Nothing = new(false, "", "", "", false);
    }

    // IIDs and vtable slots from Include\10.0.26100.0\winrt\windows.media.control.h.
    private const string ManagerClass = "Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager";
    private static readonly Guid IID_ManagerStatics = new("2050c4ee-11a0-57de-aed7-c97c70338245");
    private static readonly Guid IID_CurrentSessionChangedHandler = new("228bd0ed-1fa2-5e9b-a6ec-42566173103b");
    private static readonly Guid IID_PlaybackInfoChangedHandler = new("2bdf1426-d41f-5896-897f-efc0b0fa7392");
    private static readonly Guid IID_MediaPropertiesChangedHandler = new("0f2ce2b7-afa7-5ed0-8cb6-8c40cf9b3a5f");
    private static readonly Guid IID_TimelinePropertiesChangedHandler = new("e8bf62af-fac1-5fff-9053-0bf191ae777e");

    private const int Statics_RequestAsync = 6;
    private const int Manager_GetCurrentSession = 6, Manager_AddCurrentSessionChanged = 8, Manager_RemoveCurrentSessionChanged = 9;
    private const int Session_SourceAppUserModelId = 6, Session_TryGetMediaPropertiesAsync = 7, Session_GetTimelineProperties = 8, Session_GetPlaybackInfo = 9;
    private const int Session_AddTimelinePropertiesChanged = 25, Session_RemoveTimelinePropertiesChanged = 26;
    private const int Timeline_StartTime = 6, Timeline_EndTime = 7, Timeline_Position = 10, Timeline_LastUpdatedTime = 11;
    private const int Properties_Thumbnail = 15, StreamReference_OpenReadAsync = 6;
    private const int MaxArtworkBytes = 4 * 1024 * 1024;
    private const int Session_TrySkipNextAsync = 16, Session_TrySkipPreviousAsync = 17, Session_TryTogglePlayPauseAsync = 20;
    private const int Session_AddPlaybackInfoChanged = 27, Session_RemovePlaybackInfoChanged = 28;
    private const int Session_AddMediaPropertiesChanged = 29, Session_RemoveMediaPropertiesChanged = 30;
    private const int Properties_Title = 6, Properties_Artist = 9, Properties_AlbumTitle = 10;
    private const int PlaybackInfo_Status = 7;
    private const int StatusPlaying = 4;   // GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private IntPtr _manager, _session;
    private long _sessionToken, _playbackToken, _propertiesToken, _timelineToken;
    private WinRt.Handler? _sessionHandler, _playbackHandler, _propertiesHandler, _timelineHandler;
    private string _artworkKey = "";
    private bool _refreshQueued, _disposed;
    private int _generation;   // ignores a slow properties lookup for a session that's gone meanwhile

    public State Current { get; private set; } = State.Nothing;

    // The track's album art / video thumbnail, small (decoded at 96 px). Null if the app gives none.
    public ImageSource? Artwork { get; private set; }

    public event Action? Changed;

    // Must be created on the UI thread. Never throws: if Windows refuses, it just stays "nothing playing".
    public MediaSessions()
    {
        IntPtr statics = WinRt.GetStatics(ManagerClass, IID_ManagerStatics);
        if (statics == IntPtr.Zero) return;
        IntPtr request = WinRt.GetPointer(statics, Statics_RequestAsync);
        WinRt.Release(statics);
        WinRt.WhenDone(request, manager =>
        {
            if (_disposed || manager == IntPtr.Zero)
            {
                WinRt.Release(manager);
                return;
            }
            _manager = manager;
            _sessionHandler = new WinRt.Handler(IID_CurrentSessionChangedHandler, () => _dispatcher.InvokeAsync(SwitchSession));
            _sessionToken = WinRt.AddHandler(_manager, Manager_AddCurrentSessionChanged, _sessionHandler.Pointer);
            SwitchSession();
        });
    }

    public void TogglePlayPause() => Send(Session_TryTogglePlayPauseAsync);
    public void Next() => Send(Session_TrySkipNextAsync);
    public void Previous() => Send(Session_TrySkipPreviousAsync);

    // Fire and forget: the app's reaction comes back through PlaybackInfoChanged / MediaPropertiesChanged.
    private void Send(int slot)
    {
        if (_session != IntPtr.Zero) WinRt.Release(WinRt.GetPointer(_session, slot));
    }

    // The "current" session (the one Windows' media flyout shows) changed: follow the new one.
    private void SwitchSession()
    {
        if (_disposed || _manager == IntPtr.Zero) return;
        Unhook();
        _generation++;
        _session = WinRt.GetPointer(_manager, Manager_GetCurrentSession);   // Zero = nothing playing anywhere
        if (_session != IntPtr.Zero)
        {
            _playbackHandler = new WinRt.Handler(IID_PlaybackInfoChangedHandler, QueueRefresh);
            _propertiesHandler = new WinRt.Handler(IID_MediaPropertiesChangedHandler, QueueRefresh);
            _playbackToken = WinRt.AddHandler(_session, Session_AddPlaybackInfoChanged, _playbackHandler.Pointer);
            _propertiesToken = WinRt.AddHandler(_session, Session_AddMediaPropertiesChanged, _propertiesHandler.Pointer);
            _timelineHandler = new WinRt.Handler(IID_TimelinePropertiesChangedHandler, () => _dispatcher.InvokeAsync(RefreshTimeline));
            _timelineToken = WinRt.AddHandler(_session, Session_AddTimelinePropertiesChanged, _timelineHandler.Pointer);
        }
        Refresh();
    }

    // Called on Windows' threads, often several times in a row (a track change sends a few events): one refresh.
    private void QueueRefresh()
    {
        lock (this)
        {
            if (_refreshQueued) return;
            _refreshQueued = true;
        }
        _dispatcher.InvokeAsync(() =>
        {
            lock (this) _refreshQueued = false;
            Refresh();
        }, DispatcherPriority.Background);
    }

    private void Refresh()
    {
        if (_disposed) return;
        if (_session == IntPtr.Zero)
        {
            SetArtwork("", null);
            Publish(State.Nothing);
            return;
        }

        IntPtr info = WinRt.GetPointer(_session, Session_GetPlaybackInfo);
        bool playing = info != IntPtr.Zero && WinRt.GetInt(info, PlaybackInfo_Status, 0) == StatusPlaying;
        WinRt.Release(info);
        string app = AppName(WinRt.TakeString(WinRt.GetPointer(_session, Session_SourceAppUserModelId)));

        // Title and artist come from an async call; keep showing the old ones until it answers.
        Publish(ReadTimeline(Current with { Active = true, App = app, Playing = playing }));
        int generation = _generation;
        WinRt.WhenDone(WinRt.GetPointer(_session, Session_TryGetMediaPropertiesAsync), properties =>
        {
            if (properties == IntPtr.Zero || _disposed || generation != _generation)
            {
                WinRt.Release(properties);
                return;
            }
            string title = WinRt.TakeString(WinRt.GetPointer(properties, Properties_Title));
            string artist = WinRt.TakeString(WinRt.GetPointer(properties, Properties_Artist));
            string album = WinRt.TakeString(WinRt.GetPointer(properties, Properties_AlbumTitle));
            // Artwork only when the track changed: reading and decoding it is the expensive part.
            string key = app + "|" + title + "|" + artist;
            if (key != _artworkKey) LoadArtwork(key, WinRt.GetPointer(properties, Properties_Thumbnail), generation);
            WinRt.Release(properties);
            Publish(Current with { Title = title, Artist = artist, Album = album });
        });
    }

    // The app reported a new position (seek, new track, or its periodic update).
    private void RefreshTimeline()
    {
        if (!_disposed && _session != IntPtr.Zero) Publish(ReadTimeline(Current));
    }

    private State ReadTimeline(State state)
    {
        IntPtr timeline = WinRt.GetPointer(_session, Session_GetTimelineProperties);
        if (timeline == IntPtr.Zero) return state with { Position = 0, Duration = 0 };
        long start = WinRt.GetInt64(timeline, Timeline_StartTime, 0);
        long end = WinRt.GetInt64(timeline, Timeline_EndTime, 0);
        long position = WinRt.GetInt64(timeline, Timeline_Position, 0);
        long updated = WinRt.GetInt64(timeline, Timeline_LastUpdatedTime, 0);   // FILETIME: 100 ns since 1601, UTC
        WinRt.Release(timeline);
        DateTime at = updated > 0 ? DateTime.FromFileTimeUtc(updated) : DateTime.UtcNow;
        return state with { Position = Math.Max(0, position - start), Duration = Math.Max(0, end - start), PositionAt = at };
    }

    // Thumbnail (IRandomAccessStreamReference) -> OpenReadAsync -> bytes -> a small frozen bitmap.
    private void LoadArtwork(string key, IntPtr reference, int generation)
    {
        _artworkKey = key;
        if (reference == IntPtr.Zero)
        {
            SetArtwork(key, null);
            return;
        }
        IntPtr open = WinRt.GetPointer(reference, StreamReference_OpenReadAsync);
        WinRt.Release(reference);
        WinRt.WhenDone(open, stream =>
        {
            byte[]? bytes = stream == IntPtr.Zero ? null : WinRt.ReadAll(stream, MaxArtworkBytes);
            WinRt.Release(stream);
            if (_disposed || generation != _generation || key != _artworkKey) return;
            SetArtwork(key, bytes is null ? null : Decode(bytes));
        });
    }

    private static ImageSource? Decode(byte[] bytes)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = new MemoryStream(bytes);
            image.DecodePixelWidth = 96;                   // shown at 72 DIP at most: decode small, not the full-size cover
            image.CacheOption = BitmapCacheOption.OnLoad;   // read now, so the stream can go
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception) { return null; }   // not an image WPF can decode
    }

    private void SetArtwork(string key, ImageSource? artwork)
    {
        _artworkKey = key;
        if (ReferenceEquals(artwork, Artwork)) return;
        Artwork = artwork;
        Changed?.Invoke();
    }

    private void Publish(State state)
    {
        if (state == Current) return;
        Current = state;
        Changed?.Invoke();
    }

    // "Spotify.exe" -> "Spotify", "chrome" -> "Chrome",
    // "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic" (a Store app ID) -> "ZuneMusic",
    // "Chrome.NW5ODL574P26URTNGYOEG35L6Q" (Chrome's per-profile ID) -> "Chrome": the last dotted part that isn't
    // a long generated ID.
    private static string AppName(string appId)
    {
        string name = appId.Contains('!') ? appId[(appId.LastIndexOf('!') + 1)..] : appId;
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        static bool GeneratedId(string part) => part.Length >= 16 && part.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c));
        name = name.Split('.').LastOrDefault(part => part.Length > 0 && !GeneratedId(part)) ?? name;
        return name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];
    }

    private void Unhook()
    {
        WinRt.RemoveHandler(_session, Session_RemovePlaybackInfoChanged, _playbackToken);
        WinRt.RemoveHandler(_session, Session_RemoveMediaPropertiesChanged, _propertiesToken);
        WinRt.RemoveHandler(_session, Session_RemoveTimelinePropertiesChanged, _timelineToken);
        _playbackToken = _propertiesToken = _timelineToken = 0;
        _playbackHandler?.Dispose();
        _propertiesHandler?.Dispose();
        _timelineHandler?.Dispose();
        _playbackHandler = _propertiesHandler = _timelineHandler = null;
        WinRt.Release(_session);
        _session = IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unhook();
        WinRt.RemoveHandler(_manager, Manager_RemoveCurrentSessionChanged, _sessionToken);
        _sessionHandler?.Dispose();
        WinRt.Release(_manager);
        _manager = IntPtr.Zero;
    }
}
