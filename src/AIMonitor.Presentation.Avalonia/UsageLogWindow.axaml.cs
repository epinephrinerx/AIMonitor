using AIMonitor.Presentation.Avalonia.ViewModels;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

namespace AIMonitor.Presentation.Avalonia;

public sealed partial class UsageLogWindow : Window
{
    public UsageLogWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not UsageLogViewModel model) return;
            model.CloseRequested += Close;
            model.SaveRequested += () => _ = SaveAsync(model);
        };
    }

    /// <summary>The file picker belongs to the window, so the view model stays testable without one.</summary>
    private async Task SaveAsync(UsageLogViewModel model)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the usage log",
            SuggestedFileName = model.SuggestedFileName,
            DefaultExtension = "csv",
            FileTypeChoices =
            [
                new FilePickerFileType("Spreadsheet") { Patterns = ["*.csv"] },
                new FilePickerFileType("Markdown") { Patterns = ["*.md"] },
                new FilePickerFileType("Web page") { Patterns = ["*.html"] },
            ],
        });

        if (file is null) return;

        try
        {
            var path = file.TryGetLocalPath() ?? file.Name;
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(model.Render(path));
            model.ReportSaved(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            model.ReportSaveFailed(exception.Message);
        }
    }
}
