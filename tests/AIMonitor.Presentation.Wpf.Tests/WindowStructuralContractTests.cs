using System.IO;
using System.Text.RegularExpressions;

namespace AIMonitor.Presentation.Wpf.Tests;

public sealed class WindowStructuralContractTests
{
    [Fact]
    public void MainWindowAndWidgetWindow_AdhereToCentralizedPlacementContract()
    {
        var solutionRoot = FindSolutionRoot();
        var presentationDir = Path.Combine(solutionRoot, "src", "AIMonitor.Presentation.Wpf");

        var targetFiles = new[]
        {
            Path.Combine(presentationDir, "MainWindow.xaml.cs"),
            Path.Combine(presentationDir, "WidgetWindow.xaml.cs")
        };

        var inspectedCount = 0;
        foreach (var file in targetFiles)
        {
            Assert.True(File.Exists(file), $"Expected window code-behind file does not exist: {file}");
            var content = File.ReadAllText(file);

            // (2) Each file must contain WindowPlacementRecorder.RecordAsync
            Assert.Contains("WindowPlacementRecorder.RecordAsync", content, StringComparison.Ordinal);

            // (3) Neither file contains .UpdateAsync(, ISettingsStore, or a .SaveAsync( call on a store
            Assert.DoesNotContain(".UpdateAsync(", content, StringComparison.Ordinal);
            Assert.DoesNotContain("ISettingsStore", content, StringComparison.Ordinal);
            Assert.DoesNotContain(".SaveAsync(", content, StringComparison.Ordinal);

            // (4) Neither declares a FIELD of type AppSettings (match only member declarations with an access modifier)
            var appSettingsFieldMatch = Regex.Match(
                content,
                @"(private|internal|protected|public)\s+(readonly\s+|volatile\s+)?AppSettings\??\s+[_a-zA-Z]",
                RegexOptions.Compiled);
            Assert.False(
                appSettingsFieldMatch.Success,
                $"File {Path.GetFileName(file)} must not declare a field of type AppSettings, but found: '{appSettingsFieldMatch.Value}'");

            inspectedCount++;
        }

        // (1) Assert at least 2 files were inspected
        Assert.True(inspectedCount >= 2, $"Expected at least 2 files inspected, but inspected {inspectedCount}");
    }

    [Fact]
    public void MainWindowAndApp_AdhereToTrayPolicyAndStartupApplyContract()
    {
        var solutionRoot = FindSolutionRoot();
        var presentationDir = Path.Combine(solutionRoot, "src", "AIMonitor.Presentation.Wpf");

        var mainWindowFile = Path.Combine(presentationDir, "MainWindow.xaml.cs");
        var appFile = Path.Combine(presentationDir, "App.xaml.cs");

        Assert.True(File.Exists(mainWindowFile), $"Expected file does not exist: {mainWindowFile}");
        Assert.True(File.Exists(appFile), $"Expected file does not exist: {appFile}");

        // 1. MainWindow.xaml.cs must route hide-on-close decisions through TrayPolicy.ShouldHideOnClose,
        // and within the extracted OnClosing body, ShouldHideOnClose must be followed by e.Cancel = true and Hide() in that order (T9).
        var mainWindowContent = File.ReadAllText(mainWindowFile);
        var cleanMainWindowContent = StripComments(mainWindowContent);

        const string onClosingSignature = "void OnClosing(";
        var onClosingIndex = cleanMainWindowContent.IndexOf(onClosingSignature, StringComparison.Ordinal);
        Assert.True(onClosingIndex >= 0, "Method declaration 'void OnClosing(' was not found in MainWindow.xaml.cs");

        var openBraceIndex = cleanMainWindowContent.IndexOf('{', onClosingIndex);
        Assert.True(openBraceIndex >= 0, "Opening brace for OnClosing was not found in MainWindow.xaml.cs");

        var braceDepth = 0;
        var closeBraceIndex = -1;
        for (var i = openBraceIndex; i < cleanMainWindowContent.Length; i++)
        {
            if (cleanMainWindowContent[i] == '{')
            {
                braceDepth++;
            }
            else if (cleanMainWindowContent[i] == '}')
            {
                braceDepth--;
                if (braceDepth == 0)
                {
                    closeBraceIndex = i;
                    break;
                }
            }
        }

        Assert.True(closeBraceIndex > openBraceIndex, "Matching closing brace for OnClosing was not found in MainWindow.xaml.cs via brace matching");

        var onClosingBody = cleanMainWindowContent.Substring(openBraceIndex + 1, closeBraceIndex - openBraceIndex - 1);

        var shouldHideIndex = onClosingBody.IndexOf("ShouldHideOnClose", StringComparison.Ordinal);
        Assert.True(shouldHideIndex >= 0, "OnClosing body must contain 'ShouldHideOnClose'.");

        var cancelIndex = onClosingBody.IndexOf("e.Cancel = true", shouldHideIndex, StringComparison.Ordinal);
        Assert.True(cancelIndex >= 0, "OnClosing body must contain 'e.Cancel = true' following 'ShouldHideOnClose'.");

        var hideIndex = onClosingBody.IndexOf("Hide()", cancelIndex, StringComparison.Ordinal);
        Assert.True(hideIndex >= 0, "OnClosing body must contain 'Hide()' following 'e.Cancel = true'.");

        // 2. App.xaml.cs must route startup hidden decisions through TrayPolicy, and invoke startup settings apply
        var appContent = File.ReadAllText(appFile);
        var cleanAppContent = StripComments(appContent);
        Assert.Contains("TrayPolicy.ShouldStartHidden", cleanAppContent, StringComparison.Ordinal);
        Assert.Contains("_liveSettingsApplier.Apply(_settingsSession.Current)", cleanAppContent, StringComparison.Ordinal);
    }

    private static string StripComments(string code)
    {
        return Regex.Replace(code, @"/\*[\s\S]*?\*/|//.*", string.Empty);
    }

    private static string FindSolutionRoot()
    {
        var testAssemblyLocation = typeof(WindowStructuralContractTests).Assembly.Location;
        var startDir = !string.IsNullOrEmpty(testAssemblyLocation)
            ? Path.GetDirectoryName(testAssemblyLocation)
            : AppContext.BaseDirectory;

        var current = new DirectoryInfo(startDir ?? AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "AIMonitor.sln")))
        {
            current = current.Parent;
        }

        if (current is not null)
        {
            return current.FullName;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate solution root containing 'AIMonitor.sln' by walking up from '{startDir}'.");
    }
}
