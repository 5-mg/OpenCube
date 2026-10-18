using System.IO;
using System.Text.Json;

namespace OpenCube.Services;

public static class AppPaths
{
    public static string DataDir
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(local, "OpenCube");

            Directory.CreateDirectory(dir);

            // The app used to be called "Optimizer": carry over its settings and tweak backups.
            // Files are only moved when the new folder doesn't already have one, so nothing is overwritten.
            var legacy = Path.Combine(local, "Optimizer");
            try
            {
                if (Directory.Exists(legacy))
                {
                    foreach (var file in Directory.GetFiles(legacy))
                    {
                        var target = Path.Combine(dir, Path.GetFileName(file));
                        if (!File.Exists(target)) File.Move(file, target);
                    }
                    if (!Directory.EnumerateFileSystemEntries(legacy).Any()) Directory.Delete(legacy);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

            return dir;
        }
    }
}

public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public List<string> Exclusions { get; set; } = [];

    /// <summary>Sidebar shows only icons.</summary>
    public bool SidebarCollapsed { get; set; }

    private static string FilePath => Path.Combine(AppPaths.DataDir, "settings.json");

    /// <summary>Folders that are never cleaned and can't be removed from the list.</summary>
    public static IReadOnlyList<string> BuiltInExclusions =>
        [Path.Combine(Path.GetTempPath(), "claude")]; // temp files of Claude Code sessions

    public IReadOnlyList<string> AllExclusions() => BuiltInExclusions.Concat(Exclusions).ToList();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new AppSettings();
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Changes one setting without overwriting the others: several screens keep their own copy in memory,
    /// so every change reloads the file first and writes back only what it modified.
    /// </summary>
    public static void Update(Action<AppSettings> change)
    {
        var fresh = Load();
        change(fresh);
        fresh.Save();
    }
}
