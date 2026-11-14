using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace AIMonitor.Presentation.Avalonia;

/// <summary>
/// A short message with an OK button: what the Windows build says with a tray balloon or a message
/// box. Avalonia has no notification-centre API, and a small window in front is something the user
/// is sure to see.
/// </summary>
public sealed partial class NoticeWindow : Window
{
    public NoticeWindow() => AvaloniaXamlLoader.Load(this);

    public NoticeWindow(string title, string message)
        : this()
    {
        Title = title;
        this.FindControl<TextBlock>("Heading")!.Text = title;
        this.FindControl<TextBlock>("Body")!.Text = message;
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close();
}
