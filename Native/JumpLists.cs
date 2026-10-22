using System.Runtime.InteropServices;

namespace MacShell.Native;

// The "Recent" / "Frequent" part of an app's jump list (what the taskbar's right-click menu shows above the app's
// commands): Word's recent documents, Notepad's recent files, File Explorer's frequent folders...
// Documented API: IApplicationDocumentLists, given the app's AppUserModelID (the dock's AppsFolder parsing name, e.g.
// "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App", "MSEdge", or for classic apps a known-folder path like
// "{6D809377-...}\Notepad++\notepad++.exe" — the same ID Windows files the jump list under).
// An app's own "Tasks" (Edge's "New InPrivate window") have no API; they live in undocumented files and aren't read.
internal static class JumpLists
{
    public readonly record struct Item(string Name, string Path);

    private const int ADLT_RECENT = 0, ADLT_FREQUENT = 1;
    private const uint SIGDN_NORMALDISPLAY = 0x00000000, SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000;

    // Up to `max` recent items (frequent ones if the app keeps no recent list). Never throws; empty if none.
    public static List<Item> Recent(string appId, int max)
    {
        if (string.IsNullOrEmpty(appId)) return [];
        try
        {
            var lists = (IApplicationDocumentLists)new ApplicationDocumentLists();
            if (lists.SetAppID(appId) != 0) return [];
            var items = Read(lists, ADLT_RECENT, max);
            if (items.Count == 0) items = Read(lists, ADLT_FREQUENT, max);
            Marshal.ReleaseComObject(lists);
            return items;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return [];
        }
    }

    private static List<Item> Read(IApplicationDocumentLists lists, int type, int max)
    {
        var result = new List<Item>();
        var iidArray = typeof(IObjectArray).GUID;
        if (lists.GetList(type, (uint)max, ref iidArray, out object? arrayObject) != 0 || arrayObject is not IObjectArray array) return result;
        if (array.GetCount(out uint count) != 0) return result;

        var iidUnknown = new Guid("00000000-0000-0000-C000-000000000046");
        for (uint i = 0; i < count && result.Count < max; i++)
        {
            if (array.GetAt(i, ref iidUnknown, out object? entry) != 0 || entry is null) continue;
            // The list holds shell items (files, folders) and shell links (shortcuts with arguments).
            IShellItem? item = entry as IShellItem ?? FromLink(entry as IShellLinkW);
            if (item is null) continue;
            string? name = DisplayName(item, SIGDN_NORMALDISPLAY), path = DisplayName(item, SIGDN_DESKTOPABSOLUTEPARSING);
            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(path)) result.Add(new Item(name, path));
        }
        return result;
    }

    // Opens a recent item (with its default program, which for a jump list item is normally that app).
    public static void Open(Item item) => Shell.Open(item.Path);

    private static IShellItem? FromLink(IShellLinkW? link)
    {
        if (link is null || link.GetIDList(out IntPtr pidl) != 0 || pidl == IntPtr.Zero) return null;
        try
        {
            var iid = typeof(IShellItem).GUID;
            return SHCreateItemFromIDList(pidl, ref iid, out IShellItem item) == 0 ? item : null;
        }
        finally { Marshal.FreeCoTaskMem(pidl); }
    }

    private static string? DisplayName(IShellItem item, uint form)
    {
        if (item.GetDisplayName(form, out IntPtr text) != 0 || text == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(text); }
        finally { Marshal.FreeCoTaskMem(text); }
    }

    // ---------------- COM declarations (shobjidl_core.h) ----------------

    [ComImport, Guid("86BEC222-30F2-47E0-9F25-60D11CD75C28")]
    private class ApplicationDocumentLists;

    [ComImport, Guid("3C594F9F-9F30-47A1-979A-C9E83D3D0A06"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationDocumentLists
    {
        [PreserveSig] int SetAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);
        [PreserveSig] int GetList(int listType, uint itemsDesired, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? list);
    }

    [ComImport, Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectArray
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? item);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid riid, out IntPtr result);
        [PreserveSig] int GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint form, out IntPtr name);
    }

    // Only the first two methods are declared (the order of the vtable matters, the rest are never called).
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        [PreserveSig] int GetPath(IntPtr buffer, int size, IntPtr findData, uint flags);
        [PreserveSig] int GetIDList(out IntPtr pidl);
    }

    [DllImport("shell32.dll")]
    private static extern int SHCreateItemFromIDList(IntPtr pidl, ref Guid riid, out IShellItem item);
}
