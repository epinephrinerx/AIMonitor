namespace AIMonitor.Domain;

/// <summary>
/// Formats the caveat attached to an equivalent-value figure: what fraction of it is a family-rate
/// estimate, and what it left out entirely because a model had no published price at all
/// (<see cref="PricingKind.Unknown"/> tokens add no money and must not vanish silently - a dashboard
/// that prints a dollar figure it knows is incomplete, without saying so, is worse than one that
/// admits the gap).
/// </summary>
public static class UsageValueCaveatFormatter
{
    private const string BaseCaveat = "at API list price";

    /// <param name="estimatedTokens">Tokens priced at a family rate (<see cref="PricingKind.Estimated"/>)
    /// within the range being described.</param>
    /// <param name="estimatedModels">Display names of models contributing <paramref name="estimatedTokens"/>.</param>
    /// <param name="unpricedTokens">Tokens with no published price (<see cref="PricingKind.Unknown"/>)
    /// within the range being described, already excluded from the value being captioned.</param>
    /// <param name="unpricedModels">Display names of models contributing <paramref name="unpricedTokens"/>.</param>
    public static string Format(
        long estimatedTokens,
        IReadOnlyList<string> estimatedModels,
        long unpricedTokens,
        IReadOnlyList<string> unpricedModels)
    {
        ArgumentNullException.ThrowIfNull(estimatedModels);
        ArgumentNullException.ThrowIfNull(unpricedModels);

        var segments = new List<string>(2);

        if (estimatedTokens > 0)
        {
            segments.Add(
                $"estimates {FormatTokenCount(estimatedTokens)} for {NamesOrCount(estimatedModels)} " +
                "at the model family's rate");
        }

        if (unpricedTokens > 0)
        {
            segments.Add(
                $"excludes {FormatTokenCount(unpricedTokens)} from {NamesOrCount(unpricedModels)} " +
                "(no published price)");
        }

        return segments.Count == 0 ? BaseCaveat : BaseCaveat + "; " + string.Join("; ", segments);
    }

    /// <summary>Up to two names are listed; three or more collapse to a count, since a caption is one
    /// line and several model ids would not fit on it.</summary>
    private static string NamesOrCount(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "no models",
        1 => names[0],
        2 => $"{names[0]} and {names[1]}",
        _ => $"{names.Count} models",
    };

    private static string FormatTokenCount(long tokens)
    {
        if (tokens >= 1_000_000)
        {
            var millions = Math.Round(tokens / 1_000_000.0, 1, MidpointRounding.AwayFromZero);
            var text = millions == Math.Floor(millions) ? $"{millions:0}M" : $"{millions:0.0}M";
            return $"{text} tokens";
        }

        if (tokens >= 1_000)
        {
            var thousands = Math.Round(tokens / 1_000.0, 0, MidpointRounding.AwayFromZero);
            return $"{thousands:0}K tokens";
        }

        return $"{tokens} tokens";
    }
}
