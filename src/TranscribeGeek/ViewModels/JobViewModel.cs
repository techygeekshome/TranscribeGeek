using System.Diagnostics;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Threading;
using TranscribeGeek.Core.Models;

namespace TranscribeGeek.ViewModels;

/// <summary>
/// One row in the queue. Wraps a <see cref="TranscriptionJob"/> rather than replacing it, so the
/// Core project stays free of anything to do with the screen and the same job object can be
/// handed to the engine untouched.
/// </summary>
public sealed class JobViewModel : ObservableObject
{
    public JobViewModel(TranscriptionJob job)
    {
        Job = job;
        OpenTranscript = new RelayCommand(() => OpenPath(Job.TranscriptPath));
        OpenFolder = new RelayCommand(() => OpenPath(Path.GetDirectoryName(Job.SourcePath)));
    }

    /// <summary>Opens the finished transcript in whatever the machine uses for .txt files.</summary>
    public ICommand OpenTranscript { get; }

    /// <summary>Opens the folder the transcript was written to.</summary>
    public ICommand OpenFolder { get; }

    private static void OpenPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // No association for .txt, or the file has been moved since. Not worth a dialog.
        }
    }

    public TranscriptionJob Job { get; }

    public string FileName => Job.FileName;
    public string FolderName => Path.GetDirectoryName(Job.SourcePath) ?? "";

    public JobState State => Job.State;
    public string Status => Job.Status;
    public double Progress => Job.Progress * 100;

    /// <summary>
    /// Length of the media, or an empty string. Deliberately blank rather than "unknown" - a row
    /// that cannot tell you the length should say nothing, not apologise.
    /// </summary>
    public string DurationText => Job.Duration is { } d
        ? d.TotalHours >= 1 ? $"{(int)d.TotalHours}:{d.Minutes:00}:{d.Seconds:00}" : $"{d.Minutes}:{d.Seconds:00}"
        : "";

    public bool ShowProgress => Job.State == JobState.Running;
    public bool HasOutput => Job.State == JobState.Done && Job.TranscriptPath is not null;

    /// <summary>
    /// The one coloured thing on the row. Green done, red failed, blue working, grey waiting -
    /// the same four meanings the rest of the range uses, so a glance reads the same everywhere.
    /// </summary>
    public IBrush StateBrush => Job.State switch
    {
        JobState.Done => Brush.Parse("#3BA55C"),
        JobState.Failed => Brush.Parse("#FF5E5B"),
        JobState.Running => Brush.Parse("#2E78D8"),
        JobState.Cancelled => Brush.Parse("#E0A62B"),
        _ => Brush.Parse("#4A5468")
    };

    public string StateText => Job.State switch
    {
        JobState.Done => "Done",
        JobState.Failed => "Failed",
        JobState.Running => "Working",
        JobState.Cancelled => "Stopped",
        _ => "Waiting"
    };

    /// <summary>
    /// Moves the job on and says why, in one call, so a state and its explanation can never
    /// disagree on screen. Marshalled to the UI thread because progress from Whisper arrives on
    /// whichever thread happened to be decoding.
    /// </summary>
    public void SetState(JobState state, string status)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SetState(state, status));
            return;
        }

        Job.State = state;
        Job.Status = status;
        if (state == JobState.Failed) Job.Error = status;
        Refresh();
    }

    /// <summary>Re-reads everything from the underlying job. Cheaper than wiring twelve setters.</summary>
    public void Refresh()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(Refresh);
            return;
        }

        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(ShowProgress));
        OnPropertyChanged(nameof(HasOutput));
        OnPropertyChanged(nameof(StateBrush));
        OnPropertyChanged(nameof(StateText));
    }
}
