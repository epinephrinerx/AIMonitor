using AIMonitor.Application.OpenCode;

namespace AIMonitor.Infrastructure.Tests.OpenCode;

/// <summary>
/// R-020 acceptance "secret sentinel tests (F1-F5)" and F5b (Docs/SPEC_R020_R021.md, Tests item 1):
/// a sentinel placed in every secret field of auth.json (refresh, access, key, token, metadata), in the 2.x
/// connection id/label/message/url, in stdout noise and in stderr must never appear in the status, its
/// ToString, Error, Source, or any exception message. Names that fail the allow-list (a character outside
/// it such as '=', '"' or an ANSI escape) become "Unnamed provider"; an env var name with only allowed
/// characters must still never appear (the kind is just "Environment").
/// </summary>
public sealed class OpenCodeAuthReaderSecretTests
{
    private const string Secret = "SENTINEL-SECRET-9c41d7";
    private const string EnvName = "SECRETENVNAME9C41D7";

    private static readonly string[] Needles = [Secret, "9c41d7", EnvName];

    [Fact]
    public async Task OneX_SentinelInEverySecretField_NeverAppearsAnywhere_F4()
    {
        var h = new ReaderHarness();
        h.SetVersion("1.19.5");
        h.SetAuthJson($$"""
            {
              "anthropic": {"type":"oauth","refresh":"{{Secret}}","access":"{{Secret}}","expires":1,"accountId":"{{Secret}}"},
              "openai": {"type":"api","key":"{{Secret}}","metadata":{"note":"{{Secret}}"} },
              "custom": {"type":"wellknown","key":"{{Secret}}","token":"{{Secret}}"},
              "odd": {"type":"{{Secret}}","key":"{{Secret}}"}
            }
            """);

        var status = await h.ReadAsync();

        Assert.Equal(
            new[] { "anthropic|OAuth", "openai|API key", "custom|Well-known", "odd|Other" },
            ReaderHarness.Pairs(status));
        ReaderHarness.AssertNoLeak(status, Needles);
    }

    [Fact]
    public async Task TwoX_SentinelInConnectionIdLabelMessageUrlAndExtras_NeverAppearsAnywhere_F3()
    {
        var h = new ReaderHarness();
        h.UseTwoX($$"""
            [
              {
                "id": "anthropic",
                "name": "Anthropic",
                "secretField": "{{Secret}}",
                "connections": [
                  {
                    "type": "credential",
                    "id": "cred_{{Secret}}",
                    "label": "{{Secret}}",
                    "method": "oauth",
                    "token": "{{Secret}}",
                    "status": {"status":"needs_auth","message":"{{Secret}}","url":"https://example.test/?k={{Secret}}"}
                  },
                  {"type":"env","name":"{{EnvName}}","status":{"status":"ok","message":"{{Secret}}","url":"{{Secret}}"} }
                ]
              }
            ]
            """);

        var status = await h.ReadAsync();

        Assert.Equal("opencode auth list", status.Source);
        Assert.Equal(2, status.SignIns.Count);
        Assert.Equal("Anthropic", status.SignIns[0].Name);
        Assert.Equal("Anthropic", status.SignIns[1].Name);
        Assert.Equal("Environment", status.SignIns[1].Kind);
        ReaderHarness.AssertNoLeak(status, Needles);
    }

    [Fact]
    public async Task EnvVarNameWithOnlyAllowedCharacters_NeverAppears_KindIsJustEnvironment_F5b()
    {
        var h = new ReaderHarness();
        h.UseTwoX($$"""[{"id":"openai","name":"OpenAI","connections":[{"type":"env","name":"{{EnvName}}"}]}]""");

        var status = await h.ReadAsync();

        Assert.Equal(new[] { "OpenAI|Environment" }, ReaderHarness.Pairs(status));
        ReaderHarness.AssertNoLeak(status, EnvName);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("id")]
    [InlineData("both")]
    [InlineData("authjson-property")]
    public async Task BadCharacterSentinelInNameIdOrPropertyName_YieldsUnnamedProvider_AndNeverLeaks_F5b(string where)
    {
        // Each bad name contains the secret text AND a character outside the allow-list ('=', '"' or ESC).
        string[] badNames = [$"{Secret}=x", $"{Secret}\\\"x", $"\\u001b[31m{Secret}"];
        foreach (var bad in badNames)
        {
            var h = new ReaderHarness();
            if (where == "authjson-property")
            {
                h.SetVersion("1.19.5");
                h.SetAuthJson($$"""{"{{bad}}":{"type":"oauth"} }""");
            }
            else
            {
                var name = where == "id" ? "" : $"\"name\":\"{bad}\",";
                var id = where == "name" ? "ok-id" : bad;
                h.UseTwoX($$"""[{ {{name}} "id":"{{id}}","connections":[{"type":"credential","method":"oauth"}]}]""");
            }

            var status = await h.ReadAsync();

            // where == "name": name is invalid and id is a perfectly good identifier; the entry is still not
            // allowed to show the bad name, so it is either "Unnamed provider" or the clean id, never the bad text.
            var signIn = Assert.Single(status.SignIns);
            Assert.DoesNotContain("9c41d7", signIn.Name, StringComparison.OrdinalIgnoreCase);
            if (where != "name")
            {
                Assert.Equal("Unnamed provider", signIn.Name);
            }

            ReaderHarness.AssertNoLeak(status, Needles);
        }
    }

    [Fact]
    public async Task SentinelInVersionStdoutNoise_NeverAppears_F5()
    {
        var h = new ReaderHarness();
        // The text must have NO digit (F5: "its text has no digit -> version unknown"), so this sentinel has none.
        const string noDigitSecret = "SENTINEL-SECRET-QWERTY";
        h.OnVersion = () => new FakeOpenCodeProcess($"{noDigitSecret} banner without a version number\n");

        var status = await h.ReadAsync();

        Assert.Equal(ReaderHarness.ExpectedErrorText, status.Error); // unknown version + no auth.json -> Error
        ReaderHarness.AssertNoLeak(status, noDigitSecret, "QWERTY");
    }

    [Fact]
    public async Task SentinelInVersionTextThatHasADigit_NeverAppears_F5()
    {
        var h = new ReaderHarness();
        h.OnVersion = () => new FakeOpenCodeProcess($"1.19.5 {Secret}\n", error: $"warning {Secret}");
        h.SetAuthJson("""{"anthropic":{"type":"oauth"}}""");

        var status = await h.ReadAsync();

        Assert.Equal(new[] { "anthropic|OAuth" }, ReaderHarness.Pairs(status));
        ReaderHarness.AssertNoLeak(status, Needles);
    }

    [Theory]
    [InlineData("exit1-stderr")]
    [InlineData("exit1-stdout")]
    [InlineData("invalid-json-stdout")]
    [InlineData("noise-before-json")]
    [InlineData("shape-error")]
    public async Task SentinelInAuthListOutputThatFails_NeverAppears_AndFallbackIsGeneric_F3_F5(string scenario)
    {
        var h = new ReaderHarness();
        h.SetVersion("2.2.7");
        h.OnList = scenario switch
        {
            "exit1-stderr" => () => new FakeOpenCodeProcess("", error: $"fatal: {Secret} at C:\\{Secret}\\x", exitCode: 1),
            "exit1-stdout" => () => new FakeOpenCodeProcess($"[{{\"id\":\"{Secret}\"}}]", exitCode: 1),
            "invalid-json-stdout" => () => new FakeOpenCodeProcess($"not json {Secret}"),
            "noise-before-json" => () => new FakeOpenCodeProcess($"{Secret}\n[]"),
            _ => () => new FakeOpenCodeProcess($"[{{\"name\":\"{Secret}\",\"connections\":[{{\"id\":\"{Secret}\"}}]}}]"),
        };

        var status = await h.ReadAsync();

        Assert.True(status.Installed);
        Assert.Equal(ReaderHarness.ExpectedErrorText, status.Error); // no auth.json -> generic Error
        ReaderHarness.AssertNoLeak(status, Needles);
    }

    [Fact]
    public async Task SentinelInStderrOfASuccessfulAuthList_NeverAppears_F2()
    {
        var h = new ReaderHarness();
        h.OnVersion = () => new FakeOpenCodeProcess("2.2.7", error: Secret);
        h.OnList = () => new FakeOpenCodeProcess("""[{"id":"a","name":"Anthropic","connections":[{"type":"credential","method":"key"}]}]""", error: Secret);

        var status = await h.ReadAsync();

        Assert.Equal(new[] { "Anthropic|API key" }, ReaderHarness.Pairs(status));
        ReaderHarness.AssertNoLeak(status, Needles);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("unauthorized")]
    [InlineData("invalid-json")]
    public async Task AuthJsonFailures_NeverPutSentinelPathsOrExceptionTextInTheStatus_F5(string failure)
    {
        var h = new ReaderHarness();
        h.SetVersion("1.19.5");
        switch (failure)
        {
            case "io":
                h.SetAuthJson("{}");
                h.FailOpen(ReaderHarness.DefaultAuthJson, new IOException($"{Secret} C:\\{Secret}\\auth.json"));
                break;
            case "unauthorized":
                h.SetAuthJson("{}");
                h.FailOpen(ReaderHarness.DefaultAuthJson, new UnauthorizedAccessException(Secret));
                break;
            default:
                h.SetAuthJson($$"""{"anthropic":{"type":"oauth","key":"{{Secret}}",""");
                break;
        }

        var status = await h.ReadAsync();

        Assert.Equal(ReaderHarness.ExpectedErrorText, status.Error);
        ReaderHarness.AssertNoLeak(status, Needles);
        ReaderHarness.AssertNoLeak(status, @"C:\", ReaderHarness.DefaultAuthJson, "AppData", ".local");
    }

    [Theory]
    [InlineData("version-start")]
    [InlineData("list-start")]
    [InlineData("auth-file-open")]
    public async Task NoExceptionOtherThanCancellationEscapes_EvenWhenTheSeamsThrowArbitraryExceptions_F5(string seam)
    {
        // "The reader throws nothing else; inner exceptions are not kept."
        var h = new ReaderHarness();
        h.SetVersion("2.2.7");
        h.SetAuthJson("{}");
        switch (seam)
        {
            case "version-start":
                h.OnVersion = () => throw new InvalidOperationException(Secret, new IOException(Secret));
                break;
            case "list-start":
                h.OnList = () => throw new InvalidOperationException(Secret, new IOException(Secret));
                break;
            default:
                h.FailOpen(ReaderHarness.DefaultAuthJson, new InvalidDataException(Secret));
                h.OnList = () => new FakeOpenCodeProcess("bad");
                break;
        }

        var thrown = await Record.ExceptionAsync(async () => ReaderHarness.AllText(await h.ReadAsync()));

        Assert.Null(thrown);
    }

    [Fact]
    public void StatusToString_ShowsOnlyNamesAndKinds_NeverSourceErrorOrFlags_F1()
    {
        var status = new OpenCodeAuthStatus(
            Installed: true,
            Source: "SOURCE-MARKER",
            SignIns: [new OpenCodeSignIn("Anthropic", "OAuth"), new OpenCodeSignIn("OpenAI", "API key")],
            MayBeOutdated: true,
            Error: "ERROR-MARKER");

        var text = status.ToString();

        Assert.Contains("Anthropic", text, StringComparison.Ordinal);
        Assert.Contains("OAuth", text, StringComparison.Ordinal);
        Assert.Contains("OpenAI", text, StringComparison.Ordinal);
        Assert.Contains("API key", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SOURCE-MARKER", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ERROR-MARKER", text, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusToString_HasTheExactSpecShape_H2()
    {
        var status = new OpenCodeAuthStatus(
            Installed: true,
            Source: "SOURCE-MARKER",
            SignIns: [new OpenCodeSignIn("Anthropic", "OAuth"), new OpenCodeSignIn("OpenAI", "API key")],
            MayBeOutdated: true,
            Error: "ERROR-MARKER");

        Assert.Equal("OpenCodeAuthStatus { SignIns = [Anthropic (OAuth), OpenAI (API key)] }", status.ToString());
    }

    [Fact]
    public void StatusToString_WithoutSignIns_IsEmptyBrackets_AndNeverShowsErrorOrSource_H2()
    {
        var status = new OpenCodeAuthStatus(false, "SOURCE-MARKER", [], false, "ERROR-MARKER");

        Assert.Equal("OpenCodeAuthStatus { SignIns = [] }", status.ToString());
    }

    [Fact]
    public async Task EverySentinelScenario_NoFieldOfTheReturnedStatusContainsTheSentinel_F1_F5()
    {
        // Every field of the status is checked one by one (Source, Error, each SignIn's Name and Kind, ToString()).
        // The card side of the same gap (Reviewer2) is covered in the WPF tests, which push hand-built statuses that
        // carry a sentinel in every string field through ConnectionCardViewModel.UpdateOpenCode.
        var scenarios = new List<ReaderHarness>();

        var oneX = new ReaderHarness();
        oneX.SetVersion("1.19.5");
        oneX.SetAuthJson($$"""
            {
              "anthropic": {"type":"oauth","refresh":"{{Secret}}","access":"{{Secret}}","accountId":"{{Secret}}"},
              "openai": {"type":"api","key":"{{Secret}}","metadata":{"note":"{{Secret}}"} },
              "custom": {"type":"wellknown","key":"{{Secret}}","token":"{{Secret}}"},
              "bad={{Secret}}": {"type":"oauth"}
            }
            """);
        scenarios.Add(oneX);

        var twoX = new ReaderHarness();
        twoX.UseTwoX($$"""
            [{"id":"anthropic","name":"Anthropic","connections":[
              {"type":"credential","id":"cred_{{Secret}}","label":"{{Secret}}","method":"oauth","status":{"status":"needs_auth","message":"{{Secret}}","url":"https://example.test/?k={{Secret}}"} },
              {"type":"env","name":"{{EnvName}}","status":{"status":"ok","message":"{{Secret}}"} }]},
             {"id":"x={{Secret}}","name":"bad={{Secret}}","connections":[{"type":"credential","method":"key"}]}]
            """);
        scenarios.Add(twoX);

        var failedList = new ReaderHarness();
        failedList.SetVersion("2.2.7");
        failedList.OnList = () => new FakeOpenCodeProcess($"not json {Secret}", error: Secret, exitCode: 1);
        failedList.AddFile(ReaderHarness.DefaultAuthJson, $$"""{"anthropic":{"type":"oauth","key":"{{Secret}}",""");
        scenarios.Add(failedList);

        var unreadable = new ReaderHarness();
        unreadable.SetVersion("1.19.5");
        unreadable.SetAuthJson("{}");
        unreadable.FailOpen(ReaderHarness.DefaultAuthJson, new IOException($"{Secret} C:\\{Secret}\\auth.json"));
        scenarios.Add(unreadable);

        var versionNoise = new ReaderHarness();
        versionNoise.OnVersion = () => new FakeOpenCodeProcess("SENTINEL-SECRET-QWERTY banner without digits", error: Secret);
        scenarios.Add(versionNoise);

        var notInstalled = new ReaderHarness();
        notInstalled.RemoveFile(ReaderHarness.DefaultExe);
        scenarios.Add(notInstalled);

        foreach (var scenario in scenarios)
        {
            var status = await scenario.ReadAsync();

            var fields = new List<string> { status.Source, status.Error, status.ToString() };
            foreach (var signIn in status.SignIns)
            {
                fields.Add(signIn.Name);
                fields.Add(signIn.Kind);
                fields.Add(signIn.ToString());
            }

            foreach (var field in fields)
            {
                Assert.DoesNotContain("9c41d7", field, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("SENTINEL", field, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("QWERTY", field, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(EnvName, field, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(@"C:\", field, StringComparison.Ordinal);
            }

            ReaderHarness.AssertNoLeak(status, Needles);
        }
    }

    [Fact]
    public void StatusShape_HasNoSecretCarryingMember_F1()
    {
        // F1: OpenCodeAuthStatus(bool Installed, string Source, IReadOnlyList<OpenCodeSignIn> SignIns,
        // bool MayBeOutdated, string Error); OpenCodeSignIn(string Name, string Kind). Nothing else public.
        var statusProperties = typeof(OpenCodeAuthStatus).GetProperties();
        var signInProperties = typeof(OpenCodeSignIn).GetProperties();

        Assert.Contains(statusProperties, p => p.Name == "Installed" && p.PropertyType == typeof(bool));
        Assert.Contains(statusProperties, p => p.Name == "Source" && p.PropertyType == typeof(string));
        Assert.Contains(statusProperties, p => p.Name == "SignIns" && p.PropertyType == typeof(IReadOnlyList<OpenCodeSignIn>));
        Assert.Contains(statusProperties, p => p.Name == "MayBeOutdated" && p.PropertyType == typeof(bool));
        Assert.Contains(statusProperties, p => p.Name == "Error" && p.PropertyType == typeof(string));
        Assert.Contains(signInProperties, p => p.Name == "Name" && p.PropertyType == typeof(string));
        Assert.Contains(signInProperties, p => p.Name == "Kind" && p.PropertyType == typeof(string));

        string[] forbidden =
        [
            "token", "secret", "key", "password", "refresh", "access", "credential", "content", "raw",
            "output", "stdout", "stderr", "path", "exception", "message", "label", "url", "env",
        ];
        foreach (var property in statusProperties.Concat(signInProperties))
        {
            Assert.DoesNotContain(forbidden, f => property.Name.Contains(f, StringComparison.OrdinalIgnoreCase));
        }

        Assert.Empty(typeof(OpenCodeAuthStatus).GetFields());
        Assert.Empty(typeof(OpenCodeSignIn).GetFields());
    }
}
