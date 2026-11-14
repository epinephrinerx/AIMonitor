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
    private readonly Func<string, int, CancellationToken, Task<byte[]?>> _readFile;
    private readonly Func<ProcessStartInfo, IOpenCodeProcess> _start;

    public OpenCodeAuthReader() : this(ResolveExecutable, Environment.GetEnvironmentVariable,
        ReadFileAsync, info => new OpenCodeProcess(info), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20))
    { }

    internal OpenCodeAuthReader(Func<string?> path, Func<string, string?> environment,
        Func<string, int, CancellationToken, Task<byte[]?>> readFile, Func<ProcessStartInfo, IOpenCodeProcess> start,
        TimeSpan versionDeadline, TimeSpan authListDeadline)
    {
        _path = path; _environment = environment; _readFile = readFile; _start = start;
        VersionDeadline = versionDeadline; AuthListDeadline = authListDeadline;
    }

    internal TimeSpan VersionDeadline { get; }
    internal TimeSpan AuthListDeadline { get; }

    public async Task<OpenCodeAuthStatus> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? executable;
        try { executable = _path(); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { executable = null; }
        if (executable is null) return new(false, "", [], false, "");

        var version = await RunAsync(executable, ["--version"], VersionDeadline, 4096, cancellationToken).ConfigureAwait(false);
        var major = version.Success ? ParseMajor(version.Output) : null;
        if (major is >= 2)
        {
            var auth = await RunAsync(executable, ["auth", "list", "--format", "json"], AuthListDeadline, 256 * 1024, cancellationToken).ConfigureAwait(false);
            if (auth.Success)
            {
                try { return new(true, "opencode auth list", ParseConnections(auth.Output), false, ""); }
                catch { /* malformed command output falls back to auth.json */ }
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
            var root = !string.IsNullOrWhiteSpace(xdg) && Path.IsPathRooted(xdg) && !IsDriveRelative(xdg)
                ? xdg : GetProfileRoot();
            if (root is null) return Failed();
            var file = Path.Combine(root, "opencode", "auth.json");
            var bytes = await _readFile(file, AuthFileLimit + 1, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (bytes is null)
                return !outdated ? new(true, "auth.json", [], false, "") : Failed();
            if (bytes.Length > AuthFileLimit) return Failed();
            var offset = bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
            using var doc = JsonDocument.Parse(bytes.AsMemory(offset));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return Failed();
            var rows = new List<OpenCodeSignIn>();
            foreach (var item in doc.RootElement.EnumerateObject())
            {
                var type = item.Value.ValueKind == JsonValueKind.Object && item.Value.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                rows.Add(new(SafeName(item.Name), KindForType(type)));
            }
            return new(true, "auth.json", Deduplicate(rows), outdated, "");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return Failed(); }
    }

    private string? GetProfileRoot()
    {
        var profile = _environment("USERPROFILE");
        return !string.IsNullOrWhiteSpace(profile) && Path.IsPathRooted(profile) && !IsDriveRelative(profile)
            ? Path.Combine(profile, ".local", "share") : null;
    }

    private async Task<(bool Success, string Output)> RunAsync(string executable, string[] args, TimeSpan duration, int limit, CancellationToken ct)
    {
        IOpenCodeProcess? process = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(duration);
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

            // Observe stdout immediately. In particular, an over-cap stream kills the process
            // without waiting for exit or stderr, both of which may otherwise remain open.
            var first = await Task.WhenAny(stdout, exited).WaitAsync(deadline.Token).ConfigureAwait(false);
            if (first == stdout)
            {
                var output = await stdout.ConfigureAwait(false);
                await exited.WaitAsync(deadline.Token).ConfigureAwait(false);
                await stderr.WaitAsync(deadline.Token).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                return process.ExitCode == 0 ? (true, output) : (false, "");
            }
            await exited.ConfigureAwait(false);
            await stdout.WaitAsync(deadline.Token).ConfigureAwait(false);
            await stderr.WaitAsync(deadline.Token).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return process.ExitCode == 0 ? (true, stdout.Result) : (false, "");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return (false, ""); }
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
        var buffer = new char[2048]; var output = new StringBuilder(); var bytes = 0;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (count == 0) return output.ToString();
            bytes += Encoding.UTF8.GetByteCount(buffer.AsSpan(0, count));
            if (bytes > limit) throw new OutputLimitException();
            output.Append(buffer, 0, count);
        }
    }

    private static async Task DrainAsync(TextReader reader, int limit, CancellationToken ct)
    { var buffer = new char[2048]; var total = 0; while (true) { var count = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false); if (count == 0) return; total = Math.Min(limit, total + count); } }

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
            if (!provider.TryGetProperty("connections", out var connections)) continue;
            if (connections.ValueKind != JsonValueKind.Array) throw new InvalidDataException();
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
        return Deduplicate(rows);
    }

    private static List<OpenCodeSignIn> Deduplicate(IEnumerable<OpenCodeSignIn> rows)
    {
        var seen = new HashSet<(string Name, string Kind)>();
        return rows.Where(row => seen.Add((row.Name, row.Kind))).ToList();
    }
    private static string KindForType(string? type) => type switch { "oauth" => "OAuth", "api" => "API key", "wellknown" => "Well-known", _ => "Other" };
    private static string SafeName(string? value)
    {
        var name = value?.Trim();
        return name is not null && !name.Contains("://", StringComparison.Ordinal) && !name.Contains('@')
            && Regex.IsMatch(name, @"^[A-Za-z0-9][A-Za-z0-9 ._()+/@:-]{0,47}$") ? name : "Unnamed provider";
    }
    private static int? ParseMajor(string output) { var match = Regex.Match(output, @"\d+"); return match.Success && int.TryParse(match.Value, out var value) ? value : null; }
    private static OpenCodeAuthStatus Failed() => new(true, "", [], false, ErrorText);
    internal static async Task<byte[]?> ReadFileAsync(string path, int maxBytes, CancellationToken ct)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[maxBytes]; var total = 0;
            while (total < maxBytes)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total, maxBytes - total), ct).ConfigureAwait(false);
                if (read == 0) break;
                total += read;
            }
            return buffer.AsSpan(0, total).ToArray();
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
    private static string? ResolveExecutable() => ResolveExecutable(Environment.GetEnvironmentVariable("PATH"), File.Exists);
    internal static string? ResolveExecutable(string? searchPath, Func<string, bool> fileExists)
    {
        foreach (var entry in (searchPath ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        { var dir = entry.Trim().Trim('"'); if (!Path.IsPathRooted(dir) || IsDriveRelative(dir)) continue; foreach (var ext in new[] { "exe", "cmd" }) { var path = Path.Combine(dir, "opencode." + ext); if (fileExists(path)) return path; } }
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
