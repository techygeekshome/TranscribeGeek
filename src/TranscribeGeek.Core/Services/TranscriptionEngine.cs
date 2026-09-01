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

        // Must happen before the first call into Whisper.net. See WhisperRuntime for why.
        WhisperRuntime.Prepare();

        _factory?.Dispose();
        try
        {
            _factory = WhisperFactory.FromPath(modelPath);
        }
        catch (Exception ex)
        {
            // Whisper.net's own message for a missing library says nothing about where it looked,
            // which is exactly what you need to know. Say where this build put the library and
            // how it got there, so a screenshot of the failure is enough to work from.
            throw new InvalidOperationException(
                ex.Message.TrimEnd() + Environment.NewLine + Environment.NewLine +
                "TranscribeGeek looked for it " + WhisperRuntime.Source +
                (WhisperRuntime.ResolvedPath is { } p ? ", at " + p : "") + ".", ex);
        }
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
        var result = await RunAsync(sourcePath, modelPath, language, knownDuration, progress,
            cancellationToken: ct).ConfigureAwait(false);
        return result.Segments;
    }

    /// <summary>
    /// Transcribes, and optionally works out who is speaking while the decoded audio is still
    /// to hand. Doing both here rather than in the caller means the file is decoded once, not
    /// twice, and the temporary WAV has exactly one owner.
    /// </summary>
    /// <param name="diarizer">Null to skip working out speakers, which is the default.</param>
    /// <param name="expectedSpeakers">How many people are on the recording, or 0 to work it out.</param>
    /// <param name="speakerProgress">Fraction complete for the speaker pass, which runs after Whisper.</param>
    public async Task<TranscriptionResult> RunAsync(
        string sourcePath,
        string modelPath,
        string language,
        TimeSpan? knownDuration = null,
        IProgress<double>? progress = null,
        SpeakerDiarizer? diarizer = null,
        int expectedSpeakers = 0,
        IProgress<double>? speakerProgress = null,
        Action<string>? onStage = null,
        CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;

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

            var segments = new List<TranscriptSegment>();

            using (var processor = builder.Build())
            await using (var audio = File.OpenRead(wavPath))
            {
                await foreach (var seg in processor.ProcessAsync(audio, ct).ConfigureAwait(false))
                {
                    var text = seg.Text.Trim();
                    if (text.Length > 0)
                        segments.Add(new TranscriptSegment(seg.Start, seg.End, text));

                    if (duration is { TotalSeconds: > 0 })
                        progress?.Report(Math.Clamp(seg.End.TotalSeconds / duration.Value.TotalSeconds, 0, 1));
                }
            }

            progress?.Report(1.0);

            if (diarizer is null || segments.Count == 0)
                return new TranscriptionResult(segments, 0, null);

            // A transcript is worth more than a speaker label, so a failure here is reported and
            // the transcript is still written. Cancellation is the one exception - if the user
            // pressed Stop they meant it, and a half-finished job should not quietly complete.
            try
            {
                onStage?.Invoke("Working out who is speaking…");

                var turns = await diarizer
                    .DiarizeAsync(wavPath, expectedSpeakers, progress: speakerProgress, ct: ct)
                    .ConfigureAwait(false);

                var labelled = SpeakerDiarizer.Assign(segments, turns);
                return new TranscriptionResult(labelled, SpeakerDiarizer.CountSpeakers(labelled), null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new TranscriptionResult(segments, 0, ex.Message);
            }
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
