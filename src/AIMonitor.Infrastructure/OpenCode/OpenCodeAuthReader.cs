using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIMonitor.Application.OpenCode;

namespace AIMonitor.Infrastructure.OpenCode;

public sealed class OpenCodeAuthReader : IOpenCodeAuthReader
{
    private const string ErrorText = "Could not read OpenCode sign-ins. Run `opencode auth list` in a terminal to check.";
    private const int AuthFileLimit = 1024 * 1024;
    private readonly Func<string?> _path;
    private readonly Func<string, string?> _environment;
    private readonly Func<string, long> _fileLength;
    private readonly Func<string, CancellationToken, Task<byte[]>> _readFile;
    private readonly Func<ProcessStartInfo, IOpenCodeProcess> _start;

    public OpenCodeAuthReader() : this(ResolveExecutable, Environment.GetEnvironmentVariable,
        FileLength, File.ReadAllBytesAsync, info => new OpenCodeProcess(info))
    { }

    internal OpenCodeAuthReader(Func<string?> path, Func<string, string?> environment,
        Func<string, long> fileLength, Func<string, CancellationToken, Task<byte[]>> readFile,
        Func<ProcessStartInfo, IOpenCodeProcess> start)
    {
        _path = path; _environment = environment; _fileLength = fileLength; _readFile = readFile; _start = start;
    }

    public async Task<OpenCodeAuthStatus> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? executable;
        try { executable = _path(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { executable = null; }
        if (executable is null) return new(false, "", [], false, "");

        var version = await RunAsync(executable, ["--version"], 10, 4096, cancellationToken).ConfigureAwait(false);
        var major = version.Success ? ParseMajor(version.Output) : null;
        if (major is >= 2)
        {
            var auth = await RunAsync(executable, ["auth", "list", "--format", "json"], 20, 256 * 1024, cancellationToken).ConfigureAwait(false);
            if (auth.Success)
            {
                try { return new(true, "opencode auth list", ParseConnections(auth.Output), false, ""); }
                catch (Exception ex) when (ex is JsonException or InvalidDataException or FormatException) { }
            }
            return await ReadAuthFileAsync(outdated: true, cancellationToken).ConfigureAwait(false);
        }
        return await ReadAuthFileAsync(outdated: major != 1, cancellationToken).ConfigureAwait(false);
    }

    private async Task<OpenCodeAuthStatus> ReadAuthFileAsync(bool outdated, CancellationToken ct)
    {
        try
        {
            var xdg = _environment("XDG_DATA_HOME");
            var root = !string.IsNullOrWhiteSpace(xdg) && Path.IsPathRooted(xdg)
                ? xdg : Path.Combine(_environment("USERPROFILE") ?? "", ".local", "share");
            var file = Path.Combine(root, "opencode", "auth.json");
            var length = _fileLength(file);
            if (length < 0 && !outdated) return new(true, "auth.json", [], false, "");
            if (length < 0 || length > AuthFileLimit) return Failed();
            var bytes = await _readFile(file, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (bytes.Length > AuthFileLimit) return Failed();
            using var doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return Failed();
            var rows = new List<OpenCodeSignIn>();
            foreach (var item in doc.RootElement.EnumerateObject())
            {
                var name = SafeName(item.Name);
                var type = item.Value.ValueKind == JsonValueKind.Object && item.Value.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                rows.Add(new(name, type switch { "oauth" => "OAuth", "api" => "API key", "wellknown" => "Well-known", _ => "Other" }));
            }
            return new(true, "auth.json", rows, outdated, "");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        { return Failed(); }
    }

    private async Task<(bool Success, string Output)> RunAsync(string executable, string[] args, int seconds, int limit, CancellationToken ct)
    {
        IOpenCodeProcess? process = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
        try
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
            foreach (var arg in args) info.ArgumentList.Add(arg);
            info.Environment["NO_COLOR"] = "1";
            process = _start(info);
            var stdout = ReadBoundedAsync(process.Output, limit, deadline.Token);
            var stderr = DrainAsync(process.Error, 1024 * 1024, deadline.Token);
            var exited = process.WaitForExitAsync(deadline.Token);
            var all = Task.WhenAll(stdout, stderr, exited);
            await Task.WhenAny(all, stdout, stderr).ConfigureAwait(false);
            if (stdout.IsFaulted) await stdout.ConfigureAwait(false);
            if (stderr.IsFaulted) await stderr.ConfigureAwait(false);
            await all.WaitAsync(deadline.Token).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return process.ExitCode == 0 ? (true, stdout.Result) : (false, "");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (false, ""); }
        catch (Exception)
        { return (false, ""); }
        finally
        {
            deadline.Cancel();
            if (process is not null)
            {
                try { process.Kill(); } catch { }
                try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
                try { process.Dispose(); } catch { }
            }
        }
    }

    private static async Task<string> ReadBoundedAsync(TextReader reader, int limit, CancellationToken ct)
    {
        var b = new char[2048]; var sb = new StringBuilder(); var bytes = 0;
        while (true) { var n = await reader.ReadAsync(b.AsMemory(), ct).ConfigureAwait(false); if (n == 0) return sb.ToString(); bytes += Encoding.UTF8.GetByteCount(b.AsSpan(0, n)); if (bytes > limit) throw new OutputLimitException(); sb.Append(b, 0, n); }
    }
    private static async Task DrainAsync(TextReader reader, int limit, CancellationToken ct)
    { var b = new char[2048]; var total = 0; while (true) { var n = await reader.ReadAsync(b.AsMemory(), ct).ConfigureAwait(false); if (n == 0) return; total = Math.Min(limit, total + n); } }
    private static List<OpenCodeSignIn> ParseConnections(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException();
        var rows = new List<OpenCodeSignIn>();
        foreach (var provider in doc.RootElement.EnumerateArray())
        {
            if (provider.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
            var name = provider.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name)) name = provider.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
            if (name is null) throw new InvalidDataException();
            if (!provider.TryGetProperty("connections", out var connections) || connections.ValueKind != JsonValueKind.Array) continue;
            foreach (var connection in connections.EnumerateArray())
            {
                if (connection.ValueKind != JsonValueKind.Object || !connection.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) throw new InvalidDataException();
                var kind = type.GetString() switch
                {
                    "env" => "Environment",
                    "credential" when connection.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String => method.GetString() switch { "oauth" => "OAuth", "key" => "API key", "external" => "External", _ => "Other" },
                    _ => "Other"
                };
                if (connection.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Object && status.TryGetProperty("status", out var state) && state.ValueKind == JsonValueKind.String && state.GetString() == "needs_auth") kind += " (needs sign-in)";
                rows.Add(new(SafeName(name), kind));
            }
        }
        return rows;
    }
    private static string SafeName(string? value) => value is not null && Regex.IsMatch(value.Trim(), @"^[A-Za-z0-9][A-Za-z0-9 ._()+/@:-]{0,47}$") ? value.Trim() : "Unnamed provider";
    private static int? ParseMajor(string output) { var m = Regex.Match(output, @"\d+"); return m.Success && int.TryParse(m.Value, out var value) ? value : null; }
    private static OpenCodeAuthStatus Failed() => new(true, "", [], false, ErrorText);
    private static long FileLength(string path) { try { return new FileInfo(path).Length; } catch (FileNotFoundException) { return -1; } catch (DirectoryNotFoundException) { return -1; } }
    private static string? ResolveExecutable() => ResolveExecutable(Environment.GetEnvironmentVariable("PATH"), File.Exists);
    internal static string? ResolveExecutable(string? searchPath, Func<string, bool> fileExists)
    {
        foreach (var entry in (searchPath ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        { var dir = entry.Trim().Trim('"'); if (!Path.IsPathRooted(dir) || IsDriveRelative(dir)) continue; foreach (var ext in new[] { "exe", "cmd" }) { var p = Path.Combine(dir, "opencode." + ext); if (fileExists(p)) return p; } }
        return null;
    }
    private static bool IsDriveRelative(string path) => path.Length >= 2 && path[1] == ':' && char.IsLetter(path[0])
        && (path.Length == 2 || path[2] != Path.DirectorySeparatorChar && path[2] != Path.AltDirectorySeparatorChar);
    private sealed class OutputLimitException : Exception;
}

internal interface IOpenCodeProcess : IDisposable { TextReader Output { get; } TextReader Error { get; } int ExitCode { get; } Task WaitForExitAsync(CancellationToken ct); void Kill(); }
internal sealed class OpenCodeProcess : IOpenCodeProcess
{
    private readonly Process _process;
    public OpenCodeProcess(ProcessStartInfo info) { _process = new Process { StartInfo = info }; if (!_process.Start()) throw new InvalidOperationException(); _process.StandardInput.Close(); }
    public TextReader Output => _process.StandardOutput; public TextReader Error => _process.StandardError; public int ExitCode => _process.ExitCode;
    public Task WaitForExitAsync(CancellationToken ct) => _process.WaitForExitAsync(ct);
    public void Kill() { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
    public void Dispose() { _process.Dispose(); }
}
