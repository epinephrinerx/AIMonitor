using System.Windows;
using AIMonitor.Application.Version;

namespace AIMonitor.Presentation.Wpf.Updates;

/// <summary>The message boxes and progress window of the update flow.</summary>
public sealed class WpfUpdateUi(Func<Window?> owner) : IUpdateUi
{
    private const string Caption = "AI Usage Monitor — Update";

    public bool ConfirmInstall(ReleaseInfo release, string installedVersion)
    {
        var published = string.IsNullOrEmpty(release.Published) ? "" : $" (published {release.Published})";
        var text = $"A newer version is available: {release.Tag}{published}. You have {installedVersion}.\n\n"
            + "Download and install it now? The installer comes straight from the GitHub release, "
            + "and the app closes so it can be replaced.";
        return Show(text, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    public void Notify(string message, bool isError) =>
        Show(message, MessageBoxButton.OK, isError ? MessageBoxImage.Warning : MessageBoxImage.Information);

    public IUpdateProgress ShowProgress(ReleaseInfo release, CancellationTokenSource cancel)
    {
        var window = new UpdateProgressWindow(release.Tag, cancel) { Owner = owner() };
        window.Show();
        return window;
    }

    private MessageBoxResult Show(string text, MessageBoxButton buttons, MessageBoxImage image)
    {
        var parent = owner();
        return parent is null
            ? System.Windows.MessageBox.Show(text, Caption, buttons, image)
            : System.Windows.MessageBox.Show(parent, text, Caption, buttons, image);
    }
}
