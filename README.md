# TranscribeGeek

Turns recordings into text, on your own machine.

Drop in audio or video and TranscribeGeek writes out a transcript, and a `.srt` subtitle file if
you want one. It runs OpenAI's Whisper speech models locally through
[whisper.cpp](https://github.com/ggerganov/whisper.cpp). There is no account, no server, no
upload and no per-minute limit — the recording never leaves the computer it is on.

Part of the [TechyGeeksHome](https://techygeekshome.info/geek-tools/) range.

## What it does

- Transcribes WAV, MP3, M4A, FLAC, OGG, Opus, WMA, AAC, MP4, MKV, MOV, AVI, WebM
- Writes a plain text transcript beside the source file, with or without timestamps
- Writes a SubRip `.srt` subtitle file alongside it
- Queues as many files as you like and works through them one at a time
- 22 languages, or automatic detection

## What it will not do

- **It does not send your recordings anywhere.** Transcription runs in-process using your own
  processor. The only thing the app ever downloads is a speech model, from the Models screen,
  when you ask for one.
- **It does not overwrite your files.** If a transcript is already there, the next one is
  numbered. The source file is only ever read.
- **It does not bundle ffmpeg.** MP3, MP4 and the rest need ffmpeg to decode. TranscribeGeek runs
  it as a separate program if it finds one and says so plainly if it does not, rather than
  quietly fetching a 90 MB binary you did not ask for. Plain 16 kHz mono WAV files work either
  way.

## Speech models

Nothing is included in the installer. A model is between 78 MB and 1.5 GB, and most people only
ever use one, so TranscribeGeek fetches the one you pick and keeps it in
`%LocalAppData%\TechyGeeksHome\TranscribeGeek\models`.

| Model | Download | When to use it |
|---|---|---|
| Tiny | 78 MB | Checking that a file transcribes at all |
| Base | 148 MB | An older machine |
| Small | 488 MB | The usual choice — start here |
| Medium | 1.5 GB | Accents and poor recordings. Several times slower |

## ffmpeg

Put `ffmpeg.exe` next to `TranscribeGeek.exe`, or anywhere on your `PATH`. Builds from
[ffmpeg.org](https://ffmpeg.org) or `winget install Gyan.FFmpeg` both work. If `ffprobe` is
beside it, the queue also shows how long each recording is.

## Requirements

Windows 10 1809 or later, 64-bit. .NET 8 is included in the installer build.

## Building

```
dotnet build TranscribeGeek.sln -c Release
```

## Licence

GPL-3.0. Free to use, including at work. No paid tier, ever.
