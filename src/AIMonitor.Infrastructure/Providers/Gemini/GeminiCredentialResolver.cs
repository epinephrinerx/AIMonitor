using AIMonitor.Application.Time;
using AIMonitor.Domain;

namespace AIMonitor.Infrastructure.Providers.Gemini;

/// <summary>Result of resolving Gemini's credential sources: the provider-neutral detection outcome
/// plus (when found) the credential needed to actually make a request in a later phase.
/// <see cref="Credential"/> is Infrastructure-internal and never leaves this boundary -
/// <see cref="Detection"/> is what travels into a <see cref="ProviderSnapshot"/>.</summary>
internal sealed record GeminiResolution(DetectionInfo Detection, GeminiCredential? Credential);

/// <summary>
/// Resolves Gemini's credential in the priority order PAR-011/PAR-012 require: a service-account file
/// the user saved in this app, then the <c>GOOGLE_APPLICATION_CREDENTIALS</c> file, then Gemini CLI's
/// own OAuth login, then a service account parked in gcloud's application-default location - most
/// explicit first, matching the Python baseline's <c>GEMINI_SOURCES</c>. Every probe is read-only;
/// nothing here ever writes to, refreshes, or renews a credential, and no probe stops the walk early
/// just because it found something Limited or Expired - only a Connected credential short-circuits,
/// so a usable login later in the list is never hidden by an unusable one found first.
///
/// This type has no public surface yet (matching the equally provisional <c>OpenAiCredentialResolver</c>):
/// no live-quota client or composition-root wiring consumes Gemini credentials in this phase, so
/// nothing outside this assembly needs it. A later phase that adds one can reference this directly.
/// </summary>
internal static class GeminiCredentialResolver
{
    public const string ProviderId = "gemini";

    public const string ManualSourceId = "manual";
    private const string ManualSourceLabel = "Service account saved in this app";
    public const string EnvSourceId = "env";
    private const string EnvSourceLabel = "GOOGLE_APPLICATION_CREDENTIALS";
    public const string GeminiCliSourceId = "gemini_cli";
    private const string GeminiCliSourceLabel = "Gemini CLI login";
    private const string GeminiCliRefreshHint = "Run `gemini` and sign in again to refresh the token.";
    public const string GcloudAdcSourceId = "gcloud_adc";
    private const string GcloudAdcSourceLabel = "gcloud application-default credentials";

    private const string GoogleApplicationCredentialsVariable = "GOOGLE_APPLICATION_CREDENTIALS";

    /// <summary>
    /// Walks manual (saved service-account file) -&gt; env (<c>GOOGLE_APPLICATION_CREDENTIALS</c>) -&gt;
    /// Gemini CLI login -&gt; gcloud ADC, in that order, probing lazily and returning as soon as a
    /// source proves both present and usage-capable. A source that yields a credential which cannot
    /// actually read usage (an incomplete/malformed service-account file, or a gcloud user login) is
    /// remembered as a Limited/Expired fallback rather than skipped outright - but probing continues
    /// past it, so "nothing at all is connected" is never confused with "something is connected but
    /// cannot be used yet", matching the Python baseline's <c>resolve()</c>.
    /// </summary>
    /// <param name="savedServiceAccountPath">The service-account file path saved in this app's
    /// settings, if any - trimmed of surrounding whitespace and a single layer of wrapping double
    /// quotes, matching the baseline's handling of a path pasted from Windows Explorer's "Copy as
    /// path".</param>
    /// <param name="environmentVariableReader">Reads a named environment variable; injected so tests
    /// never depend on the real process environment.</param>
    /// <param name="userProfileDirectory">The user profile directory Gemini CLI's and gcloud's
    /// well-known subpaths are resolved under; injected so tests never touch the real
    /// <c>%USERPROFILE%</c>.</param>
    public static async Task<GeminiResolution> ResolveAsync(
        string? savedServiceAccountPath,
        Func<string, string?> environmentVariableReader,
        string userProfileDirectory,
        IClock clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environmentVariableReader);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrWhiteSpace(userProfileDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        var candidatesSoFar = new List<DetectionCandidate>();
        string? fallbackSourceId = null;
        string? fallbackSourceLabel = null;
        GeminiCredential? fallbackCredential = null;

        GeminiResolution? Consider(string sourceId, string sourceLabel, GeminiCredential? credential)
        {
            if (credential is null)
            {
                return null;
            }

            candidatesSoFar.Add(new DetectionCandidate(sourceId, sourceLabel));
            var state = StateOf(credential, clock);
            if (state == DetectionState.Connected)
            {
                return new GeminiResolution(
                    BuildDetectionInfo(sourceId, sourceLabel, credential, state, candidatesSoFar), credential);
            }

            if (fallbackCredential is null)
            {
                fallbackSourceId = sourceId;
                fallbackSourceLabel = sourceLabel;
                fallbackCredential = credential;
            }

            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var manualPath = savedServiceAccountPath?.Trim().Trim('"');
        var manual = await GeminiServiceAccountCredentialReader.ReadAsync(manualPath, cancellationToken).ConfigureAwait(false);
        if (Consider(ManualSourceId, ManualSourceLabel, manual) is { } manualResult)
        {
            return manualResult;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var envPath = environmentVariableReader(GoogleApplicationCredentialsVariable);
        var env = await GeminiServiceAccountCredentialReader.ReadAsync(envPath, cancellationToken).ConfigureAwait(false);
        if (Consider(EnvSourceId, EnvSourceLabel, env) is { } envResult)
        {
            return envResult;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var cli = await GeminiCliCredentialReader.ReadAsync(userProfileDirectory, cancellationToken).ConfigureAwait(false);
        if (Consider(GeminiCliSourceId, GeminiCliSourceLabel, cli) is { } cliResult)
        {
            return cliResult;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var gcloud = await GeminiGcloudAdcCredentialReader.ReadAsync(userProfileDirectory, cancellationToken).ConfigureAwait(false);
        if (Consider(GcloudAdcSourceId, GcloudAdcSourceLabel, gcloud) is { } gcloudResult)
        {
            return gcloudResult;
        }

        if (fallbackCredential is not null)
        {
            var state = StateOf(fallbackCredential, clock);
            return new GeminiResolution(
                BuildDetectionInfo(fallbackSourceId!, fallbackSourceLabel!, fallbackCredential, state, candidatesSoFar),
                fallbackCredential);
        }

        return new GeminiResolution(new DetectionInfo(ProviderId, DetectionState.NotConnected, candidates: candidatesSoFar), null);
    }

    private static DetectionState StateOf(GeminiCredential credential, IClock clock) =>
        credential.ExpiresAtUtc is DateTimeOffset expiry && expiry <= clock.UtcNow
            ? DetectionState.Expired
            : credential.UsageCapable
                ? DetectionState.Connected
                : DetectionState.Limited;

    private static DetectionInfo BuildDetectionInfo(
        string sourceId,
        string sourceLabel,
        GeminiCredential credential,
        DetectionState state,
        IReadOnlyList<DetectionCandidate> candidates)
    {
        // Only a Gemini CLI (OAuth) credential ever carries an expiry - every service-account
        // credential does not - so the refresh hint for Expired is always the Gemini CLI one.
        var hint = state switch
        {
            DetectionState.Expired => GeminiCliRefreshHint,
            DetectionState.Limited => credential.LimitedReason,
            _ => string.Empty,
        };

        return new DetectionInfo(
            ProviderId, state, sourceId, sourceLabel, account: credential.Account, hint: hint, candidates: candidates);
    }
}
