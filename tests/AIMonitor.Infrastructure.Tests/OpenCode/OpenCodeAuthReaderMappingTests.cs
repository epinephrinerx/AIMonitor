namespace AIMonitor.Infrastructure.Tests.OpenCode;

/// <summary>
/// R-020 F3 (OpenCode 2.x JSON mapping), F4 (OpenCode 1.x auth.json mapping) and F5b (display-name
/// allow-list), Docs/SPEC_R020_R021.md. Each oracle names the spec row it comes from.
/// A SignIn is "Name|Kind" in reader order (the order OpenCode printed / the file's property order).
/// </summary>
public sealed class OpenCodeAuthReaderMappingTests
{
    private static string Provider(string name, params string[] connections) =>
        $$"""{"id":"id-{{name}}","name":"{{name}}","connections":[{{string.Join(",", connections)}}]}""";

    private static string Credential(string method, string? status = null) =>
        status is null
            ? $$"""{"type":"credential","id":"cred_1","label":"main","method":"{{method}}"}"""
            : $$"""{"type":"credential","id":"cred_1","label":"main","method":"{{method}}","status":{"status":"{{status}}","message":"m"} }""";

    private static async Task<string[]> TwoXPairs(string json)
    {
        var h = new ReaderHarness();
        h.UseTwoX(json);
        var status = await h.ReadAsync();
        Assert.Equal("opencode auth list", status.Source); // the 2.x path was used, not the fallback
        return ReaderHarness.Pairs(status);
    }

    private static async Task<string[]> OneXPairs(string authJson)
    {
        var h = new ReaderHarness();
        h.SetVersion("1.19.5");
        h.SetAuthJson(authJson);
        var status = await h.ReadAsync();
        Assert.True(string.IsNullOrEmpty(status.Error));
        return ReaderHarness.Pairs(status);
    }

    // ── F3: connection kinds ────────────────────────────────────────────────────

    [Theory]
    [InlineData("oauth", "OAuth")]
    [InlineData("key", "API key")]
    [InlineData("external", "External")]
    [InlineData("something-new", "Other")]
    [InlineData("", "Other")]
    public async Task TwoX_CredentialMethod_MapsToKind_F3(string method, string kind)
    {
        var pairs = await TwoXPairs("[" + Provider("Anthropic", Credential(method)) + "]");

        Assert.Equal(new[] { "Anthropic|" + kind }, pairs);
    }

    [Fact]
    public async Task TwoX_CredentialWithoutMethod_IsOther_F3()
    {
        var pairs = await TwoXPairs("""[{"id":"a","name":"Anthropic","connections":[{"type":"credential","id":"cred_1"}]}]""");

        Assert.Equal(new[] { "Anthropic|Other" }, pairs);
    }

    [Theory]
    [InlineData("oauth", "OAuth (needs sign-in)")]
    [InlineData("key", "API key (needs sign-in)")]
    [InlineData("external", "External (needs sign-in)")]
    [InlineData("other-method", "Other (needs sign-in)")]
    public async Task TwoX_NeedsAuthStatus_AppendsNeedsSignIn_F3(string method, string kind)
    {
        var pairs = await TwoXPairs("[" + Provider("Anthropic", Credential(method, "needs_auth")) + "]");

        Assert.Equal(new[] { "Anthropic|" + kind }, pairs);
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("connected")]
    public async Task TwoX_AnyOtherStatus_AddsNothing_F3(string status)
    {
        var pairs = await TwoXPairs("[" + Provider("Anthropic", Credential("oauth", status)) + "]");

        Assert.Equal(new[] { "Anthropic|OAuth" }, pairs);
    }

    [Fact]
    public async Task TwoX_EnvConnection_IsKindEnvironment_F3_F5b()
    {
        var pairs = await TwoXPairs("""[{"id":"a","name":"OpenAI","connections":[{"type":"env","name":"OPENAI_API_KEY"}]}]""");

        Assert.Equal(new[] { "OpenAI|Environment" }, pairs);
    }

    [Fact]
    public async Task TwoX_SeveralProvidersAndConnections_KeepReaderOrder_F3()
    {
        var json = "[" + string.Join(
            ",",
            Provider("Anthropic", Credential("oauth"), Credential("key")),
            Provider("OpenAI", """{"type":"env","name":"OPENAI_API_KEY"}"""),
            Provider("Gemini", Credential("external", "needs_auth"))) + "]";

        var pairs = await TwoXPairs(json);

        Assert.Equal(
            new[]
            {
                "Anthropic|OAuth",
                "Anthropic|API key",
                "OpenAI|Environment",
                "Gemini|External (needs sign-in)",
            },
            pairs);
    }

    [Fact]
    public async Task TwoX_NameWinsOverId_AndIdIsTheFallback_F3()
    {
        var pairs = await TwoXPairs(
            """
            [
              {"id":"anthropic","name":"Anthropic","connections":[{"type":"credential","method":"oauth"}]},
              {"id":"openrouter","connections":[{"type":"credential","method":"key"}]}
            ]
            """);

        Assert.Equal(new[] { "Anthropic|OAuth", "openrouter|API key" }, pairs);
    }

    [Fact]
    public async Task TwoX_UnknownExtraFieldsAreIgnored_F3()
    {
        var pairs = await TwoXPairs(
            """[{"id":"a","name":"Anthropic","extra":{"x":1},"connections":[{"type":"credential","method":"oauth","future":[1,2]}]}]""");

        Assert.Equal(new[] { "Anthropic|OAuth" }, pairs);
    }

    // ── F4: auth.json kinds ─────────────────────────────────────────────────────

    [Fact]
    public async Task OneX_TypeMapping_OAuthApiWellKnownAndEverythingElseIsOther_F4()
    {
        var pairs = await OneXPairs(
            """
            {
              "anthropic": {"type":"oauth","refresh":"r","access":"a","expires":1},
              "openai": {"type":"api","key":"k","metadata":{"a":"b"}},
              "custom": {"type":"wellknown","key":"k","token":"t"},
              "weird": {"type":"saml"},
              "untyped": {"key":"k"},
              "numeric-type": {"type":5},
              "not-an-object": "just a string",
              "null-value": null,
              "array-value": [1,2]
            }
            """);

        Assert.Equal(
            new[]
            {
                "anthropic|OAuth",
                "openai|API key",
                "custom|Well-known",
                "weird|Other",
                "untyped|Other",
                "numeric-type|Other",
                "not-an-object|Other",
                "null-value|Other",
                "array-value|Other",
            },
            pairs);
    }

    [Fact]
    public async Task OneX_FileOrderIsKept_F4()
    {
        var pairs = await OneXPairs("""{"zeta":{"type":"api"},"alpha":{"type":"oauth"},"mid":{"type":"wellknown"}}""");

        Assert.Equal(new[] { "zeta|API key", "alpha|OAuth", "mid|Well-known" }, pairs);
    }

    [Fact]
    public async Task OneX_UnknownExtraFieldsAndNestingAreIgnored_F4()
    {
        var pairs = await OneXPairs("""{"anthropic":{"type":"oauth","deep":{"a":[1,{"b":2}]},"expires":1e3}}""");

        Assert.Equal(new[] { "anthropic|OAuth" }, pairs);
    }

    // ── F5b: provider-name allow-list ───────────────────────────────────────────

    [Theory]
    [InlineData("Anthropic")]
    [InlineData("OpenAI")]
    [InlineData("GitHub Copilot")]
    [InlineData("a")]
    [InlineData("0pen")]
    [InlineData("my.provider_v2 (beta)+x/y@z:w-1")]
    [InlineData("https://example.test/.well-known")] // ':' '/' '.' '-' are all on the allow-list
    public async Task AllowedProviderNames_AreShown_F5b(string name)
    {
        var pairs = await OneXPairs($$"""{"{{name}}":{"type":"oauth"} }""");

        Assert.Equal(new[] { name + "|OAuth" }, pairs);
    }

    [Fact]
    public async Task ProviderName_IsTrimmedBeforeTheAllowListIsApplied_F5b()
    {
        var pairs = await TwoXPairs("""[{"id":"x","name":"  Anthropic  ","connections":[{"type":"credential","method":"oauth"}]}]""");

        Assert.Equal(new[] { "Anthropic|OAuth" }, pairs);
    }

    [Fact]
    public async Task ProviderName_OfExactly48Characters_IsAllowed_And49IsNot_F5b()
    {
        // Regex ^[A-Za-z0-9][A-Za-z0-9 ._()+/@:-]{0,47}$ -> at most 1 + 47 = 48 characters.
        var forty8 = new string('a', 48);
        var forty9 = new string('a', 49);

        var pairs = await OneXPairs($$"""{"{{forty8}}":{"type":"oauth"},"{{forty9}}":{"type":"oauth"} }""");

        Assert.Equal(new[] { forty8 + "|OAuth", "Unnamed provider|OAuth" }, pairs);
    }

    [Theory]
    [InlineData("-leading-dash")]
    [InlineData(".dot")]
    [InlineData("(paren")]
    [InlineData("has=equals")]
    [InlineData("has\\\"quote")] // JSON text for a name containing a double quote
    [InlineData("has'apostrophe")]
    [InlineData("has;semicolon")]
    [InlineData("has,comma")]
    [InlineData("has#hash")]
    [InlineData("has%percent")]
    [InlineData("has?query")]
    [InlineData("has*star")]
    [InlineData("has\\u001b[31mansi")] // JSON text for an ANSI escape
    [InlineData("has\\ttab")]
    [InlineData("has\\nnewline")]
    [InlineData("Ünïcode")]
    [InlineData("名前")]
    [InlineData("has\\u0000nul")]
    public async Task DisallowedProviderNames_BecomeUnnamedProvider_InAuthJson_F5b(string jsonEscapedName)
    {
        var pairs = await OneXPairs($$"""{"{{jsonEscapedName}}":{"type":"oauth"} }""");

        Assert.Equal(new[] { "Unnamed provider|OAuth" }, pairs);
    }

    [Theory]
    [InlineData("has=equals")]
    [InlineData("has\\\"quote")]
    [InlineData("has\\u001b[31mansi")]
    [InlineData("-leading")]
    public async Task DisallowedProviderNames_BecomeUnnamedProvider_InTwoXNameAndId_F5b(string jsonEscapedName)
    {
        // Both `name` and `id` carry the bad text: no raw text may survive in either field.
        var json = $$"""[{"id":"{{jsonEscapedName}}","name":"{{jsonEscapedName}}","connections":[{"type":"credential","method":"key"}]}]""";

        var pairs = await TwoXPairs(json);

        Assert.Equal(new[] { "Unnamed provider|API key" }, pairs);
    }

    [Theory]
    [InlineData("has=equals")]
    [InlineData("has\\\"quote")]
    [InlineData("has\\u001b[31mansi")]
    public async Task DisallowedIdIsUnnamed_WhenThereIsNoName_F3_F5b(string jsonEscapedId)
    {
        var json = $$"""[{"id":"{{jsonEscapedId}}","connections":[{"type":"credential","method":"key"}]}]""";

        var pairs = await TwoXPairs(json);

        Assert.Equal(new[] { "Unnamed provider|API key" }, pairs);
    }

    [Fact]
    public async Task WellKnownUrlKeyWithQueryCharacters_ShowsAsUnnamedProvider_F5b()
    {
        // F5b last sentence: a well-known URL key that does not match the allow-list shows as "Unnamed provider".
        var pairs = await OneXPairs("""{"https://host.test/path?token=abc":{"type":"wellknown","key":"k","token":"t"}}""");

        Assert.Equal(new[] { "Unnamed provider|Well-known" }, pairs);
    }

    [Fact]
    public async Task EmptyOrBlankNames_DoNotCrash_AndNeverShowBlank_F5b()
    {
        var pairs = await OneXPairs("""{"":{"type":"oauth"},"   ":{"type":"api"}}""");

        Assert.Equal(new[] { "Unnamed provider|OAuth", "Unnamed provider|API key" }, pairs);
    }

    [Fact]
    public async Task StatusAndSignInsExposeOnlyNamesAndKinds_F1()
    {
        var h = new ReaderHarness();
        h.UseTwoX("""[{"id":"a","name":"Anthropic","connections":[{"type":"credential","method":"oauth"}]}]""");

        var status = await h.ReadAsync();

        var signIn = Assert.Single(status.SignIns);
        Assert.Equal("Anthropic", signIn.Name);
        Assert.Equal("OAuth", signIn.Kind);
        Assert.Contains("Anthropic", status.ToString(), StringComparison.Ordinal);
        Assert.Contains("OAuth", status.ToString(), StringComparison.Ordinal);
    }
}
