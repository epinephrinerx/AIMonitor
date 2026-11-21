namespace AIMonitor.Infrastructure.Tests;

/// <summary>
/// Marks a test that can only run on Linux as a non-root user, because it relies on file
/// permissions that root bypasses (a read-only folder, a mode-000 file). Skipped, not silently
/// passed, anywhere else.
/// </summary>
public sealed class LinuxNonRootOnlyFactAttribute : FactAttribute
{
    public LinuxNonRootOnlyFactAttribute(string because)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(because);
        if (!OperatingSystem.IsLinux())
        {
            Skip = $"Linux-only: {because}";
        }
        else if (Environment.IsPrivilegedProcess)
        {
            Skip = $"Linux non-root only: {because}";
        }
    }
}
