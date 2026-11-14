using System.Globalization;

namespace AIMonitor.Infrastructure.Providers.OpenAi;

/// <summary>Number formatting for OpenAI stat tiles (Admin API spend/tokens, Codex daily token
/// totals), matching the Python baseline's <c>formatting.compact()</c>/<c>formatting.money()</c> so
/// the figures read the same as the app this replaces.</summary>
internal static class OpenAiNumberFormatting
{
    /// <summary>1,284 / 12.9K / 4.2M / 1.0B -&gt; "1B" - the stat-tile number format.</summary>
    public static string FormatCompactNumber(double value)
    {
        var magnitude = Math.Abs(value);
        if (magnitude >= 1_000_000_000)
        {
            return FormatSuffixed(value / 1_000_000_000, "B");
        }

        if (magnitude >= 1_000_000)
        {
            return FormatSuffixed(value / 1_000_000, "M");
        }

        if (magnitude >= 10_000)
        {
            return FormatSuffixed(value / 1_000, "K");
        }

        return value.ToString("#,##0", CultureInfo.InvariantCulture);
    }

    public static string FormatMoney(double value)
    {
        if (value != 0 && Math.Abs(value) < 0.01)
        {
            return "<$0.01";
        }

        if (Math.Abs(value) >= 1000)
        {
            return "$" + FormatCompactNumber(value);
        }

        return "$" + value.ToString("#,##0.00", CultureInfo.InvariantCulture);
    }

    private static string FormatSuffixed(double scaled, string suffix)
    {
        var rounded = scaled.ToString("0.0", CultureInfo.InvariantCulture);
        if (rounded.EndsWith(".0", StringComparison.Ordinal))
        {
            rounded = rounded[..^2];
        }

        return rounded + suffix;
    }
}
