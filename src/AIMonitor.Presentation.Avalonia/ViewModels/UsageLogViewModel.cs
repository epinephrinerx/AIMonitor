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
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke());
    }

    /// <summary>Raised when the user asks to save; the window owns the file picker.</summary>
    public event Action? SaveRequested;

    public event Action? CloseRequested;

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
