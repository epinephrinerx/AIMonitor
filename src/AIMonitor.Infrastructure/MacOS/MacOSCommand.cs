using System.Diagnostics;
using System.Text;

namespace AIMonitor.Infrastructure.MacOS;

/// <summary>The outcome of one command-line tool invocation.</summary>
internal readonly record struct CommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Runs the macOS command-line tools the adapters in this namespace depend on
/// (<c>/usr/bin/security</c>, <c>/bin/launchctl</c>).
///
/// Adapters take the runner as a delegate so their tests assert on the invocation they build
/// without starting a process; this is the one place that actually starts one.
/// </summary>
internal static class MacOSCommand
{
    /// <summary>Enough for a command that answers by itself; a call that may put a dialog in
    /// front of the user passes its own.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    /// <summary>A redirected, non-shell invocation of <paramref name="fileName"/>.</summary>
    public static ProcessStartInfo StartInfo(string fileName, params string[] arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        var info = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false, true),
            StandardErrorEncoding = new UTF8Encoding(false, true),
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return info;
    }

    /// <summary>Runs one invocation, optionally feeding <paramref name="standardInput"/> to it.</summary>
    public static CommandResult Run(ProcessStartInfo startInfo, string? standardInput) =>
        Run(startInfo, standardInput, DefaultTimeout);

    /// <summary>As above, with a deadline of its own.</summary>
    public static CommandResult Run(ProcessStartInfo startInfo, string? standardInput, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException($"'{startInfo.FileName}' is a macOS tool.");
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {startInfo.FileName}.");

        // Standard error is drained through the event API rather than a second blocking read: two
        // ReadToEnd calls on one process deadlock as soon as either pipe's buffer fills.
        var standardError = new StringBuilder();
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null) standardError.AppendLine(args.Data);
        };
        process.BeginErrorReadLine();

        if (standardInput is not null)
        {
            process.StandardInput.Write(standardInput);
        }

        process.StandardInput.Close();
        var standardOutput = process.StandardOutput.ReadToEnd();

        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It exited between the timeout and the kill; nothing left to stop.
            }

            throw new TimeoutException(
                $"{startInfo.FileName} did not finish within {timeout.TotalSeconds:0} seconds.");
        }

        return new CommandResult(process.ExitCode, standardOutput, standardError.ToString());
    }
}
