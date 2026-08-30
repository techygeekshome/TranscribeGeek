using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace TranscribeGeek.Views;

public partial class ModelsView : UserControl
{
    public ModelsView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
