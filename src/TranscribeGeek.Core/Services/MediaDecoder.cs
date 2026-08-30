using System.Diagnostics;

namespace TranscribeGeek.Core.Services;

/// <summary>
/// Turns whatever the user dropped in into the 16 kHz mono 16-bit WAV that Whisper needs.
///
/// Two paths, deliberately:
///
/// 1. If the file is already a 16 kHz mono PCM WAV, it is used as-is. No decode, no temp file.
/// 2. Anything else - MP3, M4A, MP4, MKV, FLAC, OGG, WMA, MOV - needs ffmpeg.
///
/// **ffmpeg is invoked as a separate process, never linked.** That is not a style choice: the
/// commonly distributed ffmpeg builds are GPL, and linking one into this application would
/// impose licence terms on it that we have not signed up to. Running it as a child process and
/// reading its output is explicitly fine, and is what every well-behaved application does.
///
/// It is also not bundled. If ffmpeg is not present, the app says so plainly and still handles
/// WAV, rather than silently downloading a 90 MB binary the user did not ask for.
/// </summary>
public sealed class MediaDecoder
{
    public const int RequiredSampleRate = 16_000;

    /// <summary>Extensions we will accept on the drop target.</summary>
    public static readonly string[] SupportedExtensions =
    {
        ".wav", ".mp3", ".m4a", ".mp4", ".mkv", ".mov", ".avi",
        ".flac", ".ogg", ".opus", ".wma", ".aac", ".webm", ".m4v"
    };

    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    private static string? _ffmpegPath;
    private static bool _ffmpegChecked;

    /// <summary>
    /// Where ffmpeg is, or null. Looks beside the executable first so a portable copy can be
    /// dropped next to the app, then falls back to PATH.
    /// </summary>
    public static string? FindFfmpeg()
    {
        if (_ffmpegChecked) return _ffmpegPath;
        _ffmpegChecked = true;

        var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

        var beside = Path.Combine(AppContext.BaseDirectory, exe);
        if (File.Exists(beside)) { _ffmpegPath = beside; return _ffmpegPath; }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), exe);
                if (File.Exists(candidate)) { _ffmpegPath = candidate; return _ffmpegPath; }
            }
            catch (ArgumentException) { /* a malformed PATH entry is not our problem */ }
        }

        return _ffmpegPath;
    }

    public static bool FfmpegAvailable => FindFfmpeg() is not null;

    /// <summary>
    /// Reads the WAV header to decide whether a file can go straight to Whisper. Only a plain
    /// 16-bit PCM mono file at the right rate qualifies; anything cleverer goes through ffmpeg.
    /// </summary>
    public static bool IsAlreadyUsableWav(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            if (new string(br.ReadChars(4)) != "RIFF") return false;
            br.ReadInt32();
            if (new string(br.ReadChars(4)) != "WAVE") return false;

            while (fs.Position < fs.Length - 8)
            {
                var chunkId = new string(br.ReadChars(4));
                var chunkSize = br.ReadInt32();
                if (chunkId == "fmt ")
                {
                    var format = br.ReadInt16();      // 1 = PCM
                    var channels = br.ReadInt16();
                    var rate = br.ReadInt32();
                    br.ReadInt32();                   // byte rate
                    br.ReadInt16();                   // block align
                    var bits = br.ReadInt16();
                    return format == 1 && channels == 1 && rate == RequiredSampleRate && bits == 16;
                }
                fs.Seek(chunkSize, SeekOrigin.Current);
            }
        }
        catch (Exception)
        {
            // A file we cannot parse is a file we should hand to ffmpeg, not reject.
            return false;
        }

        return false;
    }

    /// <summary>
    /// Produces a path to a 16 kHz mono WAV for <paramref name="sourcePath"/>. The second value
    /// says whether it is a temporary file the caller must delete.
    /// </summary>
    public static async Task<(string Path, bool IsTemporary)> ToWhisperWavAsync(
        string sourcePath, CancellationToken ct = default)
    {
        if (IsAlreadyUsableWav(sourcePath))
            return (sourcePath, false);

        var ffmpeg = FindFfmpeg()
            ?? throw new MediaDecodeException(
                $"{Path.GetExtension(sourcePath)} files need ffmpeg, and it was not found on this machine. " +
                "Put ffmpeg.exe next to TranscribeGeek, or convert the file to a 16 kHz mono WAV first.");

        var temp = Path.Combine(Path.GetTempPath(),
            $"transcribegeek-{Guid.NewGuid():N}.wav");

        var psi = new ProcessStartInfo(ffmpeg)
        {
            // -vn drops any video stream; -ac 1 mono; -ar 16000 the rate Whisper wants;
            // -f wav so the container is unambiguous whatever the extension says.
            Arguments = $"-hide_banner -loglevel error -nostdin -y -i \"{sourcePath}\" " +
                        $"-vn -ac 1 -ar {RequiredSampleRate} -c:a pcm_s16le -f wav \"{temp}\"",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi)
            ?? throw new MediaDecodeException("ffmpeg would not start.");

        var stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);

        if (proc.ExitCode != 0 || !File.Exists(temp))
        {
            TryDelete(temp);
            var detail = string.IsNullOrWhiteSpace(stderr) ? "" : " " + stderr.Trim().Split('\n').Last();
            throw new MediaDecodeException($"ffmpeg could not read that file.{detail}");
        }

        return (temp, true);
    }

    /// <summary>Media length, via ffprobe if it is there. Null rather than throwing - it is only used for display.</summary>
    public static async Task<TimeSpan?> TryGetDurationAsync(string path, CancellationToken ct = default)
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return null;

        var probe = Path.Combine(Path.GetDirectoryName(ffmpeg)!,
            OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        if (!File.Exists(probe)) return null;

        try
        {
            var psi = new ProcessStartInfo(probe)
            {
                Arguments = $"-v error -show_entries format=duration -of default=nw=1:nk=1 \"{path}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var s = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            return double.TryParse(s.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var secs)
                ? TimeSpan.FromSeconds(secs)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* a temp file we cannot delete is not worth failing a job over */ }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed class MediaDecodeException : Exception
{
    public MediaDecodeException(string message) : base(message) { }
}
