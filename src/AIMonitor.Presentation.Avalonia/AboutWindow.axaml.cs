using System.Diagnostics;
using AIMonitor.Presentation.Avalonia.ViewModels;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace AIMonitor.Presentation.Avalonia;

public sealed partial class AboutWindow : Window
{
    public AboutWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not AboutViewModel model) return;
            model.CloseRequested += Close;
            model.OpenUrlRequested += OpenInBrowser;
        };
    }

    /// <summary>
    /// Hands the URL to the system, which opens it in whatever browser the user chose.
    /// <c>UseShellExecute</c> is what makes that happen rather than trying to run the URL.
    /// </summary>
    private static void OpenInBrowser(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Nothing to open it with; silently doing nothing is better than a crash from About.
        }
    }
}
