using TranscribeGeek.Core.Models;
using TranscribeGeek.Core.Services;

// A plain console harness rather than a test framework, matching the other apps in the range:
// it runs in CI, exits non-zero on failure, and adds no dependency.
int failed = 0;
void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(ok || detail is null ? "" : "  -> " + detail)}");
    if (!ok) failed++;
}

void Skip(string name, string why) => Console.WriteLine($"SKIP  {name} ({why})");

// ---- MediaDecoder -----------------------------------------------------------------
Check("mp4 is a supported extension", MediaDecoder.IsSupported("a.MP4"));
Check("txt is not", !MediaDecoder.IsSupported("a.txt"));
// ffmpeg is OPTIONAL by design - the app says so plainly and still handles 16 kHz mono WAV -
// so its absence is reported, never failed. CI runners do not all carry it, and a check that
// fails the build for a missing optional dependency is a check that trains people to ignore CI.
if (MediaDecoder.FfmpegAvailable)
    Check("ffmpeg found, so the decoding checks can run", true);
else
    Skip("ffmpeg decoding checks", "ffmpeg is not on this machine - it is optional");

var sample = Environment.GetEnvironmentVariable("TG_SAMPLE_WAV") ?? "/tmp/tgprobe/jfk.wav";
if (File.Exists(sample))
{
    Check("16k mono wav recognised as already usable", MediaDecoder.IsAlreadyUsableWav(sample));

    if (MediaDecoder.FfmpegAvailable)
    {
        var dur = await MediaDecoder.TryGetDurationAsync(sample);
        Check("duration probed", dur is { TotalSeconds: > 5 and < 30 }, dur?.ToString());
    }
}

// ---- TranscriptWriter -------------------------------------------------------------
var tmp = Path.Combine(Path.GetTempPath(), "tg-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tmp);
var fakeSource = Path.Combine(tmp, "interview.mp3");
File.WriteAllText(fakeSource, "not really audio");

var segs = new List<TranscriptSegment>
{
    new(TimeSpan.FromSeconds(0),    TimeSpan.FromSeconds(2.5),  "First line."),
    new(TimeSpan.FromSeconds(2.5),  TimeSpan.FromSeconds(6.25), "Second line."),
};

var txt = TranscriptWriter.WritePlainText(fakeSource, segs, includeTimestamps: true);
Check("txt written beside the source", Path.GetDirectoryName(txt) == tmp && txt.EndsWith("interview.txt"), txt);

var srt = TranscriptWriter.WriteSubRip(fakeSource, segs);
var srtBody = File.ReadAllText(srt);
Check("srt uses a comma for milliseconds", srtBody.Contains("00:00:00,000 --> 00:00:02,500"), srtBody.Split('\n')[1]);
Check("srt numbers entries from 1", srtBody.StartsWith("1\n"));

// running it twice must not clobber the first transcript
var txt2 = TranscriptWriter.WritePlainText(fakeSource, segs, includeTimestamps: false);
Check("second run does not overwrite the first", txt2 != txt && txt2.EndsWith("interview (2).txt"), txt2);
Check("first transcript still intact", File.ReadAllText(txt).Contains("[00:00:00]  First line."));

// speaker labels: a heading where the speaker changes in the text file, on every line in the srt
var spoken = new List<TranscriptSegment>
{
    new(TimeSpan.FromSeconds(0),   TimeSpan.FromSeconds(2), "Morning.",       "Speaker 1"),
    new(TimeSpan.FromSeconds(2),   TimeSpan.FromSeconds(4), "Still me.",      "Speaker 1"),
    new(TimeSpan.FromSeconds(4),   TimeSpan.FromSeconds(6), "My turn now.",   "Speaker 2"),
};
var spokenTxt = File.ReadAllText(TranscriptWriter.WritePlainText(fakeSource, spoken, includeTimestamps: false));
Check("speaker heading written once per change",
    spokenTxt.Split("Speaker 1:").Length == 2 && spokenTxt.Contains("Speaker 2:"), spokenTxt.Replace("\n", " | "));
Check("speaker heading is not repeated on every line",
    !spokenTxt.Contains("Speaker 1:\nMorning.\nSpeaker 1:"));

var spokenSrt = File.ReadAllText(TranscriptWriter.WriteSubRip(fakeSource, spoken));
Check("srt names the speaker on every caption",
    spokenSrt.Contains("Speaker 1: Morning.") && spokenSrt.Contains("Speaker 1: Still me.")
    && spokenSrt.Contains("Speaker 2: My turn now."));

// unlabelled segments must come out exactly as they went in
var plainSrt = File.ReadAllText(TranscriptWriter.WriteSubRip(fakeSource, segs));
Check("no speaker prefix when speakers were not worked out", !plainSrt.Contains(": First line."));

Directory.Delete(tmp, true);

// ---- Assigning speakers to lines --------------------------------------------------
var turns = new List<SpeakerTurn>
{
    new(TimeSpan.FromSeconds(0),  TimeSpan.FromSeconds(5),  1),
    new(TimeSpan.FromSeconds(6),  TimeSpan.FromSeconds(12), 2),
};
var lines = new List<TranscriptSegment>
{
    new(TimeSpan.FromSeconds(0.5),  TimeSpan.FromSeconds(4),    "Clearly the first."),
    new(TimeSpan.FromSeconds(4.5),  TimeSpan.FromSeconds(7),    "Straddles the handover, mostly the second."),
    new(TimeSpan.FromSeconds(8),    TimeSpan.FromSeconds(11),   "Clearly the second."),
    new(TimeSpan.FromSeconds(40),   TimeSpan.FromSeconds(42),   "Miles away from any turn."),
};
var assigned = SpeakerDiarizer.Assign(lines, turns);
Check("a line inside one turn takes that speaker", assigned[0].Speaker == "Speaker 1", assigned[0].Speaker);
Check("a straddling line goes to whoever spoke most of it", assigned[1].Speaker == "Speaker 2", assigned[1].Speaker);
Check("a far away line is left unlabelled", assigned[3].Speaker is null, assigned[3].Speaker);
Check("speakers are counted, not guessed", SpeakerDiarizer.CountSpeakers(assigned) == 2);
Check("no turns means the lines come back untouched",
    ReferenceEquals(SpeakerDiarizer.Assign(lines, Array.Empty<SpeakerTurn>()), lines));

// ---- SpeakerModelCatalog ----------------------------------------------------------
Check("the speaker pack is two files", SpeakerModelCatalog.All.Count == 2);
Check("both have a full 64 character sha256 recorded",
    SpeakerModelCatalog.All.All(f => f.Sha256.Length == 64 && f.Sha256.All(c => char.IsAsciiHexDigitLower(c))));
Check("the pack is under 40 MB", SpeakerModelCatalog.TotalBytes is > 30_000_000 and < 40_000_000,
    SpeakerModelCatalog.TotalBytes.ToString());
Check("speaker models sit under the speech model folder",
    SpeakerModelCatalog.Directory.StartsWith(ModelCatalog.ModelDirectory));

// ---- ModelCatalog -----------------------------------------------------------------
Check("four models offered", ModelCatalog.All.Count == 4);
Check("default is small", ModelCatalog.Default.Id == "small");
Check("model path is under LocalApplicationData",
    ModelCatalog.PathFor(ModelCatalog.Default).Contains("TranscribeGeek"));

// ---- Where the native Whisper library was found ------------------------------------
// This is the check that a packaged build needs and a build from source does not. Run from a
// build folder the library sits in runtimes/ next to the assembly and everything works. Run
// as the single executable the product actually ships as, the library is inside the bundle and
// Whisper.net looks for it in the wrong place, which is how 1.0.0 and 1.0.1 shipped able to
// install, able to download a model, and unable to transcribe a single file.
{
    WhisperRuntime.Prepare();
    Check("the native Whisper library was located", WhisperRuntime.ResolvedPath is not null,
        WhisperRuntime.ResolvedPath ?? "not found");

    if (WhisperRuntime.ResolvedPath is { } where)
        Check("and it is really there", Directory.Exists(where), where);
}

// ---- End to end, only if a model has already been fetched -------------------------
var model = Environment.GetEnvironmentVariable("TG_TEST_MODEL");
if (model is not null && File.Exists(model) && File.Exists(sample))
{
    using var engine = new TranscriptionEngine();
    var got = await engine.TranscribeAsync(sample, model, "en");
    var all = string.Join(" ", got.Select(s => s.Text));

    Check("transcribes the sample into words", all.Trim().Length > 20 && all.Any(char.IsLetter),
        all[..Math.Min(90, all.Length)]);
    Check("segments carry timings", got.Count > 0 && got[0].End > got[0].Start);
    Check("the segments run in order", got.Zip(got.Skip(1)).All(p => p.Second.Start >= p.First.Start));

    // The JFK clip is the usual sample and its words are known, so when it is the one in use
    // the transcript is checked against them rather than merely against being non-empty.
    if (sample.Contains("jfk", StringComparison.OrdinalIgnoreCase))
        Check("the known sample transcribes correctly",
            all.Contains("country", StringComparison.OrdinalIgnoreCase), all[..Math.Min(90, all.Length)]);
}
else
{
    Console.WriteLine("SKIP  end-to-end transcription (set TG_TEST_MODEL to a ggml .bin to run it)");
}

// ---- Diarisation, end to end ------------------------------------------------------
// The one part of this that is hard to get right is the native call and the model files, so it
// is worth actually running rather than only unit testing the arithmetic around it. Downloading
// 36 MB is not something to do on every build, so it runs when TG_DIARISE_WAV points at a
// 16 kHz mono WAV with more than one person on it.
var speech = Environment.GetEnvironmentVariable("TG_DIARISE_WAV");
if (!string.IsNullOrWhiteSpace(speech))
{
    // If the variable is set, the checks below MUST run. A sample that failed to download and
    // quietly turned into a skip would leave a green build that proved nothing, which is worse
    // than no check at all.
    Check("the diarisation sample is where TG_DIARISE_WAV points", File.Exists(speech), speech);
}

if (!string.IsNullOrWhiteSpace(speech) && File.Exists(speech))
{
    if (!SpeakerModelCatalog.IsReady) await SpeakerModelCatalog.DownloadAsync();

    Check("both speaker models verified and kept", SpeakerModelCatalog.IsReady);
    Check("segmentation model matches its recorded checksum",
        SpeakerModelCatalog.Sha256(SpeakerModelCatalog.PathFor(SpeakerModelCatalog.Segmentation))
        == SpeakerModelCatalog.Segmentation.Sha256);

    using var diarizer = new SpeakerDiarizer();
    var got = await diarizer.DiarizeAsync(speech);

    Check("turns come back", got.Count > 0, got.Count.ToString());
    Check("turns are in time order", got.Zip(got.Skip(1)).All(p => p.Second.Start >= p.First.Start));
    Check("every turn ends after it starts", got.All(t => t.End > t.Start));
    Check("speakers are numbered from 1 with no gaps",
        got.Select(t => t.Speaker).Distinct().OrderBy(n => n).SequenceEqual(
            Enumerable.Range(1, got.Select(t => t.Speaker).Distinct().Count())),
        string.Join(",", got.Select(t => t.Speaker).Distinct()));
    Check("the first voice heard is Speaker 1", got[0].Speaker == 1);
    Check("more than one person was heard on a two speaker sample",
        got.Select(t => t.Speaker).Distinct().Count() >= 2,
        string.Join(",", got.Select(t => $"{t.Start.TotalSeconds:0.0}-{t.End.TotalSeconds:0.0}:S{t.Speaker}")));
}
else
{
    Console.WriteLine("SKIP  diarisation (set TG_DIARISE_WAV to a 16 kHz mono WAV to run it)");
}

Console.WriteLine(failed == 0 ? "\nAll checks passed." : $"\n{failed} check(s) failed.");
return failed == 0 ? 0 : 1;
