using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using AIMonitor.Infrastructure.MacOS;

namespace AIMonitor.Infrastructure.Tests.MacOS;

/// <summary>
/// The Keychain itself is replaced by a recording fake, so these run on any host and can assert
/// what the adapter asked the Keychain to do — including that the key never appears in a process
/// argument.
/// </summary>
public sealed class KeychainSecretProtectorTests
{
    private const string Service = "AIMonitor2.secrets.test";
    private const string Account = "secret-store-key";

    /// <summary>Stands in for <c>/usr/bin/security</c> and remembers every invocation.</summary>
    private sealed class FakeKeychain
    {
        public List<(IReadOnlyList<string> Arguments, string? StandardInput)> Invocations { get; } = [];

        public string? StoredValue { get; set; }

        public int AddExitCode { get; set; }

        public CommandResult Run(ProcessStartInfo startInfo, string? standardInput)
        {
            Invocations.Add((startInfo.ArgumentList.ToArray(), standardInput));

            if (standardInput is null)
            {
                // find-generic-password -w: exit 44 is the Keychain's "no such item".
                return StoredValue is null
                    ? new CommandResult(44, string.Empty, "security: SecKeychainSearchCopyNext: The specified item could not be found in the keychain.")
                    : new CommandResult(0, StoredValue + "\n", string.Empty);
            }

            if (standardInput.StartsWith("add-generic-password", StringComparison.Ordinal))
            {
                if (AddExitCode != 0) return new CommandResult(AddExitCode, string.Empty, "security: refused");
                StoredValue = ExtractAddedValue(standardInput);
                return new CommandResult(0, string.Empty, string.Empty);
            }

            if (standardInput.StartsWith("delete-generic-password", StringComparison.Ordinal))
            {
                var existed = StoredValue is not null;
                StoredValue = null;
                return new CommandResult(existed ? 0 : 44, string.Empty, string.Empty);
            }

            return new CommandResult(1, string.Empty, "unexpected command");
        }

        /// <summary>Pulls the value back out of the quoted <c>-w "..."</c> the adapter wrote.</summary>
        private static string ExtractAddedValue(string command)
        {
            var marker = command.LastIndexOf("-w \"", StringComparison.Ordinal);
            Assert.True(marker >= 0, $"no -w value in: {command}");
            var body = command[(marker + 4)..].TrimEnd('\n');
            Assert.EndsWith("\"", body, StringComparison.Ordinal);
            return body[..^1]
                .Replace("\\\"", "\"", StringComparison.Ordinal)
                .Replace("\\\\", "\\", StringComparison.Ordinal);
        }
    }

    private static KeychainSecretProtector Protector(FakeKeychain keychain) =>
        new(Service, Account, keychain.Run);

    [Fact]
    public void ProtectThenUnprotect_RoundTripsThroughAFreshKey()
    {
        var keychain = new FakeKeychain();
        var plaintext = Encoding.UTF8.GetBytes("""{"openai":"not-a-real-key"}""");

        var sealedBytes = Protector(keychain).Protect(plaintext);

        Assert.DoesNotContain("not-a-real-key", Encoding.UTF8.GetString(sealedBytes), StringComparison.Ordinal);
        Assert.Equal(plaintext, Protector(keychain).Unprotect(sealedBytes));
    }

    [Fact]
    public void Protect_PutsTheKeyOnStandardInputOnly_NeverInAProcessArgument()
    {
        var keychain = new FakeKeychain();

        Protector(keychain).Protect(Encoding.UTF8.GetBytes("payload"));

        var key = Assert.IsType<string>(keychain.StoredValue);
        Assert.NotEmpty(key);
        foreach (var (arguments, _) in keychain.Invocations)
        {
            Assert.DoesNotContain(arguments, argument => argument.Contains(key, StringComparison.Ordinal));
        }

        var carried = keychain.Invocations.Where(invocation => invocation.StandardInput is not null).ToList();
        Assert.Single(carried);
        Assert.Equal(["-i"], carried[0].Arguments);
        Assert.Contains(key, carried[0].StandardInput!, StringComparison.Ordinal);
    }

    [Fact]
    public void Protect_ReusesTheKeyAlreadyInTheKeychain_RatherThanReplacingIt()
    {
        var keychain = new FakeKeychain { StoredValue = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };
        var existing = keychain.StoredValue;

        var sealedBytes = Protector(keychain).Protect(Encoding.UTF8.GetBytes("payload"));

        Assert.Equal(existing, keychain.StoredValue);
        Assert.DoesNotContain(keychain.Invocations, invocation =>
            invocation.StandardInput?.StartsWith("add-generic-password", StringComparison.Ordinal) == true);
        Assert.Equal("payload", Encoding.UTF8.GetString(Protector(keychain).Unprotect(sealedBytes)));
    }

    [Fact]
    public void Protect_UsesAFreshNonceEachTime_SoTwoSealsOfOneValueDiffer()
    {
        var keychain = new FakeKeychain();
        var protector = Protector(keychain);
        var plaintext = Encoding.UTF8.GetBytes("payload");

        Assert.NotEqual(protector.Protect(plaintext), protector.Protect(plaintext));
    }

    [Fact]
    public void Unprotect_WhenTheKeychainHasNoKey_SaysSoInsteadOfReturningRubbish()
    {
        var keychain = new FakeKeychain();
        var sealedBytes = Protector(keychain).Protect(Encoding.UTF8.GetBytes("payload"));

        keychain.StoredValue = null;

        Assert.Throws<CryptographicException>(() => Protector(keychain).Unprotect(sealedBytes));
    }

    [Fact]
    public void Unprotect_WhenTheKeyIsNotTheOneThatSealedIt_Fails()
    {
        var keychain = new FakeKeychain();
        var sealedBytes = Protector(keychain).Protect(Encoding.UTF8.GetBytes("payload"));

        keychain.StoredValue = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        Assert.Throws<AuthenticationTagMismatchException>(() => Protector(keychain).Unprotect(sealedBytes));
    }

    [Fact]
    public void Unprotect_WhenTheSealedBytesWereTamperedWith_Fails()
    {
        var keychain = new FakeKeychain();
        var sealedBytes = Protector(keychain).Protect(Encoding.UTF8.GetBytes("payload"));
        sealedBytes[^1] ^= 0xFF;

        Assert.Throws<AuthenticationTagMismatchException>(() => Protector(keychain).Unprotect(sealedBytes));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(28)]
    public void Unprotect_WhenTheFileIsTooShortToHoldAHeader_Fails(int length)
    {
        var keychain = new FakeKeychain { StoredValue = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };

        Assert.Throws<CryptographicException>(() => Protector(keychain).Unprotect(new byte[length]));
    }

    [Fact]
    public void Unprotect_WhenTheFormatVersionIsUnknown_SaysWhichVersionItFound()
    {
        var keychain = new FakeKeychain();
        var sealedBytes = Protector(keychain).Protect(Encoding.UTF8.GetBytes("payload"));
        sealedBytes[0] = 0x7F;

        var exception = Assert.Throws<CryptographicException>(() => Protector(keychain).Unprotect(sealedBytes));
        Assert.Contains("127", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not base64 at all!")]
    [InlineData("c2hvcnQ=")]
    public void Protect_WhenTheKeychainItemIsNotAKeyWeWrote_RefusesInsteadOfOverwritingIt(string stored)
    {
        var keychain = new FakeKeychain { StoredValue = stored };

        Assert.Throws<CryptographicException>(() => Protector(keychain).Protect(Encoding.UTF8.GetBytes("payload")));
        Assert.Equal(stored, keychain.StoredValue);
    }

    [Fact]
    public void Protect_WhenTheKeychainRefusesToStoreTheKey_ReportsIt()
    {
        var keychain = new FakeKeychain { AddExitCode = 51 };

        var exception = Assert.Throws<CryptographicException>(
            () => Protector(keychain).Protect(Encoding.UTF8.GetBytes("payload")));
        Assert.Contains("51", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Forget_RemovesTheKey_SoClearingEverythingLeavesNothingBehindInTheKeychain()
    {
        var keychain = new FakeKeychain();
        var protector = Protector(keychain);
        protector.Protect(Encoding.UTF8.GetBytes("payload"));

        Assert.True(protector.Forget());
        Assert.Null(keychain.StoredValue);
    }
}
