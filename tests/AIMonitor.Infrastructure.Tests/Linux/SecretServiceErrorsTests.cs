using AIMonitor.Infrastructure.Linux;

namespace AIMonitor.Infrastructure.Tests.Linux;

/// <summary>
/// The pure rules for turning Secret Service errors into a status and into text that is safe to
/// show (R-024). Nothing here touches the disk, the environment or the keyring, so it runs on
/// every operating system.
/// </summary>
public sealed class SecretServiceErrorsTests
{
    private const string Plaintext = "{\"openai\":\"sk-test-0123456789abcdef\"}";

    // ---- Classify ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(24)]
    public void Classify_TheGioCodesForNoServiceAreUnavailable(int code)
    {
        Assert.Equal(SecretServiceStatus.Unavailable, SecretServiceErrors.Classify("g-io-error-quark", code, false));
    }

    [Theory]
    [InlineData(19)]
    [InlineData(0)]
    [InlineData(14)]
    [InlineData(2)]
    [InlineData(25)]
    [InlineData(-1)]
    public void Classify_AnyOtherGioCodeIsFailed_IncludingCancelled(int code)
    {
        Assert.Equal(SecretServiceStatus.Failed, SecretServiceErrors.Classify("g-io-error-quark", code, false));
    }

    [Theory]
    [InlineData("g-io-error-quark", 19)]
    [InlineData("secret-error", 2)]
    [InlineData("g-dbus-error-quark", 9)]
    [InlineData(null, 0)]
    public void Classify_ATimeoutIsAlwaysUnavailable(string? domain, int code)
    {
        Assert.Equal(SecretServiceStatus.Unavailable, SecretServiceErrors.Classify(domain, code, true));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(15)]
    [InlineData(20)]
    [InlineData(23)]
    [InlineData(24)]
    [InlineData(27)]
    [InlineData(33)]
    [InlineData(34)]
    public void Classify_TheDbusCodesForNoServiceAreUnavailable(int code)
    {
        Assert.Equal(SecretServiceStatus.Unavailable, SecretServiceErrors.Classify("g-dbus-error-quark", code, false));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(19)]
    [InlineData(21)]
    [InlineData(22)]
    [InlineData(35)]
    [InlineData(-1)]
    public void Classify_AnyOtherDbusCodeIsFailed(int code)
    {
        Assert.Equal(SecretServiceStatus.Failed, SecretServiceErrors.Classify("g-dbus-error-quark", code, false));
    }

    [Theory]
    [InlineData("secret-error", 2)]
    [InlineData("secret-error", 1)]
    [InlineData("secret-error", 24)]
    [InlineData("secret-error", 9)]
    [InlineData(null, 1)]
    [InlineData("G-IO-ERROR-QUARK", 1)]
    [InlineData("g-io-error-quark ", 1)]
    [InlineData("", 1)]
    [InlineData("other", 24)]
    public void Classify_AnUnknownOrDifferentlyCasedDomainIsFailed(string? domain, int code)
    {
        Assert.Equal(SecretServiceStatus.Failed, SecretServiceErrors.Classify(domain, code, false));
    }

    // ---- HasSessionBus ----------------------------------------------------------------------------

    [Fact]
    public void HasSessionBus_AnAddressIsEnoughAndTheDiskIsNotAsked()
    {
        var asked = false;

        var result = SecretServiceErrors.HasSessionBus("unix:path=/x", null, _ =>
        {
            asked = true;
            return false;
        });

        Assert.True(result);
        Assert.False(asked);
    }

    [Theory]
    [InlineData("/run/user/1")]
    [InlineData("/run/user/1/")]
    [InlineData("/run/user/1//")]
    public void HasSessionBus_WithoutAnAddress_LooksForTheBusSocketInTheRuntimeFolder(string runtime)
    {
        Assert.True(SecretServiceErrors.HasSessionBus(null, runtime, path => path == "/run/user/1/bus"));
    }

    [Fact]
    public void HasSessionBus_AWhitespaceAddressDoesNotCount_ButTheSocketStillDoes()
    {
        Assert.True(SecretServiceErrors.HasSessionBus("  ", "/run/user/1", path => path == "/run/user/1/bus"));
        Assert.False(SecretServiceErrors.HasSessionBus("  ", "/run/user/1", _ => false));
    }

    [Fact]
    public void HasSessionBus_TheRootFolderGivesAPathDirectlyUnderTheRoot()
    {
        Assert.True(SecretServiceErrors.HasSessionBus(null, "/", path => path == "/bus"));
    }

    [Theory]
    [InlineData(null, "run/user/1")]
    [InlineData(null, null)]
    [InlineData(null, "")]
    [InlineData(null, "/r\0")]
    [InlineData(null, "   ")]
    public void HasSessionBus_AnUnusableRuntimeFolderMeansNoBus_EvenIfEveryFileExists(string? address, string? runtime)
    {
        Assert.False(SecretServiceErrors.HasSessionBus(address, runtime, _ => true));
    }

    [Fact]
    public void HasSessionBus_WithoutTheSocketThereIsNoBus()
    {
        Assert.False(SecretServiceErrors.HasSessionBus(null, "/run/user/1", _ => false));
    }

    [Fact]
    public void HasSessionBus_ANullFileCheckIsRefused()
    {
        Assert.Equal("fileExists", Assert.Throws<ArgumentNullException>(() => SecretServiceErrors.HasSessionBus(null, null, null!)).ParamName);
    }

    // ---- SafeDetail -------------------------------------------------------------------------------

    [Fact]
    public void ShimMessages_AreTheExactTextsOfTheContract()
    {
        Assert.Equal(
            "No D-Bus session bus (DBUS_SESSION_BUS_ADDRESS is not set and $XDG_RUNTIME_DIR/bus does not exist).",
            SecretServiceErrors.NoSessionBusMessage);
        Assert.Equal(
            "libsecret is not installed (libsecret-1.so.0 could not be loaded).",
            SecretServiceErrors.LibraryMissingMessage);
        Assert.Equal("The Secret Service did not answer within 30 seconds.", SecretServiceErrors.TimeoutMessage);
        Assert.Equal(
            "secret_password_storev_sync returned FALSE without an error.",
            SecretServiceErrors.StoreFalseMessage);
    }

    [Theory]
    [InlineData("g-io-error-quark 19")]
    [InlineData("g-dbus-error-quark 2")]
    [InlineData("secret-error 2")]
    [InlineData("other -5")]
    [InlineData("secret-error 0")]
    [InlineData("g-io-error-quark 1234567890")]
    [InlineData("other -1234567890")]
    public void SafeDetail_PassesTheErrorShapeOfTheShimThrough(string message)
    {
        Assert.Equal(message, SecretServiceErrors.SafeDetail(message));
    }

    [Fact]
    public void SafeDetail_PassesEachConstantMessageOfTheShimThrough()
    {
        foreach (var message in new[]
        {
            SecretServiceErrors.NoSessionBusMessage,
            SecretServiceErrors.LibraryMissingMessage,
            SecretServiceErrors.TimeoutMessage,
            SecretServiceErrors.StoreFalseMessage,
        })
        {
            Assert.Equal(message, SecretServiceErrors.SafeDetail(message));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("refused s3cr3t")]
    [InlineData(Plaintext)]
    [InlineData("g-io-error-quark 19 extra")]
    [InlineData("G-IO-ERROR-QUARK 1")]
    [InlineData("g-io-error-quark 12345678901")]
    [InlineData("Could not connect: No such file or directory")]
    [InlineData("g-io-error-quark 19\n")]
    [InlineData("g-io-error-quark 19\r\n")]
    [InlineData("\ng-io-error-quark 19")]
    [InlineData("g-io-error-quark 19\nsk-test-0123456789abcdef")]
    [InlineData("g-io-error-quark ١٩")]
    [InlineData("g-io-error-quark  19")]
    [InlineData("g-io-error-quark +19")]
    [InlineData("g-io-error-quark ")]
    [InlineData("g-io-error-quark")]
    [InlineData("other  1")]
    [InlineData(" other 1")]
    [InlineData("other 1 ")]
    [InlineData("unknown 1")]
    public void SafeDetail_WithholdsEverythingElse(string? message)
    {
        Assert.Equal("details withheld", SecretServiceErrors.SafeDetail(message));
    }

    [Fact]
    public void SafeDetail_WithholdsAConstantMessageWithATrailingNewline()
    {
        Assert.Equal("details withheld", SecretServiceErrors.SafeDetail(SecretServiceErrors.TimeoutMessage + "\n"));
        Assert.Equal("details withheld", SecretServiceErrors.SafeDetail(SecretServiceErrors.StoreFalseMessage + " "));
        Assert.Equal("details withheld", SecretServiceErrors.SafeDetail(SecretServiceErrors.TimeoutMessage.ToUpperInvariant()));
    }

    // ---- SecretServiceResult ----------------------------------------------------------------------

    [Fact]
    public void Result_ToString_NamesTheStatusAndTheMessageButNeverTheSecret()
    {
        var result = new SecretServiceResult(SecretServiceStatus.Found, "s3cr3t", "m");

        Assert.Equal("Found: m", result.ToString());
        Assert.DoesNotContain("s3cr3t", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Result_ToString_WithoutAMessageEndsAfterTheColon()
    {
        Assert.Equal("NotFound: ", new SecretServiceResult(SecretServiceStatus.NotFound).ToString());
    }
}
