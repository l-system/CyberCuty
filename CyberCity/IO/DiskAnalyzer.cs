using System.Diagnostics;
using System.Threading;

namespace CyberCity.IO;

/// <summary>
/// Port of disk_analyzer.py's FileInfo dataclass.
/// Named FileEntry (not FileInfo) to avoid colliding with System.IO.FileInfo.
/// </summary>
public sealed record FileEntry(
    string Path,
    long Size,
    bool IsDirectory,
    int Depth,
    DateTimeOffset ModifiedUtc);

/// <summary>
/// Streams FileEntry batches to a callback as they're discovered, flushing on
/// an interval. Port of disk_analyzer.py's RealTimeDataStreamer.
/// </summary>
public sealed class RealTimeDataStreamer
{
    private readonly Action<IReadOnlyList<FileEntry>> _callback;
    private readonly TimeSpan _interval;
    private readonly List<FileEntry> _accumulated = new();
    private readonly Lock _gate = new();
    private long _lastUpdateTicks;

    public RealTimeDataStreamer(Action<IReadOnlyList<FileEntry>> callback, double updateIntervalSeconds = 0.5)
    {
        _callback = callback;
        _interval = TimeSpan.FromSeconds(updateIntervalSeconds);
        _lastUpdateTicks = Stopwatch.GetTimestamp();
    }

    public void AddFile(FileEntry entry)
    {
        lock (_gate)
        {
            _accumulated.Add(entry);

            var elapsed = Stopwatch.GetElapsedTime(_lastUpdateTicks);
            if (elapsed >= _interval)
            {
                FlushLocked();
            }
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            FlushLocked();
        }
    }

    private void FlushLocked()
    {
        if (_accumulated.Count == 0)
        {
            return;
        }

        var batch = _accumulated.ToArray();
        _accumulated.Clear();
        _lastUpdateTicks = Stopwatch.GetTimestamp();

        try
        {
            _callback(batch);
        }
        catch
        {
            // Match the Python original: a callback failure must not take down the scan thread.
        }
    }
}

/// <summary>
/// Recursive directory scanner. Port of disk_analyzer.py's DiskAnalyzer.
/// </summary>
public sealed class DiskAnalyzer
{
    private readonly int _maxDepth;
    private readonly int _maxFiles;
    private readonly double _timeoutSeconds;
    private readonly RealTimeDataStreamer? _dataStreamer;

    private List<FileEntry> _files = new();
    private Stopwatch _stopwatch = Stopwatch.StartNew();

    public IReadOnlyList<FileEntry> Files => _files;

    // NOTE: the Python source accepts a follow_symlinks constructor argument but then
    // unconditionally sets self.follow_symlinks = False in __init__, so symlinks are
    // never followed regardless of what's passed in. That behavior is preserved here:
    // there is intentionally no followSymlinks parameter, and reparse points/symlinks
    // are always skipped. Flag this to the user if that turns out to be unintended.
    public DiskAnalyzer(int maxDepth = 8, int maxFiles = 10000, double timeoutSeconds = 300,
        RealTimeDataStreamer? dataStreamer = null)
    {
        _maxDepth = maxDepth;
        _maxFiles = maxFiles;
        _timeoutSeconds = timeoutSeconds;
        _dataStreamer = dataStreamer;
    }

    public IReadOnlyList<FileEntry> ScanDirectory(string rootPath)
    {
        var root = new DirectoryInfo(Path.GetFullPath(rootPath));
        if (!root.Exists)
        {
            throw new ArgumentException($"Invalid directory: {rootPath}");
        }

        _files = new List<FileEntry>();
        _stopwatch = Stopwatch.StartNew();

        foreach (var entry in Walk(root, 0))
        {
            _files.Add(entry);
        }

        _files.Sort(CompareEntries);
        return _files;
    }

    /// <summary>
    /// Mirrors DiskAnalyzer._sort_key: depth, then parent path (case-insensitive),
    /// then directories-before-files, then name (case-insensitive).
    /// </summary>
    private static int CompareEntries(FileEntry a, FileEntry b)
    {
        int cmp = a.Depth.CompareTo(b.Depth);
        if (cmp != 0) return cmp;

        string parentA = Path.GetDirectoryName(a.Path)?.ToLowerInvariant() ?? string.Empty;
        string parentB = Path.GetDirectoryName(b.Path)?.ToLowerInvariant() ?? string.Empty;
        cmp = string.CompareOrdinal(parentA, parentB);
        if (cmp != 0) return cmp;

        int typeA = a.IsDirectory ? 0 : 1;
        int typeB = b.IsDirectory ? 0 : 1;
        cmp = typeA.CompareTo(typeB);
        if (cmp != 0) return cmp;

        string nameA = Path.GetFileName(a.Path).ToLowerInvariant();
        string nameB = Path.GetFileName(b.Path).ToLowerInvariant();
        return string.CompareOrdinal(nameA, nameB);
    }

    private IEnumerable<FileEntry> Walk(DirectoryInfo directory, int depth)
    {
        if (depth > _maxDepth)
        {
            yield break;
        }

        List<FileSystemInfo> entries;
        try
        {
            entries = directory.EnumerateFileSystemInfos().ToList();
            // Directories first, then files, both alphanumeric (case-insensitive) —
            // matches the traversal order in _walk (final results are re-sorted anyway).
            entries.Sort((x, y) =>
            {
                int typeX = x is DirectoryInfo ? 0 : 1;
                int typeY = y is DirectoryInfo ? 0 : 1;
                int cmp = typeX.CompareTo(typeY);
                return cmp != 0 ? cmp : string.CompareOrdinal(x.Name.ToLowerInvariant(), y.Name.ToLowerInvariant());
            });
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            yield break;
        }

        foreach (var entry in entries)
        {
            if (_stopwatch.Elapsed.TotalSeconds > _timeoutSeconds)
            {
                yield break;
            }

            if (_files.Count >= _maxFiles)
            {
                yield break;
            }

            // follow_symlinks is always false — see the constructor note above.
            if (entry.LinkTarget is not null)
            {
                continue;
            }

            FileEntry fileEntry;
            try
            {
                bool isDirectory = entry is DirectoryInfo;

                // .NET has no cross-platform equivalent of Python's stat().st_size for a
                // directory node itself (e.g. Linux's 4096-byte directory inode size).
                // Directories are reported with Size = 0 rather than an OS-specific value.
                long size = entry is FileInfo fi ? fi.Length : 0;

                fileEntry = new FileEntry(
                    Path: entry.FullName,
                    Size: size,
                    IsDirectory: isDirectory,
                    Depth: depth + 1,
                    ModifiedUtc: entry.LastWriteTimeUtc);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            _dataStreamer?.AddFile(fileEntry);
            yield return fileEntry;

            if (fileEntry.IsDirectory && entry is DirectoryInfo subDirectory)
            {
                foreach (var nested in Walk(subDirectory, depth + 1))
                {
                    yield return nested;
                }
            }
        }
    }
}
