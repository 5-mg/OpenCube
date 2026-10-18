using System.IO;

namespace OpenCube.Services;

public sealed record CleanupFile(string Path, long Size);

public sealed class CleanupCategory
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required IReadOnlyList<string> Roots { get; init; }
    public bool NeedsAdmin { get; init; }
    public Severity Severity { get; init; } = Severity.Low;

    /// <summary>What deleting this category will do; shown on hover next to the severity.</summary>
    public string Warning { get; init; } = "";
}

public sealed class CategoryScan
{
    public required CleanupCategory Category { get; init; }
    public required IReadOnlyList<CleanupFile> Files { get; init; }
    public long TotalBytes { get; init; }
}

public sealed record CleanupResult(int Deleted, long FreedBytes, int Skipped);

/// <summary>
/// Scan builds the exact list that is shown in the preview; Clean deletes only that list,
/// re-checking each file first. Safety rules: files only (never whole folders), only inside the
/// category roots, never inside an excluded folder, never through junctions/symlinks, and never
/// anything modified in the last 24 h.
/// </summary>
public static class CleanupService
{
    public static readonly TimeSpan MinAge = TimeSpan.FromHours(24);

    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint, // do not follow junctions or symlinks
    };

    public static IReadOnlyList<CleanupCategory> DefaultCategories()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        var list = new List<CleanupCategory>
        {
            new()
            {
                Id = "user-temp",
                Name = "Archivos temporales del usuario",
                Description = "Restos que dejan programas e instaladores en tu carpeta Temp.",
                Roots = [Path.GetTempPath()],
                Severity = Severity.Medium,
                Warning = "Va a borrar de tu carpeta Temp los archivos con más de 24 horas sin modificarse. " +
                          "Ahí no solo hay basura: también pueden estar instaladores descargados, temporales de herramientas " +
                          "de desarrollo y datos de sesiones de asistentes como Claude Code (la carpeta Temp\\claude está " +
                          "excluida por defecto). Un programa que esperaba esos archivos tendrá que regenerarlos y podrías " +
                          "perder trabajo no guardado. Revisa la lista con «Ver archivos» y añade exclusiones si dudas. No va a la papelera.",
            },
            new()
            {
                Id = "windows-temp",
                Name = "Archivos temporales de Windows",
                Description = "Carpeta Temp del sistema. Requiere ejecutar como administrador.",
                Roots = [Path.Combine(windows, "Temp")],
                NeedsAdmin = true,
                Severity = Severity.Low,
                Warning = "Va a borrar los temporales del sistema con más de 24 horas. Windows los vuelve a crear cuando los necesita. " +
                          "Los que estén en uso se omiten.",
            },
            new()
            {
                Id = "crash-dumps",
                Name = "Volcados de fallos",
                Description = "Copias de memoria que Windows guarda cuando un programa se cierra por un error.",
                Roots = [Path.Combine(local, "CrashDumps")],
                Severity = Severity.Low,
                Warning = "Va a borrar los volcados de memoria de fallos antiguos. Pierdes la posibilidad de analizar esos fallos " +
                          "pasados (por ejemplo para averiguar por qué se cerró un juego), pero no afecta al funcionamiento de Windows.",
            },
            new()
            {
                Id = "wer",
                Name = "Informes de errores de Windows",
                Description = "Informes de errores ya generados; no afectan al funcionamiento del sistema.",
                Roots =
                [
                    Path.Combine(local, "Microsoft", "Windows", "WER"),
                    Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportArchive"),
                ],
                Severity = Severity.Low,
                Warning = "Va a borrar informes de errores que Windows ya generó. Si alguna vez necesitas enviarlos a soporte " +
                          "técnico, ya no estarán; no afecta al funcionamiento del sistema.",
            },
            new()
            {
                Id = "shader-cache",
                Name = "Caché de sombreadores de DirectX",
                Description = "Se regenera sola; el primer arranque de algunos juegos puede tardar un poco más.",
                Roots = [Path.Combine(local, "D3DSCache")],
                Severity = Severity.Low,
                Warning = "Va a borrar la caché de sombreadores. Se regenera sola, pero el primer arranque de algunos juegos " +
                          "puede tardar más y notarse tirones breves hasta que se vuelva a crear.",
            },
        };

        list.AddRange(BrowserCategories(local));
        return list;
    }

    // Only the cache folders of each profile: cookies, passwords, history and sessions are never touched.
    private static IEnumerable<CleanupCategory> BrowserCategories(string local)
    {
        (string Id, string Name, string UserData)[] chromium =
        [
            ("chrome", "Google Chrome", Path.Combine(local, "Google", "Chrome", "User Data")),
            ("edge", "Microsoft Edge", Path.Combine(local, "Microsoft", "Edge", "User Data")),
            ("brave", "Brave", Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data")),
        ];

        foreach (var (id, name, userData) in chromium)
        {
            var roots = new List<string>();
            foreach (var profile in SafeDirectories(userData).Where(d => d == "Default" || d.StartsWith("Profile ")))
                foreach (var cache in new[] { "Cache", "Code Cache", "GPUCache" })
                {
                    var path = Path.Combine(userData, profile, cache);
                    if (Directory.Exists(path)) roots.Add(path);
                }
            if (roots.Count > 0) yield return BrowserCategory(id, name, roots);
        }

        var firefoxProfiles = Path.Combine(local, "Mozilla", "Firefox", "Profiles");
        var firefoxRoots = SafeDirectories(firefoxProfiles)
            .Select(p => Path.Combine(firefoxProfiles, p, "cache2"))
            .Where(Directory.Exists).ToList();
        if (firefoxRoots.Count > 0) yield return BrowserCategory("firefox", "Firefox", firefoxRoots);
    }

    private static CleanupCategory BrowserCategory(string id, string name, List<string> roots) => new()
    {
        Id = $"browser-{id}",
        Name = $"Caché de {name}",
        Description = "Archivos web guardados para cargar páginas más rápido. No borra contraseñas, cookies ni historial. Cierra el navegador para poder limpiar más.",
        Roots = roots,
        Severity = Severity.Low,
        Warning = $"Va a borrar archivos de caché de {name} con más de 24 horas. Las páginas pueden cargar más lento la primera vez. " +
                  "No toca contraseñas, cookies, historial ni sesiones iniciadas. Si el navegador está abierto, los archivos en uso se omiten.",
    };

    private static IEnumerable<string> SafeDirectories(string path)
    {
        try
        {
            return Directory.Exists(path)
                ? new DirectoryInfo(path).EnumerateDirectories().Select(d => d.Name).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    public static CategoryScan Scan(CleanupCategory category, IReadOnlyList<string>? exclusions = null, DateTime? nowUtc = null)
    {
        var cutoff = (nowUtc ?? DateTime.UtcNow) - MinAge;
        var excluded = (exclusions ?? []).Where(e => !string.IsNullOrWhiteSpace(e)).Select(NormalizeRoot).ToList();
        var files = new List<CleanupFile>();
        long total = 0;

        foreach (var root in category.Roots)
        {
            if (!IsSafeRoot(root)) continue;
            var dir = new DirectoryInfo(root);
            if (!dir.Exists || IsReparsePoint(dir)) continue;

            try
            {
                foreach (var f in dir.EnumerateFiles("*", Options))
                {
                    try
                    {
                        if (f.LastWriteTimeUtc >= cutoff) continue;
                        if (excluded.Any(x => IsInside(f.FullName, x))) continue;
                        files.Add(new CleanupFile(f.FullName, f.Length));
                        total += f.Length;
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return new CategoryScan { Category = category, Files = files, TotalBytes = total };
    }

    public static CleanupResult Clean(IReadOnlyList<CategoryScan> scans, IReadOnlyList<string>? exclusions = null, IProgress<double>? progress = null)
    {
        var cutoff = DateTime.UtcNow - MinAge;
        var excluded = (exclusions ?? []).Where(e => !string.IsNullOrWhiteSpace(e)).Select(NormalizeRoot).ToList();
        var totalFiles = scans.Sum(s => s.Files.Count);
        int deleted = 0, skipped = 0, seen = 0;
        long freed = 0;

        foreach (var scan in scans)
        {
            var roots = scan.Category.Roots.Where(IsSafeRoot).Select(NormalizeRoot).ToList();
            var touchedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in scan.Files)
            {
                seen++;
                if (seen % 50 == 0) progress?.Report((double)seen / totalFiles);

                try
                {
                    var full = Path.GetFullPath(file.Path);
                    if (!roots.Any(r => IsInside(full, r)) || excluded.Any(x => IsInside(full, x))) { skipped++; continue; }

                    var info = new FileInfo(full);
                    if (!info.Exists || IsReparsePoint(info) || info.LastWriteTimeUtc >= cutoff) { skipped++; continue; }

                    var size = info.Length;
                    info.Delete();
                    deleted++;
                    freed += size;
                    if (info.DirectoryName is { } parent) touchedDirs.Add(parent);
                }
                catch (IOException) { skipped++; }          // in use by another program
                catch (UnauthorizedAccessException) { skipped++; }
            }

            RemoveEmptyDirs(touchedDirs, roots, excluded);
        }

        progress?.Report(1);
        return new CleanupResult(deleted, freed, skipped);
    }

    // Removes directories that became empty because of our deletions, walking up but never past a root.
    private static void RemoveEmptyDirs(HashSet<string> dirs, List<string> roots, List<string> excluded)
    {
        foreach (var start in dirs.OrderByDescending(d => d.Length))
        {
            var current = start;
            while (roots.Any(r => IsInside(current + Path.DirectorySeparatorChar, r)))
            {
                if (excluded.Any(x => IsInside(current + Path.DirectorySeparatorChar, x))) break;
                try
                {
                    var info = new DirectoryInfo(current);
                    if (!info.Exists || IsReparsePoint(info) || info.EnumerateFileSystemInfos().Any()) break;
                    info.Delete(recursive: false);
                    current = Path.GetDirectoryName(current) ?? "";
                    if (current.Length == 0) break;
                }
                catch (IOException) { break; }
                catch (UnauthorizedAccessException) { break; }
            }
        }
    }

    private static bool IsReparsePoint(FileSystemInfo info) => (info.Attributes & FileAttributes.ReparsePoint) != 0;

    private static string NormalizeRoot(string root) =>
        Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

    private static bool IsInside(string fullPath, string normalizedRoot) =>
        fullPath.Length > normalizedRoot.Length && fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);

    // Refuse drive roots and other very short paths so a bad configuration can never target a whole drive.
    private static bool IsSafeRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        var full = Path.GetFullPath(root);
        return full.TrimEnd(Path.DirectorySeparatorChar).Length > 3 && Path.GetPathRoot(full) != full;
    }
}
