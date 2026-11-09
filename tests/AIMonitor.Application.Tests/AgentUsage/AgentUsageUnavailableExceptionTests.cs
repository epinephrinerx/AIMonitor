using AIMonitor.Application.AgentUsage;

namespace AIMonitor.Application.Tests.AgentUsage;

/// <summary>
/// Tests for <see cref="AgentUsageUnavailableException"/> and <see cref="AgentUsageErrorKind"/>
/// per SPEC R-014 §P1.2.
/// </summary>
public class AgentUsageUnavailableExceptionTests
{
    [Fact]
    public void Ctor_PreservesKindAndMessage()
    {
        // SPEC P1.2: "AgentUsageUnavailableException(AgentUsageErrorKind kind, string message)".
        var error = new AgentUsageUnavailableException(AgentUsageErrorKind.Timeout, "synthetic user-facing message");

        Assert.Equal(AgentUsageErrorKind.Timeout, error.Kind);
        Assert.Equal("synthetic user-facing message", error.Message);
    }

    [Fact]
    public void ErrorKind_HasExactlyTheFiveSpecifiedKinds()
    {
        // SPEC P1.2: kinds are "NotInstalled, Timeout, OutputTooLarge, Failed, InvalidResponse" — no others.
        // Enum.GetValues ordering is not guaranteed, so compare ordinal-sorted names:
        // Failed < InvalidResponse < NotInstalled < OutputTooLarge < Timeout.
        var values = Enum.GetValues<AgentUsageErrorKind>().OrderBy(v => v.ToString(), StringComparer.Ordinal).ToArray();

        Assert.Equal(
            new[]
            {
                AgentUsageErrorKind.Failed,
                AgentUsageErrorKind.InvalidResponse,
                AgentUsageErrorKind.NotInstalled,
                AgentUsageErrorKind.OutputTooLarge,
                AgentUsageErrorKind.Timeout,
            },
            values);
    }
}
