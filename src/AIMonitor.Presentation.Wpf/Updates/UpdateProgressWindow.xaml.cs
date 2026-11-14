using System.Windows;

namespace AIMonitor.Presentation.Wpf.Updates;

public partial class UpdateProgressWindow : Window, IUpdateProgress
{
    private readonly CancellationTokenSource _cancel;

    public UpdateProgressWindow(string tag, CancellationTokenSource cancel)
    {
        InitializeComponent();
        _cancel = cancel;
        Heading.Text = $"Downloading {tag}…";
    }

    /// <summary>Called from the download loop, so it hops to the UI thread when it must.</summary>
    public void Report(double value)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Report(value));
            return;
        }

        Bar.Value = value;
        Percent.Text = $"{value:P0}";
    }

    public void Dispose() => Close();

    private void OnCancel(object sender, RoutedEventArgs e) => _cancel.Cancel();
}
