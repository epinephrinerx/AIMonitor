using System.Diagnostics;
using AIMonitor.Infrastructure.OpenCode;

namespace AIMonitor.Infrastructure.Tests.OpenCode;

/// <summary>
/// R-020 fix round 2 (Docs/SPEC_R020_R021.md, "Fix round 2"), reader rows: J1 (the first wait on stdout and exit is
/// itself bounded by the deadline) and J2 (<c>OpenCodeAuthReader.ReadFileAsync</c> bounded file read). J2 uses only
/// synthetic files in a unique temp directory that is deleted afterwards: no real OpenCode, ccusage, credential
/// file, network or process is touched.
/// </summary>
public sealed class OpenCodeAuthReaderFixRound2Tests
{
    private const string GoodAuthJson = """{"anthropic":{"type":"oauth"},"openai":{"type":"api"}}""";
    private static readonly string[] GoodAuthPairs = ["anthropic|OAuth", "openai|API key"];
    private static readonly TimeSpan Deadline = TimeSpan.FromMilliseconds(500);

    /// <summary>The post-kill wait for exit stays capped at 2 s (J1).</summary>
    private static readonly TimeSpan PostKillCap = TimeSpan.FromSeconds(2);

    /// <summary>Scheduling margin on top of deadline + cap; far below the 30 s harness deadline a reader that is
    /// not bounded would otherwise sit on, so an unbounded wait still fails.</summary>
    private static readonly TimeSpan Margin = TimeSpan.FromSeconds(4);

    private const int OneMegabytePlusOne = (1024 * 1024) + 1;

    // ── J1 ──────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("version", false)]
    [InlineData("version", true)]
    [InlineData("list", false)]
    [InlineData("list", true)]
    public async Task StdoutAndExitThatBothIgnoreCancellation_AreBoundedByTheDeadline_AndTheProcessIsKilled_J1(string command, bool stderrNeverEnds)
    {
        // Stdout never ends and ignores its token (and the kill); the wait for exit never completes and ignores its
        // token too. Nothing the fake does can end the first wait, so only the reader's own deadline can: it must
        // reach the kill path and then give up on the post-kill exit wait after at most 2 s.
        var h = new ReaderHarness { VersionDeadline = Deadline, AuthListDeadline = Deadline };
        var index = command == "version" ? 0 : 1;
        TextReader Stderr() => stderrNeverEnds ? new NeverEndingReader() : new StringReader("");
        if (command == "version")
        {
            h.OnVersion = () => new FakeOpenCodeProcess(new NeverEndingReader(), Stderr(), hang: true, exitIgnoresCancellation: true);
        }
        else
        {
            h.SetVersion("2.2.7");
            h.OnList = () => new FakeOpenCodeProcess(new NeverEndingReader(), Stderr(), hang: true, exitIgnoresCancellation: true);
        }

        h.SetAuthJson(GoodAuthJson);

        var watch = Stopwatch.StartNew();
        var status = await h.ReadAsync().WaitAsync(Deadline + PostKillCap + Margin);
        watch.Stop();

        Assert.True(h.Processes[index].Killed, "The kill path was not reached.");
        Assert.True(watch.Elapsed < Deadline + PostKillCap + Margin, $"The read took {watch.Elapsed}.");
        Assert.Equal("auth.json", status.Source); // the failed command -> F4 fallback
        Assert.True(status.MayBeOutdated);
        Assert.Equal(GoodAuthPairs, ReaderHarness.Pairs(status));
    }

    [Fact]
    public async Task ACommandThatFinishesNormally_IsStillReadInFull_UnderTheNewBound_J1()
    {
        // The new bound must not cut a command short: stdout that ends at once and an exit that completes at once
        // finish normally and their output is used (the reader may still kill the process tree while cleaning up).
        var h = new ReaderHarness { VersionDeadline = Deadline, AuthListDeadline = Deadline };
        h.SetVersion("2.2.7");
        h.SetListJson("""[{"id":"a","name":"Anthropic","connections":[{"type":"credential","method":"oauth"}]}]""");

        var status = await h.ReadAsync().WaitAsync(Deadline + PostKillCap + Margin);

        Assert.Equal("opencode auth list", status.Source);
        Assert.Equal(new[] { "Anthropic|OAuth" }, ReaderHarness.Pairs(status));
    }

    // ── J2: OpenCodeAuthReader.ReadFileAsync ────────────────────────────────────

    [Fact]
    public async Task ReadFile_OfAFileLargerThanTheLimit_ReturnsExactlyTheFirstMaxBytes_J2()
    {
        using var dir = new TempDirectory();
        var content = Pattern(OneMegabytePlusOne + 4096);
        var path = dir.Write("big.json", content);

        var bytes = await OpenCodeAuthReader.ReadFileAsync(path, OneMegabytePlusOne, CancellationToken.None);

        Assert.NotNull(bytes);
        Assert.Equal(OneMegabytePlusOne, bytes!.Length);
        Assert.True(content.AsSpan(0, OneMegabytePlusOne).SequenceEqual(bytes), "Not the first maxBytes bytes of the file.");
        Assert.True(File.ReadAllBytes(path).AsSpan().SequenceEqual(content), "The file was changed.");
    }

    [Fact]
    public async Task ReadFile_OfAFileExactlyOneByteOverTheLimit_ReturnsTheFirstMaxBytes_J2()
    {
        using var dir = new TempDirectory();
        var content = Pattern(OneMegabytePlusOne + 1);
        var path = dir.Write("edge.json", content);

        var bytes = await OpenCodeAuthReader.ReadFileAsync(path, OneMegabytePlusOne, CancellationToken.None);

        Assert.NotNull(bytes);
        Assert.Equal(OneMegabytePlusOne, bytes!.Length);
        Assert.True(content.AsSpan(0, OneMegabytePlusOne).SequenceEqual(bytes));
    }

    [Fact]
    public async Task ReadFile_OfAFileExactlyTheLimit_ReturnsTheWholeFile_J2()
    {
        using var dir = new TempDirectory();
        var content = Pattern(OneMegabytePlusOne);
        var path = dir.Write("exact.json", content);

        var bytes = await OpenCodeAuthReader.ReadFileAsync(path, OneMegabytePlusOne, CancellationToken.None);

        Assert.NotNull(bytes);
        Assert.True(content.AsSpan().SequenceEqual(bytes));
    }

    [Fact]
    public async Task ReadFile_OfASmallFile_ReturnsItsWholeContent_J2()
    {
        using var dir = new TempDirectory();
        var content = System.Text.Encoding.UTF8.GetBytes(GoodAuthJson);
        var path = dir.Write("small.json", content);

        var bytes = await OpenCodeAuthReader.ReadFileAsync(path, OneMegabytePlusOne, CancellationToken.None);

        Assert.NotNull(bytes);
        Assert.True(content.AsSpan().SequenceEqual(bytes));
    }

    [Fact]
    public async Task ReadFile_KeepsTheBytesAsTheyAre_ABomAndBinaryValuesIncluded_J2()
    {
        using var dir = new TempDirectory();
        byte[] content = [0xEF, 0xBB, 0xBF, (byte)'{', (byte)'}', 0x00, 0xFF, 0x0D, 0x0A];
        var path = dir.Write("bom.json", content);

        var bytes = await OpenCodeAuthReader.ReadFileAsync(path, OneMegabytePlusOne, CancellationToken.None);

        Assert.NotNull(bytes);
        Assert.True(content.AsSpan().SequenceEqual(bytes));
    }

    [Fact]
    public async Task ReadFile_OfAnEmptyFile_ReturnsAnEmptyArray_NotNull_J2()
    {
        using var dir = new TempDirectory();
        var path = dir.Write("empty.json", []);

        var bytes = await OpenCodeAuthReader.ReadFileAsync(path, OneMegabytePlusOne, CancellationToken.None);

        Assert.NotNull(bytes);
        Assert.Empty(bytes!);
    }

    [Fact]
    public async Task ReadFile_WithASmallerLimitThanTheFile_ReturnsOnlyTheLimit_J2()
    {
        using var dir = new TempDirectory();
        var content = Pattern(100);
        var path = dir.Write("limit.json", content);

        var bytes = await OpenCodeAuthReader.ReadFileAsync(path, 10, CancellationToken.None);

        Assert.NotNull(bytes);
        Assert.True(content.AsSpan(0, 10).SequenceEqual(bytes));
    }

    [Fact]
    public async Task ReadFile_OfAMissingFile_InAnExistingDirectory_ReturnsNull_J2()
    {
        using var dir = new TempDirectory();

        var bytes = await OpenCodeAuthReader.ReadFileAsync(Path.Combine(dir.Path, "missing.json"), OneMegabytePlusOne, CancellationToken.None);

        Assert.Null(bytes);
    }

    [Fact]
    public async Task ReadFile_InAMissingDirectory_ReturnsNull_J2()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "no-such-directory", "deeper", "auth.json");

        var bytes = await OpenCodeAuthReader.ReadFileAsync(path, OneMegabytePlusOne, CancellationToken.None);

        Assert.Null(bytes);
    }

    /// <summary>Deterministic, non-repeating-looking bytes so a wrong offset or length shows up in a comparison.</summary>
    private static byte[] Pattern(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)((i * 31) + (i / 251));
        }

        return bytes;
    }

    /// <summary>A unique directory under the system temp path that is deleted when the test ends.</summary>
    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aimon-r020-j2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Write(string name, byte[] content)
        {
            var file = System.IO.Path.Combine(Path, name);
            File.WriteAllBytes(file, content);
            return file;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
