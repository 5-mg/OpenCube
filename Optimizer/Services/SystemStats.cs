using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace OpenCube.Services;

public sealed record MemoryInfo(double UsedGb, double TotalGb, double Percent);

public sealed record DiskInfo(double UsedGb, double TotalGb, double Percent);

public sealed record ProcessInfo(string Name, double MemoryMb);

public sealed class SystemStats
{
    private ulong _prevIdle, _prevKernel, _prevUser;

    public SystemStats() => ReadTimes(out _prevIdle, out _prevKernel, out _prevUser);

    /// <summary>CPU usage since the previous call, 0-100.</summary>
    public double ReadCpuPercent()
    {
        ReadTimes(out var idle, out var kernel, out var user);
        var dIdle = idle - _prevIdle;
        var dTotal = (kernel - _prevKernel) + (user - _prevUser); // kernel time already includes idle
        _prevIdle = idle; _prevKernel = kernel; _prevUser = user;
        return dTotal == 0 ? 0 : Math.Clamp((1.0 - (double)dIdle / dTotal) * 100.0, 0, 100);
    }

    public MemoryInfo ReadMemory()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status)) return new MemoryInfo(0, 0, 0);
        const double gb = 1024.0 * 1024 * 1024;
        var total = status.ullTotalPhys / gb;
        var used = (status.ullTotalPhys - status.ullAvailPhys) / gb;
        return new MemoryInfo(used, total, status.dwMemoryLoad);
    }

    public DiskInfo? ReadSystemDisk()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory);
            if (root is null) return null;
            var d = new DriveInfo(root);
            const double gb = 1024.0 * 1024 * 1024;
            var total = d.TotalSize / gb;
            var used = (d.TotalSize - d.TotalFreeSpace) / gb;
            return new DiskInfo(used, total, total == 0 ? 0 : used / total * 100);
        }
        catch { return null; }
    }

    public static IReadOnlyList<ProcessInfo> TopProcessesByMemory(int count)
    {
        var grouped = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var mb = p.WorkingSet64 / (1024.0 * 1024);
                grouped[p.ProcessName] = grouped.GetValueOrDefault(p.ProcessName) + mb;
            }
            catch { /* process exited or access denied */ }
            finally { p.Dispose(); }
        }
        return grouped.OrderByDescending(kv => kv.Value)
                      .Take(count)
                      .Select(kv => new ProcessInfo(kv.Key, kv.Value))
                      .ToList();
    }

    public static TimeSpan Uptime => TimeSpan.FromMilliseconds(Environment.TickCount64);

    private static void ReadTimes(out ulong idle, out ulong kernel, out ulong user)
    {
        GetSystemTimes(out var i, out var k, out var u);
        idle = ToUlong(i); kernel = ToUlong(k); user = ToUlong(u);
    }

    private static ulong ToUlong(FILETIME ft) => ((ulong)ft.dwHighDateTime << 32) | ft.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public uint dwLowDateTime; public uint dwHighDateTime; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
}
