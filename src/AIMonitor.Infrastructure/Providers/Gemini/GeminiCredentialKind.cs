namespace AIMonitor.Infrastructure.Providers.Gemini;

/// <summary>The kind of login a Gemini credential source yielded, mirroring the Python baseline's
/// SERVICE_ACCOUNT/OAUTH vocabulary (Gemini never yields an API-key-kind credential).</summary>
internal enum GeminiCredentialKind
{
    ServiceAccount,
    OAuth,
}
