namespace TranscribeGeek.Core.Models;

/// <summary>
/// One Whisper model the user can download. Sizes are the real download sizes, not the
/// parameter counts - the number that matters to somebody on a slow connection is the one
/// they are about to wait for.
/// </summary>
/// <param name="Id">Whisper.net's own identifier for the model.</param>
/// <param name="Name">What we call it on screen.</param>
/// <param name="ApproxBytes">Download size. Shown before anything is fetched.</param>
/// <param name="Blurb">One line: when to pick this one.</param>
public sealed record WhisperModel(string Id, string Name, long ApproxBytes, string Blurb)
{
    public string FileName => $"ggml-{Id}.bin";
}

/// <summary>A file waiting to be transcribed, or one that has been.</summary>
public sealed class TranscriptionJob
{
    public required string SourcePath { get; init; }
    public string FileName => Path.GetFileName(SourcePath);

    public JobState State { get; set; } = JobState.Waiting;
    public string Status { get; set; } = "Waiting";
    public double Progress { get; set; }

    /// <summary>Length of the media, once we have been able to work it out.</summary>
    public TimeSpan? Duration { get; set; }

    /// <summary>Where the transcript was written. Null until it has been.</summary>
    public string? TranscriptPath { get; set; }
    public string? SubtitlePath { get; set; }

    /// <summary>Why it failed, in words a person can act on.</summary>
    public string? Error { get; set; }

    public List<TranscriptSegment> Segments { get; } = new();
}

public enum JobState
{
    Waiting,
    Running,
    Done,
    Failed,
    Cancelled
}

/// <summary>
/// One timed line of transcript.
/// </summary>
/// <param name="Speaker">
/// "Speaker 1", "Speaker 2" and so on, or null when speakers were not worked out. Numbered in
/// the order they first talk, because that is the order somebody reading the transcript meets
/// them in. The app never tries to guess a real name.
/// </param>
public sealed record TranscriptSegment(TimeSpan Start, TimeSpan End, string Text, string? Speaker = null);

/// <summary>A stretch of audio one person is talking over, before any text is attached to it.</summary>
public sealed record SpeakerTurn(TimeSpan Start, TimeSpan End, int Speaker);

/// <summary>
/// One of the two files the speaker pack is made of. Both are needed, both are checked against
/// a hash recorded here, and neither is in the installer.
/// </summary>
/// <param name="FileName">What it is saved as.</param>
/// <param name="Url">Where it comes from.</param>
/// <param name="Bytes">Exact size, so a truncated download is caught before the hash is even run.</param>
/// <param name="Sha256">Lower case hex. A file that does not match is deleted, not used.</param>
/// <param name="Origin">Who made it and under what licence, shown on the Models screen.</param>
public sealed record SpeakerModelFile(string FileName, string Url, long Bytes, string Sha256, string Origin);

/// <summary>
/// What came back from one file: the lines, how many people were heard, and, if working out
/// speakers was asked for and did not work, why. A speaker problem is never a failed job -
/// the transcript is still there.
/// </summary>
public sealed record TranscriptionResult(
    IReadOnlyList<TranscriptSegment> Segments,
    int SpeakerCount,
    string? SpeakerProblem);
