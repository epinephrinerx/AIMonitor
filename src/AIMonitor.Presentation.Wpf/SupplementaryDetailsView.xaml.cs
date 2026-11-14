using UserControl = System.Windows.Controls.UserControl;

namespace AIMonitor.Presentation.Wpf;

/// <summary>
/// A dedicated supplementary details user control displaying account tier, workspace credits,
/// reached-limit reason, quota reset entitlement status, and activity metrics under the gauges.
/// </summary>
public partial class SupplementaryDetailsView : UserControl
{
    public SupplementaryDetailsView()
    {
        InitializeComponent();
    }
}
