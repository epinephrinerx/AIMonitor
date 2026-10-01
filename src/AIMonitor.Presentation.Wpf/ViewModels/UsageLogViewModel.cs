using System.IO;
using System.Windows.Input;
using AIMonitor.Application.Reports;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace AIMonitor.Presentation.Wpf.ViewModels;

public sealed class UsageLogViewModel : ViewModelBase
{
    private readonly UsageReport _report;
    private string _formattedContent;

    public UsageLogViewModel(UsageReport report)
    {
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _formattedContent = UsageReportGenerator.ToMarkdown(report);

        SaveAsCommand = new RelayCommand(SaveAs);
    }

    public UsageReport Report => _report;

    public string FormattedContent
    {
        get => _formattedContent;
        set => SetProperty(ref _formattedContent, value);
    }

    public ICommand SaveAsCommand { get; }

    public event Action<string>? NotifyMessage;

    public void SaveAs()
    {
        var defaultName = UsageReportGenerator.DefaultFilename(_report.GeneratedAt, "csv");
        var dialog = new SaveFileDialog
        {
            FileName = defaultName,
            Filter = "Spreadsheet (*.csv)|*.csv|Markdown (*.md)|*.md|Web Page (*.html)|*.html|All Files (*.*)|*.*",
            DefaultExt = ".csv"
        };

        if (dialog.ShowDialog() == true)
        {
            var ext = Path.GetExtension(dialog.FileName).ToLowerInvariant();
            var content = ext switch
            {
                ".csv" => UsageReportGenerator.ToCsv(_report),
                ".html" or ".htm" => UsageReportGenerator.ToHtml(_report, dark: false),
                _ => UsageReportGenerator.ToMarkdown(_report)
            };

            try
            {
                File.WriteAllText(dialog.FileName, content);
                NotifyMessage?.Invoke($"Log saved successfully to {Path.GetFileName(dialog.FileName)}");
            }
            catch (Exception ex)
            {
                NotifyMessage?.Invoke($"Failed to save file: {ex.Message}");
            }
        }
    }
}
