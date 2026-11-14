namespace AIMonitor.Infrastructure.Tests;

/// <summary>
/// Marks a test that can only run on macOS, so a run on another operating system reports it as
/// skipped instead of executing it. The counterpart of <see cref="WindowsOnlyFactAttribute"/>, and
/// skipped for the same reason: a silent early <c>return</c> would report the test as passed.
/// </summary>
public sealed class MacOSOnlyFactAttribute : FactAttribute
{
    public MacOSOnlyFactAttribute(string because)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(because);
        if (!OperatingSystem.IsMacOS())
        {
            Skip = $"macOS-only: {because}";
        }
    }
}
