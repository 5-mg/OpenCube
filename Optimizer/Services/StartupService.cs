using System.Security;
using System.Security.Principal;
using Microsoft.Win32;

namespace OpenCube.Services;

public sealed record StartupEntry(
    string Name,
    string Command,
    string Source,
    bool IsEnabled,
    RegistryHive Hive,
    RegistryView View,
    bool NeedsAdmin);

/// <summary>
/// Lists and toggles Run-key startup programs using the same StartupApproved
/// mechanism Task Manager uses, so nothing is deleted and every change is reversible.
/// </summary>
public static class StartupService
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    private static readonly (RegistryHive Hive, RegistryView View, string Label)[] Sources =
    [
        (RegistryHive.CurrentUser, RegistryView.Default, "Usuario"),
        (RegistryHive.LocalMachine, RegistryView.Registry64, "Sistema"),
        (RegistryHive.LocalMachine, RegistryView.Registry32, "Sistema (32 bits)"),
    ];

    public static bool IsAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static IReadOnlyList<StartupEntry> GetEntries()
    {
        var list = new List<StartupEntry>();
        var admin = IsAdmin();

        foreach (var (hive, view, label) in Sources)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var run = baseKey.OpenSubKey(RunPath);
                if (run is null) continue;

                var approved = ReadApproved(hive, ApprovedName(view));
                foreach (var name in run.GetValueNames())
                {
                    var command = run.GetValue(name)?.ToString() ?? "";
                    var enabled = !approved.TryGetValue(name, out var state) || IsEnabledState(state);
                    list.Add(new StartupEntry(name, command, label, enabled, hive, view,
                        NeedsAdmin: hive == RegistryHive.LocalMachine && !admin));
                }
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException) { }
        }

        return list.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <returns>null on success, otherwise a message explaining the failure.</returns>
    public static string? SetEnabled(StartupEntry entry, bool enabled)
    {
        try
        {
            // Approvals live in the 64-bit view; 32-bit machine entries use the "Run32" subkey.
            using var baseKey = RegistryKey.OpenBaseKey(entry.Hive, RegistryView.Registry64);
            using var key = baseKey.CreateSubKey(ApprovedPath + ApprovedName(entry.View), writable: true);
            key.SetValue(entry.Name, BuildState(enabled), RegistryValueKind.Binary);
            return null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
        {
            return "Necesitas ejecutar OpenCube como administrador para cambiar este programa.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static string ApprovedName(RegistryView view) => view == RegistryView.Registry32 ? "Run32" : "Run";

    private static Dictionary<string, byte[]> ReadApproved(RegistryHive hive, string subKey)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(ApprovedPath + subKey);
            if (key is null) return result;
            foreach (var name in key.GetValueNames())
                if (key.GetValue(name) is byte[] bytes) result[name] = bytes;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException) { }
        return result;
    }

    // Even first byte (02/06) = enabled, odd (03/07) = disabled.
    private static bool IsEnabledState(byte[] data) => data.Length == 0 || (data[0] & 1) == 0;

    private static byte[] BuildState(bool enabled)
    {
        var data = new byte[12];
        if (enabled)
        {
            data[0] = 0x02;
        }
        else
        {
            data[0] = 0x03;
            BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
        }
        return data;
    }
}
