using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text.Json;
using Microsoft.Win32;

namespace OpenCube.Services;

/// <summary>One registry value under HKEY_CURRENT_USER. Default is what Windows uses when nothing was backed up (null = value absent).</summary>
public sealed record RegValue(string Path, string Name, object Value, RegistryValueKind Kind, object? Default = null);

public sealed record Tweak(
    string Id,
    string Category,
    string Title,
    string Description,
    string Note,
    IReadOnlyList<RegValue> Values,
    bool NeedsRelogin = false,
    Severity Severity = Severity.Low);

public enum TweakState { Off, On, Partial }

public sealed class BackupEntry
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Existed { get; set; }
    public string Kind { get; set; } = "";
    public string? Text { get; set; }
    public long? Number { get; set; }
}

/// <summary>
/// Applies registry tweaks that only affect the current user (no administrator needed). Before the first change
/// of a tweak its original values are written to backups.json, so reverting restores exactly what was there.
/// </summary>
public static class TweakService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string BackupFile { get; set; } = System.IO.Path.Combine(AppPaths.DataDir, "backups.json");

    public static IReadOnlyList<Tweak> All { get; } = BuildTweaks();

    public static TweakState GetState(Tweak tweak)
    {
        var on = tweak.Values.Count(v => Matches(Read(v), v));
        return on == 0 ? TweakState.Off : on == tweak.Values.Count ? TweakState.On : TweakState.Partial;
    }

    public static bool HasBackup(Tweak tweak) => LoadBackups().ContainsKey(tweak.Id);

    /// <returns>null on success, otherwise a message explaining the failure.</returns>
    public static string? Apply(Tweak tweak)
    {
        try
        {
            var backups = LoadBackups();
            if (!backups.ContainsKey(tweak.Id))
            {
                backups[tweak.Id] = tweak.Values.Select(Capture).ToList();
                SaveBackups(backups); // written before touching the registry
            }

            foreach (var v in tweak.Values)
            {
                using var key = Registry.CurrentUser.CreateSubKey(v.Path, writable: true);
                key.SetValue(v.Name, v.Value, v.Kind);
            }
            return null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return "No se pudo aplicar el ajuste: " + ex.Message;
        }
    }

    /// <returns>null on success, otherwise a message explaining the failure.</returns>
    public static string? Revert(Tweak tweak)
    {
        try
        {
            var backups = LoadBackups();
            if (backups.TryGetValue(tweak.Id, out var saved))
            {
                foreach (var b in saved) Restore(b);
                backups.Remove(tweak.Id);
                SaveBackups(backups);
            }
            else
            {
                foreach (var v in tweak.Values) // applied outside this app: fall back to Windows' defaults
                {
                    using var key = Registry.CurrentUser.CreateSubKey(v.Path, writable: true);
                    if (v.Default is null) key.DeleteValue(v.Name, throwOnMissingValue: false);
                    else key.SetValue(v.Name, v.Default, v.Kind);
                }
            }
            return null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return "No se pudo revertir el ajuste: " + ex.Message;
        }
    }

    /// <summary>Creates a Windows restore point. Needs administrator rights and System Protection turned on.</summary>
    public static string? CreateRestorePoint()
    {
        if (!StartupService.IsAdmin())
            return "Crear un punto de restauración requiere ejecutar OpenCube como administrador.";

        const string script =
            "try { Checkpoint-Computer -Description 'OpenCube' -RestorePointType MODIFY_SETTINGS -ErrorAction Stop -WarningAction Stop } " +
            "catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }";
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -Command \"{script}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            })!;
            var error = p.StandardError.ReadToEnd().Trim();
            p.WaitForExit();
            return p.ExitCode == 0 ? null : (error.Length > 0 ? error : "Windows no pudo crear el punto de restauración.");
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            return ex.Message;
        }
    }

    private static object? Read(RegValue v)
    {
        using var key = Registry.CurrentUser.OpenSubKey(v.Path);
        return key?.GetValue(v.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }

    private static bool Matches(object? current, RegValue v)
    {
        if (current is null) return false;
        return v.Kind == RegistryValueKind.DWord
            ? current is int i && i == Convert.ToInt32(v.Value)
            : string.Equals(current as string, Convert.ToString(v.Value), StringComparison.Ordinal);
    }

    // Records what to restore. A value that already equals the tweaked value (set outside this app) is recorded
    // as "Windows' default" so reverting does not bring the tweak back.
    private static BackupEntry Capture(RegValue v)
    {
        var current = Read(v);
        var entry = new BackupEntry { Path = v.Path, Name = v.Name, Kind = v.Kind.ToString() };

        if (current is not null && !Matches(current, v))
        {
            entry.Existed = true;
            if (current is int i) entry.Number = i; else entry.Text = Convert.ToString(current);
        }
        else if (current is not null && v.Default is not null)
        {
            entry.Existed = true;
            if (v.Kind == RegistryValueKind.DWord) entry.Number = Convert.ToInt32(v.Default); else entry.Text = Convert.ToString(v.Default);
        }
        return entry;
    }

    private static void Restore(BackupEntry b)
    {
        using var key = Registry.CurrentUser.CreateSubKey(b.Path, writable: true);
        if (!b.Existed) { key.DeleteValue(b.Name, throwOnMissingValue: false); return; }

        if (b.Kind == nameof(RegistryValueKind.DWord)) key.SetValue(b.Name, (int)(b.Number ?? 0), RegistryValueKind.DWord);
        else key.SetValue(b.Name, b.Text ?? "", RegistryValueKind.String);
    }

    private static Dictionary<string, List<BackupEntry>> LoadBackups()
    {
        try
        {
            if (File.Exists(BackupFile))
                return JsonSerializer.Deserialize<Dictionary<string, List<BackupEntry>>>(File.ReadAllText(BackupFile)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return [];
    }

    private static void SaveBackups(Dictionary<string, List<BackupEntry>> backups)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(BackupFile)!);
        File.WriteAllText(BackupFile, JsonSerializer.Serialize(backups, JsonOptions));
    }

    private static IReadOnlyList<Tweak> BuildTweaks()
    {
        const string cdm = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
        const string advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
        var dword = RegistryValueKind.DWord;
        var str = RegistryValueKind.String;

        return
        [
            new Tweak("gamedvr", "Juegos",
                "Desactivar la grabación en segundo plano de Xbox Game Bar",
                "Evita que Windows grabe clips de fondo mientras juegas, lo que libera algo de CPU y GPU.",
                "Dejarás de poder usar la opción de guardar los últimos segundos de juego.",
                [
                    new(@"System\GameConfigStore", "GameDVR_Enabled", 0, dword, 1),
                    new(@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, dword, 1),
                ]),

            new Tweak("mouse-accel", "Juegos",
                "Desactivar la aceleración del ratón",
                "El cursor se mueve siempre la misma distancia para el mismo movimiento de mano; más predecible al apuntar.",
                "Se nota distinto al principio. Se aplica del todo al volver a iniciar sesión.",
                [
                    new(@"Control Panel\Mouse", "MouseSpeed", "0", str, "1"),
                    new(@"Control Panel\Mouse", "MouseThreshold1", "0", str, "6"),
                    new(@"Control Panel\Mouse", "MouseThreshold2", "0", str, "10"),
                ], NeedsRelogin: true),

            new Tweak("menu-delay", "Interfaz y rapidez",
                "Menús más rápidos",
                "Reduce de 400 ms a 50 ms la espera antes de que se abran los submenús.",
                "Se aplica al volver a iniciar sesión.",
                [new(@"Control Panel\Desktop", "MenuShowDelay", "50", str, "400")], NeedsRelogin: true),

            new Tweak("transparency", "Interfaz y rapidez",
                "Desactivar los efectos de transparencia",
                "Quita el efecto de cristal de menús y barras. Ayuda algo en equipos modestos.",
                "Cambia el aspecto de Windows; se aplica al instante.",
                [new(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0, dword, 1)]),

            new Tweak("startup-delay", "Interfaz y rapidez",
                "Quitar el retraso de los programas de inicio",
                "Windows espera unos segundos antes de abrir tus programas de inicio; con esto arrancan de inmediato.",
                "Justo al iniciar sesión habrá más carga a la vez. Se aplica al volver a iniciar sesión.",
                [new(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec", 0, dword)],
                NeedsRelogin: true),

            new Tweak("file-ext", "Comodidad",
                "Mostrar las extensiones de los archivos",
                "Ves el nombre completo (por ejemplo foto.jpg). Ayuda a detectar archivos disfrazados como documentos.",
                "Se aplica al abrir una ventana nueva del Explorador.",
                [new(advanced, "HideFileExt", 0, dword, 1)]),

            new Tweak("ads-id", "Privacidad y sugerencias",
                "Desactivar el identificador de publicidad",
                "Impide que las aplicaciones usen un identificador tuyo para personalizar anuncios.",
                "Los anuncios seguirán apareciendo, pero menos personalizados.",
                [new(@"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0, dword, 1)]),

            new Tweak("suggestions", "Privacidad y sugerencias",
                "Desactivar sugerencias, consejos y anuncios de Windows",
                "Quita las aplicaciones sugeridas en Inicio, los consejos y las promociones que Windows muestra por su cuenta.",
                "Dejarás de ver novedades que Windows promociona.",
                [
                    new(cdm, "SubscribedContent-338388Enabled", 0, dword, 1),
                    new(cdm, "SubscribedContent-338389Enabled", 0, dword, 1),
                    new(cdm, "SubscribedContent-310093Enabled", 0, dword, 1),
                    new(cdm, "SubscribedContent-353694Enabled", 0, dword, 1),
                    new(cdm, "SubscribedContent-353696Enabled", 0, dword, 1),
                    new(cdm, "SystemPaneSuggestionsEnabled", 0, dword, 1),
                    new(cdm, "SoftLandingEnabled", 0, dword, 1),
                ]),
        ];
    }
}
