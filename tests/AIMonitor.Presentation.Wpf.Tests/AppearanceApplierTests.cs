using AIMonitor.Application.Settings;
using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf.Tests;

[Collection("Wpf")]
public sealed class AppearanceApplierTests
{
    [Fact]
    public void Apply_Theme_AlwaysCallsOnFirstApply_AndOnlyWhenChangedThereafter()
    {
        var appliedThemes = new List<string>();
        var applier = new AppearanceApplier(
            applyTheme: theme => appliedThemes.Add(theme),
            widget: null,
            resizeDashboard: (_, _) => { },
            initialWidth: 1120,
            initialHeight: 820);

        // First apply with "system": must be called
        applier.Apply(new AppSettings { Theme = "system" });
        Assert.Single(appliedThemes);
        Assert.Equal("system", appliedThemes[0]);

        // Second apply with identical theme ("system"): must NOT be called again
        applier.Apply(new AppSettings { Theme = "system" });
        Assert.Single(appliedThemes);

        // Third apply with changed theme ("dark"): must be called
        applier.Apply(new AppSettings { Theme = "dark" });
        Assert.Equal(2, appliedThemes.Count);
        Assert.Equal("dark", appliedThemes[1]);

        // Fourth apply with identical theme ("dark"): must NOT be called again
        applier.Apply(new AppSettings { Theme = "dark" });
        Assert.Equal(2, appliedThemes.Count);

        // Fifth apply with changed theme ("light"): must be called
        applier.Apply(new AppSettings { Theme = "light" });
        Assert.Equal(3, appliedThemes.Count);
        Assert.Equal("light", appliedThemes[2]);
    }

    [Fact]
    public void Apply_Widget_InvokesApplySettingsOnWidgetOnEveryCall()
    {
        var tab = new ProviderTabViewModel("claude", "Claude");
        var widgetVm = new WidgetViewModel([tab]);

        var applier = new AppearanceApplier(
            applyTheme: _ => { },
            widget: widgetVm,
            resizeDashboard: (_, _) => { },
            initialWidth: 1120,
            initialHeight: 820);

        // First apply
        applier.Apply(new AppSettings
        {
            WidgetOpacity = 0.55,
            WidgetAlwaysOnTop = false
        });

        Assert.Equal(0.55, widgetVm.Opacity);
        Assert.False(widgetVm.AlwaysOnTop);

        // Second apply
        applier.Apply(new AppSettings
        {
            WidgetOpacity = 0.85,
            WidgetAlwaysOnTop = true
        });

        Assert.Equal(0.85, widgetVm.Opacity);
        Assert.True(widgetVm.AlwaysOnTop);
    }

    [Fact]
    public void Apply_Widget_WhenNull_ToleratesNullWithoutThrowing()
    {
        var themeCallCount = 0;
        var applier = new AppearanceApplier(
            applyTheme: _ => themeCallCount++,
            widget: null,
            resizeDashboard: (_, _) => { },
            initialWidth: 1120,
            initialHeight: 820);

        var exception = Record.Exception(() => applier.Apply(new AppSettings { Theme = "dark" }));
        Assert.Null(exception);
        Assert.Equal(1, themeCallCount);
    }

    [Fact]
    public void Apply_ResizeDashboard_OnlyWhenChangedAndNotZeroByZero()
    {
        var resizeCalls = new List<(int Width, int Height)>();
        var applier = new AppearanceApplier(
            applyTheme: _ => { },
            widget: null,
            resizeDashboard: (w, h) => resizeCalls.Add((w, h)),
            initialWidth: 0,
            initialHeight: 0);

        // 1. First apply with non-zero size (960, 680): must be called
        applier.Apply(new AppSettings { DashboardWidth = 960, DashboardHeight = 680 });
        Assert.Single(resizeCalls);
        Assert.Equal((960, 680), resizeCalls[0]);

        // 2. Second apply with identical size (960, 680): must NOT be called again
        applier.Apply(new AppSettings { DashboardWidth = 960, DashboardHeight = 680 });
        Assert.Single(resizeCalls);

        // 3. Third apply with changed non-zero size (1400, 900): must be called
        applier.Apply(new AppSettings { DashboardWidth = 1400, DashboardHeight = 900 });
        Assert.Equal(2, resizeCalls.Count);
        Assert.Equal((1400, 900), resizeCalls[1]);

        // 4. Fourth apply with (0, 0): must NOT be called (is 0x0)
        applier.Apply(new AppSettings { DashboardWidth = 0, DashboardHeight = 0 });
        Assert.Equal(2, resizeCalls.Count);

        // 5. Fifth apply with identical (0, 0): must NOT be called
        applier.Apply(new AppSettings { DashboardWidth = 0, DashboardHeight = 0 });
        Assert.Equal(2, resizeCalls.Count);

        // 6. Sixth apply with changed non-zero size (1400, 900): must be called
        applier.Apply(new AppSettings { DashboardWidth = 1400, DashboardHeight = 900 });
        Assert.Equal(3, resizeCalls.Count);
        Assert.Equal((1400, 900), resizeCalls[2]);
    }

    [Fact]
    public void Apply_ResizeDashboard_DoesNotResizeOnFirstApplyIfZeroByZero()
    {
        var resizeCalls = new List<(int Width, int Height)>();
        var applier = new AppearanceApplier(
            applyTheme: _ => { },
            widget: null,
            resizeDashboard: (w, h) => resizeCalls.Add((w, h)),
            initialWidth: 0,
            initialHeight: 0);

        // First apply with (0, 0)
        applier.Apply(new AppSettings { DashboardWidth = 0, DashboardHeight = 0 });
        Assert.Empty(resizeCalls);

        // Subsequent apply with non-zero size (1120, 820): called
        applier.Apply(new AppSettings { DashboardWidth = 1120, DashboardHeight = 820 });
        Assert.Single(resizeCalls);
        Assert.Equal((1120, 820), resizeCalls[0]);
    }

    [Fact]
    public void Constructor_NullArguments_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new AppearanceApplier(null!, null, (_, _) => { }, 1120, 820));
        Assert.Throws<ArgumentNullException>(() => new AppearanceApplier(_ => { }, null, null!, 1120, 820));
    }

    [Fact]
    public void Apply_NullSettings_ThrowsArgumentNullException()
    {
        var applier = new AppearanceApplier(_ => { }, null, (_, _) => { }, 1120, 820);
        Assert.Throws<ArgumentNullException>(() => applier.Apply(null!));
    }

    [Fact]
    public void Apply_FirstApply_WithOnlyThemeChangedAndSameSizeAsInitial_DoesNotResize()
    {
        var resizeCalls = new List<(int Width, int Height)>();
        var applier = new AppearanceApplier(
            applyTheme: _ => { },
            widget: null,
            resizeDashboard: (w, h) => resizeCalls.Add((w, h)),
            initialWidth: 1120,
            initialHeight: 820);

        // (1) First Apply with only the theme changed and the same size as the initial -> resize NOT called
        applier.Apply(new AppSettings
        {
            Theme = "dark",
            DashboardWidth = 1120,
            DashboardHeight = 820
        });

        Assert.Empty(resizeCalls);
    }

    [Fact]
    public void Apply_ChangeSizeToWide_CallsResizeOnceWith1400x900()
    {
        var resizeCalls = new List<(int Width, int Height)>();
        var applier = new AppearanceApplier(
            applyTheme: _ => { },
            widget: null,
            resizeDashboard: (w, h) => resizeCalls.Add((w, h)),
            initialWidth: 1120,
            initialHeight: 820);

        // First apply with same size as initial
        applier.Apply(new AppSettings
        {
            Theme = "dark",
            DashboardWidth = 1120,
            DashboardHeight = 820
        });

        // (2) Change size to Wide -> resize called once with 1400x900
        applier.Apply(new AppSettings
        {
            Theme = "dark",
            DashboardWidth = 1400,
            DashboardHeight = 900
        });

        Assert.Single(resizeCalls);
        Assert.Equal((1400, 900), resizeCalls[0]);
    }

    [Fact]
    public void Apply_SameWideAgain_DoesNotCallResizeAgain()
    {
        var resizeCalls = new List<(int Width, int Height)>();
        var applier = new AppearanceApplier(
            applyTheme: _ => { },
            widget: null,
            resizeDashboard: (w, h) => resizeCalls.Add((w, h)),
            initialWidth: 1120,
            initialHeight: 820);

        // First apply with same size as initial
        applier.Apply(new AppSettings
        {
            Theme = "dark",
            DashboardWidth = 1120,
            DashboardHeight = 820
        });

        // Change size to Wide
        applier.Apply(new AppSettings
        {
            Theme = "dark",
            DashboardWidth = 1400,
            DashboardHeight = 900
        });
        Assert.Single(resizeCalls);

        // (3) Apply the same Wide again -> not called again
        applier.Apply(new AppSettings
        {
            Theme = "dark",
            DashboardWidth = 1400,
            DashboardHeight = 900
        });

        Assert.Single(resizeCalls);
        Assert.Equal((1400, 900), resizeCalls[0]);
    }

    [Fact]
    public void Apply_ChangeToZeroZeroRememberLastSize_NotCalled_AndFollowingChangeBackToStandard_Called()
    {
        var resizeCalls = new List<(int Width, int Height)>();
        var applier = new AppearanceApplier(
            applyTheme: _ => { },
            widget: null,
            resizeDashboard: (w, h) => resizeCalls.Add((w, h)),
            initialWidth: 1120,
            initialHeight: 820);

        // First apply with same size as initial
        applier.Apply(new AppSettings
        {
            Theme = "dark",
            DashboardWidth = 1120,
            DashboardHeight = 820
        });

        // (4) Change to (0,0) "Remember last size" -> not called
        applier.Apply(new AppSettings
        {
            Theme = "dark",
            DashboardWidth = 0,
            DashboardHeight = 0
        });
        Assert.Empty(resizeCalls);

        // Following change back to Standard -> called
        applier.Apply(new AppSettings
        {
            Theme = "dark",
            DashboardWidth = 1120,
            DashboardHeight = 820
        });

        Assert.Single(resizeCalls);
        Assert.Equal((1120, 820), resizeCalls[0]);
    }

    [Fact]
    public void Apply_WindowSizeLifecycle_SeededWithInitialSize_CompliesWith133Parity()
    {
        var resizeCalls = new List<(int Width, int Height)>();
        var applier = new AppearanceApplier(
            applyTheme: _ => { },
            widget: null,
            resizeDashboard: (w, h) => resizeCalls.Add((w, h)),
            initialWidth: 1120,
            initialHeight: 820);

        // (1) First Apply with only the theme changed and the same size as the initial -> resize NOT called
        applier.Apply(new AppSettings { Theme = "dark", DashboardWidth = 1120, DashboardHeight = 820 });
        Assert.Empty(resizeCalls);

        // (2) Change size to Wide -> resize called once with 1400x900
        applier.Apply(new AppSettings { Theme = "dark", DashboardWidth = 1400, DashboardHeight = 900 });
        Assert.Single(resizeCalls);
        Assert.Equal((1400, 900), resizeCalls[0]);

        // (3) Apply the same Wide again -> not called again
        applier.Apply(new AppSettings { Theme = "dark", DashboardWidth = 1400, DashboardHeight = 900 });
        Assert.Single(resizeCalls);

        // (4) Change to (0,0) "Remember last size" -> not called, and a following change back to Standard -> called
        applier.Apply(new AppSettings { Theme = "dark", DashboardWidth = 0, DashboardHeight = 0 });
        Assert.Single(resizeCalls);

        applier.Apply(new AppSettings { Theme = "dark", DashboardWidth = 1120, DashboardHeight = 820 });
        Assert.Equal(2, resizeCalls.Count);
        Assert.Equal((1120, 820), resizeCalls[1]);
    }
}
