using AIMonitor.Infrastructure;
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
public sealed class PlatformAdaptersTests
{
    [Fact]
    public void IsSupportedPlatform_IsTrueOnTheTwoPlatformsWithAFullSetOfAdapters()
    {
        Assert.Equal(
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(),
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
    public void CreateStartupRegistrar_PicksTheRunKeyOnWindowsAndALaunchAgentOnMacOS()
    {
        var registrar = PlatformAdapters.CreateStartupRegistrar();

        if (OperatingSystem.IsWindows())
        {
            Assert.IsType<WindowsStartupRegistrar>(registrar);
        }
        else
        {
            Assert.IsType<LaunchAgentStartupRegistrar>(registrar);
        }
    }

    [Fact]
    public async Task CreateSingleInstanceCoordinator_PicksTheMutexOnWindowsAndASocketOnMacOS()
    {
        await using var coordinator = PlatformAdapters.CreateSingleInstanceCoordinator();

        if (OperatingSystem.IsWindows())
        {
            Assert.IsType<NamedMutexSingleInstance>(coordinator);
        }
        else
        {
            // Constructing it also proves the real socket path fits inside the length a Unix domain
            // socket allows, which depends on how long this user's home directory is.
            Assert.IsType<LocalSocketSingleInstance>(coordinator);
        }
    }

    [Fact]
    public void CreateSecretProtector_PicksDpapiOnWindowsAndTheKeychainOnMacOS()
    {
        var protector = PlatformAdapters.CreateSecretProtector();

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("WindowsDpapiProtector", protector.GetType().Name);
        }
        else
        {
            Assert.IsType<KeychainSecretProtector>(protector);
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
