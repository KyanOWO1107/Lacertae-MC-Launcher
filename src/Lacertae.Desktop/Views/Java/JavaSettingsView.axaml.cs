using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Lacertae.Desktop.ViewModels.Java;

namespace Lacertae.Desktop.Views.Java;

public partial class JavaSettingsView : UserControl
{
    public JavaSettingsView() => InitializeComponent();

    private async void CopyPath(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: JavaRuntimeItem item })
        {
            return;
        }

        TopLevel? topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(item.ExecutablePath);
        }
    }
}
