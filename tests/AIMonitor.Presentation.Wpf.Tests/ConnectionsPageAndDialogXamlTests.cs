using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AIMonitor.Application.Providers;
using AIMonitor.Application.Settings;
using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.Theme;
using AIMonitor.Presentation.Wpf.ViewModels;
using AIMonitor.TestSupport;

namespace AIMonitor.Presentation.Wpf.Tests;

[Collection("Wpf")]
public sealed class ConnectionsPageAndDialogXamlTests
{

    private static (ProviderConnectionStore Store, TestSecretStore SecretStore, SettingsSession Session) CreateConnectionStore(
        string? initialKey = null, string initialExtra = "")
    {
        var secretMap = new Dictionary<string, string>();
        if (initialKey is not null)
        {
            secretMap["providers/openai/key"] = initialKey;
        }

        var secretStore = new TestSecretStore(secretMap);
        var initialSettings = new AppSettings
        {
            Providers = new Dictionary<string, ProviderPreference>
            {
                ["openai"] = new(Enabled: true, Extra: initialExtra)
            }
        };

        var settingsStore = new BlockingSettingsStore(initialSettings);
        settingsStore.Release();
        var session = new SettingsSession(settingsStore, initialSettings);
        var store = new ProviderConnectionStore(secretStore, session);

        return (store, secretStore, session);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject depObj) where T : DependencyObject
    {
        if (depObj == null) yield break;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(depObj); i++)
        {
            var child = VisualTreeHelper.GetChild(depObj, i);
            if (child is T t) yield return t;
            foreach (var childOfChild in FindVisualChildren<T>(child))
                yield return childOfChild;
        }
    }

    [Fact]
    public void ConnectionsPage_ConstructsAndRendersThreeCardsWithGlyphAndWordText()
    {
        WpfTestHost.Run(() =>
        {
            var settings = new AppSettings { ShowConnectionsAtStartup = true };
            var store = new BlockingSettingsStore(settings);
            store.Release();
            var session = new SettingsSession(store, settings);
            var vm = new ConnectionsViewModel(session);

            var claudeSnap = new ProviderSnapshot(
                "claude",
                configured: true,
                detection: new DetectionInfo("claude", DetectionState.Connected, "cli", "Claude CLI", "claude-user"));

            var openAiSnap = new ProviderSnapshot(
                "openai",
                configured: true,
                detection: new DetectionInfo("openai", DetectionState.Limited, "admin", "Admin Key", "org-99"));

            var geminiSnap = new ProviderSnapshot(
                "gemini",
                configured: true,
                detection: new DetectionInfo("gemini", DetectionState.NotConnected, "", "", ""));

            vm.Update(new Dictionary<string, ProviderSnapshot>
            {
                ["claude"] = claudeSnap,
                ["openai"] = openAiSnap,
                ["gemini"] = geminiSnap
            });

            var page = new ConnectionsPage { DataContext = vm };
            var window = new Window { Content = page, Width = 800, Height = 600 };
            using var _ = WpfTestHost.ShowOffscreen(window);
            WpfTestHost.Realize(page);

            // Verify title and subtitle text
            var titleBlock = (TextBlock)page.FindName("TitleTextBlock");
            Assert.NotNull(titleBlock);
            Assert.Equal("Your AI services", titleBlock.Text);

            var subtitleBlock = (TextBlock)page.FindName("SubtitleTextBlock");
            Assert.NotNull(subtitleBlock);
            Assert.Contains("Each service is detected the same way", subtitleBlock.Text);

            // Verify footer controls
            var startupCheckBox = (CheckBox)page.FindName("ShowAtStartupCheckBox");
            Assert.NotNull(startupCheckBox);
            Assert.Equal("Show this page at startup", startupCheckBox.Content);
            Assert.True(startupCheckBox.IsChecked);

            var redetectAllBtn = (Button)page.FindName("RedetectAllButton");
            Assert.NotNull(redetectAllBtn);
            Assert.Equal("Re-detect all", redetectAllBtn.Content);

            var openDashboardBtn = (Button)page.FindName("OpenDashboardButton");
            Assert.NotNull(openDashboardBtn);
            Assert.Equal("Open dashboard", openDashboardBtn.Content);

            // Verify card contents rendered in visual tree
            var textBlocks = FindVisualChildren<TextBlock>(page).Select(t => t.Text).ToList();

            // State Glyphs
            Assert.Contains("✓", textBlocks);
            Assert.Contains("!", textBlocks);
            Assert.Contains("–", textBlocks);

            // State Words
            Assert.Contains("Connected", textBlocks);
            Assert.Contains("Limited", textBlocks);
            Assert.Contains("Not connected", textBlocks);

            // Provider display names and badge initials
            Assert.Contains("Claude", textBlocks);
            Assert.Contains("OpenAI / Codex", textBlocks);
            Assert.Contains("Gemini", textBlocks);
            Assert.Contains("C", textBlocks);
            Assert.Contains("O", textBlocks);
            Assert.Contains("G", textBlocks);
        });
    }

    [Fact]
    public void ConnectDialog_ForClaude_HasNoKeyField_CloseButtonPresent_AndNoSaveButton()
    {
        WpfTestHost.Run(() =>
        {
            var (store, _, _) = CreateConnectionStore();
            var vm = new ConnectDialogViewModel(
                ProviderMeta.Claude,
                new DetectionInfo("claude", DetectionState.Connected, "cli", "Claude CLI", "claude-user"),
                new ProviderConnection(null, ""),
                store);

            var dialog = new ConnectDialog(vm) { DataContext = vm };
            using var _ = WpfTestHost.ShowOffscreen(dialog);
            WpfTestHost.Realize(dialog);

            Assert.Equal("Claude sign-in", dialog.Title);

            var credSection = (FrameworkElement)dialog.FindName("CredentialsSection");
            Assert.NotNull(credSection);
            Assert.Equal(Visibility.Collapsed, credSection.Visibility);

            var saveButton = (Button)dialog.FindName("SaveButton");
            Assert.NotNull(saveButton);
            Assert.Equal(Visibility.Collapsed, saveButton.Visibility);

            var cancelButton = (Button)dialog.FindName("CancelButton");
            Assert.NotNull(cancelButton);
            Assert.Equal(Visibility.Collapsed, cancelButton.Visibility);

            var closeButton = (Button)dialog.FindName("CloseButton");
            Assert.NotNull(closeButton);
            Assert.Equal(Visibility.Visible, closeButton.Visibility);
        });
    }

    [Fact]
    public void ConnectDialog_ForOpenAi_HasCredentialsSection_Save_Cancel_Clear_AndExtra_NoBrowse()
    {
        WpfTestHost.Run(() =>
        {
            var (store, _, _) = CreateConnectionStore(initialKey: "sk-admin-test-key", initialExtra: "50");
            var vm = new ConnectDialogViewModel(
                ProviderMeta.OpenAi,
                new DetectionInfo("openai", DetectionState.Connected, "admin", "Admin Key", "org-test"),
                new ProviderConnection("sk-admin-test-key", "50"),
                store);

            var dialog = new ConnectDialog(vm) { DataContext = vm };
            using var _ = WpfTestHost.ShowOffscreen(dialog);
            WpfTestHost.Realize(dialog);

            Assert.Equal("Connect OpenAI / Codex", dialog.Title);

            var credSection = (FrameworkElement)dialog.FindName("CredentialsSection");
            Assert.NotNull(credSection);
            Assert.Equal(Visibility.Visible, credSection.Visibility);

            var saveButton = (Button)dialog.FindName("SaveButton");
            Assert.NotNull(saveButton);
            Assert.Equal(Visibility.Visible, saveButton.Visibility);

            var cancelButton = (Button)dialog.FindName("CancelButton");
            Assert.NotNull(cancelButton);
            Assert.Equal(Visibility.Visible, cancelButton.Visibility);

            var clearButton = (Button)dialog.FindName("ClearButton");
            Assert.NotNull(clearButton);
            Assert.Equal(Visibility.Visible, clearButton.Visibility);

            var extraSection = (FrameworkElement)dialog.FindName("ExtraFieldSection");
            Assert.NotNull(extraSection);
            Assert.Equal(Visibility.Visible, extraSection.Visibility);

            var extraLabel = (TextBlock)dialog.FindName("ExtraLabelText");
            Assert.NotNull(extraLabel);
            Assert.Equal("Monthly budget (USD, optional)", extraLabel.Text);

            var closeButton = (Button)dialog.FindName("CloseButton");
            Assert.NotNull(closeButton);
            Assert.Equal(Visibility.Collapsed, closeButton.Visibility);

            var browseButton = (Button)dialog.FindName("BrowseButton");
            Assert.NotNull(browseButton);
            Assert.Equal(Visibility.Collapsed, browseButton.Visibility);
        });
    }

    [Fact]
    public void ConnectDialog_ForGemini_ShowsBrowseButton_AndSaveButton()
    {
        WpfTestHost.Run(() =>
        {
            var (store, _, _) = CreateConnectionStore();
            var vm = new ConnectDialogViewModel(
                ProviderMeta.Gemini,
                new DetectionInfo("gemini", DetectionState.NotConnected),
                new ProviderConnection(null, ""),
                store);

            var dialog = new ConnectDialog(vm) { DataContext = vm };
            using var _ = WpfTestHost.ShowOffscreen(dialog);
            WpfTestHost.Realize(dialog);

            Assert.Equal("Connect Gemini", dialog.Title);

            var browseButton = (Button)dialog.FindName("BrowseButton");
            Assert.NotNull(browseButton);
            Assert.Equal(Visibility.Visible, browseButton.Visibility);

            var saveButton = (Button)dialog.FindName("SaveButton");
            Assert.NotNull(saveButton);
            Assert.Equal(Visibility.Visible, saveButton.Visibility);

            var credSection = (FrameworkElement)dialog.FindName("CredentialsSection");
            Assert.NotNull(credSection);
            Assert.Equal(Visibility.Visible, credSection.Visibility);
        });
    }
}
