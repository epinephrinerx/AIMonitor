using AIMonitor.Application.AgentUsage;
using AIMonitor.Domain;
using AIMonitor.Infrastructure.AgentUsage;
using AIMonitor.TestSupport;

namespace AIMonitor.Infrastructure.Tests.AgentUsage;

/// <summary>
/// Tests for <see cref="CcUsageAgentUsageSource"/> per SPEC R-014 §P1.3 "Source": runner + parser +
/// <c>IClock</c> for retrievedAt.
/// Seam per decisions M3 and M4: the internal ctor is <c>CcUsageAgentUsageSource(IClock clock,
/// ICcUsageRunner runner)</c> where <c>ICcUsageRunner.RunAsync(DateOnly? since, CancellationToken ct)</c>
/// returns <c>CcUsageRunResult(string Output, AgentUsagePricing Pricing)</c>, so these tests use a
/// hand-written fake runner and never touch a process seam — process-level behaviour (argument list,
/// timeout, kill-on-deadline, output limit, the offline retry) is covered in
/// <c>CcUsageRunnerTests</c>. No real ccusage is ever started (Docs/TESTING.md).
/// </summary>
public sealed class CcUsageAgentUsageSourceTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 9, 13, 0, 0, TimeSpan.Zero);

    // One valid daily row (claude 100/200/300/350, total 1000 → Other = 50; model bucket sum
    // 60 + 80 + 20 + 40 = 200) — the same shape as the parser unit tests.
    private const string OneRowJson =
        """{"daily":[{"period":"2026-11-05","agents":[{"agent":"claude","inputTokens":100,"outputTokens":200,"cacheCreationTokens":300,"cacheReadTokens":350,"totalTokens":1000,"modelBreakdowns":[{"modelName":"claude-opus-4-8","inputTokens":60,"outputTokens":80,"cacheCreationTokens":20,"cacheReadTokens":40,"cost":0.0425}]}]}],"monthly":[],"session":[]}""";

    /// <summary>
    /// Hand-written fake of the internal runner seam (M3): records the <c>since</c> and token of every
    /// call and returns the queued <see cref="CcUsageRunResult"/> or failure. A call with nothing queued
    /// throws, which turns any unexpected extra runner call into a test failure.
    /// </summary>
    private sealed class FakeRunner : ICcUsageRunner
    {
        private readonly Queue<object> _outcomes = new();

        public List<DateOnly?> Sinces { get; } = new();

        public List<CancellationToken> Tokens { get; } = new();

        public int Calls => Sinces.Count;

        public DateOnly? LastSince => Sinces.Count == 0 ? null : Sinces[^1];

        public void EnqueueSuccess(string output, AgentUsagePricing pricing = AgentUsagePricing.OnlineRequested) =>
            _outcomes.Enqueue(new CcUsageRunResult(output, pricing)); // M3: the record is the seam's return value

        public void EnqueueFailure(AgentUsageErrorKind kind, string message = "synthetic runner failure") =>
            _outcomes.Enqueue(new AgentUsageUnavailableException(kind, message)); // M1: kind first

        public Task<CcUsageRunResult> RunAsync(DateOnly? since, CancellationToken ct)
        {
            Sinces.Add(since);
            Tokens.Add(ct);

            if (_outcomes.Count == 0)
                throw new InvalidOperationException($"unexpected runner call #{Calls}: nothing queued by the test");

            return _outcomes.Dequeue() switch
            {
                CcUsageRunResult result => Task.FromResult(result),
                Exception error => Task.FromException<CcUsageRunResult>(error),
                _ => throw new InvalidOperationException("queued outcome is neither a result nor an exception"),
            };
        }
    }

    private static CcUsageAgentUsageSource Source(FakeRunner runner) =>
        // SPEC P1.3 + decision M4: internal ctor "(IClock clock, ICcUsageRunner runner)".
        new(new FakeClock(FixedNow), runner);

    [Fact]
    public async Task ReadAsync_ParsesTheRunnersOutput_WithTheClockInstantAndTheRunnersPricing()
    {
        // SPEC P1.3: "Source CcUsageAgentUsageSource : IAgentUsageSource = runner + parser + IClock
        // for retrievedAt"; M3 hands the stdout and the pricing flag over in CcUsageRunResult.
        var runner = new FakeRunner();
        runner.EnqueueSuccess(OneRowJson, AgentUsagePricing.OnlineRequested);
        IAgentUsageSource source = Source(runner); // SPEC P1.3: the source implements the port.

        var report = await source.ReadAsync(null, default);

        Assert.Equal(FixedNow, report.RetrievedAt); // "IClock for retrievedAt"
        Assert.Equal(AgentUsagePricing.OnlineRequested, report.Pricing); // first-run success (P1.3)
        Assert.Equal(0, report.SkippedRows);
        var row = Assert.Single(report.Daily);
        Assert.Equal(new DateOnly(2026, 11, 5), row.Date);
        Assert.Equal(50, row.Agents[0].Tokens.Other); // 1000 − (100 + 200 + 300 + 350 = 950) = 50
        Assert.Equal(0.0425m, row.Agents[0].PricedCostUsd);
        // The port is called with the caller's since: null means "no --since".
        Assert.Equal(1, runner.Calls);
        Assert.Null(runner.LastSince);
        Assert.False(runner.Tokens[0].IsCancellationRequested);
    }

    [Fact]
    public async Task ReadAsync_CarriesTheOfflinePricingOfTheRunnerResultIntoTheReport()
    {
        // SPEC P1.3 + decision OP3: the pricing flag is produced by the runner and must reach the
        // report unchanged ("success of the second run → Pricing = Offline").
        var runner = new FakeRunner();
        runner.EnqueueSuccess(OneRowJson, AgentUsagePricing.Offline); // M3: CcUsageRunResult(., Offline)

        var report = await Source(runner).ReadAsync(null, default);

        Assert.Equal(AgentUsagePricing.Offline, report.Pricing);
        Assert.Equal(1, runner.Calls); // the retry itself is the runner's decision, already done for us
    }

    [Fact]
    public async Task ReadAsync_ForwardsSinceToTheRunner()
    {
        // SPEC P1.2 port signature (DateOnly? since) → SPEC P1.3 runner signature (M3, same parameter).
        // The "yyyyMMdd" formatting itself is asserted on the process seam in CcUsageRunnerTests.
        var runner = new FakeRunner();
        runner.EnqueueSuccess(OneRowJson);

        await Source(runner).ReadAsync(new DateOnly(2026, 10, 8), default);

        Assert.Equal(new DateOnly?[] { new(2026, 10, 8) }, runner.Sinces);
    }

    [Fact]
    public async Task ReadAsync_ForwardsTheCallersCancellationTokenToTheRunner()
    {
        // SPEC P1.3/P1.2: the caller's token travels to the process ("same pipe-drain and
        // kill-on-deadline behaviour" as the AGY runner), so it must reach the runner unchanged.
        var runner = new FakeRunner();
        runner.EnqueueSuccess(OneRowJson);
        using var cancellation = new CancellationTokenSource();

        await Source(runner).ReadAsync(null, cancellation.Token);

        Assert.Equal(cancellation.Token, runner.Tokens[0]);
        Assert.True(runner.Tokens[0].CanBeCanceled);
    }

    [Fact]
    public async Task ReadAsync_RunnersOutputUnparseable_ThrowsInvalidResponse_AndCallsTheRunnerExactlyOnce()
    {
        // Decision A6: "Exit 0 + parse failure → InvalidResponse, no retry." Decision R1 lists
        // InvalidResponse among the "no retry (exactly one start)" cases: the source must not add a
        // second run of its own, so the fake has one outcome queued and a second call would throw.
        var runner = new FakeRunner();
        runner.EnqueueSuccess("}}} not json {{{"); // M3: exit-0 output, unparseable

        var error = await Assert.ThrowsAsync<AgentUsageUnavailableException>(() => Source(runner).ReadAsync(null, default));

        Assert.Equal(AgentUsageErrorKind.InvalidResponse, error.Kind);
        Assert.Equal(1, runner.Calls);
    }

    [Theory]
    [InlineData(AgentUsageErrorKind.NotInstalled)]
    [InlineData(AgentUsageErrorKind.Timeout)]
    [InlineData(AgentUsageErrorKind.OutputTooLarge)]
    [InlineData(AgentUsageErrorKind.InvalidResponse)]
    [InlineData(AgentUsageErrorKind.Failed)]
    public async Task Read_PropagatesEveryRunnerFailureKindUnchanged(AgentUsageErrorKind kind)
    {
        // SPEC P1.3: the source is runner + parser with no error handling of its own, so each runner
        // failure kind (P1.2 list) surfaces through the port unchanged; SPEC P1.2 keeps the message
        // user-facing. Requirements R-014: "ไม่มี ccusage … แสดงคำแนะนำการติดตั้ง" relies on NotInstalled.
        var runner = new FakeRunner();
        runner.EnqueueFailure(kind);

        var error = await Assert.ThrowsAsync<AgentUsageUnavailableException>(() => Source(runner).ReadAsync(null, default));

        Assert.Equal(kind, error.Kind);
        Assert.Equal("synthetic runner failure", error.Message);
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public void Source_WithOnlyTheClock_IsAnAgentUsageSource_AndStartingNothingIsTheCtorsJob()
    {
        // Decision M4: public ctor "CcUsageAgentUsageSource(IClock clock)" wires the real
        // CcUsageRunner. SPEC P1.3 composition: "Startup must not run ccusage" — construction alone
        // must therefore be side-effect free. ReadAsync is deliberately not called here: that would
        // launch the real ccusage, which Docs/TESTING.md forbids in tests.
        IAgentUsageSource source = new CcUsageAgentUsageSource(new FakeClock(FixedNow));

        Assert.NotNull(source);
    }
}
