using System.Diagnostics;
using System.Text.Json;

namespace AIMonitor.Infrastructure.Providers.Gemini;

/// <summary>
/// The fields every Gemini service-account candidate (a caller-selected file, the
/// <c>GOOGLE_APPLICATION_CREDENTIALS</c> file, or either gcloud application-default location) is
/// validated against, in exactly one place - matching the Python baseline's <c>usable_service_account()</c>,
/// which exists specifically so the detector and the (future) token-signing code cannot drift apart
/// about what counts as a usable key. <see cref="PrivateKey"/> is a bearer secret and must never
/// appear in <see cref="ToString"/>, an exception message, or a log line.
/// </summary>
[DebuggerDisplay("GeminiServiceAccountKey (redacted)")]
internal sealed record GeminiServiceAccountKey
{
    /// <summary>Fixed value the <c>type</c> field must equal for this to be a service-account key at
    /// all, as opposed to e.g. a gcloud <c>authorized_user</c> login.</summary>
    private const string ServiceAccountType = "service_account";

    public string Type { get; }

    public string ClientEmail { get; }

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string PrivateKey { get; }

    public string ProjectId { get; }

    public GeminiServiceAccountKey(string type, string clientEmail, string privateKey, string projectId)
    {
        Type = type ?? string.Empty;
        ClientEmail = clientEmail ?? string.Empty;
        PrivateKey = privateKey ?? string.Empty;
        ProjectId = projectId ?? string.Empty;
    }

    /// <summary>Nothing found at all - a non-object JSON root, or a file that could not be read as an
    /// object - is treated as an all-empty key, which is never usable.</summary>
    public static readonly GeminiServiceAccountKey Empty = new(string.Empty, string.Empty, string.Empty, string.Empty);

    /// <summary>
    /// Total, on purpose, matching the Python baseline's <c>usable_service_account()</c>: this is safe
    /// to call on a key parsed from any JSON object, however incomplete or wrong-shaped, and always
    /// has an answer rather than assuming the fields it needs are present. A field present with the
    /// wrong JSON type (e.g. <c>private_key</c> as a number) is treated as absent, not coerced.
    /// </summary>
    public bool IsUsableServiceAccount =>
        Type == ServiceAccountType && ClientEmail.Length > 0 && PrivateKey.Length > 0;

    /// <summary>
    /// Reads whatever of the four fields are present as JSON strings from <paramref name="root"/>,
    /// leaving each as empty when missing, blank, or a different JSON type. A non-object root yields
    /// <see cref="Empty"/>, matching the baseline's predicate handling <c>[]</c>, <c>"text"</c>, a bare
    /// number, and <see langword="null"/> roots without raising.
    /// </summary>
    public static GeminiServiceAccountKey FromJsonObject(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Empty;
        }

        return new GeminiServiceAccountKey(
            ReadString(root, "type"),
            ReadString(root, "client_email"),
            ReadString(root, "private_key"),
            ReadString(root, "project_id"));
    }

    private static string ReadString(JsonElement obj, string propertyName) =>
        obj.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>
    /// Overrides the compiler-generated record <c>ToString</c> (which would otherwise print every
    /// property, including <see cref="PrivateKey"/>) with a fixed string that never depends on any
    /// field value.
    /// </summary>
    public override string ToString() => "GeminiServiceAccountKey { <redacted> }";
}
