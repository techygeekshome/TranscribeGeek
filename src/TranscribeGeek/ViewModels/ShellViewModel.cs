using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using TranscribeGeek.Core.Models;
using TranscribeGeek.Core.Services;

namespace TranscribeGeek.ViewModels;

/// <summary>
/// The whole application's state. TranscribeGeek is small enough that one view model is
/// honest - splitting it would be ceremony rather than structure.
/// </summary>
public sealed class ShellViewModel : ObservableObject
{
    private readonly TranscriptionEngine _engine = new();
    private readonly SpeakerDiarizer _diarizer = new();
    private CancellationTokenSource? _cts;

    public ShellViewModel()
    {
        ShowTranscribe = new RelayCommand(() => Page = "Transcribe");
        ShowModels = new RelayCommand(() => Page = "Models");
        ShowSettings = new RelayCommand(() => Page = "Settings");

        foreach (var m in ModelCatalog.All)
            Models.Add(new ModelRowViewModel(m, this));

        SpeakerPack = new SpeakerPackViewModel(this);

        SelectedModel = Models.FirstOrDefault(m => m.IsDownloaded)
                        ?? Models.First(m => m.Model.Id == ModelCatalog.Default.Id);

        RefreshReadiness();
    }

    // ---------------------------------------------------------------- navigation

    private string _page = "Transcribe";
    public string Page
    {
        get => _page;
        set
        {
            if (!SetField(ref _page, value)) return;
            OnPropertyChanged(nameof(IsTranscribe));
            OnPropertyChanged(nameof(IsModels));
            OnPropertyChanged(nameof(IsSettings));
            OnPropertyChanged(nameof(PageTitle));
            OnPropertyChanged(nameof(StatusLine));
        }
    }

    public bool IsTranscribe => Page == "Transcribe";
    public bool IsModels => Page == "Models";
    public bool IsSettings => Page == "Settings";

    public ICommand ShowTranscribe { get; }
    public ICommand ShowModels { get; }
    public ICommand ShowSettings { get; }

    // ---------------------------------------------------------------- chrome

    public string BrandName => "TranscribeGeek";
    public string BrandBy => "by TechyGeeksHome";
    public string VersionText => TechyGeeksHome.Common.AppInfo.CurrentVersionText;

    /// <summary>Shown on Settings so there is never a guess about where a gigabyte went.</summary>
    public string ModelFolder => ModelCatalog.ModelDirectory;

    public string SpeakerFolder => SpeakerModelCatalog.Directory;

    public string FfmpegLocation => MediaDecoder.FindFfmpeg() ?? "Not found on this machine.";

    public string PageTitle => Page switch
    {
        "Models" => "Models",
        "Settings" => "Settings",
        _ => "Transcribe"
    };

    /// <summary>
    /// The one line under the title. Every app in the range says what was found and what was
    /// changed here, never a bare "Ready".
    /// </summary>
    public string StatusLine => Page switch
    {
        "Models" => $"{Models.Count(m => m.IsDownloaded)} of {Models.Count} speech models downloaded"
                    + (SpeakerPack.IsDownloaded ? ", speaker pack ready" : ", speaker pack not downloaded")
                    + $" · kept in {ModelCatalog.ModelDirectory}",
        "Settings" => "What TranscribeGeek will and will not do, in plain words.",
        _ => Jobs.Count == 0
            ? "Nothing queued. Drop audio or video files here - they are read on this machine and nothing is uploaded."
            : $"{Jobs.Count} file{(Jobs.Count == 1 ? "" : "s")} · {Jobs.Count(j => j.State == JobState.Done)} done"
               + (Jobs.Any(j => j.State == JobState.Failed) ? $" · {Jobs.Count(j => j.State == JobState.Failed)} failed" : "")
    };

    // ---------------------------------------------------------------- readiness

    private string _readiness = "";
    /// <summary>Anything standing between the user and a working transcription, said plainly.</summary>
    public string Readiness { get => _readiness; private set => SetField(ref _readiness, value); }

    private bool _hasReadinessProblem;
    public bool HasReadinessProblem { get => _hasReadinessProblem; private set => SetField(ref _hasReadinessProblem, value); }

    public void RefreshReadiness()
    {
        foreach (var m in Models) m.Refresh();

        if (!Models.Any(m => m.IsDownloaded))
        {
            Readiness = "No speech model has been downloaded yet. Open Models and download one - " +
                        "Small is the usual choice at about 465 MB. Nothing is downloaded without you asking.";
            HasReadinessProblem = true;
        }
        else if (!MediaDecoder.FfmpegAvailable)
        {
            Readiness = "ffmpeg was not found, so only 16 kHz mono WAV files can be read. " +
                        "Put ffmpeg.exe next to TranscribeGeek to handle MP3, MP4, M4A and the rest.";
            HasReadinessProblem = true;
        }
        else
        {
            Readiness = "";
            HasReadinessProblem = false;
        }

        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(StatusLine));
    }

    /// <summary>
    /// Called by a model row after a download or a removal. If the user has just fetched their
    /// first model, select it for them - making somebody download a model and then hunt for the
    /// dropdown that turns it on would be a poor way to start.
    /// </summary>
    public void OnModelsChanged(ModelRowViewModel row)
    {
        if (row.IsDownloaded && SelectedModel is not { IsDownloaded: true })
            SelectedModel = row;

        RefreshReadiness();
    }

    /// <summary>The two models that working out who is speaking needs. One row, two files.</summary>
    public SpeakerPackViewModel SpeakerPack { get; }

    /// <summary>
    /// Called after the speaker pack is downloaded or removed. Turning the option on for somebody
    /// who has just downloaded the pack is the obvious thing to do; turning it off when the pack
    /// has gone is the honest one, because leaving a tick next to something that cannot run is a
    /// promise the app cannot keep.
    /// </summary>
    public void OnSpeakerPackChanged()
    {
        if (SpeakerPack.IsDownloaded) IdentifySpeakers = true;
        else IdentifySpeakers = false;

        OnPropertyChanged(nameof(CanIdentifySpeakers));
        OnPropertyChanged(nameof(SpeakerHint));
        OnPropertyChanged(nameof(StatusLine));
    }

    // ---------------------------------------------------------------- the queue

    public ObservableCollection<JobViewModel> Jobs { get; } = new();

    public void AddFiles(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (!File.Exists(p)) continue;
            if (!MediaDecoder.IsSupported(p)) continue;
            if (Jobs.Any(j => string.Equals(j.Job.SourcePath, p, StringComparison.OrdinalIgnoreCase))) continue;
            Jobs.Add(new JobViewModel(new TranscriptionJob { SourcePath = p }));
        }
        OnPropertyChanged(nameof(StatusLine));
        OnPropertyChanged(nameof(HasJobs));
        OnPropertyChanged(nameof(CanStart));
    }

    public void ClearFinished()
    {
        foreach (var j in Jobs.Where(j => j.Job.State is JobState.Done or JobState.Failed or JobState.Cancelled).ToList())
            Jobs.Remove(j);
        OnPropertyChanged(nameof(StatusLine));
        OnPropertyChanged(nameof(HasJobs));
    }

    public bool HasJobs => Jobs.Count > 0;

    // ---------------------------------------------------------------- options

    public ObservableCollection<ModelRowViewModel> Models { get; } = new();

    private ModelRowViewModel _selectedModel = null!;
    public ModelRowViewModel SelectedModel
    {
        get => _selectedModel;
        set { if (SetField(ref _selectedModel, value)) OnPropertyChanged(nameof(CanStart)); }
    }

    public ObservableCollection<LanguageOption> Languages { get; } = new(LanguageOption.Common);

    private LanguageOption _selectedLanguage = LanguageOption.Common[0];
    public LanguageOption SelectedLanguage { get => _selectedLanguage; set => SetField(ref _selectedLanguage, value); }

    private bool _writeSubtitles = true;
    public bool WriteSubtitles { get => _writeSubtitles; set => SetField(ref _writeSubtitles, value); }

    private bool _writeTimestamps = true;
    public bool WriteTimestamps { get => _writeTimestamps; set => SetField(ref _writeTimestamps, value); }

    private bool _identifySpeakers;
    /// <summary>Off until the speaker pack is here, and off again if it is removed.</summary>
    public bool IdentifySpeakers
    {
        get => _identifySpeakers && CanIdentifySpeakers;
        set { if (SetField(ref _identifySpeakers, value)) OnPropertyChanged(nameof(SpeakerHint)); }
    }

    public bool CanIdentifySpeakers => SpeakerPack.IsDownloaded;

    /// <summary>The line under the tick box. Says the one thing the user needs to know right now.</summary>
    public string SpeakerHint => CanIdentifySpeakers
        ? "Adds a Speaker 1, Speaker 2 label to each line. It is a good guess, not a certainty - " +
          "similar voices on a poor recording can be merged, and one voice can occasionally be split."
        : "Needs the speaker pack, which is a " +
          $"{SpeakerModelCatalog.TotalBytes / 1_000_000d:0} MB download on the Models screen.";

    public ObservableCollection<SpeakerCountOption> SpeakerCounts { get; } = new(SpeakerCountOption.All);

    private SpeakerCountOption _selectedSpeakerCount = SpeakerCountOption.All[0];
    public SpeakerCountOption SelectedSpeakerCount
    {
        get => _selectedSpeakerCount;
        set => SetField(ref _selectedSpeakerCount, value);
    }

    // ---------------------------------------------------------------- running

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!SetField(ref _isRunning, value)) return;
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(NotRunning));
        }
    }

    public bool NotRunning => !IsRunning;

    public bool CanStart => !IsRunning
                            && Jobs.Any(j => j.Job.State == JobState.Waiting)
                            && SelectedModel is { IsDownloaded: true };

    /// <summary>
    /// Works through the queue one file at a time. Sequential on purpose - Whisper already uses
    /// every core it can, so running two files at once makes both slower and the progress
    /// meaningless.
    /// </summary>
    public async Task RunQueueAsync()
    {
        if (!CanStart) return;

        IsRunning = true;
        _cts = new CancellationTokenSource();
        var modelPath = ModelCatalog.PathFor(SelectedModel.Model);

        try
        {
            foreach (var vm in Jobs.ToList())
            {
                if (_cts.IsCancellationRequested) break;
                if (vm.Job.State != JobState.Waiting) continue;

                vm.SetState(JobState.Running, "Reading the audio…");
                try
                {
                    vm.Job.Duration ??= await MediaDecoder.TryGetDurationAsync(vm.Job.SourcePath, _cts.Token);
                    vm.Refresh();

                    var progress = new Progress<double>(p =>
                    {
                        vm.Job.Progress = p;
                        vm.SetState(JobState.Running, $"Transcribing… {p:P0}");
                    });

                    var speakerProgress = new Progress<double>(p =>
                        vm.SetState(JobState.Running, $"Working out who is speaking… {p:P0}"));

                    var result = await _engine.RunAsync(
                        vm.Job.SourcePath, modelPath, SelectedLanguage.Code,
                        vm.Job.Duration, progress,
                        IdentifySpeakers ? _diarizer : null,
                        SelectedSpeakerCount.Count,
                        speakerProgress,
                        stage => vm.SetState(JobState.Running, stage),
                        _cts.Token);

                    var segments = result.Segments;

                    if (segments.Count == 0)
                    {
                        vm.SetState(JobState.Failed, "No speech was found in that file.");
                        continue;
                    }

                    vm.Job.Segments.Clear();
                    vm.Job.Segments.AddRange(segments);

                    vm.Job.TranscriptPath =
                        TranscriptWriter.WritePlainText(vm.Job.SourcePath, segments, WriteTimestamps);
                    if (WriteSubtitles)
                        vm.Job.SubtitlePath = TranscriptWriter.WriteSubRip(vm.Job.SourcePath, segments);

                    var saved = $"Saved {Path.GetFileName(vm.Job.TranscriptPath)}"
                                + (vm.Job.SubtitlePath is null ? "" : $" and {Path.GetFileName(vm.Job.SubtitlePath)}");

                    // A speaker pass that did not work is said out loud next to a transcript that
                    // did, rather than being swallowed or being allowed to fail the whole job.
                    if (result.SpeakerProblem is not null)
                        vm.SetState(JobState.Done, $"{saved}. Speakers were not worked out: {result.SpeakerProblem}");
                    else if (result.SpeakerCount > 0)
                        vm.SetState(JobState.Done,
                            $"{saved}. {result.SpeakerCount} speaker{(result.SpeakerCount == 1 ? "" : "s")} found.");
                    else
                        vm.SetState(JobState.Done, saved);
                }
                catch (OperationCanceledException)
                {
                    vm.SetState(JobState.Cancelled, "Stopped before it finished.");
                }
                catch (MediaDecodeException ex)
                {
                    vm.SetState(JobState.Failed, ex.Message);
                }
                catch (Exception ex)
                {
                    Log.Write($"{vm.Job.FileName}: {ex}");
                    vm.SetState(JobState.Failed, ex.Message);
                }

                OnPropertyChanged(nameof(StatusLine));
            }
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
            OnPropertyChanged(nameof(StatusLine));
        }
    }

    public void Cancel() => _cts?.Cancel();

    /// <summary>Called when the window closes, so the loaded models are let go of properly.</summary>
    public void Shutdown()
    {
        _cts?.Cancel();
        _diarizer.Dispose();
        _engine.Dispose();
    }
}

/// <summary>
/// How many people are on the recording. Worth asking, because it is the one thing the person
/// dropping the file knows for certain and the model has to guess at.
/// </summary>
public sealed record SpeakerCountOption(int Count, string Name)
{
    public override string ToString() => Name;

    public static readonly SpeakerCountOption[] All =
        new[] { new SpeakerCountOption(0, "Work it out") }
            .Concat(Enumerable.Range(2, 9).Select(n => new SpeakerCountOption(n, $"{n} people")))
            .ToArray();
}

/// <summary>A language Whisper can be pointed at, plus the automatic option.</summary>
public sealed record LanguageOption(string Code, string Name)
{
    public override string ToString() => Name;

    public static readonly LanguageOption[] Common =
    {
        new("auto", "Detect automatically"),
        new("en", "English"),
        new("fr", "French"),
        new("de", "German"),
        new("es", "Spanish"),
        new("it", "Italian"),
        new("pt", "Portuguese"),
        new("nl", "Dutch"),
        new("pl", "Polish"),
        new("ru", "Russian"),
        new("uk", "Ukrainian"),
        new("cs", "Czech"),
        new("sv", "Swedish"),
        new("da", "Danish"),
        new("no", "Norwegian"),
        new("fi", "Finnish"),
        new("tr", "Turkish"),
        new("ar", "Arabic"),
        new("hi", "Hindi"),
        new("ja", "Japanese"),
        new("ko", "Korean"),
        new("zh", "Chinese"),
    };
}

/// <summary>Minimal INotifyPropertyChanged, matching the hand-rolled one in DriverGeek and CleanGeek.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

/// <summary>A command with no parameter. Enough for this app; no need for a toolkit.</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _run;
    private readonly Func<bool>? _can;

    public RelayCommand(Action run, Func<bool>? can = null) { _run = run; _can = can; }

    public bool CanExecute(object? parameter) => _can?.Invoke() ?? true;
    public void Execute(object? parameter) => _run();
    public event EventHandler? CanExecuteChanged;
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
