using System.Windows.Input;
using Lacertae.Application.Home;

namespace Lacertae.Desktop.ViewModels.Home;

public sealed class HomeModuleViewModel
{
    private readonly Action<HomeQuickAction>? executeQuickAction;

    public HomeModuleViewModel(
        HomeModuleState state,
        IReadOnlyList<HomeQuickAction>? quickActions = null,
        Action<HomeQuickAction>? executeQuickAction = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        this.executeQuickAction = executeQuickAction;
        Module = state.Module;
        Order = state.Order;
        IsVisible = state.IsVisible;
        Title = state.Title;
        Summary = state.Summary;
        HasError = state.HasError;
        ErrorCode = state.ErrorCode;
        QuickActions = quickActions ?? [];
        ExecuteQuickActionCommand = new DelegateCommand(parameter =>
        {
            if (parameter is HomeQuickAction action)
            {
                if (Enum.IsDefined(action.Id) && QuickActions.Contains(action))
                {
                    this.executeQuickAction?.Invoke(action);
                }
            }
        });
    }

    public Lacertae.Domain.Home.HomeModuleId Module { get; }

    public int Order { get; }

    public bool IsVisible { get; }

    public string Title { get; }

    public string Summary { get; }

    public bool HasError { get; }

    public string? ErrorCode { get; }

    public string ErrorSummary => HasError
        ? "此板块暂时不可用，请稍后重试。"
        : Summary;

    public IReadOnlyList<HomeQuickAction> QuickActions { get; }

    public ICommand ExecuteQuickActionCommand { get; }

    private sealed class DelegateCommand(Action<object?> execute) : ICommand
    {
        event EventHandler? ICommand.CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => execute(parameter);
    }
}
