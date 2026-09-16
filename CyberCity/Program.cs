using System.Globalization;
using CyberCity.App;

namespace CyberCity;

/// <summary>
/// Cyber City Directory Visualization Tool — entry point. Port of main.py.
/// </summary>
public static class Program
{
    private const int DefaultWidth = 2880;
    private const int DefaultHeight = 1620;
    private const string DefaultShaderType = "real_data";

    // Practical scan limits — genuinely system-dependent (a slow network share or
    // a home directory with 500k files needs different limits than a small project
    // folder), so these are exposed as CLI flags rather than fixed constants.
    private const int DefaultMaxDepth = 8;
    private const int DefaultMaxFiles = 10000;
    private const int DefaultMaxDirectories = 1000;

    /// <summary>Refractive index options for transparent objects (port of IOR_OPTIONS).</summary>
    private static readonly (string Name, double Value)[] IorOptions =
    {
        ("Air", 1.0),
        ("Ice", 1.31),
        ("Water", 1.333),
        ("Ethanol", 1.36),
        ("Acrylic", 1.49),
        ("Crown Glass", 1.52),
        ("Quartz", 1.54),
        ("Flint Glass", 1.66),
        ("Sapphire", 1.77),
        ("Zircon", 1.92),
        ("Diamond", 2.42),
        ("Silicon", 3.88),
    };

    private static readonly double DefaultIor = IorOptions.First(o => o.Name == "Ice").Value;

    private sealed record CliArguments(string? Directory, int Width, int Height, double Ior, int MaxDepth, int MaxFiles, int MaxDirectories);

    public static int Main(string[] args)
    {
        try
        {
            CliArguments parsed = ParseArguments(args);

            string scanPath;
            if (!string.IsNullOrEmpty(parsed.Directory))
            {
                if (ValidateDirectory(parsed.Directory))
                {
                    scanPath = parsed.Directory;
                    Console.WriteLine($"Scanning directory: {scanPath}");
                }
                else
                {
                    Console.WriteLine($"Error: '{parsed.Directory}' is not a valid directory.");
                    Console.WriteLine("Please enter a valid directory path...");
                    scanPath = GetUserDirectory();
                }
            }
            else
            {
                scanPath = GetUserDirectory();
            }

            Console.WriteLine();
            Console.WriteLine($"Initializing visualization for: {scanPath}");
            Console.WriteLine($"Resolution: {parsed.Width}x{parsed.Height}");
            Console.WriteLine($"Index of Refraction: {parsed.Ior}");
            Console.WriteLine($"Scan limits: max depth {parsed.MaxDepth}, max files {parsed.MaxFiles}, max city blocks {parsed.MaxDirectories}");
            Console.WriteLine();

            using var app = new Raymarcher(parsed.Width, parsed.Height, (float)parsed.Ior, DefaultShaderType, scanPath,
                parsed.MaxDepth, parsed.MaxFiles, parsed.MaxDirectories);

            Console.WriteLine("Starting Cyber City visualization...");
            app.Run();
        }
        catch (Exception ex)
        {
            // NOTE: the Python original also specifically catches KeyboardInterrupt (Ctrl+C)
            // to print a friendlier message before exiting. Doing that faithfully in .NET
            // means cooperatively cancelling a blocking Console.ReadLine() mid-read, which
            // needs real cancellation plumbing around console I/O to do without a fragile
            // Environment.Exit-from-a-signal-handler hack. Left as a known gap rather than
            // faked — Ctrl+C here just terminates immediately via the default runtime behavior.
            Console.WriteLine($"An error occurred during application runtime: {ex.Message}");
            Console.WriteLine(ex);
            return 1;
        }
        finally
        {
            Console.WriteLine("Application exited.");
        }

        return 0;
    }

    /// <summary>Port of get_user_directory.</summary>
    private static string GetUserDirectory()
    {
        Console.WriteLine("=== Cyber City Directory Scanner ===");

        while (true)
        {
            Console.Write($"Enter directory to scan (press Enter for current directory '{Directory.GetCurrentDirectory()}'): ");
            string? line = Console.ReadLine();

            if (line is null)
            {
                // Console.ReadLine() returns null on EOF (e.g. redirected/closed stdin) —
                // the analog of Python's EOFError here.
                Console.WriteLine();
                Console.WriteLine("No input received. Using current directory.");
                return Directory.GetCurrentDirectory();
            }

            string userInput = line.Trim();
            if (userInput.Length == 0)
            {
                return Directory.GetCurrentDirectory();
            }

            if (ValidateDirectory(userInput))
            {
                return userInput;
            }

            Console.WriteLine($"Error: '{userInput}' is not a valid directory.");
            Console.WriteLine();
        }
    }

    /// <summary>
    /// Port of validate_directory. Directory.Exists already implies both "exists"
    /// and "is a directory" in one call (Python does these as two separate checks).
    /// The read-permission check is emulated by attempting one directory-entry read,
    /// since .NET has no direct equivalent of os.access(path, os.R_OK).
    /// </summary>
    private static bool ValidateDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return false;
            }

            using var enumerator = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
            enumerator.MoveNext();
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>Port of parse_arguments, plus --max-depth/--max-files/--max-directories (new — see the constants above).</summary>
    private static CliArguments ParseArguments(string[] args)
    {
        string? directory = null;
        int width = DefaultWidth;
        int height = DefaultHeight;
        double ior = DefaultIor;
        int maxDepth = DefaultMaxDepth;
        int maxFiles = DefaultMaxFiles;
        int maxDirectories = DefaultMaxDirectories;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--width" when i + 1 < args.Length:
                    width = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;

                case "--height" when i + 1 < args.Length:
                    height = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;

                case "--ior" when i + 1 < args.Length:
                    ior = double.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;

                case "--max-depth" when i + 1 < args.Length:
                    maxDepth = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;

                case "--max-files" when i + 1 < args.Length:
                    maxFiles = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;

                case "--max-directories" when i + 1 < args.Length:
                    maxDirectories = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;

                case "-h":
                case "--help":
                    PrintHelp();
                    Environment.Exit(0);
                    break;

                default:
                    if (directory is null && !args[i].StartsWith('-'))
                    {
                        directory = args[i];
                    }
                    break;
            }
        }

        return new CliArguments(directory, width, height, ior, maxDepth, maxFiles, maxDirectories);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Cyber City Directory Visualization Tool");
        Console.WriteLine();
        Console.WriteLine("Usage: CyberCity [directory] [--width N] [--height N] [--ior N]");
        Console.WriteLine("                 [--max-depth N] [--max-files N] [--max-directories N]");
        Console.WriteLine();
        Console.WriteLine("  directory              Directory to scan and visualize (if not provided, you'll be prompted to enter one)");
        Console.WriteLine($"  --width N              Render width in pixels (default: {DefaultWidth})");
        Console.WriteLine($"  --height N             Render height in pixels (default: {DefaultHeight})");
        Console.WriteLine($"  --ior N                Index of refraction for transparent objects (default: {DefaultIor})");
        Console.WriteLine($"  --max-depth N          How many directory levels deep to recurse (default: {DefaultMaxDepth})");
        Console.WriteLine($"  --max-files N          Maximum number of files/directories to scan (default: {DefaultMaxFiles})");
        Console.WriteLine($"  --max-directories N    Maximum number of top-level directories rendered as city blocks (default: {DefaultMaxDirectories})");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  CyberCity                              # Interactive directory input");
        Console.WriteLine("  CyberCity /path/to/dir                 # Scan specific directory");
        Console.WriteLine("  CyberCity /path/to/dir --max-files 2000 --max-depth 4   # Smaller/faster scan");
    }
}