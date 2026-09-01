using System.Security.Cryptography;
using TranscribeGeek.Core.Models;

namespace TranscribeGeek.Core.Services;

/// <summary>
/// The two models that working out who is speaking needs, and where they live on disk.
///
/// One of them decides when the voice on the recording changes; the other turns each stretch
/// of speech into a fingerprint so the same voice can be recognised later on. Neither is in
/// the installer, for the same reason no Whisper model is: most people never turn the feature
/// on, and 36 MB nobody asked for is 36 MB nobody asked for.
///
/// Every download is checked against a size and a SHA-256 recorded here before it is kept. A
/// file that does not match is deleted rather than used, so the app runs the exact models it
/// was tested with or it runs none at all.
/// </summary>
public static class SpeakerModelCatalog
{
    /// <summary>A subfolder of the speech model directory, so Settings only has one path to show.</summary>
    public static string Directory { get; } =
        Path.Combine(ModelCatalog.ModelDirectory, "speakers");

    /// <summary>Splits the recording into stretches of one voice.</summary>
    public static readonly SpeakerModelFile Segmentation = new(
        "pyannote-segmentation-3-0.onnx",
        "https://huggingface.co/csukuangfj/sherpa-onnx-pyannote-segmentation-3-0/resolve/main/model.onnx",
        5_992_913,
        "220ad67ca923bef2fa91f2390c786097bf305bceb5e261d4af67b38e938e1079",
        "pyannote segmentation 3.0, CNRS, MIT");

    /// <summary>Turns a stretch of speech into a fingerprint that can be matched to another stretch.</summary>
    public static readonly SpeakerModelFile Embedding = new(
        "campplus-voxceleb-16k.onnx",
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/3dspeaker_speech_campplus_sv_en_voxceleb_16k.onnx",
        29_596_978,
        "357a834f702b80161e5b981182c038e18553c1f2ca752ed6cec2052365d4129b",
        "CAM++ from 3D-Speaker, Apache-2.0");

    public static IReadOnlyList<SpeakerModelFile> All { get; } = new[] { Segmentation, Embedding };

    public static long TotalBytes => All.Sum(f => f.Bytes);

    public static string PathFor(SpeakerModelFile file) => Path.Combine(Directory, file.FileName);

    public static bool IsDownloaded(SpeakerModelFile file)
    {
        var p = PathFor(file);
        return File.Exists(p) && new FileInfo(p).Length == file.Bytes;
    }

    /// <summary>Both files, or the feature cannot run.</summary>
    public static bool IsReady => All.All(IsDownloaded);

    public static long SizeOnDisk => All.Where(IsDownloaded).Sum(f => new FileInfo(PathFor(f)).Length);

    /// <summary>
    /// Fetches whatever is missing. Progress is across the pack as a whole rather than per file,
    /// because from the outside this is one thing being downloaded, not two.
    /// </summary>
    public static async Task DownloadAsync(
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        System.IO.Directory.CreateDirectory(Directory);

        var outstanding = All.Where(f => !IsDownloaded(f)).ToList();
        var wanted = outstanding.Sum(f => f.Bytes);
        long doneBefore = 0;

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TranscribeGeek");

        foreach (var file in outstanding)
        {
            var final = PathFor(file);
            var part = final + ".part";
            if (File.Exists(part)) File.Delete(part);

            var carried = doneBefore;

            using (var response = await http
                       .GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, ct)
                       .ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();

                await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dest = File.Create(part);

                var buffer = new byte[1 << 20];
                long got = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await dest.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    got += read;
                    if (wanted > 0)
                        progress?.Report(Math.Min(1.0, (carried + got) / (double)wanted));
                }
            }

            var problem = Verify(part, file);
            if (problem is not null)
            {
                MediaDecoder.TryDelete(part);
                throw new InvalidDataException(problem);
            }

            if (File.Exists(final)) File.Delete(final);
            File.Move(part, final);
            doneBefore += file.Bytes;
        }

        progress?.Report(1.0);
    }

    /// <summary>Null if the file is exactly what it should be, otherwise why it is not.</summary>
    private static string? Verify(string path, SpeakerModelFile expected)
    {
        var actualBytes = new FileInfo(path).Length;
        if (actualBytes != expected.Bytes)
            return $"{expected.FileName} came down as {actualBytes:N0} bytes instead of {expected.Bytes:N0}. " +
                   "It has been deleted rather than used. Try again.";

        if (!Sha256(path).Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
            return $"{expected.FileName} did not match the checksum recorded in TranscribeGeek. " +
                   "It has been deleted rather than used.";

        return null;
    }

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>Removes both files. Behind a confirm on the Models screen.</summary>
    public static void Delete()
    {
        foreach (var f in All)
        {
            var p = PathFor(f);
            if (File.Exists(p)) File.Delete(p);
        }
    }
}
