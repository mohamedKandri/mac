using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MacShell.Native;

// The apps in the Start menu's "All apps" list (the shell's AppsFolder), their icons, and launching them.
// One mechanism for both classic apps (Edge, VS Code) and Store apps (Settings, Teams):
//   each item has a display name ("Microsoft Edge") and a parsing name, which is either an app ID
//   ("MSEdge", "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel")
//   or a path with a known-folder prefix. "shell:AppsFolder\<parsing name>" launches it.
internal static class AppCatalog
{
    // IsFile = a file pinned directly (exe or .lnk) instead of a Start menu app.
    // ExePath = the program a classic app's Start menu entry points to (null for Store apps); used for its icon
    // (some entries, e.g. Firefox, have no icon of their own) and, in dock step 3, to match running windows.
    public sealed record App(string Name, string ParsingName, bool IsFile, string? ExePath = null);

    // Everything in "All apps". Takes ~100 ms, so callers keep the list instead of asking per tile.
    public static List<App> StartMenuApps()
    {
        var apps = new List<App>();
        try
        {
            var folderId = FolderIdAppsFolder;
            var iid = typeof(IShellItem).GUID;
            if (SHGetKnownFolderItem(ref folderId, 0, IntPtr.Zero, ref iid, out IShellItem folder) != 0) return apps;

            var bhid = BhidEnumItems;
            var enumIid = typeof(IEnumShellItems).GUID;
            folder.BindToHandler(IntPtr.Zero, ref bhid, ref enumIid, out object enumerator);
            var items = (IEnumShellItems)enumerator;
            while (items.Next(1, out IShellItem item, out uint fetched) == 0 && fetched == 1)
            {
                string? name = DisplayName(item, SIGDN_NORMALDISPLAY);
                string? parsing = DisplayName(item, SIGDN_PARENTRELATIVEPARSING);
                if (name is not null && parsing is not null) apps.Add(new App(name, parsing, IsFile: false, TargetExe(item)));
                Marshal.ReleaseComObject(item);
            }
            Marshal.ReleaseComObject(items);
            Marshal.ReleaseComObject(folder);
        }
        catch (Exception)
        {
            // Shell not ready (very early at sign-in): an empty catalog; pins resolve on the next config reload.
        }
        return apps;
    }

    // A pin is a Start menu name ("Microsoft Edge"), a parsing name, or a path to an .exe / .lnk.
    // Returns null and a hint when nothing matches.
    public static App? Resolve(string pin, List<App> catalog, out string? hint)
    {
        hint = null;
        if (pin.Contains('\\') && !pin.StartsWith('{'))   // a path (parsing names of classic apps start with "{GUID}\")
        {
            string path = Environment.ExpandEnvironmentVariables(pin);
            if (File.Exists(path))
                return new App(Path.GetFileNameWithoutExtension(path), path, IsFile: true,
                    path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? path : null);
            hint = "File not found.";
            return null;
        }

        var match = catalog.FirstOrDefault(a => a.Name.Equals(pin, StringComparison.OrdinalIgnoreCase))
                 ?? catalog.FirstOrDefault(a => a.ParsingName.Equals(pin, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            // Items from the folder listing sometimes lack properties (Firefox's target, for one); the same item
            // opened by name has them all. Only done for pinned apps, so it's a handful of lookups.
            return match.ExePath is null ? match with { ExePath = TargetExe(@"shell:AppsFolder\" + match.ParsingName) } : match;
        }

        var close = catalog.Where(a => a.Name.Contains(pin, StringComparison.OrdinalIgnoreCase)).Select(a => a.Name).Distinct().Take(3).ToList();
        hint = close.Count > 0 ? $"Did you mean: {string.Join(", ", close)}?" : "Use the name shown in Start > All apps, or a full path to an .exe.";
        return null;
    }

    public static void Launch(App app) =>
        Shell.Open(app.IsFile ? app.ParsingName : @"shell:AppsFolder\" + app.ParsingName);

    // The app's icon, `px` pixels square, with transparency. Null if the shell has none.
    public static BitmapSource? Icon(App app, int px)
    {
        // Prefer the program's own icon: some Start menu entries have none and would show a blank page.
        string parsing = app.ExePath ?? (app.IsFile ? app.ParsingName : @"shell:AppsFolder\" + app.ParsingName);
        var iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(parsing, IntPtr.Zero, ref iid, out object? factoryObject) != 0 || factoryObject is null)
            return null;

        var factory = (IShellItemImageFactory)factoryObject;
        try
        {
            // ICONONLY: the app icon, not a file thumbnail. BIGGERSIZEOK: a larger image is fine, we scale it.
            if (factory.GetImage(new NativeSize { Width = px, Height = px }, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out IntPtr hbitmap) != 0)
                return null;
            try
            {
                return ToBitmapSource(hbitmap);
            }
            finally
            {
                Win32.DeleteObject(hbitmap);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    // GDI bitmap -> WPF image. Imaging.CreateBitmapSourceFromHBitmap would drop the alpha channel (black corners),
    // so copy the pixels ourselves, top row first. The shell's pixels are STRAIGHT alpha (color not pre-multiplied
    // by transparency): reading them as premultiplied (Pbgra32) gave every soft edge a light fringe, which looked
    // like a badly cut-out PNG at small sizes. Verified side by side (catalogtest, icon-compare.png).
    private static BitmapSource? ToBitmapSource(IntPtr hbitmap)
    {
        if (Win32.GetObjectW(hbitmap, Marshal.SizeOf<Win32.BITMAP>(), out var info) == 0) return null;
        int width = info.bmWidth, height = Math.Abs(info.bmHeight);
        var header = new Win32.BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height,
            biPlanes = 1,
            biBitCount = 32,
        };
        var pixels = new byte[width * height * 4];
        IntPtr hdc = Win32.GetDC(IntPtr.Zero);
        try
        {
            if (Win32.GetDIBits(hdc, hbitmap, 0, (uint)height, pixels, ref header, 0) == 0) return null;
        }
        finally
        {
            Win32.ReleaseDC(IntPtr.Zero, hdc);
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();   // immutable: cheaper, and usable from any thread
        return bitmap;
    }

    private static string? TargetExe(string parsingName)
    {
        var iid = typeof(IShellItem2).GUID;
        if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out object? item) != 0 || item is null) return null;
        try { return TargetExe((IShellItem)item); }
        finally { Marshal.ReleaseComObject(item); }
    }

    // The "Target" of a Start menu entry (System.Link.TargetParsingPath), if it's an existing .exe.
    private static string? TargetExe(IShellItem item)
    {
        try
        {
            var key = new PropertyKey { FormatId = new Guid("B9B4B3FC-2B51-4A42-B5D8-324146AFCF25"), PropertyId = 2 };
            if (((IShellItem2)item).GetString(ref key, out IntPtr text) != 0) return null;
            string? path;
            try { path = Marshal.PtrToStringUni(text); }
            finally { Marshal.FreeCoTaskMem(text); }
            return path is not null && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path) ? path : null;
        }
        catch (Exception)
        {
            return null;   // not a classic app, or the property isn't there
        }
    }

    private static string? DisplayName(IShellItem item, uint form)
    {
        if (item.GetDisplayName(form, out IntPtr text) != 0) return null;
        try { return Marshal.PtrToStringUni(text); }
        finally { Marshal.FreeCoTaskMem(text); }
    }

    // ---------------- Shell COM declarations ----------------

    private static readonly Guid FolderIdAppsFolder = new("1E87508D-89C2-42F0-8A7E-645A0F50CA58");
    private static readonly Guid BhidEnumItems = new("94F60519-2850-4924-AA5A-D15E84868039");
    private const uint SIGDN_NORMALDISPLAY = 0x00000000;
    private const uint SIGDN_PARENTRELATIVEPARSING = 0x80018001;
    private const int SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_ICONONLY = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width, Height;
    }

    // Finds a known folder ("All apps") as a shell item.
    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderItem(ref Guid folderId, uint flags, IntPtr token, ref Guid riid, out IShellItem item);

    // Turns a path or "shell:..." string into a shell item, asking for the interface `riid`.
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid,
        [MarshalAs(UnmanagedType.IUnknown)] out object? item);

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object result);
        void GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint form, out IntPtr name);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    // IShellItem plus property access. COM interop can't inherit interfaces, so IShellItem's five methods are
    // repeated first; the unused ones keep their vtable slots.
    [ComImport, Guid("7E9FB0D3-919F-4307-AB2E-9B1860310C93"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem2
    {
        void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid riid, out IntPtr result);
        void GetParent(out IntPtr parent);
        [PreserveSig] int GetDisplayName(uint form, out IntPtr name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IntPtr other, uint hint, out int order);
        void GetPropertyStore(int flags, ref Guid riid, out IntPtr store);
        void GetPropertyStoreWithCreateObject(int flags, IntPtr createObject, ref Guid riid, out IntPtr store);
        void GetPropertyStoreForKeys(IntPtr keys, uint count, int flags, ref Guid riid, out IntPtr store);
        void GetPropertyDescriptionList(ref PropertyKey type, ref Guid riid, out IntPtr list);
        void Update(IntPtr bindContext);
        void GetProperty(ref PropertyKey key, IntPtr value);
        void GetCLSID(ref PropertyKey key, out Guid clsid);
        void GetFileTime(ref PropertyKey key, out long time);
        void GetInt32(ref PropertyKey key, out int value);
        [PreserveSig] int GetString(ref PropertyKey key, out IntPtr value);
    }

    [ComImport, Guid("70629033-E363-4A28-A567-0DB78006E6D7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumShellItems
    {
        [PreserveSig] int Next(uint count, out IShellItem item, out uint fetched);
    }

    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(NativeSize size, int flags, out IntPtr hbitmap);
    }
}
