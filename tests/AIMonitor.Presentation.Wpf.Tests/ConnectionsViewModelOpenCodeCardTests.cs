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

    // ── J3: the chip notifications are raised after StateWord has its final value ─

    public enum CardTarget
    {
        Checking,
        NotInstalled,
        ZeroSignIns,
        Error,
        Connected,
    }

    private static void Apply(ConnectionsViewModel vm, CardTarget target)
    {
        switch (target)
        {
            case CardTarget.Checking:
                vm.UpdateOpenCode(null);
                break;
            case CardTarget.NotInstalled:
                vm.UpdateOpenCode(Status(installed: false, source: ""));
                break;
            case CardTarget.ZeroSignIns:
                vm.UpdateOpenCode(Status());
                break;
            case CardTarget.Error:
                vm.UpdateOpenCode(Status(error: "Could not read OpenCode sign-ins. Run `opencode auth list` in a terminal to check."));
                break;
            default:
                vm.UpdateOpenCode(Status(signIns: [("Anthropic", "OAuth")]));
                break;
        }
    }

    private static string WordOf(CardTarget target) => target switch
    {
        CardTarget.Checking => "Checking...",
        CardTarget.NotInstalled => "Not connected",
        CardTarget.ZeroSignIns => "Not connected",
        CardTarget.Error => "Unavailable",
        _ => "Connected",
    };

    /// <summary>The colours a card in that state must show, taken from independent references (the Connected green
    /// from the palette; every other state from a card of another provider that really is in that state).</summary>
    private static (System.Windows.Media.Color Chip, System.Windows.Media.Color Tint) ExpectedChips(CardTarget target)
    {
        switch (target)
        {
            case CardTarget.Connected:
                var good = ThemePalette.StatusGood;
                return (
                    System.Windows.Media.Color.FromArgb(0xFF, good.R, good.G, good.B),
                    System.Windows.Media.Color.FromArgb(0x24, good.R, good.G, good.B));
            case CardTarget.Checking:
                return Chips(new ConnectionCardViewModel(ProviderMeta.Agy));
            case CardTarget.Error:
                var unavailable = new ConnectionCardViewModel(ProviderMeta.Agy);
                unavailable.SetUnavailable("x");
                return Chips(unavailable);
            default:
                var vm = CreateViewModel();
                vm.Update(new Dictionary<string, ProviderSnapshot>
                {
                    ["openai"] = new ProviderSnapshot("openai", configured: true, detection: Detection("openai", DetectionState.NotConnected)),
                });
                return Chips(vm.OpenAiCard);
        }
    }

    [Theory]
    [InlineData(CardTarget.Connected, CardTarget.Checking)] // UpdateOpenCode(null)
    [InlineData(CardTarget.Connected, CardTarget.NotInstalled)]
    [InlineData(CardTarget.Connected, CardTarget.ZeroSignIns)]
    [InlineData(CardTarget.Connected, CardTarget.Error)]
    [InlineData(CardTarget.Checking, CardTarget.Connected)]
    [InlineData(CardTarget.NotInstalled, CardTarget.Connected)]
    [InlineData(CardTarget.ZeroSignIns, CardTarget.Connected)]
    [InlineData(CardTarget.Error, CardTarget.Connected)]
    [InlineData(CardTarget.Error, CardTarget.NotInstalled)]
    [InlineData(CardTarget.NotInstalled, CardTarget.Error)]
    public void ChipNotifications_ReadTheFinalColour_InsideTheirHandlers_J3(CardTarget from, CardTarget to)
    {
        var vm = CreateViewModel();
        Apply(vm, from); // a new card is already "Checking..."
        var card = vm.OpenCodeCard;
        Assert.Equal(WordOf(from), card.StateWord);

        var seen = new List<(string Property, string Word, System.Windows.Media.Color Colour)>();
        card.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConnectionCardViewModel.StateBrush))
            {
                seen.Add((e.PropertyName, card.StateWord, ((SolidColorBrush)card.StateBrush).Color));
            }
            else if (e.PropertyName == nameof(ConnectionCardViewModel.StateTint))
            {
                seen.Add((e.PropertyName, card.StateWord, ((SolidColorBrush)card.StateTint).Color));
            }
        };

        Apply(vm, to);

        var expected = ExpectedChips(to);
        Assert.Equal(WordOf(to), card.StateWord);
        Assert.Equal(expected, Chips(card));

        // The value a bound view reads in the LAST notification of each property is the final colour: the
        // notification must not be raised before StateWord (which the colour derives from) has its final value.
        var lastBrush = seen.Last(x => x.Property == nameof(ConnectionCardViewModel.StateBrush));
        var lastTint = seen.Last(x => x.Property == nameof(ConnectionCardViewModel.StateTint));
        Assert.Equal(expected.Chip, lastBrush.Colour);
        Assert.Equal(expected.Tint, lastTint.Colour);
        Assert.Equal(WordOf(to), lastBrush.Word);
        Assert.Equal(WordOf(to), lastTint.Word);
    }

    // ── sentinels through UpdateOpenCode (F8: what the card may show verbatim) ───

    private const string SentinelSource = "SENTINEL-SOURCE-9c41d7";
    private const string SentinelName = "SENTINEL-NAME-9c41d7";
    private const string SentinelKind = "SENTINEL-KIND-9c41d7";
    private const string SentinelError = "SENTINEL-ERROR-9c41d7";

    private static string[] Displayed(ConnectionCardViewModel card) =>
        [card.StateWord, card.StateGlyph, card.Account, card.SourceLine, card.ConnectButtonText, card.Meta.DisplayName, card.Meta.Tagline];

    private static void AssertNoSentinel(ConnectionCardViewModel card, params string[] forbidden)
    {
        foreach (var text in Displayed(card))
        {
            foreach (var needle in forbidden)
            {
                Assert.DoesNotContain(needle, text, StringComparison.Ordinal);
            }
        }
    }

    // F8 says exactly which status fields reach the card verbatim:
    //  - Account: "Signed in: " + `Name (Kind)` joined with ", " (names and kinds, verbatim, in reader order);
    //  - SourceLine: "Detected from auth.json" / "Detected from opencode auth list" (+ the fixed outdated suffix), chosen
    //    from Source but never containing it; in the Error state SourceLine = Error (the one verbatim string).
    // Everything else (Source text, Error outside the error state, flags) never appears.

    [Fact]
    public void Connected_ShowsNamesAndKindsVerbatim_ButNeverTheSourceText_NorTheErrorText_F8()
    {
        var vm = CreateViewModel();

        // A hand-built status with a sentinel in EVERY string field. Error is empty in the connected state by F5;
        // the sentinel in Source must not be shown, and the state is decided without it.
        vm.UpdateOpenCode(new OpenCodeAuthStatus(true, SentinelSource, [new OpenCodeSignIn(SentinelName, SentinelKind)], false, ""));

        Assert.Equal("Connected", vm.OpenCodeCard.StateWord);
        Assert.Equal($"Signed in: {SentinelName} ({SentinelKind})", vm.OpenCodeCard.Account); // verbatim by spec
        Assert.Equal("Detected from opencode auth list", vm.OpenCodeCard.SourceLine);          // fixed text, not Source
        AssertNoSentinel(vm.OpenCodeCard, SentinelSource, SentinelError);
    }

    [Fact]
    public void Connected_FromAuthJson_ShowsFixedSourceWording_NotTheSourceField_F8()
    {
        var vm = CreateViewModel();

        vm.UpdateOpenCode(new OpenCodeAuthStatus(true, "auth.json", [new OpenCodeSignIn(SentinelName, SentinelKind)], true, ""));

        Assert.Equal("Detected from auth.json" + MayBeOutdatedSuffix, vm.OpenCodeCard.SourceLine);
        AssertNoSentinel(vm.OpenCodeCard, SentinelSource, SentinelError);
    }

    [Fact]
    public void ZeroSignIns_ShowsOnlyFixedWording_WhateverSourceSays_F8()
    {
        var vm = CreateViewModel();

        vm.UpdateOpenCode(new OpenCodeAuthStatus(true, SentinelSource, [], true, ""));

        Assert.Equal("Not connected", vm.OpenCodeCard.StateWord);
        Assert.Equal("No providers signed in", vm.OpenCodeCard.Account);
        Assert.Equal("Run `opencode auth login` to sign in", vm.OpenCodeCard.SourceLine);
        AssertNoSentinel(vm.OpenCodeCard, SentinelSource, SentinelError, SentinelName, SentinelKind);
    }

    [Fact]
    public void NotInstalled_ShowsOnlyFixedWording_WhateverSourceAndSignInsSay_F8()
    {
        var vm = CreateViewModel();

        vm.UpdateOpenCode(new OpenCodeAuthStatus(false, SentinelSource, [new OpenCodeSignIn(SentinelName, SentinelKind)], true, ""));

        Assert.Equal("Not connected", vm.OpenCodeCard.StateWord);
        Assert.Equal("OpenCode not found on PATH", vm.OpenCodeCard.Account);
        Assert.Equal("Install OpenCode, then run `opencode auth login`", vm.OpenCodeCard.SourceLine);
        AssertNoSentinel(vm.OpenCodeCard, SentinelSource, SentinelName, SentinelKind);
    }

    [Fact]
    public void ErrorState_ShowsTheErrorTextAsSourceLine_AndNothingElseFromTheStatus_F8()
    {
        var vm = CreateViewModel();

        // F8: Error -> "Unavailable" / "!", Account "Could not check", SourceLine = Error (the only verbatim string
        // in this state). The reader guarantees Error is the fixed sentence; the card shows what it is given.
        vm.UpdateOpenCode(new OpenCodeAuthStatus(true, SentinelSource, [new OpenCodeSignIn(SentinelName, SentinelKind)], true, SentinelError));

        Assert.Equal("Unavailable", vm.OpenCodeCard.StateWord);
        Assert.Equal("!", vm.OpenCodeCard.StateGlyph);
        Assert.Equal("Could not check", vm.OpenCodeCard.Account);
        Assert.Equal(SentinelError, vm.OpenCodeCard.SourceLine);
        AssertNoSentinel(vm.OpenCodeCard, SentinelSource, SentinelName, SentinelKind);
        Assert.DoesNotContain(SentinelError, vm.OpenCodeCard.Account, StringComparison.Ordinal);
        Assert.DoesNotContain(SentinelError, vm.OpenCodeCard.StateWord, StringComparison.Ordinal);
    }

    [Fact]
    public void NoStateEverShowsTheSourceFieldOrASignInInTheStateWordGlyphOrButton_F8()
    {
        // Across every state the chip word, glyph and button text are fixed vocabulary.
        var statuses = new[]
        {
            new OpenCodeAuthStatus(true, SentinelSource, [new OpenCodeSignIn(SentinelName, SentinelKind)], false, ""),
            new OpenCodeAuthStatus(true, SentinelSource, [], false, ""),
            new OpenCodeAuthStatus(false, SentinelSource, [], false, ""),
            new OpenCodeAuthStatus(true, SentinelSource, [new OpenCodeSignIn(SentinelName, SentinelKind)], false, SentinelError),
        };
        string[] words = ["Connected", "Not connected", "Unavailable"];
        foreach (var status in statuses)
        {
            var vm = CreateViewModel();

            vm.UpdateOpenCode(status);

            Assert.Contains(vm.OpenCodeCard.StateWord, words);
            Assert.Contains(vm.OpenCodeCard.StateGlyph, new[] { "✓", "–", "!" });
            Assert.Equal("Sign-in help", vm.OpenCodeCard.ConnectButtonText);
            Assert.DoesNotContain(SentinelSource, string.Concat(Displayed(vm.OpenCodeCard)), StringComparison.Ordinal);
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
