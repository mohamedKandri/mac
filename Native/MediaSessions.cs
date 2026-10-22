using System.Windows.Threading;

namespace MacShell.Native;

// "What's playing": the same source as the media flyout of Windows' volume keys. Any app that reports its media
// to Windows shows up (Spotify, the Media Player, YouTube or Netflix in Chrome/Edge/Firefox...).
// API: Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager (WinRT, called by hand: see WinRt).
// Event-driven: the manager says when the current session changes, the session says when its track or play state
// changes. `Changed` is raised on the UI thread; `Current` is the latest state.
internal sealed class MediaSessions : IDisposable
{
    public readonly record struct State(bool Active, string Title, string Artist, string App, bool Playing)
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

    private const int Statics_RequestAsync = 6;
    private const int Manager_GetCurrentSession = 6, Manager_AddCurrentSessionChanged = 8, Manager_RemoveCurrentSessionChanged = 9;
    private const int Session_SourceAppUserModelId = 6, Session_TryGetMediaPropertiesAsync = 7, Session_GetPlaybackInfo = 9;
    private const int Session_TrySkipNextAsync = 16, Session_TrySkipPreviousAsync = 17, Session_TryTogglePlayPauseAsync = 20;
    private const int Session_AddPlaybackInfoChanged = 27, Session_RemovePlaybackInfoChanged = 28;
    private const int Session_AddMediaPropertiesChanged = 29, Session_RemoveMediaPropertiesChanged = 30;
    private const int Properties_Title = 6, Properties_Artist = 9;
    private const int PlaybackInfo_Status = 7;
    private const int StatusPlaying = 4;   // GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private IntPtr _manager, _session;
    private long _sessionToken, _playbackToken, _propertiesToken;
    private WinRt.Handler? _sessionHandler, _playbackHandler, _propertiesHandler;
    private bool _refreshQueued, _disposed;
    private int _generation;   // ignores a slow properties lookup for a session that's gone meanwhile

    public State Current { get; private set; } = State.Nothing;

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
            Publish(State.Nothing);
            return;
        }

        IntPtr info = WinRt.GetPointer(_session, Session_GetPlaybackInfo);
        bool playing = info != IntPtr.Zero && WinRt.GetInt(info, PlaybackInfo_Status, 0) == StatusPlaying;
        WinRt.Release(info);
        string app = AppName(WinRt.TakeString(WinRt.GetPointer(_session, Session_SourceAppUserModelId)));

        // Title and artist come from an async call; keep showing the old ones until it answers.
        Publish(Current with { Active = true, App = app, Playing = playing });
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
            WinRt.Release(properties);
            Publish(Current with { Title = title, Artist = artist });
        });
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
        _playbackToken = _propertiesToken = 0;
        _playbackHandler?.Dispose();
        _propertiesHandler?.Dispose();
        _playbackHandler = _propertiesHandler = null;
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
