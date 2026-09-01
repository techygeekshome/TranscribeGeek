using Whisper.net.LibraryLoader;

namespace TranscribeGeek.Core.Services;

/// <summary>
/// Points Whisper.net at its own native libraries when TranscribeGeek is running as a single
/// executable.
///
/// The problem, which is worth writing down because it produced a version that installed
/// perfectly and then failed on the first file:
///
/// TranscribeGeek ships as one file. The .NET host bundles the native libraries inside the
/// executable and, at startup, extracts them to a temporary folder. Whisper.net does not use
/// ordinary name-based P/Invoke to find its library; it goes looking for
/// <c>runtimes/win-x64/whisper.dll</c> underneath a short list of directories, and every
/// directory on that list points at where the executable is rather than at where the host put
/// the extracted files. There is no <c>runtimes</c> folder next to the executable, because
/// there is only the executable, so the search finds nothing and reports
/// "Native Library not found in default paths".
///
/// Nothing about this is visible in a normal build: run from a build folder, or from a publish
/// that is not a single file, and the <c>runtimes</c> folder is sitting right there. It only
/// appears in the packaged product, which is exactly where it is least welcome.
///
/// The fix is to find the extraction folder and hand it to Whisper.net through
/// <c>RuntimeOptions.LibraryPath</c>, which is the first place its own search looks. The folder
/// is not exposed by any API, but the host puts it somewhere predictable: a per-application
/// folder under the bundle extraction base, with one subfolder per build. So this looks in that
/// one place, checks each subfolder actually contains the library, and stops.
/// </summary>
public static class WhisperRuntime
{
    private static bool _done;
    private static readonly object Gate = new();

    /// <summary>
    /// Where the library was found, or null. Shown on the Settings screen, because "which copy
    /// of whisper.dll is this actually using" is the first question when something is wrong.
    /// </summary>
    public static string? ResolvedPath { get; private set; }

    /// <summary>
    /// Safe to call as often as you like; it does its work once. Never throws: if it cannot
    /// work out where the library is, it leaves Whisper.net to try on its own and report its
    /// own error, which is a better message than anything invented here.
    /// </summary>
    public static void Prepare()
    {
        if (_done) return;

        lock (Gate)
        {
            if (_done) return;
            _done = true;

            try
            {
                // Already laid out normally, which is every case except the packaged one.
                var beside = Path.Combine(AppContext.BaseDirectory, "runtimes", Rid);
                if (File.Exists(Path.Combine(beside, LibraryFileName)))
                {
                    ResolvedPath = beside;
                    return;
                }

                var extracted = FindExtractionDirectory();
                if (extracted is null) return;

                // LibraryPath is treated as a path to a file: Whisper.net takes its directory
                // and appends runtimes/{platform}-{architecture}. So it is given a name inside
                // the extraction folder rather than the folder itself.
                RuntimeOptions.LibraryPath = Path.Combine(extracted, "TranscribeGeek");
                ResolvedPath = Path.Combine(extracted, "runtimes", Rid);
            }
            catch (Exception)
            {
                // A failure to look is not a failure to run. Whisper.net will try the ordinary
                // paths and say so itself if they are not there either.
            }
        }
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
    /// The folder the .NET host extracted the bundled native libraries into, or null if this is
    /// not a single-file build or the folder cannot be found.
    ///
    /// The host uses {base}/{application name}/{build id}. The base is DOTNET_BUNDLE_EXTRACT_BASE_DIR
    /// when it is set, and otherwise the platform's own default. The build id changes with every
    /// build, so rather than trying to work it out, each candidate is checked for the library
    /// itself. There are normally one or two.
    /// </summary>
    private static string? FindExtractionDirectory()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return null;

        // Both spellings, because they are not always the same thing. The host names the folder
        // after the executable file; on Windows that is TranscribeGeek.exe and dropping the
        // extension is right, but a name that merely contains a dot has no extension to drop and
        // trimming one takes a piece of the name off. Checking both costs nothing and the wrong
        // one simply does not exist.
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
}
