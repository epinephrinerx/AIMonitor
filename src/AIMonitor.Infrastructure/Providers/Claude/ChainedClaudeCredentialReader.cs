using AIMonitor.Application.Time;

namespace AIMonitor.Infrastructure.Providers.Claude;

/// <summary>
/// Tries each credential source in turn and stops at the first that has something to say.
///
/// Only <see cref="ClaudeCredentialStatus.Missing"/> moves on to the next source: a login that
/// exists but is unreadable, malformed or expired is the answer, and quietly reaching past it to
/// another source would hide a problem the user needs to see. That ordering is what lets a Mac with
/// <c>CLAUDE_CONFIG_DIR</c> set keep using the file while every other Mac falls through to the
/// Keychain.
/// </summary>
public sealed class ChainedClaudeCredentialReader : IClaudeCredentialReader
{
    private readonly IReadOnlyList<IClaudeCredentialReader> _sources;

    public ChainedClaudeCredentialReader(params IClaudeCredentialReader[] sources)
        : this((IReadOnlyList<IClaudeCredentialReader>)sources)
    {
    }

    public ChainedClaudeCredentialReader(IReadOnlyList<IClaudeCredentialReader> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0)
        {
            throw new ArgumentException("At least one credential source is required.", nameof(sources));
        }

        if (sources.Any(source => source is null))
        {
            throw new ArgumentException("No credential source may be null.", nameof(sources));
        }

        _sources = sources;
    }

    public async Task<ClaudeCredentialReadResult> ReadAsync(
        string path, IClock clock, CancellationToken cancellationToken)
    {
        ClaudeCredentialReadResult? last = null;
        foreach (var source in _sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            last = await source.ReadAsync(path, clock, cancellationToken).ConfigureAwait(false);
            if (last.Status != ClaudeCredentialStatus.Missing)
            {
                return last;
            }
        }

        // Every source came up empty; the last one's description is the most specific place to
        // point the user at, because it is the one this platform actually uses.
        return last!;
    }
}
