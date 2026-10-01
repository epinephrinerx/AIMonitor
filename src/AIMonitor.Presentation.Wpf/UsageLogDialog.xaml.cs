using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using AIMonitor.Presentation.Wpf.ViewModels;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
using PrintDialog = System.Windows.Controls.PrintDialog;

namespace AIMonitor.Presentation.Wpf;

public partial class UsageLogDialog : Window
{
    private readonly UsageLogViewModel? _viewModel;

    public UsageLogDialog()
    {
        InitializeComponent();
    }

    public UsageLogDialog(UsageLogViewModel viewModel) : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;

        viewModel.NotifyMessage += msg =>
        {
            StatusLabel.Text = msg;
        };
    }

    private void OnPrintClick(object sender, RoutedEventArgs e)
    {
        var printDialog = new PrintDialog();
        if (printDialog.ShowDialog() == true)
        {
            var doc = new FlowDocument(new Paragraph(new Run(ContentTextBox.Text)))
            {
                FontFamily = new FontFamily("Consolas, monospace"),
                FontSize = 11,
                PagePadding = new Thickness(40),
                Foreground = Brushes.Black,
                Background = Brushes.White
            };

            var paginator = ((IDocumentPaginatorSource)doc).DocumentPaginator;
            printDialog.PrintDocument(paginator, "AIMonitor Usage Log");
            StatusLabel.Text = "Report sent to printer.";
        }
    }
}
