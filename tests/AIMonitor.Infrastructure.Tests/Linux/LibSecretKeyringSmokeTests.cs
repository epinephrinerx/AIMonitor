using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using AIMonitor.Infrastructure.Linux;
using Xunit.Abstractions;

namespace AIMonitor.Infrastructure.Tests.Linux;

/// <summary>
/// Talks to the real Secret Service through the native shim (R-024). Every test here is skipped
/// unless the run opts in with AIM_SECRET_SERVICE_SMOKE=1, and the manual steps are narrowed
/// further by a second variable, so an ordinary test run never touches the login keyring. The
/// environment-dependent skip lives in the attribute; these tests read no variable themselves
/// except the one that names the scope to clear.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class LibSecretKeyringSmokeTests
{
    private const string Schema = "net.apichart.aiusagemonitor.SecretStoreKey";
    private const string Account = "secret-store-key";
    private const string ProbeService = "AIMonitor2.secrets.smoke-m4";

    private readonly ITestOutputHelper _output;

    public LibSecretKeyringSmokeTests(ITestOutputHelper output) => _output = output;

    private static Dictionary<string, string> Attributes(string service) =>
        new() { ["service"] = service, ["account"] = Account };

    [LinuxSecretServiceSmokeFact("round-trips a throwaway item through the real Secret Service")]
    public void Smoke_StoreLookupClearRoundTrip()
    {
        var keyring = new LibSecretKeyring();
        var attributes = Attributes("AIMonitor2.secrets.smoke-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Assert.Equal(SecretServiceStatus.NotFound, keyring.Lookup(Schema, attributes).Status);

            var stored = keyring.Store(Schema, attributes, "AI Usage Monitor smoke test", "c21va2U=");
            Assert.Equal(SecretServiceStatus.Done, stored.Status);

            var found = keyring.Lookup(Schema, attributes);
            Assert.Equal(SecretServiceStatus.Found, found.Status);
            Assert.Equal("c21va2U=", found.Secret);

            Assert.Equal(SecretServiceStatus.Done, keyring.Clear(Schema, attributes).Status);
            Assert.Equal(SecretServiceStatus.NotFound, keyring.Lookup(Schema, attributes).Status);
        }
        finally
        {
            keyring.Clear(Schema, attributes);
        }
    }

    [LinuxSecretServiceSmokeFact("stores the probe item for a manual check", RequiredEnvironment = "AIM_SECRET_SERVICE_SMOKE_M4=store")]
    public void Smoke_M4_StoreProbeItem()
    {
        var result = new LibSecretKeyring().Store(
            Schema,
            Attributes(ProbeService),
            "AI Usage Monitor smoke test (manual)",
            "bTQ=");

        Assert.Equal(SecretServiceStatus.Done, result.Status);
    }

    [LinuxSecretServiceSmokeFact("looks the probe item up and reports what happened", RequiredEnvironment = "AIM_SECRET_SERVICE_SMOKE_M4=lookup")]
    public void Smoke_M4_LookupProbeItem()
    {
        var result = new LibSecretKeyring().Lookup(Schema, Attributes(ProbeService));

        // This step only reports; it passes whatever the keyring answered.
        _output.WriteLine($"M4 outcome: {result.Status} ({SecretServiceErrors.SafeDetail(result.Message)})");
    }

    [LinuxSecretServiceSmokeFact("clears the probe item after a manual check", RequiredEnvironment = "AIM_SECRET_SERVICE_SMOKE_M4=clear")]
    public void Smoke_M4_ClearProbeItem()
    {
        var result = new LibSecretKeyring().Clear(Schema, Attributes(ProbeService));

        Assert.True(
            result.Status is SecretServiceStatus.Done or SecretServiceStatus.NotFound,
            $"unexpected outcome: {result.Status}");
    }

    [LinuxSecretServiceSmokeFact("clears the keyring item of one manual scope", RequiredEnvironment = "AIM_SECRET_SERVICE_SMOKE_CLEAR_SCOPE")]
    public void Smoke_ClearManualScopeKeyringItem()
    {
        var scope = Environment.GetEnvironmentVariable("AIM_SECRET_SERVICE_SMOKE_CLEAR_SCOPE") ?? string.Empty;
        if (!Regex.IsMatch(scope, @"\Ar024m[2-6]-[0-9]{1,12}\z", RegexOptions.CultureInvariant))
        {
            Assert.Fail("refusing to clear a scope that M2-M6 did not create");
        }

        var result = new LibSecretKeyring().Clear(
            Schema,
            Attributes("AIMonitor2.secrets.v1." + scope));

        Assert.True(
            result.Status is SecretServiceStatus.Done or SecretServiceStatus.NotFound,
            $"unexpected outcome: {result.Status}");
    }

    // ---- argument checks: these are refused before the session bus or the library is ever touched,
    // so they never reach the keyring and need no opt-in.

    private sealed class PairList(params (string Key, string Value)[] pairs) : IReadOnlyDictionary<string, string>
    {
        public IEnumerable<string> Keys => pairs.Select(p => p.Key);

        public IEnumerable<string> Values => pairs.Select(p => p.Value);

        public int Count => pairs.Length;

        public string this[string key] => pairs.First(p => p.Key == key).Value;

        public bool ContainsKey(string key) => pairs.Any(p => p.Key == key);

        public bool TryGetValue(string key, out string value)
        {
            value = pairs.FirstOrDefault(p => p.Key == key).Value;
            return ContainsKey(key);
        }

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() =>
            pairs.Select(p => new KeyValuePair<string, string>(p.Key, p.Value)).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static IReadOnlyDictionary<string, string> Fine() => Attributes("AIMonitor2.secrets.argument-test-" + Guid.NewGuid().ToString("N")[..8]);

    [LinuxOnlyFact("argument checks never reach the keyring")]
    public void Arguments_ANullSchemaNameIsRefusedWithItsParameterNamed()
    {
        var keyring = new LibSecretKeyring();

        Assert.Equal("schemaName", Assert.Throws<ArgumentNullException>(() => keyring.Lookup(null!, Fine())).ParamName);
        Assert.Equal("schemaName", Assert.Throws<ArgumentNullException>(() => keyring.Store(null!, Fine(), "label", "secret")).ParamName);
        Assert.Equal("schemaName", Assert.Throws<ArgumentNullException>(() => keyring.Clear(null!, Fine())).ParamName);
    }

    [LinuxOnlyFact("argument checks never reach the keyring")]
    public void Arguments_ABlankOrNulSchemaNameIsRefused()
    {
        var keyring = new LibSecretKeyring();

        foreach (var bad in new[] { "", "  ", "a\0b" })
        {
            Assert.Equal("schemaName", Assert.Throws<ArgumentException>(() => keyring.Lookup(bad, Fine())).ParamName);
            Assert.Equal("schemaName", Assert.Throws<ArgumentException>(() => keyring.Store(bad, Fine(), "label", "secret")).ParamName);
            Assert.Equal("schemaName", Assert.Throws<ArgumentException>(() => keyring.Clear(bad, Fine())).ParamName);
        }
    }

    [LinuxOnlyFact("argument checks never reach the keyring")]
    public void Arguments_BadAttributesAreRefusedWithTheParameterNamed()
    {
        var keyring = new LibSecretKeyring();
        var tooMany = Enumerable.Range(0, 33).ToDictionary(i => "k" + i, _ => "v");

        Assert.Equal("attributes", Assert.Throws<ArgumentNullException>(() => keyring.Lookup(Schema, null!)).ParamName);
        Assert.Equal("attributes", Assert.Throws<ArgumentNullException>(() => keyring.Clear(Schema, null!)).ParamName);
        Assert.Equal("attributes", Assert.Throws<ArgumentNullException>(() => keyring.Store(Schema, null!, "label", "secret")).ParamName);
        foreach (var bad in new IReadOnlyDictionary<string, string>[]
        {
            new Dictionary<string, string>(),
            tooMany,
            new Dictionary<string, string> { [""] = "v" },
            new Dictionary<string, string> { ["a\0b"] = "v" },
            new Dictionary<string, string> { ["k"] = "a\0b" },
        })
        {
            Assert.Equal("attributes", Assert.Throws<ArgumentException>(() => keyring.Lookup(Schema, bad)).ParamName);
            Assert.Equal("attributes", Assert.Throws<ArgumentException>(() => keyring.Clear(Schema, bad)).ParamName);
            Assert.Equal("attributes", Assert.Throws<ArgumentException>(() => keyring.Store(Schema, bad, "label", "secret")).ParamName);
        }

        foreach (var bad in new IReadOnlyDictionary<string, string>[]
        {
            new PairList((null!, "v")),
            new PairList(("k", null!)),
        })
        {
            Assert.Equal("attributes", Assert.Throws<ArgumentNullException>(() => keyring.Lookup(Schema, bad)).ParamName);
            Assert.Equal("attributes", Assert.Throws<ArgumentNullException>(() => keyring.Clear(Schema, bad)).ParamName);
            Assert.Equal("attributes", Assert.Throws<ArgumentNullException>(() => keyring.Store(Schema, bad, "label", "secret")).ParamName);
        }
    }

    [LinuxOnlyFact("argument checks never reach the keyring")]
    public void Arguments_ABadLabelOrSecretIsRefusedWithTheParameterNamed()
    {
        var keyring = new LibSecretKeyring();

        Assert.Equal("label", Assert.Throws<ArgumentNullException>(() => keyring.Store(Schema, Fine(), null!, "secret")).ParamName);
        Assert.Equal("secret", Assert.Throws<ArgumentNullException>(() => keyring.Store(Schema, Fine(), "label", null!)).ParamName);
        foreach (var bad in new[] { "  ", "a\0b" })
        {
            Assert.Equal("label", Assert.Throws<ArgumentException>(() => keyring.Store(Schema, Fine(), bad, "secret")).ParamName);
        }

        foreach (var bad in new[] { "", "a\0b" })
        {
            Assert.Equal("secret", Assert.Throws<ArgumentException>(() => keyring.Store(Schema, Fine(), "label", bad)).ParamName);
        }
    }
}
