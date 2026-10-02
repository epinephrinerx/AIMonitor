using System;
using System.IO;
using System.Text.RegularExpressions;

namespace AIMonitor.Presentation.Wpf.Tests;

public sealed class ConnectionsSourceContractTests
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

    private static string ReadSourceFile(string relativePath)
    {
        var root = FindRepoRoot();
        var fullPath = Path.Combine(root, relativePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Required source file missing: {fullPath}", fullPath);
        }
        return File.ReadAllText(fullPath);
    }

    private static string StripComments(string code)
    {
        var withoutBlock = Regex.Replace(code, @"/\*[\s\S]*?\*/", "");
        var withoutLine = Regex.Replace(withoutBlock, @"//.*", "");
        var withoutXml = Regex.Replace(withoutLine, @"<!--[\s\S]*?-->", "");
        return withoutXml;
    }

    [Fact]
    public void App_WiresRequestConnect_ToOpenConnectDialog()
    {
        var rawCode = ReadSourceFile("src/AIMonitor.Presentation.Wpf/App.xaml.cs");
        var code = StripComments(rawCode);

        // App.xaml.cs must wire RequestConnect to OpenConnectDialog
        Assert.Matches(@"_mainViewModel\.RequestConnect\s*\+=\s*OpenConnectDialog", code);
    }

    [Fact]
    public void App_OpenConnectDialog_AwaitsRefreshAsync_OnlyAfterShowDialogReturnsTrue()
    {
        var rawCode = ReadSourceFile("src/AIMonitor.Presentation.Wpf/App.xaml.cs");
        var code = StripComments(rawCode);

        // Scope narrowly to OpenConnectDialog method body via brace matching
        const string methodSignature = "void OpenConnectDialog(";
        var methodIndex = code.IndexOf(methodSignature, StringComparison.Ordinal);
        Assert.True(methodIndex >= 0, "Method declaration 'void OpenConnectDialog(' could not be found in App.xaml.cs");

        var openBraceIndex = code.IndexOf('{', methodIndex);
        Assert.True(openBraceIndex >= 0, "Opening brace for OpenConnectDialog could not be found in App.xaml.cs");

        var braceDepth = 0;
        var closeBraceIndex = -1;
        for (var i = openBraceIndex; i < code.Length; i++)
        {
            if (code[i] == '{')
            {
                braceDepth++;
            }
            else if (code[i] == '}')
            {
                braceDepth--;
                if (braceDepth == 0)
                {
                    closeBraceIndex = i;
                    break;
                }
            }
        }

        Assert.True(closeBraceIndex > openBraceIndex, "Matching closing brace for OpenConnectDialog was not found in App.xaml.cs via brace matching.");

        var methodBody = code.Substring(openBraceIndex + 1, closeBraceIndex - openBraceIndex - 1);

        // Assert RefreshAsync( occurs EXACTLY ONCE in it
        var refreshMatches = Regex.Matches(methodBody, @"RefreshAsync\s*\(");
        Assert.Single(refreshMatches);

        // Assert RefreshAsync occurs after ShowDialog(
        var showDialogIndex = methodBody.IndexOf("ShowDialog(", StringComparison.Ordinal);
        Assert.True(showDialogIndex >= 0, "ShowDialog( was not found in OpenConnectDialog method body.");
        Assert.True(refreshMatches[0].Index > showDialogIndex, "RefreshAsync( must occur after ShowDialog(.");

        // Assert RefreshAsync occurs inside an `if (result == true)` block (not before ShowDialog and not unconditional)
        Assert.Matches(@"var\s+(?<res>\w+)\s*=\s*\w*\.ShowDialog\s*\([^)]*\)\s*;\s*if\s*\(\s*\k<res>\s*==\s*true\s*\)\s*\{[^}]*RefreshAsync\s*\(", methodBody);
    }

    [Fact]
    public void ConnectDialogCodeBehind_ContainsNoSaveAsync_AndNoConnectionStore()
    {
        var rawCode = ReadSourceFile("src/AIMonitor.Presentation.Wpf/ConnectDialog.xaml.cs");
        var code = StripComments(rawCode);

        Assert.DoesNotContain("SaveAsync", code);
        Assert.DoesNotContain("ConnectionStore", code);
    }

    [Fact]
    public void MainWindowXaml_BindsShowConnectionsCommand_AndIsConnectionsPageVisible()
    {
        var rawXaml = ReadSourceFile("src/AIMonitor.Presentation.Wpf/MainWindow.xaml");
        var xaml = StripComments(rawXaml);

        // Header button must bind ShowConnectionsCommand
        Assert.Matches(@"<Button\s+[^>]*(?:Content=""Connections""[^>]*Command=""\{\s*Binding\s+ShowConnectionsCommand\s*\}""|Command=""\{\s*Binding\s+ShowConnectionsCommand\s*\}""[^>]*Content=""Connections"")", xaml);

        // Provider navigation and content areas must react to IsConnectionsPageVisible
        Assert.Contains("Binding=\"{Binding IsConnectionsPageVisible}\"", xaml);

        // ConnectionsPage must be declared with Connections DataContext and respond to IsConnectionsPageVisible
        Assert.Matches(@"<local:ConnectionsPage\s+[^>]*DataContext=""\{\s*Binding\s+Connections\s*\}""", xaml);
    }
}
