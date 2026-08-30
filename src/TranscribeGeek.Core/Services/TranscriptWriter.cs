using System.Globalization;
using System.Text;
using TranscribeGeek.Core.Models;

namespace TranscribeGeek.Core.Services;

/// <summary>
/// Writes transcripts out. The only class in the application that creates a file in the user's
/// own folders, which is deliberate - there is one place to look when asking "what does this
/// thing write, and where".
///
/// Output lands beside the source file, named after it. Nothing is ever overwritten: if
/// <c>interview.txt</c> exists, the next one is <c>interview (2).txt</c>. Silently replacing
/// somebody's earlier transcript because they ran the file twice is not acceptable behaviour
/// for a tool that is supposed to be safe.
/// </summary>
public static class TranscriptWriter
{
    public static string WritePlainText(string sourcePath, IReadOnlyList<TranscriptSegment> segments,
        bool includeTimestamps)
    {
        var path = NextFreePath(sourcePath, ".txt");
        var sb = new StringBuilder();

        foreach (var s in segments)
        {
            if (includeTimestamps)
                sb.Append('[').Append(Hms(s.Start)).Append("]  ");
            sb.AppendLine(s.Text);
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    /// <summary>
    /// SubRip. Indices from 1, comma as the decimal separator, blank line between entries -
    /// all of which players are fussy about, so they are written explicitly with the invariant
    /// culture rather than left to the machine's locale.
    /// </summary>
    public static string WriteSubRip(string sourcePath, IReadOnlyList<TranscriptSegment> segments)
    {
        var path = NextFreePath(sourcePath, ".srt");
        var sb = new StringBuilder();

        for (var i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            sb.Append(i + 1).Append('\n');
            sb.Append(Srt(s.Start)).Append(" --> ").Append(Srt(s.End)).Append('\n');
            sb.Append(s.Text).Append('\n').Append('\n');
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return path;
    }

    private static string Srt(TimeSpan t) =>
        string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00},{3:000}",
            (int)t.TotalHours, t.Minutes, t.Seconds, t.Milliseconds);

    private static string Hms(TimeSpan t) =>
        string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}",
            (int)t.TotalHours, t.Minutes, t.Seconds);

    /// <summary>
    /// The source file's own name with a new extension, or that name with " (2)", " (3)" and so
    /// on if something is already there.
    /// </summary>
    private static string NextFreePath(string sourcePath, string extension)
    {
        var dir = Path.GetDirectoryName(sourcePath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(sourcePath);

        var candidate = Path.Combine(dir, stem + extension);
        if (!File.Exists(candidate)) return candidate;

        for (var n = 2; n < 1000; n++)
        {
            candidate = Path.Combine(dir, $"{stem} ({n}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }

        // A thousand transcripts of one file is not a real scenario, but falling back to a
        // timestamp is better than throwing at the very end of a long job.
        return Path.Combine(dir, $"{stem} {DateTime.Now:yyyyMMdd-HHmmss}{extension}");
    }
}
