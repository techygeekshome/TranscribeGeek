using System.Reflection;
using System.Security.Cryptography;
using Whisper.net.LibraryLoader;

namespace TranscribeGeek.Core.Services;

/// <summary>
/// Makes sure Whisper.net can find its own native library, whatever shape TranscribeGeek is
/// running in.
///
/// This is worth writing down, because getting it wrong shipped two versions that installed
/// perfectly and then failed on the very first file.
///
/// Whisper.net does not load its library by name. It goes looking for
/// <c>runtimes/win-x64/whisper.dll</c> underneath a short list of directories, and every
/// directory on that list is derived from where the executable is. TranscribeGeek ships as one
/// executable, so there is no <c>runtimes</c> folder next to it, and the search finds nothing:
/// "Native Library not found in default paths".
///
/// The obvious answer is to find the folder the .NET host extracted the bundle into and point
/// Whisper.net at that. That is tried first and it is cheap. But it depends on the host putting
/// the files where this code expects, in a layout nothing documents and nothing guarantees, on a
/// platform this code cannot test on. Betting the product on it is what produced 1.1.1, which
/// changed nothing a user could see.
///
/// So there is a second step that depends on nothing. The four native files are embedded in this
/// assembly as resources. If the library cannot be found anywhere, they are written out to a
/// folder under the user's own profile, in exactly the shape Whisper.net insists on, and
/// Whisper.net is pointed at that. They are under two megabytes together, they are written once
/// and checked by length and SHA-256 afterwards, so a half-written file from an interrupted first
/// run is replaced rather than used.
/// </summary>
public static class WhisperRuntime
{
    private static bool _done;
    private static readonly object Gate = new();

    /// <summary>The folder Whisper.net will load from, or null if none could be arranged.</summary>
    public static string? ResolvedPath { get; private set; }

    /// <summary>How it was found. Shown on the Settings screen, because "which copy is this
    /// actually using" is the first question when something is wrong.</summary>
    public static string Source { get; private set; } = "not looked yet";

    /// <summary>Safe to call as often as you like; it does its work once. Never throws.</summary>
    public static void Prepare()
    {
        if (_done) return;

        lock (Gate)
        {
            if (_done) return;
            _done = true;

            try
            {
                // The build check sets this so it can prove the last route works on a machine
                // where the first two always succeed. Without it that route would only ever run
                // on a user's computer, which is not where you want to find out.
                var forceEmbedded = Environment.GetEnvironmentVariable("TG_FORCE_EMBEDDED") == "1";

                // 1. Laid out normally. Every build from source, and any publish that is not a
                //    single file, lands here.
                var beside = Path.Combine(AppContext.BaseDirectory, "runtimes", Rid);
                if (!forceEmbedded && File.Exists(Path.Combine(beside, LibraryFileName)))
                {
                    Use(beside, "beside the application");
                    return;
                }

                // 2. Wherever the host put the bundle. Free when it works.
                var extracted = forceEmbedded ? null : FindExtractionDirectory();
                if (extracted is not null)
                {
                    Use(Path.Combine(extracted, "runtimes", Rid), "unpacked by the .NET host");
                    return;
                }

                // 3. Our own copy, in our own folder. This one cannot go missing.
                var written = WriteEmbeddedCopy();
                if (written is not null) Use(written, "written from the copy inside the app");
                else Source = "could not be found or written";
            }
            catch (Exception ex)
            {
                // A failure to look is not a failure to run. Whisper.net will try the ordinary
                // paths and report its own error if they are not there either.
                Source = "failed while looking: " + ex.Message;
            }
        }
    }

    private static void Use(string runtimeDirectory, string how)
    {
        // LibraryPath is read as a path to a file: Whisper.net takes its directory and appends
        // runtimes/{platform}-{architecture}. So it is given a name inside the parent of the
        // runtimes folder rather than the runtimes folder itself.
        var parent = Directory.GetParent(Directory.GetParent(runtimeDirectory)!.FullName)!.FullName;
        RuntimeOptions.LibraryPath = Path.Combine(parent, "TranscribeGeek");
        ResolvedPath = runtimeDirectory;
        Source = how;
    }

    private static string Rid =>
        OperatingSystem.IsWindows() ? $"win-{Architecture}"
        : OperatingSystem.IsMacOS() ? $"macos-{Architecture}"
        : $"linux-{Architecture}";

    private static string Architecture => System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X86 => "x86",
        System.Runtime.InteropServices.Architecture.Arm => "arm",
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        _ => "x64"
    };

    private static string LibraryFileName =>
        OperatingSystem.IsWindows() ? "whisper.dll"
        : OperatingSystem.IsMacOS() ? "libwhisper.dylib"
        : "libwhisper.so";

    /// <summary>
    /// The folder the .NET host extracted the bundle into, or null. The host uses
    /// {base}/{application file name}/{build id}; rather than working the build id out, every
    /// candidate is checked for the library itself.
    /// </summary>
    private static string? FindExtractionDirectory()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return null;

        // Both spellings: the host names the folder after the executable file, and whether that
        // keeps the extension has not been the same everywhere. The wrong one simply does not
        // exist, so checking both costs nothing.
        var names = new[] { Path.GetFileName(exe), Path.GetFileNameWithoutExtension(exe) };

        foreach (var root in ExtractionRoots())
        foreach (var name in names.Where(n => !string.IsNullOrEmpty(n)).Distinct())
        {
            var forThisApp = Path.Combine(root, name);
            if (!Directory.Exists(forThisApp)) continue;

            foreach (var candidate in Directory.EnumerateDirectories(forThisApp))
            {
                if (File.Exists(Path.Combine(candidate, "runtimes", Rid, LibraryFileName)))
                    return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> ExtractionRoots()
    {
        var custom = Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR");
        if (!string.IsNullOrWhiteSpace(custom)) yield return custom;

        yield return OperatingSystem.IsWindows()
            ? Path.Combine(Path.GetTempPath(), ".net")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".net");
    }

    /// <summary>The resource names carried for the running platform, or empty if none.</summary>
    public static IReadOnlyList<string> EmbeddedNames()
    {
        var prefix = "native." + Rid + ".";
        return typeof(WhisperRuntime).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Writes the embedded copies into the user's own profile and returns the folder, or null if
    /// there is nothing embedded for this platform. Public so the checks can call it directly
    /// rather than inferring that it worked.
    /// </summary>
    public static string? WriteEmbeddedCopy(string? intoRoot = null)
    {
        var names = EmbeddedNames();
        if (names.Count == 0) return null;

        var root = intoRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TechyGeeksHome", "TranscribeGeek", "native");

        var target = Path.Combine(root, "runtimes", Rid);
        Directory.CreateDirectory(target);

        var assembly = typeof(WhisperRuntime).Assembly;
        foreach (var name in names)
        {
            var fileName = name.Substring("native.".Length + Rid.Length + 1);
            var path = Path.Combine(target, fileName);

            using var source = assembly.GetManifestResourceStream(name);
            if (source is null) continue;

            if (AlreadyCorrect(path, source)) continue;

            source.Position = 0;
            var temp = path + ".part";
            using (var destination = File.Create(temp)) source.CopyTo(destination);
            File.Move(temp, path, overwrite: true);
        }

        return File.Exists(Path.Combine(target, LibraryFileName)) ? target : null;
    }

    /// <summary>
    /// True when the file on disk is byte for byte what is embedded. Length first because it
    /// settles it almost every time, then SHA-256, because a file left half written by a first
    /// run that was interrupted is exactly the sort of thing that produces an unexplainable bug
    /// six months later.
    /// </summary>
    private static bool AlreadyCorrect(string path, Stream embedded)
    {
        if (!File.Exists(path)) return false;
        if (new FileInfo(path).Length != embedded.Length) return false;

        embedded.Position = 0;
        var wanted = SHA256.HashData(embedded);

        using var existing = File.OpenRead(path);
        var found = SHA256.HashData(existing);

        return wanted.SequenceEqual(found);
    }
}
