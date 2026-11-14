using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AIMonitor.Application.AgentUsage;
using AIMonitor.Application.OpenCode;
using AIMonitor.Application.Settings;
using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.Theme;
using AIMonitor.Presentation.Wpf.ViewModels;
using AIMonitor.TestSupport;

namespace AIMonitor.Presentation.Wpf.Tests;

/// <summary>
/// R-020 F8 / R-021 G5 (Docs/SPEC_R020_R021.md, Tests item 6): the Connections page renders the fourth card and
/// the "Other agents" section in both themes. G5: inside the existing ScrollViewer, below the cards: heading
/// "Other agents" (SemiBold 14), the status text (12, secondary), then one bordered row per agent: name
/// (SemiBold 13) and guidance (12, wrapped, secondary); no buttons; existing brushes so both themes work.
/// Pixel-level evidence (46 ScreenEvidence screens, G6) is the Integrator's / user's check, not a unit test.
/// </summary>
[Collection("Wpf")]
public sealed class ConnectionsPageOpenCodeAndOtherAgentsTests
{
    private const string CopilotGuidance =
        "Run `copilot login` in a terminal (or /login inside GitHub Copilot CLI) and finish the browser or device-code sign-in. A fine-grained personal access token with the \"Copilot Requests\" permission in COPILOT_GITHUB_TOKEN, GH_TOKEN or GITHUB_TOKEN also works.";

    private const string ZCodeGuidance =
        "Open the ZCode desktop app, click Connect at the bottom left and sign in with Z.ai or BigModel, or add an API key under Settings → Model Providers.";

    private const string GenericGuidance =
        "Sign in with this agent's own app or CLI as its documentation describes. AIMonitor only shows the usage ccusage finds on this machine; it never signs in for you or reads this agent's credentials.";

    private const string FoundStatus = "Agents found by ccusage that have no tab of their own:";

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    private static AgentUsage Agent(string id) => new(id, new TokenCounts(10, 0, 0, 0, 10), []);

    /// <summary>The G6 synthetic report: copilot, zcode and example-agent (plus the excluded four).</summary>
    private static AgentUsageReport ReportWithThreeOthers() => new(
        daily: [new DailyAgentUsage(new DateOnly(2026, 11, 9), [Agent("claude"), Agent("opencode"), Agent("copilot"), Agent("zcode"), Agent("example-agent")])],
        monthly: [],
        sessions: [],
        unpricedModels: [],
        pricing: AgentUsagePricing.OnlineRequested,
        retrievedAt: new DateTimeOffset(2026, 11, 9, 7, 0, 0, TimeSpan.Zero),
        skippedRows: 0);

    private static ConnectionsViewModel CreateViewModel()
    {
        var settings = new AppSettings { ShowConnectionsAtStartup = true };
        var store = new BlockingSettingsStore(settings);
        store.Release();
        return new ConnectionsViewModel(new SettingsSession(store, settings));
    }

    private static T Single<T>(IEnumerable<T> items, Func<T, bool> predicate, string what)
    {
        var matches = items.Where(predicate).ToList();
        Assert.True(matches.Count >= 1, "Not found on the page: " + what);
        return matches[0];
    }

    private static Color ColorOf(Brush? brush)
    {
        var solid = Assert.IsType<SolidColorBrush>(brush);
        return solid.Color;
    }

    private static (Color Heading, Color Guidance) RenderAndAssert(string theme)
    {
        return WpfTestHost.Run(() =>
        {
            ThemeManager.Instance.ApplyTheme(theme);
            try
            {
                var vm = CreateViewModel();
                vm.UpdateOpenCode(new OpenCodeAuthStatus(
                    true,
                    "opencode auth list",
                    [new OpenCodeSignIn("Anthropic", "OAuth"), new OpenCodeSignIn("OpenAI", "API key")],
                    false,
                    ""));
                vm.UpdateOtherAgents(ReportWithThreeOthers(), null);

                var page = new ConnectionsPage { DataContext = vm };
                var window = new Window { Content = page, Width = 800, Height = 900 };
                using var _ = WpfTestHost.ShowOffscreen(window);
                WpfTestHost.Realize(page);

                var texts = Descendants<TextBlock>(page).ToList();

                // F8: the fourth card.
                Assert.Contains(texts, t => t.Text == "OpenCode");
                Assert.Contains(texts, t => t.Text == "Signed in: Anthropic (OAuth), OpenAI (API key)");
                Assert.Contains(texts, t => t.Text == "Detected from opencode auth list");
                Assert.Contains(texts, t => t.Text == "Connected");
                Assert.Contains(texts, t => t.Text == "✓");
                var cardButtons = Descendants<Button>(page).Select(b => b.Content as string).ToList();
                Assert.Equal(4, cardButtons.Count(c => c == "Re-detect"));
                Assert.Contains("Sign-in help", cardButtons);

                // G5: heading, status, one row per agent.
                var heading = Single(texts, t => t.Text == "Other agents", "heading 'Other agents'");
                Assert.Equal(14.0, heading.FontSize);
                Assert.Equal(FontWeights.SemiBold, heading.FontWeight);

                var status = Single(texts, t => t.Text == FoundStatus, "status text");
                Assert.Equal(12.0, status.FontSize);

                var expected = new (string Name, string Guidance)[]
                {
                    ("Copilot", CopilotGuidance),
                    ("example-agent", GenericGuidance),
                    ("ZCode", ZCodeGuidance),
                };

                Color guidanceColour = default;
                foreach (var (name, guidance) in expected)
                {
                    var nameBlock = Single(texts, t => t.Text == name, "row name " + name);
                    Assert.Equal(13.0, nameBlock.FontSize);
                    Assert.Equal(FontWeights.SemiBold, nameBlock.FontWeight);

                    var guidanceBlock = Single(texts, t => t.Text == guidance, "guidance of " + name);
                    Assert.Equal(12.0, guidanceBlock.FontSize);
                    Assert.Equal(TextWrapping.Wrap, guidanceBlock.TextWrapping);
                    guidanceColour = ColorOf(guidanceBlock.Foreground);

                    // One bordered row holds both the name and its guidance.
                    var border = AncestorBorderContaining(nameBlock, guidanceBlock);
                    Assert.True(border.BorderThickness.Left > 0 || border.BorderThickness.Top > 0, "row border has no thickness");
                }

                // Rows come after the cards, status after the heading (visual order top to bottom).
                var lastCard = Single(texts, t => t.Text == "Signed in: Anthropic (OAuth), OpenAI (API key)", "fourth card");
                double Top(UIElement e) => e.TranslatePoint(new Point(0, 0), page).Y;
                Assert.True(Top(heading) > Top(lastCard), "'Other agents' must sit below the cards");
                Assert.True(Top(status) > Top(heading), "status must sit below the heading");
                Assert.True(
                    Top(Single(texts, t => t.Text == "Copilot", "Copilot row")) > Top(status),
                    "rows must sit below the status text");

                // G5: no buttons in the new section: 2 per card x 4 cards + the 2 footer buttons.
                Assert.Equal((4 * 2) + 2, Descendants<Button>(page).Count());

                // Inside the existing ScrollViewer (the section scrolls with the cards).
                var scroll = Descendants<ScrollViewer>(page).First();
                Assert.True(IsDescendantOf(heading, scroll), "heading must be inside the ScrollViewer");

                return (ColorOf(heading.Foreground), guidanceColour);
            }
            finally
            {
                ThemeManager.Instance.ApplyTheme("dark"); // leave the host as WpfTestHost initialised it
            }
        });
    }

    private static Border AncestorBorderContaining(FrameworkElement first, FrameworkElement second)
    {
        DependencyObject? current = first;
        while (current is not null)
        {
            current = VisualTreeHelper.GetParent(current);
            if (current is Border border && IsDescendantOf(second, border))
            {
                return border;
            }
        }

        throw new Xunit.Sdk.XunitException($"No border contains both '{(first as TextBlock)?.Text}' and its guidance.");
    }

    private static bool IsDescendantOf(DependencyObject element, DependencyObject ancestor)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    [Fact]
    public void Page_RendersTheFourthCardAndTheOtherAgentsSection_InDarkTheme_F8_G5()
    {
        RenderAndAssert("dark");
    }

    [Fact]
    public void Page_RendersTheFourthCardAndTheOtherAgentsSection_InLightTheme_F8_G5()
    {
        RenderAndAssert("light");
    }

    [Fact]
    public void OtherAgentsTextUsesThemeBrushes_SoTheColoursDifferBetweenThemes_G5()
    {
        // "Uses existing brushes so both themes work": a hard-coded colour would be identical in both themes.
        var dark = RenderAndAssert("dark");
        var light = RenderAndAssert("light");

        Assert.NotEqual(dark.Heading, light.Heading);
        Assert.NotEqual(dark.Guidance, light.Guidance);
    }

    [Fact]
    public void WhenNoOtherAgents_ThePageShowsTheHeadingAndTheNoneStatus_AndNoRows_G3_G5()
    {
        WpfTestHost.Run(() =>
        {
            var vm = CreateViewModel();
            vm.UpdateOtherAgents(null, new AgentUsageUnavailableException(AgentUsageErrorKind.NotInstalled, "SENTINEL"));

            var page = new ConnectionsPage { DataContext = vm };
            var window = new Window { Content = page, Width = 800, Height = 900 };
            using var _ = WpfTestHost.ShowOffscreen(window);
            WpfTestHost.Realize(page);

            var texts = Descendants<TextBlock>(page).Select(t => t.Text).ToList();
            Assert.Contains("Other agents", texts);
            Assert.Contains("ccusage is not available, so other agents cannot be listed.", texts);
            Assert.DoesNotContain(texts, t => t.Contains("SENTINEL", StringComparison.Ordinal));
            Assert.DoesNotContain(CopilotGuidance, texts);
            Assert.Equal((4 * 2) + 2, Descendants<Button>(page).Count());
        });
    }
}
