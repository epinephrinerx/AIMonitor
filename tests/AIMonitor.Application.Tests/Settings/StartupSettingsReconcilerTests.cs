using AIMonitor.Application.Settings;
using AIMonitor.Application.Windows;
using AIMonitor.TestSupport;

namespace AIMonitor.Application.Tests.Settings;

public sealed class StartupSettingsReconcilerTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Reconcile_WindowsIntentWins_AndNeverReEnablesAnExternalOff(bool saved, bool windows)
    {
        var initial = new AppSettings { StartWithWindows = saved, Theme = "dark" };
        var store = new BlockingSettingsStore(initial);
        store.Release();
        using var session = new SettingsSession(store, initial);
        var registrar = new Registrar(windows);

        await StartupSettingsReconciler.ReconcileAsync(session, registrar, @"C:\fixtures\AIUsageMonitor.exe");

        Assert.Equal(windows, session.Current.StartWithWindows);
        Assert.Equal("dark", session.Current.Theme);
        Assert.Equal(0, registrar.Writes);
        Assert.Equal(saved == windows ? 0 : 1, store.SavedSettings.Count);
    }

    [Fact]
    public async Task Reconcile_ReadFailure_DoesNotOverwriteSettings()
    {
        var initial = new AppSettings { StartWithWindows = true };
        var store = new BlockingSettingsStore(initial);
        store.Release();
        using var session = new SettingsSession(store, initial);
        var registrar = new Registrar(false) { FailRead = true };

        await Assert.ThrowsAsync<InvalidOperationException>(() => StartupSettingsReconciler.ReconcileAsync(session, registrar, @"C:\fixtures\AIUsageMonitor.exe"));

        Assert.True(session.Current.StartWithWindows);
        Assert.Empty(store.SavedSettings);
        Assert.Equal(0, registrar.Writes);
    }

    private sealed class Registrar(bool enabled) : IStartupRegistrar
    {
        public int Writes { get; private set; }
        public bool FailRead { get; init; }
        public bool FailRegister { get; init; }
        public bool IsRegistered() => FailRead ? throw new InvalidOperationException("Synthetic read failure") : enabled;
        public void Register(string executablePath, string arguments = "")
        {
            if (FailRegister) throw new InvalidOperationException("Synthetic register failure");
            Writes++;
            enabled = true;
        }
        public void Unregister() { Writes++; enabled = false; }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FreshProfile_AppliesDefaultOnce_ThenExternalOffStaysOff(bool defaultOn)
    {
        var initial = new AppSettings { StartWithWindows = defaultOn };
        var store = new BlockingSettingsStore(initial);
        store.Release();
        using var session = new SettingsSession(store, initial);
        var registrar = new Registrar(false);

        await StartupSettingsReconciler.ReconcileAsync(session, registrar, @"C:\fixtures\AIUsageMonitor.exe", firstLaunch: true);

        Assert.Equal(defaultOn, registrar.IsRegistered());
        Assert.Equal(defaultOn, session.Current.StartWithWindows);
        registrar.Unregister();
        var writes = registrar.Writes;
        await StartupSettingsReconciler.ReconcileAsync(session, registrar, @"C:\fixtures\AIUsageMonitor.exe");

        Assert.False(registrar.IsRegistered());
        Assert.False(session.Current.StartWithWindows);
        Assert.Equal(writes, registrar.Writes);
    }

    [Fact]
    public async Task UpgradeWithoutLegacySettings_PreservesAbsentStartupEntry()
    {
        var initial = new AppSettings();
        var store = new BlockingSettingsStore(initial);
        store.Release();
        using var session = new SettingsSession(store, initial);
        var registrar = new Registrar(false);

        await StartupSettingsReconciler.ReconcileAsync(session, registrar, @"C:\fixtures\AIUsageMonitor.exe", firstLaunch: true, preserveInstallerIntent: true);

        Assert.False(registrar.IsRegistered());
        Assert.False(session.Current.StartWithWindows);
        Assert.Equal(0, registrar.Writes);
    }

    [Fact]
    public async Task FreshDefaultRegistrationFailure_DoesNotOverwriteSettings()
    {
        var initial = new AppSettings();
        var store = new BlockingSettingsStore(initial);
        store.Release();
        using var session = new SettingsSession(store, initial);
        var registrar = new Registrar(false) { FailRegister = true };

        await Assert.ThrowsAsync<InvalidOperationException>(() => StartupSettingsReconciler.ReconcileAsync(session, registrar, @"C:\fixtures\AIUsageMonitor.exe", firstLaunch: true));

        Assert.True(session.Current.StartWithWindows);
        Assert.Empty(store.SavedSettings);
    }

    [Fact]
    public async Task AlreadyCancelled_DoesNotReadOrWriteRegistration()
    {
        var initial = new AppSettings();
        var store = new BlockingSettingsStore(initial);
        using var session = new SettingsSession(store, initial);
        var registrar = new Registrar(false) { FailRead = true };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StartupSettingsReconciler.ReconcileAsync(session, registrar, @"C:\fixtures\AIUsageMonitor.exe", cancellationToken: cancellation.Token));

        Assert.Empty(store.SavedSettings);
        Assert.Equal(0, registrar.Writes);
    }
}
