using AIMonitor.Application.Settings;
using AIMonitor.Application.Windows;
using AIMonitor.Presentation.Wpf.ViewModels;
using AIMonitor.TestSupport;

namespace AIMonitor.Presentation.Wpf.Tests;

[Collection("Wpf")]
public sealed class SettingsPhase11Tests
{
    private static SettingsViewModel Create(AppSettings settings, out SettingsSession session, out BlockingSettingsStore store)
    {
        store = new BlockingSettingsStore(settings);
        store.Release();
        session = new SettingsSession(store, settings);
        return new SettingsViewModel(session, new NoopRegistrar());
    }

    private sealed class NoopRegistrar : IStartupRegistrar
    {
        public bool IsRegistered() => false;

        public void Register(string executablePath, string arguments = "")
        {
        }

        public void Unregister()
        {
        }
    }

    [Theory]
    [InlineData("180", "")]
    [InlineData("0", "")]
    [InlineData("30", "")]
    [InlineData("86400", "")]
    [InlineData(" 60 ", "")]
    public void Interval_AcceptsManualAndTheLegalRange(string text, string expected) =>
        Assert.Equal(expected, SettingsViewModel.ValidateInterval(text));

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("1.5")]
    [InlineData("29")]
    [InlineData("86401")]
    public void Interval_RejectsEverythingElse(string text) =>
        Assert.NotEqual("", SettingsViewModel.ValidateInterval(text));

    [Fact]
    public void BadInterval_BlocksSave_AndKeepsTheLastGoodValue()
    {
        var vm = Create(new AppSettings { RefreshIntervalSeconds = 180 }, out var session, out _);
        using var owned = session;

        vm.RefreshIntervalText = "12";
        Assert.True(vm.HasIntervalError);
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.Equal(180, vm.RefreshIntervalSeconds);

        vm.RefreshIntervalText = "300";
        Assert.False(vm.HasIntervalError);
        Assert.True(vm.SaveCommand.CanExecute(null));
        Assert.Equal(300, vm.RefreshIntervalSeconds);

        vm.RefreshIntervalText = "0";
        Assert.Equal(0, vm.RefreshIntervalSeconds);
    }

    [Fact]
    public async Task MonitorToggles_AreSaved_AndKeepTheProviderExtra()
    {
        var initial = new AppSettings
        {
            Providers = new Dictionary<string, ProviderPreference>(StringComparer.OrdinalIgnoreCase)
            {
                ["gemini"] = new ProviderPreference(true, "my-project"),
            },
        };
        var vm = Create(initial, out var session, out _);
        using var owned = session;

        Assert.Equal(3, vm.Providers.Count);
        Assert.All(vm.Providers, p => Assert.True(p.IsEnabled));

        vm.Providers.Single(p => p.Id == "gemini").IsEnabled = false;
        await vm.SaveAsync();

        var saved = session.Current.Providers;
        Assert.False(saved["gemini"].Enabled);
        Assert.Equal("my-project", saved["gemini"].Extra);
        Assert.True(saved["claude"].Enabled);
    }

    [Fact]
    public void DisabledProviders_AreReadBackFromSettings()
    {
        var initial = new AppSettings
        {
            Providers = new Dictionary<string, ProviderPreference>(StringComparer.OrdinalIgnoreCase)
            {
                ["openai"] = new ProviderPreference(false, ""),
            },
        };
        var vm = Create(initial, out var session, out _);
        using var owned = session;

        Assert.False(vm.Providers.Single(p => p.Id == "openai").IsEnabled);
    }
}
