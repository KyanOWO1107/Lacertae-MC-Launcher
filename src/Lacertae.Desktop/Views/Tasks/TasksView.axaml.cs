using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Lacertae.Desktop.Views.Tasks;

public sealed partial class TasksView : UserControl
{
    public TasksView() => AvaloniaXamlLoader.Load(this);
}
