using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenCube.Services;

public enum ProcTag { Suggested, Other }

public sealed record ProcInfo(int Pid, string Name, string? Path, long MemoryBytes, bool HasWindow);

public sealed class ProcGroup
{
    public required string Name { get; init; }
    public required IReadOnlyList<ProcInfo> Items { get; init; }
    public ProcTag Tag { get; init; }
    public string? Reason { get; init; }
    public long MemoryBytes => Items.Sum(i => i.MemoryBytes);
    public bool HasWindow => Items.Any(i => i.HasWindow);
}

/// <param name="Retry">Programs with a window that did not exit after a normal close request (many only hide in the tray).</param>
public sealed record CloseResult(int Closed, int Skipped, int Failed, long FreedBytes, IReadOnlyList<ProcInfo>? Retry = null);

/// <summary>Decides which processes may never be touched by this app.</summary>
public static class ProcessGuard
{
    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "registry", "memory compression", "memcompression", "secure system", "system interrupts",
        "smss", "csrss", "wininit", "services", "lsass", "lsaiso", "lsm", "svchost", "winlogon", "userinit", "logonui", "lockapp",
        "dwm", "explorer", "fontdrvhost", "sihost", "ctfmon", "taskhostw", "runtimebroker", "searchhost", "searchindexer",
        "searchapp", "shellexperiencehost", "startmenuexperiencehost", "textinputhost", "applicationframehost", "audiodg",
        "spoolsv", "dllhost", "conhost", "wmiprvse", "smartscreen", "wudfhost", "dashost",
        "msedgewebview2", // runtime that other apps are built on
    };

    // Security software must keep running.
    private static readonly string[] SecurityPrefixes =
    [
        "msmpeng", "nissrv", "securityhealth", "mbam", "avast", "avg", "bdagent", "bitdefender", "kaspersky", "avp",
        "norton", "mcafee", "mfemms", "egui", "ekrn",
    ];

    public static bool IsProtected(string name, string? path, int pid, ISet<int> ancestors)
    {
        if (pid <= 4) return true;
        if (pid == Environment.ProcessId || ancestors.Contains(pid)) return true; // this app and whatever launched it

        var n = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        if (ProtectedNames.Contains(n)) return true;
        if (SecurityPrefixes.Any(p => n.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return true;

        if (path is null) return true; // can't tell where it lives: leave it alone
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return path.StartsWith(windows + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

public static class ProcessService
{
    private static readonly (Regex Pattern, string Reason)[] Suggested =
    [
        (R(@"^(GoogleUpdate|GoogleCrashHandler|GoogleCrashHandler64)$"), "Actualizador de Google; Chrome se actualiza solo al abrirlo."),
        (R(@"^(MicrosoftEdgeUpdate)$"), "Actualizador de Microsoft Edge."),
        (R(@"^(OneDriveStandaloneUpdater)$"), "Actualizador de OneDrive."),
        (R(@"^(jusched|jucheck)$"), "Buscador de actualizaciones de Java."),
        (R(@"^(Adobe.*|CCXProcess|CCLibrary|Creative Cloud.*|AdobeIPCBroker)$"), "Servicios de Adobe Creative Cloud: solo hacen falta mientras usas Adobe."),
        (R(@"^(ms-teams|Teams|msteams)$"), "Microsoft Teams en segundo plano."),
        (R(@"^(Skype|SkypeApp|SkypeBackgroundHost)$"), "Skype en segundo plano."),
        (R(@"^(YourPhone|PhoneExperienceHost)$"), "Enlace con el móvil (Phone Link)."),
        (R(@"^(GameBar|GameBarPresenceWriter|GameBarFTServer)$"), "Xbox Game Bar en segundo plano; solo hace falta si lo usas."),
        (R(@"^(Widgets|WidgetService)$"), "Panel de widgets de Windows."),
        (R(@"^(NvBackend|NVIDIA Web Helper|NVIDIA Share|nvsphelper64)$"), "Componentes opcionales de GeForce Experience."),
        (R(@"^(Overwolf.*)$"), "Overwolf (overlays y apps para juegos): solo hace falta mientras juegas."),
        (R(@"^(EpicWebHelper|EpicGamesLauncher)$"), "Epic Games Launcher; ciérralo si no vas a jugar."),
    ];

    private static Regex R(string p) => new(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string? SuggestionFor(string name) =>
        Suggested.FirstOrDefault(s => s.Pattern.IsMatch(name)).Reason;

    public static IReadOnlyList<ProcGroup> GetGroups()
    {
        var ancestors = Ancestors(Environment.ProcessId);
        var infos = new List<ProcInfo>();

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    var path = GetPath(p.Id);
                    if (ProcessGuard.IsProtected(p.ProcessName, path, p.Id, ancestors)) continue;
                    if (!CanTerminate(p.Id)) continue; // elevated or belongs to someone else

                    var hasWindow = false;
                    try { hasWindow = p.MainWindowHandle != IntPtr.Zero; } catch (InvalidOperationException) { }

                    infos.Add(new ProcInfo(p.Id, p.ProcessName, path, p.WorkingSet64, hasWindow));
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { }
            }
        }

        return infos
            .GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var reason = SuggestionFor(g.Key);
                return new ProcGroup
                {
                    Name = g.Key,
                    Items = g.ToList(),
                    Tag = reason is null ? ProcTag.Other : ProcTag.Suggested,
                    Reason = reason,
                };
            })
            .OrderBy(g => g.Tag)
            .ThenByDescending(g => g.MemoryBytes)
            .ToList();
    }

    /// <summary>
    /// Programs with a visible window are first asked to close normally (they can still ask to save); windowless helpers are ended.
    /// With <paramref name="force"/> a window that did not exit is ended too (unsaved work is lost).
    /// Anything protected, or whose name no longer matches the listing, is skipped.
    /// </summary>
    public static CloseResult Close(IEnumerable<ProcInfo> items, bool force = false)
    {
        var ancestors = Ancestors(Environment.ProcessId);
        int closed = 0, skipped = 0, failed = 0;
        long freed = 0;
        var retry = new List<ProcInfo>();

        foreach (var info in items)
        {
            try
            {
                using var p = Process.GetProcessById(info.Pid);
                if (!string.Equals(p.ProcessName, info.Name, StringComparison.OrdinalIgnoreCase)) { skipped++; continue; } // PID was reused
                if (ProcessGuard.IsProtected(info.Name, GetPath(info.Pid), info.Pid, ancestors)) { skipped++; continue; }

                var hasWindow = false;
                try { hasWindow = p.MainWindowHandle != IntPtr.Zero; } catch (InvalidOperationException) { }

                if (hasWindow)
                {
                    p.CloseMainWindow();
                    if (!p.WaitForExit(force ? 1500 : 4000))
                    {
                        // Still alive: it is asking something, or it only minimised to the tray (Epic, Discord, Steam...).
                        if (!force) { failed++; retry.Add(info); continue; }
                        p.Kill();
                        p.WaitForExit(3000);
                    }
                }
                else
                {
                    p.Kill();
                    p.WaitForExit(3000);
                }
                closed++;
                freed += info.MemoryBytes;
            }
            catch (ArgumentException) { closed++; }               // already gone
            catch (InvalidOperationException) { closed++; }       // exited while we were closing it
            catch (Win32Exception) { failed++; }
        }

        return new CloseResult(closed, skipped, failed, freed, retry);
    }

    public static string? GetPath(int pid)
    {
        var handle = Native.OpenProcess(Native.ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            return Native.QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { Native.CloseHandle(handle); }
    }

    public static bool CanTerminate(int pid)
    {
        var handle = Native.OpenProcess(Native.ProcessTerminate, false, pid);
        if (handle == IntPtr.Zero) return false;
        Native.CloseHandle(handle);
        return true;
    }

    public static HashSet<int> Ancestors(int pid)
    {
        var set = new HashSet<int>();
        var current = pid;
        for (var depth = 0; depth < 12; depth++)
        {
            var parent = ParentOf(current);
            if (parent <= 4 || !set.Add(parent)) break;
            current = parent;
        }
        return set;
    }

    private static int ParentOf(int pid)
    {
        var handle = Native.OpenProcess(Native.ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return 0;
        try
        {
            var info = new Native.ProcessBasicInformation();
            return Native.NtQueryInformationProcess(handle, 0, ref info, System.Runtime.InteropServices.Marshal.SizeOf(info), out _) == 0
                ? (int)info.InheritedFromUniqueProcessId
                : 0;
        }
        finally { Native.CloseHandle(handle); }
    }
}
