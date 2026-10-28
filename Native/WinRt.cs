using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace MacShell.Native;

// Minimal plumbing for calling Windows Runtime (WinRT) APIs without the Windows SDK projection.
//
// Why by hand: modern Windows APIs (like "what's playing") are WinRT, and .NET 8 can only call them through the
// Windows SDK projection package, which adds ~25 MB to the exe and a few MB of RAM. Underneath, a WinRT object is
// just a COM object: a pointer to a table of function pointers (the "vtable"). Slots 0-2 are IUnknown
// (QueryInterface, AddRef, Release), 3-5 are IInspectable, and an interface's own methods start at slot 6 in the
// order of the Windows SDK header (Include\<ver>\winrt\*.h). So we call methods by slot number.
// Every object pointer we get back is "owned": it must be Released exactly once.
internal static unsafe class WinRt
{
    public const int S_OK = 0;

    // ---------------- Strings (HSTRING) ----------------

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string source, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern char* WindowsGetStringRawBuffer(IntPtr hstring, out uint length);

    // Reads and frees an HSTRING returned by a WinRT method (null HSTRING = empty string).
    public static string TakeString(IntPtr hstring)
    {
        if (hstring == IntPtr.Zero) return "";
        char* chars = WindowsGetStringRawBuffer(hstring, out uint length);
        string text = new(chars, 0, (int)length);
        WindowsDeleteString(hstring);
        return text;
    }

    // ---------------- Activation ----------------

    // Gets a WinRT class's "statics" object (its static methods), by class name and interface IID.
    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr classId, in Guid iid, out IntPtr factory);

    public static IntPtr GetStatics(string className, Guid iid)
    {
        if (WindowsCreateString(className, className.Length, out IntPtr name) != S_OK) return IntPtr.Zero;
        try { return RoGetActivationFactory(name, iid, out IntPtr factory) == S_OK ? factory : IntPtr.Zero; }
        finally { WindowsDeleteString(name); }
    }

    // ---------------- Calling methods by vtable slot ----------------

    private static IntPtr Slot(IntPtr obj, int slot) => (*(IntPtr**)obj)[slot];

    public static void Release(IntPtr obj)
    {
        if (obj != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(obj, 2))(obj);
    }

    public static int QueryInterface(IntPtr obj, Guid iid, out IntPtr result)
    {
        fixed (IntPtr* r = &result)
            return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Slot(obj, 0))(obj, &iid, r);
    }

    // A method with no arguments that returns an object or HSTRING (both pointer-sized). Zero on failure.
    public static IntPtr GetPointer(IntPtr obj, int slot)
    {
        IntPtr result = IntPtr.Zero;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(obj, slot))(obj, &result);
        return hr == S_OK ? result : IntPtr.Zero;
    }

    // A method taking one int / enum argument and returning an object (e.g. SetStateAsync(state) -> async operation).
    public static IntPtr CallWithInt(IntPtr obj, int slot, int argument)
    {
        IntPtr result = IntPtr.Zero;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr*, int>)Slot(obj, slot))(obj, argument, &result);
        return hr == S_OK ? result : IntPtr.Zero;
    }

    // IVectorView<T>: Size (slot 7) and GetAt(index) (slot 6). Returns owned objects.
    public static List<IntPtr> VectorItems(IntPtr vector)
    {
        var items = new List<IntPtr>();
        if (vector == IntPtr.Zero) return items;
        uint size = (uint)GetInt(vector, 7, 0);
        for (uint i = 0; i < size; i++)
        {
            IntPtr item = IntPtr.Zero;
            if (((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(vector, 6))(vector, i, &item) == S_OK && item != IntPtr.Zero)
                items.Add(item);
        }
        return items;
    }

    // A method with no arguments that returns an int or enum.
    public static int GetInt(IntPtr obj, int slot, int fallback)
    {
        int result = fallback;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Slot(obj, slot))(obj, &result);
        return hr == S_OK ? result : fallback;
    }

    // A method with no arguments that returns a 64-bit value (TimeSpan / DateTime: 100 ns ticks).
    public static long GetInt64(IntPtr obj, int slot, long fallback)
    {
        long result = fallback;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, long*, int>)Slot(obj, slot))(obj, &result);
        return hr == S_OK ? result : fallback;
    }

    // ---------------- Streams ----------------

    // Turns a WinRT IRandomAccessStream into a classic COM IStream (documented shcore helper), so it can be read.
    [DllImport("shcore.dll")]
    private static extern int CreateStreamOverRandomAccessStream(IntPtr randomAccessStream, in Guid riid, out IntPtr stream);

    private static readonly Guid IID_IStream = new("0000000C-0000-0000-C000-000000000046");

    // Reads a whole WinRT stream (at most `maxBytes`). Null if it can't be read. Doesn't take ownership.
    public static byte[]? ReadAll(IntPtr randomAccessStream, int maxBytes)
    {
        if (CreateStreamOverRandomAccessStream(randomAccessStream, IID_IStream, out IntPtr stream) != S_OK) return null;
        try
        {
            var data = new MemoryStream();
            var buffer = new byte[64 * 1024];
            fixed (byte* p = buffer)
            {
                while (data.Length < maxBytes)
                {
                    uint read = 0;
                    // IStream::Read(buffer, size, out bytesRead) is slot 3 (right after IUnknown).
                    int hr = ((delegate* unmanaged[Stdcall]<IntPtr, byte*, uint, uint*, int>)Slot(stream, 3))(stream, p, (uint)buffer.Length, &read);
                    if (hr < 0) return null;
                    if (read == 0) break;
                    data.Write(buffer, 0, (int)read);
                }
            }
            return data.Length < maxBytes ? data.ToArray() : null;
        }
        finally { Release(stream); }
    }

    // add_SomethingChanged(handler, out token). Returns the token, or 0 if it failed.
    public static long AddHandler(IntPtr obj, int slot, IntPtr handler)
    {
        long token = 0;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, long*, int>)Slot(obj, slot))(obj, handler, &token);
        return hr == S_OK ? token : 0;
    }

    // remove_SomethingChanged(token).
    public static void RemoveHandler(IntPtr obj, int slot, long token)
    {
        if (obj != IntPtr.Zero && token != 0)
            ((delegate* unmanaged[Stdcall]<IntPtr, long, int>)Slot(obj, slot))(obj, token);
    }

    // ---------------- Async operations ----------------

    // Every IAsyncOperation also implements IAsyncInfo: slot 7 = Status (0 started, 1 completed, 2 canceled, 3 error).
    private static readonly Guid IID_IAsyncInfo = new("00000036-0000-0000-C000-000000000046");
    private const int AsyncInfo_Status = 7, AsyncOperation_GetResults = 8;

    // Waits (without blocking the UI thread) for an IAsyncOperation<object> to finish, then calls `done` with its
    // result (owned by the callee; Zero if it failed). Takes ownership of `operation`.
    // Polls the status every 25 ms for at most 5 s: these operations finish in a few ms, and a real completion
    // handler would need another hand-made COM object per result type.
    public static void WhenDone(IntPtr operation, Action<IntPtr> done)
    {
        if (operation == IntPtr.Zero || QueryInterface(operation, IID_IAsyncInfo, out IntPtr info) != S_OK)
        {
            Release(operation);
            done(IntPtr.Zero);
            return;
        }
        var started = DateTime.UtcNow;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) =>
        {
            int status = GetInt(info, AsyncInfo_Status, 3);
            if (status == 0 && DateTime.UtcNow - started < TimeSpan.FromSeconds(5)) return;
            timer.Stop();
            IntPtr result = status == 1 ? GetPointer(operation, AsyncOperation_GetResults) : IntPtr.Zero;
            Release(info);
            Release(operation);
            done(result);
        };
        timer.Start();
    }

    // ---------------- Event handlers (a COM object of our own) ----------------

    // WinRT events take a delegate object: a COM object with QueryInterface/AddRef/Release + Invoke(sender, args).
    // This builds one in native memory around a C# callback. Windows calls Invoke on its own threads, so the
    // callback must not touch the UI directly (MediaSessions posts to the dispatcher).
    // Layout of the native block: [vtable pointer][ref count][GCHandle of the callback][the delegate's IID].
    public sealed class Handler : IDisposable
    {
        private static readonly IntPtr* VTable = CreateVTable();
        private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
        private static readonly Guid IID_IAgileObject = new("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90");   // "callable from any thread"

        private struct Block
        {
            public IntPtr VTable;
            public int RefCount;
            public IntPtr Callback;   // GCHandle to the Action
            public Guid Iid;
        }

        // The COM pointer to pass to add_SomethingChanged.
        public IntPtr Pointer { get; }

        // `iid` = the parameterized delegate's IID, e.g. TypedEventHandler<Session, PlaybackInfoChangedEventArgs>
        // (from the SDK header's `__declspec(uuid(...)) ITypedEventHandler<...>` lines).
        public Handler(Guid iid, Action callback)
        {
            var block = (Block*)NativeMemory.Alloc((nuint)sizeof(Block));
            block->VTable = (IntPtr)VTable;
            block->RefCount = 1;   // our own reference, dropped in Dispose
            block->Callback = GCHandle.ToIntPtr(GCHandle.Alloc(callback));
            block->Iid = iid;
            Pointer = (IntPtr)block;
        }

        public void Dispose() => Release(Pointer);

        private static IntPtr* CreateVTable()
        {
            var table = (IntPtr*)NativeMemory.Alloc((nuint)(4 * sizeof(IntPtr)));   // lives as long as the app
            table[0] = (IntPtr)(delegate* unmanaged[Stdcall]<Block*, Guid*, IntPtr*, int>)&QI;
            table[1] = (IntPtr)(delegate* unmanaged[Stdcall]<Block*, uint>)&AddRef;
            table[2] = (IntPtr)(delegate* unmanaged[Stdcall]<Block*, uint>)&ReleaseBlock;
            table[3] = (IntPtr)(delegate* unmanaged[Stdcall]<Block*, IntPtr, IntPtr, int>)&Invoke;
            return table;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int QI(Block* self, Guid* iid, IntPtr* result)
        {
            if (*iid == self->Iid || *iid == IID_IUnknown || *iid == IID_IAgileObject)
            {
                Interlocked.Increment(ref self->RefCount);
                *result = (IntPtr)self;
                return S_OK;
            }
            *result = IntPtr.Zero;
            return unchecked((int)0x80004002);   // E_NOINTERFACE
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint AddRef(Block* self) => (uint)Interlocked.Increment(ref self->RefCount);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint ReleaseBlock(Block* self)
        {
            int count = Interlocked.Decrement(ref self->RefCount);
            if (count == 0)
            {
                GCHandle.FromIntPtr(self->Callback).Free();
                NativeMemory.Free(self);
            }
            return (uint)count;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int Invoke(Block* self, IntPtr sender, IntPtr args)
        {
            try { ((Action)GCHandle.FromIntPtr(self->Callback).Target!)(); }
            catch (Exception) { }   // an exception must never cross back into Windows
            return S_OK;
        }
    }
}
