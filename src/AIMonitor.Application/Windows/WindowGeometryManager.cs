using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AIMonitor.Application.Settings;

namespace AIMonitor.Application.Windows;

/// <summary>
/// Manages window geometry retention and visible screen fitting per display topology and DPI.
/// Satisfies PAR-021: remembers window placement per display topology/DPI, retains the 8 most recent
/// layouts, and fits restored windows safely into visible screen area.
/// </summary>
public static class WindowGeometryManager
{
    public const int MaxRetainedLayouts = 8;
    public const string DashboardMode = "dashboard";
    public const string WidgetMode = "widget";

    public static string GenerateTopologyId(IEnumerable<DisplayArea> displays)
    {
        ArgumentNullException.ThrowIfNull(displays);

        var ordered = displays
            .OrderBy(d => d.Left)
            .ThenBy(d => d.Top)
            .ToArray();

        if (ordered.Length == 0)
        {
            return "d0000000000";
        }

        var sb = new StringBuilder();
        foreach (var d in ordered)
        {
            sb.Append(CultureInfo.InvariantCulture, $"{d.Left:F0},{d.Top:F0},{d.Width:F0},{d.Height:F0},{d.DpiScale:F2};");
        }

        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
        var hex = Convert.ToHexString(hash)[..10].ToLowerInvariant();
        return $"d{hex}";
    }

    public static WindowPlacement FitIntoVisibleArea(
        WindowPlacement requested,
        IEnumerable<DisplayArea> displays,
        WindowPlacement fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);

        var displayList = displays?.ToArray() ?? [];
        if (!requested.IsValid || displayList.Length == 0)
        {
            return fallback;
        }

        var targetDisplay = displayList.MaxBy(d => d.OverlapArea(requested)) ?? displayList[0];

        // Ensure window dimensions fit within target display
        var width = Math.Min(requested.Width, targetDisplay.Width);
        var height = Math.Min(requested.Height, targetDisplay.Height);

        // If the window was completely off-screen (e.g. disconnected monitor),
        // bring it fully inside the visible work area of the target display.
        if (!displayList.Any(d => d.Intersects(requested)))
        {
            var centerLeft = Math.Clamp(targetDisplay.Left + (targetDisplay.Width - width) / 2.0, targetDisplay.Left, targetDisplay.Right - width);
            var centerTop = Math.Clamp(targetDisplay.Top + (targetDisplay.Height - height) / 2.0, targetDisplay.Top, targetDisplay.Bottom - height);
            return new WindowPlacement(centerLeft, centerTop, width, height, requested.IsMaximized);
        }

        // Window intersects a display: ensure at least 50px of edge and 30px of titlebar remain on screen
        const double minVisibleWidth = 50.0;
        const double minVisibleHeight = 30.0;

        var minLeft = targetDisplay.Left - width + minVisibleWidth;
        var maxLeft = targetDisplay.Right - minVisibleWidth;
        var minTop = targetDisplay.Top;
        var maxTop = targetDisplay.Bottom - minVisibleHeight;

        var left = Math.Clamp(requested.Left, minLeft, maxLeft);
        var top = Math.Clamp(requested.Top, minTop, maxTop);

        return new WindowPlacement(left, top, width, height, requested.IsMaximized);
    }

    public static AppSettings SavePlacement(
        AppSettings currentSettings,
        string topologyId,
        string windowMode,
        WindowPlacement placement,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(currentSettings);
        ArgumentException.ThrowIfNullOrWhiteSpace(topologyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(windowMode);
        ArgumentNullException.ThrowIfNull(placement);

        var dict = new Dictionary<string, string>(currentSettings.LegacyGeometry, StringComparer.OrdinalIgnoreCase);

        // Save current placement fields
        dict[$"{topologyId}/usedAt"] = $"text:{now.UtcDateTime:O}";
        dict[$"{topologyId}/{windowMode}/left"] = $"text:{placement.Left.ToString(CultureInfo.InvariantCulture)}";
        dict[$"{topologyId}/{windowMode}/top"] = $"text:{placement.Top.ToString(CultureInfo.InvariantCulture)}";
        dict[$"{topologyId}/{windowMode}/width"] = $"text:{placement.Width.ToString(CultureInfo.InvariantCulture)}";
        dict[$"{topologyId}/{windowMode}/height"] = $"text:{placement.Height.ToString(CultureInfo.InvariantCulture)}";
        dict[$"{topologyId}/{windowMode}/maximized"] = $"text:{(placement.IsMaximized ? "1" : "0")}";

        // Enforce 8-layout limit (PAR-021)
        var layoutIds = dict.Keys
            .Select(k => k.Split('/'))
            .Where(parts => parts.Length >= 2 && IsLayoutId(parts[0]))
            .Select(parts => parts[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(id => dict.TryGetValue($"{id}/usedAt", out var raw) ? raw : string.Empty, StringComparer.Ordinal)
            .ToArray();

        if (layoutIds.Length > MaxRetainedLayouts)
        {
            var evictedLayouts = layoutIds.Skip(MaxRetainedLayouts).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var keysToRemove = dict.Keys
                .Where(k =>
                {
                    var parts = k.Split('/');
                    return parts.Length >= 2 && evictedLayouts.Contains(parts[0]);
                })
                .ToArray();

            foreach (var key in keysToRemove)
            {
                dict.Remove(key);
            }
        }

        return currentSettings with { LegacyGeometry = dict };
    }

    public static bool TryRestorePlacement(
        AppSettings settings,
        string topologyId,
        string windowMode,
        IEnumerable<DisplayArea> displays,
        out WindowPlacement placement)
    {
        placement = null!;
        if (settings?.LegacyGeometry is null || string.IsNullOrWhiteSpace(topologyId))
        {
            return false;
        }

        var dict = settings.LegacyGeometry;
        if (!TryReadDouble(dict, $"{topologyId}/{windowMode}/left", out var left) ||
            !TryReadDouble(dict, $"{topologyId}/{windowMode}/top", out var top) ||
            !TryReadDouble(dict, $"{topologyId}/{windowMode}/width", out var width) ||
            !TryReadDouble(dict, $"{topologyId}/{windowMode}/height", out var height))
        {
            return false;
        }

        var maximized = TryReadBool(dict, $"{topologyId}/{windowMode}/maximized", false);
        var rawPlacement = new WindowPlacement(left, top, width, height, maximized);

        if (!rawPlacement.IsValid)
        {
            return false;
        }

        placement = FitIntoVisibleArea(rawPlacement, displays, rawPlacement);
        return true;
    }

    private static bool IsLayoutId(string value) =>
        value.Length == 11 && value[0] == 'd' && value[1..].All(Uri.IsHexDigit);

    private static bool TryReadDouble(IReadOnlyDictionary<string, string> dict, string key, out double value)
    {
        value = 0.0;
        if (!dict.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw)) return false;

        var text = raw.StartsWith("text:", StringComparison.OrdinalIgnoreCase) ? raw[5..] : raw;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    private static bool TryReadBool(IReadOnlyDictionary<string, string> dict, string key, bool fallback)
    {
        if (!dict.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw)) return fallback;

        var text = raw.StartsWith("text:", StringComparison.OrdinalIgnoreCase) ? raw[5..] : raw;
        if (bool.TryParse(text, out var b)) return b;
        return text is "1" or "true";
    }
}
