using AIMonitor.Infrastructure.MacOS;

namespace AIMonitor.Infrastructure.Tests.MacOS;

/// <summary>
/// Only the shape of each <c>/usr/bin/security</c> invocation is under test here, so these run on
/// any host. The first test is the one that matters most: it fails the moment a secret moves from
/// stdin into the command line.
/// </summary>
public sealed class MacOSKeychainTests
{
    private const string Secret = "sk-not-a-real-credential";

    [Fact]
    public void BuildBatchStartInfo_PassesOnlyTheBatchSwitch_SoNoSecretCanReachArgv()
    {
        var startInfo = MacOSKeychain.BuildBatchStartInfo();

        Assert.Equal(MacOSKeychain.ExecutablePath, startInfo.FileName);
        Assert.Equal(["-i"], startInfo.ArgumentList);
        Assert.True(startInfo.RedirectStandardInput);
        Assert.False(startInfo.UseShellExecute);
    }

    [Fact]
    public void BuildAddCommand_CarriesTheSecretInTheCommandText_NotInAnyArgument()
    {
        var command = MacOSKeychain.BuildAddCommand("svc", "acct", Secret);
        var startInfo = MacOSKeychain.BuildBatchStartInfo();

        Assert.Contains(Secret, command, StringComparison.Ordinal);
        Assert.DoesNotContain(startInfo.ArgumentList, argument => argument.Contains(Secret, StringComparison.Ordinal));
    }

    [Fact]
    public void BuildAddCommand_UpdatesInPlaceAndLabelsTheItem()
    {
        var command = MacOSKeychain.BuildAddCommand("svc", "acct", Secret);

        Assert.StartsWith("add-generic-password -U ", command, StringComparison.Ordinal);
        Assert.Contains($"-l \"{MacOSKeychain.ItemLabel}\"", command, StringComparison.Ordinal);
        Assert.Contains("-a \"acct\"", command, StringComparison.Ordinal);
        Assert.Contains("-s \"svc\"", command, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("plain", "\"plain\"")]
    [InlineData("with space", "\"with space\"")]
    [InlineData("quote\"inside", "\"quote\\\"inside\"")]
    [InlineData("back\\slash", "\"back\\\\slash\"")]
    [InlineData("both\"and\\", "\"both\\\"and\\\\\"")]
    public void Quote_EscapesWhatTheBatchParserWouldOtherwiseEat(string value, string expected)
    {
        Assert.Equal(expected, MacOSKeychain.Quote(value));
    }

    [Fact]
    public void BuildReadStartInfo_AsksForTheValue()
    {
        var startInfo = MacOSKeychain.BuildReadStartInfo("svc", "acct");

        Assert.Equal(["find-generic-password", "-a", "acct", "-s", "svc", "-w"], startInfo.ArgumentList);
    }

    [Fact]
    public void BuildProbeStartInfo_OmitsDashW_SoItRaisesNoAccessPrompt()
    {
        var startInfo = MacOSKeychain.BuildProbeStartInfo("svc", "acct");

        Assert.Equal(["find-generic-password", "-a", "acct", "-s", "svc"], startInfo.ArgumentList);
        Assert.DoesNotContain("-w", startInfo.ArgumentList);
    }

    [Fact]
    public void BuildDeleteCommand_TargetsOneItem()
    {
        Assert.Equal("delete-generic-password -a \"acct\" -s \"svc\"",
            MacOSKeychain.BuildDeleteCommand("svc", "acct"));
    }
}
