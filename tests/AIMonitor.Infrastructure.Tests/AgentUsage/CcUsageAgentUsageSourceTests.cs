using System.Diagnostics;
using AIMonitor.Application.AgentUsage;
using AIMonitor.Domain;
using AIMonitor.Infrastructure.AgentUsage;
using AIMonitor.TestSupport;

namespace AIMonitor.Infrastructure.Tests.AgentUsage;

/// <summary>
/// Tests for <see cref="CcUsageAgentUsageSource"/> per SPEC R-014 §P1.3 "Source": runner + parser +
/// <c>IClock</c> for retrievedAt, wired through the fake process seam only (no real process).
/// Seam assumption (see handoff report): constructor <c>(CcUsageRunner runner, IClock clock)</c>
/// and the class implements <c>IAgentUsageSource</c>.
/// </summary>
public sealed class CcUsageAgentUsageSourceTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 9, 13, 0, 0, TimeSpan.Zero);

    // One valid daily row (claude 100/200/300/350, total 1000 → Other = 50; model bucket sum
    // 60 + 80 + 20 + 40 = 200) — the same shape as the parser unit tests.
    private const string OneRowJson =
        """{"daily":[{"period":"2026-11-05","agents":[{"agent":"claude","inputTokens":100,"outputTokens":200,"cacheCreationTokens":300,"cacheReadTokens":350,"totalTokens":1000,"modelBreakdowns":[{"modelName":"claude-opus-4-8","inputTokens":60,"outputTokens":80,"cacheCreationTokens":20,"cacheReadTokens":40,"cost":0.0425}]}]}],"monthly":[],"session":[]}""";

    private sealed class Child : ICcUsageProcess
    {
        public TextReader Output { get; init; } = new StringReader(OneRowJson);
        public TextReader Error { get; init; } = new StringReader("");
        public int ExitCode { get; init; }
        public bool Hang { get; init; }
        public bool Killed { get; private set; }
        public bool Disposed { get; private set; }
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitForExitAsync(CancellationToken ct) => Hang ? _exit.Task.WaitAsync(ct) : Task.CompletedTask;
        public void Kill()
        {
            Killed = true;
            _exit.TrySetResult();
        }

        public void Dispose()
        {
            Disposed = true;
            Output.Dispose();
            Error.Dispose();
        }
    }

    private sealed class Factory
    {
        private readonly Queue<Child> _children = new();

        public List<ProcessStartInfo> Infos { get; } = new();

        public int Starts => Infos.Count;

        public void Enqueue(Child child) => _children.Enqueue(child);

        public ICcUsageProcess Start(ProcessStartInfo info)
        {
            Infos.Add(info);
            return _children.Dequeue();
        }
    }

    private static readonly string[] BaseArguments =
        ["daily", "--json", "--by-agent", "--sections", "daily,monthly,session"];

    private static CcUsageAgentUsageSource Source(Factory factory, Func<string?>? resolve = null)
    {
        var runner = new CcUsageRunner(
            resolve ?? (() => @"C:\synthetic\bin\ccusage.exe"),
            factory.Start,
            TimeSpan.FromSeconds(5),
            32 * 1024 * 1024);
        // SPEC P1.3: "CcUsageAgentUsageSource : IAgentUsageSource = runner + parser + IClock".
        return new CcUsageAgentUsageSource(runner, new FakeClock(FixedNow));
    }

    [Fact]
    public async Task ReadAsync_BuildsReportFromStdout_CarriesClockInstantAndOnlinePricing()
    {
        var factory = new Factory();
        factory.Enqueue(new Child());
        IAgentUsageSource source = Source(factory); // SPEC P1.3: the source implements the port.

        var report = await source.ReadAsync(null, default);

        Assert.Equal(FixedNow, report.RetrievedAt); // "IClock for retrievedAt"
        Assert.Equal(AgentUsagePricing.OnlineRequested, report.Pricing); // first-run success (P1.3)
        var row = Assert.Single(report.Daily);
        Assert.Equal(new DateOnly(2026, 11, 5), row.Date);
        Assert.Equal(50, row.Agents[0].Tokens.Other); // 1000 − (100 + 200 + 300 + 350 = 950) = 50
        Assert.Equal(0.0425m, row.Agents[0].PricedCostUsd);
    }

    [Fact]
    public async Task ReadAsync_WithSince_PassesInvariantDateArgumentsToTheProcess()
    {
        // SPEC P1.3: constants plus "--since yyyyMMdd" (invariant) when since is set.
        var factory = new Factory();
        factory.Enqueue(new Child());
        var source = Source(factory);

        await source.ReadAsync(new DateOnly(2026, 10, 8), default);

        Assert.Equal(
            BaseArguments.Append("--since").Append("20261008").ToArray(),
            factory.Infos[0].ArgumentList);
    }

    [Fact]
    public async Task ReadAsync_FirstRunFailsThenOfflineSucceeds_ReportCarriesOfflinePricing()
    {
        // SPEC P1.3 + P1.1: the pricing flag from the runner reaches the report ("success of the
        // second run → Pricing = Offline").
        var factory = new Factory();
        factory.Enqueue(new Child { ExitCode = 1 });
        factory.Enqueue(new Child());
        var source = Source(factory);

        var report = await source.ReadAsync(null, default);

        Assert.Equal(AgentUsagePricing.Offline, report.Pricing);
        Assert.Equal(2, factory.Starts);
        Assert.Equal(BaseArguments.Append("--offline").ToArray(), factory.Infos[1].ArgumentList);
    }

    [Fact]
    public async Task ReadAsync_ExitZeroWithUnparseableJson_ThrowsInvalidResponse_WithoutOfflineRetry()
    {
        // Decision A6: "Exit 0 + parse failure → InvalidResponse, no retry." Exactly one process was
        // started even though the report is unusable.
        var factory = new Factory();
        factory.Enqueue(new Child { Output = new StringReader("}}} not json {{{") });
        var source = Source(factory);

        var error = await Assert.ThrowsAsync<AgentUsageUnavailableException>(() => source.ReadAsync(null, default));

        Assert.Equal(AgentUsageErrorKind.InvalidResponse, error.Kind);
        Assert.Equal(1, factory.Starts);
    }

    [Fact]
    public async Task ReadAsync_WhenNotInstalled_PropagatesNotInstalled()
    {
        // SPEC P1.3 composition chain: the source surfaces the runner's NotInstalled unchanged
        // (Requirements R-014: "ไม่มี ccusage … แสดงคำแนะนำการติดตั้ง").
        var factory = new Factory();
        var source = Source(factory, resolve: () => null);

        var error = await Assert.ThrowsAsync<AgentUsageUnavailableException>(() => source.ReadAsync(null, default));

        Assert.Equal(AgentUsageErrorKind.NotInstalled, error.Kind);
        Assert.Equal(0, factory.Starts);
    }

    [Fact]
    public async Task ReadAsync_BothRunsTimeout_PropagatesTimeout()
    {
        // SPEC P1.3: "Both fail → error of the second run" surfaced through the port; the cache
        // (Application layer) keeps serving LastGood.
        var clock = new FakeClock(FixedNow);
        var factory = new Factory();
        factory.Enqueue(new Child { Hang = true });
        factory.Enqueue(new Child { Hang = true });
        var runner = new CcUsageRunner(
            () => @"C:\synthetic\bin\ccusage.exe", factory.Start, TimeSpan.FromMilliseconds(30), 32 * 1024 * 1024);
        var source = new CcUsageAgentUsageSource(runner, clock);

        var error = await Assert.ThrowsAsync<AgentUsageUnavailableException>(() =>
            source.ReadAsync(null, default).WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(AgentUsageErrorKind.Timeout, error.Kind);
        Assert.Equal(2, factory.Starts);
    }
}
