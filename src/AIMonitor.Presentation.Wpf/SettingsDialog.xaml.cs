using System;
using System.ComponentModel;
using System.Windows;
using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf;

public partial class SettingsDialog : Window
{
    private readonly SettingsViewModel? _viewModel;

    public SettingsDialog()
    {
        InitializeComponent();
        Closing += OnClosing;
    }

    public SettingsDialog(SettingsViewModel viewModel)
        : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.RequestClose += OnRequestClose;
        viewModel.SaveFailed += OnSaveFailed;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        var vm = _viewModel ?? DataContext as SettingsViewModel;
        if (vm is not null && vm.IsSaving)
        {
            e.Cancel = true;
            return;
        }

        vm?.Discard();
    }

    private void OnRequestClose(bool result)
    {
        try
        {
            DialogResult = result;
        }
        catch (InvalidOperationException)
        {
            // If window was not shown modally via ShowDialog() (e.g. in test hosts)
        }
        Close();
    }

    private void OnSaveFailed(string message)
    {
        System.Windows.MessageBox.Show(this, message, "Settings", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
    }
}
