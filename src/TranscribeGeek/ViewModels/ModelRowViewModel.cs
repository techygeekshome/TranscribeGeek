using System.Windows.Input;
using Avalonia.Threading;
using TranscribeGeek.Core.Models;
using TranscribeGeek.Core.Services;

namespace TranscribeGeek.ViewModels;

/// <summary>
/// One row on the Models screen: what a model is for, how big it is, and whether it is here yet.
///
/// Downloading is the only thing TranscribeGeek ever fetches from the internet, and it only
/// happens when somebody presses the button on this row. That is worth stating plainly because
/// the whole point of the app is that recordings never leave the machine.
/// </summary>
public sealed class ModelRowViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private CancellationTokenSource? _cts;

    public ModelRowViewModel(WhisperModel model, ShellViewModel shell)
    {
        Model = model;
        _shell = shell;

        Download = new RelayCommand(() => _ = DownloadAsync());
        Cancel = new RelayCommand(() => _cts?.Cancel());
        Remove = new RelayCommand(RemoveModel);
    }

    public WhisperModel Model { get; }

    public string Name => Model.Name;
    public string Blurb => Model.Blurb;

    /// <summary>The download size, or the real size on disk once it is here.</summary>
    public string SizeText => IsDownloaded
        ? Bytes(ModelCatalog.SizeOnDisk(Model))
        : Bytes(Model.ApproxBytes) + " download";

    public bool IsDownloaded => ModelCatalog.IsDownloaded(Model);
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
    /// <summary>Whatever the row needs to say right now: progress, or why a download failed.</summary>
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
                Note = $"{p:P0} of {Bytes(Model.ApproxBytes)}";
            });

            await ModelCatalog.DownloadAsync(Model, progress, _cts.Token);
            Note = "";
        }
        catch (OperationCanceledException)
        {
            Note = "Cancelled. Nothing was kept.";
        }
        catch (Exception ex)
        {
            Log.Write($"Model {Model.Id}: {ex}");
            Note = "That download did not finish: " + ex.Message;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsDownloading = false;
            Refresh();
            _shell.OnModelsChanged(this);
        }
    }

    private void RemoveModel()
    {
        try
        {
            ModelCatalog.Delete(Model);
            Note = "";
        }
        catch (Exception ex)
        {
            Note = "It could not be removed: " + ex.Message;
        }

        Refresh();
        _shell.OnModelsChanged(this);
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

    /// <summary>
    /// Sizes in the units people actually use. Whole numbers under a gigabyte - "465 MB" reads
    /// better than "465.31 MB" and nobody is making a decision on the decimal.
    /// </summary>
    private static string Bytes(long b) => b >= 1_000_000_000
        ? $"{b / 1_000_000_000d:0.0} GB"
        : $"{b / 1_000_000d:0} MB";

    public override string ToString() => $"{Name} — {SizeText}";
}
