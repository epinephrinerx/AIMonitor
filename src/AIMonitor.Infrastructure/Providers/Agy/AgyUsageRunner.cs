using System.Diagnostics;
using System.Text;

namespace AIMonitor.Infrastructure.Providers.Agy;

/// <summary>Reads the CLI slash command without invoking a shell or a model prompt.</summary>
public sealed class AgyUsageRunner : IAgyUsageRunner
{
    private readonly Func<string?> _resolve;
    private readonly Func<ProcessStartInfo, IAgyUsageProcess> _start;
    private readonly TimeSpan _timeout;
    private readonly int _outputLimit;

    public AgyUsageRunner() : this(ResolveExecutable, info => new AgyUsageProcess(info), TimeSpan.FromSeconds(30)) { }

    internal AgyUsageRunner(Func<string?> resolve, Func<ProcessStartInfo, IAgyUsageProcess> start,
        TimeSpan timeout, int outputLimit = 1_000_000)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        ArgumentNullException.ThrowIfNull(start);
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (outputLimit <= 0) throw new ArgumentOutOfRangeException(nameof(outputLimit));
        _resolve = resolve;
        _start = start;
        _timeout = timeout;
        _outputLimit = outputLimit;
    }

    public async Task<string> ReadUsageAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        IAgyUsageProcess? process = null;
        Task<string>? stdout = null;
        Task<string>? stderr = null;
        try
        {
            var path = _resolve();
            if (string.IsNullOrWhiteSpace(path)) throw new AgyUsageException("AGY CLI was not found. Install AGY and add agy.exe to PATH, then restart AIMonitor.", AgyErrorKind.NotInstalled);
            process = _start(BuildStartInfo(path));
            stdout = ReadPipeAsync(process.Output, _outputLimit, retain: true, deadline);
            stderr = ReadPipeAsync(process.Error, _outputLimit, retain: false, deadline);
            var exited = process.WaitForExitAsync(deadline.Token);
            await Task.WhenAll(stdout, stderr, exited).WaitAsync(deadline.Token).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new AgyUsageException("AGY usage failed. Check your AGY sign-in, then refresh.");
            return await stdout.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AgyUsageException("AGY usage timed out or exceeded its output limit. Check AGY, then refresh.", AgyErrorKind.Timeout);
        }
        catch (OperationCanceledException) { throw; }
        catch (AgyUsageException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                   or System.ComponentModel.Win32Exception or DecoderFallbackException)
        {
            throw new AgyUsageException("Could not read AGY usage. Check that AGY is installed and signed in, then refresh.");
        }
        finally
        {
            deadline.Cancel();
            if (process is not null)
            {
                try { process.Kill(); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
                process.Dispose();
            }
            // Observe faulted reader tasks even if a child kept a pipe open after cancellation.
            foreach (var task in new[] { stdout, stderr })
            {
                if (task is not null) _ = task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }

    internal static ProcessStartInfo BuildStartInfo(string executable)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false, true),
            StandardErrorEncoding = new UTF8Encoding(false, true)
        };
        foreach (var arg in new[] { "-p", "/usage", "--output-format", "json" }) info.ArgumentList.Add(arg);
        return info;
    }

    private static async Task<string> ReadPipeAsync(TextReader reader, int limit, bool retain, CancellationTokenSource deadline)
    {
        var buffer = new char[4096];
        var result = retain ? new StringBuilder() : null;
        long count = 0;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), deadline.Token).ConfigureAwait(false);
            if (read == 0) break;
            count += read;
            if (count > limit)
            {
                deadline.Cancel();
                throw new OperationCanceledException(deadline.Token);
            }
            result?.Append(buffer, 0, read);
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
                if (!Path.IsPathFullyQualified(fullDirectory)) continue;
                var path = Path.Combine(fullDirectory, "agy.exe");
                if (File.Exists(path)) return path;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        return null;
    }
}

internal interface IAgyUsageProcess : IDisposable
{
    TextReader Output { get; }
    TextReader Error { get; }
    int ExitCode { get; }
    Task WaitForExitAsync(CancellationToken cancellationToken);
    void Kill();
}

internal sealed class AgyUsageProcess : IAgyUsageProcess
{
    private readonly Process _process;
    public AgyUsageProcess(ProcessStartInfo info)
    {
        _process = new Process { StartInfo = info };
        try
        {
            if (!_process.Start()) throw new InvalidOperationException("Process did not start.");
            _process.StandardInput.Close();
        }
        catch { _process.Dispose(); throw; }
    }
    public TextReader Output => _process.StandardOutput;
    public TextReader Error => _process.StandardError;
    public int ExitCode => _process.ExitCode;
    public Task WaitForExitAsync(CancellationToken cancellationToken) => _process.WaitForExitAsync(cancellationToken);
    public void Kill() { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
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
