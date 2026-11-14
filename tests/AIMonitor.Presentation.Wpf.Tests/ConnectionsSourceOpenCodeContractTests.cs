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

    /// <summary>
    /// Body of the member whose declaration starts with <paramref name="signature"/>, for BOTH shapes:
    /// a block body (the balanced braces) and an expression body (<c>=&gt; ...;</c>, up to the semicolon at nesting
    /// depth zero). The earlier version looked for the next '{' and so returned the NEXT method for an
    /// expression-bodied member (Inspector B, minor 4). String and char literals are skipped so braces,
    /// parentheses and semicolons inside them are not counted.
    /// </summary>
    internal static string MemberBody(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "Member not found: " + signature);

        // Skip the parameter list: from the first '(' of the signature to its matching ')'.
        var i = code.IndexOf('(', start);
        Assert.True(i >= 0, "No parameter list after: " + signature);
        i = SkipBalanced(code, i, '(', ')') + 1;

        // Between the parameter list and the body there may be whitespace, constraints or nothing.
        while (i < code.Length && code[i] != '{' && !(code[i] == '=' && i + 1 < code.Length && code[i + 1] == '>'))
        {
            Assert.True(code[i] != ';', "Member has no body (abstract/interface/partial declaration): " + signature);
            i++;
        }

        Assert.True(i < code.Length, "No body found after: " + signature);
        if (code[i] == '{')
        {
            var close = SkipBalanced(code, i, '{', '}');
            return code.Substring(i, close - i + 1);
        }

        // Expression body: read to the ';' that ends it (depth zero for (), {}, []).
        var depth = 0;
        var begin = i + 2;
        for (var j = begin; j < code.Length; j++)
        {
            j = SkipLiteral(code, j);
            switch (code[j])
            {
                case '(' or '{' or '[':
                    depth++;
                    break;
                case ')' or '}' or ']':
                    depth--;
                    break;
                case ';' when depth == 0:
                    return code.Substring(begin, j - begin);
            }
        }

        throw new InvalidOperationException("Unterminated expression body after: " + signature);
    }

    /// <summary>Index of the bracket that closes the one at <paramref name="open"/>.</summary>
    private static int SkipBalanced(string code, int open, char opener, char closer)
    {
        var depth = 0;
        for (var i = open; i < code.Length; i++)
        {
            i = SkipLiteral(code, i);
            if (code[i] == opener)
            {
                depth++;
            }
            else if (code[i] == closer && --depth == 0)
            {
                return i;
            }
        }

        throw new InvalidOperationException("Unbalanced '" + opener + "' in source.");
    }

    /// <summary>If a string or char literal starts at <paramref name="i"/>, the index of its last character;
    /// otherwise <paramref name="i"/> (regular, verbatim and plain interpolated literals; no raw strings are used here).</summary>
    private static int SkipLiteral(string code, int i)
    {
        var c = code[i];
        var verbatim = (c == '@' && i + 1 < code.Length && code[i + 1] == '"')
                       || (c == '$' && i + 2 < code.Length && code[i + 1] == '@' && code[i + 2] == '"')
                       || (c == '@' && i + 2 < code.Length && code[i + 1] == '$' && code[i + 2] == '"');
        if (verbatim)
        {
            var q = code.IndexOf('"', i);
            for (var j = q + 1; j < code.Length; j++)
            {
                if (code[j] == '"')
                {
                    if (j + 1 < code.Length && code[j + 1] == '"')
                    {
                        j++;
                        continue;
                    }

                    return j;
                }
            }

            return code.Length - 1;
        }

        if (c == '$' && i + 1 < code.Length && code[i + 1] == '"')
        {
            i++;
            c = '"';
        }

        if (c == '"' || c == '\'')
        {
            for (var j = i + 1; j < code.Length; j++)
            {
                if (code[j] == '\\')
                {
                    j++;
                }
                else if (code[j] == c)
                {
                    return j;
                }
            }

            return code.Length - 1;
        }

        return i;
    }

    [Fact]
    public void MemberBody_HandlesExpressionBodiesAndBlockBodies_AndNeverReturnsTheNextMember()
    {
        const string sample = """
            class C
            {
                public Task First(string? a = null, bool m = false) =>
                    Core(a, m, "x;{y}", SynchronizationContext.Current);

                private async Task Core(string? a)
                {
                    await Other("}");
                }
            }
            """;

        var expression = MemberBody(sample, "public Task First(");
        var block = MemberBody(sample, "private async Task Core(");

        Assert.Contains("Core(a, m,", expression, StringComparison.Ordinal);
        Assert.DoesNotContain("Other", expression, StringComparison.Ordinal);
        Assert.Contains("Other(", block, StringComparison.Ordinal);
        Assert.StartsWith("{", block, StringComparison.Ordinal);
        Assert.EndsWith("}", block, StringComparison.Ordinal);
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

        var body = MemberBody(code, "private async Task RefreshCoreAsync(");
        Assert.True(body.Length > 100, "RefreshCoreAsync body looks empty: the wrong member was inspected.");
        Assert.DoesNotContain("RequestOpenCodeCheck", body, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenCodeAuth", body, StringComparison.Ordinal);

        // RefreshAsync is expression-bodied (`=> RefreshCoreAsync(...)`): the body must be THAT expression, not the
        // next method, and it only forwards to RefreshCoreAsync.
        var publicRefresh = MemberBody(code, "public Task RefreshAsync(");
        Assert.Contains("RefreshCoreAsync", publicRefresh, StringComparison.Ordinal);
        Assert.DoesNotContain("RequestOpenCodeCheck", publicRefresh, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenCodeAuth", publicRefresh, StringComparison.Ordinal);
        Assert.DoesNotContain("async", publicRefresh, StringComparison.Ordinal); // not the next (async) method
    }

    [Fact]
    public void ConnectionsWiring_RedetectAllPairsTheExistingRefreshWithTheCheck_F9()
    {
        var code = ReadSource("src/AIMonitor.Presentation.Wpf/ViewModels/MainWindowViewModel.cs");

        Assert.Matches(@"RedetectRequested\s*\+=", code);
        Assert.Matches(@"RequestOpenCodeCheck\s*\(", code);
    }
}
