using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace MacShell.Native;

// Numbers for the notch rings: CPU load, memory use, disk use, and the busiest apps.
// Windows has no "CPU load changed" event, so the notch samples these every `notch.refreshSeconds` (each call costs microseconds).
// The per-app breakdowns read one snapshot of all processes, only while a popup is open.
internal static class SystemStats
{
    public readonly record struct Memory(int Percent, double UsedGb, double TotalGb);
    public readonly record struct Drive(string Name, int Percent, double UsedGb, double TotalGb);
    public readonly record struct AppLoad(string Name, double Percent, string Value);

    public static Memory ReadMemory()
    {
        var status = new Win32.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Win32.MEMORYSTATUSEX>() };
        if (!Win32.GlobalMemoryStatusEx(ref status)) return default;
        const double Gb = 1024.0 * 1024 * 1024;
        return new Memory((int)status.dwMemoryLoad, (status.ullTotalPhys - status.ullAvailPhys) / Gb, status.ullTotalPhys / Gb);
    }

    // Fixed (internal) drives; the first one is the system drive.
    public static List<Drive> ReadDrives()
    {
        const double Gb = 1024.0 * 1024 * 1024;
        string system = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        return DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .OrderByDescending(d => d.Name.Equals(system, StringComparison.OrdinalIgnoreCase))
            .Select(d =>
            {
                double used = (d.TotalSize - d.TotalFreeSpace) / Gb, total = d.TotalSize / Gb;
                return new Drive(d.Name.TrimEnd('\\'), total > 0 ? (int)Math.Round(used / total * 100) : 0, used, total);
            })
            .ToList();
    }

    // The busiest apps by memory (private working set, like Task Manager's "Memory" column), grouped by program
    // name (Chrome's many processes count as one).
    public static List<AppLoad> TopMemory(double totalGb, int count)
    {
        var groups = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in ProcessSnapshot.Take())
            groups[p.Name] = groups.GetValueOrDefault(p.Name) + p.PrivateBytes;
        const double Gb = 1024.0 * 1024 * 1024;
        return groups.OrderByDescending(g => g.Value).Take(count)
            .Select(g => new AppLoad(g.Key, g.Value / Gb / totalGb * 100, g.Value / Gb >= 1 ? $"{g.Value / Gb:F1} GB" : $"{g.Value / 1024 / 1024} MB"))
            .ToList();
    }

    // One process from a snapshot: "Chrome" (exe name without .exe, capitalized), CPU time used so far, private memory.
    public readonly record struct ProcessEntry(int Id, string Name, long CpuTicks, long PrivateBytes);

    // Reads NtQuerySystemInformation(SystemProcessInformation): one call for every process, no process handles.
    private static class ProcessSnapshot
    {
        // Field offsets in SYSTEM_PROCESS_INFORMATION (64-bit layout; this app only ships for win-x64).
        private const int NextOffset = 0, PrivateSet = 8, UserTime = 40, KernelTime = 48, NameLength = 56, NameBuffer = 64, ProcessId = 80;
        private static int _size = 1024 * 1024;   // remembered between calls; grows if Windows asks for more

        public static List<ProcessEntry> Take()
        {
            var result = new List<ProcessEntry>(400);
            for (int attempt = 0; attempt < 4; attempt++)
            {
                // Allocated per call and freed right away: the buffer is ~1 MB and only needed for a moment.
                IntPtr buffer = Marshal.AllocHGlobal(_size);
                try
                {
                    int status = Win32.NtQuerySystemInformation(Win32.SystemProcessInformation, buffer, _size, out int needed);
                    if (status == Win32.STATUS_INFO_LENGTH_MISMATCH)
                    {
                        _size = Math.Max(_size * 2, needed + 64 * 1024);   // processes can start between the two calls
                        continue;
                    }
                    if (status < 0) return result;

                    for (IntPtr entry = buffer; ; )
                    {
                        int id = (int)Marshal.ReadIntPtr(entry, ProcessId);
                        int nameBytes = Marshal.ReadInt16(entry, NameLength);
                        if (id != 0 && nameBytes > 0)   // 0 = the "System Idle Process", which has no name
                        {
                            string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(entry, NameBuffer), nameBytes / 2);
                            result.Add(new ProcessEntry(id, Pretty(name),
                                Marshal.ReadInt64(entry, UserTime) + Marshal.ReadInt64(entry, KernelTime),
                                Marshal.ReadInt64(entry, PrivateSet)));
                        }
                        int next = Marshal.ReadInt32(entry, NextOffset);
                        if (next == 0) break;
                        entry += next;
                    }
                    return result;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            return result;
        }

        private static string Pretty(string exe)
        {
            string name = exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe[..^4] : exe;
            return name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];
        }
    }

    // CPU load since the previous Sample() (0-100). The first call returns 0.
    public sealed class CpuMeter
    {
        private long _idle, _total;

        public int Sample()
        {
            if (!Win32.GetSystemTimes(out long idle, out long kernel, out long user)) return 0;
            long total = kernel + user;   // kernel time already includes idle time
            long dIdle = idle - _idle, dTotal = total - _total;
            bool first = _total == 0;
            _idle = idle;
            _total = total;
            return first || dTotal <= 0 ? 0 : (int)Math.Round(100.0 * (dTotal - dIdle) / dTotal);
        }
    }

    // Per-app CPU since the previous Sample(), grouped by program name, as a share of the whole machine (like
    // Task Manager). Only sampled while the CPU popup shows; the first call returns an empty list.
    public sealed class AppCpuMeter
    {
        private Dictionary<int, long> _last = [];
        private long _lastAt;

        public List<AppLoad> Sample(int count)
        {
            long now = Stopwatch.GetTimestamp();
            // Elapsed time × cores, in 100 ns ticks (the unit of the process CPU times).
            double capacity = Stopwatch.GetElapsedTime(_lastAt, now).Ticks * (double)Environment.ProcessorCount;
            var current = new Dictionary<int, long>();
            var groups = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in ProcessSnapshot.Take())
            {
                current[p.Id] = p.CpuTicks;
                if (_last.TryGetValue(p.Id, out long before))
                    groups[p.Name] = groups.GetValueOrDefault(p.Name) + Math.Max(0, p.CpuTicks - before);
            }
            bool first = _last.Count == 0;
            _last = current;
            _lastAt = now;
            if (first || capacity <= 0) return [];
            return groups.OrderByDescending(g => g.Value).Take(count)
                .Select(g =>
                {
                    double pct = Math.Clamp(g.Value / capacity * 100, 0, 100);
                    return new AppLoad(g.Key, pct, $"{pct:F0}%");
                })
                .ToList();
        }
    }
}
