using SherpaOnnx;
using TranscribeGeek.Core.Models;

namespace TranscribeGeek.Core.Services;

/// <summary>
/// Works out who is speaking, and when.
///
/// This runs after Whisper, over the same 16 kHz mono audio, and produces a separate list of
/// stretches: "someone was talking from 4.4 to 6.4 seconds, and it was the same someone as at
/// 1.6 seconds". <see cref="Assign"/> then puts those two lists together, so a transcript line
/// can say who said it.
///
/// It is a second opinion, not a fact. Two people with similar voices on a poor recording will
/// sometimes come out as one, and one person on a very variable recording will sometimes come
/// out as two. The app says so on screen rather than pretending otherwise, and the transcript
/// is still written even when this step fails.
///
/// Like everything else here it runs on this machine, offline, once the models are downloaded.
/// </summary>
public sealed class SpeakerDiarizer : IDisposable
{
    /// <summary>
    /// The whole recording has to be in memory as floats for this, which is four bytes a sample:
    /// about 230 MB an hour. Four hours is the point at which that stops being reasonable on an
    /// ordinary machine, so past it the transcript is written without speaker labels and the
    /// reason is said out loud rather than the app quietly running the machine out of memory.
    /// </summary>
    public static readonly TimeSpan LongestSupported = TimeSpan.FromHours(4);

    private OfflineSpeakerDiarization? _sd;
    private int _lastSpeakerCount = -1;
    private double _lastThreshold = -1;

    /// <summary>
    /// Reads a 16 kHz mono WAV into the sample array sherpa-onnx wants. Only ever called with a
    /// file <see cref="MediaDecoder"/> has already produced or vouched for, so the header work
    /// here is a sanity check rather than a parser.
    /// </summary>
    public static float[] ReadWav(string wavPath)
    {
        using var fs = File.OpenRead(wavPath);
        using var br = new BinaryReader(fs);

        if (new string(br.ReadChars(4)) != "RIFF") throw new MediaDecodeException("That is not a WAV file.");
        br.ReadInt32();
        if (new string(br.ReadChars(4)) != "WAVE") throw new MediaDecodeException("That is not a WAV file.");

        short channels = 0, bits = 0;
        int rate = 0;

        while (fs.Position < fs.Length - 8)
        {
            var id = new string(br.ReadChars(4));
            var size = br.ReadInt32();

            if (id == "fmt ")
            {
                br.ReadInt16();                 // format tag, already checked upstream
                channels = br.ReadInt16();
                rate = br.ReadInt32();
                br.ReadInt32();                 // byte rate
                br.ReadInt16();                 // block align
                bits = br.ReadInt16();
                fs.Seek(size - 16, SeekOrigin.Current);
                continue;
            }

            if (id == "data")
            {
                if (channels != 1 || rate != MediaDecoder.RequiredSampleRate || bits != 16)
                    throw new MediaDecodeException(
                        "Speakers can only be worked out from 16 kHz mono audio, and that file is not.");

                var count = size / 2;
                var samples = new float[count];
                var raw = br.ReadBytes(size);
                for (var i = 0; i < count; i++)
                    samples[i] = BitConverter.ToInt16(raw, i * 2) / 32768f;
                return samples;
            }

            fs.Seek(size + (size & 1), SeekOrigin.Current);
        }

        throw new MediaDecodeException("That WAV file has no audio in it.");
    }

    /// <summary>
    /// Builds the diarizer, or reuses the one already loaded. Loading takes a second or two, so
    /// a queue of twenty files should pay it once, the same way the Whisper model does.
    /// </summary>
    private OfflineSpeakerDiarization GetDiarizer(int expectedSpeakers, double threshold)
    {
        if (_sd is not null && _lastSpeakerCount == expectedSpeakers && Math.Abs(_lastThreshold - threshold) < 0.0001)
            return _sd;

        foreach (var f in SpeakerModelCatalog.All)
        {
            if (!SpeakerModelCatalog.IsDownloaded(f))
                throw new FileNotFoundException(
                    "The speaker models have not been downloaded yet. Open Models and download the speaker pack.",
                    SpeakerModelCatalog.PathFor(f));
        }

        var config = new OfflineSpeakerDiarizationConfig();
        config.Segmentation.Pyannote.Model = SpeakerModelCatalog.PathFor(SpeakerModelCatalog.Segmentation);
        config.Segmentation.NumThreads = Threads;
        config.Embedding.Model = SpeakerModelCatalog.PathFor(SpeakerModelCatalog.Embedding);
        config.Embedding.NumThreads = Threads;

        // -1 means "work out how many people there are". A number here is only used when the
        // user has told us, on the Transcribe screen, how many people are on the recording -
        // which is worth offering, because it is the one thing they know and the model does not.
        config.Clustering.NumClusters = expectedSpeakers > 0 ? expectedSpeakers : -1;
        config.Clustering.Threshold = (float)threshold;

        config.MinDurationOn = 0.3f;    // shorter than this is a noise, not a turn
        config.MinDurationOff = 0.5f;   // a gap shorter than this is a breath, not a handover

        _sd?.Dispose();
        _sd = new OfflineSpeakerDiarization(config);
        _lastSpeakerCount = expectedSpeakers;
        _lastThreshold = threshold;
        return _sd;
    }

    private static int Threads => Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    /// <summary>
    /// The speaker turns in a 16 kHz mono WAV, earliest first, numbered from 1 in the order the
    /// voices first appear.
    /// </summary>
    /// <param name="expectedSpeakers">How many people are on the recording, or 0 to work it out.</param>
    /// <param name="threshold">
    /// How different two voices have to sound before they are counted as two people. Lower finds
    /// more speakers, higher finds fewer. 0.5 is the sherpa-onnx default and is what the app uses.
    /// </param>
    public async Task<IReadOnlyList<SpeakerTurn>> DiarizeAsync(
        string wavPath,
        int expectedSpeakers = 0,
        double threshold = 0.5,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var samples = ReadWav(wavPath);

        var length = TimeSpan.FromSeconds(samples.Length / (double)MediaDecoder.RequiredSampleRate);
        if (length > LongestSupported)
            throw new InvalidOperationException(
                $"That recording is {length.TotalHours:0.#} hours long. Speakers are only worked out for " +
                $"recordings up to {LongestSupported.TotalHours:0} hours, because the whole thing has to be " +
                "held in memory at once. The transcript itself is not affected.");

        ct.ThrowIfCancellationRequested();
        var sd = GetDiarizer(expectedSpeakers, threshold);

        // Process is one long call into native code that cannot be interrupted part way, so it
        // goes on a background thread and cancellation is honoured either side of it rather than
        // being claimed and then not delivered.
        OfflineSpeakerDiarizationSegment[] raw = null!;
        await Task.Run(() =>
        {
            OfflineSpeakerDiarizationProgressCallback cb = (processed, total, _) =>
            {
                if (total > 0) progress?.Report(Math.Clamp(processed / (double)total, 0, 1));
                return 0;
            };

            raw = sd.ProcessWithCallback(samples, cb, IntPtr.Zero);
            GC.KeepAlive(cb);
        }, ct).ConfigureAwait(false);

        progress?.Report(1.0);

        // sherpa-onnx hands back cluster numbers, which are not contiguous and mean nothing to
        // a reader. Renumber them in the order they are first heard: whoever speaks first is 1.
        var order = new Dictionary<int, int>();
        var turns = new List<SpeakerTurn>(raw.Length);

        foreach (var s in raw)
        {
            if (!order.TryGetValue(s.Speaker, out var n))
            {
                n = order.Count + 1;
                order[s.Speaker] = n;
            }

            turns.Add(new SpeakerTurn(
                TimeSpan.FromSeconds(s.Start),
                TimeSpan.FromSeconds(s.End),
                n));
        }

        return turns;
    }

    /// <summary>
    /// Puts a speaker on each transcript line.
    ///
    /// Whisper's idea of where a sentence begins and the diarizer's idea of where a voice begins
    /// never line up exactly, so a line is given to whichever speaker was talking for most of it.
    /// A line that overlaps nothing at all - Whisper heard words in a stretch the diarizer called
    /// silence - takes the speaker of whichever turn it sits closest to, but only if that turn is
    /// within a couple of seconds. Further away than that and it is left unlabelled rather than
    /// guessed at.
    /// </summary>
    private const double NearestTurnSeconds = 2.0;

    public static IReadOnlyList<TranscriptSegment> Assign(
        IReadOnlyList<TranscriptSegment> segments,
        IReadOnlyList<SpeakerTurn> turns)
    {
        if (turns.Count == 0) return segments;

        var labelled = new List<TranscriptSegment>(segments.Count);

        foreach (var seg in segments)
        {
            var best = 0d;
            var bestSpeaker = 0;

            foreach (var turn in turns)
            {
                var start = seg.Start > turn.Start ? seg.Start : turn.Start;
                var end = seg.End < turn.End ? seg.End : turn.End;
                var overlap = (end - start).TotalSeconds;

                if (overlap > best)
                {
                    best = overlap;
                    bestSpeaker = turn.Speaker;
                }
            }

            if (bestSpeaker == 0)
            {
                var nearest = double.MaxValue;

                foreach (var turn in turns)
                {
                    var gap = seg.Start > turn.End ? (seg.Start - turn.End).TotalSeconds
                            : turn.Start > seg.End ? (turn.Start - seg.End).TotalSeconds
                            : 0;

                    if (gap < nearest) { nearest = gap; bestSpeaker = turn.Speaker; }
                }

                if (nearest > NearestTurnSeconds) bestSpeaker = 0;
            }

            labelled.Add(bestSpeaker == 0
                ? seg
                : seg with { Speaker = $"Speaker {bestSpeaker}" });
        }

        return labelled;
    }

    /// <summary>How many distinct people ended up on the transcript. For the line under the queue.</summary>
    public static int CountSpeakers(IReadOnlyList<TranscriptSegment> segments) =>
        segments.Where(s => s.Speaker is not null).Select(s => s.Speaker).Distinct().Count();

    public void Dispose()
    {
        _sd?.Dispose();
        _sd = null;
    }
}
