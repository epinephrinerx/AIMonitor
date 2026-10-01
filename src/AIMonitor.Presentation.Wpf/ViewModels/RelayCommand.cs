using System.Windows.Input;

namespace AIMonitor.Presentation.Wpf.ViewModels;

/// <summary>
/// A parameterless command that delegates its execution logic.
/// </summary>
public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    private readonly Action _execute = execute ?? throw new ArgumentNullException(nameof(execute));
    private readonly Func<bool>? _canExecute = canExecute;

    private EventHandler? _internalCanExecuteChanged;

    public event EventHandler? CanExecuteChanged
    {
        add
        {
            CommandManager.RequerySuggested += value;
            _internalCanExecuteChanged += value;
        }
        remove
        {
            CommandManager.RequerySuggested -= value;
            _internalCanExecuteChanged -= value;
        }
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();

    public void RaiseCanExecuteChanged()
    {
        _internalCanExecuteChanged?.Invoke(this, EventArgs.Empty);
        CommandManager.InvalidateRequerySuggested();
    }
}

/// <summary>
/// A generic command that accepts a parameter of type T.
/// </summary>
public sealed class RelayCommand<T>(Action<T?> execute, Func<T?, bool>? canExecute = null) : ICommand
{
    private readonly Action<T?> _execute = execute ?? throw new ArgumentNullException(nameof(execute));
    private readonly Func<T?, bool>? _canExecute = canExecute;

    private EventHandler? _internalCanExecuteChanged;

    public event EventHandler? CanExecuteChanged
    {
        add
        {
            CommandManager.RequerySuggested += value;
            _internalCanExecuteChanged += value;
        }
        remove
        {
            CommandManager.RequerySuggested -= value;
            _internalCanExecuteChanged -= value;
        }
    }

    public bool CanExecute(object? parameter)
    {
        if (parameter is null && typeof(T).IsValueType)
        {
            return _canExecute?.Invoke(default) ?? true;
        }

        return _canExecute?.Invoke((T?)parameter) ?? true;
    }

    public void Execute(object? parameter) => _execute((T?)parameter);

    public void RaiseCanExecuteChanged()
    {
        _internalCanExecuteChanged?.Invoke(this, EventArgs.Empty);
        CommandManager.InvalidateRequerySuggested();
    }
}
