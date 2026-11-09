using System.Diagnostics;
using AIMonitor.Domain;
using AIMonitor.Infrastructure.AgentUsage;

namespace AIMonitor.Infrastructure.Tests.AgentUsage;

/// <summary>
/// Pins the runner seam exactly as decision R7 decides it: "Runner seam exactly as M3:
/// <c>CcUsageRunner(Func&lt;string?&gt; resolve, Func&lt;ProcessStartInfo, ICcUsageProcess&gt; start,
/// TimeSpan timeout, int outputLimit)</c> (stderr cap is a 1 MiB constant), and <c>RunAsync</c> is a
/// public (implicit) interface implementation." The handoff grants it: "you may now call RunAsync on
/// a CcUsageRunner directly; use the named argument outputLimit: once."
///
/// Expected red against 083120e: this file does not COMPILE there, because the reviewed runner
/// implements <c>ICcUsageRunner.RunAsync</c> explicitly (not callable on the class) and its test
/// constructor names the limit parameter <c>stdoutLimit</c> with an extra <c>stderrLimit</c>
/// parameter. Both are "Who changes: WO2" per R7; the test turns buildable (and green — the
/// behaviour below already holds) once the seam is renamed and the implementation made public.
/// All other tests in this suite deliberately keep reaching the seam through
/// <see cref="ICcUsageRunner"/> with positional arguments so the rest of the suite stays runnable
/// against the current implementation.
/// </summary>
public sealed class CcUsageRunnerSeamR7Tests
{
    private const string EmptyReportJson = """{"daily":[],"monthly":[],"session":[]}""";

    /// The smallest fake process the seam test needs: exit 0, one report-shaped stdout.
    private sealed class Fake : ICcUsageProcess
    {
        public Task WaitForExitAsync(CancellationToken ct) => Task.CompletedTask;
        public void Kill() { }
        public void Dispose() { }
        public int ExitCode => 0;
        public TextReader Output { get; } = new StringReader(EmptyReportJson);
        public TextReader Error { get; } = new StringReader("");
    }

    [Fact]
    public async Task Seam_RunAsyncIsCalledOnTheConcreteRunner_AndTheConstructorTakesOutputLimit_R7()
    {
        // Decision R7: RunAsync is a public (implicit) interface implementation → callable on a
        // CcUsageRunner-typed variable directly (no ICcUsageRunner cast), and the seam takes the
        // limit as the named 4th constructor parameter `outputLimit` (the 1 MiB stderr cap is an
        // internal constant, not a parameter).
        var starts = new List<ProcessStartInfo>();
        CcUsageRunner runner = new(
            () => @"C:\synthetic\bin\ccusage.exe",
            info =>
            {
                starts.Add(info);
                return new Fake();
            },
            TimeSpan.FromSeconds(5),
            outputLimit: 32 * 1024 * 1024); // named, once, per the handoff — the SPEC P1.3 stdout limit

        CcUsageRunResult result = await runner.RunAsync(null, default);

        Assert.Equal(EmptyReportJson, result.Output);
        Assert.Equal(AgentUsagePricing.OnlineRequested, result.Pricing);
        Assert.Single(starts);
    }
}
