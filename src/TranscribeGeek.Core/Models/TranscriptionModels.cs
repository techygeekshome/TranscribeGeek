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

/// <summary>One timed line of transcript.</summary>
public sealed record TranscriptSegment(TimeSpan Start, TimeSpan End, string Text);
