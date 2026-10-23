namespace MacShell.Native;

internal static class ProcessMemory
{
    // Hands this process's idle RAM back to Windows ("trims the working set"): pages we aren't touching move to the
    // standby list, and come back on their own (a soft fault, microseconds) the next time they're used. Task
    // Manager's "Memory" column is this working set. Windows does the same thing itself under memory pressure;
    // doing it while MacShell is idle keeps its footprint low all day. Private bytes (committed memory) don't change.
    public static void TrimWorkingSet() =>
        Win32.SetProcessWorkingSetSize(Win32.GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));   // -1, -1 = "trim it"
}
