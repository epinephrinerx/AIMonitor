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

        // Scope narrowly to OpenConnectDialog method body
        var startIndex = code.IndexOf("void OpenConnectDialog");
        Assert.True(startIndex >= 0, "OpenConnectDialog method could not be found in App.xaml.cs");

        var methodBody = code[startIndex..];
        var nextMethodIndex = methodBody.IndexOf("public void ", 20);
        if (nextMethodIndex < 0) nextMethodIndex = methodBody.IndexOf("private void ", 20);
        if (nextMethodIndex > 0) methodBody = methodBody[..nextMethodIndex];

        // Verify ShowDialog is called
        Assert.Contains("ShowDialog()", methodBody);

        // Verify RefreshAsync is invoked inside an if-statement checking that ShowDialog returned true
        Assert.Matches(@"var\s+(?<res>\w+)\s*=\s*\w+\.ShowDialog\(\)\s*;\s*if\s*\(\s*\k<res>\s*==\s*true\s*\)\s*\{\s*await\s+_mainViewModel\.RefreshAsync\(\)\s*;\s*\}", methodBody);
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
