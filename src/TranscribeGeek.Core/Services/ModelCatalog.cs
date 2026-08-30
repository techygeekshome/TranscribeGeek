using System.Net.Http;
using TranscribeGeek.Core.Models;
using Whisper.net.Ggml;

namespace TranscribeGeek.Core.Services;

/// <summary>
/// The models TranscribeGeek offers, and where they live on disk.
///
/// Nothing is shipped in the installer. A model is 75 MB at the small end and 1.5 GB at the
/// large end, and most people only ever need one of them - bundling any of them would make the
/// download several times bigger for no benefit. They are fetched on first use, with the size
/// shown before anything starts.
/// </summary>
public sealed class ModelCatalog
{
    /// <summary>
    /// Where models are kept. Under LocalApplicationData rather than beside the executable, so
    /// the portable build does not have to be in a writable folder and so a reinstall does not
    /// mean downloading a gigabyte again.
    /// </summary>
    public static string ModelDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TechyGeeksHome", "TranscribeGeek", "models");

    public static IReadOnlyList<WhisperModel> All { get; } = new[]
    {
        new WhisperModel("tiny",   "Tiny",   77_700_000,
            "Fastest, and the least accurate. Good for checking a file transcribes at all."),
        new WhisperModel("base",   "Base",   148_000_000,
            "A sensible starting point on an older machine."),
        new WhisperModel("small",  "Small",  488_000_000,
            "The best balance of speed and accuracy for most people. Start here."),
        new WhisperModel("medium", "Medium", 1_530_000_000,
            "Noticeably better on accents and poor recordings. Several times slower."),
    };

    public static WhisperModel Default => All.Single(m => m.Id == "small");

    public static string PathFor(WhisperModel model) =>
        Path.Combine(ModelDirectory, model.FileName);

    public static bool IsDownloaded(WhisperModel model)
    {
        var p = PathFor(model);
        // A part-downloaded file is worse than no file - it fails at load time with an error
        // that means nothing to the user. Treat anything suspiciously small as absent.
        return File.Exists(p) && new FileInfo(p).Length > 1_000_000;
    }

    public static long SizeOnDisk(WhisperModel model)
        => IsDownloaded(model) ? new FileInfo(PathFor(model)).Length : 0;

    private static GgmlType ToGgml(WhisperModel m) => m.Id switch
    {
        "tiny" => GgmlType.Tiny,
        "base" => GgmlType.Base,
        "small" => GgmlType.Small,
        "medium" => GgmlType.Medium,
        _ => throw new ArgumentOutOfRangeException(nameof(m), m.Id, "Unknown model")
    };

    /// <summary>
    /// Downloads a model, reporting progress as a fraction. Writes to a .part file and renames
    /// only on success, so a cancelled or failed download can never leave a half-model behind
    /// that looks downloaded.
    /// </summary>
    public static async Task DownloadAsync(
        WhisperModel model,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(ModelDirectory);
        var final = PathFor(model);
        var part = final + ".part";

        if (File.Exists(part)) File.Delete(part);

        using var source = await WhisperGgmlDownloader.Default
            .GetGgmlModelAsync(ToGgml(model), cancellationToken: ct)
            .ConfigureAwait(false);

        await using (var dest = File.Create(part))
        {
            var buffer = new byte[1 << 20];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dest.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                total += read;
                progress?.Report(Math.Min(1.0, (double)total / model.ApproxBytes));
            }
        }

        if (File.Exists(final)) File.Delete(final);
        File.Move(part, final);
        progress?.Report(1.0);
    }

    /// <summary>Removes a downloaded model. Used by the Models screen, always behind a confirm.</summary>
    public static void Delete(WhisperModel model)
    {
        var p = PathFor(model);
        if (File.Exists(p)) File.Delete(p);
    }
}
