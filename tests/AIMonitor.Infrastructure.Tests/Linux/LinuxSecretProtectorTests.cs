using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using AIMonitor.Infrastructure.Linux;
using AIMonitor.Infrastructure.Storage;

namespace AIMonitor.Infrastructure.Tests.Linux;

/// <summary>
/// The Linux secret protector (R-024): it seals the secret store with a key from the Secret
/// Service, or from a private key file when there is no Secret Service. Every test injects a fake
/// keyring, so nothing here ever talks to a real keyring. They run on real files, folders, modes
/// and lock files in a temp folder of their own, so all of it is Linux-only. Every path and timeout
/// is passed in; nothing here reads or sets an environment variable.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxSecretProtectorTests : IDisposable
{
    private const string Service = "AIMonitor2.secrets.test";
    private const string Account = "secret-store-key";
    private const string Schema = "net.apichart.aiusagemonitor.SecretStoreKey";
    private const string SecretText = "sk-test-0123456789abcdef";
    private const string ValidAllZeroKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
    private static readonly byte[] Plaintext = Encoding.UTF8.GetBytes("{\"openai\":\"" + SecretText + "\"}");

    // The exact exception texts of the contract.
    private const string Short = "The sealed secret store is too short to be valid.";
    private const string SsInvalid = "The Secret Service item for the secret store key was not written by this app.";
    private const string SsNotFound = "The secret store was sealed with a key from the Secret Service, but the Secret Service holds no such key.";
    private const string SsUnavailable = "The secret store was sealed with a key from the Secret Service, which is not available in this session; the stored secrets were left unchanged.";
    private const string SsFailed = "The Secret Service could not provide the secret store key; the stored secrets were left unchanged.";
    private const string KeyFileMissing = "The secret store was sealed with a key file that no longer exists.";
    private const string LookupFailed = "The Secret Service could not be asked for the secret store key.";
    private const string StoreFailed = "The Secret Service refused to store the secret store key.";
    private const string NoMigrate = "The secret store uses the Secret Service, which is not available in this session; nothing was saved.";
    private const string LockBusy = "Another copy of AI Usage Monitor is creating the secret store key; try again.";
    private const string LockError = "Cannot lock the secret store key.";
    private const string DirError = "Cannot prepare the secret store folder.";
    private const string ParentLink = "The secret store folder is a symbolic link; refusing to use it.";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "aim-sp-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories).Prepend(_root))
        {
            try
            {
                if (new FileInfo(entry).LinkTarget is null)
                {
                    File.SetUnixFileMode(
                        entry,
                        Directory.Exists(entry)
                            ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                            : UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        Directory.Delete(_root, recursive: true);
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static UnixFileMode Mode(string octal) => (UnixFileMode)Convert.ToInt32(octal, 8);

    private static UnixFileMode ModeOf(string path) => File.GetUnixFileMode(path) & Mode("7777");

    private static string[] NamesIn(string directory) =>
        [.. Directory.GetFileSystemEntries(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal)!];

    private string Folder(string name = "d") => Path.Combine(_root, name);

    private string KeyFile(string name = "d") => Path.Combine(Folder(name), "secrets.key");

    private string LockFile(string name = "d") => Path.Combine(Folder(name), "secrets.key.lock");

    private LinuxSecretProtector New(FakeKeyring fake, string name = "d", TimeSpan? timeout = null) =>
        new(Service, Account, fake, KeyFile(name), timeout);

    private static string Base64(byte[] key) => Convert.ToBase64String(key);

    private static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

    private static byte[] Decrypt(byte[] key, byte[] blob, byte source)
    {
        var plaintext = new byte[blob.Length - 30];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(blob.AsSpan(2, 12), blob.AsSpan(30), blob.AsSpan(14, 16), plaintext, [0x01, source]);
        return plaintext;
    }

    private static void AssertSealedWith(byte[] key, byte source, byte[] blob, byte[]? expectedPlaintext = null)
    {
        Assert.Equal(30 + (expectedPlaintext ?? Plaintext).Length, blob.Length);
        Assert.Equal(0x01, blob[0]);
        Assert.Equal(source, blob[1]);
        Assert.Equal(expectedPlaintext ?? Plaintext, Decrypt(key, blob, source));
    }

    // A message of a raised exception, and of everything inside it, must not carry a key, the secret or the plaintext.
    private static void AssertNothingLeaks(Exception exception, FakeKeyring? fake, params byte[][] keys)
    {
        var text = exception.ToString();
        Assert.DoesNotContain(SecretText, text, StringComparison.Ordinal);
        foreach (var key in keys)
        {
            Assert.DoesNotContain(Base64(key), text, StringComparison.Ordinal);
        }

        if (fake is not null)
        {
            foreach (var call in fake.Calls.Where(call => !string.IsNullOrEmpty(call.Secret)))
            {
                Assert.DoesNotContain(call.Secret!, text, StringComparison.Ordinal);
            }
        }
    }

    private static CryptographicException Refused(Func<object?> action, string expectedMessage, FakeKeyring? fake = null, params byte[][] keys)
    {
        var thrown = Assert.Throws<CryptographicException>(action);
        Assert.Equal(expectedMessage, thrown.Message);
        AssertNothingLeaks(thrown, fake, keys);
        return thrown;
    }

    private static string Suffix(string status, string detail) => $" ({status}: {detail})";

    /// <summary>A sealed store made with a key the fake serves, ready to be opened by another instance.</summary>
    private byte[] SealWithSecretService(byte[] key, string name = "d")
    {
        var fake = new FakeKeyring { LookupResult = SecretServiceResultOf.Found(Base64(key)) };
        return New(fake, name).Protect(Plaintext);
    }

    private byte[] SealWithKeyFile(string name = "d")
    {
        var fake = new FakeKeyring { LookupResult = new(SecretServiceStatus.Unavailable) };
        return New(fake, name).Protect(Plaintext);
    }

    // ---- constructor and constants -------------------------------------------------------------

    [LinuxOnlyFact("builds the Linux protector")]
    public void Constants_AreTheNamesOfTheContract()
    {
        Assert.Equal("secrets.key", LinuxSecretProtector.KeyFileName);
        Assert.Equal("secrets.key.lock", LinuxSecretProtector.LockFileName);
        Assert.Equal("net.apichart.aiusagemonitor.SecretStoreKey", LinuxSecretProtector.SchemaName);
        Assert.Equal(TimeSpan.FromSeconds(65), LinuxSecretProtector.DefaultKeyLockTimeout);
        Assert.Equal(
            new Dictionary<string, string> { ["service"] = "s", ["account"] = "a" },
            LinuxSecretProtector.KeyAttributes("s", "a"));
        Assert.Equal("AI Usage Monitor secret store key (s)", LinuxSecretProtector.KeyLabel("s"));
        Assert.Equal((byte)1, (byte)LinuxKeySource.SecretService);
        Assert.Equal((byte)2, (byte)LinuxKeySource.KeyFile);
    }

    [LinuxOnlyFact("builds the Linux protector")]
    public void P3_21_ConstructingCallsNoKeyringAndTouchesNoDisk()
    {
        var fake = new FakeKeyring();

        var protector = New(fake);

        Assert.Empty(fake.Calls);
        Assert.False(Directory.Exists(_root));
        Assert.Equal(Service, protector.Service);
        Assert.Equal(Account, protector.Account);
        Assert.Equal(KeyFile(), protector.KeyFilePath);
        Assert.Equal(LockFile(), protector.LockFilePath);
    }

    [LinuxOnlyFact("builds the Linux protector")]
    public void P3_22_BadConstructorArgumentsAreRefused_WithTheParameterNamed()
    {
        var fake = new FakeKeyring();
        var key = KeyFile();

        Assert.Equal("service", Assert.Throws<ArgumentException>(() => new LinuxSecretProtector(" ", Account, fake, key)).ParamName);
        Assert.Equal("account", Assert.Throws<ArgumentException>(() => new LinuxSecretProtector(Service, " ", fake, key)).ParamName);
        Assert.Equal("keyFilePath", Assert.Throws<ArgumentException>(() => new LinuxSecretProtector(Service, Account, fake, " ")).ParamName);
        Assert.Equal("keyFilePath", Assert.Throws<ArgumentException>(() => new LinuxSecretProtector(Service, Account, fake, "rel/secrets.key")).ParamName);
        Assert.Equal("keyring", Assert.Throws<ArgumentNullException>(() => new LinuxSecretProtector(Service, Account, null!, key)).ParamName);
        Assert.Equal("service", Assert.Throws<ArgumentNullException>(() => new LinuxSecretProtector(null!, Account, fake, key)).ParamName);
        Assert.Equal("account", Assert.Throws<ArgumentNullException>(() => new LinuxSecretProtector(Service, null!, fake, key)).ParamName);
        Assert.Equal("keyFilePath", Assert.Throws<ArgumentNullException>(() => new LinuxSecretProtector(Service, Account, fake, null!)).ParamName);
        Assert.Throws<ArgumentOutOfRangeException>(() => new LinuxSecretProtector(Service, Account, fake, key, TimeSpan.FromMilliseconds(-1)));
        Assert.Empty(fake.Calls);
        Assert.False(Directory.Exists(_root));
    }

    // ---- Protect: choosing and creating the key ------------------------------------------------

    [LinuxOnlyFact("works on real files and modes")]
    public void P3_23_WithNoKeyYet_AFreshKeyIsStoredInTheSecretServiceAndSealsTheData()
    {
        var fake = new FakeKeyring { LookupResult = new(SecretServiceStatus.NotFound), StoreResult = new(SecretServiceStatus.Done) };

        var blob = New(fake).Protect(Plaintext);

        var store = Assert.Single(fake.Calls, call => call.Method == "Store");
        Assert.Equal(Schema, store.Schema);
        Assert.Equal(
            new Dictionary<string, string> { ["service"] = Service, ["account"] = Account },
            store.Attributes);
        Assert.Equal("AI Usage Monitor secret store key (AIMonitor2.secrets.test)", store.Label);
        Assert.Equal(44, store.Secret!.Length);
        var key = Convert.FromBase64String(store.Secret);
        Assert.Equal(32, key.Length);
        Assert.Equal(store.Secret, Base64(key));
        AssertSealedWith(key, 0x01, blob);
        Assert.False(File.Exists(KeyFile()));
        Assert.True(File.Exists(LockFile()));
        Assert.Equal(0, new FileInfo(LockFile()).Length);
    }

    [LinuxOnlyFact("works on real files and modes")]
    public void P3_23_TheLookupAsksForTheSameItemTheStoreWrites()
    {
        var fake = new FakeKeyring { LookupResult = new(SecretServiceStatus.NotFound), StoreResult = new(SecretServiceStatus.Done) };

        New(fake).Protect(Plaintext);

        var lookup = Assert.Single(fake.Calls, call => call.Method == "Lookup");
        Assert.Equal(Schema, lookup.Schema);
        Assert.Equal(
            new Dictionary<string, string> { ["service"] = Service, ["account"] = Account },
            lookup.Attributes);
    }

    [LinuxOnlyFact("works on real files and modes")]
    public void P3_24_ASecondProtectAsksAgainAndReusesTheStoredKey()
    {
        var fake = new FakeKeyring { LookupResult = new(SecretServiceStatus.NotFound), StoreResult = new(SecretServiceStatus.Done) };
        var protector = New(fake);

        var first = protector.Protect(Plaintext);
        var second = protector.Protect(Plaintext);

        Assert.Equal(2, fake.Lookups);
        Assert.Equal(1, fake.Stores);
        Assert.NotEqual(first, second);
        Assert.NotEqual(first.AsSpan(2, 12).ToArray(), second.AsSpan(2, 12).ToArray());
        var key = Convert.FromBase64String(fake.Calls.Single(call => call.Method == "Store").Secret!);
        AssertSealedWith(key, 0x01, first);
        AssertSealedWith(key, 0x01, second);
    }

    [LinuxOnlyFact("works on real files and modes")]
    public void P3_25_AKeyTheSecretServiceAlreadyHoldsIsUsedAsItIs()
    {
        var key = NewKey();
        var fake = new FakeKeyring { LookupResult = SecretServiceResultOf.Found(Base64(key)) };

        var blob = New(fake).Protect(Plaintext);

        Assert.Equal(0, fake.Stores);
        AssertSealedWith(key, 0x01, blob);
        Assert.False(File.Exists(KeyFile()));
    }

    [LinuxOnlyFact("works on real files and modes")]
    public void P3_25_ACanonicalAllZeroKeyIsAccepted()
    {
        var fake = new FakeKeyring { LookupResult = SecretServiceResultOf.Found(ValidAllZeroKey) };

        var blob = New(fake).Protect(Plaintext);

        AssertSealedWith(new byte[32], 0x01, blob);
    }

    [LinuxOnlyFact("works on real files and modes")]
    public void P3_26_WithNoSecretService_TheKeyFileIsUsedAndNothingIsStored()
    {
        var fake = new FakeKeyring { LookupResult = new(SecretServiceStatus.Unavailable) };

        var blob = New(fake).Protect(Plaintext);

        Assert.Equal(0, fake.Stores);
        var keyBytes = File.ReadAllBytes(KeyFile());
        Assert.Equal(32, keyBytes.Length);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, ModeOf(KeyFile()));
        AssertSealedWith(keyBytes, 0x02, blob);
    }

    [LinuxOnlyFact("works on real files and modes")]
    public void P3_27_WhenTheSecretServiceCannotStoreTheKey_TheKeyFileIsUsed()
    {
        var fake = new FakeKeyring { LookupResult = new(SecretServiceStatus.NotFound), StoreResult = new(SecretServiceStatus.Unavailable) };

        var blob = New(fake).Protect(Plaintext);

        Assert.Equal(1, fake.Stores);
        Assert.Equal(0x02, blob[1]);
        AssertSealedWith(File.ReadAllBytes(KeyFile()), 0x02, blob);
    }

    [LinuxOnlyFact("works on real files and modes")]
    public void P3_28_WhenTheSecretServiceRefusesTheKey_ThereIsNoFallback()
    {
        var fake = new FakeKeyring
        {
            LookupResult = new(SecretServiceStatus.NotFound),
            StoreResult = new(SecretServiceStatus.Failed, null, "g-dbus-error-quark 9"),
        };

        Refused(() => New(fake).Protect(Plaintext), StoreFailed + Suffix("Failed", "g-dbus-error-quark 9"), fake);

        Assert.False(File.Exists(KeyFile()));
    }

    [LinuxOnlyFact("works on real files and modes")]
    public void P3_29_WhenTheSecretServiceCannotBeAsked_ThereIsNoFallbackAndNothingIsStored()
    {
        var fake = new FakeKeyring { LookupResult = new(SecretServiceStatus.Failed, null, "secret-error 2: locked") };

        Refused(() => New(fake).Protect(Plaintext), LookupFailed + Suffix("Failed", "details withheld"), fake);

        Assert.Equal(0, fake.Stores);
        Assert.False(File.Exists(KeyFile()));
    }

    [LinuxOnlyFact("works on real files and modes")]
    public void P3_30_AnItemThatIsNotACanonicalKeyIsRefusedAndNeverOverwritten()
    {
        foreach (var bad in new[]
        {
            ValidAllZeroKey + "\n",
            "AAAAAAAAAAAAAAAAAAAAA AAAAAAAAAAAAAAAAAAAAAA=",
            ValidAllZeroKey[..^1],
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB=",
            Convert.ToBase64String(new byte[16]),
            Convert.ToBase64String(new byte[33]),
            string.Empty,
            SecretText,
        })
        {
            var fake = new FakeKeyring { LookupResult = SecretServiceResultOf.Found(bad) };

            Refused(() => New(fake).Protect(Plaintext), SsInvalid, fake);

            Assert.True(fake.Stores == 0, $"'{bad}' was overwritten");
        }
    }

    // ---- Unprotect ------------------------------------------------------------------------------

    [LinuxOnlyFact("works on real files and modes")]
    public void P3_31_ASecondInstanceOpensWhatTheFirstSealed_AndKeepsUsingTheSameSource()
    {
        var key = NewKey();
        var blob = SealWithSecretService(key);
        var fake = new FakeKeyring { LookupResult = SecretServiceResultOf.Found(Base64(key)) };
        var other = New(fake);

        var opened = other.Unprotect(blob);
        var resealed = other.Protect(opened);

        Assert.Equal(Plaintext, opened);
        Assert.Equal(2, fake.Lookups);
        Assert.Equal(0, fake.Stores);
        AssertSealedWith(key, 0x01, resealed);
    }

    [LinuxOnlyFact("works on real files and modes")]
    public void P3_32_ASecretServiceStoreCannotBeOpenedWithoutTheSecretService_AndTheKeyFileIsNotInvented()
    {
        var blob = SealWithSecretService(NewKey());
        var fake = new FakeKeyring { LookupResult = new(SecretServiceStatus.Unavailable) };

        var thrown = Refused(() => New(fake, "other").Unprotect(blob), SsUnavailable + Suffix("Unavailable", "details withheld"), fake);

        Assert.Contains("Secret Service", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("not available", thrown.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(KeyFile("other")));
        Assert.Equal(0, fake.Stores);
    }

    [LinuxOnlyFact("works on real files and modes")]
    public void P3_33_ASecretServiceStoreWhoseKeyIsGoneOrUnreadableIsRefused_WithoutTouchingAnything()
    {
        var blob = SealWithSecretService(NewKey());
        var missing = new FakeKeyring { LookupResult = new(SecretServiceStatus.NotFound) };
        var failed = new FakeKeyring { LookupResult = new(SecretServiceStatus.Failed) };

        var notFound = Refused(() => New(missing, "o1").Unprotect(blob), SsNotFound + Suffix("NotFound", "details withheld"), missing);
        Refused(() => New(failed, "o2").Unprotect(blob), SsFailed + Suffix("Failed", "details withheld"), failed);

        Assert.Contains("Secret Service", notFound.Message, StringComparison.Ordinal);
        Assert.Equal(0, missing.Stores);
        Assert.Equal(0, failed.Stores);
        Assert.False(Directory.Exists(Folder("o1")));
        Assert.False(Directory.Exists(Folder("o2")));
    }

    [LinuxOnlyFact("works on real files and modes")]
    public void P3_33_AKeyThatIsNotCanonicalIsRefusedOnOpeningToo()
    {
        var blob = SealWithSecretService(NewKey());
        var fake = new FakeKeyring { LookupResult = SecretServiceResultOf.Found(ValidAllZeroKey + "\n") };

        Refused(() => New(fake, "o").Unprotect(blob), SsInvalid, fake);
    }

    [LinuxOnlyFact("works on real files and modes")]
    public void P3_34_ASecretServiceStoreIsNeverMovedToTheKeyFile()
    {
        var key = NewKey();
        var blob = SealWithSecretService(key);
        var fake = new FakeKeyring { LookupResult = SecretServiceResultOf.Found(Base64(key)) };
        var protector = New(fake, "other");
        Assert.Equal(Plaintext, protector.Unprotect(blob));
        fake.LookupResult = new(SecretServiceStatus.Unavailable);

        Refused(() => protector.Protect(Plaintext), NoMigrate + Suffix("Unavailable", "details withheld"), fake, key);

        Assert.False(File.Exists(KeyFile("other")));
        Assert.Equal(0, fake.Stores);
    }

    [LinuxOnlyFact("works on real files and modes")]
    public void P3_34_AStoreOnTheSecretServiceDoesNotFallBackToTheFileEitherWhenTheKeyIsGone()
    {
        var key = NewKey();
        var blob = SealWithSecretService(key);
        var fake = new FakeKeyring { LookupResult = SecretServiceResultOf.Found(Base64(key)) };
        var protector = New(fake, "other");
        protector.Unprotect(blob);
        fake.LookupResult = new(SecretServiceStatus.NotFound);
        fake.StoreResult = new(SecretServiceStatus.Unavailable);

        Refused(() => protector.Protect(Plaintext), NoMigrate + Suffix("Unavailable", "details withheld"), fake);

        Assert.False(File.Exists(KeyFile("other")));
    }

    [LinuxOnlyFact("works on real files and modes")]
    public void P3_35_AKeyFileStoreStaysOnTheKeyFile_EvenWhenASecretServiceAppears()
    {
        var blob = SealWithKeyFile();
        var fake = new FakeKeyring { LookupResult = SecretServiceResultOf.Found(Base64(NewKey())) };
        var other = New(fake);

        var opened = other.Unprotect(blob);
        var resealed = other.Protect(opened);

        Assert.Equal(Plaintext, opened);
        Assert.Empty(fake.Calls);
        Assert.Equal(0x02, resealed[1]);
        AssertSealedWith(File.ReadAllBytes(KeyFile()), 0x02, resealed);
    }

    [LinuxOnlyFact("works on real files and modes")]
    public void P3_36_AKeyFileStoreWhoseKeyFileIsGoneIsRefused_AndNoNewKeyIsMade()
    {
        var blob = SealWithKeyFile();
        File.Delete(KeyFile());
        var fake = new FakeKeyring();

        var thrown = Refused(() => New(fake).Unprotect(blob), KeyFileMissing, fake);

        Assert.Contains("key file", thrown.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(KeyFile()));
        Assert.Empty(fake.Calls);
    }

    [LinuxOnlyFact("works on real files and modes")]
    public void P3_37_AfterAnOpenedKeyFileGoesMissing_TheNextProtectMakesANewKeyFile()
    {
        var blob = SealWithKeyFile();
        var fake = new FakeKeyring { LookupResult = new(SecretServiceStatus.Unavailable) };
        var protector = New(fake);
        Assert.Equal(Plaintext, protector.Unprotect(blob));
        var oldKey = File.ReadAllBytes(KeyFile());
        File.Delete(KeyFile());

        var resealed = protector.Protect(Plaintext);

        var newKey = File.ReadAllBytes(KeyFile());
        Assert.NotEqual(oldKey, newKey);
        AssertSealedWith(newKey, 0x02, resealed);
        Assert.Equal(0, fake.Lookups);
    }

    [LinuxOnlyFact("works on real files and modes")]
    public void P3_38_AKeyReplacedFromOutsideIsSeenAtOnce_AndPutBackIsSeenAtOnceToo()
    {
        var key = NewKey();
        var blob = SealWithSecretService(key);
        var fake = new FakeKeyring { LookupResult = SecretServiceResultOf.Found(Base64(NewKey())) };
        var protector = New(fake, "other");

        Assert.Throws<AuthenticationTagMismatchException>(() => protector.Unprotect(blob));
        fake.LookupResult = SecretServiceResultOf.Found(Base64(key));

        Assert.Equal(Plaintext, protector.Unprotect(blob));
    }

    [LinuxOnlyFact("works on real files and modes")]
    public void P3_39_ABlobThatIsTooShortOrOfAnUnknownVersionOrSourceIsRefusedBeforeAnythingIsAsked()
    {
        var good = SealWithKeyFile();
        var tooShort = RandomNumberGenerator.GetBytes(29);
        var badVersion = (byte[])good.Clone();
        badVersion[0] = 0x02;
        var sourceZero = (byte[])good.Clone();
        sourceZero[1] = 0x00;
        var sourceThree = (byte[])good.Clone();
        sourceThree[1] = 0x03;
        var fake = new FakeKeyring();
        var protector = New(fake, "fresh");

        Refused(() => protector.Unprotect(tooShort), Short, fake);
        Refused(() => protector.Unprotect(badVersion), "The sealed secret store has format version 2, which this build cannot read.", fake);
        Refused(() => protector.Unprotect(sourceZero), "The sealed secret store names an unknown key source 0.", fake);
        Refused(() => protector.Unprotect(sourceThree), "The sealed secret store names an unknown key source 3.", fake);

        Assert.Empty(fake.Calls);
        Assert.False(Directory.Exists(Folder("fresh")));
    }

    [LinuxOnlyFact("works on real files and modes")]
    public void P3_40_ATamperedBlobIsRefused()
    {
        var blob = SealWithKeyFile();
        var protector = New(new FakeKeyring());

        foreach (var position in new[] { 2, 13, 14, 29, 30, blob.Length - 1 })
        {
            var tampered = (byte[])blob.Clone();
            tampered[position] ^= 0x01;

            Assert.True(
                Record.Exception(() => protector.Unprotect(tampered)) is CryptographicException,
                $"flipping a bit at byte {position} was not refused");
        }
    }

    [LinuxOnlyFact("works on real files and modes")]
    public void P3_41_TheSourceByteIsAuthenticated()
    {
        var blob = SealWithKeyFile();
        var keyBytes = File.ReadAllBytes(KeyFile());
        var relabelled = (byte[])blob.Clone();
        relabelled[1] = 0x01;
        var fake = new FakeKeyring { LookupResult = SecretServiceResultOf.Found(Base64(keyBytes)) };

        Assert.IsAssignableFrom<CryptographicException>(Record.Exception(() => New(fake, "other").Unprotect(relabelled)));
    }

    [LinuxOnlyFact("works on real files and modes")]
    public void P3_42_AnEmptyPlaintextSealsToJustTheHeader_AndComesBackEmpty()
    {
        var fake = new FakeKeyring { LookupResult = SecretServiceResultOf.Found(Base64(NewKey())) };
        var protector = New(fake);

        var blob = protector.Protect([]);

        Assert.Equal(30, blob.Length);
        Assert.Empty(protector.Unprotect(blob));
    }

    // ---- what the exceptions may say ------------------------------------------------------------

    [LinuxOnlyFact("works on real files and modes")]
    public void P3_43_NothingUntrustedReachesAnExceptionMessage()
    {
        var plaintextText = Encoding.UTF8.GetString(Plaintext);

        var a = new FakeKeyring { LookupResult = new(SecretServiceStatus.Failed, null, plaintextText) };
        var ea = Refused(() => New(a, "a").Protect(Plaintext), LookupFailed + " (Failed: details withheld)", a);
        Assert.Null(ea.InnerException);

        var b = new FakeKeyring
        {
            LookupResult = new(SecretServiceStatus.NotFound),
            StoreFactory = secret => new(SecretServiceStatus.Failed, null, "refused " + secret),
        };
        var eb = Refused(() => New(b, "b").Protect(Plaintext), StoreFailed + " (Failed: details withheld)", b);
        Assert.Null(eb.InnerException);

        var c = new FakeKeyring { LookupResult = SecretServiceResultOf.Found(SecretText) };
        var ec = Refused(() => New(c, "c").Protect(Plaintext), SsInvalid, c);
        Assert.Null(ec.InnerException);

        var blob = SealWithSecretService(NewKey());
        var d = new FakeKeyring { LookupResult = new(SecretServiceStatus.Unavailable, null, plaintextText) };
        var ed = Refused(() => New(d, "d2").Unprotect(blob), SsUnavailable + " (Unavailable: details withheld)", d);
        Assert.Null(ed.InnerException);

        var e = new FakeKeyring { LookupResult = new(SecretServiceStatus.Failed, null, "g-io-error-quark 19") };
        var ee = Refused(() => New(e, "e").Unprotect(blob), SsFailed + " (Failed: g-io-error-quark 19)", e);
        Assert.Null(ee.InnerException);
    }

    [LinuxOnlyFact("works on real files and modes")]
    public void P3_44_NoKeyOrSecretLeaksIntoAnyExceptionOfTheFailureCases()
    {
        var key = NewKey();
        var blob = SealWithSecretService(key);
        var fake = new FakeKeyring { LookupResult = SecretServiceResultOf.Found(Base64(NewKey())) };
        var wrongKey = Record.Exception(() => New(fake, "w").Unprotect(blob));

        Assert.NotNull(wrongKey);
        AssertNothingLeaks(wrongKey, fake, key);
    }

    // ---- the key lock ---------------------------------------------------------------------------

    [LinuxOnlyFact("holds a real lock file")]
    public void P3_45_WhenAnotherCopyHoldsTheKeyLock_ProtectGivesUpAndAsksNothing()
    {
        Directory.CreateDirectory(Folder());
        File.SetUnixFileMode(Folder(), Mode("0700"));
        using var held = LinuxFileLock.TryAcquire(LockFile(), TimeSpan.Zero);
        Assert.NotNull(held);
        var fake = new FakeKeyring { LookupResult = new(SecretServiceStatus.NotFound) };
        var protector = New(fake, timeout: TimeSpan.FromMilliseconds(300));

        var clock = Stopwatch.StartNew();
        Refused(() => protector.Protect(Plaintext), LockBusy, fake);
        clock.Stop();

        Assert.True(clock.ElapsedMilliseconds >= 250, $"gave up after only {clock.ElapsedMilliseconds} ms");
        Assert.Empty(fake.Calls);
        Assert.False(File.Exists(KeyFile()));
    }

    [LinuxOnlyFact("races real protectors on one key")]
    public void P3_46_TwoProtectorsAtOnce_StoreTheKeyOnlyOnce_AndNeverAskTheKeyringTogether()
    {
        for (var round = 0; round < 10; round++)
        {
            var name = "r" + round;
            var fake = new FakeKeyring
            {
                LookupResult = new(SecretServiceStatus.NotFound),
                StoreResult = new(SecretServiceStatus.Done),
                LookupDelay = TimeSpan.FromMilliseconds(100),
            };
            var first = New(fake, name);
            var second = New(fake, name);

            var blobs = Task.WhenAll(Task.Run(() => first.Protect(Plaintext)), Task.Run(() => second.Protect(Plaintext)))
                .WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();

            Assert.True(fake.Stores == 1, $"round {round}: {fake.Stores} stores");
            Assert.True(fake.MaxConcurrentCalls == 1, $"round {round}: keyring calls overlapped");
            var key = Convert.FromBase64String(fake.Calls.Single(call => call.Method == "Store").Secret!);
            AssertSealedWith(key, 0x01, blobs[0]);
            AssertSealedWith(key, 0x01, blobs[1]);
        }
    }

    [LinuxOnlyFact("races real protectors on one key")]
    public async Task P3_46a_AHolderSlowerThanTheTimeout_MakesTheOtherGiveUpWithoutAskingAnything()
    {
        var fake = new FakeKeyring
        {
            LookupResult = new(SecretServiceStatus.NotFound),
            StoreResult = new(SecretServiceStatus.Done),
            HoldLookup = true,
        };
        var a = New(fake, timeout: TimeSpan.FromSeconds(5));
        var b = New(fake, timeout: TimeSpan.FromMilliseconds(200));
        var running = Task.Run(() => a.Protect(Plaintext));
        await fake.LookupEntered.WaitAsync(TimeSpan.FromSeconds(10));

        var clock = Stopwatch.StartNew();
        var refusal = await Task.Run(() => Record.Exception(() => b.Protect(Plaintext))).WaitAsync(TimeSpan.FromSeconds(10));
        clock.Stop();

        var thrown = Assert.IsType<CryptographicException>(refusal);
        Assert.Equal(LockBusy, thrown.Message);
        Assert.True(clock.ElapsedMilliseconds >= 150, $"gave up after only {clock.ElapsedMilliseconds} ms");
        Assert.Equal(0, fake.Stores);
        Assert.Equal(1, fake.Lookups);

        fake.LookupGate.Set();
        var aBlob = await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, fake.Stores);

        var bBlob = b.Protect(Plaintext);

        Assert.Equal(1, fake.Stores);
        var key = Convert.FromBase64String(fake.Calls.Single(call => call.Method == "Store").Secret!);
        AssertSealedWith(key, 0x01, aBlob);
        AssertSealedWith(key, 0x01, bBlob);
    }

    [LinuxOnlyFact("races real protectors on one key")]
    public void P3_46b_WhenTheStoreIsUnavailable_EachProtectorTriesOnceAndTheyShareOneKeyFile()
    {
        for (var round = 0; round < 10; round++)
        {
            var name = "r" + round;
            var fake = new FakeKeyring
            {
                LookupResult = new(SecretServiceStatus.NotFound),
                StoreResult = new(SecretServiceStatus.Unavailable),
                LookupDelay = TimeSpan.FromMilliseconds(100),
            };
            var first = New(fake, name);
            var second = New(fake, name);

            var blobs = Task.WhenAll(Task.Run(() => first.Protect(Plaintext)), Task.Run(() => second.Protect(Plaintext)))
                .WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();

            Assert.True(fake.Stores == 2, $"round {round}: {fake.Stores} stores");
            Assert.Equal(["secrets.key", "secrets.key.lock"], NamesIn(Folder(name)));
            var keyBytes = File.ReadAllBytes(KeyFile(name));
            Assert.Equal(32, keyBytes.Length);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, ModeOf(KeyFile(name)));
            Assert.Equal([0x01, 0x02], blobs[0][..2]);
            Assert.Equal([0x01, 0x02], blobs[1][..2]);
            Assert.Equal(Plaintext, first.Unprotect(blobs[1]));
            Assert.Equal(Plaintext, second.Unprotect(blobs[0]));
        }
    }

    [LinuxOnlyFact("races real protectors on one key")]
    public void P3_46c_WhenTheStoreFails_EachProtectorTriesOnceAndNoKeyFileIsMade()
    {
        for (var round = 0; round < 10; round++)
        {
            var name = "r" + round;
            var fake = new FakeKeyring
            {
                LookupResult = new(SecretServiceStatus.NotFound),
                StoreResult = new(SecretServiceStatus.Failed, null, "g-dbus-error-quark 9"),
                LookupDelay = TimeSpan.FromMilliseconds(100),
            };
            var first = New(fake, name);
            var second = New(fake, name);

            var errors = Task.WhenAll(
                    Task.Run(() => Record.Exception(() => first.Protect(Plaintext))),
                    Task.Run(() => Record.Exception(() => second.Protect(Plaintext))))
                .WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();

            foreach (var error in errors)
            {
                var thrown = Assert.IsType<CryptographicException>(error);
                Assert.Equal(StoreFailed + " (Failed: g-dbus-error-quark 9)", thrown.Message);
            }

            Assert.True(fake.Stores == 2, $"round {round}: {fake.Stores} stores");
            Assert.False(File.Exists(KeyFile(name)));
        }
    }

    // ---- folders and lock files that cannot be used ---------------------------------------------

    [LinuxOnlyFact("symbolic links are a Linux filesystem feature")]
    public void P3_50_AKeyFolderThatIsASymbolicLinkIsRefusedBeforeAnythingIsTouched()
    {
        Directory.CreateDirectory(_root);
        var target = Path.Combine(_root, "t");
        Directory.CreateDirectory(target);
        File.SetUnixFileMode(target, Mode("0755"));
        Directory.CreateSymbolicLink(Folder(), target);

        foreach (var result in new[] { SecretServiceResultOf.Found(Base64(NewKey())), new SecretServiceResult(SecretServiceStatus.Unavailable) })
        {
            var fake = new FakeKeyring { LookupResult = result };

            Refused(() => New(fake).Protect(Plaintext), ParentLink, fake);

            Assert.Empty(fake.Calls);
        }

        Assert.Equal(Mode("0755"), ModeOf(target));
        Assert.Empty(Directory.GetFileSystemEntries(target));
    }

    [LinuxOnlyFact("symbolic links are a Linux filesystem feature")]
    public void P3_51_AKeyFileStoreIsNotOpenedThroughAFolderThatIsASymbolicLink()
    {
        var blob = SealWithKeyFile();
        var target = Folder("t");
        Directory.Move(Folder(), target);
        File.SetUnixFileMode(Path.Combine(target, "secrets.key"), Mode("0644"));
        Directory.CreateSymbolicLink(Folder(), target);
        var fake = new FakeKeyring();

        Assert.IsType<CryptographicException>(Record.Exception(() => New(fake).Unprotect(blob)));

        Assert.Equal(Mode("0644"), ModeOf(Path.Combine(target, "secrets.key")));
        Assert.Empty(fake.Calls);
    }

    [LinuxNonRootOnlyFact("root can create folders in a read-only parent")]
    public void P3_52_AFolderThatCannotBeMadeIsReported_AndAsksNothing()
    {
        Directory.CreateDirectory(Folder("ro"));
        File.SetUnixFileMode(Folder("ro"), Mode("0500"));
        var fake = new FakeKeyring { LookupResult = new(SecretServiceStatus.Unavailable) };
        var protector = new LinuxSecretProtector(Service, Account, fake, Path.Combine(Folder("ro"), "d", "secrets.key"));

        var thrown = Refused(() => protector.Protect(Plaintext), DirError, fake);

        Assert.True(thrown.InnerException is UnauthorizedAccessException or IOException);
        Assert.Empty(fake.Calls);
    }

    [LinuxOnlyFact("symbolic links are a Linux filesystem feature")]
    public void P3_53_ALockFileThatIsASymbolicLinkIsReported_AndAsksNothing()
    {
        Directory.CreateDirectory(Folder());
        File.SetUnixFileMode(Folder(), Mode("0700"));
        var anywhere = Path.Combine(_root, "anywhere");
        File.WriteAllText(anywhere, "x");
        File.CreateSymbolicLink(LockFile(), anywhere);
        var fake = new FakeKeyring { LookupResult = SecretServiceResultOf.Found(Base64(NewKey())) };

        var thrown = Refused(() => New(fake).Protect(Plaintext), LockError, fake);

        Assert.IsAssignableFrom<IOException>(thrown.InnerException);
        Assert.Empty(fake.Calls);
        Assert.Equal("x", File.ReadAllText(anywhere));
    }

    [LinuxOnlyFact("sets and reads real file modes, as root or not")]
    public void P3_54_ARestrictiveLockFileOfTheUsersOwnIsMadeUsable()
    {
        foreach (var mode in new[] { "0000", "0400" })
        {
            var name = "m" + mode;
            Directory.CreateDirectory(Folder(name));
            File.SetUnixFileMode(Folder(name), Mode("0700"));
            File.WriteAllBytes(LockFile(name), []);
            File.SetUnixFileMode(LockFile(name), Mode(mode));
            var key = NewKey();
            var fake = new FakeKeyring { LookupResult = SecretServiceResultOf.Found(Base64(key)) };

            var blob = New(fake, name).Protect(Plaintext);

            AssertSealedWith(key, 0x01, blob);
            Assert.True((UnixFileMode.UserRead | UnixFileMode.UserWrite) == ModeOf(LockFile(name)), $"lock file mode {mode} was not made 0600");
            Assert.True(fake.Lookups == 1, $"lock file mode {mode}: {fake.Lookups} lookups");
        }
    }

    [LinuxOnlyFact("works on real files and modes")]
    public void ANewKeyIsRandom_DifferentForEveryStore()
    {
        var first = new FakeKeyring { LookupResult = new(SecretServiceStatus.NotFound), StoreResult = new(SecretServiceStatus.Done) };
        var second = new FakeKeyring { LookupResult = new(SecretServiceStatus.NotFound), StoreResult = new(SecretServiceStatus.Done) };

        New(first, "a").Protect(Plaintext);
        New(second, "b").Protect(Plaintext);

        var one = Convert.FromBase64String(first.Calls.Single(call => call.Method == "Store").Secret!);
        var two = Convert.FromBase64String(second.Calls.Single(call => call.Method == "Store").Secret!);
        Assert.NotEqual(one, two);
        Assert.Contains(one, b => b != 0);
    }

    [LinuxOnlyFact("works on real files and modes")]
    public void AnExistingKeyFolderOpenToOthersIsMadePrivateBeforeAnythingIsAsked()
    {
        Directory.CreateDirectory(Folder());
        File.SetUnixFileMode(Folder(), Mode("0755"));
        var key = NewKey();
        var fake = new FakeKeyring { LookupResult = SecretServiceResultOf.Found(Base64(key)) };

        var blob = New(fake).Protect(Plaintext);

        AssertSealedWith(key, 0x01, blob);
        Assert.Equal(Mode("0700"), ModeOf(Folder()));
    }

    // ---- with the real secret store on top ------------------------------------------------------

    private DpapiSecretStore NewStore(FakeKeyring fake) =>
        new(Path.Combine(Folder(), "secrets.dat"), New(fake), new AtomicFileWriter());

    private static IEnumerable<string> FilesContaining(string root, string text)
    {
        var needle = Encoding.UTF8.GetBytes(text);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (new FileInfo(file).LinkTarget is null && File.ReadAllBytes(file).AsSpan().IndexOf(needle) >= 0)
            {
                yield return file;
            }
        }
    }

    [LinuxOnlyFact("writes real files")]
    public async Task P3_47_WithoutASecretService_TheStoreRoundTripsAndNeverHoldsThePlaintextOnDisk()
    {
        var fake = new FakeKeyring { LookupResult = new(SecretServiceStatus.Unavailable) };
        var store = NewStore(fake);

        await store.SetAsync("openai", SecretText);
        var back = await store.GetAsync("openai");

        Assert.Equal(SecretText, back);
        Assert.Empty(FilesContaining(_root, SecretText));
        Assert.Equal([0x01, 0x02], File.ReadAllBytes(Path.Combine(Folder(), "secrets.dat"))[..2]);
        Assert.Equal(32, new FileInfo(KeyFile()).Length);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, ModeOf(KeyFile()));
        Assert.Equal(Mode("0700"), ModeOf(Folder()));
    }

    [LinuxOnlyFact("writes real files")]
    public async Task P3_48_WithASecretService_TheStoreRoundTripsAndNoKeyFileIsMade()
    {
        var fake = new FakeKeyring { LookupResult = new(SecretServiceStatus.NotFound), StoreResult = new(SecretServiceStatus.Done) };
        var store = NewStore(fake);

        await store.SetAsync("openai", SecretText);
        var back = await store.GetAsync("openai");

        Assert.Equal(SecretText, back);
        Assert.Empty(FilesContaining(_root, SecretText));
        Assert.Equal([0x01, 0x01], File.ReadAllBytes(Path.Combine(Folder(), "secrets.dat"))[..2]);
        Assert.False(File.Exists(KeyFile()));
    }

    [LinuxOnlyFact("writes real files")]
    public async Task P3_49_AStoreThatCannotBeOpenedIsNeverOverwritten_AndOpensAgainWhenTheKeyIsBack()
    {
        var first = new FakeKeyring { LookupResult = new(SecretServiceStatus.NotFound), StoreResult = new(SecretServiceStatus.Done) };
        await NewStore(first).SetAsync("openai", SecretText);
        var storedKey = first.Calls.Single(call => call.Method == "Store").Secret!;
        var path = Path.Combine(Folder(), "secrets.dat");
        var before = File.ReadAllBytes(path);

        var gone = new FakeKeyring { LookupResult = new(SecretServiceStatus.Unavailable) };
        await Assert.ThrowsAsync<CryptographicException>(() => NewStore(gone).SetAsync("x", "y"));

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(File.Exists(KeyFile()));
        var back = new FakeKeyring { LookupResult = SecretServiceResultOf.Found(storedKey) };
        Assert.Equal(SecretText, await NewStore(back).GetAsync("openai"));
    }

    // ---- fake keyring ---------------------------------------------------------------------------

    private static class SecretServiceResultOf
    {
        public static SecretServiceResult Found(string secret) => new(SecretServiceStatus.Found, secret);
    }

    private sealed record Call(string Method, string Schema, IReadOnlyDictionary<string, string> Attributes, string? Label, string? Secret);

    /// <summary>
    /// A keyring that answers what the test says. A store that succeeds makes later lookups find
    /// that secret. It is thread-safe, records every call, and notes if two calls ever overlapped.
    /// </summary>
    private sealed class FakeKeyring : ISecretServiceKeyring
    {
        private readonly object _sync = new();
        private readonly List<Call> _calls = [];
        private readonly TaskCompletionSource _lookupEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string? _stored;
        private SecretServiceResult _lookupResult = new(SecretServiceStatus.NotFound);
        private int _inFlight;
        private int _maxInFlight;

        public ManualResetEventSlim LookupGate { get; } = new(false);

        public Task LookupEntered => _lookupEntered.Task;

        public bool HoldLookup { get; set; }

        public TimeSpan LookupDelay { get; set; }

        public SecretServiceResult StoreResult { get; set; } = new(SecretServiceStatus.Done);

        public Func<string, SecretServiceResult>? StoreFactory { get; set; }

        public SecretServiceResult LookupResult
        {
            get { lock (_sync) { return _lookupResult; } }
            set
            {
                lock (_sync)
                {
                    _lookupResult = value;
                    _stored = null;
                }
            }
        }

        public IReadOnlyList<Call> Calls
        {
            get { lock (_sync) { return [.. _calls]; } }
        }

        public int Lookups => Calls.Count(call => call.Method == "Lookup");

        public int Stores => Calls.Count(call => call.Method == "Store");

        public int MaxConcurrentCalls
        {
            get { lock (_sync) { return _maxInFlight; } }
        }

        private void Enter(Call call)
        {
            lock (_sync)
            {
                _calls.Add(call);
                _inFlight++;
                _maxInFlight = Math.Max(_maxInFlight, _inFlight);
            }
        }

        private void Exit()
        {
            lock (_sync)
            {
                _inFlight--;
            }
        }

        public SecretServiceResult Lookup(string schemaName, IReadOnlyDictionary<string, string> attributes)
        {
            Enter(new Call("Lookup", schemaName, new Dictionary<string, string>(attributes), null, null));
            try
            {
                bool hold;
                lock (_sync)
                {
                    hold = HoldLookup;
                    HoldLookup = false;
                }

                if (hold)
                {
                    _lookupEntered.TrySetResult();
                    if (!LookupGate.Wait(TimeSpan.FromSeconds(10)))
                    {
                        throw new TimeoutException("the test never opened the lookup gate");
                    }
                }

                if (LookupDelay > TimeSpan.Zero)
                {
                    Thread.Sleep(LookupDelay);
                }

                lock (_sync)
                {
                    return _stored is not null ? new SecretServiceResult(SecretServiceStatus.Found, _stored) : _lookupResult;
                }
            }
            finally
            {
                Exit();
            }
        }

        public SecretServiceResult Store(string schemaName, IReadOnlyDictionary<string, string> attributes, string label, string secret)
        {
            Enter(new Call("Store", schemaName, new Dictionary<string, string>(attributes), label, secret));
            try
            {
                var result = StoreFactory?.Invoke(secret) ?? StoreResult;
                if (result.Status == SecretServiceStatus.Done)
                {
                    lock (_sync)
                    {
                        _stored = secret;
                    }
                }

                return result;
            }
            finally
            {
                Exit();
            }
        }

        public SecretServiceResult Clear(string schemaName, IReadOnlyDictionary<string, string> attributes)
        {
            Enter(new Call("Clear", schemaName, new Dictionary<string, string>(attributes), null, null));
            Exit();
            return new SecretServiceResult(SecretServiceStatus.Done);
        }
    }
}
