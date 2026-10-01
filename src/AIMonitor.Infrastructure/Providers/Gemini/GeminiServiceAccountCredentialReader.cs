using System.Text.Json;

namespace AIMonitor.Infrastructure.Providers.Gemini;

/// <summary>
/// Reads a service-account JSON file pointed at directly by a caller - either the path saved in this
/// app's settings ("manual"), or the path named by the <c>GOOGLE_APPLICATION_CREDENTIALS</c>
/// environment variable ("env"). Both candidates are the same question asked of two different paths -
/// the Python baseline answered it with two separately written functions that quietly disagreed on
/// how much to explain when the file could not even be parsed as JSON, so this port uses one method
/// for both, distinguishing "could not be read as JSON at all" from "valid JSON but not a usable key"
/// for whichever of the two candidates hits it. Read-only: never creates, updates, or deletes the file.
/// </summary>
internal static class GeminiServiceAccountCredentialReader
{
    internal const string CouldNotReadAsJsonReason =
        "That file could not be read as JSON. Choose the service-account key downloaded from the " +
        "Google Cloud console.";

    internal const string IncompleteKeyReason =
        "This service account file is missing the fields needed to sign a token. Export the key " +
        "again from the Google Cloud console, or point this at a complete service-account JSON with " +
        "the Monitoring Viewer role.";

    /// <summary>
    /// Returns <see langword="null"/> when <paramref name="path"/> is blank or names a file that does
    /// not exist - "nothing configured here", not an error. Any file that does exist is reported as a
    /// <see cref="GeminiCredential"/> even when unusable, so the caller sees why rather than nothing.
    /// </summary>
    internal static async Task<GeminiCredential?> ReadAsync(string? path, CancellationToken cancellationToken)
    {
        var trimmedPath = path?.Trim();
        if (string.IsNullOrEmpty(trimmedPath))
        {
            return null;
        }

        var fileResult = await GeminiCredentialFile.ReadTextAsync(trimmedPath, cancellationToken).ConfigureAwait(false);
        switch (fileResult.Status)
        {
            case GeminiCredentialFile.ReadStatus.Missing:
                return null;
            case GeminiCredentialFile.ReadStatus.Unreadable:
                return Limited(account: string.Empty, CouldNotReadAsJsonReason);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(fileResult.Text!);
        }
        catch (JsonException)
        {
            return Limited(account: string.Empty, CouldNotReadAsJsonReason);
        }

        using (document)
        {
            // Rechecked after parsing (a pure, synchronous, non-trivial-cost step over content up to
            // the file-size cap) so a caller cancellation that arrives during parsing is still
            // observed before this method returns.
            cancellationToken.ThrowIfCancellationRequested();

            var key = GeminiServiceAccountKey.FromJsonObject(document.RootElement);
            if (!key.IsUsableServiceAccount)
            {
                return Limited(key.ClientEmail, IncompleteKeyReason);
            }

            return new GeminiCredential(
                GeminiCredentialKind.ServiceAccount,
                trimmedPath,
                account: key.ClientEmail,
                project: key.ProjectId,
                usageCapable: true);
        }
    }

    /// <summary>An unusable candidate's path is always withheld (<see cref="GeminiCredential.Value"/>
    /// empty) so nothing downstream can try to sign with it, matching the baseline.</summary>
    private static GeminiCredential Limited(string account, string reason) =>
        new(GeminiCredentialKind.ServiceAccount, value: string.Empty, account: account, usageCapable: false, limitedReason: reason);
}
