using System.Runtime.Versioning;
using AIMonitor.Application.Settings;
using AIMonitor.Application.Windows;
using AIMonitor.Infrastructure.MacOS;
using AIMonitor.Infrastructure.Providers.Claude;
using AIMonitor.Infrastructure.Storage;
using AIMonitor.Infrastructure.Windows;

namespace AIMonitor.Infrastructure;

/// <summary>
/// Chooses the adapter that implements each operating-system port on the host actually running.
///
/// A composition root calls these instead of naming a concrete adapter, so Windows and macOS make
/// the same choices from one place rather than each presentation layer repeating them and drifting
/// apart (R-022). Everything else in this assembly is platform-neutral or already annotated, so this
/// is the only type that needs to know which operating system it is on.
/// </summary>
public static class PlatformAdapters
{
    /// <summary>
    /// The identity both platforms file their per-user state under. It is the same string the
    /// Windows installer uses as its AppId (ADR-0003), so an upgrade finds the profile it left.
    /// </summary>
    public const string AppId = "AIUsageMonitor";

    /// <summary>
    /// Set this to run against a profile of its own instead of the installed app's.
    ///
    /// The name carries over from the 1.x build, which learned that a scope has to reach every
    /// per-user thing the app owns: a run that redirected only the settings folder still wrote the
    /// real Keychain item, removed the real login item and answered the real single-instance
    /// socket. So the scope is appended to all four.
    /// </summary>
    public const string ScopeEnvironmentVariable = "AI_USAGE_MONITOR_SETTINGS_SCOPE";

    /// <summary>The scope in effect, or empty when running as the installed app.</summary>
    public static string Scope =>
        (Environment.GetEnvironmentVariable(ScopeEnvironmentVariable) ?? string.Empty).Trim();

    /// <summary><paramref name="name"/> with the scope appended, or unchanged when there is none.</summary>
    public static string Scoped(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var scope = Scope;
        return scope.Length == 0 ? name : $"{name}.{scope}";
    }

    /// <summary>The platforms with a full set of adapters. Nothing else can run the app.</summary>
    public static bool IsSupportedPlatform => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    /// <summary>Where settings, secrets and logs live for this user.</summary>
    public static string AppDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Scoped(AppId));

    /// <summary>
    /// The secret store: one sealed file either way, sealed with DPAPI on Windows and with a key
    /// from the login Keychain on macOS.
    /// </summary>
    public static ISecretStore CreateSecretStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new DpapiSecretStore(path, CreateSecretProtector(), new AtomicFileWriter());
    }

    /// <summary>The per-user "open at login" entry: the HKCU Run key, or a LaunchAgent plist.</summary>
    public static IStartupRegistrar CreateStartupRegistrar()
    {
        if (OperatingSystem.IsWindows())
        {
            return CreateWindowsStartupRegistrar();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new LaunchAgentStartupRegistrar(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Scoped(LaunchAgentPlist.DefaultLabel),
                MacOSCommand.Run,
                LaunchAgentStartupRegistrar.CurrentGuiSessionTarget);
        }

        throw Unsupported(nameof(IStartupRegistrar));
    }

    /// <summary>
    /// The single-instance guard: a named mutex plus a named pipe on Windows, one listening Unix
    /// domain socket on macOS.
    /// </summary>
    public static ISingleInstanceCoordinator CreateSingleInstanceCoordinator()
    {
        if (OperatingSystem.IsWindows())
        {
            return CreateNamedMutexSingleInstance();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new LocalSocketSingleInstance(AppDataDirectory, Scoped(AppId));
        }

        throw Unsupported(nameof(ISingleInstanceCoordinator));
    }

    /// <summary>
    /// The 1.x profile to import on first run.
    ///
    /// Windows reads the legacy Registry tree. macOS returns nothing: the 1.x macOS build kept its
    /// settings in a <c>~/Library/Preferences</c> plist and its keys in the Keychain under its own
    /// service, and importing those is a separate decision that R-022 does not cover — so a macOS
    /// user starts with a fresh profile rather than a half-read one.
    /// </summary>
    public static ILegacySettingsReader CreateLegacySettingsReader() =>
        OperatingSystem.IsWindows() ? CreateLegacyRegistrySettingsReader() : new NoLegacySettings();

    /// <summary>
    /// Opens a secret sealed by the 1.x build. DPAPI on Windows; on macOS nothing to open, for the
    /// same reason <see cref="CreateLegacySettingsReader"/> reads nothing there.
    /// </summary>
    public static ILegacySecretUnsealer CreateLegacySecretUnsealer() =>
        OperatingSystem.IsWindows() ? new LegacyDpapiSecretUnsealer() : new NoLegacySecrets();

    /// <summary>
    /// Where to look for Claude Code's login. Windows keeps it in a file under the Claude config
    /// directory; macOS files the same JSON in the login Keychain and usually has no file at all,
    /// so the Mac tries the file first — honouring a <c>CLAUDE_CONFIG_DIR</c> that does point at
    /// one — and falls through to the Keychain when there is none.
    /// </summary>
    public static IClaudeCredentialReader CreateClaudeCredentialReader() =>
        OperatingSystem.IsMacOS()
            ? new ChainedClaudeCredentialReader(
                new ClaudeCredentialFileReader(),
                new ClaudeKeychainCredentialReader())
            : new ClaudeCredentialFileReader();

    internal static ISecretProtector CreateSecretProtector()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsDpapiProtector("AIMonitor2.secrets.v1");
        }

        if (OperatingSystem.IsMacOS())
        {
            return new KeychainSecretProtector(
                Scoped(KeychainSecretProtector.DefaultService),
                KeychainSecretProtector.DefaultAccount,
                MacOSCommand.Run);
        }

        throw Unsupported(nameof(ISecretProtector));
    }

    [SupportedOSPlatform("windows")]
    private static IStartupRegistrar CreateWindowsStartupRegistrar() => new WindowsStartupRegistrar();

    [SupportedOSPlatform("windows")]
    private static ISingleInstanceCoordinator CreateNamedMutexSingleInstance() => new NamedMutexSingleInstance();

    [SupportedOSPlatform("windows")]
    private static ILegacySettingsReader CreateLegacyRegistrySettingsReader() => new LegacyRegistrySettingsReader();

    private static PlatformNotSupportedException Unsupported(string port) =>
        new($"{port} has no adapter for this operating system; AI Usage Monitor supports Windows and macOS.");

    /// <summary>No 1.x profile to read on this platform.</summary>
    private sealed class NoLegacySettings : ILegacySettingsReader
    {
        public IReadOnlyDictionary<string, object?> ReadAll() =>
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>No 1.x sealed secrets to open on this platform.</summary>
    private sealed class NoLegacySecrets : ILegacySecretUnsealer
    {
        public bool TryUnseal(string protectedValue, out string plaintext)
        {
            plaintext = string.Empty;
            return false;
        }
    }
}
