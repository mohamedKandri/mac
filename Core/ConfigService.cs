using System.IO;
using System.Windows.Threading;

namespace MacShell.Core;

// Owns config.json: loads it at start, reloads it when the file is saved, and keeps the last valid
// config whenever the file is broken. Events always fire on the UI thread.
public sealed class ConfigService : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly DispatcherTimer _debounce;
    private string? _lastText;

    public string FilePath { get; }

    // The config in use. Starts as the built-in defaults; replaced only by a config that parsed and validated.
    public AppConfig Current { get; private set; } = new();

    public event Action<AppConfig>? Changed;
    public event Action<string>? Failed;     // the error message; Current stays as it was

    private readonly Func<WidgetEntry, string?> _checkWidget;

    public ConfigService(string filePath, Func<WidgetEntry, string?> checkWidget)
    {
        FilePath = filePath;
        _checkWidget = checkWidget;

        // A DispatcherTimer ticks on the UI thread (like a Handler.postDelayed on Android's main looper).
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Reload(force: false);
        };

        // Watches the folder for changes to config.json. Editors save differently: some write in place,
        // others write a temp file and rename it, so listen to all three events.
        _watcher = new FileSystemWatcher(Path.GetDirectoryName(filePath)!, Path.GetFileName(filePath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        _watcher.Changed += OnFileEvent;
        _watcher.Created += OnFileEvent;
        _watcher.Renamed += OnFileEvent;
    }

    // Loads the file (creating it with defaults if missing), then starts watching.
    // Separate from the constructor so callers can subscribe to the events first.
    public void Start()
    {
        if (!File.Exists(FilePath))
        {
            try
            {
                File.WriteAllText(FilePath, ConfigLoader.DefaultJson());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Failed?.Invoke($"Could not create config.json: {ex.Message}");
            }
        }

        Reload(force: true);
        _watcher.EnableRaisingEvents = true;
    }

    // Reads and applies the file. `force` also reports problems when the text didn't change (used by the tray's
    // "Reload config"). Returns true when a valid config was applied.
    public bool Reload(bool force)
    {
        string? text = TryRead();
        if (text is null)
        {
            // Missing or locked file: keep the current config. Only complain when asked explicitly.
            if (force) Failed?.Invoke("Could not read config.json (missing, or locked by another program).");
            return false;
        }

        // A single save often fires several events with the same content: skip repeats.
        if (!force && text == _lastText) return false;
        _lastText = text;

        var (config, error) = ConfigLoader.Parse(text, _checkWidget);
        if (config is null)
        {
            Failed?.Invoke(error!);
            return false;
        }

        Current = config;
        Changed?.Invoke(config);
        return true;
    }

    // FileSystemWatcher calls this on a background thread, often several times per save.
    // Hop to the UI thread and restart the timer, so we reload once, 300 ms after the last event.
    private void OnFileEvent(object sender, FileSystemEventArgs e) =>
        _debounce.Dispatcher.InvokeAsync(() =>
        {
            _debounce.Stop();
            _debounce.Start();
        });

    // The editor may still hold the file open for a moment after saving, so retry briefly.
    private string? TryRead()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                // FileShare.ReadWrite | Delete: don't block the editor while we read.
                using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }
        return null;
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _debounce.Stop();
    }
}
