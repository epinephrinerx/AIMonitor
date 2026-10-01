using System.Windows;
using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf;

public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();
    }

    public AboutDialog(AboutViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }
}
