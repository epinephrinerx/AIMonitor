using System.Windows.Input;

namespace AIMonitor.Presentation.Avalonia.ViewModels;

/// <summary>
/// <see cref="ICommand"/> lives in the base class libraries, not in either UI toolkit, so the same
/// command shape works under Avalonia and WPF.
/// </summary>
public sealed class AsyncCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private bool _running;

    public AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;

        _running = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute().ConfigureAwait(true);
        }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>The synchronous counterpart, for commands that only raise an event.</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;

    public RelayCommand(Action execute) => _execute = execute ?? throw new ArgumentNullException(nameof(execute));

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => _execute();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
