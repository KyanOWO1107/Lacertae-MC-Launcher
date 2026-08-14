using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Lacertae.Desktop.ViewModels.Versions;

namespace Lacertae.Desktop.Views.Versions;

public sealed partial class VersionsView : UserControl
{
    public VersionsView() => AvaloniaXamlLoader.Load(this);

    private void EditSettings(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button ||
            button.DataContext is not VersionRowViewModel row ||
            DataContext is not VersionsViewModel viewModel)
        {
            return;
        }

        viewModel.RequestEditSettings(row);
        e.Handled = true;
    }
}
