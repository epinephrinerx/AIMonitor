using System.Runtime.Versioning;
using AIMonitor.Infrastructure;
using AIMonitor.Infrastructure.Linux;
using AIMonitor.Infrastructure.MacOS;
using AIMonitor.Infrastructure.Storage;
using AIMonitor.Infrastructure.Windows;

namespace AIMonitor.Infrastructure.Tests;

/// <summary>
/// Which adapter the factory picks on the host running the test. Each case asserts the choice for
/// both platforms, so the same test proves the Windows selection on Windows CI and the macOS
/// selection on macOS CI rather than skipping on either.
///
/// Nothing here calls an adapter's operating-system side: constructing is free, but reading a
/// secret would reach into the real login Keychain and registering a login item would add a real
/// entry to the user's account. The smoke tool under <c>tools/</c> is where that happens, on purpose
/// and in isolation.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class PlatformAdaptersTests
{
    [Fact]
    public void IsSupportedPlatform_IsTrueOnThePlatformsWithAFullSetOfAdapters()
    {
        Assert.Equal(
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux(),
            PlatformAdapters.IsSupportedPlatform);
    }

    [Fact]
    public void AppId_MatchesTheIdentityBothPlatformsFileStateUnder()
    {
        Assert.Equal("AIUsageMonitor", PlatformAdapters.AppId);
        Assert.Equal(PlatformAdapters.AppId, LocalSocketSingleInstance.DefaultAppId);
    }

    [Fact]
    public void AppDataDirectory_IsThisUsersOwnFolderNamedAfterTheApp()
    {
        var directory = PlatformAdapters.AppDataDirectory;

        Assert.Equal(PlatformAdapters.AppId, Path.GetFileName(directory));
        Assert.True(Path.IsPathFullyQualified(directory), directory);
    }

    [Fact]
    public void CreateStartupRegistrar_PicksTheRunKeyOnWindowsALaunchAgentOnMacOSAndAnAutostartEntryOnLinux()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.IsType<WindowsStartupRegistrar>(PlatformAdapters.CreateStartupRegistrar());
        }
        else if (OperatingSystem.IsMacOS())
        {
            Assert.IsType<LaunchAgentStartupRegistrar>(PlatformAdapters.CreateStartupRegistrar());
        }
        else if (OperatingSystem.IsLinux())
        {
            var registrar = Assert.IsType<XdgAutostartStartupRegistrar>(PlatformAdapters.CreateStartupRegistrar());

            Assert.Equal(PlatformAdapters.Scoped("net.apichart.aiusagemonitor"), registrar.FileId);
            Assert.Equal(
                XdgAutostartStartupRegistrar.ResolveAutostartDirectory(
                    Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"),
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify))
                + "/" + registrar.FileId + ".desktop",
                registrar.DesktopFilePath);
        }
        else
        {
            Assert.Throws<PlatformNotSupportedException>(() => PlatformAdapters.CreateStartupRegistrar());
        }
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task CreateSingleInstanceCoordinator_PicksTheMutexOnWindowsAndASocketOnMacOSAndLinux()
    {
        if (OperatingSystem.IsWindows())
        {
            await using var coordinator = PlatformAdapters.CreateSingleInstanceCoordinator();
            Assert.IsType<NamedMutexSingleInstance>(coordinator);
        }
        else if (OperatingSystem.IsMacOS())
        {
            await using var coordinator = PlatformAdapters.CreateSingleInstanceCoordinator();

            // Constructing it also proves the real socket path fits inside the length a Unix domain
            // socket allows, which depends on how long this user's home directory is.
            Assert.IsType<LocalSocketSingleInstance>(coordinator);
        }
        else if (OperatingSystem.IsLinux())
        {
            await using var coordinator = PlatformAdapters.CreateSingleInstanceCoordinator();

            var linux = Assert.IsType<LinuxSingleInstance>(coordinator);
            var scopedAppId = PlatformAdapters.Scoped("AIUsageMonitor");
            Assert.Equal(
                LocalSocketSingleInstance.ResolveSocketPath(
                    LinuxRuntimeDirectory.ResolveSocketDirectory(
                        Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"),
                        PlatformAdapters.AppDataDirectory,
                        scopedAppId),
                    scopedAppId),
                linux.SocketPath);
        }
        else
        {
            Assert.Throws<PlatformNotSupportedException>(() => PlatformAdapters.CreateSingleInstanceCoordinator());
        }
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public void CreateSecretProtector_PicksDpapiOnWindowsTheKeychainOnMacOSAndTheSecretServiceOnLinux()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("WindowsDpapiProtector", PlatformAdapters.CreateSecretProtector().GetType().Name);
        }
        else if (OperatingSystem.IsMacOS())
        {
            Assert.IsType<KeychainSecretProtector>(PlatformAdapters.CreateSecretProtector());
        }
        else if (OperatingSystem.IsLinux())
        {
            var protector = Assert.IsType<LinuxSecretProtector>(PlatformAdapters.CreateSecretProtector());

            Assert.Equal(PlatformAdapters.Scoped("AIMonitor2.secrets.v1"), protector.Service);
            Assert.Equal("secret-store-key", protector.Account);
            Assert.Equal(Path.Combine(PlatformAdapters.AppDataDirectory, "secrets.key"), protector.KeyFilePath);
        }
        else
        {
            Assert.Throws<PlatformNotSupportedException>(() => PlatformAdapters.CreateSecretProtector());
        }
    }

    [Fact]
    public void CreateSecretStore_IsTheSameSealedFileStoreOnBothPlatforms()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aim-secrets-{Guid.NewGuid():N}.dat");

        Assert.IsType<DpapiSecretStore>(PlatformAdapters.CreateSecretStore(path));
        Assert.False(File.Exists(path), "constructing the store must not write anything");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateSecretStore_WithoutAPath_IsRefused(string? path)
    {
        // ThrowsAny: a null path raises ArgumentNullException, a blank one ArgumentException.
        Assert.ThrowsAny<ArgumentException>(() => PlatformAdapters.CreateSecretStore(path!));
    }

    [Fact]
    public void Scoped_LeavesTheNameAloneWhenNoScopeIsSet()
    {
        using var scope = new ScopedEnvironment(null);

        Assert.Equal(string.Empty, PlatformAdapters.Scope);
        Assert.Equal("AIUsageMonitor", PlatformAdapters.Scoped("AIUsageMonitor"));
    }

    [Theory]
    [InlineData("smoke", "AIUsageMonitor.smoke")]
    [InlineData("  padded  ", "AIUsageMonitor.padded")]
    public void Scoped_AppendsTheScopeSoARunCannotWriteOverTheInstalledApp(string scope, string expected)
    {
        using var applied = new ScopedEnvironment(scope);

        Assert.Equal(expected, PlatformAdapters.Scoped("AIUsageMonitor"));
    }

    [Fact]
    public void AppDataDirectory_IsScopedToo_SoSettingsAndSecretsMoveWithIt()
    {
        using var scope = new ScopedEnvironment("smoke");

        Assert.Equal("AIUsageMonitor.smoke", Path.GetFileName(PlatformAdapters.AppDataDirectory));
    }

    [MacOSOnlyFact("the LaunchAgent registrar is the macOS adapter")]
    public void CreateStartupRegistrar_UnderAScope_TargetsItsOwnLoginItem_NotTheInstalledOne()
    {
        using var scope = new ScopedEnvironment("smoke");

        var registrar = Assert.IsType<LaunchAgentStartupRegistrar>(PlatformAdapters.CreateStartupRegistrar());

        // The 1.x port found this the hard way: a scope that misses the login item means a test run
        // deletes the real one.
        Assert.Equal($"{LaunchAgentPlist.DefaultLabel}.smoke", registrar.Label);
        Assert.EndsWith($"{LaunchAgentPlist.DefaultLabel}.smoke.plist", registrar.PlistPath, StringComparison.Ordinal);
    }

    [LinuxOnlyFact("the autostart registrar is the Linux adapter")]
    public void CreateStartupRegistrar_OnLinux_UnderAScope_TargetsItsOwnEntry_NotTheInstalledOne()
    {
        using var scope = new ScopedEnvironment("smoke");

        var registrar = Assert.IsType<XdgAutostartStartupRegistrar>(PlatformAdapters.CreateStartupRegistrar());

        // A scope that misses the autostart entry means a test run deletes the real one.
        Assert.Equal("net.apichart.aiusagemonitor.smoke", registrar.FileId);
        Assert.EndsWith("/autostart/net.apichart.aiusagemonitor.smoke.desktop", registrar.DesktopFilePath, StringComparison.Ordinal);
    }

    [LinuxOnlyFact("the socket coordinator is the Linux adapter")]
    [SupportedOSPlatform("linux")]
    public async Task CreateSingleInstanceCoordinator_OnLinux_UnderAScope_UsesItsOwnSocket()
    {
        string unscopedPath;
        await using (var unscoped = PlatformAdapters.CreateSingleInstanceCoordinator())
        {
            unscopedPath = Assert.IsType<LinuxSingleInstance>(unscoped).SocketPath;
        }

        using var scope = new ScopedEnvironment("smoke");
        await using var scoped = PlatformAdapters.CreateSingleInstanceCoordinator();
        var scopedPath = Assert.IsType<LinuxSingleInstance>(scoped).SocketPath;

        Assert.NotEqual(unscopedPath, scopedPath);
        var xdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        var resolved = LinuxRuntimeDirectory.ResolveSocketDirectory(xdg, PlatformAdapters.AppDataDirectory, "AIUsageMonitor.smoke");
        Assert.Equal(LocalSocketSingleInstance.ResolveSocketPath(resolved, "AIUsageMonitor.smoke"), scopedPath);
        if (resolved != PlatformAdapters.AppDataDirectory)
        {
            // L3 strips every trailing slash before use, so the expected prefix is the normalised path.
            var normalised = xdg!.TrimEnd('/');
            if (normalised.Length == 0)
            {
                normalised = "/";
            }

            Assert.StartsWith(Path.Combine(normalised, "AIUsageMonitor.smoke") + "/", scopedPath, StringComparison.Ordinal);
            Assert.DoesNotContain("//", scopedPath, StringComparison.Ordinal);
        }
    }

    [LinuxOnlyFact("activation goes through a real Unix socket")]
    [SupportedOSPlatform("linux")]
    public async Task CreateSingleInstanceCoordinator_OnLinux_ASecondLaunchActivatesTheFirst()
    {
        // A scope of its own, so the run never touches the folder of an installed copy.
        using var scope = new ScopedEnvironment("t" + Guid.NewGuid().ToString("N")[..8]);
        var first = Assert.IsType<LinuxSingleInstance>(PlatformAdapters.CreateSingleInstanceCoordinator());
        var second = Assert.IsType<LinuxSingleInstance>(PlatformAdapters.CreateSingleInstanceCoordinator());
        var socketFolder = Path.GetDirectoryName(first.SocketPath)!;
        try
        {
            var activated = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            first.Activated += arguments => activated.TrySetResult(arguments);

            Assert.True(await first.TryAcquireAsync());
            Assert.True(first.HasOwnership);
            Assert.False(await second.TryAcquireAsync());
            Assert.True(await second.NotifyExistingInstanceAsync(["--show"]));

            Assert.Equal(["--show"], await activated.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            await second.DisposeAsync();
            await first.DisposeAsync();
            if (Directory.Exists(socketFolder))
            {
                Directory.Delete(socketFolder, recursive: true);
            }
        }
    }

    [LinuxOnlyFact("the data folder rule is Linux-only")]
    public void AppDataDirectory_OnLinux_IsFullyQualifiedEvenWhenTheDataFolderDoesNotExistYet()
    {
        var missing = Path.Combine(Path.GetTempPath(), "aim-nodata-" + Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        using var scope = new ScopedEnvironment(null);
        try
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", missing);

            Assert.Equal(Path.Combine(missing, "AIUsageMonitor"), PlatformAdapters.AppDataDirectory);
            Assert.False(Directory.Exists(missing));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", previous);
        }
    }

    [LinuxOnlyFact("the autostart folder is resolved from the Linux environment")]
    public void CreateStartupRegistrar_OnLinux_UsesTheAbsoluteXdgConfigHomeWhenThereIsOne()
    {
        var config = Path.Combine(Path.GetTempPath(), "aim-cfg-" + Guid.NewGuid().ToString("N"));
        using var scope = new ScopedEnvironment(null);
        using var env = new EnvironmentVariables(("XDG_CONFIG_HOME", config), ("HOME", "/aim/home"));

        var registrar = Assert.IsType<XdgAutostartStartupRegistrar>(PlatformAdapters.CreateStartupRegistrar());

        Assert.Equal(config + "/autostart/net.apichart.aiusagemonitor.desktop", registrar.DesktopFilePath);
        Assert.False(Directory.Exists(config), "creating the registrar must not touch the disk");
    }

    [LinuxOnlyFact("the autostart folder is resolved from the Linux environment")]
    public void CreateStartupRegistrar_OnLinux_FallsBackToTheHomeFolderWhenXdgConfigHomeIsUnsetOrRelative()
    {
        using var scope = new ScopedEnvironment(null);

        foreach (var xdg in new string?[] { null, "", "relative/cfg" })
        {
            using var env = new EnvironmentVariables(("XDG_CONFIG_HOME", xdg), ("HOME", "/aim/home"));

            var registrar = Assert.IsType<XdgAutostartStartupRegistrar>(PlatformAdapters.CreateStartupRegistrar());

            Assert.True(
                "/aim/home/.config/autostart/net.apichart.aiusagemonitor.desktop" == registrar.DesktopFilePath,
                $"XDG_CONFIG_HOME={xdg ?? "(unset)"} gave {registrar.DesktopFilePath}");
        }
    }

    [LinuxOnlyFact("the autostart folder is resolved from the Linux environment")]
    public void CreateStartupRegistrar_OnLinux_DoesNotNeedTheHomeFolderToExist()
    {
        using var scope = new ScopedEnvironment(null);
        using var env = new EnvironmentVariables(("XDG_CONFIG_HOME", null), ("HOME", "/aim/no/such/home"));

        var registrar = Assert.IsType<XdgAutostartStartupRegistrar>(PlatformAdapters.CreateStartupRegistrar());

        Assert.Equal("/aim/no/such/home/.config/autostart/net.apichart.aiusagemonitor.desktop", registrar.DesktopFilePath);
    }

    [LinuxOnlyFact("the scope becomes part of a file name on Linux")]
    public void CreateStartupRegistrar_OnLinux_RefusesAScopeThatCannotBePartOfAFileName()
    {
        // A NUL cannot be tested here: the operating system truncates an environment value at the NUL.
        // The NUL case is covered where the file id is passed in directly.
        foreach (var bad in new[] { "a/b", "../x", "/abs" })
        {
            using var scope = new ScopedEnvironment(bad);

            var thrown = Record.Exception(() => PlatformAdapters.CreateStartupRegistrar());

            Assert.True(
                thrown is not null && thrown.GetType() == typeof(ArgumentException),
                $"scope '{bad}' must make the factory throw ArgumentException, got {thrown?.GetType().Name ?? "nothing"}");
        }
    }

    [LinuxOnlyFact("the autostart folder is resolved from the Linux environment")]
    public void CreateStartupRegistrar_OnLinux_NeverReadsOrWritesTheSystemWideAutostartFolders()
    {
        var root = Path.Combine(Path.GetTempPath(), "aim-sys-" + Guid.NewGuid().ToString("N"));
        var system = Path.Combine(root, "system");
        var user = Path.Combine(root, "user");
        var scopeName = "t" + Guid.NewGuid().ToString("N")[..8];
        var fileId = "net.apichart.aiusagemonitor." + scopeName;
        var systemEntry = Path.Combine(system, "autostart", fileId + ".desktop");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(systemEntry)!);
            const string systemContents = "[Desktop Entry]\nType=Application\nName=System\nExec=/usr/bin/system-app\n";
            File.WriteAllText(systemEntry, systemContents);
            using var scope = new ScopedEnvironment(scopeName);
            using var env = new EnvironmentVariables(("XDG_CONFIG_HOME", user), ("XDG_CONFIG_DIRS", system), ("HOME", "/aim/home"));
            var registrar = PlatformAdapters.CreateStartupRegistrar();

            Assert.False(registrar.IsRegistered(), "an entry in a system folder must not count");

            registrar.Register("/opt/x/aim");
            Assert.True(registrar.IsRegistered());
            registrar.Unregister();

            Assert.False(registrar.IsRegistered());
            Assert.Equal(systemContents, File.ReadAllText(systemEntry));
            Assert.Equal([systemEntry], Directory.GetFiles(system, "*", SearchOption.AllDirectories));
            Assert.Equal([Path.Combine(user, "autostart")], Directory.GetDirectories(user));
            Assert.Empty(Directory.GetFiles(user, "*", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [LinuxOnlyFact("the secret protector is the Linux adapter")]
    [SupportedOSPlatform("linux")]
    public void CreateSecretProtector_OnLinux_UnderAScope_UsesItsOwnKeyringItemAndKeyFile()
    {
        using var scope = new ScopedEnvironment("smoke");

        var protector = Assert.IsType<LinuxSecretProtector>(PlatformAdapters.CreateSecretProtector());

        // A scope that misses the keyring item or the key file means a test run reads the installed app's key.
        Assert.Equal("AIMonitor2.secrets.v1.smoke", protector.Service);
        Assert.Equal("AIUsageMonitor.smoke", Path.GetFileName(Path.GetDirectoryName(protector.KeyFilePath)));
    }

    [MacOSOnlyFact("the socket coordinator is the macOS adapter")]
    public async Task CreateSingleInstanceCoordinator_UnderAScope_UsesItsOwnSocket()
    {
        string unscopedPath;
        await using (var unscoped = PlatformAdapters.CreateSingleInstanceCoordinator())
        {
            unscopedPath = Assert.IsType<LocalSocketSingleInstance>(unscoped).SocketPath;
        }

        using var scope = new ScopedEnvironment("smoke");
        await using var scoped = PlatformAdapters.CreateSingleInstanceCoordinator();

        Assert.NotEqual(unscopedPath, Assert.IsType<LocalSocketSingleInstance>(scoped).SocketPath);
    }

    /// <summary>Sets environment variables for one test and puts back whatever was there.</summary>
    private sealed class EnvironmentVariables : IDisposable
    {
        private readonly (string Name, string? Previous)[] _previous;

        public EnvironmentVariables(params (string Name, string? Value)[] variables)
        {
            _previous = [.. variables.Select(v => (v.Name, Environment.GetEnvironmentVariable(v.Name)))];
            foreach (var (name, value) in variables)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Dispose()
        {
            foreach (var (name, previous) in _previous)
            {
                Environment.SetEnvironmentVariable(name, previous);
            }
        }
    }

    /// <summary>Sets the scope variable for one test and puts back whatever was there.</summary>
    private sealed class ScopedEnvironment : IDisposable
    {
        private readonly string? _previous =
            Environment.GetEnvironmentVariable(PlatformAdapters.ScopeEnvironmentVariable);

        public ScopedEnvironment(string? scope) =>
            Environment.SetEnvironmentVariable(PlatformAdapters.ScopeEnvironmentVariable, scope);

        public void Dispose() =>
            Environment.SetEnvironmentVariable(PlatformAdapters.ScopeEnvironmentVariable, _previous);
    }

    [Fact]
    public void CreateLegacySettingsReader_ReadsTheRegistryOnWindowsAndNothingOnMacOS()
    {
        var reader = PlatformAdapters.CreateLegacySettingsReader();

        if (OperatingSystem.IsWindows())
        {
            Assert.IsType<LegacyRegistrySettingsReader>(reader);
        }
        else
        {
            // A macOS 1.x profile lives in a Preferences plist and the Keychain, which R-022 does
            // not import; a fresh profile is the honest result, not a half-read one.
            Assert.Empty(reader.ReadAll());
        }
    }

    [Fact]
    public void CreateLegacySecretUnsealer_OpensDpapiBlobsOnWindowsAndNothingOnMacOS()
    {
        var unsealer = PlatformAdapters.CreateLegacySecretUnsealer();

        if (OperatingSystem.IsWindows())
        {
            Assert.IsType<LegacyDpapiSecretUnsealer>(unsealer);
        }
        else
        {
            Assert.False(unsealer.TryUnseal("AQAAANCMnd8BFdERjHoAwE/Cl+sB", out var plaintext));
            Assert.Empty(plaintext);
        }
    }
}
