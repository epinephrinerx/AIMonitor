using AIMonitor.Presentation.Avalonia.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace AIMonitor.Presentation.Avalonia;

public sealed partial class ConnectionsWindow : Window
{
    public ConnectionsWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ConnectionsViewModel model)
            {
                model.CloseRequested += Close;
            }
        };
    }

    private void OnSaveKey(object? sender, RoutedEventArgs e) => Write(sender, clear: false);

    private void OnClearKey(object? sender, RoutedEventArgs e) => Write(sender, clear: true);

    private void Write(object? sender, bool clear)
    {
        if (DataContext is not ConnectionsViewModel model) return;
        if ((sender as Control)?.DataContext is not ConnectionCardViewModel card) return;

        _ = clear ? model.ClearAsync(card) : model.SaveAsync(card);
    }
}
