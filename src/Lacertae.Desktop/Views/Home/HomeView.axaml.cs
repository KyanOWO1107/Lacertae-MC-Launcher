using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Lacertae.Desktop.Views.Home;

public sealed partial class HomeView : UserControl
{
    public HomeView() => AvaloniaXamlLoader.Load(this);
}
