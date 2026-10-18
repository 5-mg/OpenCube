using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace OpenCube.Services;

public enum AppSource { Program, Store }

public sealed record InstalledApp(
    string Id,
    string Name,
    string? Version,
    string? Publisher,
    long SizeBytes,
    DateTime? InstallDate,
    string? UninstallString,
    AppSource Source,
    string? FriendlyName = null)
{
    /// <summary>What to show to people; <see cref="Name"/> stays the technical name that the detector matches on.</summary>
    public string Title => string.IsNullOrWhiteSpace(FriendlyName) ? Name : FriendlyName;
}

/// <summary>Lists installed programs and Store apps, and starts their official uninstaller. Never uninstalls silently.</summary>
public static partial class InstalledAppsService
{
    private const string UninstallPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    private static readonly string[] BlockedStoreNames =
    [
        "Microsoft.WindowsStore", "Microsoft.StorePurchaseApp", "Microsoft.DesktopAppInstaller",
        "Microsoft.SecHealthUI", "Microsoft.WindowsAppRuntime",
    ];

    public static IReadOnlyList<InstalledApp> GetProgramApps()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<InstalledApp>();

        (RegistryHive Hive, RegistryView View)[] roots =
        [
            (RegistryHive.CurrentUser, RegistryView.Default),
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
        ];

        foreach (var (hive, view) in roots)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(UninstallPath);
                if (uninstall is null) continue;

                foreach (var sub in uninstall.GetSubKeyNames())
                {
                    using var key = uninstall.OpenSubKey(sub);
                    if (key is null) continue;
                    var app = ReadProgram(key, sub);
                    if (app is not null && seen.Add($"{app.Name}|{app.Version}")) list.Add(app);
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }

        return list.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static InstalledApp? ReadProgram(RegistryKey key, string id)
    {
        var name = (key.GetValue("DisplayName") as string)?.Trim();
        if (string.IsNullOrEmpty(name)) return null;
        if (key.GetValue("SystemComponent") is int sc && sc == 1) return null;
        if (key.GetValue("NoRemove") is int nr && nr == 1) return null;
        if (key.GetValue("ParentKeyName") is not null || key.GetValue("ParentDisplayName") is not null) return null;
        if (key.GetValue("ReleaseType") is string rt && rt is "Update" or "Hotfix" or "Security Update" or "Service Pack") return null;

        var uninstall = (key.GetValue("UninstallString") as string)?.Trim();
        if (string.IsNullOrEmpty(uninstall)) return null; // nothing we could launch

        long size = key.GetValue("EstimatedSize") is int kb && kb > 0 ? kb * 1024L : 0;

        DateTime? date = null;
        if (key.GetValue("InstallDate") is string d &&
            DateTime.TryParseExact(d, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            date = parsed;

        return new InstalledApp(id, name, key.GetValue("DisplayVersion") as string, key.GetValue("Publisher") as string,
            size, date, uninstall, AppSource.Program);
    }

    public static IReadOnlyList<InstalledApp> GetStoreApps()
    {
        // Get-StartApps knows the real, localized names of apps that have a Start entry (key = package family name).
        const string script =
            "[Console]::OutputEncoding=[Text.Encoding]::UTF8; " +
            "$start=@{}; try { Get-StartApps | ForEach-Object { $f=($_.AppID -split '!')[0]; if ($f -and -not $start.ContainsKey($f)) { $start[$f]=$_.Name } } } catch {}; " +
            "Get-AppxPackage | Where-Object { -not $_.IsFramework -and -not $_.NonRemovable -and $_.SignatureKind -ne 'System' } | " +
            "ForEach-Object { [pscustomobject]@{ Name=$_.Name; PackageFullName=$_.PackageFullName; Publisher=$_.Publisher; Version=[string]$_.Version; DisplayName=$start[$_.PackageFamilyName] } } | " +
            "ConvertTo-Json -Compress";
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -Command \"{script}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            })!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return ParseStoreJson(output);
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or JsonException) { return []; }
    }

    public static IReadOnlyList<InstalledApp> ParseStoreJson(string json)
    {
        json = json.Trim();
        if (json.Length == 0) return [];

        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];

        var list = new List<InstalledApp>();
        foreach (var e in items)
        {
            var name = e.TryGetProperty("Name", out var n) ? n.GetString() : null;
            var full = e.TryGetProperty("PackageFullName", out var f) ? f.GetString() : null;
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(full)) continue;
            if (IsBlockedStoreName(name)) continue;

            var display = e.TryGetProperty("DisplayName", out var dn) && dn.ValueKind == JsonValueKind.String ? dn.GetString() : null;
            // Real name when Windows knows it, otherwise a readable version of the technical name.
            var friendly = string.IsNullOrWhiteSpace(display) ? PrettifyStoreName(name) : display.Trim();

            list.Add(new InstalledApp(full, name,
                e.TryGetProperty("Version", out var v) ? v.GetString() : null,
                e.TryGetProperty("Publisher", out var pub) ? pub.GetString() : null,
                0, null, null, AppSource.Store, friendly));
        }
        return list.OrderBy(a => a.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// "47492CenterpointGaming.ProSightCrosshairExtension" -> "Pro Sight Crosshair Extension".
    /// Drops the publisher part (first segment) and splits CamelCase into words.
    /// </summary>
    public static string PrettifyStoreName(string technical)
    {
        var segments = technical.Split('.', StringSplitOptions.RemoveEmptyEntries).ToList();

        // "king.com.CandyCrushSaga": a reversed domain is the publisher
        var tld = segments.FindIndex(s => s.Equals("com", StringComparison.OrdinalIgnoreCase) || s.Equals("net", StringComparison.OrdinalIgnoreCase) || s.Equals("org", StringComparison.OrdinalIgnoreCase));
        if (tld >= 1 && tld < segments.Count - 1) segments.RemoveRange(0, tld + 1);
        else if (segments.Count > 1) segments.RemoveAt(0);

        var words = string.Join(" ", segments.Select(s => CamelWords().Replace(s, " ")));
        words = SpacesRegex().Replace(words, " ").Trim();
        return words.Length == 0 ? technical : words;
    }

    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])")]
    private static partial Regex CamelWords();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacesRegex();

    // Shell and platform packages must never be offered for removal.
    public static bool IsBlockedStoreName(string name) =>
        name.StartsWith("Microsoft.Windows.", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("MicrosoftWindows.", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Windows.", StringComparison.OrdinalIgnoreCase) ||
        BlockedStoreNames.Any(b => name.StartsWith(b, StringComparison.OrdinalIgnoreCase));

    /// <summary>Splits an UninstallString into program and arguments.</summary>
    public static (string Exe, string Args) ParseUninstallString(string s)
    {
        s = s.Trim();
        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            if (end > 0) return (s[1..end], s[(end + 1)..].Trim());
        }

        var idx = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) return (s[..(idx + 4)], s[(idx + 4)..].Trim());

        var space = s.IndexOf(' ');
        return space < 0 ? (s, "") : (s[..space], s[(space + 1)..].Trim());
    }

    /// <summary>Opens the program's own uninstaller (it shows its own window and confirmations). null = launched.</summary>
    public static string? StartUninstall(InstalledApp app)
    {
        if (app.Source == AppSource.Store) return RemoveStoreApp(app);
        if (string.IsNullOrWhiteSpace(app.UninstallString)) return "Este programa no tiene un desinstalador registrado.";

        var (exe, args) = ParseUninstallString(app.UninstallString);
        var isSystemTool = exe.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) ||
                           exe.Equals("msiexec", StringComparison.OrdinalIgnoreCase) ||
                           exe.Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase);
        if (!isSystemTool && !File.Exists(Environment.ExpandEnvironmentVariables(exe)))
            return "No se encontró el desinstalador del programa (puede que ya esté desinstalado a medias).";

        try
        {
            Process.Start(new ProcessStartInfo(Environment.ExpandEnvironmentVariables(exe), args) { UseShellExecute = true });
            return null;
        }
        catch (Win32Exception ex)
        {
            return ex.NativeErrorCode == 1223 ? "Cancelaste la ventana de permisos de Windows." : ex.Message;
        }
    }

    private static string? RemoveStoreApp(InstalledApp app)
    {
        if (!SafePackageName().IsMatch(app.Id) || IsBlockedStoreName(app.Name)) return "Paquete no permitido.";
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -Command \"try {{ Remove-AppxPackage -Package '{app.Id}' -ErrorAction Stop }} catch {{ [Console]::Error.WriteLine($_.Exception.Message); exit 1 }}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
            })!;
            var error = p.StandardError.ReadToEnd().Trim();
            p.WaitForExit();
            return p.ExitCode == 0 ? null : (error.Length > 0 ? error : "Windows no pudo quitar la aplicación.");
        }
        catch (Exception ex) when (ex is IOException or Win32Exception) { return ex.Message; }
    }

    [GeneratedRegex(@"^[A-Za-z0-9._~\-]+$")]
    private static partial Regex SafePackageName();
}
