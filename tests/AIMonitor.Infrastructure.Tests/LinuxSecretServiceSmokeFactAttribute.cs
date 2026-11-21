namespace AIMonitor.Infrastructure.Tests;

/// <summary>
/// Marks a test that talks to the real Secret Service on a real Linux session. It is skipped
/// unless the run opts in with <c>AIM_SECRET_SERVICE_SMOKE=1</c>, because it reads and writes the
/// login keyring. <see cref="RequiredEnvironment"/> narrows it further to one manual step.
/// </summary>
public sealed class LinuxSecretServiceSmokeFactAttribute : FactAttribute
{
    public const string OptInVariable = "AIM_SECRET_SERVICE_SMOKE";

    private readonly string _because;
    private string? _explicitSkip;

    public LinuxSecretServiceSmokeFactAttribute(string because)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(because);
        _because = because;
    }

    /// <summary>
    /// Either "NAME" (the variable must be set and non-empty) or "NAME=VALUE" (it must equal VALUE
    /// exactly). Checked after the operating system and the opt-in.
    /// </summary>
    public string? RequiredEnvironment { get; init; }

    public override string? Skip
    {
        get => _explicitSkip ?? ComputeSkip();
        set => _explicitSkip = value;
    }

    private string? ComputeSkip()
    {
        if (!OperatingSystem.IsLinux())
        {
            return $"Linux-only: {_because}";
        }

        if (!string.Equals(Environment.GetEnvironmentVariable(OptInVariable), "1", StringComparison.Ordinal))
        {
            return $"Secret Service smoke: {_because}";
        }

        if (RequiredEnvironment is { } required && !RequirementMet(required))
        {
            return $"Secret Service smoke: {_because}";
        }

        return null;
    }

    private static bool RequirementMet(string required)
    {
        var separator = required.IndexOf('=', StringComparison.Ordinal);
        if (separator < 0)
        {
            return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(required));
        }

        var name = required[..separator];
        var expected = required[(separator + 1)..];
        return string.Equals(Environment.GetEnvironmentVariable(name), expected, StringComparison.Ordinal);
    }
}
