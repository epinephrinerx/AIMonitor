using System.IO;
using System.Text.RegularExpressions;

namespace AIMonitor.Presentation.Wpf.Tests;

public sealed class WindowStructuralContractTests
{
    [Fact]
    public void MainWindowAndWidgetWindow_DoNotReferenceSettingsStoreOrHoldAppSettingsField()
    {
        var solutionRoot = FindSolutionRoot();
        var presentationDir = Path.Combine(solutionRoot, "src", "AIMonitor.Presentation.Wpf");

        var targetFiles = new[]
        {
            Path.Combine(presentationDir, "MainWindow.xaml.cs"),
            Path.Combine(presentationDir, "WidgetWindow.xaml.cs")
        };

        Assert.True(targetFiles.Length >= 2, "Contract test requires inspecting at least 2 window code-behind files.");

        var inspectedCount = 0;
        foreach (var file in targetFiles)
        {
            Assert.True(File.Exists(file), $"Expected window code-behind file does not exist: {file}");
            var content = File.ReadAllText(file);

            // 1. Must not reference ISettingsStore
            Assert.DoesNotContain("ISettingsStore", content, StringComparison.Ordinal);

            // 2. Must not call SaveAsync on a store
            Assert.DoesNotContain(".SaveAsync(", content, StringComparison.Ordinal);

            // 3. Must not declare any AppSettings field (e.g. private AppSettings _currentSettings)
            var appSettingsFieldMatch = Regex.Match(
                content,
                @"(private|internal|protected|public)?\s*(readonly\s+)?AppSettings\??\s+[_a-zA-Z]",
                RegexOptions.Compiled);
            Assert.False(
                appSettingsFieldMatch.Success,
                $"File {Path.GetFileName(file)} must not contain an AppSettings field, but found: '{appSettingsFieldMatch.Value}'");

            inspectedCount++;
        }

        Assert.True(inspectedCount >= 2, $"Expected at least 2 files inspected, but only inspected {inspectedCount}");
    }

    private static string FindSolutionRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "AIMonitor.sln")))
        {
            current = current.Parent;
        }

        if (current is not null)
        {
            return current.FullName;
        }

        current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "AIMonitor.sln")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Could not find solution root containing AIMonitor.sln");
    }
}
