using System.Text;
using AIMonitor.Infrastructure.MacOS;

namespace AIMonitor.Infrastructure.Tests.MacOS;

public sealed class LaunchAgentPlistTests
{
    [Fact]
    public void DefaultLabel_MatchesTheOneThe1xMacPortUsed_SoTheUpgradeClaimsOneLoginItem()
    {
        Assert.Equal("net.apichart.aiusagemonitor", LaunchAgentPlist.DefaultLabel);
    }

    [Fact]
    public void Build_ProducesAPropertyListApplesParserAccepts()
    {
        var xml = LaunchAgentPlist.Build("com.example.app", "/Applications/App.app/Contents/MacOS/App");

        // The encoding name is compared without case: XML declares it case-insensitively and .NET
        // writes "utf-8" where Apple's own files say "UTF-8".
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", xml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\"", xml, StringComparison.Ordinal);
        Assert.Contains("<plist version=\"1.0\">", xml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData("", 1)]
    [InlineData("   ", 1)]
    [InlineData("--tray", 2)]
    [InlineData("--minimized --tray", 3)]
    [InlineData("  --minimized   --tray  ", 3)]
    public void Build_CountsTheExecutableAsTheFirstProgramArgument(string? arguments, int expected)
    {
        var xml = LaunchAgentPlist.Build("com.example.app", "/bin/app", arguments);

        var programArguments = LaunchAgentPlist.ReadProgramArguments(xml);
        Assert.NotNull(programArguments);
        Assert.Equal(expected, programArguments.Length);
    }

    [Fact]
    public void ReadProgramArguments_RoundTripsWhatBuildWrote()
    {
        var xml = LaunchAgentPlist.Build("com.example.app", "/bin/app", "--tray");

        var programArguments = LaunchAgentPlist.ReadProgramArguments(xml);
        Assert.NotNull(programArguments);
        Assert.Equal(["/bin/app", "--tray"], programArguments);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not xml")]
    [InlineData("<plist version=\"1.0\"><dict></dict></plist>")]
    [InlineData("<plist version=\"1.0\"><dict><key>Label</key><string>x</string></dict></plist>")]
    public void ReadProgramArguments_WhenItIsNotAPlistWeWrote_IsNull(string xml)
    {
        Assert.Null(LaunchAgentPlist.ReadProgramArguments(xml));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("disabled services = {\n}\n", false)]
    [InlineData("disabled services = {\n\t\"other\" => true\n}\n", false)]
    [InlineData("disabled services = {\n\t\"net.apichart.aiusagemonitor\" => false\n}\n", false)]
    [InlineData("disabled services = {\n\t\"net.apichart.aiusagemonitor\" => true\n}\n", true)]
    [InlineData("disabled services = {\r\n\t\"net.apichart.aiusagemonitor\" => true\r\n}\r\n", true)]
    public void IsDisabledByUser_ReadsOnlyOurOwnLine(string? output, bool expected)
    {
        Assert.Equal(expected, LaunchAgentPlist.IsDisabledByUser(output, LaunchAgentPlist.DefaultLabel));
    }

    [Fact]
    public void IsDisabledByUser_DoesNotMatchALabelThatMerelyContainsOurs()
    {
        var output = "disabled services = {\n\t\"net.apichart.aiusagemonitor.helper\" => true\n}\n";

        Assert.False(LaunchAgentPlist.IsDisabledByUser(output, LaunchAgentPlist.DefaultLabel));
    }

    [MacOSOnlyFact("plutil is the macOS property-list parser")]
    public void Build_IsAcceptedByMacOSOwnPropertyListParser()
    {
        // XDocument reading it back only proves it is XML. launchd will not load a plist its own
        // parser rejects, so the check that counts is plutil's.
        var path = Path.Combine(Path.GetTempPath(), $"aim-plist-{Guid.NewGuid():N}.plist");
        File.WriteAllText(
            path,
            LaunchAgentPlist.Build(LaunchAgentPlist.DefaultLabel, "/bin/app", "--tray"),
            new UTF8Encoding(false));

        try
        {
            var startInfo = MacOSCommand.StartInfo("/usr/bin/plutil", "-lint", path);
            var result = MacOSCommand.Run(startInfo, null);

            Assert.True(
                result.Succeeded,
                $"plutil rejected the plist: {result.StandardOutput}{result.StandardError}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DirectoryFor_IsTheUsersOwnLaunchAgentsFolder()
    {
        Assert.Equal(
            Path.Combine("/Users/someone", "Library", "LaunchAgents"),
            LaunchAgentPlist.DirectoryFor("/Users/someone"));
    }
}
