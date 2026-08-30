using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;

namespace TranscribeGeek.Views;

public partial class TranscribeView : UserControl
{
    public TranscribeView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// The "Choose files…" button on the empty state. It hands straight back to the window so
    /// there is one file picker in the app rather than two that could drift apart.
    /// </summary>
    private async void OnAddFiles(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (this.GetVisualRoot() is MainWindow window)
            await window.PickFilesAsync();
    }
}
