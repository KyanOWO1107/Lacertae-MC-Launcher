using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Lacertae.Desktop.Views.Versions;

public sealed partial class VersionSettingsView : UserControl
{
    public VersionSettingsView() => AvaloniaXamlLoader.Load(this);
}
