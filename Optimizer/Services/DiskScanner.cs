using System.IO;

namespace OpenCube.Services;

public sealed record BigFile(string Path, long Size);

public sealed class DirNode
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public DirNode? Parent { get; init; }

    /// <summary>Total size including all subfolders.</summary>
    public long Size { get; set; }
    public int FileCount { get; set; }

    public long OwnFilesSize { get; set; }
    public int OwnFileCount { get; set; }

    public List<DirNode> Children { get; } = [];
    public List<BigFile> BigFiles { get; } = [];

    public IEnumerable<BigFile> BigFilesInTree()
    {
        var stack = new Stack<DirNode>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            foreach (var f in n.BigFiles) yield return f;
            foreach (var c in n.Children) stack.Push(c);
        }
    }
}

/// <summary>
/// Read-only folder size scanner. Never follows junctions/symlinks (no double counting, no loops) and ignores
/// cloud placeholders that are not actually stored on the disk.
/// </summary>
public sealed class DiskScanner
{
    public static long BigFileThreshold { get; set; } = 50L * 1024 * 1024;

    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = 0, // include hidden and system items too
    };

    private long _files;
    public long FilesScanned => Interlocked.Read(ref _files);

    public DirNode Scan(string root, CancellationToken ct)
    {
        var full = System.IO.Path.GetFullPath(root);
        var node = new DirNode { Path = full, Name = NameOf(full) };
        ScanDir(node, ct, parallelDepth: 2);
        return node;
    }

    private void ScanDir(DirNode node, CancellationToken ct, int parallelDepth)
    {
        ct.ThrowIfCancellationRequested();
        var subdirs = new List<DirNode>();

        try
        {
            foreach (var entry in new DirectoryInfo(node.Path).EnumerateFileSystemInfos("*", Options))
            {
                try
                {
                    if (entry is FileInfo file)
                    {
                        if ((file.Attributes & (RecallOnOpen | RecallOnDataAccess | FileAttributes.Offline)) != 0) continue;
                        var len = file.Length;
                        node.OwnFilesSize += len;
                        node.OwnFileCount++;
                        if (len >= BigFileThreshold) node.BigFiles.Add(new BigFile(file.FullName, len));
                        Interlocked.Increment(ref _files);
                    }
                    else if ((entry.Attributes & FileAttributes.ReparsePoint) == 0)
                    {
                        subdirs.Add(new DirNode { Path = entry.FullName, Name = entry.Name, Parent = node });
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        if (parallelDepth > 0 && subdirs.Count > 1)
        {
            Parallel.ForEach(subdirs,
                new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount },
                d => ScanDir(d, ct, parallelDepth - 1));
        }
        else
        {
            foreach (var d in subdirs) ScanDir(d, ct, 0);
        }

        node.Children.AddRange(subdirs);
        node.Children.Sort((a, b) => b.Size.CompareTo(a.Size));
        node.Size = node.OwnFilesSize + node.Children.Sum(c => c.Size);
        node.FileCount = node.OwnFileCount + node.Children.Sum(c => c.FileCount);
    }

    private static string NameOf(string full)
    {
        var trimmed = full.TrimEnd(System.IO.Path.DirectorySeparatorChar);
        var name = System.IO.Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? full : name;
    }
}
