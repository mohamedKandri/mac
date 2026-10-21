using System.Runtime.InteropServices;

namespace MacShell.Native;

// The Recycle Bin: empty or not, opening it, and moving files into it.
//
// No live change notifications on purpose. A version that subscribed to every Recycle Bin event
// (SHChangeNotifyRegister, all events, recursive, interrupt level) coincided twice with the owner's desktop going
// black (Explorer not drawing). Until that's ruled out, the dock re-checks the state when the mouse enters it and
// after a drop, which is cheap (one SHQueryRecycleBin call).
internal static class RecycleBin
{
    // True when there's nothing to restore. Counts all drives' bins.
    public static bool IsEmpty()
    {
        var info = new SHQUERYRBINFO { Size = (uint)Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBinW(null, ref info) != 0 || info.ItemCount == 0;
    }

    public static void Open() => Shell.Open("shell:RecycleBinFolder");

    // Moves files/folders to the Recycle Bin, with Windows' usual progress window and confirmation (per your
    // Recycle Bin settings). Never deletes outright: if something can't be recycled (network drive, too big),
    // Windows asks first ("delete permanently?"). Runs on its own thread so the dock stays responsive.
    // `failed` gets an error message and `done` is called when it's over, both on that thread.
    public static void Recycle(IReadOnlyList<string> paths, Action<string> failed, Action done)
    {
        var thread = new Thread(() =>
        {
            var operation = new SHFILEOPSTRUCTW
            {
                Function = FO_DELETE,
                From = string.Join('\0', paths) + "\0\0",   // the API wants a list of paths ending in two NULs
                Flags = FOF_ALLOWUNDO | FOF_WANTNUKEWARNING,
            };
            int result = SHFileOperationW(ref operation);
            if (result != 0 && !operation.AnyAborted)
                failed($"Windows error 0x{result:X}");
            done();
        });
        thread.SetApartmentState(ApartmentState.STA);   // shell UI (progress, confirmation) needs an STA thread
        thread.IsBackground = true;
        thread.Start();
    }

    // ---------------- shell32.dll ----------------

    private const uint FO_DELETE = 3;
    private const ushort FOF_ALLOWUNDO = 0x0040;        // = to the Recycle Bin
    private const ushort FOF_WANTNUKEWARNING = 0x4000;  // warn before anything would be deleted permanently

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public uint Size;
        public long TotalBytes;
        public long ItemCount;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCTW
    {
        public IntPtr Window;
        public uint Function;
        public string From;
        public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool AnyAborted;
        public IntPtr NameMappings;
        public string? ProgressTitle;
    }

    // Size and item count of the Recycle Bin (null root = all drives).
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBinW(string? rootPath, ref SHQUERYRBINFO info);

    // Copy / move / delete files with the shell's UI; delete + FOF_ALLOWUNDO = recycle.
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW operation);
}
