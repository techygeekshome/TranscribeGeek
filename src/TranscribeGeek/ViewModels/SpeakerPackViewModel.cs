using System.Windows.Input;
using Avalonia.Threading;
using TranscribeGeek.Core.Services;

namespace TranscribeGeek.ViewModels;

/// <summary>
/// The speaker pack row on the Models screen. Two files, treated as one thing, because from the
/// outside it is one thing: the ability to say who is talking.
/// </summary>
public sealed class SpeakerPackViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private CancellationTokenSource? _cts;

    public SpeakerPackViewModel(ShellViewModel shell)
    {
        _shell = shell;
        Download = new RelayCommand(() => _ = DownloadAsync());
        Cancel = new RelayCommand(() => _cts?.Cancel());
        Remove = new RelayCommand(RemovePack);
    }

    public string SizeText => IsDownloaded
        ? $"{SpeakerModelCatalog.SizeOnDisk / 1_000_000d:0} MB"
        : $"{SpeakerModelCatalog.TotalBytes / 1_000_000d:0} MB download";

    /// <summary>Who made each model and under what licence. Shown, not buried in a credits box.</summary>
    public string OriginText =>
        string.Join("  ·  ", SpeakerModelCatalog.All.Select(f => f.Origin));

    public bool IsDownloaded => SpeakerModelCatalog.IsReady;
    public bool IsMissing => !IsDownloaded && !IsDownloading;

    private bool _isDownloading;
    public bool IsDownloading
    {
        get => _isDownloading;
        private set
        {
            if (!SetField(ref _isDownloading, value)) return;
            OnPropertyChanged(nameof(IsMissing));
            OnPropertyChanged(nameof(CanRemove));
        }
    }

    public bool CanRemove => IsDownloaded && !IsDownloading;

    private double _progress;
    public double Progress { get => _progress; private set => SetField(ref _progress, value); }

    private string _note = "";
    public string Note { get => _note; private set => SetField(ref _note, value); }

    public ICommand Download { get; }
    public ICommand Cancel { get; }
    public ICommand Remove { get; }

    private async Task DownloadAsync()
    {
        if (IsDownloading || IsDownloaded) return;

        IsDownloading = true;
        Progress = 0;
        Note = "Starting…";
        _cts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<double>(p =>
            {
                Progress = p * 100;
                Note = $"{p:P0} of {SpeakerModelCatalog.TotalBytes / 1_000_000d:0} MB";
            });

            await SpeakerModelCatalog.DownloadAsync(progress, _cts.Token);
            Note = "";
        }
        catch (OperationCanceledException)
        {
            Note = "Cancelled. Nothing was kept.";
        }
        catch (Exception ex)
        {
            Log.Write($"Speaker pack: {ex}");
            Note = "That download did not finish: " + ex.Message;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsDownloading = false;
            Refresh();
            _shell.OnSpeakerPackChanged();
        }
    }

    private void RemovePack()
    {
        try
        {
            SpeakerModelCatalog.Delete();
            Note = "";
        }
        catch (Exception ex)
        {
            Note = "It could not be removed: " + ex.Message;
        }

        Refresh();
        _shell.OnSpeakerPackChanged();
    }

    public void Refresh()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(Refresh);
            return;
        }

        OnPropertyChanged(nameof(IsDownloaded));
        OnPropertyChanged(nameof(IsMissing));
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(SizeText));
    }
}
