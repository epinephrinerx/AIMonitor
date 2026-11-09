using AIMonitor.Domain;

namespace AIMonitor.Domain.Tests;

/// <summary>
/// Covers SPEC R-014 "P2b detailed" §P2b.5's domain requirement: <c>UsageCounters.Subtract(UsageCounters
/// other)</c> - "checked; throws if any field would go negative". The store calls it twice per
/// replacement: field-by-field floored on the day x model and day x project buckets (B13) and exact on
/// AllTotals (B4). Both the arithmetic and the refusal are tested here, independently of the caller's
/// floor.
/// </summary>
public sealed class UsageCountersSubtractTests
{
    /// <summary>The cell every test starts from. Hand-picked so each field has a different value, which
    /// keeps a swapped-field bug from passing.</summary>
    private const int Messages = 1;
    private const long Input = 100;
    private const long Output = 50;
    private const long CacheWrite = 25;
    private const long CacheRead = 20;
    private const decimal Cost = 0.0006m;
    private const long Unpriced = 2;
    private const long Estimated = 1;

    [Fact]
    public void Subtract_SubtractsEveryField()
    {
        // Field by field: messages 3 - 1 = 2, input 300 - 100 = 200, output 200 - 50 = 150,
        // cache write 100 - 25 = 75, cache read 50 - 20 = 30, cost 0.01 - 0.004 = 0.006,
        // unpriced 7 - 5 = 2, estimated 3 - 2 = 1.
        var from = Counters(messages: 3, input: 300, output: 200, cacheWrite: 100, cacheRead: 50, cost: 0.01m, unpriced: 7, estimated: 3);
        var other = Counters(messages: 1, input: 100, output: 50, cacheWrite: 25, cacheRead: 20, cost: 0.004m, unpriced: 5, estimated: 2);

        var result = from.Subtract(other);

        Assert.Equal(2, result.Messages);
        Assert.Equal(200, result.InputTokens);
        Assert.Equal(150, result.OutputTokens);
        Assert.Equal(75, result.CacheWriteTokens);
        Assert.Equal(30, result.CacheReadTokens);
        Assert.Equal(0.006m, result.CostUsd);
        Assert.Equal(2, result.UnpricedTokens);
        Assert.Equal(1, result.EstimatedTokens);
    }

    [Fact]
    public void Subtract_Zero_LeavesTheCellUnchanged()
    {
        var from = Counters();

        Assert.Equal(from, from.Subtract(UsageCounters.Zero));
    }

    [Fact]
    public void Subtract_Itself_IsZero()
    {
        var from = Counters();

        Assert.Equal(UsageCounters.Zero, from.Subtract(from));
    }

    [Fact]
    public void Subtract_UndoesCombine_InBothDirections()
    {
        // The property the store leans on when a replacement folds a record back out of a bucket it was
        // combined into (P2b.5, OP4): (a + b) - b == a and (a + b) - a == b.
        var a = UsageCounters.ForMessage(1_000, 500, 100, 200, 0.018225m, unpricedTokens: 11, estimatedTokens: 22);
        var b = UsageCounters.ForMessage(3, 4, 5, 6, 0.000007m, unpricedTokens: 1, estimatedTokens: 2);

        Assert.Equal(a, a.Combine(b).Subtract(b));
        Assert.Equal(b, a.Combine(b).Subtract(a));
    }

    [Theory]
    // Each row is `other` with exactly one field one step larger than the source cell (0.0006 -> 0.0007
    // for the cost field), so that field alone would go negative. Both operands stay legal, so the throw
    // can only come from Subtract itself.
    [InlineData("messages", 2, 100, 50, 25, 20, 0.0006, 2, 1)]
    [InlineData("input", 1, 101, 50, 25, 20, 0.0006, 2, 1)]
    [InlineData("output", 1, 100, 51, 25, 20, 0.0006, 2, 1)]
    [InlineData("cacheWrite", 1, 100, 50, 26, 20, 0.0006, 2, 1)]
    [InlineData("cacheRead", 1, 100, 50, 25, 21, 0.0006, 2, 1)]
    [InlineData("cost", 1, 100, 50, 25, 20, 0.0007, 2, 1)]
    [InlineData("unpriced", 1, 100, 50, 25, 20, 0.0006, 3, 1)]
    [InlineData("estimated", 1, 100, 50, 25, 20, 0.0006, 2, 2)]
    public void Subtract_TheOneFieldThatWouldGoNegative_Throws(
        string field,
        int messages,
        long input,
        long output,
        long cacheWrite,
        long cacheRead,
        decimal cost,
        long unpriced,
        long estimated)
    {
        var from = Counters();
        var other = Counters(messages, input, output, cacheWrite, cacheRead, cost, unpriced, estimated);

        // Self-check the row: the field named by the data is the only one that is larger, so a
        // mis-typed row cannot quietly test a different field (or none).
        Assert.Equal(
            FieldNames.Select(name => name == field ? 1 : 0),
            FieldNames.Select(name => Math.Sign(FieldValue(other, name) - FieldValue(from, name))));

        Assert.Throws<ArgumentOutOfRangeException>(() => from.Subtract(other));
    }

    [Fact]
    public void Subtract_EveryFieldShortAtOnce_Throws()
    {
        var from = UsageCounters.ForMessage(10, 10, 10, 10, 0.000_01m, unpricedTokens: 1, estimatedTokens: 1);
        var other = UsageCounters.ForMessage(11, 11, 11, 11, 0.000_02m, unpricedTokens: 2, estimatedTokens: 2);

        Assert.Throws<ArgumentOutOfRangeException>(() => from.Subtract(other));
    }

    [Fact]
    public void Subtract_ARefusedSubtraction_LeavesBothOperandsUsable()
    {
        // B13 has the store floor the day buckets and still do the exact AllTotals subtraction afterwards,
        // so a rejection must not consume or corrupt either operand.
        var from = UsageCounters.ForMessage(100, 0, 0, 0, 0.0005m);
        var tooBig = UsageCounters.ForMessage(101, 0, 0, 0, 0.0005m);

        Assert.Throws<ArgumentOutOfRangeException>(() => from.Subtract(tooBig));
        Assert.Throws<ArgumentOutOfRangeException>(() => from.Subtract(tooBig));
        Assert.Equal(100, from.InputTokens);
        Assert.Equal(101, tooBig.InputTokens);

        // And a legal subtraction still works on the same instances.
        Assert.Equal(60, from.Subtract(UsageCounters.ForMessage(40, 0, 0, 0, 0.0002m)).InputTokens);
    }

    [Fact]
    public void Subtract_CostIsExactDecimal_NotARoundedTotal()
    {
        // B10(b): a $0.035 contribution taken out of $0.035 + $0.000351 must leave exactly $0.000351.
        // A double round-trip would show as 3.51E-05 with a drift here.
        var from = UsageCounters.ForMessage(0, 0, 0, 0, 0.035m + 0.000351m);
        var other = UsageCounters.ForMessage(0, 0, 0, 0, 0.035m);

        Assert.Equal(0.000351m, from.Subtract(other).CostUsd);
        Assert.Equal(0m, other.Subtract(other).CostUsd);
    }

    [Fact]
    public void Subtract_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => UsageCounters.Zero.Subtract(null!));
    }

    /// <summary>Every field <see cref="UsageCounters"/> carries, in the spelling the theory data uses.</summary>
    private static readonly string[] FieldNames =
        ["messages", "input", "output", "cacheWrite", "cacheRead", "cost", "unpriced", "estimated"];

    private static decimal FieldValue(UsageCounters counters, string field) => field switch
    {
        "messages" => counters.Messages,
        "input" => counters.InputTokens,
        "output" => counters.OutputTokens,
        "cacheWrite" => counters.CacheWriteTokens,
        "cacheRead" => counters.CacheReadTokens,
        "cost" => counters.CostUsd,
        "unpriced" => counters.UnpricedTokens,
        "estimated" => counters.EstimatedTokens,
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Not a UsageCounters field."),
    };

    private static UsageCounters Counters(
        int messages = Messages,
        long input = Input,
        long output = Output,
        long cacheWrite = CacheWrite,
        long cacheRead = CacheRead,
        decimal cost = Cost,
        long unpriced = Unpriced,
        long estimated = Estimated) =>
        new(messages, input, output, cacheWrite, cacheRead, cost, unpriced, estimated);
}
