using System.Threading;

namespace CyberCity.IO;

/// <summary>
/// Port of text_generator.py. Kicks off a background DiskAnalyzer scan and turns
/// the results into the per-building label text used for the directory textures.
/// </summary>
public sealed class TextGenerator
{
    private readonly string? _rootPath;
    private readonly int _maxDepth;
    private readonly int _maxFiles;
    private readonly double _timeoutSeconds;
    private readonly List<FileEntry> _allScannedFiles = new();
    private readonly Lock _gate = new();
    private readonly ManualResetEventSlim _scanCompleted = new(initialState: false);

    private Task? _scanTask;
    private DiskAnalyzer? _diskAnalyzer;

    /// <summary>
    /// maxDepth/maxFiles/timeoutSeconds are hardcoded in the Python source's
    /// TextGenerator (which always constructs its DiskAnalyzer as
    /// DiskAnalyzer(max_files=10000) and takes the class's own max_depth=8,
    /// timeout_seconds=300 defaults for everything else) — exposing them here
    /// as constructor parameters, defaulted to those same values, is new surface
    /// area rather than a restoration, added so the practical scan limits (how
    /// far to recurse, how many files to read) can be set per-machine instead of
    /// baked in.
    /// </summary>
    public TextGenerator(string? rootPath = null, int maxDepth = 8, int maxFiles = 10000, double timeoutSeconds = 300)
    {
        _rootPath = rootPath;
        _maxDepth = maxDepth;
        _maxFiles = maxFiles;
        _timeoutSeconds = timeoutSeconds;

        if (!string.IsNullOrEmpty(_rootPath))
        {
            StartScanInBackground();
        }
    }

    private void StartScanInBackground()
    {
        _scanCompleted.Reset();

        var streamer = new RealTimeDataStreamer(OnFilesDiscovered);
        _diskAnalyzer = new DiskAnalyzer(maxDepth: _maxDepth, maxFiles: _maxFiles, timeoutSeconds: _timeoutSeconds, dataStreamer: streamer);

        _scanTask = Task.Run(() => RunScan(streamer));
        Console.WriteLine($"Started directory scan for {_rootPath} in a background task.");
    }

    private void RunScan(RealTimeDataStreamer streamer)
    {
        try
        {
            // The DiskAnalyzer will call OnFilesDiscovered as it finds files.
            _diskAnalyzer!.ScanDirectory(_rootPath!);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during directory scan: {ex.Message}");
        }
        finally
        {
            // Ensure any remaining accumulated files are flushed at the end of the scan.
            streamer.Flush();
            _scanCompleted.Set();
            Console.WriteLine("Directory scan task finished.");
        }
    }

    private void OnFilesDiscovered(IReadOnlyList<FileEntry> newFiles)
    {
        // Called only from the background scan task's own thread (single writer);
        // consumers below only read after WaitForScan() returns, so this lock is
        // a belt-and-braces measure rather than a hot contention point.
        lock (_gate)
        {
            _allScannedFiles.AddRange(newFiles);
        }
    }

    private List<FileEntry> WaitForScanAndSnapshot(string waitingMessage, string doneMessage)
    {
        if (_scanTask is not null && !_scanTask.IsCompleted)
        {
            Console.WriteLine(waitingMessage);
            _scanCompleted.Wait();
            Console.WriteLine(doneMessage);
        }

        lock (_gate)
        {
            return new List<FileEntry>(_allScannedFiles);
        }
    }

    /// <summary>
    /// Port of get_folder_data_with_files: every scanned directory (keyed by full
    /// path, so no name-collision issue here), with its immediate-child files
    /// attached. Sorted by descending file count then ascending display name.
    /// </summary>
    public IReadOnlyList<(string DisplayName, int Count, IReadOnlyList<FileEntry> Files)> GetFolderDataWithFiles()
    {
        List<FileEntry> files = WaitForScanAndSnapshot(
            "Waiting for directory scan to complete for detailed file data...",
            "Directory scan completed. Processing detailed file data.");

        var order = new List<string>();
        var byPath = new Dictionary<string, (string Name, int Count, List<FileEntry> Files)>();

        foreach (var entry in files)
        {
            if (entry.IsDirectory && !byPath.ContainsKey(entry.Path))
            {
                byPath[entry.Path] = (Path.GetFileName(entry.Path), 0, new List<FileEntry>());
                order.Add(entry.Path);
            }
        }

        foreach (var entry in files)
        {
            if (entry.IsDirectory)
            {
                continue;
            }

            string? parentPath = Path.GetDirectoryName(entry.Path);
            if (parentPath is not null && byPath.TryGetValue(parentPath, out var acc))
            {
                acc.Files.Add(entry);
                byPath[parentPath] = (acc.Name, acc.Count + 1, acc.Files);
            }
        }

        var result = order.Select(path =>
        {
            var (name, count, entryFiles) = byPath[path];
            string displayName = !string.IsNullOrEmpty(name) ? name.ToUpperInvariant() : Path.GetFileName(path).ToUpperInvariant();
            return (DisplayName: displayName, Count: count, Files: (IReadOnlyList<FileEntry>)entryFiles);
        });

        return result
            .OrderByDescending(r => r.Count)
            .ThenBy(r => r.DisplayName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Port of generate_text. Note: the Python original also accepted a file_count
    /// parameter that the method body never actually used — omitted here rather
    /// than carried forward as dead API surface.
    /// </summary>
    public string GenerateText(
        string? folderName = null,
        bool showSizes = true,
        int maxItems = 110,
        IReadOnlyList<FileEntry>? directoryFiles = null,
        int maxLineWidth = 150)
    {
        List<FileEntry> filesToUse;
        if (directoryFiles is not null && directoryFiles.Count > 0)
        {
            filesToUse = new List<FileEntry>(directoryFiles);
        }
        else
        {
            filesToUse = WaitForScanAndSnapshot(
                "Waiting for directory scan to complete before generating text...",
                "Directory scan completed. Generating text.");
        }

        string displayDirName = string.IsNullOrEmpty(folderName) ? "UNKNOWN DIRECTORY" : folderName;
        var generatedContent = new List<string> { $"**{displayDirName}**", string.Empty };

        if (filesToUse.Count == 0)
        {
            generatedContent.Add("No files found.");
            return string.Join('\n', generatedContent);
        }

        var displayFiles = maxItems > 0 ? filesToUse.Take(maxItems).ToList() : filesToUse;
        generatedContent.Add(GenerateSimpleList(displayFiles, showSizes, maxLineWidth));

        return string.Join('\n', generatedContent);
    }

    /// <summary>Port of format_file_size.</summary>
    public static string FormatFileSize(long sizeBytes)
    {
        if (sizeBytes == 0)
        {
            return "0 B";
        }

        string[] sizeNames = ["B", "KB", "MB", "GB", "TB"];
        double size = sizeBytes;
        int i = 0;
        while (size >= 1024 && i < sizeNames.Length - 1)
        {
            size /= 1024.0;
            i++;
        }

        return i == 0
            ? $"{(int)size} {sizeNames[i]}"
            : $"{size:F1} {sizeNames[i]}";
    }

    /// <summary>Port of generate_simple_list.</summary>
    public static string GenerateSimpleList(IReadOnlyList<FileEntry> files, bool showSizes = true, int maxWidth = 80)
    {
        if (files.Count == 0)
        {
            return "No files found";
        }

        var lines = new List<string>();
        var sorted = files.OrderBy(f => f.Path, StringComparer.Ordinal);

        foreach (var entry in sorted)
        {
            string displayString = Path.GetFileName(entry.Path);

            if (entry.IsDirectory && !displayString.EndsWith('/'))
            {
                displayString += "/";
            }

            if (showSizes && !entry.IsDirectory)
            {
                displayString += $" ({FormatFileSize(entry.Size)})";
            }

            if (displayString.Length > maxWidth)
            {
                displayString = maxWidth > 3
                    ? displayString[..(maxWidth - 3)] + "..."
                    : displayString[..maxWidth];
            }

            lines.Add(displayString);
        }

        return string.Join('\n', lines);
    }
}