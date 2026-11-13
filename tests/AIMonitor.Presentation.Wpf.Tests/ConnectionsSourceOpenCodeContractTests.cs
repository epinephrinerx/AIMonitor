using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace AIMonitor.Presentation.Wpf.Tests;

/// <summary>
/// R-020 F9/F10 composition rules that live in <c>App.xaml.cs</c> and cannot be reached by a unit test, pinned the
/// way <see cref="ConnectionsSourceContractTests"/> pins the existing wiring (comments stripped, text matched):
/// (a) App calls <c>RequestOpenCodeCheck()</c> exactly once, at startup; (b) the refresh timer, the widget refresh
/// and the other RefreshAsync callers never call it; (c) App hands an OpenCode reader to the view model; and
/// <c>MainWindowViewModel.RefreshCoreAsync</c> (shared by timer, Refresh button, widget and the pending rerun)
/// never mentions it. Behavioural coverage of the same rules is in MainWindowViewModelOpenCodeCheckTests.
/// </summary>
public sealed class ConnectionsSourceOpenCodeContractTests
{
    private static string FindRepoRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "AIMonitor.sln")))
            {
                return current;
            }

            current = Directory.GetParent(current)?.FullName;
        }

        throw new FileNotFoundException("Repository root containing AIMonitor.sln could not be found.");
    }

    private static string ReadSource(string relativePath)
    {
        var path = Path.Combine(FindRepoRoot(), relativePath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Required source file missing: {path}", path);
        }

        var code = File.ReadAllText(path);
        var withoutBlock = Regex.Replace(code, @"/\*[\s\S]*?\*/", "");
        return Regex.Replace(withoutBlock, @"//.*", "");
    }

    private static string MethodBody(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "Method not found: " + signature);
        var open = code.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < code.Length; i++)
        {
            if (code[i] == '{')
            {
                depth++;
            }
            else if (code[i] == '}' && --depth == 0)
            {
                return code.Substring(open, i - open + 1);
            }
        }

        throw new InvalidOperationException("Unbalanced braces after: " + signature);
    }

    [Fact]
    public void App_CallsRequestOpenCodeCheckExactlyOnce_AtStartup_F9()
    {
        var code = ReadSource("src/AIMonitor.Presentation.Wpf/App.xaml.cs");

        var calls = Regex.Matches(code, @"RequestOpenCodeCheck\s*\(");
        Assert.Single(calls);
    }

    [Fact]
    public void App_TimerWidgetAndRefreshButtonPaths_NeverCallRequestOpenCodeCheck_F9()
    {
        var code = ReadSource("src/AIMonitor.Presentation.Wpf/App.xaml.cs");

        var lines = code.Split('\n');
        var callLine = Assert.Single(lines, l => l.Contains("RequestOpenCodeCheck", StringComparison.Ordinal));
        Assert.DoesNotContain("Tick", callLine, StringComparison.Ordinal);
        Assert.DoesNotContain("RequestRefresh", callLine, StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshAsync", callLine, StringComparison.Ordinal);

        // The timer tick and the widget refresh are the existing one-liners; they must stay free of the check.
        foreach (var line in lines.Where(l => l.Contains("_refreshTimer.Tick", StringComparison.Ordinal)
                                              || l.Contains("_widgetViewModel.RequestRefresh", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain("OpenCode", line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void App_PassesAnOpenCodeAuthReaderToTheMainViewModel_F9()
    {
        var code = ReadSource("src/AIMonitor.Presentation.Wpf/App.xaml.cs");

        Assert.Contains("OpenCodeAuthReader", code, StringComparison.Ordinal);
        Assert.Matches(@"new\s+MainWindowViewModel\s*\([^;]*OpenCode|new\s+MainWindowViewModel\s*\([^;]*[Rr]eader", code);
    }

    [Fact]
    public void MainWindowViewModel_RefreshCore_NeverMentionsTheOpenCodeCheck_F9()
    {
        var code = ReadSource("src/AIMonitor.Presentation.Wpf/ViewModels/MainWindowViewModel.cs");

        var body = MethodBody(code, "private async Task RefreshCoreAsync(");
        Assert.DoesNotContain("RequestOpenCodeCheck", body, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenCodeAuth", body, StringComparison.Ordinal);

        var publicRefresh = MethodBody(code, "public Task RefreshAsync(");
        Assert.DoesNotContain("RequestOpenCodeCheck", publicRefresh, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectionsWiring_RedetectAllPairsTheExistingRefreshWithTheCheck_F9()
    {
        var code = ReadSource("src/AIMonitor.Presentation.Wpf/ViewModels/MainWindowViewModel.cs");

        Assert.Matches(@"RedetectRequested\s*\+=", code);
        Assert.Matches(@"RequestOpenCodeCheck\s*\(", code);
    }
}
