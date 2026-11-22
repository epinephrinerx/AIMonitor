using System.Text.RegularExpressions;

namespace AIMonitor.Infrastructure.Linux;

internal static class SecretServiceErrors
{
    public const string NoSessionBusMessage = "No D-Bus session bus (DBUS_SESSION_BUS_ADDRESS is not set and $XDG_RUNTIME_DIR/bus does not exist).";
    public const string LibraryMissingMessage = "libsecret is not installed (libsecret-1.so.0 could not be loaded).";
    public const string TimeoutMessage = "The Secret Service did not answer within 30 seconds.";
    public const string StoreFalseMessage = "secret_password_storev_sync returned FALSE without an error.";
    private const string Withheld = "details withheld";
    private static readonly Regex SafeNative = new(@"\A(?:g-io-error-quark|g-dbus-error-quark|secret-error|other) -?[0-9]{1,10}\z", RegexOptions.CultureInvariant);

    public static SecretServiceStatus Classify(string? domain, int code, bool timedOut)
    {
        if (timedOut) return SecretServiceStatus.Unavailable;
        if (string.Equals(domain, "g-io-error-quark", StringComparison.Ordinal))
            return code is 1 or 24 ? SecretServiceStatus.Unavailable : SecretServiceStatus.Failed;
        if (string.Equals(domain, "g-dbus-error-quark", StringComparison.Ordinal))
            return code is 2 or 3 or 4 or 5 or 6 or 11 or 12 or 13 or 15 or 20 or (>= 23 and <= 34)
                ? SecretServiceStatus.Unavailable : SecretServiceStatus.Failed;
        return SecretServiceStatus.Failed;
    }

    public static bool HasSessionBus(string? dbusSessionBusAddress, string? xdgRuntimeDir, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        if (!string.IsNullOrWhiteSpace(dbusSessionBusAddress)) return true;
        return xdgRuntimeDir is { Length: > 0 } && xdgRuntimeDir[0] == '/' && !xdgRuntimeDir.Contains('\0')
            && fileExists(xdgRuntimeDir.TrimEnd('/') + "/bus");
    }

    public static string SafeDetail(string? message) => message is not null &&
        (message is NoSessionBusMessage or LibraryMissingMessage or TimeoutMessage or StoreFalseMessage || SafeNative.IsMatch(message))
            ? message : Withheld;
}
