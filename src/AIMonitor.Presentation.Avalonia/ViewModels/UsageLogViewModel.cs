using System.Windows.Input;
using AIMonitor.Application.Reports;

namespace AIMonitor.Presentation.Avalonia.ViewModels;

/// <summary>The usage log: the report as text, and a way to keep a copy of it.</summary>
public sealed class UsageLogViewModel : ObservableObject
{
    private string _message = string.Empty;

    public UsageLogViewModel(UsageReport report)
    {
        Report = report ?? throw new ArgumentNullException(nameof(report));
        Content = UsageReportGenerator.ToMarkdown(report);
        SaveAsCommand = new RelayCommand(() => SaveRequested?.Invoke());
        PrintCommand = new RelayCommand(() => PrintRequested?.Invoke());
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke());
    }

    /// <summary>Raised when the user asks to save; the window owns the file picker.</summary>
    public event Action? SaveRequested;

    public event Action? CloseRequested;

    /// <summary>Raised when the user asks to print; the window hands the page to the system.</summary>
    public event Action? PrintRequested;

    public ICommand PrintCommand { get; }

    /// <summary>
    /// The page that is printed: the report in the light palette whatever the app's theme (PAR-031),
    /// asking the browser for its print dialog as soon as it has loaded. macOS has no print API this
    /// toolkit can reach, so the browser's print dialog, with its preview and "Save as PDF", is the
    /// Mac's counterpart of the Windows print dialog.
    /// </summary>
    public string RenderPrintPage()
    {
        var body = UsageReportGenerator.ToHtml(Report, dark: false)
            .Replace("font-family:Segoe UI, sans-serif", "font-family:-apple-system, BlinkMacSystemFont, 'Helvetica Neue', sans-serif", StringComparison.Ordinal);
        return "<!doctype html>\n<html lang=\"en\"><head><meta charset=\"utf-8\"><title>AI Usage Monitor usage log</title>"
            + "<style>@media print { body { margin: 0 !important; } }</style></head>\n"
            + body
            + "<script>window.addEventListener('load', function () { window.print(); });</script>\n</html>\n";
    }

    public void ReportPrintFailed(string reason) => Message = $"Could not print the log: {reason}";

    public UsageReport Report { get; }

    public string Content { get; }

    public ICommand SaveAsCommand { get; }

    public ICommand CloseCommand { get; }

    public string SuggestedFileName => UsageReportGenerator.DefaultFilename(Report.GeneratedAt, "csv");

    public string Message
    {
        get => _message;
        private set
        {
            if (Set(ref _message, value)) Raise(nameof(HasMessage));
        }
    }

    public bool HasMessage => _message.Length > 0;

    /// <summary>
    /// The report in whichever format the chosen name asks for. The extension decides, so saving as
    /// <c>.csv</c> produces a spreadsheet rather than markdown with a misleading name.
    /// </summary>
    public string Render(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".csv" => UsageReportGenerator.ToCsv(Report),
            ".html" or ".htm" => UsageReportGenerator.ToHtml(Report, dark: false),
            _ => UsageReportGenerator.ToMarkdown(Report),
        };
    }

    public void ReportSaved(string path) => Message = $"Saved to {Path.GetFileName(path)}";

    public void ReportSaveFailed(string reason) => Message = $"Could not save the log: {reason}";
}
