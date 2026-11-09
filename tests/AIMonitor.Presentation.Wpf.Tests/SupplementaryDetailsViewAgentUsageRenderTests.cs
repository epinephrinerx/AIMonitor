using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf.Tests;

/// <summary>
/// P4/C8/C19 — one render test of <see cref="SupplementaryDetailsView"/> showing the AGY ccusage-only
/// section: "a ccusage-only section therefore shows the header and the ccusage rows only" (C19), and
/// the reset row Border collapses when HasResetInfo is false (C19). The view is realised exactly as in
/// ConnectionsPageAndDialogXamlTests (WpfTestHost STA host, off-screen window, Realize).
/// Written from the spec only (§5.6); compiles once the P4 code merges.
/// </summary>
[Collection("Wpf")]
public sealed class SupplementaryDetailsViewAgentUsageRenderTests
{
    [Fact]
    public void SupplementaryDetailsView_WhenAgyCcUsageOnlySection_RendersHeaderAndRowsAndCollapsesResetCard()
    {
        WpfTestHost.Run(() =>
        {
            // C8: AGY with a summary that has lines → Supplementary with only the ccusage rows,
            // ShowSupplementary = true. Value oracle: "<TokensText> · <CostText>" (P4).
            var vm = new ProviderTabViewModel("agy", "Gemini / AGY");
            vm.SetAgentUsage(new AgentUsageSummary(
                new AgentUsageLine("antigravity", "Antigravity", 1_234, "1,234", "$1.23"),
                new AgentUsageLine("antigravity", "Antigravity", 9_999, "9,999", "$10.50")));

            Assert.True(vm.ShowSupplementary);
            Assert.False(vm.HasResetInfo); // C19: false for a ccusage-only section

            var view = new SupplementaryDetailsView { DataContext = vm };
            var window = new Window { Content = view, Width = 520, Height = 640 };
            using var _ = WpfTestHost.ShowOffscreen(window);
            WpfTestHost.Realize(view);

            // The section itself is visible (ShowSupplementary drives the UserControl trigger).
            Assert.True(view.IsVisible);

            // C19: "shows the header and the ccusage rows only".
            var visibleTexts = FindVisibleTexts(view);
            Assert.Contains("Supplementary Details", visibleTexts);
            Assert.Contains("Today (ccusage)", visibleTexts);
            Assert.Contains("1,234 · $1.23", visibleTexts);
            Assert.Contains("This month (ccusage)", visibleTexts);
            Assert.Contains("9,999 · $10.50", visibleTexts);

            // C19: the reset Border collapses via the same trigger style as the activity border —
            // the "Reset details" expander exists in the tree but is not visible.
            var resetExpander = Assert.Single(FindVisualChildren<Expander>(view));
            Assert.False(resetExpander.IsVisible);

            // C20/C8: with no reset part there is no default "Rate Limit Resets" title rendered.
            Assert.DoesNotContain("Rate Limit Resets", visibleTexts);
        });
    }

    private static List<string> FindVisibleTexts(DependencyObject root) =>
        FindVisualChildren<TextBlock>(root).Where(t => t.IsVisible).Select(t => t.Text).ToList();

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject depObj) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(depObj);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(depObj, i);
            if (child is T t)
            {
                yield return t;
            }

            foreach (var childOfChild in FindVisualChildren<T>(child))
            {
                yield return childOfChild;
            }
        }
    }
}
