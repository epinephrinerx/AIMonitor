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
