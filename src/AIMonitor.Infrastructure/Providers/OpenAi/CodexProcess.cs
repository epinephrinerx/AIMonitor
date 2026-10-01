using System.Diagnostics;
using System.Text;

namespace AIMonitor.Infrastructure.Providers.OpenAi;

/// <summary>
/// Real <see cref="ICodexProcess"/>, launching <c>codex app-server --listen stdio://</c> with
/// <see cref="ProcessStartInfo.ArgumentList"/> (never a hand-built command-line string, so nothing
/// about the working directory or environment can be misparsed as extra arguments). No window is
/// created. Production only - tests substitute a hand-written fake instead of a real subprocess.
/// </summary>
internal sealed class CodexProcess : ICodexProcess
{
    private readonly Process _process;
    private bool _disposed;

    public CodexProcess(CodexProcessStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var startInfo = new ProcessStartInfo(request.ExecutablePath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = request.WorkingDirectory,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--listen");
        startInfo.ArgumentList.Add("stdio://");

        startInfo.Environment.Clear();
        foreach (var (key, value) in request.Environment)
        {
            if (value is not null)
            {
                startInfo.Environment[key] = value;
            }
        }

        _process = new Process { StartInfo = startInfo };
        try
        {
            _process.Start();
        }
        catch
        {
            _process.Dispose();
            throw;
        }

        // Stderr is redirected above (so it never inherits this app's console) but nothing ever reads
        // its content - without continuously draining it, the OS pipe's bounded buffer would fill and
        // the child would block the moment it writes enough diagnostic output, hanging the session.
        // Backgrounded via Task.Run, matching CodexJsonRpcClient's stdout reader loop, so this never
        // does any of its own reading synchronously on the constructor's calling thread. Deliberately
        // not observed any further: DiscardAsync never lets an exception escape it.
        _ = Task.Run(() => TextReaderDrain.DiscardAsync(_process.StandardError));
    }

    public TextWriter StandardInput => _process.StandardInput;

    public TextReader StandardOutput => _process.StandardOutput;

    public bool HasExited => _process.HasExited;

    public void Kill()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Exited between the check above and the call - nothing left to kill.
        }
    }

    public async Task<bool> WaitForExitAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return _process.HasExited;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Explicit, individually-guarded closes so a problem with one stream never skips the others
        // or the process handle itself - belt-and-braces alongside Process.Dispose(), which would
        // otherwise close these implicitly.
        CloseQuietly(_process.StandardInput);
        CloseQuietly(_process.StandardOutput);
        CloseQuietly(_process.StandardError);
        _process.Dispose();
    }

    private static void CloseQuietly(IDisposable stream)
    {
        try
        {
            stream.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }
}
