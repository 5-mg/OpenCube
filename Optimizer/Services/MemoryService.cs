using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;

namespace OpenCube.Services;

public sealed record MemorySnapshot(long TotalBytes, long AvailableBytes, long StandbyBytes, long ModifiedBytes)
{
    public long UsedBytes => TotalBytes - AvailableBytes;
    public double UsedPercent => TotalBytes == 0 ? 0 : (double)UsedBytes / TotalBytes * 100;
}

public sealed record TrimResult(int Processes, long FreedBytes, int Failed);

/// <summary>
/// Honest RAM tools: Windows already manages memory, so these only help in specific cases.
/// Trimming moves the idle memory of background programs out of RAM (they reload it when used);
/// purging the standby list drops Windows' own file cache (needs administrator).
/// </summary>
public static class MemoryService
{
    public static MemorySnapshot Read()
    {
        var status = new Native.MemoryStatusEx { Length = (uint)Marshal.SizeOf<Native.MemoryStatusEx>() };
        if (!Native.GlobalMemoryStatusEx(ref status)) return new MemorySnapshot(0, 0, 0, 0);

        long standby = 0, modified = 0;
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_PerfFormattedData_PerfOS_Memory");
            foreach (ManagementBaseObject o in searcher.Get())
            {
                standby = ToLong(o, "StandbyCacheNormalPriorityBytes") + ToLong(o, "StandbyCacheReserveBytes") + ToLong(o, "StandbyCacheCoreBytes");
                modified = ToLong(o, "ModifiedPageListBytes");
                o.Dispose();
                break;
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException) { }

        return new MemorySnapshot((long)status.TotalPhys, (long)status.AvailPhys, standby, modified);
    }

    private static long ToLong(ManagementBaseObject o, string property)
    {
        try { return o[property] is null ? 0 : Convert.ToInt64(o[property]); }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or ManagementException) { return 0; }
    }

    /// <summary>Trims the working set of every user program except the one you are looking at and protected ones.</summary>
    public static TrimResult TrimBackgroundProcesses()
    {
        var before = Read().AvailableBytes;

        Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out var foreground);
        var ancestors = ProcessService.Ancestors(Environment.ProcessId);
        int trimmed = 0, failed = 0;

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id == (int)foreground) continue; // never trim what you're using right now
                    var path = ProcessService.GetPath(p.Id);
                    if (ProcessGuard.IsProtected(p.ProcessName, path, p.Id, ancestors)) continue;

                    var handle = Native.OpenProcess(Native.ProcessSetQuota | Native.ProcessQueryInformation, false, p.Id);
                    if (handle == IntPtr.Zero) continue; // not ours to touch
                    try
                    {
                        if (Native.EmptyWorkingSet(handle)) trimmed++; else failed++;
                    }
                    finally { Native.CloseHandle(handle); }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { failed++; }
            }
        }

        Thread.Sleep(600); // let the memory manager settle before measuring
        var after = Read().AvailableBytes;
        return new TrimResult(trimmed, Math.Max(0, after - before), failed);
    }

    /// <returns>null on success, otherwise a message explaining why it could not be done.</returns>
    public static string? PurgeStandbyList()
    {
        if (!StartupService.IsAdmin()) return "Necesita ejecutar OpenCube como administrador.";
        if (!EnablePrivilege("SeProfileSingleProcessPrivilege")) return "No se pudo obtener el permiso necesario de Windows.";

        const int SystemMemoryListInformation = 80;
        var command = 4; // MemoryPurgeStandbyList
        var status = Native.NtSetSystemInformation(SystemMemoryListInformation, ref command, sizeof(int));
        return status == 0 ? null : $"Windows rechazó la operación (código 0x{status:X8}).";
    }

    private static bool EnablePrivilege(string name)
    {
        if (!Native.OpenProcessToken(Native.GetCurrentProcess(), 0x0020 | 0x0008, out var token)) return false;
        try
        {
            if (!Native.LookupPrivilegeValue(null, name, out var luid)) return false;
            var state = new Native.TokenPrivileges { Count = 1, Luid = luid, Attributes = 0x00000002 };
            return Native.AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero) && Marshal.GetLastWin32Error() == 0;
        }
        finally { Native.CloseHandle(token); }
    }
}
