namespace AIMonitor.Domain;

/// <summary>
/// How a model's price was arrived at. The distinction is not academic: two models in the same
/// family can ship at different published rates, so a price resolved from a family prefix carries
/// real doubt that a caption must be able to disclose separately from "no basis at all".
/// </summary>
public enum PricingKind
{
    /// <summary>No basis for a price at all; contributes zero to <see cref="ClaudeModelPricing.Cost"/>.</summary>
    Unknown = 0,

    /// <summary>Priced at the model family's rate because the exact model id has no published price
    /// of its own. Does contribute to <see cref="ClaudeModelPricing.Cost"/>, but on a guess.</summary>
    Estimated = 1,

    /// <summary>Published for this exact model id (or its id with a trailing date snapshot trimmed).</summary>
    Exact = 2,
}

/// <summary>USD list price per million tokens for one Claude model (or model family).</summary>
public sealed record ClaudeModelRate
{
    private const decimal CacheWrite5mMultiplier = 1.25m;
    private const decimal CacheWrite1hMultiplier = 2.0m;

    public decimal InputPerMillion { get; }

    public decimal OutputPerMillion { get; }

    public string DisplayName { get; }

    public decimal CacheWrite5mPerMillion => InputPerMillion * CacheWrite5mMultiplier;

    public decimal CacheWrite1hPerMillion => InputPerMillion * CacheWrite1hMultiplier;

    public decimal CacheReadPerMillion { get; }

    public ClaudeModelRate(decimal inputPerMillion, decimal outputPerMillion, string displayName, decimal cacheReadPerMillion)
    {
        if (inputPerMillion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inputPerMillion), inputPerMillion, "Rate cannot be negative.");
        }

        if (outputPerMillion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(outputPerMillion), outputPerMillion, "Rate cannot be negative.");
        }

        if (cacheReadPerMillion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cacheReadPerMillion), cacheReadPerMillion, "Rate cannot be negative.");
        }

        InputPerMillion = inputPerMillion;
        OutputPerMillion = outputPerMillion;
        DisplayName = DomainGuard.RequireNonBlank(displayName, nameof(displayName));
        CacheReadPerMillion = cacheReadPerMillion;
    }
}

/// <summary>
/// Per-token list prices for local Claude token usage. On a Max or Pro subscription nothing here is
/// actually billed - the figures are an "equivalent API value", i.e. what the same traffic would have
/// cost at list price on the Claude API. Exact model ids are matched first; a trailing date snapshot
/// (e.g. <c>-20251001</c>) is trimmed and retried; anything left falls back to a family prefix table
/// at <see cref="PricingKind.Estimated"/>, and a model matching neither is <see cref="PricingKind.Unknown"/>
/// and priced at zero.
/// </summary>
public static class ClaudeModelPricing
{
    private const int Million = 1_000_000;
    private const int DateSuffixLength = 8;
    private const long Haiku55TierThreshold = 100_000;

    private static readonly IReadOnlyDictionary<string, ClaudeModelRate> ExactRates =
        new Dictionary<string, ClaudeModelRate>(StringComparer.Ordinal)
        {
            ["claude-fable-5-1"] = new(10.00m, 50.00m, "Fable 5.1", 0.25m),
            ["claude-fable-5"] = new(10.00m, 50.00m, "Fable 5", 1.00m),
            ["claude-mythos-5-1"] = new(10.00m, 50.00m, "Mythos 5.1", 0.25m),
            ["claude-mythos-5"] = new(10.00m, 50.00m, "Mythos 5", 1.00m),
            ["claude-opus-5-5"] = new(4.00m, 20.00m, "Opus 5.5", 0.20m),
            ["claude-opus-5"] = new(5.00m, 25.00m, "Opus 5", 0.50m),
            ["claude-opus-4-8"] = new(5.00m, 25.00m, "Opus 4.8", 0.50m),
            ["claude-opus-4-7"] = new(5.00m, 25.00m, "Opus 4.7", 0.50m),
            ["claude-opus-4-6"] = new(5.00m, 25.00m, "Opus 4.6", 0.50m),
            ["claude-opus-4-5"] = new(5.00m, 25.00m, "Opus 4.5", 0.50m),
            ["claude-opus-4-1"] = new(15.00m, 75.00m, "Opus 4.1", 1.50m),
            ["claude-opus-4"] = new(15.00m, 75.00m, "Opus 4", 1.50m),
            ["claude-sonnet-5-5"] = new(2.00m, 10.00m, "Sonnet 5.5", 0.10m),
            ["claude-sonnet-5"] = new(2.00m, 10.00m, "Sonnet 5", 0.20m),
            ["claude-sonnet-4-6"] = new(3.00m, 15.00m, "Sonnet 4.6", 0.30m),
            ["claude-sonnet-4-5"] = new(3.00m, 15.00m, "Sonnet 4.5", 0.30m),
            ["claude-sonnet-4"] = new(3.00m, 15.00m, "Sonnet 4", 0.30m),
            ["claude-haiku-5-5"] = new(0.10m, 0.50m, "Haiku 5.5", 0.01m),
            ["claude-haiku-4-5"] = new(1.00m, 5.00m, "Haiku 4.5", 0.10m),
        };

    private static readonly ClaudeModelRate Haiku55HighTier = new(0.50m, 2.50m, "Haiku 5.5", 0.05m);

    /// <summary>Prefix fallbacks for ids this table predates (e.g. a future Opus point release).
    /// Order matters only in that every prefix here happens to be unambiguous; the first (only) match wins.</summary>
    private static readonly (string Prefix, ClaudeModelRate Rate)[] PrefixRates =
    [
        ("claude-fable", new ClaudeModelRate(10.00m, 50.00m, "Fable", 0.25m)),
        ("claude-mythos", new ClaudeModelRate(10.00m, 50.00m, "Mythos", 0.25m)),
        ("claude-opus", new ClaudeModelRate(4.00m, 20.00m, "Opus", 0.20m)),
        ("claude-sonnet", new ClaudeModelRate(2.00m, 10.00m, "Sonnet", 0.10m)),
        ("claude-haiku", new ClaudeModelRate(0.10m, 0.50m, "Haiku", 0.01m)),
    ];

    private static readonly ClaudeModelRate UnknownRate = new(0m, 0m, "Unknown", 0m);

    /// <summary>The rate to use, and how much faith to put in it.</summary>
    public static (ClaudeModelRate Rate, PricingKind Kind) Resolve(string? model)
    {
        if (string.IsNullOrEmpty(model))
        {
            return (UnknownRate, PricingKind.Unknown);
        }

        if (ExactRates.TryGetValue(model, out var exact))
        {
            return (exact, PricingKind.Exact);
        }

        var trimmed = model;
        var lastHyphen = model.LastIndexOf('-');
        if (lastHyphen >= 0)
        {
            var tail = model[(lastHyphen + 1)..];
            if (tail.Length == DateSuffixLength && IsAllAsciiDigits(tail))
            {
                var dateStripped = model[..lastHyphen];
                if (ExactRates.TryGetValue(dateStripped, out var datedExact))
                {
                    return (datedExact, PricingKind.Exact);
                }

                trimmed = dateStripped;
            }
        }

        foreach (var (prefix, rate) in PrefixRates)
        {
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
            {
                return (rate, PricingKind.Estimated);
            }
        }

        return (UnknownRate, PricingKind.Unknown);
    }

    public static ClaudeModelRate RateFor(string? model) => Resolve(model).Rate;

    public static PricingKind PriceKind(string? model) => Resolve(model).Kind;

    /// <summary>Is there any basis for pricing this model at all? False only for <see cref="PricingKind.Unknown"/>,
    /// where every rate is zero and the model contributes nothing to <see cref="Cost"/>.</summary>
    public static bool IsPriced(string? model) => PriceKind(model) != PricingKind.Unknown;

    public static string DisplayName(string? model)
    {
        var (rate, kind) = Resolve(model);
        return kind == PricingKind.Unknown
            ? (string.IsNullOrEmpty(model) ? "Unknown" : model)
            : rate.DisplayName;
    }

    /// <summary>Equivalent API list-price value in USD for one message's token counts. Negative
    /// token counts are a caller bug (every ingest path validates non-negativity first), so they are
    /// not defended against here.</summary>
    public static decimal Cost(
        string? model,
        long inputTokens = 0,
        long outputTokens = 0,
        long cacheWrite5mTokens = 0,
        long cacheWrite1hTokens = 0,
        long cacheReadTokens = 0)
    {
        var rate = RateFor(model);
        var prompt = inputTokens + cacheWrite5mTokens + cacheWrite1hTokens + cacheReadTokens;
        if (IsHaiku55Exact(model) && prompt > Haiku55TierThreshold)
        {
            rate = Haiku55HighTier;
        }

        var total =
            inputTokens * rate.InputPerMillion +
            outputTokens * rate.OutputPerMillion +
            cacheWrite5mTokens * rate.CacheWrite5mPerMillion +
            cacheWrite1hTokens * rate.CacheWrite1hPerMillion +
            cacheReadTokens * rate.CacheReadPerMillion;
        return total / Million;
    }

    private static bool IsHaiku55Exact(string? model)
    {
        if (string.IsNullOrEmpty(model))
        {
            return false;
        }

        if (model == "claude-haiku-5-5")
        {
            return true;
        }

        var lastHyphen = model.LastIndexOf('-');
        if (lastHyphen < 0)
        {
            return false;
        }

        var tail = model[(lastHyphen + 1)..];
        if (tail.Length != DateSuffixLength || !IsAllAsciiDigits(tail))
        {
            return false;
        }

        return model[..lastHyphen] == "claude-haiku-5-5";
    }

    private static bool IsAllAsciiDigits(string value)
    {
        foreach (var ch in value)
        {
            if (!char.IsAsciiDigit(ch))
            {
                return false;
            }
        }

        return true;
    }
}
