using TechyGeeksHome.Common;

namespace TranscribeGeek;

/// <summary>
/// Everything the shared About window and update check need to know about this app. One place,
/// so the wording here and the wording on the product page can be kept in step.
/// </summary>
internal static class AppFacts
{
    public static readonly AppInfo Info = new()
    {
        Name = "TranscribeGeek",
        Tagline = "Turns recordings into text, on your own machine",
        Description =
            "Drop in audio or video and TranscribeGeek writes out a transcript and, if you want one, " +
            "a subtitle file. It uses OpenAI's Whisper speech models running locally through " +
            "whisper.cpp - there is no account, no server, no upload and no per-minute limit. " +
            "The recording never leaves the computer it is on.",
        GitHubOwner = "techygeekshome",
        GitHubRepo = "TranscribeGeek",
        ProductUrl = "https://techygeekshome.info/transcribegeek/",
        IconUri = "avares://TranscribeGeek/Assets/transcribegeek.png",
        LicenceLine = "Free to use, including at work. GPL-3.0. No paid tier, ever.",
        Credits = new[]
        {
            new Credit("Whisper.net", "MIT", "https://github.com/sandrohanea/whisper.net"),
            new Credit("whisper.cpp", "MIT", "https://github.com/ggerganov/whisper.cpp"),
            new Credit("Whisper models", "MIT (OpenAI)", "https://github.com/openai/whisper"),
            new Credit("Avalonia", "MIT", "https://avaloniaui.net"),
            new Credit("ffmpeg", "Used as a separate program, never linked", "https://ffmpeg.org")
        }
    };
}
