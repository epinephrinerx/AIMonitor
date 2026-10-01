using System.Text;
using System.Text.Json;
using AIMonitor.Application.Time;
using AIMonitor.Domain;

namespace AIMonitor.Infrastructure.Providers.OpenAi;

/// <summary>Result of resolving OpenAI's credential sources: the provider-neutral detection outcome
/// plus (when found) the credential needed to actually make a request. <see cref="Credential"/> is
/// Infrastructure-internal and never leaves this boundary - <see cref="Detection"/> is what travels
/// into a <see cref="ProviderSnapshot"/>.</summary>
internal sealed record OpenAiResolution(DetectionInfo Detection, OpenAiCredential? Credential);

/// <summary>
/// Resolves OpenAI's credential in the priority order PAR-008 requires: a Codex ChatGPT OAuth login
/// always wins over a saved Admin key, an environment variable, or a key stored in Codex CLI's own
/// login file - and an <em>expired</em> Codex OAuth login is reported as such rather than silently
/// falling back to one of the others. Every probe is read-only; nothing here ever writes to, refreshes,
/// or renews a credential.
/// </summary>
internal static class OpenAiCredentialResolver
{
    public const string ProviderId = "openai";

    public const string ManualSourceId = "manual";
    private const string ManualSourceLabel = "Admin key saved in this app";
    public const string EnvSourceId = "env";
    private const string EnvSourceLabel = "Environment variable";
    public const string CodexCliSourceId = "codex_cli";
    private const string CodexCliSourceLabel = "Codex CLI login (ChatGPT)";

    private const string CodexRefreshHint = "Run `codex` and sign in again.";

    private const string AdminKeyPrefix = "sk-admin-";

    private const string NotAdminKeyReason =
        "API spend requires an organization Admin key (sk-admin-…). Sign in to Codex with " +
        "ChatGPT to monitor Codex quota instead.";

    private const string NoAccountIdReason = "Codex login has no account ID. Sign in to Codex again.";

    private const int MaxAuthFileBytes = 1024 * 1024;

    private static readonly string[] EnvVarNamesInPriorityOrder = ["OPENAI_ADMIN_KEY", "OPENAI_API_KEY"];

    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// The codex-first carve-out (PAR-008): resolves only the Codex CLI source first, and returns it
    /// immediately - connected or expired - whenever it is an OAuth-kind credential, without probing
    /// any other source. Falls through to <see cref="ResolveAsync"/> only when Codex CLI yields
    /// nothing at all, or yields an API-key-kind login (from <c>codex login --api-key</c>, which then
    /// competes on equal footing with the other key sources).
    /// </summary>
    public static async Task<OpenAiResolution> DetectAsync(
        string? savedAdminKey,
        Func<string, string?> environmentVariableReader,
        string codexAuthPath,
        IClock clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clock);
        cancellationToken.ThrowIfCancellationRequested();

        var codexOnly = await ProbeCodexCliAsync(codexAuthPath, cancellationToken).ConfigureAwait(false);
        if (codexOnly is not null && codexOnly.Kind == OpenAiCredentialKind.OAuth)
        {
            var candidates = new List<DetectionCandidate> { new(CodexCliSourceId, CodexCliSourceLabel) };
            var state = StateOf(codexOnly, clock);
            return new OpenAiResolution(
                BuildDetectionInfo(CodexCliSourceId, CodexCliSourceLabel, codexOnly, state, candidates),
                codexOnly);
        }

        return await ResolveAsync(savedAdminKey, environmentVariableReader, codexAuthPath, clock, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Walks manual (saved Admin key) -&gt; environment variable -&gt; Codex CLI login, in that order,
    /// probing lazily and returning as soon as a source proves both connected and usage-capable. A
    /// source that yields a credential which cannot actually read usage (an ordinary project key, or a
    /// login missing what it needs) is remembered as a Limited/Expired fallback rather than skipped
    /// outright - but probing continues past it, so "nothing at all is connected" is never confused
    /// with "something is connected but cannot be used yet", matching the Python baseline's <c>resolve()</c>.
    /// </summary>
    public static async Task<OpenAiResolution> ResolveAsync(
        string? savedAdminKey,
        Func<string, string?> environmentVariableReader,
        string codexAuthPath,
        IClock clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clock);

        var candidatesSoFar = new List<DetectionCandidate>();
        string? fallbackSourceId = null;
        string? fallbackSourceLabel = null;
        OpenAiCredential? fallbackCredential = null;

        OpenAiResolution? Consider(string sourceId, string sourceLabel, OpenAiCredential? credential)
        {
            if (credential is null)
            {
                return null;
            }

            candidatesSoFar.Add(new DetectionCandidate(sourceId, sourceLabel));
            var state = StateOf(credential, clock);
            if (state == DetectionState.Connected)
            {
                return new OpenAiResolution(
                    BuildDetectionInfo(sourceId, sourceLabel, credential, state, candidatesSoFar),
                    credential);
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
        var manualResult = Consider(ManualSourceId, ManualSourceLabel, ProbeManual(savedAdminKey));
        if (manualResult is not null)
        {
            return manualResult;
        }

        var envResult = Consider(EnvSourceId, EnvSourceLabel, ProbeEnvironment(environmentVariableReader));
        if (envResult is not null)
        {
            return envResult;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var codexCredential = await ProbeCodexCliAsync(codexAuthPath, cancellationToken).ConfigureAwait(false);
        var codexResult = Consider(CodexCliSourceId, CodexCliSourceLabel, codexCredential);
        if (codexResult is not null)
        {
            return codexResult;
        }

        if (fallbackCredential is not null)
        {
            var state = StateOf(fallbackCredential, clock);
            return new OpenAiResolution(
                BuildDetectionInfo(fallbackSourceId!, fallbackSourceLabel!, fallbackCredential, state, candidatesSoFar),
                fallbackCredential);
        }

        return new OpenAiResolution(
            new DetectionInfo(ProviderId, DetectionState.NotConnected, candidates: candidatesSoFar), null);
    }

    private static DetectionState StateOf(OpenAiCredential credential, IClock clock) =>
        credential.ExpiresAtUtc is DateTimeOffset expiry && expiry <= clock.UtcNow
            ? DetectionState.Expired
            : credential.UsageCapable
                ? DetectionState.Connected
                : DetectionState.Limited;

    private static DetectionInfo BuildDetectionInfo(
        string sourceId,
        string sourceLabel,
        OpenAiCredential credential,
        DetectionState state,
        IReadOnlyList<DetectionCandidate> candidates)
    {
        // Only a Codex CLI credential in this provider ever carries an expiry (manual/environment
        // keys have none), so the refresh hint for Expired is always the Codex one.
        var hint = state switch
        {
            DetectionState.Expired => CodexRefreshHint,
            DetectionState.Limited => credential.LimitedReason,
            _ => string.Empty,
        };

        return new DetectionInfo(
            ProviderId, state, sourceId, sourceLabel, account: credential.Account, hint: hint, candidates: candidates);
    }

    private static OpenAiCredential? ProbeManual(string? savedAdminKey)
    {
        var trimmed = savedAdminKey?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : MakeApiKeyCredential(trimmed, "saved in this app");
    }

    private static OpenAiCredential? ProbeEnvironment(Func<string, string?> environmentVariableReader)
    {
        ArgumentNullException.ThrowIfNull(environmentVariableReader);

        foreach (var name in EnvVarNamesInPriorityOrder)
        {
            var value = environmentVariableReader(name)?.Trim();
            if (!string.IsNullOrEmpty(value))
            {
                return MakeApiKeyCredential(value, $"from ${name}");
            }
        }

        return null;
    }

    private static OpenAiCredential MakeApiKeyCredential(string value, string account) =>
        new(
            OpenAiCredentialKind.ApiKey,
            value,
            account: account,
            usageCapable: value.StartsWith(AdminKeyPrefix, StringComparison.Ordinal),
            limitedReason: NotAdminKeyReason);

    /// <summary>
    /// Reads Codex CLI's <c>auth.json</c>, read-only. Returns either an OAuth-kind credential (a
    /// ChatGPT login, decoded from <c>tokens.access_token</c>/<c>id_token</c>) or an API-key-kind
    /// credential (from <c>codex login --api-key</c>, which writes a top-level <c>OPENAI_API_KEY</c>
    /// field), or <see langword="null"/> when the file is missing, unreadable, or has neither.
    /// </summary>
    private static async Task<OpenAiCredential?> ProbeCodexCliAsync(string authPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var document = await ReadJsonObjectAsync(authPath, cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return null;
        }

        var root = document.RootElement;

        JsonElement tokens = default;
        var hasTokens = false;
        if (root.TryGetProperty("tokens", out var tokensElement))
        {
            if (tokensElement.ValueKind != JsonValueKind.Object)
            {
                // A malformed `tokens` field invalidates the whole login, matching the Python
                // baseline's `if not isinstance(tokens, dict): return None` - checked before the
                // OPENAI_API_KEY field below, so a corrupt `tokens` shape is never masked by an
                // otherwise-valid top-level key.
                return null;
            }

            tokens = tokensElement;
            hasTokens = true;
        }

        // A usable ChatGPT OAuth login always outranks a coexisting top-level OPENAI_API_KEY
        // (PAR-008): Codex CLI normally writes only one or the other, but when both are present in
        // the same file - e.g. a stale key left over from an earlier `codex login --api-key` - the
        // OAuth login must still win, connected or expired, never the reverse. Only when there is no
        // usable OAuth login here at all does the API key get a chance.
        if (hasTokens && TryBuildOAuthCredential(tokens) is { } oauthCredential)
        {
            return oauthCredential;
        }

        if (root.TryGetProperty("OPENAI_API_KEY", out var apiKeyElement) && apiKeyElement.ValueKind == JsonValueKind.String)
        {
            var apiKey = apiKeyElement.GetString();
            if (!string.IsNullOrEmpty(apiKey) && apiKey.StartsWith("sk-", StringComparison.Ordinal))
            {
                var account = hasTokens ? AccountFromTokens(tokens) : string.Empty;
                return MakeApiKeyCredential(apiKey, account);
            }
        }

        return null;
    }

    /// <summary>Builds an OAuth-kind credential from an already-validated <c>tokens</c> object, or
    /// <see langword="null"/> when it has no usable (present, string, non-blank,
    /// control-character-free) access token - the caller falls back to a top-level API key in that
    /// case.</summary>
    private static OpenAiCredential? TryBuildOAuthCredential(JsonElement tokens)
    {
        if (!tokens.TryGetProperty("access_token", out var accessTokenElement)
            || accessTokenElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var accessToken = accessTokenElement.GetString();
        if (string.IsNullOrEmpty(accessToken) || ContainsControlCharacter(accessToken))
        {
            return null;
        }

        var account = AccountFromTokens(tokens);

        // Priority matches the Python baseline: the login file's own `tokens.account_id` first, the
        // access token's decoded `chatgpt_account_id` claim only as a fallback.
        var accountId = string.Empty;
        if (tokens.TryGetProperty("account_id", out var tokenAccountIdElement)
            && tokenAccountIdElement.ValueKind == JsonValueKind.String)
        {
            accountId = tokenAccountIdElement.GetString() ?? string.Empty;
        }

        DateTimeOffset? expiresAtUtc = null;
        using (var accessClaims = OpenAiJwt.DecodeClaims(accessToken))
        {
            if (accessClaims is not null && accessClaims.RootElement.ValueKind == JsonValueKind.Object)
            {
                var claimsRoot = accessClaims.RootElement;
                if (string.IsNullOrEmpty(accountId)
                    && claimsRoot.TryGetProperty("https://api.openai.com/auth", out var auth)
                    && auth.ValueKind == JsonValueKind.Object
                    && auth.TryGetProperty("chatgpt_account_id", out var claimAccountIdElement)
                    && claimAccountIdElement.ValueKind == JsonValueKind.String)
                {
                    accountId = claimAccountIdElement.GetString() ?? string.Empty;
                }

                if (claimsRoot.TryGetProperty("exp", out var expElement))
                {
                    expiresAtUtc = OpenAiEpoch.Parse(expElement);
                }
            }
        }

        return new OpenAiCredential(
            OpenAiCredentialKind.OAuth,
            accessToken,
            account: account,
            accountId: accountId,
            expiresAtUtc: expiresAtUtc,
            usageCapable: !string.IsNullOrEmpty(accountId),
            limitedReason: NoAccountIdReason);
    }

    private static string AccountFromTokens(JsonElement tokens)
    {
        if (tokens.TryGetProperty("id_token", out var idTokenElement) && idTokenElement.ValueKind == JsonValueKind.String)
        {
            using var idClaims = OpenAiJwt.DecodeClaims(idTokenElement.GetString());
            if (idClaims is not null)
            {
                var account = OpenAiJwt.AccountFromClaims(idClaims.RootElement);
                if (!string.IsNullOrEmpty(account))
                {
                    return account;
                }
            }
        }

        if (tokens.TryGetProperty("account_id", out var accountIdElement) && accountIdElement.ValueKind == JsonValueKind.String)
        {
            return accountIdElement.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    /// <summary>An access token containing a control character (e.g. a stray newline or NUL) is never
    /// a legitimate bearer token and is rejected rather than forwarded into an Authorization header or
    /// the App Server login call.</summary>
    private static bool ContainsControlCharacter(string value)
    {
        foreach (var ch in value)
        {
            if (char.IsControl(ch))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads and parses a JSON object file, bounded independently of its declared size, decoded with a
    /// strict UTF-8 decoder. Returns <see langword="null"/> for a missing file, an I/O/access failure,
    /// invalid JSON, or a root that is not an object - mirroring the Python baseline's <c>read_json()</c>,
    /// which folds every one of those into "nothing found" rather than distinguishing them, since a
    /// corrupt Codex login must not stop detection any differently than a missing one would.
    /// </summary>
    private static async Task<JsonDocument?> ReadJsonObjectAsync(string path, CancellationToken cancellationToken)
    {
        byte[] bytes;
        try
        {
            var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096, useAsync: true);
            await using (stream.ConfigureAwait(false))
            {
                using var buffer = new MemoryStream();
                var chunk = new byte[8192];
                int bytesRead;
                while ((bytesRead = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (buffer.Length + bytesRead > MaxAuthFileBytes)
                    {
                        return null;
                    }

                    buffer.Write(chunk, 0, bytesRead);
                }

                bytes = buffer.ToArray();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // IOException already covers FileNotFoundException/DirectoryNotFoundException (a missing
            // auth.json is the common case, not an error).
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        string json;
        try
        {
            json = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        try
        {
            var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                return null;
            }

            return document;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
