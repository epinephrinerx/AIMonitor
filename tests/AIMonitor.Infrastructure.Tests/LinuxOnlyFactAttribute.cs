namespace AIMonitor.Infrastructure.Tests;

/// <summary>
/// Marks a test that can only run on Linux, so a run on another operating system reports it as
/// skipped instead of executing it. The counterpart of <see cref="MacOSOnlyFactAttribute"/>, and
/// skipped for the same reason: a silent early <c>return</c> would report the test as passed.
/// </summary>
public sealed class LinuxOnlyFactAttribute : FactAttribute
{
    public LinuxOnlyFactAttribute(string because)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(because);
        if (!OperatingSystem.IsLinux())
        {
            Skip = $"Linux-only: {because}";
        }
    }
}
