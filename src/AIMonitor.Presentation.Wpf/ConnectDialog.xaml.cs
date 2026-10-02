using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf;

/// <summary>
/// Interaction logic for ConnectDialog.xaml.
/// Displays connection details and credentials input for a provider.
/// Follows three-way key semantics without network validation on the UI thread.
/// </summary>
public partial class ConnectDialog : System.Windows.Window
{
    private bool _isSyncingKey;

    public ConnectDialog()
    {
        InitializeComponent();
    }

    public ConnectDialog(ConnectDialogViewModel viewModel)
        : this()
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;

        viewModel.RequestClose += OnRequestClose;
        viewModel.SaveFailed += OnSaveFailed;
        viewModel.BrowseRequested += OnBrowseRequested;

        KeyPasswordBox.Password = viewModel.Key ?? string.Empty;
        KeyTextBox.Text = viewModel.Key ?? string.Empty;

        KeyPasswordBox.PasswordChanged += OnPasswordChanged;
        KeyTextBox.TextChanged += OnKeyTextChanged;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_isSyncingKey) return;
        if (DataContext is ConnectDialogViewModel vm)
        {
            _isSyncingKey = true;
            try
            {
                vm.Key = KeyPasswordBox.Password;
                if (KeyTextBox.Text != KeyPasswordBox.Password)
                {
                    KeyTextBox.Text = KeyPasswordBox.Password;
                }
            }
            finally
            {
                _isSyncingKey = false;
            }
        }
    }

    private void OnKeyTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isSyncingKey) return;
        if (DataContext is ConnectDialogViewModel vm)
        {
            _isSyncingKey = true;
            try
            {
                vm.Key = KeyTextBox.Text;
                if (KeyPasswordBox.Password != KeyTextBox.Text)
                {
                    KeyPasswordBox.Password = KeyTextBox.Text;
                }
            }
            finally
            {
                _isSyncingKey = false;
            }
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConnectDialogViewModel.Key))
        {
            if (_isSyncingKey) return;
            if (DataContext is ConnectDialogViewModel vm)
            {
                _isSyncingKey = true;
                try
                {
                    var newKey = vm.Key ?? string.Empty;
                    if (KeyPasswordBox.Password != newKey)
                    {
                        KeyPasswordBox.Password = newKey;
                    }
                    if (KeyTextBox.Text != newKey)
                    {
                        KeyTextBox.Text = newKey;
                    }
                }
                finally
                {
                    _isSyncingKey = false;
                }
            }
        }
    }

    private void OnRequestClose(bool result)
    {
        try
        {
            DialogResult = result;
        }
        catch (InvalidOperationException)
        {
            // If window was not shown modally via ShowDialog() (e.g. in headless unit tests)
        }
        Close();
    }

    private void OnSaveFailed(string message)
    {
        System.Windows.MessageBox.Show(this, message, "Connection Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
    }

    private void OnBrowseRequested()
    {
        var openFileDialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "JSON files (*.json)|*.json",
            Title = "Select service account JSON"
        };

        if (openFileDialog.ShowDialog(this) == true)
        {
            if (DataContext is ConnectDialogViewModel vm)
            {
                vm.SetKeyFromBrowse(openFileDialog.FileName);
            }
        }
    }
}
