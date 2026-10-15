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
    public async Task StandardInputEncoding_OsPipe_PreservesFirstJsonBytes()
    {
        // Exercise an OS pipe using the configured transport encoding, without an external executable.
        var request = new CodexProcessStartRequest("synthetic.exe", _tempDirectory,
            new Dictionary<string, string?>());
        var encoding = CodexProcess.CreateStartInfo(request).StandardInputEncoding;
        Assert.NotNull(encoding);
        using var server = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.Out);
        using var client = new System.IO.Pipes.AnonymousPipeClientStream(System.IO.Pipes.PipeDirection.In,
            server.GetClientHandleAsString());
        using var writer = new StreamWriter(server, encoding, leaveOpen: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        const string message = "{\"jsonrpc\":\"2.0\"}";
        await writer.WriteAsync(message.AsMemory(), timeout.Token);
        await writer.FlushAsync(timeout.Token);
        var bytes = new byte[encoding.GetByteCount(message)];
        await client.ReadExactlyAsync(bytes, timeout.Token);
        Assert.Equal((byte)'{', bytes[0]);
        Assert.Equal(message, Encoding.UTF8.GetString(bytes));
    }
}
