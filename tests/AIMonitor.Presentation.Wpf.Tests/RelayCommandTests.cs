using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf.Tests;

public class RelayCommandTests
{
    [Fact]
    public void Constructor_NullAction_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new RelayCommand(null!));
    }

    [Fact]
    public void CanExecute_NoPredicate_ReturnsTrue()
    {
        var command = new RelayCommand(() => { });
        Assert.True(command.CanExecute(null));
    }

    [Fact]
    public void CanExecute_WithPredicate_EvaluatesCorrectly()
    {
        var canRun = false;
        var command = new RelayCommand(() => { }, () => canRun);

        Assert.False(command.CanExecute(null));

        canRun = true;
        Assert.True(command.CanExecute(null));
    }

    [Fact]
    public void Execute_InvokesAction()
    {
        var executed = false;
        var command = new RelayCommand(() => executed = true);

        command.Execute(null);

        Assert.True(executed);
    }

    [Fact]
    public void RaiseCanExecuteChanged_FiresCanExecuteChangedEvent()
    {
        var command = new RelayCommand(() => { });
        var fired = false;
        command.CanExecuteChanged += (_, _) => fired = true;

        command.RaiseCanExecuteChanged();

        Assert.True(fired);
    }
}
