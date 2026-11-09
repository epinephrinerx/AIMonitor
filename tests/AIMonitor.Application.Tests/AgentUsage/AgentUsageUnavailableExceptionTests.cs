using AIMonitor.Application.AgentUsage;

namespace AIMonitor.Application.Tests.AgentUsage;

/// <summary>
/// Tests for <see cref="AgentUsageUnavailableException"/> and <see cref="AgentUsageErrorKind"/>
/// per SPEC R-014 §P1.2, with the constructor argument order fixed by decision M1.
/// Decision M1 also allows "an overload adding <c>Exception inner</c> last"; it is permitted, not
/// required, so no test depends on it.
/// </summary>
public class AgentUsageUnavailableExceptionTests
{
    [Fact]
    public void Ctor_PreservesKindAndMessage()
    {
        // Decision M1: "AgentUsageUnavailableException(AgentUsageErrorKind kind, string message) —
        // kind first, as P1.2 says". The positional order below is therefore part of the contract.
        var error = new AgentUsageUnavailableException(AgentUsageErrorKind.Timeout, "synthetic user-facing message");

        Assert.Equal(AgentUsageErrorKind.Timeout, error.Kind);
        Assert.Equal("synthetic user-facing message", error.Message);
    }

    [Theory]
    [InlineData(AgentUsageErrorKind.NotInstalled)]
    [InlineData(AgentUsageErrorKind.Timeout)]
    [InlineData(AgentUsageErrorKind.OutputTooLarge)]
    [InlineData(AgentUsageErrorKind.Failed)]
    [InlineData(AgentUsageErrorKind.InvalidResponse)]
    public void Ctor_KindIsStoredForEverySpecifiedKind(AgentUsageErrorKind kind)
    {
        // SPEC P1.2 + decision M1: the kind comes first and every one of the five kinds is usable;
        // SPEC P1.2 also requires the message to be user-facing, so it is preserved unchanged.
        var error = new AgentUsageUnavailableException(kind, "synthetic message for the UI");

        Assert.Equal(kind, error.Kind);
        Assert.Equal("synthetic message for the UI", error.Message);
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
