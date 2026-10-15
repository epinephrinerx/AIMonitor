using System.Diagnostics;
using System.Text;
using AIMonitor.Infrastructure.Providers.OpenAi;

namespace AIMonitor.Infrastructure.Tests.Providers.OpenAi;

[Trait("Category", "Contract")]
public sealed class CodexProcessTests : IDisposable
{
    private readonly string _tempDirectory =
        Directory.CreateTempSubdirectory("aimonitor-codex-process-tests-").FullName;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void CreateStartInfo_StandardInputEncoding_DoesNotEmitBom()
    {
        // Encoding.UTF8 emits BOM by default in .NET (0xEF, 0xBB, 0xBF).
        // CodexProcess must configure a BOM-less UTF8Encoding so the first JSON-RPC message is not broken.
        Assert.Equal(3, Encoding.UTF8.GetPreamble().Length);

        var request = new CodexProcessStartRequest(
            ExecutablePath: "dummy.exe",
            WorkingDirectory: _tempDirectory,
            Environment: new Dictionary<string, string?>());

        var startInfo = CodexProcess.CreateStartInfo(request);

        Assert.NotNull(startInfo.StandardInputEncoding);
        Assert.Empty(startInfo.StandardInputEncoding.GetPreamble());
    }

    [Fact]
    public void CreateStartInfo_StandardInputEncoding_ByteBoundary_WritesFirstCharacterWithoutBom()
    {
        var request = new CodexProcessStartRequest(
            ExecutablePath: "dummy.exe",
            WorkingDirectory: _tempDirectory,
            Environment: new Dictionary<string, string?>());

        var startInfo = CodexProcess.CreateStartInfo(request);

        using var memoryStream = new MemoryStream();
        using (var writer = new StreamWriter(memoryStream, startInfo.StandardInputEncoding, bufferSize: 1024, leaveOpen: true))
        {
            writer.Write("{\"jsonrpc\":\"2.0\"}");
            writer.Flush();
        }

        var writtenBytes = memoryStream.ToArray();
        Assert.NotEmpty(writtenBytes);
        Assert.Equal((byte)'{', writtenBytes[0]);

        var startsWithBom = writtenBytes.Length >= 3
            && writtenBytes[0] == 0xEF
            && writtenBytes[1] == 0xBB
            && writtenBytes[2] == 0xBF;
        Assert.False(startsWithBom, "StandardInputEncoding must not emit the 0xEF 0xBB 0xBF BOM preamble on write.");
    }

    [Fact]
    public void CreateStartInfo_StandardInputEncoding_PreservesNonAsciiText()
    {
        var request = new CodexProcessStartRequest(
            ExecutablePath: "dummy.exe",
            WorkingDirectory: _tempDirectory,
            Environment: new Dictionary<string, string?>());

        var startInfo = CodexProcess.CreateStartInfo(request);

        const string nonAsciiMessage = "{\"message\":\"สถานะตอนนี้ของ AIMonitor 🚀\"}";

        using var memoryStream = new MemoryStream();
        using (var writer = new StreamWriter(memoryStream, startInfo.StandardInputEncoding, bufferSize: 1024, leaveOpen: true))
        {
            writer.Write(nonAsciiMessage);
            writer.Flush();
        }

        var bytes = memoryStream.ToArray();
        Assert.Equal((byte)'{', bytes[0]);

        var decoded = Encoding.UTF8.GetString(bytes);
        Assert.Equal(nonAsciiMessage, decoded);
    }

    [Fact]
    public async Task CodexProcess_RealSubprocess_PipeDoesNotPrependBomToFirstJsonMessage()
    {
        // Exercises the real CodexProcess constructor and OS pipe redirection with a disposable script.
        var scriptPath = Path.Combine(_tempDirectory, "dumper.cmd");
        var scriptContent = "@echo off\r\npowershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"$s = [Console]::OpenStandardInput(); $b = New-Object byte[] 32; $n = $s.Read($b, 0, 32); if ($n -gt 0) { [BitConverter]::ToString($b, 0, $n) }\"\r\n";
        File.WriteAllText(scriptPath, scriptContent);

        var environment = CodexEnvironmentSanitizer.Sanitize(
            new Dictionary<string, string?>
            {
                ["PATH"] = Environment.GetEnvironmentVariable("PATH"),
                ["SYSTEMROOT"] = Environment.GetEnvironmentVariable("SYSTEMROOT"),
                ["COMSPEC"] = Environment.GetEnvironmentVariable("COMSPEC"),
            },
            temporaryCodexHome: _tempDirectory);

        var request = new CodexProcessStartRequest(
            ExecutablePath: scriptPath,
            WorkingDirectory: _tempDirectory,
            Environment: environment);

        var process = new CodexProcess(request);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1}".AsMemory(), cts.Token);
            await process.StandardInput.FlushAsync(cts.Token);
            process.StandardInput.Close();

            var outputLine = await process.StandardOutput.ReadLineAsync(cts.Token);
            Assert.NotNull(outputLine);

            // If UTF8Encoding(true) was used, outputLine starts with EF-BB-BF-7B.
            // With UTF8Encoding(false), outputLine starts with 7B ('{').
            Assert.DoesNotContain("EF-BB-BF", outputLine);
            Assert.StartsWith("7B", outputLine);
        }
        finally
        {
            try
            {
                process.Kill();
                await process.WaitForExitAsync(TimeSpan.FromSeconds(2));
            }
            finally
            {
                process.Dispose();
            }
        }
    }
}
