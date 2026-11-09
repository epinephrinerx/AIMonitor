using System.ComponentModel;
using System.Diagnostics;
using AIMonitor.Application.AgentUsage;
using AIMonitor.Domain;
using AIMonitor.Infrastructure.AgentUsage;

namespace AIMonitor.Infrastructure.Tests.AgentUsage;

/// <summary>
/// Tests for <see cref="CcUsageRunner"/> per SPEC R-014 §P1.3 "Runner", decisions A6, A12, OP3,
/// R1 (the offline retry rules, which retry cases run and which failures must not),
/// R2 (OutputTooLarge vs Timeout classification with asynchronous readers) and R7 (the seam),
/// mirroring the hand-written fake-process shape of the AGY runner tests
/// (<c>tests/AIMonitor.Infrastructure.Tests/Providers/Agy/AgyProcessBoundaryTests.cs</c>).
/// No real process is ever started.
/// Seam per decisions M3 and R7: <c>CcUsageRunner(Func&lt;string?&gt; resolve,
/// Func&lt;ProcessStartInfo, ICcUsageProcess&gt; start, TimeSpan timeout, int outputLimit)</c> with
/// <c>internal interface ICcUsageRunner { Task&lt;CcUsageRunResult&gt; RunAsync(DateOnly? since,
/// CancellationToken ct); }</c> and <c>internal sealed record CcUsageRunResult(string Output,
/// AgentUsagePricing Pricing)</c>. Most tests reach the runner as <c>ICcUsageRunner</c> (M3); the
/// R7 decision additionally requires <c>RunAsync</c> to be public (implicit) on the class with the
/// limit named <c>outputLimit</c>, which the separate
/// <c>CcUsageRunnerSeamR7Tests</c> pins by calling it directly on <c>CcUsageRunner</c> — that file
/// only compiles once WO2 applies R7, so the tests here keep the M3 reachability.
/// </summary>
public sealed class CcUsageRunnerTests
{
    private const string EmptyReportJson = """{"daily":[],"monthly":[],"session":[]}""";

    /// SPEC P1.3: constants-only argument list, exactly as written in the spec.
    private static readonly string[] BaseArguments =
        ["daily", "--json", "--by-agent", "--sections", "daily,monthly,session"];

    /// Generous stdout limit for tests that are not about the limit itself (the spec default is 32 MiB;
    /// the seam takes an explicit limit, so the default is not under test here).
    private const int DefaultLimit = 32 * 1024 * 1024;

    private sealed class Child : ICcUsageProcess
    {
        public TextReader Output { get; init; } = new StringReader(EmptyReportJson);
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

    /// <summary>
    /// A pipe-like <see cref="TextReader"/> required by decision R2 ("fakes must include an async
    /// TextReader that yields, not only <see cref="StringReader"/>"): every read first
    /// <c>await Task.Yield()</c>s — so a read never completes synchronously and the runner must be
    /// mid-await when later reads arrive — then hands out the data in fixed-size chunks and honours
    /// the cancellation token the way a real pipe reader does.
    /// </summary>
    private sealed class YieldingTextReader : TextReader
    {
        private readonly string _data;
        private readonly int _chunkSize;
        private int _position;

        public YieldingTextReader(string data, int chunkSize)
        {
            _data = data;
            _chunkSize = chunkSize;
        }

        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();

            if (_position >= _data.Length)
                return 0;

            var take = Math.Min(_chunkSize, _data.Length - _position);
            _data.AsSpan(_position, take).CopyTo(buffer.Span);
            _position += take;
            return take;
        }
    }

    /// <summary>
    /// A child whose exit "happens at the same instant the deadline fires" (decision R2: "If the
    /// deadline fired, the result is Timeout even when the process has exited in the same instant"):
    /// <see cref="WaitForExitAsync"/> completes its task from a callback registered on the token the
    /// runner passes in — the same token the deadline cancels — with a clean exit code and drained
    /// output, so a runner that looks at the exit first would be tempted to return the output
    /// instead of reporting Timeout.
    /// </summary>
    private sealed class ExitOnDeadlineChild : ICcUsageProcess
    {
        public TextReader Output { get; init; } = new YieldingTextReader(EmptyReportJson, 8);
        public TextReader Error { get; init; } = new StringReader("");
        public int ExitCode { get; } = 0; // the process "exited fine" — the deadline must still win
        public bool Killed { get; private set; }
        public bool Disposed { get; private set; }
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitForExitAsync(CancellationToken ct)
        {
            ct.Register(() => _exit.TrySetResult());
            return _exit.Task;
        }

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
        private readonly Queue<ICcUsageProcess> _children = new();

        public List<ProcessStartInfo> Infos { get; } = new();

        public int Starts => Infos.Count;

        public void Enqueue(ICcUsageProcess child) => _children.Enqueue(child);

        public ICcUsageProcess Start(ProcessStartInfo info)
        {
            Infos.Add(info);
            return _children.Dequeue();
        }
    }

    /// <c>CcUsageRunner</c> implements <c>ICcUsageRunner.RunAsync</c> explicitly (decision M3 wires
    /// the source to the interface, not the class), so the seam is reached as <c>ICcUsageRunner</c>.
    private static ICcUsageRunner Runner(Func<string?> resolve, Factory factory, TimeSpan timeout, int outputLimit = DefaultLimit) =>
        new CcUsageRunner(resolve, factory.Start, timeout, outputLimit);

    private static void AssertCommonStartInfo(ProcessStartInfo info, string expectedFileName)
    {
        // SPEC P1.3: "UseShellExecute=false, no window, stdin closed" and constants-only arguments.
        // (Stdin being *closed* after redirect is not observable through the process seam; the redirect
        // flag is asserted, the close is covered by review — see handoff report.)
        Assert.Equal(expectedFileName, info.FileName);
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
        Assert.True(info.RedirectStandardInput);
        Assert.True(info.RedirectStandardOutput);
        Assert.True(info.RedirectStandardError);
    }

    [Fact]
    public async Task MissingExecutable_ThrowsNotInstalled_WithoutStartingAProcess()
    {
        // SPEC P1.3: "Not found → NotInstalled" with the exact quoted message; "Never invoke npx".
        // Decision R1: no --offline retry on NotInstalled — "exactly one start" here means zero,
        // and the start factory throws if called, proving no process is launched when resolution
        // fails (and therefore no second run either).
        ICcUsageRunner runner = new CcUsageRunner( // M3: RunAsync is reachable through ICcUsageRunner only
            () => null,
            _ => throw new InvalidOperationException("must not start"),
            TimeSpan.FromSeconds(1),
            DefaultLimit);

        var error = await Assert.ThrowsAsync<AgentUsageUnavailableException>(() => runner.RunAsync(null, default));

        Assert.Equal(AgentUsageErrorKind.NotInstalled, error.Kind);
        Assert.Equal("ccusage was not found. Install it with `npm i -g ccusage`, then restart AIMonitor.", error.Message);
    }

    [Fact]
    public async Task SuccessfulFirstRun_ReturnsStdoutWithOnlineRequestedPricing_AndCleansUp()
    {
        // SPEC P1.3: "first run without --offline … first-run success → OnlineRequested".
        // Decision OP3: even if ccusage's own price fetch silently failed it still exits 0, so the
        // runner reports OnlineRequested; no stderr parsing happens.
        var child = new Child { Output = new StringReader(EmptyReportJson) };
        var factory = new Factory();
        factory.Enqueue(child);
        // M3: the seam returns the CcUsageRunResult record; reaching it through ICcUsageRunner pins
        // that CcUsageRunner implements the internal interface the source depends on.
        ICcUsageRunner runner = Runner(() => @"C:\synthetic\bin\ccusage.exe", factory, TimeSpan.FromSeconds(5));

        CcUsageRunResult result = await runner.RunAsync(null, default); // M3: (string Output, AgentUsagePricing Pricing)

        Assert.Equal(EmptyReportJson, result.Output);
        Assert.Equal(AgentUsagePricing.OnlineRequested, result.Pricing);
        Assert.Equal(1, factory.Starts);
        Assert.True(child.Disposed);

        AssertCommonStartInfo(factory.Infos[0], @"C:\synthetic\bin\ccusage.exe");
        Assert.Equal(BaseArguments, factory.Infos[0].ArgumentList);
        // SPEC P1.3: "NO_COLOR=1 added to the environment".
        Assert.Equal("1", factory.Infos[0].Environment["NO_COLOR"]);
    }

    [Fact]
    public async Task SinceDate_AppendsInvariantPaddedSinceArgument()
    {
        // SPEC P1.3: "plus --since yyyyMMdd (invariant) when since is set".
        // 2025-09-07 invariant-padded → "20250907" (zero-padded month and day).
        var factory = new Factory();
        factory.Enqueue(new Child());
        var runner = Runner(() => "synthetic.exe", factory, TimeSpan.FromSeconds(5));

        await runner.RunAsync(new DateOnly(2025, 9, 7), default);

        Assert.Equal(
            BaseArguments.Append("--since").Append("20250907").ToArray(),
            factory.Infos[0].ArgumentList);
    }

    [Fact]
    public async Task CmdShimResolved_UsesArgumentListUnchanged()
    {
        // Decision A12: "Keep ArgumentList with the .cmd path (the npm shim forwards %*, the comma
        // argument has no spaces so it is not quoted)." Forwarding itself needs the real npm shim and
        // is Integrator-verified once; what is unit-testable is that the runner keeps the .cmd path in
        // FileName and the constants in ArgumentList.
        var factory = new Factory();
        factory.Enqueue(new Child());
        var runner = Runner(() => @"C:\synthetic\npm\ccusage.cmd", factory, TimeSpan.FromSeconds(5));

        await runner.RunAsync(null, default);

        Assert.Equal(@"C:\synthetic\npm\ccusage.cmd", factory.Infos[0].FileName);
        Assert.Equal(BaseArguments, factory.Infos[0].ArgumentList);
    }

    [Fact]
    public async Task FirstRunTimeout_RetriesOnceWithOfflineFlag_AndReportsOfflinePricing()
    {
        // SPEC P1.3: "If it times out … run once more with --offline appended; success of the second
        // run → Pricing = Offline". Decision A6: timeout of the first run is retryable. Decision R1:
        // this retry "must actually run" — at 083120e RunOnceAsync throws instead of returning the
        // failure as a result, so the retry gate is never reached and this test is red.
        var first = new Child { Hang = true };
        var second = new Child();
        var factory = new Factory();
        factory.Enqueue(first);
        factory.Enqueue(second);
        var runner = Runner(() => "synthetic.exe", factory, TimeSpan.FromMilliseconds(30));

        var result = await runner.RunAsync(null, default);

        Assert.Equal(EmptyReportJson, result.Output); // M3: stdout arrives as CcUsageRunResult.Output
        Assert.Equal(AgentUsagePricing.Offline, result.Pricing);
        Assert.Equal(2, factory.Starts);
        Assert.Equal(BaseArguments, factory.Infos[0].ArgumentList); // first run: no --offline
        Assert.Equal(BaseArguments.Append("--offline").ToArray(), factory.Infos[1].ArgumentList);
        Assert.True(first.Killed); // kill-on-deadline, mirroring the AGY runner
        Assert.True(first.Disposed);
        Assert.True(second.Disposed);
    }

    [Fact]
    public async Task FirstRunNonZeroExit_RetriesOnceWithOfflineFlag_AndReportsOfflinePricing()
    {
        // SPEC P1.3: "or exits non-zero, run once more with --offline appended". Decision R1: a
        // first-run non-zero exit must actually trigger the retry (red at 083120e — the failure
        // escapes RunOnceAsync and the retry gate is never reached).
        var first = new Child { ExitCode = 1 };
        var second = new Child();
        var factory = new Factory();
        factory.Enqueue(first);
        factory.Enqueue(second);
        var runner = Runner(() => "synthetic.exe", factory, TimeSpan.FromSeconds(5));

        var result = await runner.RunAsync(null, default);

        Assert.Equal(AgentUsagePricing.Offline, result.Pricing); // M3: CcUsageRunResult.Pricing
        Assert.Equal(2, factory.Starts);
        Assert.Equal(BaseArguments.Append("--offline").ToArray(), factory.Infos[1].ArgumentList);
    }

    [Fact]
    public async Task BothRunsFailNonZero_ThrowsFailed_FromTheSecondRun()
    {
        // SPEC P1.3: "Both fail → error of the second run." Second run exits non-zero → Failed, with
        // the exact quoted message. Decision R1 confirms: "both fail → second run's error kind", and
        // this requires the retry to run at all (red at 083120e).
        var factory = new Factory();
        factory.Enqueue(new Child { ExitCode = 1 });
        factory.Enqueue(new Child { ExitCode = 2 });
        var runner = Runner(() => "synthetic.exe", factory, TimeSpan.FromSeconds(5));

        var error = await Assert.ThrowsAsync<AgentUsageUnavailableException>(() => runner.RunAsync(null, default));

        Assert.Equal(AgentUsageErrorKind.Failed, error.Kind);
        Assert.Equal("ccusage could not read usage. Run `ccusage daily` in a terminal to see why.", error.Message);
        Assert.Equal(2, factory.Starts);
    }

    [Fact]
    public async Task TimeoutThenNonZeroExit_ThrowsFailed_BecauseTheSecondRunDecidesTheKind()
    {
        // SPEC P1.3: "Both fail → error of the second run" — the first run's timeout kind must not leak.
        // Decision R1 restates it as "both fail → second run's error kind"; the retry must run first.
        var factory = new Factory();
        factory.Enqueue(new Child { Hang = true });
        factory.Enqueue(new Child { ExitCode = 3 });
        var runner = Runner(() => "synthetic.exe", factory, TimeSpan.FromMilliseconds(30));

        var error = await Assert.ThrowsAsync<AgentUsageUnavailableException>(() =>
            runner.RunAsync(null, default).WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(AgentUsageErrorKind.Failed, error.Kind);
        Assert.Equal(2, factory.Starts);
    }

    [Fact]
    public async Task NonZeroExitThenTimeout_ThrowsTimeout_BecauseTheSecondRunDecidesTheKind()
    {
        // SPEC P1.3: "Both fail → error of the second run" (other direction: second run timed out).
        // Decision R1 restates it as "both fail → second run's error kind"; the retry must run first.
        var factory = new Factory();
        factory.Enqueue(new Child { ExitCode = 1 });
        factory.Enqueue(new Child { Hang = true });
        var runner = Runner(() => "synthetic.exe", factory, TimeSpan.FromMilliseconds(30));

        var error = await Assert.ThrowsAsync<AgentUsageUnavailableException>(() =>
            runner.RunAsync(null, default).WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(AgentUsageErrorKind.Timeout, error.Kind);
        Assert.Equal(2, factory.Starts);
    }

    [Fact]
    public async Task FailedRun_DoesNotExposeStderrText()
    {
        // SPEC P1.2: messages "never contain stdout/stderr text, file paths or environment values".
        // Decision R1: a first-run non-zero exit retries, so the second run's (also non-zero) failure
        // is the one thrown; the retry path must actually run (two starts) and still hide the stderr.
        var factory = new Factory();
        factory.Enqueue(new Child { ExitCode = 1, Error = new StringReader("synthetic-private-detail") });
        factory.Enqueue(new Child { ExitCode = 1, Error = new StringReader("synthetic-private-detail") });
        var runner = Runner(() => "synthetic.exe", factory, TimeSpan.FromSeconds(5));

        var error = await Assert.ThrowsAsync<AgentUsageUnavailableException>(() => runner.RunAsync(null, default));

        Assert.DoesNotContain("synthetic-private-detail", error.Message);
        Assert.Equal(2, factory.Starts); // stderr content alone never triggers an extra retry
    }

    [Fact]
    public async Task ExcessiveStdout_KillsAndThrowsOutputTooLarge_WithoutOfflineRetry()
    {
        // SPEC P1.3: "stdout limit … (exceeding → kill, OutputTooLarge)". Decision A6 and R1: retry
        // happens only on timeout or non-zero exit, so an OutputTooLarge first run must NOT start a
        // second run ("exactly one start"). Decision R2: this is OutputTooLarge even with a synchronous
        // StringReader; the async pipe-reader case is asserted separately below.
        var child = new Child { Output = new StringReader(new string('x', 100)) };
        var factory = new Factory();
        factory.Enqueue(child);
        var runner = Runner(() => "synthetic.exe", factory, TimeSpan.FromSeconds(5), 16);

        var error = await Assert.ThrowsAsync<AgentUsageUnavailableException>(() => runner.RunAsync(null, default));

        Assert.Equal(AgentUsageErrorKind.OutputTooLarge, error.Kind);
        Assert.True(child.Killed);
        Assert.True(child.Disposed);
        Assert.Equal(1, factory.Starts);
    }

    [Fact]
    public async Task LargeStderr_IsDrainedAndDiscarded_DoesNotFailTheRun_AndIsNotPartOfTheResult()
    {
        // SPEC P1.3: "stderr drained and discarded, capped at 1 MiB". Exceeding the internal stderr
        // cap must not fail the run (the 32 MiB limit applies to stdout), so a ~2 MiB stderr with a
        // clean stdout and exit 0 still yields the stdout and OnlineRequested.
        var stderr = new string('e', 1024 * 1024 * 2); // 2 MiB of synthetic stderr
        var child = new Child { Error = new StringReader(stderr) };
        var factory = new Factory();
        factory.Enqueue(child);
        var runner = Runner(() => "synthetic.exe", factory, TimeSpan.FromSeconds(10));

        var result = await runner.RunAsync(null, default);

        Assert.Equal(EmptyReportJson, result.Output);
        Assert.Equal(AgentUsagePricing.OnlineRequested, result.Pricing);
        Assert.Equal(1, factory.Starts);
        Assert.True(child.Disposed);
    }

    [Fact]
    public async Task SuccessfulRun_StderrIsNotParsedIntoPricing()
    {
        // Decision OP3: "No stderr parsing." Price-fetch-failure-looking stderr with exit 0 must still
        // produce OnlineRequested (ccusage silently uses embedded prices).
        var child = new Child { Error = new StringReader("price fetch failed: network down") };
        var factory = new Factory();
        factory.Enqueue(child);
        var runner = Runner(() => "synthetic.exe", factory, TimeSpan.FromSeconds(5));

        var result = await runner.RunAsync(null, default);

        Assert.Equal(AgentUsagePricing.OnlineRequested, result.Pricing);
        Assert.Equal(1, factory.Starts);
    }

    [Fact]
    public async Task CallerCancellation_Propagates_AndCleansUpTheChild()
    {
        // SPEC P1.3: same behaviour as the AGY runner ("same pipe-drain and kill-on-deadline
        // behaviour"); cancellation is not a timeout, so decision A6 must not trigger an offline
        // retry. Decision R1 lists it explicitly: no retry (exactly one start) on caller cancellation.
        var child = new Child { Hang = true };
        var factory = new Factory();
        factory.Enqueue(child);
        using var cancellation = new CancellationTokenSource();
        var runner = Runner(() => "synthetic.exe", factory, TimeSpan.FromSeconds(10));

        var task = runner.RunAsync(null, cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(child.Killed);
        Assert.True(child.Disposed);
        Assert.Equal(1, factory.Starts);
    }

    [Fact]
    public async Task HangingRunPastDeadline_IsKilledAndDisposed()
    {
        // SPEC P1.3: "Timeout per run … kill-on-deadline". Both runs hang → Timeout (second run's
        // error per "Both fail → error of the second run"); the deadline must kill every child.
        // Decision R1: this needs the timeout retry to actually run (two starts).
        var first = new Child { Hang = true };
        var second = new Child { Hang = true };
        var factory = new Factory();
        factory.Enqueue(first);
        factory.Enqueue(second);
        var runner = Runner(() => "synthetic.exe", factory, TimeSpan.FromMilliseconds(30));

        var error = await Assert.ThrowsAsync<AgentUsageUnavailableException>(() =>
            runner.RunAsync(null, default).WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(AgentUsageErrorKind.Timeout, error.Kind);
        Assert.True(first.Killed);
        Assert.True(second.Killed);
        Assert.True(first.Disposed);
        Assert.True(second.Disposed);
        Assert.Equal(2, factory.Starts);
    }

    [Fact]
    public async Task ProcessStartFailure_ThrowsFailed_AndNeverRetries()
    {
        // Decision R1: "no retry (exactly one start) for … process-start failure (the fake `start`
        // throws Win32Exception)" — "classified Failed but not retried". A non-zero exit also maps to
        // Failed but DOES retry (FirstRunNonZeroExit above), so the runner must distinguish "the
        // process ran and exited non-zero" from "the process could not be started at all": this test
        // stays green only while a start failure never triggers the offline retry, before and after
        // the R1 retry fix.
        var attempts = 0;
        ICcUsageRunner runner = new CcUsageRunner(
            () => @"C:\synthetic\bin\ccusage.exe",
            _ =>
            {
                attempts++;
                throw new Win32Exception(2, "synthetic: the system cannot find the file specified");
            },
            TimeSpan.FromSeconds(5),
            DefaultLimit);

        var error = await Assert.ThrowsAsync<AgentUsageUnavailableException>(() => runner.RunAsync(null, default));

        Assert.Equal(AgentUsageErrorKind.Failed, error.Kind);
        Assert.Equal(1, attempts); // exactly one start — no --offline retry for a start failure
    }

    [Fact]
    public async Task FirstRunTimeoutWithSince_Retries_AndTheSecondStartAppendsOfflineLast()
    {
        // Decision R1: "second success → Pricing = Offline and the second start has --offline last".
        // With since set, the second run's ArgumentList must be
        // [daily, --json, --by-agent, --sections, daily,monthly,session, --since, yyyyMMdd, --offline]
        // — --offline is the final element, after --since, per SPEC P1.3 "run once more with
        // --offline appended". Red at 083120e because the retry never runs (R1).
        var first = new Child { Hang = true };
        var second = new Child();
        var factory = new Factory();
        factory.Enqueue(first);
        factory.Enqueue(second);
        var runner = Runner(() => "synthetic.exe", factory, TimeSpan.FromMilliseconds(30));

        var result = await runner.RunAsync(new DateOnly(2025, 9, 7), default);

        Assert.Equal(AgentUsagePricing.Offline, result.Pricing);
        Assert.Equal(2, factory.Starts);
        Assert.Equal(
            BaseArguments.Append("--since").Append("20250907").ToArray(),
            factory.Infos[0].ArgumentList); // first run: no --offline
        var offlineRun = factory.Infos[1].ArgumentList.ToArray();
        Assert.Equal("--offline", offlineRun[^1]); // --offline is last, after --since yyyyMMdd
        Assert.Equal(
            BaseArguments.Append("--since").Append("20250907").Append("--offline").ToArray(),
            offlineRun);
    }

    [Fact]
    public async Task ExcessiveStdout_AsyncPipeReader_ThrowsOutputTooLarge_NotTimeout_AndNeverRetries()
    {
        // Decision R2: "stdout over the limit is always OutputTooLarge, never Timeout, including with
        // an asynchronous (pipe-like) reader". The fake reader yields on every read, so the limit is
        // hit while the runner is suspended mid-await — exactly the race that misplaces the
        // classification. The StringReader shape above passes today; this shape must pass too.
        // Decision R1: OutputTooLarge is not a retry trigger — exactly one start.
        var first = new Child
        {
            Output = new YieldingTextReader(new string('x', 256), chunkSize: 8),
            Hang = true, // pipe-like: the process cannot exit before its stdout is drained
        };
        var factory = new Factory();
        factory.Enqueue(first);
        factory.Enqueue(new Child()); // must never start — a retry attempt would be caught by Starts
        var runner = Runner(() => "synthetic.exe", factory, TimeSpan.FromSeconds(5), 16);

        var error = await Assert.ThrowsAsync<AgentUsageUnavailableException>(() => runner.RunAsync(null, default));

        Assert.Equal(AgentUsageErrorKind.OutputTooLarge, error.Kind); // R2: never Timeout here
        Assert.Equal(1, factory.Starts); // R1: OutputTooLarge does not retry
        Assert.True(first.Killed);
        Assert.True(first.Disposed);
    }

    [Fact]
    public async Task DeadlineFiredWhileProcessExited_ThrowsTimeout_AndOutputIsNeverParsed()
    {
        // Decision R2: "If the deadline fired, the result is Timeout even when the process has exited
        // in the same instant (partial output is never parsed)." Both fakes "exit" exactly when the
        // deadline token fires, with exit code 0: the first run even has a complete, parseable stdout
        // ready — and the second run is still streaming partial output — so a runner that prefers the
        // exit over the deadline would wrongly return output (or a parsed-looking success) instead of
        // Timeout. Decision R1: the first run's Timeout is a retry trigger, so the second run carries
        // --offline and the second run's Timeout is the error thrown.
        var first = new ExitOnDeadlineChild();
        var second = new ExitOnDeadlineChild { Output = new NeverEndingYieldingReader() };
        var factory = new Factory();
        factory.Enqueue(first);
        factory.Enqueue(second);
        var runner = Runner(() => "synthetic.exe", factory, TimeSpan.FromMilliseconds(30));

        var error = await Assert.ThrowsAsync<AgentUsageUnavailableException>(() =>
            runner.RunAsync(null, default).WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(AgentUsageErrorKind.Timeout, error.Kind); // the deadline decides, not ExitCode 0
        Assert.Equal(2, factory.Starts); // R1: a first-run Timeout retries
        Assert.Equal(BaseArguments.Append("--offline").ToArray(), factory.Infos[1].ArgumentList);
        Assert.True(first.Killed);
        Assert.True(second.Killed);
    }

    /// <summary>
    /// A pipe-like reader that never reaches EOF: every read yields to the thread pool and hands back
    /// a chunk, so the reader is still streaming when the deadline fires — the "exited in the same
    /// instant" partial-output state of decision R2. Honours cancellation like a real pipe reader.
    /// </summary>
    private sealed class NeverEndingYieldingReader : TextReader
    {
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            var take = Math.Min(buffer.Length, 64);
            buffer.Span[..take].Fill('x');
            return take;
        }
    }
}
