namespace AIMonitor.Presentation.Wpf.Tests;

using System;
using System.Collections.Generic;
using AIMonitor.Application.Providers;
using AIMonitor.Application.Settings;
using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf;
using AIMonitor.Presentation.Wpf.ViewModels;
using AIMonitor.TestSupport;
using Xunit;

[Collection("Wpf")]
public sealed class ConnectDialogTests
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

    [Fact]
    public void KeyPasswordBox_PasswordChanged_SyncsWithViewModelKey()
    {
        WpfTestHost.Run(() =>
        {
            var (store, _, _) = CreateConnectionStore();
            var vm = new ConnectDialogViewModel(
                ProviderMeta.OpenAi,
                new DetectionInfo("openai", DetectionState.NotConnected),
                new ProviderConnection(null, ""),
                store);

            var dialog = new ConnectDialog(vm);
            using var offscreen = WpfTestHost.ShowOffscreen(dialog);
            WpfTestHost.Realize(dialog);

            var passwordBox = (System.Windows.Controls.PasswordBox)dialog.FindName("KeyPasswordBox");
            Assert.NotNull(passwordBox);

            // (a) set PasswordBox.Password = "typed" and assert vm.Key == "typed"
            passwordBox.Password = "typed";
            Assert.Equal("typed", vm.Key);
        });
    }

    [Fact]
    public void IsKeyRevealed_TogglesVisibility_AndEditingTextBoxSyncsWithViewModelKey()
    {
        WpfTestHost.Run(() =>
        {
            var (store, _, _) = CreateConnectionStore();
            var vm = new ConnectDialogViewModel(
                ProviderMeta.OpenAi,
                new DetectionInfo("openai", DetectionState.NotConnected),
                new ProviderConnection(null, ""),
                store);

            var dialog = new ConnectDialog(vm);
            using var offscreen = WpfTestHost.ShowOffscreen(dialog);
            WpfTestHost.Realize(dialog);

            var passwordBox = (System.Windows.Controls.PasswordBox)dialog.FindName("KeyPasswordBox");
            var textBox = (System.Windows.Controls.TextBox)dialog.FindName("KeyTextBox");
            Assert.NotNull(passwordBox);
            Assert.NotNull(textBox);

            passwordBox.Password = "typed";
            Assert.Equal("typed", vm.Key);

            // (b) set IsKeyRevealed = true (Show), assert the TextBox is visible, PasswordBox hidden
            // and the TextBox shows the same typed value; edit the TextBox and assert vm.Key updated;
            // toggling back keeps the value
            vm.IsKeyRevealed = true;
            WpfTestHost.Realize(dialog);

            Assert.Equal(System.Windows.Visibility.Visible, textBox.Visibility);
            Assert.Equal(System.Windows.Visibility.Collapsed, passwordBox.Visibility);
            Assert.Equal("typed", textBox.Text);

            textBox.Text = "edited";
            Assert.Equal("edited", vm.Key);

            vm.IsKeyRevealed = false;
            WpfTestHost.Realize(dialog);

            Assert.Equal(System.Windows.Visibility.Visible, passwordBox.Visibility);
            Assert.Equal(System.Windows.Visibility.Collapsed, textBox.Visibility);
            Assert.Equal("edited", passwordBox.Password);
            Assert.Equal("edited", vm.Key);
        });
    }

    [Fact]
    public void SetKeyFromBrowse_PutsPathIntoVisibleBoxAndViewModelKey()
    {
        WpfTestHost.Run(() =>
        {
            var (store, _, _) = CreateConnectionStore();
            var vm = new ConnectDialogViewModel(
                ProviderMeta.Gemini,
                null,
                new ProviderConnection(null, ""),
                store);

            var dialog = new ConnectDialog(vm);
            using var offscreen = WpfTestHost.ShowOffscreen(dialog);
            WpfTestHost.Realize(dialog);

            var passwordBox = (System.Windows.Controls.PasswordBox)dialog.FindName("KeyPasswordBox");
            var textBox = (System.Windows.Controls.TextBox)dialog.FindName("KeyTextBox");
            Assert.NotNull(passwordBox);
            Assert.NotNull(textBox);

            // (c) vm.SetKeyFromBrowse("C:\x.json") puts the path into the visible box and into vm.Key
            const string browsePath = @"C:\x.json";
            vm.SetKeyFromBrowse(browsePath);
            WpfTestHost.Realize(dialog);

            Assert.True(vm.IsKeyRevealed);
            Assert.Equal(System.Windows.Visibility.Visible, textBox.Visibility);
            Assert.Equal(System.Windows.Visibility.Collapsed, passwordBox.Visibility);
            Assert.Equal(browsePath, textBox.Text);
            Assert.Equal(browsePath, vm.Key);
        });
    }

    [Fact]
    public void ClearCommand_EmptiesBoxes_AndSetsPlaceholderTextToClearedWhenYouSave()
    {
        WpfTestHost.Run(() =>
        {
            var (store, _, _) = CreateConnectionStore(initialKey: "existing-key");
            var vm = new ConnectDialogViewModel(
                ProviderMeta.OpenAi,
                null,
                new ProviderConnection("existing-key", ""),
                store);

            var dialog = new ConnectDialog(vm);
            using var offscreen = WpfTestHost.ShowOffscreen(dialog);
            WpfTestHost.Realize(dialog);

            var passwordBox = (System.Windows.Controls.PasswordBox)dialog.FindName("KeyPasswordBox");
            var textBox = (System.Windows.Controls.TextBox)dialog.FindName("KeyTextBox");
            var placeholder = (System.Windows.Controls.TextBlock)dialog.FindName("KeyPlaceholderText");
            Assert.NotNull(passwordBox);
            Assert.NotNull(textBox);
            Assert.NotNull(placeholder);

            passwordBox.Password = "typed-secret";
            Assert.Equal("typed-secret", vm.Key);

            // (d) after vm.ClearCommand the boxes are empty and the placeholder text is "(cleared when you save)"
            vm.ClearCommand.Execute(null);
            WpfTestHost.Realize(dialog);

            Assert.Equal(string.Empty, passwordBox.Password);
            Assert.Equal(string.Empty, textBox.Text);
            Assert.Equal(string.Empty, vm.Key);
            Assert.Equal("(cleared when you save)", vm.KeyPlaceholder);
            Assert.Equal("(cleared when you save)", placeholder.Text);
            Assert.Equal(System.Windows.Visibility.Visible, placeholder.Visibility);
        });
    }
}
