using AIMonitor.Application.OpenCode;
using AIMonitor.Application.Settings;
using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.Theme;
using AIMonitor.Presentation.Wpf.ViewModels;
using AIMonitor.TestSupport;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace AIMonitor.Presentation.Wpf.Tests;

/// <summary>
/// R-020 F8 (Docs/SPEC_R020_R021.md): the OpenCode connection card. Each test names the state and quotes the
/// spec words. <c>UpdateOpenCode(OpenCodeAuthStatus?)</c> is the only way the card changes;
/// <c>Update(snapshots)</c> never touches it; the badge hue is series index 3 (set explicitly because
/// OpenCode is not in <c>ProviderMeta.All</c>); the "Sign-in help" button shows in every state.
/// </summary>
public sealed class ConnectionsViewModelOpenCodeCardTests
{
    private const string MayBeOutdatedSuffix = "  -  may be out of date (OpenCode 2.x keeps sign-ins in its database)";

    private static ConnectionsViewModel CreateViewModel()
    {
        var settings = new AppSettings();
        var store = new BlockingSettingsStore(settings);
        store.Release();
        return new ConnectionsViewModel(new SettingsSession(store, settings));
    }

    private static OpenCodeAuthStatus Status(
        bool installed = true,
        string source = "opencode auth list",
        bool mayBeOutdated = false,
        string error = "",
        params (string Name, string Kind)[] signIns) =>
        new(installed, source, signIns.Select(s => new OpenCodeSignIn(s.Name, s.Kind)).ToList(), mayBeOutdated, error);

    private static System.Windows.Media.Color Rgb(System.Windows.Media.Brush brush)
    {
        var c = ((SolidColorBrush)brush).Color;
        return System.Windows.Media.Color.FromRgb(c.R, c.G, c.B);
    }

    // ── shape ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Cards_AreClaudeOpenAiAgyOpenCode_InThatOrder_F8()
    {
        var vm = CreateViewModel();

        Assert.Equal(new[] { "claude", "openai", "agy", "opencode" }, vm.Cards.Select(c => c.Meta.Id));
        Assert.Same(vm.OpenCodeCard, vm.Cards[3]);
        Assert.Same(ProviderMeta.OpenCode, vm.OpenCodeCard.Meta);
    }

    [Fact]
    public void BadgeHue_IsSeriesIndexThree_F8()
    {
        var vm = CreateViewModel();
        var palette = ThemePalette.For(ThemeManager.Instance.IsDark);

        Assert.Equal(palette.Series(3), Rgb(vm.OpenCodeCard.BadgeBrush));
        Assert.Equal(palette.Series(3), Rgb(vm.OpenCodeCard.BadgeTint));
        // The other three keep their own indexes 0..2 (OpenCode is not in All, so it could not derive 3 itself).
        Assert.Equal(palette.Series(0), Rgb(vm.ClaudeCard.BadgeBrush));
        Assert.Equal(palette.Series(1), Rgb(vm.OpenAiCard.BadgeBrush));
        Assert.Equal(palette.Series(2), Rgb(vm.AgyCard.BadgeBrush));
        Assert.NotEqual(palette.Series(0), palette.Series(3));
    }

    // ── states ──────────────────────────────────────────────────────────────────

    [Fact]
    public void NullStatus_IsChecking_F8()
    {
        var vm = CreateViewModel();

        vm.UpdateOpenCode(null);

        Assert.Equal("Checking...", vm.OpenCodeCard.StateWord);
        Assert.Equal("–", vm.OpenCodeCard.StateGlyph);
        Assert.Equal("Sign-in help", vm.OpenCodeCard.ConnectButtonText);
    }

    [Fact]
    public void NewCard_BeforeAnyUpdate_IsChecking_WithSignInHelp_F8()
    {
        var vm = CreateViewModel();

        Assert.Equal("Checking...", vm.OpenCodeCard.StateWord);
        Assert.Equal("–", vm.OpenCodeCard.StateGlyph);
        Assert.Equal("Sign-in help", vm.OpenCodeCard.ConnectButtonText);
    }

    [Fact]
    public void NotInstalled_IsNotConnected_WithInstallGuidance_F8()
    {
        var vm = CreateViewModel();

        vm.UpdateOpenCode(Status(installed: false, source: ""));

        Assert.Equal("Not connected", vm.OpenCodeCard.StateWord);
        Assert.Equal("–", vm.OpenCodeCard.StateGlyph);
        Assert.Equal("OpenCode not found on PATH", vm.OpenCodeCard.Account);
        Assert.Equal("Install OpenCode, then run `opencode auth login`", vm.OpenCodeCard.SourceLine);
        Assert.Equal("Sign-in help", vm.OpenCodeCard.ConnectButtonText);
    }

    [Fact]
    public void InstalledWithZeroSignIns_IsNotConnected_WithLoginGuidance_F8()
    {
        var vm = CreateViewModel();

        vm.UpdateOpenCode(Status());

        Assert.Equal("Not connected", vm.OpenCodeCard.StateWord);
        Assert.Equal("–", vm.OpenCodeCard.StateGlyph);
        Assert.Equal("No providers signed in", vm.OpenCodeCard.Account);
        Assert.Equal("Run `opencode auth login` to sign in", vm.OpenCodeCard.SourceLine);
        Assert.Equal("Sign-in help", vm.OpenCodeCard.ConnectButtonText);
    }

    [Fact]
    public void SignIns_AreConnected_WithNamesAndKindsInReaderOrder_FromAuthList_F8()
    {
        var vm = CreateViewModel();

        vm.UpdateOpenCode(Status(signIns: [("Anthropic", "OAuth"), ("OpenAI", "API key")]));

        Assert.Equal("Connected", vm.OpenCodeCard.StateWord);
        Assert.Equal("✓", vm.OpenCodeCard.StateGlyph);
        Assert.Equal("Signed in: Anthropic (OAuth), OpenAI (API key)", vm.OpenCodeCard.Account);
        Assert.Equal("Detected from opencode auth list", vm.OpenCodeCard.SourceLine);
        Assert.Equal("Sign-in help", vm.OpenCodeCard.ConnectButtonText);
    }

    [Fact]
    public void SignIns_FromAuthJson_SayDetectedFromAuthJson_F8()
    {
        var vm = CreateViewModel();

        vm.UpdateOpenCode(Status(source: "auth.json", signIns: [("Custom", "Well-known")]));

        Assert.Equal("Connected", vm.OpenCodeCard.StateWord);
        Assert.Equal("Signed in: Custom (Well-known)", vm.OpenCodeCard.Account);
        Assert.Equal("Detected from auth.json", vm.OpenCodeCard.SourceLine);
    }

    [Fact]
    public void SignIns_ThatMayBeOutOfDate_AppendTheWarning_F8()
    {
        var vm = CreateViewModel();

        vm.UpdateOpenCode(Status(source: "auth.json", mayBeOutdated: true, signIns: [("Anthropic", "OAuth")]));

        Assert.Equal("Connected", vm.OpenCodeCard.StateWord);
        Assert.Equal("✓", vm.OpenCodeCard.StateGlyph);
        Assert.Equal("Signed in: Anthropic (OAuth)", vm.OpenCodeCard.Account);
        Assert.Equal("Detected from auth.json" + MayBeOutdatedSuffix, vm.OpenCodeCard.SourceLine);
    }

    [Fact]
    public void SignIns_NotOutdated_NeverShowTheWarning_F8()
    {
        var vm = CreateViewModel();

        vm.UpdateOpenCode(Status(source: "auth.json", mayBeOutdated: false, signIns: [("Anthropic", "OAuth")]));

        Assert.DoesNotContain("out of date", vm.OpenCodeCard.SourceLine, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SingleSignIn_ReadsAsNameParenKind_F8()
    {
        var vm = CreateViewModel();

        vm.UpdateOpenCode(Status(signIns: [("Anthropic", "OAuth (needs sign-in)")]));

        Assert.Equal("Signed in: Anthropic (OAuth (needs sign-in))", vm.OpenCodeCard.Account);
    }

    [Fact]
    public void Error_IsUnavailable_WithTheErrorTextAsSourceLine_F8()
    {
        var vm = CreateViewModel();
        const string error = "Could not read OpenCode sign-ins. Run `opencode auth list` in a terminal to check.";

        vm.UpdateOpenCode(Status(error: error));

        Assert.Equal("Unavailable", vm.OpenCodeCard.StateWord);
        Assert.Equal("!", vm.OpenCodeCard.StateGlyph);
        Assert.Equal("Could not check", vm.OpenCodeCard.Account);
        Assert.Equal(error, vm.OpenCodeCard.SourceLine);
        Assert.Equal("Sign-in help", vm.OpenCodeCard.ConnectButtonText);
    }

    [Fact]
    public void Transitions_FromConnectedBackToChecking_AndToNotInstalled_ReplaceEveryField_F8()
    {
        var vm = CreateViewModel();
        vm.UpdateOpenCode(Status(signIns: [("Anthropic", "OAuth")]));

        vm.UpdateOpenCode(null);
        Assert.Equal("Checking...", vm.OpenCodeCard.StateWord);
        Assert.Equal("–", vm.OpenCodeCard.StateGlyph);

        vm.UpdateOpenCode(Status(installed: false, source: ""));
        Assert.Equal("Not connected", vm.OpenCodeCard.StateWord);
        Assert.Equal("OpenCode not found on PATH", vm.OpenCodeCard.Account);
        Assert.DoesNotContain("Anthropic", vm.OpenCodeCard.Account, StringComparison.Ordinal);
        Assert.DoesNotContain("out of date", vm.OpenCodeCard.SourceLine, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateOpenCode_RaisesPropertyChanged_ForEveryDisplayedField_F8()
    {
        var vm = CreateViewModel();
        var changed = new List<string?>();
        vm.OpenCodeCard.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.UpdateOpenCode(Status(signIns: [("Anthropic", "OAuth")]));

        Assert.Contains(nameof(ConnectionCardViewModel.StateWord), changed);
        Assert.Contains(nameof(ConnectionCardViewModel.StateGlyph), changed);
        Assert.Contains(nameof(ConnectionCardViewModel.Account), changed);
        Assert.Contains(nameof(ConnectionCardViewModel.SourceLine), changed);
    }

    // ── Update(snapshots) never touches the OpenCode card ───────────────────────

    [Fact]
    public void UpdateSnapshots_NeverChangesTheOpenCodeCard_WhateverTheDictionaryHolds_F8()
    {
        var vm = CreateViewModel();
        vm.UpdateOpenCode(Status(signIns: [("Anthropic", "OAuth")]));

        vm.Update(new Dictionary<string, ProviderSnapshot>()); // no snapshots at all
        AssertStillConnected(vm);

        vm.Update(new Dictionary<string, ProviderSnapshot>
        {
            ["claude"] = new ProviderSnapshot("claude", configured: true, detection: new DetectionInfo("claude", DetectionState.Connected, "cli", "Claude CLI", "u")),
            ["openai"] = new ProviderSnapshot("openai", configured: true, detection: new DetectionInfo("openai", DetectionState.NotConnected)),
            ["agy"] = new ProviderSnapshot("agy", configured: true, detection: new DetectionInfo("agy", DetectionState.NotConnected)),
        });
        AssertStillConnected(vm);

        // A snapshot that even carries the card's own id is ignored: OpenCode has no provider snapshot.
        vm.Update(new Dictionary<string, ProviderSnapshot>
        {
            ["opencode"] = new ProviderSnapshot("opencode", configured: true, detection: new DetectionInfo("opencode", DetectionState.Expired)),
        });
        AssertStillConnected(vm);

        // An error snapshot must not turn it "Unavailable" either.
        vm.Update(new Dictionary<string, ProviderSnapshot>
        {
            ["opencode"] = new ProviderSnapshot("opencode", configured: false, error: "boom"),
        });
        AssertStillConnected(vm);
    }

    private static void AssertStillConnected(ConnectionsViewModel vm)
    {
        Assert.Equal("Connected", vm.OpenCodeCard.StateWord);
        Assert.Equal("✓", vm.OpenCodeCard.StateGlyph);
        Assert.Equal("Signed in: Anthropic (OAuth)", vm.OpenCodeCard.Account);
        Assert.Equal("Detected from opencode auth list", vm.OpenCodeCard.SourceLine);
    }

    [Fact]
    public void UpdateSnapshots_BeforeAnyOpenCodeStatus_LeavesTheCardChecking_F8()
    {
        var vm = CreateViewModel();

        vm.Update(new Dictionary<string, ProviderSnapshot>());

        Assert.Equal("Checking...", vm.OpenCodeCard.StateWord);
        Assert.Equal("–", vm.OpenCodeCard.StateGlyph);
    }

    [Fact]
    public void UpdateOpenCode_NeverChangesTheOtherThreeCards_F8()
    {
        var vm = CreateViewModel();
        vm.Update(new Dictionary<string, ProviderSnapshot>
        {
            ["claude"] = new ProviderSnapshot("claude", configured: true, detection: new DetectionInfo("claude", DetectionState.Connected, "cli", "Claude CLI", "claude-user")),
        });

        vm.UpdateOpenCode(Status(signIns: [("Anthropic", "OAuth")]));

        Assert.Equal("Connected", vm.ClaudeCard.StateWord);
        Assert.Equal("claude-user", vm.ClaudeCard.Account);
        Assert.Equal("Checking...", vm.OpenAiCard.StateWord);
        Assert.Equal("Checking...", vm.AgyCard.StateWord);
    }

    // ── H14: chip colours ───────────────────────────────────────────────────────

    private static (System.Windows.Media.Color Chip, System.Windows.Media.Color Tint) Chips(ConnectionCardViewModel card) =>
        (((SolidColorBrush)card.StateBrush).Color, ((SolidColorBrush)card.StateTint).Color);

    private static DetectionInfo Detection(string id, DetectionState state) => new(id, state, "cli", "CLI", "user");

    [Fact]
    public void ConnectedChip_HasTheSameGreenAsTheOtherCardsConnectedChip_H14()
    {
        var vm = CreateViewModel();
        vm.Update(new Dictionary<string, ProviderSnapshot>
        {
            ["claude"] = new ProviderSnapshot("claude", configured: true, detection: Detection("claude", DetectionState.Connected)),
            ["openai"] = new ProviderSnapshot("openai", configured: true, detection: Detection("openai", DetectionState.Connected)),
            ["agy"] = new ProviderSnapshot("agy", configured: true, detection: Detection("agy", DetectionState.Connected)),
        });

        vm.UpdateOpenCode(Status(signIns: [("Anthropic", "OAuth")]));

        Assert.Equal("Connected", vm.OpenCodeCard.StateWord);
        var opencode = Chips(vm.OpenCodeCard);
        foreach (var other in new[] { vm.ClaudeCard, vm.OpenAiCard, vm.AgyCard })
        {
            Assert.Equal("Connected", other.StateWord);
            Assert.Equal(Chips(other), opencode);
        }

        var good = ThemePalette.StatusGood;
        Assert.Equal(System.Windows.Media.Color.FromArgb(0xFF, good.R, good.G, good.B), opencode.Chip);
        Assert.Equal(System.Windows.Media.Color.FromArgb(0x24, good.R, good.G, good.B), opencode.Tint);
    }

    [Fact]
    public void ConnectedChip_AlsoIsGreen_WhenTheSignInsMayBeOutOfDate_H14()
    {
        var vm = CreateViewModel();
        vm.Update(new Dictionary<string, ProviderSnapshot>
        {
            ["claude"] = new ProviderSnapshot("claude", configured: true, detection: Detection("claude", DetectionState.Connected)),
        });

        vm.UpdateOpenCode(Status(source: "auth.json", mayBeOutdated: true, signIns: [("Anthropic", "OAuth")]));

        Assert.Equal(Chips(vm.ClaudeCard), Chips(vm.OpenCodeCard));
    }

    [Fact]
    public void ConnectedChip_ReturnsToTheMutedStyle_WhenTheCardLeavesConnected_H14()
    {
        var vm = CreateViewModel();
        vm.UpdateOpenCode(Status(signIns: [("Anthropic", "OAuth")]));
        var green = Chips(vm.OpenCodeCard);

        vm.UpdateOpenCode(Status());

        Assert.NotEqual(green, Chips(vm.OpenCodeCard));
    }

    [Fact]
    public void OtherStates_UseTheExistingStylingOfTheirWords_H14()
    {
        var vm = CreateViewModel();
        vm.Update(new Dictionary<string, ProviderSnapshot>
        {
            ["openai"] = new ProviderSnapshot("openai", configured: true, detection: Detection("openai", DetectionState.NotConnected)),
        });
        var notConnected = Chips(vm.OpenAiCard);                    // "Not connected"
        var checking = Chips(new ConnectionCardViewModel(ProviderMeta.Agy)); // "Checking..."
        var unavailableCard = new ConnectionCardViewModel(ProviderMeta.Agy);
        unavailableCard.SetUnavailable("x");
        var unavailable = Chips(unavailableCard);                   // "Unavailable"

        vm.UpdateOpenCode(Status(installed: false, source: ""));
        Assert.Equal("Not connected", vm.OpenCodeCard.StateWord);
        Assert.Equal(notConnected, Chips(vm.OpenCodeCard));

        vm.UpdateOpenCode(Status());
        Assert.Equal("Not connected", vm.OpenCodeCard.StateWord);
        Assert.Equal(notConnected, Chips(vm.OpenCodeCard));

        vm.UpdateOpenCode(Status(error: "Could not read OpenCode sign-ins. Run `opencode auth list` in a terminal to check."));
        Assert.Equal("Unavailable", vm.OpenCodeCard.StateWord);
        Assert.Equal(unavailable, Chips(vm.OpenCodeCard));

        vm.UpdateOpenCode(null);
        Assert.Equal("Checking...", vm.OpenCodeCard.StateWord);
        Assert.Equal(checking, Chips(vm.OpenCodeCard));
    }

    [Fact]
    public void SentinelInTheStatusSourceOrFlags_NeverReachesTheCardText_F8()
    {
        // The card shows fixed wording for the source ("auth.json" or "opencode auth list"); any other Source text from
        // the status object must never be displayed, in any state (names and kinds are the only variable text).
        const string sentinel = "SENTINEL-SOURCE-9c41d7";
        foreach (var status in new[]
                 {
                     Status(source: sentinel, signIns: [("Anthropic", "OAuth")]),
                     Status(source: sentinel, mayBeOutdated: true, signIns: [("Anthropic", "OAuth")]),
                     Status(source: sentinel),
                     Status(installed: false, source: sentinel),
                 })
        {
            var vm = CreateViewModel();

            vm.UpdateOpenCode(status);

            var text = string.Join("|", vm.OpenCodeCard.StateWord, vm.OpenCodeCard.StateGlyph, vm.OpenCodeCard.Account, vm.OpenCodeCard.SourceLine);
            Assert.DoesNotContain("9c41d7", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── buttons ─────────────────────────────────────────────────────────────────

    [Fact]
    public void SignInHelpButton_RaisesConnectRequestedWithOpenCodeId_F8_F10()
    {
        var vm = CreateViewModel();
        string? requested = null;
        vm.ConnectRequested += id => requested = id;

        vm.OpenCodeCard.ConnectCommand.Execute(null);

        Assert.Equal("opencode", requested);
    }

    [Fact]
    public void RedetectButton_RaisesRedetectRequestedWithOpenCodeId_F9()
    {
        var vm = CreateViewModel();
        string? requested = "sentinel";
        vm.RedetectRequested += id => requested = id;

        vm.OpenCodeCard.RedetectCommand.Execute(null);

        Assert.Equal("opencode", requested);
    }

    [Fact]
    public void RedetectAll_StillRaisesRedetectRequestedWithNull_F9()
    {
        var vm = CreateViewModel();
        string? requested = "sentinel";
        vm.RedetectRequested += id => requested = id;

        vm.RedetectAllCommand.Execute(null);

        Assert.Null(requested);
    }
}
