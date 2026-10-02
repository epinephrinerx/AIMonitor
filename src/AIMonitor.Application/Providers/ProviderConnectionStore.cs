namespace AIMonitor.Application.Providers;

using AIMonitor.Application.Settings;

/// <summary>
/// Manages provider credentials in <see cref="ISecretStore"/> and provider preferences in <see cref="SettingsSession"/>.
/// Coordinates secret read/write and settings update with three-way key semantics: replace, remove, or untouched.
/// </summary>
public sealed class ProviderConnectionStore
{
    private readonly ISecretStore _secrets;
    private readonly SettingsSession _session;

    /// <summary>
    /// Raised when a provider's connection settings or secrets are successfully changed.
    /// </summary>
    public event Action<string>? Changed;

    /// <summary>
    /// Initializes a new instance of <see cref="ProviderConnectionStore"/>.
    /// </summary>
    public ProviderConnectionStore(ISecretStore secrets, SettingsSession session)
    {
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    /// <summary>
    /// Retrieves the current connection details for <paramref name="providerId"/>.
    /// </summary>
    public async Task<ProviderConnection> GetAsync(string providerId, CancellationToken cancellationToken = default)
    {
        var id = NormalizeProviderId(providerId);

        string? key = null;
        if (id is not "claude")
        {
            key = await _secrets.GetAsync($"providers/{id}/key", cancellationToken).ConfigureAwait(false);
        }

        var extra = "";
        if (_session.Current.Providers.TryGetValue(id, out var pref) && pref.Extra is not null)
        {
            extra = pref.Extra;
        }

        return new ProviderConnection(key, extra);
    }

    /// <summary>
    /// Saves connection details for <paramref name="providerId"/> with 3-way key semantics:
    /// <list type="bullet">
    ///   <item><description>If <paramref name="typedKey"/> is non-empty after trimming and stripping quotes: replaces key.</description></item>
    ///   <item><description>If <paramref name="typedKey"/> is empty and <paramref name="clearRequested"/> is true: removes key.</description></item>
    ///   <item><description>If <paramref name="typedKey"/> is empty and <paramref name="clearRequested"/> is false: leaves key untouched.</description></item>
    /// </list>
    /// For <paramref name="extra"/>: <see langword="null"/> leaves extra untouched; otherwise trims and saves (empty clears).
    /// Secrets are updated first; if secret storage fails, settings are not touched and an exception is thrown.
    /// On success, raises <see cref="Changed"/>.
    /// </summary>
    public async Task SaveAsync(
        string providerId,
        string? typedKey,
        bool clearRequested,
        string? extra,
        CancellationToken cancellationToken = default)
    {
        var id = NormalizeProviderId(providerId);

        if (id is "claude")
        {
            var sanitizedClaudeKey = SanitizeKey(typedKey);
            if (!string.IsNullOrEmpty(sanitizedClaudeKey))
            {
                throw new ArgumentException("Claude does not use an API key.", nameof(typedKey));
            }

            if (extra is not null && extra.Trim().Length > 0)
            {
                throw new ArgumentException("Claude does not support extra settings.", nameof(extra));
            }
        }
        else
        {
            var sanitizedKey = SanitizeKey(typedKey);
            if (!string.IsNullOrEmpty(sanitizedKey))
            {
                // แทนที่ (clear แล้วพิมพ์ใหม่ = แทนที่)
                await _secrets.SetAsync($"providers/{id}/key", sanitizedKey, cancellationToken).ConfigureAwait(false);
            }
            else if (clearRequested)
            {
                // ลบ
                await _secrets.RemoveAsync($"providers/{id}/key", cancellationToken).ConfigureAwait(false);
            }
            // else: ว่างเฉย ๆ = ไม่แตะ key
        }

        if (extra is not null)
        {
            var trimmedExtra = extra.Trim();
            await _session.UpdateAsync(s =>
            {
                var enabled = true;
                if (s.Providers.TryGetValue(id, out var currentPref))
                {
                    enabled = currentPref.Enabled;
                }

                var updatedProviders = new Dictionary<string, ProviderPreference>(s.Providers, StringComparer.OrdinalIgnoreCase)
                {
                    [id] = new ProviderPreference(enabled, trimmedExtra)
                };

                return s with { Providers = updatedProviders };
            }, cancellationToken).ConfigureAwait(false);
        }

        Changed?.Invoke(id);
    }

    private static string? SanitizeKey(string? rawKey)
    {
        if (rawKey is null) return null;
        var trimmed = rawKey.Trim();
        if (trimmed.Length == 0) return string.Empty;
        return trimmed.Trim('"').Trim();
    }

    private static string NormalizeProviderId(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        var normalized = providerId.Trim().ToLowerInvariant();
        if (normalized is not ("claude" or "openai" or "gemini"))
        {
            throw new ArgumentException($"Unknown provider '{providerId}'. Supported providers are claude, openai, and gemini.", nameof(providerId));
        }

        return normalized;
    }
}
