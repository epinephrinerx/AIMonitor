using AIMonitor.Presentation.Avalonia.ViewModels;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace AIMonitor.Presentation.Avalonia;

public sealed partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SettingsViewModel model)
            {
                model.RequestClose += _ => Close();
            }
        };
    }
}
