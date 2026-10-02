using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using AIMonitor.Application.Settings;
using AIMonitor.Application.Windows;
using AIMonitor.Presentation.Wpf;
using AIMonitor.Presentation.Wpf.ViewModels;
using AIMonitor.TestSupport;
using Xunit;

namespace AIMonitor.Presentation.Wpf.Tests;

[Collection("Wpf")]
public sealed class SettingsDialogTests
{
    [Fact]
    public void SettingsDialog_WhenClosedWithoutSaving_RevertsPreviewToEntrySettingsViaClosing()
    {
        WpfTestHost.Run(() =>
        {
            var initial = new AppSettings
            {
                Theme = "light",
                WidgetOpacity = 0.9,
                WidgetAlwaysOnTop = false,
                DashboardWidth = 1120,
                DashboardHeight = 820
            };

            var store = new FakeSettingsStore(initial);
            var registrar = new FakeStartupRegistrar();
            using var session = new SettingsSession(store, initial);

            var recordedCallbacks = new List<AppSettings>();
            var vm = new SettingsViewModel(session, registrar, s => recordedCallbacks.Add(s));

            var dialog = new SettingsDialog(vm);
            using var offscreen = WpfTestHost.ShowOffscreen(dialog);

            // Change theme to "dark" and opacity to 0.5 through the view model
            vm.Theme = "dark";
            vm.WidgetOpacity = 0.5;

            Assert.True(recordedCallbacks.Count >= 2);
            Assert.Equal("dark", recordedCallbacks[^1].Theme);
            Assert.Equal(0.5, recordedCallbacks[^1].WidgetOpacity);

            // Close the dialog with Close() WITHOUT saving
            dialog.Close();

            // The LAST recorded callback has the entry theme/opacity (revert happened via Closing)
            Assert.NotEmpty(recordedCallbacks);
            var last = recordedCallbacks[^1];
            Assert.Equal("light", last.Theme);
            Assert.Equal(0.9, last.WidgetOpacity);
        });
    }

    [Fact]
    public async Task SettingsDialog_WhenSavedThenClosed_DoesNotRevertPreviewAfterSave()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var initial = new AppSettings
            {
                Theme = "light",
                WidgetOpacity = 0.9,
                WidgetAlwaysOnTop = false,
                DashboardWidth = 1120,
                DashboardHeight = 820
            };

            var store = new FakeSettingsStore(initial);
            var registrar = new FakeStartupRegistrar();
            using var session = new SettingsSession(store, initial);

            var recordedCallbacks = new List<AppSettings>();
            var vm = new SettingsViewModel(session, registrar, s => recordedCallbacks.Add(s));

            var dialog = new SettingsDialog(vm);
            using var offscreen = WpfTestHost.ShowOffscreen(dialog);

            // Change theme to "dark" and opacity to 0.5 through the view model
            vm.Theme = "dark";
            vm.WidgetOpacity = 0.5;

            var countBeforeSave = recordedCallbacks.Count;
            Assert.Equal("dark", recordedCallbacks[^1].Theme);
            Assert.Equal(0.5, recordedCallbacks[^1].WidgetOpacity);

            // after await vm.SaveAsync() then Close()
            await vm.SaveAsync();

            dialog.Close();

            // no revert callback after the save
            Assert.Equal(countBeforeSave, recordedCallbacks.Count);
            Assert.Equal("dark", recordedCallbacks[^1].Theme);
            Assert.Equal(0.5, recordedCallbacks[^1].WidgetOpacity);
        });
    }

    [Fact]
    public async Task SettingsDialog_WhenClosingWhileSaving_ClosingIsCancelled_AndDialogStaysOpen()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var initial = new AppSettings
            {
                Theme = "light",
                WidgetOpacity = 0.9,
                WidgetAlwaysOnTop = false,
                DashboardWidth = 1120,
                DashboardHeight = 820
            };

            var store = new BlockingSettingsStore(initial);
            var registrar = new FakeStartupRegistrar();
            using var session = new SettingsSession(store, initial);

            var vm = new SettingsViewModel(session, registrar);
            var dialog = new SettingsDialog(vm);
            using var offscreen = WpfTestHost.ShowOffscreen(dialog);

            Assert.True(dialog.IsVisible);

            // Start saving
            var saveTask = vm.SaveAsync();
            await store.SaveStarted.Task;

            Assert.True(vm.IsSaving);

            // Closing while saving must be cancelled (dialog remains open)
            dialog.Close();
            Assert.True(dialog.IsVisible);

            // Release the store and allow Save to complete
            store.Release();
            await saveTask;

            // When save completes, dialog closes via RequestClose(true)
            Assert.False(dialog.IsVisible);
        });
    }

    [Fact]
    public void SettingsDialog_ControlsExist_WindowSizeComboHas4Items_SwatchFollowsOpacity_CancelButtonIsCancelTrue()
    {
        WpfTestHost.Run(() =>
        {
            var initial = new AppSettings
            {
                Theme = "light",
                WidgetOpacity = 0.8,
                DashboardWidth = 1120,
                DashboardHeight = 820
            };

            var store = new FakeSettingsStore(initial);
            var registrar = new FakeStartupRegistrar();
            using var session = new SettingsSession(store, initial);
            var vm = new SettingsViewModel(session, registrar);

            var dialog = new SettingsDialog(vm);
            using var offscreen = WpfTestHost.ShowOffscreen(dialog);

            // (c) controls exist:
            // 1. window size combo has 4 items
            var windowSizeCombo = (dialog.FindName("WindowSizeComboBox") ?? dialog.FindName("DefaultWindowSizeComboBox")) as System.Windows.Controls.ComboBox;
            Assert.NotNull(windowSizeCombo);
            Assert.Equal(4, windowSizeCombo.Items.Count);

            // 2. swatch Opacity follows vm.WidgetOpacity
            var swatch = dialog.FindName("OpacitySwatch") as System.Windows.Controls.Border;
            Assert.NotNull(swatch);
            Assert.Equal(0.8, swatch.Opacity);

            vm.WidgetOpacity = 0.45;
            Assert.Equal(0.45, swatch.Opacity);

            // 3. Cancel button IsCancel true
            var cancelButton = (dialog.FindName("CancelButton") as System.Windows.Controls.Button)
                ?? FindButtonByContent(dialog, "Cancel");
            Assert.NotNull(cancelButton);
            Assert.True(cancelButton.IsCancel);
        });
    }

    [Fact]
    public void SettingsDialog_WindowSizeComboBox_SelectedItem_BindsTwoWayWithSelectedWindowSize()
    {
        WpfTestHost.Run(() =>
        {
            var initial = new AppSettings
            {
                Theme = "light",
                WidgetOpacity = 0.8,
                DashboardWidth = 1120,
                DashboardHeight = 820
            };

            var store = new FakeSettingsStore(initial);
            var registrar = new FakeStartupRegistrar();
            using var session = new SettingsSession(store, initial);
            var vm = new SettingsViewModel(session, registrar);

            var dialog = new SettingsDialog(vm);
            using var offscreen = WpfTestHost.ShowOffscreen(dialog);

            var windowSizeCombo = (dialog.FindName("WindowSizeComboBox") ?? dialog.FindName("DefaultWindowSizeComboBox")) as ComboBox;
            Assert.NotNull(windowSizeCombo);

            // Sets vm.SelectedWindowSize to a non-default option (e.g. Wide), and asserts WindowSizeComboBox.SelectedItem equals it
            vm.SelectedWindowSize = WindowSizeOption.Wide;
            Assert.Equal(WindowSizeOption.Wide, windowSizeCombo.SelectedItem);

            // Then sets ComboBox.SelectedItem to Compact and asserts vm.SelectedWindowSize changed
            windowSizeCombo.SelectedItem = WindowSizeOption.Compact;
            Assert.NotEqual(WindowSizeOption.Wide, vm.SelectedWindowSize);
            Assert.Equal(WindowSizeOption.Compact, vm.SelectedWindowSize);
        });
    }

    [Fact]
    public void SourceContract_OpenSettingsDialog_ConstructsAppearanceApplier_AndPassesApplyCallbackToSettingsViewModel()
    {
        var repoRoot = FindRepoRoot();
        var appXamlCsPath = Path.Combine(repoRoot, "src", "AIMonitor.Presentation.Wpf", "App.xaml.cs");
        Assert.True(File.Exists(appXamlCsPath), $"App.xaml.cs not found at {appXamlCsPath}");

        var source = File.ReadAllText(appXamlCsPath);
        var stripped = StripComments(source);

        // locate "void OpenSettingsDialog(" declaration, not the first occurrence of OpenSettingsDialog
        var methodBody = ExtractMethodBody(stripped, @"void\s+OpenSettingsDialog\s*\(");

        // OpenSettingsDialog constructs AppearanceApplier
        Assert.True(
            Regex.IsMatch(methodBody, @"new\s+AppearanceApplier\s*\("),
            "OpenSettingsDialog method body does not construct AppearanceApplier ('new AppearanceApplier(' was not found).");

        // and constructs DashboardPreviewResizer
        Assert.True(
            Regex.IsMatch(methodBody, @"new\s+DashboardPreviewResizer\s*\("),
            "OpenSettingsDialog method body does not construct DashboardPreviewResizer ('new DashboardPreviewResizer(' was not found).");

        // and passes an Apply callback to SettingsViewModel
        Assert.True(
            Regex.IsMatch(methodBody, @"new\s+SettingsViewModel\s*\([^)]*\bapplier\.Apply\b"),
            "OpenSettingsDialog method body does not pass applier.Apply callback to SettingsViewModel constructor.");
    }

    [Fact]
    public void SourceContract_SettingsDialogClosingHandler_CallsDiscard()
    {
        var repoRoot = FindRepoRoot();
        var settingsDialogCsPath = Path.Combine(repoRoot, "src", "AIMonitor.Presentation.Wpf", "SettingsDialog.xaml.cs");
        Assert.True(File.Exists(settingsDialogCsPath), $"SettingsDialog.xaml.cs not found at {settingsDialogCsPath}");

        var source = File.ReadAllText(settingsDialogCsPath);
        var stripped = StripComments(source);

        // locate the Closing handler declaration in SettingsDialog.xaml.cs
        var methodBody = ExtractMethodBody(stripped, @"void\s+OnClosing\s*\(");

        // SettingsDialog.xaml.cs Closing handler calls Discard
        Assert.True(
            Regex.IsMatch(methodBody, @"\bDiscard\s*\("),
            "SettingsDialog.xaml.cs Closing handler does not call Discard ('Discard(' was not found in OnClosing body).");

        // Also assert Closing event is subscribed to OnClosing
        Assert.True(
            Regex.IsMatch(stripped, @"Closing\s*\+=\s*OnClosing"),
            "SettingsDialog does not subscribe Closing event to OnClosing handler.");
    }

    private static System.Windows.Controls.Button? FindButtonByContent(DependencyObject parent, string content)
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is System.Windows.Controls.Button button && Equals(button.Content, content))
            {
                return button;
            }
            var nested = FindButtonByContent(child, content);
            if (nested != null)
            {
                return nested;
            }
        }
        return null;
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AIMonitor.sln")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }

        current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AIMonitor.sln")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }

        throw new InvalidOperationException($"Could not find repository root containing AIMonitor.sln from {AppContext.BaseDirectory} or {Directory.GetCurrentDirectory()}");
    }

    private static string StripComments(string source)
    {
        return Regex.Replace(
            source,
            @"(@(?:""[^""]*"")+|""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*')|//.*?$|/\*[\s\S]*?\*/",
            match =>
            {
                if (match.Groups[1].Success)
                {
                    return match.Groups[1].Value;
                }
                return string.Empty;
            },
            RegexOptions.Multiline);
    }

    private static string ExtractMethodBody(string sourceWithoutComments, string methodDeclarationPattern)
    {
        var match = Regex.Match(sourceWithoutComments, methodDeclarationPattern);
        if (!match.Success)
        {
            Assert.Fail($"Method declaration matching pattern '{methodDeclarationPattern}' was not found in source.");
        }

        var startIdx = match.Index + match.Length;
        var openBraceIdx = sourceWithoutComments.IndexOf('{', startIdx);
        if (openBraceIdx == -1)
        {
            Assert.Fail($"Opening brace '{{' not found after method declaration matching '{methodDeclarationPattern}'.");
        }

        var depth = 0;
        var closeBraceIdx = -1;
        var inString = false;
        var inVerbatim = false;
        for (var i = openBraceIdx; i < sourceWithoutComments.Length; i++)
        {
            var ch = sourceWithoutComments[i];
            if (inString)
            {
                if (ch == '\\') { i++; continue; }
                if (ch == '"') { inString = false; }
            }
            else if (inVerbatim)
            {
                if (ch == '"')
                {
                    if (i + 1 < sourceWithoutComments.Length && sourceWithoutComments[i + 1] == '"')
                    {
                        i++;
                    }
                    else
                    {
                        inVerbatim = false;
                    }
                }
            }
            else
            {
                if (ch == '@' && i + 1 < sourceWithoutComments.Length && sourceWithoutComments[i + 1] == '"')
                {
                    inVerbatim = true;
                    i++;
                }
                else if (ch == '"')
                {
                    inString = true;
                }
                else if (ch == '{')
                {
                    depth++;
                }
                else if (ch == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        closeBraceIdx = i;
                        break;
                    }
                }
            }
        }

        if (closeBraceIdx == -1)
        {
            Assert.Fail($"Matching closing brace '}}' not found for method body starting at index {openBraceIdx}.");
        }

        return sourceWithoutComments.Substring(openBraceIdx, closeBraceIdx - openBraceIdx + 1);
    }

    private sealed class FakeSettingsStore(AppSettings initial) : ISettingsStore
    {
        public bool Exists => true;
        public AppSettings? SavedSettings { get; private set; }

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(initial);

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            SavedSettings = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeStartupRegistrar : IStartupRegistrar
    {
        public bool IsRegisteredResult { get; set; }
        public bool RegisterCalled { get; private set; }
        public bool UnregisterCalled { get; private set; }

        public bool IsRegistered() => IsRegisteredResult;

        public void Register(string executablePath, string arguments = "")
        {
            RegisterCalled = true;
        }

        public void Unregister()
        {
            UnregisterCalled = true;
        }
    }
}
