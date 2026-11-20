using AIMonitor.Application.Time;
using AIMonitor.Infrastructure.Providers.Claude;
using System.Diagnostics;

namespace AIMonitor.Infrastructure.MacOS;

/// <summary>
/// Reads Claude Code's login from the macOS login Keychain.
///
/// On Windows, Claude Code keeps that JSON in a file under its config directory. On macOS it files
/// the same JSON in the Keychain instead and the file is usually absent, so a build that only looks
/// for the file reports "not signed in" on every Mac — which is what the 1.x macOS port found and
/// fixed the same way.
///
/// Read-only, like the file reader: Claude Code owns the refresh cycle for these tokens, and a
/// second process writing them can invalidate the user's login.
/// </summary>
internal sealed class ClaudeKeychainCredentialReader : IClaudeCredentialReader
{
    /// <summary>What Claude Code calls its Keychain item.</summary>
    public const string KeychainService = "Claude Code-credentials";

    /// <summary>
    /// The item's ACL names Claude Code, not this app, so the first read puts an "allow access?"
    /// dialog in front of the user. The deadline is generous on purpose: a user who takes a moment
    /// over that dialog must not have the read killed underneath them.
    /// </summary>
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromMinutes(2);

    private readonly string _service;
    private readonly Func<ProcessStartInfo, string?, TimeSpan, CommandResult> _run;

    public ClaudeKeychainCredentialReader()
        : this(KeychainService, MacOSCommand.Run)
    {
    }

    internal ClaudeKeychainCredentialReader(
        string service,
        Func<ProcessStartInfo, string?, TimeSpan, CommandResult> run)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        _service = service;
        _run = run ?? throw new ArgumentNullException(nameof(run));
    }

    /// <summary>How to describe where the login is expected to be, in a message to the user.</summary>
    public string Source => $"the login Keychain (service \"{_service}\")";

    /// <summary>
    /// <paramref name="path"/> is ignored: this source is not a path. It stays in the signature
    /// because the contract is shared with the file reader.
    /// </summary>
    public async Task<ClaudeCredentialReadResult> ReadAsync(
        string path, IClock clock, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clock);
        cancellationToken.ThrowIfCancellationRequested();

        if (!OperatingSystem.IsMacOS())
        {
            return ClaudeCredentialReadResult.Missing(Source);
        }

        CommandResult result;
        try
        {
            // security blocks while it waits on the Keychain, so it is run off the calling thread.
            result = await Task.Run(
                () => _run(MacOSKeychain.BuildServiceReadStartInfo(_service), null, ReadTimeout),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception
            is PlatformNotSupportedException
            or InvalidOperationException
            or TimeoutException
            or IOException)
        {
            return ClaudeCredentialReadResult.Invalid(Source);
        }

        if (!result.Succeeded)
        {
            // Exit 44 is "no such item", the ordinary not-signed-in case. Any other failure — a
            // locked Keychain, a denied prompt — is also reported as no login here rather than as a
            // broken item, because the user's next step is the same: sign in to Claude Code.
            return ClaudeCredentialReadResult.Missing(Source);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // security appends a newline of its own, which JSON parsing would otherwise trip over.
        return ClaudeCredentialReader.Parse(result.StandardOutput.Trim(), Source, clock);
    }
}
