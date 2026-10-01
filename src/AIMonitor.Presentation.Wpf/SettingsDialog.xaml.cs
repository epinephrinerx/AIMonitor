using System.Windows;
using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf;

public partial class SettingsDialog : Window
{
    public SettingsDialog()
    {
        InitializeComponent();
    }

    public SettingsDialog(SettingsViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
        viewModel.RequestClose += OnRequestClose;
        viewModel.SaveFailed += OnSaveFailed;
    }

    private void OnRequestClose(bool result)
    {
        DialogResult = result;
        Close();
    }

    private void OnSaveFailed(string message)
    {
        System.Windows.MessageBox.Show(this, message, "Settings", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
    }
}
