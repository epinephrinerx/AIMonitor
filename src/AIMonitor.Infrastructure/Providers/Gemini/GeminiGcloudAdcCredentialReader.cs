using System.Text.Json;

namespace AIMonitor.Infrastructure.Providers.Gemini;

/// <summary>
/// Reads a service account parked in gcloud's application-default location. <c>gcloud auth
/// application-default login</c> writes an <c>authorized_user</c> credential, not a service account,
/// and this app cannot use one: reading it would mean refreshing somebody else's OAuth token, which
/// the credential policy forbids outright. That case is reported as Limited with the explanation
/// attached - the same treatment every other present-but-unusable login gets - rather than vanishing
/// as though nothing were found.
///
/// Both the Windows and POSIX application-default locations are checked before anything is returned,
/// because one machine can have both (native tooling writes the first, WSL or a copied dotfile the
/// second): a user login found first must not hide a usable service account sitting in the other
/// location, and a service account missing its private key must not hide a usable one found next.
/// Read-only throughout - never creates, updates, or deletes either file.
/// </summary>
internal static class GeminiGcloudAdcCredentialReader
{
    internal const string GcloudUserLoginReason =
        "This is a gcloud user login (application-default). Reading usage with it would mean " +
        "refreshing another tool's OAuth token, which this app never does. Sign in with `gemini`, " +
        "or point this at a service-account JSON with the Monitoring Viewer role.";

    internal static async Task<GeminiCredential?> ReadAsync(string userProfileDirectory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userProfileDirectory);

        string[] candidatePaths =
        [
            Path.Combine(userProfileDirectory, "AppData", "Roaming", "gcloud", "application_default_credentials.json"),
            Path.Combine(userProfileDirectory, ".config", "gcloud", "application_default_credentials.json"),
        ];

        GeminiCredential? fallback = null;
        foreach (var path in candidatePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A missing or unreadable file at one well-known location is simply not there; scanning
            // moves on to the next location rather than reporting an error for an OS path the user
            // never chose directly, matching the Python baseline's shared `read_json()`.
            var fileResult = await GeminiCredentialFile.ReadTextAsync(path, cancellationToken).ConfigureAwait(false);
            if (fileResult.Status != GeminiCredentialFile.ReadStatus.Success)
            {
                continue;
            }

            using var document = GeminiCredentialFile.TryParseObject(fileResult.Text!);
            if (document is null)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var root = document.RootElement;
            var key = GeminiServiceAccountKey.FromJsonObject(root);
            if (key.IsUsableServiceAccount)
            {
                return new GeminiCredential(
                    GeminiCredentialKind.ServiceAccount, path, account: key.ClientEmail, project: key.ProjectId, usageCapable: true);
            }

            if (fallback is null)
            {
                var account = FirstNonEmpty(key.ClientEmail, ReadOptionalString(root, "account"), ReadOptionalString(root, "client_id"));
                // Matches the Python baseline exactly: type=="service_account" but still incomplete
                // (e.g. no private_key) gets the generic incomplete-key reason; anything else present
                // at this location - including no `type` field at all - is assumed to be a gcloud user
                // login, since that is the only other shape `gcloud auth application-default login`
                // actually writes here.
                var reason = key.Type == "service_account"
                    ? GeminiServiceAccountCredentialReader.IncompleteKeyReason
                    : GcloudUserLoginReason;
                fallback = new GeminiCredential(
                    GeminiCredentialKind.ServiceAccount, value: string.Empty, account: account, usageCapable: false, limitedReason: reason);
            }
        }

        return fallback;
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static string ReadOptionalString(JsonElement obj, string propertyName) =>
        obj.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
