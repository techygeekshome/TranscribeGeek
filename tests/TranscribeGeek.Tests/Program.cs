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

// ---- MediaDecoder -----------------------------------------------------------------
Check("mp4 is a supported extension", MediaDecoder.IsSupported("a.MP4"));
Check("txt is not", !MediaDecoder.IsSupported("a.txt"));
Check("ffmpeg found on this machine", MediaDecoder.FfmpegAvailable, "tests that need decoding will be skipped");

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

Directory.Delete(tmp, true);

// ---- ModelCatalog -----------------------------------------------------------------
Check("four models offered", ModelCatalog.All.Count == 4);
Check("default is small", ModelCatalog.Default.Id == "small");
Check("model path is under LocalApplicationData",
    ModelCatalog.PathFor(ModelCatalog.Default).Contains("TranscribeGeek"));

// ---- End to end, only if a model has already been fetched -------------------------
var model = Environment.GetEnvironmentVariable("TG_TEST_MODEL");
if (model is not null && File.Exists(model) && File.Exists(sample))
{
    using var engine = new TranscriptionEngine();
    var got = await engine.TranscribeAsync(sample, model, "en");
    var all = string.Join(" ", got.Select(s => s.Text));
    Check("transcribes the sample", all.Contains("country", StringComparison.OrdinalIgnoreCase), all[..Math.Min(90, all.Length)]);
    Check("segments carry timings", got.Count > 0 && got[0].End > got[0].Start);
}
else
{
    Console.WriteLine("SKIP  end-to-end transcription (set TG_TEST_MODEL to a ggml .bin to run it)");
}

Console.WriteLine(failed == 0 ? "\nAll checks passed." : $"\n{failed} check(s) failed.");
return failed == 0 ? 0 : 1;
