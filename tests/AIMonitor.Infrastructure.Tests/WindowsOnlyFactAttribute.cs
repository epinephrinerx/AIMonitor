namespace AIMonitor.Infrastructure.Tests;

/// <summary>
/// Marks a test that can only run on Windows, so a run on another operating system reports it as
/// skipped instead of executing it. Preferred over an early <c>return</c> guard, which reports the
/// test as passed and hides from the run summary how much of the suite never executed.
/// </summary>
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute(string because)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(because);
        if (!OperatingSystem.IsWindows())
        {
            Skip = $"Windows-only: {because}";
        }
    }
}
