using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Lacertae.Desktop.ViewModels.Diagnostics;

namespace Lacertae.Desktop.Views.Diagnostics;

public sealed partial class DiagnosticPreviewView : UserControl
{
    public DiagnosticPreviewView() => AvaloniaXamlLoader.Load(this);

    private async void PickSavePath(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DiagnosticPreviewViewModel viewModel)
        {
            return;
        }

        TopLevel? topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is not { CanSave: true } storageProvider)
        {
            viewModel.RequestSave(string.Empty);
            return;
        }

        IStorageFile? file = await storageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                SuggestedFileName = "lacertae-diagnostics.zip",
                DefaultExtension = "zip",
                ShowOverwritePrompt = true,
                FileTypeChoices =
                [
                    new FilePickerFileType("ZIP 诊断包")
                    {
                        Patterns = ["*.zip"],
                    },
                ],
            });
        string? localPath = file?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(localPath))
        {
            viewModel.RequestSave(localPath);
        }
    }
}
