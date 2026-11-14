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

        Closing += (_, e) =>
        {
            if (DataContext is not SettingsViewModel model) return;

            // A save still being written finishes before the window goes, so its outcome (and an
            // error, if any) has somewhere to be shown.
            if (model.IsSaving)
            {
                e.Cancel = true;
                return;
            }

            // Closing any way other than Save is a Cancel: the live preview is undone.
            model.Discard();
        };
    }
}
