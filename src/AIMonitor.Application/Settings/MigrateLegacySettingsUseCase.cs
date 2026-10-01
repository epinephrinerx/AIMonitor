using System.Globalization;

namespace AIMonitor.Application.Settings;

public sealed class MigrateLegacySettingsUseCase(
    ISettingsStore settingsStore,
    ISecretStore secretStore,
    ILegacySettingsReader legacyReader,
    ILegacySecretUnsealer legacySecretUnsealer)
{
    public async Task<LegacyMigrationResult> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        if (settingsStore.Exists)
        {
            return new(false, 0, 0, []);
        }

        IReadOnlyDictionary<string, object?> values;
        try
        {
            values = legacyReader.ReadAll();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await settingsStore.SaveAsync(new AppSettings(), cancellationToken).ConfigureAwait(false);
            return new(true, 0, 0, ["Legacy settings could not be read; defaults were used."]);
        }

        var warnings = new List<string>();
        var importedSettings = 0;
        var settings = ImportSettings(values, ref importedSettings, warnings);
        var importedSecrets = 0;

        // Persist secrets first. settings.json is the one-time completion marker.
        foreach (var pair in values.Where(static pair =>
                     pair.Key.StartsWith("providers/", StringComparison.OrdinalIgnoreCase) &&
                     pair.Key.EndsWith("/key", StringComparison.OrdinalIgnoreCase)))
        {
            if (pair.Value is not string protectedValue || string.IsNullOrWhiteSpace(protectedValue))
            {
                continue;
            }

            if (!legacySecretUnsealer.TryUnseal(protectedValue, out var plaintext) ||
                string.IsNullOrEmpty(plaintext))
            {
                warnings.Add($"Legacy secret '{pair.Key}' could not be migrated.");
                continue;
            }

            try
            {
                await secretStore.SetAsync(pair.Key, plaintext, cancellationToken).ConfigureAwait(false);
                importedSecrets++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                warnings.Add($"Legacy secret '{pair.Key}' could not be stored securely.");
            }
        }

        await settingsStore.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        return new(true, importedSettings, importedSecrets, warnings);
    }

    private static AppSettings ImportSettings(
        IReadOnlyDictionary<string, object?> values,
        ref int imported,
        ICollection<string> warnings)
    {
        var result = new AppSettings();
        result = result with { StartWithWindows = ReadBool(values, "startWithWindows", result.StartWithWindows, ref imported, warnings) };
        result = result with { MinimizeToTray = ReadBool(values, "minimizeToTray", result.MinimizeToTray, ref imported, warnings) };
        result = result with { WidgetAlwaysOnTop = ReadBool(values, "widget/alwaysOnTop", result.WidgetAlwaysOnTop, ref imported, warnings) };
        result = result with { WidgetRotationEnabled = ReadBool(values, "widget/rotate", result.WidgetRotationEnabled, ref imported, warnings) };
        result = result with { ShowConnectionsAtStartup = ReadBool(values, "showConnectionsAtStartup", result.ShowConnectionsAtStartup, ref imported, warnings) };
        result = result with { Theme = ReadString(values, "theme", result.Theme, ref imported) };
        result = result with { DashboardWidth = ReadInt(values, "window/width", result.DashboardWidth, ref imported, warnings) };
        result = result with { DashboardHeight = ReadInt(values, "window/height", result.DashboardHeight, ref imported, warnings) };
        result = result with { RefreshIntervalSeconds = ReadInt(values, "interval", result.RefreshIntervalSeconds, ref imported, warnings) };
        result = result with { WidgetOpacity = ReadDouble(values, "widget/opacity", result.WidgetOpacity, ref imported, warnings) };
        result = result with { ChartRangeDays = ReadInt(values, "range_days", result.ChartRangeDays, ref imported, warnings) };
        result = result with { ChartMetric = ReadString(values, "metric", result.ChartMetric, ref imported) };
        result = result with { ActiveProvider = ReadString(values, "activeProvider", result.ActiveProvider, ref imported) };
        result = result with { Providers = ImportProviders(values, ref imported, warnings) };
        result = result with { LegacyGeometry = ImportGeometry(values, ref imported) };
        return result.Normalize();
    }

    private static IReadOnlyDictionary<string, ProviderPreference> ImportProviders(
        IReadOnlyDictionary<string, object?> values,
        ref int imported,
        ICollection<string> warnings)
    {
        var result = new Dictionary<string, ProviderPreference>(StringComparer.OrdinalIgnoreCase);
        foreach (var providerId in new[] { "claude", "openai", "gemini" })
        {
            var enabledKey = $"providers/{providerId}/enabled";
            var extraKey = $"providers/{providerId}/extra";
            var hasEnabled = values.ContainsKey(enabledKey);
            var hasExtra = values.ContainsKey(extraKey);
            if (!hasEnabled && !hasExtra) continue;
            var enabled = ReadBool(values, enabledKey, true, ref imported, warnings);
            var extra = ReadString(values, extraKey, string.Empty, ref imported);
            result[providerId] = new(enabled, extra);
        }

        return result;
    }

    private static IReadOnlyDictionary<string, string> ImportGeometry(
        IReadOnlyDictionary<string, object?> values,
        ref int imported)
    {
        var candidates = values
            .Where(static pair => pair.Key.StartsWith("geometry/", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var retainedLayouts = candidates
            .Select(static pair => pair.Key.Split('/'))
            .Where(static parts => parts.Length >= 3 && IsLayoutId(parts[1]))
            .Select(static parts => parts[1])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(layout => Convert.ToString(
                values.GetValueOrDefault($"geometry/{layout}/usedAt"), CultureInfo.InvariantCulture),
                StringComparer.Ordinal)
            .Take(8)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in candidates)
        {
            var parts = pair.Key.Split('/');
            if (parts.Length >= 3 && IsLayoutId(parts[1]) && !retainedLayouts.Contains(parts[1])) continue;
            var encoded = EncodeLegacyValue(pair.Value);
            if (encoded is null) continue;
            result[pair.Key["geometry/".Length..]] = encoded;
            imported++;
        }

        return result;
    }

    private static bool IsLayoutId(string value) =>
        value.Length == 11 && value[0] == 'd' && value[1..].All(Uri.IsHexDigit);

    private static string? EncodeLegacyValue(object? value) => value switch
    {
        null => null,
        byte[] bytes => $"base64:{Convert.ToBase64String(bytes)}",
        _ => $"text:{Convert.ToString(value, CultureInfo.InvariantCulture)}",
    };

    private static bool ReadBool(IReadOnlyDictionary<string, object?> values, string key, bool fallback, ref int count, ICollection<string> warnings)
    {
        if (!values.TryGetValue(key, out var raw)) return fallback;
        if (raw is bool value) { count++; return value; }
        if (raw is string text && bool.TryParse(text, out value)) { count++; return value; }
        if (raw is string number && (number == "1" || number == "0")) { count++; return number == "1"; }
        warnings.Add($"Legacy setting '{key}' was invalid; its default was used.");
        return fallback;
    }

    private static int ReadInt(IReadOnlyDictionary<string, object?> values, string key, int fallback, ref int count, ICollection<string> warnings)
    {
        if (!values.TryGetValue(key, out var raw)) return fallback;
        if (int.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        { count++; return value; }
        warnings.Add($"Legacy setting '{key}' was invalid; its default was used.");
        return fallback;
    }

    private static double ReadDouble(IReadOnlyDictionary<string, object?> values, string key, double fallback, ref int count, ICollection<string> warnings)
    {
        if (!values.TryGetValue(key, out var raw)) return fallback;
        if (double.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value))
        { count++; return value; }
        warnings.Add($"Legacy setting '{key}' was invalid; its default was used.");
        return fallback;
    }

    private static string ReadString(IReadOnlyDictionary<string, object?> values, string key, string fallback, ref int count)
    {
        if (!values.TryGetValue(key, out var raw) || raw is null) return fallback;
        count++;
        return Convert.ToString(raw, CultureInfo.InvariantCulture) ?? fallback;
    }
}
