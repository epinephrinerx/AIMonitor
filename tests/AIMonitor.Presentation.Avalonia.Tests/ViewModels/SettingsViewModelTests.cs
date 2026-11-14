using AIMonitor.Application.Settings;
using AIMonitor.Presentation.Avalonia.ViewModels;

namespace AIMonitor.Presentation.Avalonia.Tests.ViewModels;

public sealed class SettingsViewModelTests
{
    private static readonly AppSettings Original = new()
    {
        Theme = "dark",
        StartWithWindows = true,
        MinimizeToTray = true,
        ShowTrayIcon = true,
        RefreshIntervalSeconds = 180,
        WidgetOpacity = 0.92,
        WidgetAlwaysOnTop = true,
        WidgetRotationEnabled = true,
        ChartRangeDays = 14,
        Providers = new Dictionary<string, ProviderPreference>(StringComparer.OrdinalIgnoreCase)
        {
            ["openai"] = new(Enabled: false),
        },
    };

    private static async Task<(SettingsViewModel Model, SettingsSession Session, InMemorySettingsStore Store)>
        BuildAsync(AppSettings? settings = null)
    {
        var store = new InMemorySettingsStore(settings ?? Original);
        var session = await SettingsSession.CreateAsync(store);
        return (new SettingsViewModel(session), session, store);
    }

    [Fact]
    public async Task OpensShowingWhatIsCurrentlySaved()
    {
        var (model, _, _) = await BuildAsync();

        Assert.Equal("dark", model.Theme);
        Assert.True(model.StartAtLogin);
        Assert.Equal("180", model.RefreshIntervalText);
        Assert.Equal(0.92, model.WidgetOpacity);
        Assert.Same(WindowSizeOption.Standard, model.SelectedWindowSize);
    }

    [Fact]
    public async Task OpensWithEachServicesMonitorSwitchAsItWasLeft()
    {
        var (model, _, _) = await BuildAsync();

        Assert.True(model.Providers.Single(p => p.ProviderId == "claude").Enabled);
        Assert.False(model.Providers.Single(p => p.ProviderId == "openai").Enabled);
    }

    [Fact]
    public async Task Cancel_PutsEveryFieldBack()
    {
        var (model, session, store) = await BuildAsync();

        model.Theme = "light";
        model.StartAtLogin = false;
        model.MinimizeToMenuBar = false;
        model.ShowMenuBarExtra = false;
        model.RefreshIntervalText = "600";
        model.WidgetOpacity = 0.4;
        model.WidgetAlwaysOnTop = false;
        model.WidgetRotationEnabled = false;
        model.SelectedWindowSize = WindowSizeOption.Wide;
        foreach (var toggle in model.Providers)
        {
            toggle.Enabled = !toggle.Enabled;
        }

        model.Cancel();

        Assert.Equal("dark", model.Theme);
        Assert.True(model.StartAtLogin);
        Assert.True(model.MinimizeToMenuBar);
        Assert.True(model.ShowMenuBarExtra);
        Assert.Equal("180", model.RefreshIntervalText);
        Assert.Equal(0.92, model.WidgetOpacity);
        Assert.True(model.WidgetAlwaysOnTop);
        Assert.True(model.WidgetRotationEnabled);
        Assert.Same(WindowSizeOption.Standard, model.SelectedWindowSize);
        Assert.True(model.Providers.Single(p => p.ProviderId == "claude").Enabled);
        Assert.False(model.Providers.Single(p => p.ProviderId == "openai").Enabled);

        // Nothing was written on the way, either: the page edits a copy until Save.
        Assert.Equal(0, store.SaveCalls);
        Assert.Equal("dark", session.Current.Theme);
    }

    [Fact]
    public async Task Cancel_ClosesWithoutSaving()
    {
        var (model, _, _) = await BuildAsync();
        bool? saved = null;
        model.RequestClose += result => saved = result;

        model.Cancel();

        Assert.False(saved);
    }

    [Fact]
    public async Task EditingWithoutSaving_ChangesNothingThatIsStored()
    {
        var (model, session, store) = await BuildAsync();

        model.Theme = "light";
        model.RefreshIntervalText = "600";

        Assert.Equal(0, store.SaveCalls);
        Assert.Equal("dark", session.Current.Theme);
        Assert.Equal(180, session.Current.RefreshIntervalSeconds);
    }

    [Fact]
    public async Task Save_WritesEveryFieldAtOnce()
    {
        var (model, session, _) = await BuildAsync();

        model.Theme = "light";
        model.StartAtLogin = false;
        model.MinimizeToMenuBar = false;
        model.ShowMenuBarExtra = false;
        model.RefreshIntervalText = "600";
        model.WidgetOpacity = 0.4;
        model.WidgetAlwaysOnTop = false;
        model.WidgetRotationEnabled = false;
        model.SelectedWindowSize = WindowSizeOption.Compact;
        await model.SaveAsync();
        await session.FlushAsync();

        var saved = session.Current;
        Assert.Equal("light", saved.Theme);
        Assert.False(saved.StartWithWindows);
        Assert.False(saved.MinimizeToTray);
        Assert.False(saved.ShowTrayIcon);
        Assert.Equal(600, saved.RefreshIntervalSeconds);
        Assert.Equal(0.4, saved.WidgetOpacity);
        Assert.False(saved.WidgetAlwaysOnTop);
        Assert.False(saved.WidgetRotationEnabled);
        Assert.Equal((960, 680), (saved.DashboardWidth, saved.DashboardHeight));
    }

    [Fact]
    public async Task Save_KeepsTheKeyAndBudgetAlreadyStoredForAService()
    {
        var settings = Original with
        {
            Providers = new Dictionary<string, ProviderPreference>(StringComparer.OrdinalIgnoreCase)
            {
                ["openai"] = new(Enabled: true, Extra: "120"),
            },
        };
        var (model, session, _) = await BuildAsync(settings);

        model.Providers.Single(p => p.ProviderId == "openai").Enabled = false;
        await model.SaveAsync();
        await session.FlushAsync();

        // Switching monitoring off must not throw away the budget the user typed on another page.
        var preference = session.Current.Providers["openai"];
        Assert.False(preference.Enabled);
        Assert.Equal("120", preference.Extra);
    }

    [Fact]
    public async Task Save_ClosesThePage()
    {
        var (model, _, _) = await BuildAsync();
        bool? saved = null;
        model.RequestClose += result => saved = result;

        await model.SaveAsync();

        Assert.True(saved);
    }

    [Theory]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("-5")]
    [InlineData("10")]
    [InlineData("90000")]
    public async Task AnIntervalThatWouldNotWork_IsRefusedWithAReason(string typed)
    {
        var (model, _, _) = await BuildAsync();

        model.RefreshIntervalText = typed;

        Assert.True(model.HasIntervalError);
        Assert.NotEmpty(model.RefreshIntervalError);
        Assert.False(model.SaveCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("30")]
    [InlineData("180")]
    [InlineData(" 600 ")]
    public async Task AWorkableIntervalIsAccepted(string typed)
    {
        var (model, _, _) = await BuildAsync();

        model.RefreshIntervalText = typed;

        Assert.False(model.HasIntervalError);
        Assert.True(model.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task Save_WithABadInterval_WritesNothing()
    {
        var (model, _, store) = await BuildAsync();

        model.RefreshIntervalText = "not a number";
        await model.SaveAsync();

        Assert.Equal(0, store.SaveCalls);
    }

    [Fact]
    public async Task OpacityPercent_FollowsTheSlider()
    {
        var (model, _, _) = await BuildAsync();

        model.WidgetOpacity = 0.455;

        Assert.Equal(46, model.OpacityPercent);
    }

    // ---- Parity with the Windows settings page (PAR-026/027) ----

    private static async Task<(SettingsViewModel Model, SettingsSession Session, List<AppSettings> Previews)> BuildWithPreviewAsync()
    {
        var session = await SettingsSession.CreateAsync(new InMemorySettingsStore(Original));
        var previews = new List<AppSettings>();
        return (new SettingsViewModel(session, applyPreview: previews.Add), session, previews);
    }

    [Fact]
    public async Task Opening_PreviewsNothing()
    {
        var (_, _, previews) = await BuildWithPreviewAsync();

        Assert.Empty(previews);
    }

    [Fact]
    public async Task AppearanceEdits_ArePreviewedAtOnce_WithoutSaving()
    {
        var (model, session, previews) = await BuildWithPreviewAsync();

        model.Theme = "light";
        model.WidgetOpacity = 0.5;
        model.WidgetAlwaysOnTop = false;
        model.SelectedWindowSize = WindowSizeOption.Wide;

        var last = previews[^1];
        Assert.Equal("light", last.Theme);
        Assert.Equal(0.5, last.WidgetOpacity);
        Assert.False(last.WidgetAlwaysOnTop);
        Assert.Equal((1400, 900), (last.DashboardWidth, last.DashboardHeight));
        Assert.Equal("dark", session.Current.Theme);
    }

    [Fact]
    public async Task Cancel_PreviewsTheSettingsThePageOpenedWith()
    {
        var (model, _, previews) = await BuildWithPreviewAsync();
        model.Theme = "light";
        model.SelectedWindowSize = WindowSizeOption.Compact;

        model.Cancel();

        var last = previews[^1];
        Assert.Equal("dark", last.Theme);
        Assert.Equal((1120, 820), (last.DashboardWidth, last.DashboardHeight));
    }

    [Fact]
    public async Task ClosingAfterSave_DoesNotUndoWhatWasSaved()
    {
        var (model, _, previews) = await BuildWithPreviewAsync();
        model.Theme = "light";
        await model.SaveAsync();
        var count = previews.Count;

        model.Discard();

        Assert.Equal(count, previews.Count);
    }

    [Fact]
    public async Task ThemeChoices_UseTheirMacNames()
    {
        var (model, _, _) = await BuildAsync();

        Assert.Equal(["Follow macOS · default", "Light", "Dark"], model.ThemeOptions.Select(option => option.Label));
        Assert.Equal("dark", model.SelectedThemeOption.Value);
        model.SelectedThemeOption = model.ThemeOptions[0];
        Assert.Equal("system", model.Theme);
    }

    [Fact]
    public async Task Opacity_IsHeldAtTheSameFloorAsTheSettingsFile()
    {
        var (model, _, _) = await BuildAsync();

        model.WidgetOpacity = 0.1;

        Assert.Equal(SettingsViewModel.MinimumOpacity, model.WidgetOpacity);
    }

    [Fact]
    public async Task EachServiceSwitch_SaysWhatTheServiceOffers()
    {
        var (model, _, _) = await BuildAsync();

        Assert.Equal("Claude Code · full quota · optional API usage", model.Providers.Single(p => p.ProviderId == "claude").Hint);
        Assert.All(model.Providers, toggle => Assert.True(toggle.HasHint));
    }

    [Fact]
    public async Task AFailedSave_IsShownOnThePage_AndTheWindowStaysOpen()
    {
        var store = new InMemorySettingsStore(Original) { FailSaves = true };
        var session = await SettingsSession.CreateAsync(store);
        var model = new SettingsViewModel(session);
        bool? closed = null;
        model.RequestClose += result => closed = result;

        model.Theme = "light";
        await model.SaveAsync();

        Assert.True(model.HasSaveError);
        Assert.StartsWith("Could not save your settings", model.SaveError, StringComparison.Ordinal);
        Assert.Null(closed);
    }
}
