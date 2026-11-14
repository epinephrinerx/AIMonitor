using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AIMonitor.Application.Providers;
using AIMonitor.Application.Settings;
using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.ViewModels;
using AIMonitor.TestSupport;

namespace AIMonitor.Presentation.Wpf.Tests;

/// <summary>
/// R-020 fix round 1, H13 (Docs/SPEC_R020_R021.md): for the OpenCode card the connect dialog never shows the
/// existing-sign-in section ("Found on this machine", "Found ..." / "No existing sign-in found"); it shows the
/// title, the SetupHint and Close only. <c>ConnectDialogViewModel.ShowsFoundSection</c> is false for opencode and
/// true for claude, openai and agy; the dialog window honours it.
/// </summary>
[Collection("Wpf")]
public sealed class ConnectDialogOpenCodeTests
{
    private static ProviderConnectionStore CreateStore()
    {
        var settings = new AppSettings();
        var settingsStore = new BlockingSettingsStore(settings);
        settingsStore.Release();
        return new ProviderConnectionStore(new TestSecretStore(new Dictionary<string, string>()), new SettingsSession(settingsStore, settings));
    }

    private static ConnectDialogViewModel Dialog(ProviderMeta meta, DetectionInfo? detection) =>
        new(meta, detection, new ProviderConnection(null, ""), CreateStore());

    [Fact]
    public void ShowsFoundSection_IsFalseForOpenCode_WhateverTheDetection_H13()
    {
        Assert.False(Dialog(ProviderMeta.OpenCode, null).ShowsFoundSection);
        Assert.False(Dialog(ProviderMeta.OpenCode, new DetectionInfo("opencode", DetectionState.NotConnected)).ShowsFoundSection);
        Assert.False(Dialog(ProviderMeta.OpenCode, new DetectionInfo("opencode", DetectionState.Connected, "auth.json", "auth.json", "x")).ShowsFoundSection);
    }

    [Fact]
    public void ShowsFoundSection_IsTrueForClaudeOpenAiAndAgy_H13()
    {
        Assert.True(Dialog(ProviderMeta.Claude, new DetectionInfo("claude", DetectionState.Connected)).ShowsFoundSection);
        Assert.True(Dialog(ProviderMeta.OpenAi, new DetectionInfo("openai", DetectionState.NotConnected)).ShowsFoundSection);
        Assert.True(Dialog(ProviderMeta.Agy, new DetectionInfo("agy", DetectionState.Connected, "agy_cli", "AGY CLI")).ShowsFoundSection);
        Assert.True(Dialog(ProviderMeta.OpenAi, null).ShowsFoundSection);
    }

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

    private static List<string> VisibleTexts(ConnectDialog dialog) =>
        Descendants<TextBlock>(dialog).Where(t => t.IsVisible).Select(t => t.Text).ToList();

    [Fact]
    public void Dialog_ForOpenCode_ShowsTitleAndSetupHintAndClose_ButNoFoundSection_H13()
    {
        WpfTestHost.Run(() =>
        {
            var vm = Dialog(ProviderMeta.OpenCode, new DetectionInfo("opencode", DetectionState.NotConnected));
            var dialog = new ConnectDialog(vm);
            using var offscreen = WpfTestHost.ShowOffscreen(dialog);
            WpfTestHost.Realize(dialog);

            var visible = VisibleTexts(dialog);
            Assert.DoesNotContain(visible, t => t.Contains("Found on this machine", StringComparison.Ordinal));
            Assert.DoesNotContain(visible, t => t.Contains("No existing sign-in found", StringComparison.Ordinal));
            Assert.DoesNotContain(visible, t => t.StartsWith("Found ", StringComparison.Ordinal));
            var foundLine = (TextBlock?)dialog.FindName("FoundLineText");
            Assert.True(foundLine is null || !foundLine.IsVisible, "the 'found' line must not be visible");

            Assert.Equal("OpenCode sign-in", dialog.Title);
            Assert.Contains(visible, t => t.Contains("opencode auth login", StringComparison.Ordinal)); // the SetupHint
            var buttons = Descendants<Button>(dialog).Where(b => b.IsVisible).Select(b => b.Content as string).ToList();
            Assert.Contains("Close", buttons);
            Assert.DoesNotContain("Save", buttons);
        });
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("openai")]
    [InlineData("agy")]
    public void Dialog_ForTheOtherProviders_StillShowsTheFoundSection_H13(string id)
    {
        WpfTestHost.Run(() =>
        {
            var meta = ProviderMeta.TryGet(id)!;
            var vm = Dialog(meta, new DetectionInfo(id, DetectionState.NotConnected));
            var dialog = new ConnectDialog(vm);
            using var offscreen = WpfTestHost.ShowOffscreen(dialog);
            WpfTestHost.Realize(dialog);

            var visible = VisibleTexts(dialog);
            Assert.Contains(visible, t => t == "Found on this machine");
            Assert.Contains(visible, t => t.Contains("No existing sign-in found", StringComparison.Ordinal));
        });
    }
}
