using System.Globalization;
using AIMonitor.Application.Providers;
using AIMonitor.Application.Settings;
using AIMonitor.Application.Windows;
using AIMonitor.Infrastructure;
using AIMonitor.Infrastructure.Providers.Agy;
using AIMonitor.Infrastructure.Providers.Claude;
using AIMonitor.Infrastructure.Providers.OpenAi;
using AIMonitor.Infrastructure.Storage;
using AIMonitor.Infrastructure.Time;

namespace AIMonitor.Presentation.Avalonia;

/// <summary>What the composition root hands the UI once the app has started.</summary>
internal sealed record StartupResult(
    SettingsSession Settings,
    LatestRefreshCoordinator RefreshCoordinator,
    ProviderConnectionStore Connections,
    IStartupRegistrar StartupRegistrar);

/// <summary>
/// Owns everything the app needs from the operating system and from
/// <c>AIMonitor.Infrastructure</c>, so the views and view models never reach into either.
/// Adapters come from <c>PlatformAdapters</c>, which is what makes this host identical on Windows
/// and macOS.
/// </summary>
internal sealed class PlatformHost : IAsyncDisposable
{
    private readonly ISingleInstanceCoordinator _singleInstance;
    private HttpClient? _httpClient;
    private LatestRefreshCoordinator? _refreshCoordinator;
    private bool _ownsSingleInstance;

    private PlatformHost(ISingleInstanceCoordinator singleInstance) => _singleInstance = singleInstance;

    public static PlatformHost Create()
    {
        if (!PlatformAdapters.IsSupportedPlatform)
        {
            throw new PlatformNotSupportedException(
                "AI Usage Monitor runs on Windows and macOS; this operating system has no adapters.");
        }

        return new PlatformHost(PlatformAdapters.CreateSingleInstanceCoordinator());
    }

    /// <summary>Raised when another launch asks the running copy to show itself.</summary>
    public event Action<string[]>? Activated;

    /// <summary>
    /// False when a copy is already running: the arguments have been handed over and the caller
    /// must exit with <paramref name="exitCode"/> rather than open a second window (PAR-023).
    /// </summary>
    public bool TryAcquireSingleInstance(string[] arguments, out int exitCode)
    {
        _ownsSingleInstance = _singleInstance.TryAcquireAsync().AsTask().GetAwaiter().GetResult();
        if (_ownsSingleInstance)
        {
            _singleInstance.Activated += OnActivated;
            exitCode = 0;
            return true;
        }

        _singleInstance.NotifyExistingInstanceAsync(arguments).AsTask().GetAwaiter().GetResult();
        exitCode = 0;
        return false;
    }

    public async Task<StartupResult> StartAsync()
    {
        var directory = PlatformAdapters.AppDataDirectory;
        Directory.CreateDirectory(directory);

        var settingsStore = new JsonSettingsStore(Path.Combine(directory, "settings.json"));
        var secretStore = PlatformAdapters.CreateSecretStore(Path.Combine(directory, "secrets.dat"));
        var startupRegistrar = PlatformAdapters.CreateStartupRegistrar();

        var firstLaunch = false;
        if (!settingsStore.Exists)
        {
            var legacyReader = PlatformAdapters.CreateLegacySettingsReader();
            try
            {
                firstLaunch = legacyReader.ReadAll().Count == 0;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // An unreadable legacy profile is never treated as a fresh profile.
            }

            await new MigrateLegacySettingsUseCase(
                settingsStore,
                secretStore,
                legacyReader,
                PlatformAdapters.CreateLegacySecretUnsealer()).ExecuteAsync().ConfigureAwait(false);
        }

        var settings = await SettingsSession.CreateAsync(settingsStore).ConfigureAwait(false);

        try
        {
            await StartupSettingsReconciler.ReconcileAsync(
                settings,
                startupRegistrar,
                Environment.ProcessPath!,
                firstLaunch,
                preserveInstallerIntent: false).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // Opening at login is optional; failing to register must not stop the dashboard.
        }

        var connections = new ProviderConnectionStore(secretStore, settings);
        _refreshCoordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase(BuildProviders(connections)));

        return new StartupResult(settings, _refreshCoordinator, connections, startupRegistrar);
    }

    private ProviderClientRegistration[] BuildProviders(ProviderConnectionStore connections)
    {
        _httpClient = new HttpClient();
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var clock = SystemClock.Instance;

        var claude = new ClaudeLiveQuotaClient(
            _httpClient,
            Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"),
            userProfile,
            clock,
            PlatformAdapters.CreateClaudeCredentialReader());

        var openAi = new ReconfigurableQuotaClient(
            connections,
            "openai",
            connection =>
            {
                double? monthlyBudget =
                    double.TryParse(connection.Extra, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
                        ? parsed
                        : null;
                return new OpenAiLiveQuotaClient(
                    _httpClient!, userProfile, clock, savedAdminKey: connection.Key, monthlyBudgetUsd: monthlyBudget);
            });

        var agy = new AgyQuotaClient(new AgyUsageRunner(), clock);

        return
        [
            new ProviderClientRegistration("claude", claude),
            new ProviderClientRegistration("openai", openAi),
            new ProviderClientRegistration("agy", agy),
        ];
    }

    private void OnActivated(string[] arguments) => Activated?.Invoke(arguments);

    public async ValueTask DisposeAsync()
    {
        if (_refreshCoordinator is not null)
        {
            await _refreshCoordinator.DisposeAsync().ConfigureAwait(false);
            _refreshCoordinator = null;
        }

        if (_ownsSingleInstance)
        {
            _singleInstance.Activated -= OnActivated;
        }

        await _singleInstance.DisposeAsync().ConfigureAwait(false);
        _httpClient?.Dispose();
        _httpClient = null;
    }
}
