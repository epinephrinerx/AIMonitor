using System.Text.Json;

namespace AIMonitor.Infrastructure.Providers.Gemini;

/// <summary>
/// Reads the Gemini CLI's own Google OAuth login (<c>&lt;home&gt;/.gemini/oauth_creds.json</c>),
/// read-only: this app never refreshes or rewrites another tool's login. Usable for Cloud Monitoring
/// only when the token's granted scopes include <c>cloud-platform</c>, which the Gemini CLI does
/// request; without it the token can prove identity but not read metrics. Matching the Python
/// baseline's actual behavior (not its aspirational docstring, which describes reporting this case as
/// Limited but whose code returns nothing), a token missing that scope is treated as though this
/// source found nothing, letting detection continue to gcloud ADC rather than surfacing a Limited
/// candidate for it.
/// </summary>
internal static class GeminiCliCredentialReader
{
    private const string CloudPlatformScope = "https://www.googleapis.com/auth/cloud-platform";

    private static readonly char[] ScopeSeparators = [' ', '\t', '\n', '\r'];

    internal static async Task<GeminiCredential?> ReadAsync(string userProfileDirectory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userProfileDirectory);

        var geminiHome = Path.Combine(userProfileDirectory, ".gemini");
        var oauthResult = await GeminiCredentialFile.ReadTextAsync(Path.Combine(geminiHome, "oauth_creds.json"), cancellationToken)
            .ConfigureAwait(false);
        if (oauthResult.Status != GeminiCredentialFile.ReadStatus.Success)
        {
            return null;
        }

        using var document = GeminiCredentialFile.TryParseObject(oauthResult.Text!);
        if (document is null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var root = document.RootElement;
        if (!TryReadNonEmptyString(root, "access_token", out var accessToken))
        {
            return null;
        }

        string[] scopes = TryReadNonEmptyString(root, "scope", out var scopeText)
            ? scopeText.Split(ScopeSeparators, StringSplitOptions.RemoveEmptyEntries)
            : [];
        if (Array.IndexOf(scopes, CloudPlatformScope) < 0)
        {
            return null;
        }

        var account = await ResolveAccountAsync(geminiHome, root, cancellationToken).ConfigureAwait(false);
        var expiresAtUtc = root.TryGetProperty("expiry_date", out var expiryElement) ? ParseEpoch(expiryElement) : null;

        return new GeminiCredential(GeminiCredentialKind.OAuth, accessToken, account: account, expiresAtUtc: expiresAtUtc, usageCapable: true);
    }

    /// <summary>The active Google account, preferring the CLI's own record of which login is active
    /// over decoding the OAuth token's own (unverified, display-only) claims.</summary>
    private static async Task<string> ResolveAccountAsync(string geminiHome, JsonElement oauthRoot, CancellationToken cancellationToken)
    {
        var accountsResult = await GeminiCredentialFile
            .ReadTextAsync(Path.Combine(geminiHome, "google_accounts.json"), cancellationToken)
            .ConfigureAwait(false);
        if (accountsResult.Status == GeminiCredentialFile.ReadStatus.Success)
        {
            using var accountsDocument = GeminiCredentialFile.TryParseObject(accountsResult.Text!);
            if (accountsDocument is not null
                && TryReadNonEmptyString(accountsDocument.RootElement, "active", out var active))
            {
                return active;
            }
        }

        return TryReadNonEmptyString(oauthRoot, "id_token", out var idToken) ? AccountFromJwt(idToken) : string.Empty;
    }

    /// <summary>Decodes a JWT payload for display only - no signature check, matching the Python
    /// baseline's <c>account_from_claims(jwt_claims(...))</c>. The payload is base64, not encryption,
    /// so this is not a cryptographic operation and the result is never treated as an authenticated
    /// fact.</summary>
    private static string AccountFromJwt(string token)
    {
        using var claims = DecodeJwtClaims(token);
        if (claims is null || claims.RootElement.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        foreach (var claimName in new[] { "email", "preferred_username", "name", "sub" })
        {
            if (TryReadNonEmptyString(claims.RootElement, claimName, out var value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static JsonDocument? DecodeJwtClaims(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            return null;
        }

        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        switch (payload.Length % 4)
        {
            case 2:
                payload += "==";
                break;
            case 3:
                payload += "=";
                break;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(payload);
        }
        catch (FormatException)
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Accepts epoch seconds or milliseconds since the epoch, matching the Python baseline's
    /// shared <c>epoch()</c> helper. Missing, non-numeric, or nonpositive values become
    /// <see langword="null"/> ("unknown") rather than an invented expiry.</summary>
    private static DateTimeOffset? ParseEpoch(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var raw) || raw <= 0)
        {
            return null;
        }

        var milliseconds = raw > 10_000_000_000 ? raw : raw * 1000.0;
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(milliseconds, MidpointRounding.AwayFromZero)).ToUniversalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static bool TryReadNonEmptyString(JsonElement obj, string propertyName, out string value)
    {
        if (obj.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString();
            if (!string.IsNullOrEmpty(text))
            {
                value = text;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }
}
