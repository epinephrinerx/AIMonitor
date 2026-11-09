using System.Diagnostics;
using System.Globalization;
using System.Text;
using AIMonitor.Application.AgentUsage;
using AIMonitor.Domain;

namespace AIMonitor.Infrastructure.AgentUsage;

/// <summary>
/// Runs the ccusage CLI with bounded output and a per-run deadline.
/// </summary>
public sealed class CcUsageRunner : ICcUsageRunner
{
    internal const int DefaultOutputLimit = 32 * 1024 * 1024;
    internal const int StderrLimit = 1 * 1024 * 1024;

    private readonly Func<string?> _resolve;
    private readonly Func<ProcessStartInfo, ICcUsageProcess> _start;
    private readonly TimeSpan _timeout;
    private readonly int _outputLimit;

    public CcUsageRunner()
        : this(ResolveExecutable, info => new CcUsageProcess(info), TimeSpan.FromSeconds(120), DefaultOutputLimit)
    {
    }

    internal CcUsageRunner(
        Func<string?> resolve,
        Func<ProcessStartInfo, ICcUsageProcess> start,
        TimeSpan timeout,
        int outputLimit = DefaultOutputLimit)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        ArgumentNullException.ThrowIfNull(start);
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (outputLimit <= 0) throw new ArgumentOutOfRangeException(nameof(outputLimit));

        _resolve = resolve;
        _start = start;
        _timeout = timeout;
        _outputLimit = outputLimit;
    }

    public async Task<CcUsageRunResult> RunAsync(DateOnly? since, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var first = await RunOnceAsync(since, offline: false, cancellationToken).ConfigureAwait(false);
        if (first.IsSuccess)
            return new CcUsageRunResult(first.Output!, AgentUsagePricing.OnlineRequested);

        // Retry with --offline only on timeout or non-zero exit (R1).
        if (first.IsRetryable)
        {
            var second = await RunOnceAsync(since, offline: true, cancellationToken).ConfigureAwait(false);
            if (second.IsSuccess)
                return new CcUsageRunResult(second.Output!, AgentUsagePricing.Offline);

            throw second.Error!;
        }

        throw first.Error!;
    }

    private async Task<CcUsageRunOnceResult> RunOnceAsync(DateOnly? since, bool offline, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        ICcUsageProcess? process = null;
        Task<string>? stdout = null;
        Task<string>? stderr = null;

        try
        {
            var path = _resolve();
            if (string.IsNullOrWhiteSpace(path))
            {
                return CcUsageRunOnceResult.Failure(
                    new AgentUsageUnavailableException(
                        AgentUsageErrorKind.NotInstalled,
                        "ccusage was not found. Install it with `npm i -g ccusage`, then restart AIMonitor."),
                    isRetryable: false);
            }

            try
            {
                process = _start(BuildStartInfo(path, since, offline));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception or DecoderFallbackException)
            {
                return CcUsageRunOnceResult.Failure(
                    new AgentUsageUnavailableException(
                        AgentUsageErrorKind.Failed,
                        "Could not run ccusage. Check that ccusage is installed, then refresh.",
                        ex),
                    isRetryable: false);
            }

            stdout = ReadPipeAsync(process.Output, _outputLimit, retain: true, deadline);
            stderr = ReadPipeAsync(process.Error, StderrLimit, retain: false, deadline);
            var exited = process.WaitForExitAsync(deadline.Token);

            await Task.WhenAll(stdout, stderr, exited).WaitAsync(deadline.Token).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                return CcUsageRunOnceResult.Failure(
                    new AgentUsageUnavailableException(
                        AgentUsageErrorKind.Failed,
                        "ccusage could not read usage. Run `ccusage daily` in a terminal to see why."),
                    isRetryable: true);
            }

            return CcUsageRunOnceResult.Success(await stdout.ConfigureAwait(false));
        }
        catch (CcUsageOutputLimitExceededException)
        {
            return CcUsageRunOnceResult.Failure(
                new AgentUsageUnavailableException(
                    AgentUsageErrorKind.OutputTooLarge,
                    "ccusage output exceeded the maximum allowed size."),
                isRetryable: false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CcUsageRunOnceResult.Failure(
                new AgentUsageUnavailableException(
                    AgentUsageErrorKind.Timeout,
                    "ccusage did not respond in time. Check ccusage, then refresh."),
                isRetryable: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        finally
        {
            deadline.Cancel();
            if (process is not null)
            {
                try { process.Kill(); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }

                try
                {
                    await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception) { }

                process.Dispose();
            }

            foreach (var task in new[] { stdout, stderr })
            {
                if (task is not null)
                {
                    _ = task.ContinueWith(
                        t => { _ = t.Exception; },
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
            }
        }
    }

    internal static ProcessStartInfo BuildStartInfo(string executable, DateOnly? since, bool offline)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false, true),
            StandardErrorEncoding = new UTF8Encoding(false, true),
        };

        info.ArgumentList.Add("daily");
        info.ArgumentList.Add("--json");
        info.ArgumentList.Add("--by-agent");
        info.ArgumentList.Add("--sections");
        info.ArgumentList.Add("daily,monthly,session");

        if (since.HasValue)
        {
            info.ArgumentList.Add("--since");
            info.ArgumentList.Add(since.Value.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        }

        if (offline) info.ArgumentList.Add("--offline");

        info.Environment["NO_COLOR"] = "1";
        return info;
    }

    private static async Task<string> ReadPipeAsync(TextReader reader, int limit, bool retain, CancellationTokenSource deadline)
    {
        var buffer = new char[4096];
        var result = retain ? new StringBuilder() : null;
        long count = 0;
        bool limitExceeded = false;

        while (true)
        {
            int read;
            try
            {
                read = await reader.ReadAsync(buffer.AsMemory(), deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (read == 0) break;

            if (limitExceeded)
            {
                continue;
            }

            count += read;
            if (count > limit)
            {
                if (retain)
                {
                    // If the deadline already fired, the result is Timeout, not OutputTooLarge (R2).
                    if (deadline.Token.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(deadline.Token);
                    }

                    throw new CcUsageOutputLimitExceededException();
                }

                limitExceeded = true;
            }
            else
            {
                result?.Append(buffer, 0, read);
            }
        }

        return result?.ToString() ?? "";
    }

    private static string? ResolveExecutable()
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var fullDirectory = directory.Trim().Trim('"');
                if (string.IsNullOrEmpty(fullDirectory)) continue;
                if (!Path.IsPathRooted(fullDirectory)) continue;
                if (IsDriveRelative(fullDirectory)) continue;

                var exePath = Path.Combine(fullDirectory, "ccusage.exe");
                if (File.Exists(exePath)) return exePath;

                var cmdPath = Path.Combine(fullDirectory, "ccusage.cmd");
                if (File.Exists(cmdPath)) return cmdPath;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        }

        return null;
    }

    private static bool IsDriveRelative(string path)
    {
        // Drive-relative: "C:" or "C:foo", not "C:\foo".
        if (path.Length >= 2 && path[1] == ':' && char.IsLetter(path[0]))
        {
            return path.Length == 2 || path[2] != Path.DirectorySeparatorChar && path[2] != Path.AltDirectorySeparatorChar;
        }

        return false;
    }

    private sealed class CcUsageOutputLimitExceededException : Exception
    {
    }
}

public sealed record CcUsageRunResult(string Output, AgentUsagePricing Pricing);

internal interface ICcUsageRunner
{
    Task<CcUsageRunResult> RunAsync(DateOnly? since, CancellationToken cancellationToken);
}

internal interface ICcUsageProcess : IDisposable
{
    TextReader Output { get; }
    TextReader Error { get; }
    int ExitCode { get; }
    Task WaitForExitAsync(CancellationToken cancellationToken);
    void Kill();
}

internal sealed class CcUsageProcess : ICcUsageProcess
{
    private readonly Process _process;

    public CcUsageProcess(ProcessStartInfo info)
    {
        _process = new Process { StartInfo = info };
        try
        {
            if (!_process.Start()) throw new InvalidOperationException("Process did not start.");
            _process.StandardInput.Close();
        }
        catch
        {
            _process.Dispose();
            throw;
        }
    }

    public TextReader Output => _process.StandardOutput;
    public TextReader Error => _process.StandardError;
    public int ExitCode => _process.ExitCode;
    public Task WaitForExitAsync(CancellationToken cancellationToken) => _process.WaitForExitAsync(cancellationToken);

    public void Kill()
    {
        if (!_process.HasExited) _process.Kill(entireProcessTree: true);
    }

    public void Dispose()
    {
        try { _process.StandardOutput.Dispose(); }
        finally
        {
            try { _process.StandardError.Dispose(); }
            finally { _process.Dispose(); }
        }
    }
}

internal sealed record CcUsageRunOnceResult(bool IsSuccess, string? Output, AgentUsageUnavailableException? Error, bool IsRetryable)
{
    public static CcUsageRunOnceResult Success(string output) => new(true, output, null, false);

    public static CcUsageRunOnceResult Failure(AgentUsageUnavailableException error, bool isRetryable)
        => new(false, null, error, isRetryable);
}
