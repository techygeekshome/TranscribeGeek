using TranscribeGeek.Core.Models;
using Whisper.net;

namespace TranscribeGeek.Core.Services;

/// <summary>
/// Runs Whisper over a file and produces timed segments.
///
/// Everything happens in this process on this machine. Nothing is uploaded, and the only file
/// this class ever writes is the temporary WAV it may need for a non-WAV input, which it
/// deletes afterwards. Writing the transcript out is <see cref="TranscriptWriter"/>'s job, so
/// that the "read the audio" and "write to the user's disk" responsibilities stay apart.
/// </summary>
public sealed class TranscriptionEngine : IDisposable
{
    private WhisperFactory? _factory;
    private string? _loadedModelPath;

    /// <summary>
    /// Loads a model, keeping it if the same one is asked for again. Loading is the slow part -
    /// a medium model takes seconds - so a queue of twenty files should pay it once.
    /// </summary>
    private WhisperFactory GetFactory(string modelPath)
    {
        if (_factory is not null && _loadedModelPath == modelPath) return _factory;

        _factory?.Dispose();
        _factory = WhisperFactory.FromPath(modelPath);
        _loadedModelPath = modelPath;
        return _factory;
    }

    /// <param name="language">Two-letter code, or "auto" to let Whisper decide.</param>
    /// <param name="progress">
    /// Fraction complete. Whisper reports the position it has reached in the audio, so this is
    /// real progress rather than a guess - but only when the duration is known.
    /// </param>
    public async Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
        string sourcePath,
        string modelPath,
        string language,
        TimeSpan? knownDuration = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException(
                "That model has not been downloaded yet. Open Models and download one first.", modelPath);

        var (wavPath, isTemp) = await MediaDecoder.ToWhisperWavAsync(sourcePath, ct).ConfigureAwait(false);

        try
        {
            var duration = knownDuration
                           ?? await MediaDecoder.TryGetDurationAsync(sourcePath, ct).ConfigureAwait(false);

            var builder = GetFactory(modelPath).CreateBuilder();
            builder = language.Equals("auto", StringComparison.OrdinalIgnoreCase)
                ? builder.WithLanguageDetection()
                : builder.WithLanguage(language);

            using var processor = builder.Build();
            await using var audio = File.OpenRead(wavPath);

            var segments = new List<TranscriptSegment>();

            await foreach (var seg in processor.ProcessAsync(audio, ct).ConfigureAwait(false))
            {
                var text = seg.Text.Trim();
                if (text.Length > 0)
                    segments.Add(new TranscriptSegment(seg.Start, seg.End, text));

                if (duration is { TotalSeconds: > 0 })
                    progress?.Report(Math.Clamp(seg.End.TotalSeconds / duration.Value.TotalSeconds, 0, 1));
            }

            progress?.Report(1.0);
            return segments;
        }
        finally
        {
            if (isTemp) MediaDecoder.TryDelete(wavPath);
        }
    }

    public void Dispose()
    {
        _factory?.Dispose();
        _factory = null;
    }
}
