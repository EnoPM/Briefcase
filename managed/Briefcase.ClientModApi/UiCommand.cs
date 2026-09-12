using System.Windows.Input;

namespace Briefcase.ClientModApi;

/// <summary>
/// Small ViewModel-friendly command used by Briefcase controls. Call
/// <see cref="NotifyCanExecuteChanged"/> when data affecting availability changes.
/// </summary>
public sealed class UiCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool> _canExecute;

    public UiCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute ?? (() => true);
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute();

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter)) _execute();
    }

    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
