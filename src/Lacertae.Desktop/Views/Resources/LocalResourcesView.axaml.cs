using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Lacertae.Desktop.Views.Resources;

public sealed partial class LocalResourcesView : UserControl
{
    public LocalResourcesView() => AvaloniaXamlLoader.Load(this);
}
