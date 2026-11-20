using System.Diagnostics;
using System.Xml.Linq;
using AIMonitor.Infrastructure.MacOS;

namespace AIMonitor.Infrastructure.Tests.MacOS;

/// <summary>
/// The plist is written into a temporary home directory and <c>launchctl</c> is replaced by a
/// recording fake, so these run on any host.
/// </summary>
public sealed class LaunchAgentStartupRegistrarTests : IDisposable
{
    private const string Label = "net.apichart.aiusagemonitor.test";
    private const string Executable = "/Applications/AI Usage Monitor.app/Contents/MacOS/AIUsageMonitor";

    private readonly string _home = Path.Combine(Path.GetTempPath(), "AIMonitorLaunchAgentTests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
    }

    /// <summary>Stands in for <c>/bin/launchctl</c> and remembers every invocation.</summary>
    private sealed class FakeLaunchctl
    {
        public List<IReadOnlyList<string>> Invocations { get; } = [];

        public string DisabledOutput { get; set; } = string.Empty;

        public int ExitCode { get; set; }

        public CommandResult Run(ProcessStartInfo startInfo, string? standardInput)
        {
            Assert.Equal(LaunchAgentPlist.LaunchctlPath, startInfo.FileName);
            Invocations.Add(startInfo.ArgumentList.ToArray());
            return new CommandResult(ExitCode, DisabledOutput, string.Empty);
        }
    }

    private LaunchAgentStartupRegistrar Registrar(FakeLaunchctl launchctl) =>
        new(_home, Label, launchctl.Run, () => "gui/501");

    private string PlistPath => Path.Combine(_home, "Library", "LaunchAgents", $"{Label}.plist");

    [Fact]
    public void Register_WritesThePlistWhereSystemSettingsLooksForIt()
    {
        Registrar(new FakeLaunchctl()).Register(Executable);

        Assert.True(File.Exists(PlistPath));
        var document = XDocument.Load(PlistPath);
        var dictionary = document.Root!.Element("dict")!;
        var values = dictionary.Elements("key").ToDictionary(
            key => key.Value,
            key => key.ElementsAfterSelf().First(),
            StringComparer.Ordinal);

        Assert.Equal(Label, values["Label"].Value);
        Assert.Equal("true", values["RunAtLoad"].Name.LocalName);
        Assert.Equal("Aqua", values["LimitLoadToSessionType"].Value);
        Assert.Equal([Executable], values["ProgramArguments"].Elements("string").Select(element => element.Value));
    }

    [Fact]
    public void Register_PutsEachArgumentInItsOwnEntry_BecauseLaunchdTakesAnArray()
    {
        Registrar(new FakeLaunchctl()).Register(Executable, "--minimized --tray");

        var arguments = LaunchAgentPlist.ReadProgramArguments(File.ReadAllText(PlistPath));
        Assert.NotNull(arguments);
        Assert.Equal([Executable, "--minimized", "--tray"], arguments);
    }

    [Fact]
    public void Register_WhenTheInputIsQuoted_DoesNotCarryTheQuotesIntoTheArray()
    {
        Registrar(new FakeLaunchctl()).Register($"\"{Executable}\"", "--tray");

        var arguments = LaunchAgentPlist.ReadProgramArguments(File.ReadAllText(PlistPath));
        Assert.NotNull(arguments);
        Assert.Equal([Executable, "--tray"], arguments);
    }

    [Fact]
    public void Register_Twice_ReplacesTheEntryRatherThanAppendingToIt()
    {
        var registrar = Registrar(new FakeLaunchctl());
        registrar.Register(Executable, "--minimized");
        registrar.Register(Executable, "--tray");

        var arguments = LaunchAgentPlist.ReadProgramArguments(File.ReadAllText(PlistPath));
        Assert.NotNull(arguments);
        Assert.Equal([Executable, "--tray"], arguments);
    }

    [Fact]
    public void Unregister_DeletesThePlistAndRunsNoLaunchctlCommandAtAll()
    {
        var launchctl = new FakeLaunchctl();
        var registrar = Registrar(launchctl);
        registrar.Register(Executable);
        launchctl.Invocations.Clear();

        registrar.Unregister();

        Assert.False(File.Exists(PlistPath));
        // A `launchctl bootout` here would kill the running app when launchd had started it, so
        // turning the switch off must touch nothing but the file.
        Assert.Empty(launchctl.Invocations);
    }

    [Fact]
    public void Unregister_WhenThereIsNothingToRemove_DoesNotThrow()
    {
        Registrar(new FakeLaunchctl()).Unregister();
    }

    [Fact]
    public void IsRegistered_WhenNoPlistExists_IsFalse()
    {
        var launchctl = new FakeLaunchctl();

        Assert.False(Registrar(launchctl).IsRegistered());
        Assert.Equal(false, Registrar(launchctl).GetRegistrationState());
    }

    [Fact]
    public void IsRegistered_AfterRegister_IsTrue_AndAsksLaunchctlAboutThisUsersGuiDomain()
    {
        var launchctl = new FakeLaunchctl();
        var registrar = Registrar(launchctl);
        registrar.Register(Executable);

        Assert.True(registrar.IsRegistered());
        Assert.Equal(["print-disabled", "gui/501"], Assert.Single(launchctl.Invocations));
    }

    [Fact]
    public void IsRegistered_WhenLoginItemsDisabledIt_IsFalse_EvenThoughThePlistIsStillThere()
    {
        // System Settings writes an override instead of deleting the file, so the file alone would
        // claim the app opens at login when macOS will never start it.
        var launchctl = new FakeLaunchctl
        {
            DisabledOutput = $"disabled services = {{\n\t\"com.example.other\" => false\n\t\"{Label}\" => true\n}}\n",
        };
        var registrar = Registrar(launchctl);
        registrar.Register(Executable);

        Assert.False(registrar.IsRegistered());
        Assert.True(File.Exists(PlistPath));
    }

    [Fact]
    public void IsRegistered_WhenLoginItemsListsItAsEnabled_IsTrue()
    {
        var launchctl = new FakeLaunchctl
        {
            DisabledOutput = $"disabled services = {{\n\t\"{Label}\" => false\n}}\n",
        };
        var registrar = Registrar(launchctl);
        registrar.Register(Executable);

        Assert.True(registrar.IsRegistered());
    }

    [Fact]
    public void GetRegistrationState_WhenThePlistCannotBeParsed_IsNull_SoTheSavedIntentSurvives()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PlistPath)!);
        File.WriteAllText(PlistPath, "this is not a property list");

        Assert.Null(Registrar(new FakeLaunchctl()).GetRegistrationState());
    }

    [Fact]
    public void GetRegistrationState_WhenLaunchctlFails_FallsBackToTheFile()
    {
        var launchctl = new FakeLaunchctl { ExitCode = 1, DisabledOutput = "nonsense" };
        var registrar = Registrar(launchctl);
        registrar.Register(Executable);

        Assert.True(registrar.GetRegistrationState());
    }

    [Fact]
    public void PlistPath_IsUnderTheUsersOwnLaunchAgentsDirectory_WhichNeedsNoAdminRights()
    {
        Assert.Equal(PlistPath, Registrar(new FakeLaunchctl()).PlistPath);
    }
}
