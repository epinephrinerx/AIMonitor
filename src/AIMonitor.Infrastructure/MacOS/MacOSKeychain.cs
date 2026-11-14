using System.Diagnostics;

namespace AIMonitor.Infrastructure.MacOS;

/// <summary>
/// Builds the <c>/usr/bin/security</c> invocations that back the login Keychain.
///
/// Anything carrying a secret is built for batch mode (<c>security -i</c>), which reads its
/// arguments from stdin: <c>add-generic-password -w &lt;secret&gt;</c> passed as real process
/// arguments would leave the secret in this process's command line, where any other process of the
/// same user can read it out of <c>ps</c>. The 1.x macOS port established this and confirmed that
/// <c>-w</c> never reads stdin of its own accord — it swallows the next argument as the value.
///
/// Every member is static and free of I/O, so the shape of each command is testable on any host.
/// </summary>
internal static class MacOSKeychain
{
    public const string ExecutablePath = "/usr/bin/security";

    /// <summary>Shown in Keychain Access, where an unlabelled item is unidentifiable.</summary>
    public const string ItemLabel = "AI Usage Monitor";

    /// <summary>Quotes a value for batch mode, which parses a shell-like mini syntax.</summary>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var escaped = value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }

    /// <summary>Batch mode: the secret travels on stdin, so argv carries only the mode switch.</summary>
    public static ProcessStartInfo BuildBatchStartInfo() =>
        MacOSCommand.StartInfo(ExecutablePath, "-i");

    /// <summary>
    /// Reads the stored secret. This is the call that raises the Keychain's access prompt, so it
    /// belongs on a worker thread, never on the thread drawing the window.
    /// </summary>
    public static ProcessStartInfo BuildReadStartInfo(string service, string account)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        return MacOSCommand.StartInfo(ExecutablePath, "find-generic-password", "-a", account, "-s", service, "-w");
    }

    /// <summary>
    /// Asks only whether the item exists. Without <c>-w</c> the command returns metadata and never
    /// reads the secret, so it does not raise the access prompt — which is what makes it safe to
    /// call while the user is looking at a window.
    /// </summary>
    public static ProcessStartInfo BuildProbeStartInfo(string service, string account)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        return MacOSCommand.StartInfo(ExecutablePath, "find-generic-password", "-a", account, "-s", service);
    }

    /// <summary>
    /// The batch-mode command that stores <paramref name="secret"/>; feed it on stdin to the process
    /// built by <see cref="BuildBatchStartInfo"/>. <c>-U</c> updates an existing item instead of
    /// failing, so re-entering a value replaces it rather than leaving two items to choose between.
    /// </summary>
    public static string BuildAddCommand(string service, string account, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        ArgumentException.ThrowIfNullOrEmpty(secret);
        return $"add-generic-password -U -a {Quote(account)} -s {Quote(service)} "
            + $"-l {Quote(ItemLabel)} -w {Quote(secret)}";
    }

    /// <summary>
    /// Reads an item filed under a service alone, with no account attribute — which is how another
    /// application's item (Claude Code's login, say) is usually stored.
    /// </summary>
    public static ProcessStartInfo BuildServiceReadStartInfo(string service)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        return MacOSCommand.StartInfo(ExecutablePath, "find-generic-password", "-s", service, "-w");
    }

    /// <summary>Presence of such an item, without reading it and so without an access prompt.</summary>
    public static ProcessStartInfo BuildServiceProbeStartInfo(string service)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        return MacOSCommand.StartInfo(ExecutablePath, "find-generic-password", "-s", service);
    }

    /// <summary>The batch-mode command that deletes the item, for when the user clears the value.</summary>
    public static string BuildDeleteCommand(string service, string account)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        return $"delete-generic-password -a {Quote(account)} -s {Quote(service)}";
    }
}
