using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace MacShell.Native;

// Master volume of the default output device (Core Audio, the API behind Windows' own volume slider),
// with a Changed event for volume/mute changes made anywhere and for switching output devices.
//
// Core Audio is COM: C# interfaces with [ComImport] + the interface GUID describe the C++ vtables (methods in
// exact order). [PreserveSig] keeps the raw HRESULT (int) instead of throwing, e.g. when there are no speakers.
internal sealed class Audio : IDisposable
{
    public readonly record struct State(bool HasDevice, float Level, bool Muted);   // Level: 0..1

    private const int ERender = 0;           // output devices (not microphones)
    private const int EMultimedia = 1;       // the "default device" role used for media
    private const int ClsctxAll = 23;

    private readonly Dispatcher _dispatcher;
    private readonly IMMDeviceEnumerator _enumerator;
    private readonly VolumeCallback _volumeCallback;
    private readonly DeviceCallback _deviceCallback;
    private IAudioEndpointVolume? _volume;

    public event Action? Changed;   // always raised on the UI thread

    public Audio(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _volumeCallback = new VolumeCallback(this);
        _deviceCallback = new DeviceCallback(this);
        _enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;
        _enumerator.RegisterEndpointNotificationCallback(_deviceCallback);
        Bind();
    }

    public State Read()
    {
        if (_volume is null) return new State(false, 0, false);
        if (_volume.GetMasterVolumeLevelScalar(out float level) != 0 || _volume.GetMute(out bool muted) != 0)
            return new State(false, 0, false);
        return new State(true, level, muted);
    }

    public void SetLevel(float level)
    {
        if (_volume is null) return;
        var context = Guid.Empty;
        _volume.SetMasterVolumeLevelScalar(Math.Clamp(level, 0f, 1f), ref context);
        if (level > 0) _volume.SetMute(false, ref context);   // like Windows: turning it up unmutes
    }

    // (Re)attaches to the current default output device.
    private void Bind()
    {
        Unbind();
        if (_enumerator.GetDefaultAudioEndpoint(ERender, EMultimedia, out var device) != 0) return;   // no speakers
        var iid = typeof(IAudioEndpointVolume).GUID;
        if (device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out object volume) != 0) return;
        _volume = (IAudioEndpointVolume)volume;
        _volume.RegisterControlChangeNotify(_volumeCallback);
    }

    private void Unbind()
    {
        if (_volume is null) return;
        _volume.UnregisterControlChangeNotify(_volumeCallback);
        Marshal.ReleaseComObject(_volume);
        _volume = null;
    }

    // COM calls our callbacks on its own background threads: hop to the UI thread.
    private void RaiseChanged(bool rebind) => _dispatcher.InvokeAsync(() =>
    {
        if (rebind) Bind();
        Changed?.Invoke();
    });

    public void Dispose()
    {
        _enumerator.UnregisterEndpointNotificationCallback(_deviceCallback);
        Unbind();
        Marshal.ReleaseComObject(_enumerator);
    }

    // ---------------- Callbacks we implement (Windows calls into these) ----------------

    [ComVisible(true)]
    private sealed class VolumeCallback(Audio owner) : IAudioEndpointVolumeCallback
    {
        public int OnNotify(IntPtr data)
        {
            owner.RaiseChanged(rebind: false);
            return 0;
        }
    }

    [ComVisible(true)]
    private sealed class DeviceCallback(Audio owner) : IMMNotificationClient
    {
        public int OnDefaultDeviceChanged(int flow, int role, string? id)
        {
            if (flow == ERender && role == EMultimedia) owner.RaiseChanged(rebind: true);
            return 0;
        }

        public int OnDeviceStateChanged(string id, int state) => 0;
        public int OnDeviceAdded(string id) => 0;
        public int OnDeviceRemoved(string id) => 0;
        public int OnPropertyValueChanged(string id, PropertyKey key) => 0;
    }

    // ---------------- COM interface declarations ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public int PropertyId;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
        [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolumeCallback
    {
        [PreserveSig] int OnNotify(IntPtr data);
    }

    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, int state);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? id);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PropertyKey key);
    }
}
