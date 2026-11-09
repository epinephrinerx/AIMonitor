using AIMonitor.Application.AgentUsage;
using AIMonitor.Application.Time;
using AIMonitor.Domain;

namespace AIMonitor.Infrastructure.AgentUsage;

/// <summary>
/// Adapter that runs ccusage and parses its JSON output into an <see cref="AgentUsageReport"/>.
/// </summary>
public sealed class CcUsageAgentUsageSource : IAgentUsageSource
{
    private readonly ICcUsageRunner _runner;
    private readonly IClock _clock;

    public CcUsageAgentUsageSource(IClock clock)
        : this(clock, new CcUsageRunner())
    {
    }

    internal CcUsageAgentUsageSource(IClock clock, ICcUsageRunner runner)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    public async Task<AgentUsageReport> ReadAsync(DateOnly? since, CancellationToken ct)
    {
        var result = await _runner.RunAsync(since, ct).ConfigureAwait(false);
        return CcUsageJsonParser.Parse(result.Output, result.Pricing, _clock.UtcNow);
    }
}
