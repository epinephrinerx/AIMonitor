using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Navigation;
using AIMonitor.Presentation.Wpf.ViewModels;
using FontFamily = System.Windows.Media.FontFamily;

namespace AIMonitor.Presentation.Wpf;

/// <summary>One window for every About page, titled and sized per page like 1.3.3's separate dialogs.</summary>
public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();
    }

    public AboutDialog(AboutViewModel viewModel, AboutPage page = AboutPage.Version) : this()
    {
        DataContext = viewModel;
        ShowPage(viewModel, page);
    }

    private void ShowPage(AboutViewModel viewModel, AboutPage page)
    {
        VersionPanel.Visibility = page == AboutPage.Version ? Visibility.Visible : Visibility.Collapsed;
        DeveloperPanel.Visibility = page == AboutPage.Developer ? Visibility.Visible : Visibility.Collapsed;
        var isDocument = page is AboutPage.Readme or AboutPage.License or AboutPage.Notices;
        DocumentPanel.Visibility = isDocument ? Visibility.Visible : Visibility.Collapsed;

        (Title, var text) = page switch
        {
            AboutPage.Readme => ("AI Usage Monitor — Readme", viewModel.ReadmeText),
            AboutPage.License => ("AI Usage Monitor — License Agreement", viewModel.LicenseText),
            AboutPage.Notices => ("AI Usage Monitor — Third-party notices", viewModel.NoticesText),
            AboutPage.Developer => ("AI Usage Monitor — Developer", ""),
            _ => ("AI Usage Monitor — Version", ""),
        };

        if (isDocument)
        {
            DocumentText.Text = text;
            Width = 680;
            Height = 520;
            if (page == AboutPage.License)
            {
                DocumentText.FontFamily = new FontFamily("Consolas");
                DocumentText.FontSize = 11;
            }
        }
        else if (page == AboutPage.Developer)
        {
            Height = 330;
        }
    }

    private void OnNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // Non-fatal if no mail client or browser can be launched
        }

        e.Handled = true;
    }
}
