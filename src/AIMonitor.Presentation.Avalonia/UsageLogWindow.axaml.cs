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
            model.PrintRequested += () => Print(model);
        };
    }

    /// <summary>
    /// Writes the print page to a private temporary folder and opens it, which brings up the print
    /// dialog. The file holds only what is already on screen; it is replaced on the next print.
    /// </summary>
    internal static void Print(UsageLogViewModel model)
    {
        try
        {
            var folder = Path.Combine(Path.GetTempPath(), "AIUsageMonitor");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "usage-log-print.html");
            File.WriteAllText(path, model.RenderPrintPage());
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            model.ReportPrintFailed(exception.Message);
        }
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
