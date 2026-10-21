using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace MacShell.Native;

// Wi-Fi state and network name, with a Changed event (no polling).
//
// Name, in order of preference:
//   1. The real SSID from the WLAN API (wlanapi.dll). Since Windows 11 24H2 this needs Location services
//      on for desktop apps; otherwise the call fails with ERROR_ACCESS_DENIED.
//   2. The network's name from the Network List Manager (what Settings shows). At home that's usually
//      the SSID; on a company domain network it's the domain (e.g. "INTRA.MA"). No permission needed.
internal sealed class Wifi : IDisposable
{
    public readonly record struct State(bool HasAdapter, bool Connected, string? Name);

    private readonly DispatcherTimer _debounce;

    public event Action? Changed;

    public Wifi()
    {
        // NetworkChange is .NET's wrapper over Windows' address-change notifications. It fires on a background
        // thread, often several times per change: hop to the UI thread and wait for it to settle.
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Changed?.Invoke();
        };
        NetworkChange.NetworkAddressChanged += OnNetworkEvent;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkEvent;
    }

    private void OnNetworkEvent(object? sender, EventArgs e) =>
        _debounce.Dispatcher.InvokeAsync(() =>
        {
            _debounce.Stop();
            _debounce.Start();
        });

    public static State Read()
    {
        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            .ToList();
        if (adapters.Count == 0) return new State(false, false, null);

        var up = adapters.FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up);
        if (up is null || !Guid.TryParse(up.Id, out Guid adapterId)) return new State(true, false, null);

        return new State(true, true, Ssid(adapterId) ?? NetworkListName(adapterId));
    }

    // ---------------- 1. WLAN API ----------------

    private const int WlanCurrentConnection = 7;   // wlan_intf_opcode_current_connection
    private const int SsidLengthOffset = 520;      // in WLAN_CONNECTION_ATTRIBUTES: 2 ints + WCHAR[256] profile name
    private const int SsidBytesOffset = 524;

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr handle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);

    // Reads one property of a Wi-Fi adapter; Windows allocates `data`, we free it with WlanFreeMemory.
    [DllImport("wlanapi.dll")]
    private static extern uint WlanQueryInterface(IntPtr handle, ref Guid interfaceGuid, int opCode, IntPtr reserved,
        out int dataSize, out IntPtr data, out int valueType);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);

    private static string? Ssid(Guid adapterId)
    {
        if (WlanOpenHandle(2, IntPtr.Zero, out _, out IntPtr handle) != 0) return null;
        try
        {
            // Fails with 5 (access denied) when Location is off: fall back to the network name.
            if (WlanQueryInterface(handle, ref adapterId, WlanCurrentConnection, IntPtr.Zero, out int size, out IntPtr data, out _) != 0)
                return null;
            try
            {
                if (size < SsidBytesOffset + 32) return null;
                int length = Math.Clamp(Marshal.ReadInt32(data, SsidLengthOffset), 0, 32);
                var bytes = new byte[length];
                Marshal.Copy(data + SsidBytesOffset, bytes, 0, length);
                string ssid = Encoding.UTF8.GetString(bytes);
                return ssid.Length > 0 ? ssid : null;
            }
            finally
            {
                WlanFreeMemory(data);
            }
        }
        finally
        {
            WlanCloseHandle(handle, IntPtr.Zero);
        }
    }

    // ---------------- 2. Network List Manager (COM) ----------------
    // COM interfaces are declared as C# interfaces with their GUIDs; methods must be listed in the exact
    // order of the C++ vtable (only the ones up to the last one we call). Errors become exceptions.

    [ComImport, Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkListManager
    {
        object GetNetworks(int flags);                       // unused, but holds its vtable slot
        INetwork GetNetwork(Guid networkId);                 // unused
        IEnumNetworkConnections GetNetworkConnections();
    }

    [ComImport, Guid("DCB00006-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IEnumNetworkConnections
    {
        object NewEnum();                                    // get__NewEnum, unused
        [PreserveSig] int Next(uint count, out INetworkConnection connection, out uint fetched);   // S_FALSE (1) = done
    }

    [ComImport, Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkConnection
    {
        INetwork GetNetwork();
        short IsConnectedToInternet();
        short IsConnected();
        int GetConnectivity();
        Guid GetConnectionId();
        Guid GetAdapterId();
    }

    [ComImport, Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetwork
    {
        [return: MarshalAs(UnmanagedType.BStr)] string GetName();
    }

    private static readonly Guid NetworkListManagerClass = new("DCB00C01-570F-4A9B-8D69-199FDBA5723B");

    private static string? NetworkListName(Guid adapterId)
    {
        object? manager = null;
        try
        {
            manager = Activator.CreateInstance(Type.GetTypeFromCLSID(NetworkListManagerClass)!);
            var connections = ((INetworkListManager)manager!).GetNetworkConnections();
            while (connections.Next(1, out var connection, out uint fetched) == 0 && fetched == 1)
            {
                if (connection.GetAdapterId() == adapterId)
                    return connection.GetNetwork().GetName();
            }
            return null;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (manager is not null) Marshal.ReleaseComObject(manager);
        }
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkEvent;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkEvent;
        _debounce.Stop();
    }
}
