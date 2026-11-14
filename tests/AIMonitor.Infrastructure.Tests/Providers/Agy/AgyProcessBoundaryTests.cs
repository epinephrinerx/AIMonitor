using AIMonitor.Infrastructure.Providers.Agy;

namespace AIMonitor.Infrastructure.Tests.Providers.Agy;

public sealed class AgyProcessBoundaryTests
{
    private sealed class Child : IAgyUsageProcess
    {
        public TextReader Output { get; init; } = new StringReader("synthetic");
        public TextReader Error { get; init; } = new StringReader("");
        public int ExitCode { get; init; }
        public bool Killed { get; private set; }
        public bool Disposed { get; private set; }
        public bool Hang { get; init; }
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task WaitForExitAsync(CancellationToken ct) => Hang ? _exit.Task.WaitAsync(ct) : Task.CompletedTask;
        public void Kill() { Killed = true; _exit.TrySetResult(); }
        public void Dispose() { Disposed = true; Output.Dispose(); Error.Dispose(); }
    }

    [Fact]
    public async Task Success_UsesHiddenShellFreeConstantCommandAndDisposesChild()
    {
        var child = new Child();
        var runner = new AgyUsageRunner(() => @"C:\synthetic\agy.exe", info =>
        {
            Assert.False(info.UseShellExecute);
            Assert.True(info.CreateNoWindow);
            Assert.True(info.RedirectStandardInput);
            Assert.True(info.RedirectStandardOutput);
            Assert.True(info.RedirectStandardError);
            Assert.Equal(new[] { "-p", "/usage", "--output-format", "json" }, info.ArgumentList);
            return child;
        }, TimeSpan.FromSeconds(1));
        Assert.Equal("synthetic", await runner.ReadUsageAsync(default));
        Assert.True(child.Disposed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExcessiveOutputOnEitherPipe_IsBoundedAndChildCleaned(bool stdout)
    {
        var child = new Child
        {
            Output = new StringReader(stdout ? new string('x', 100) : ""),
            Error = new StringReader(stdout ? "" : new string('x', 100))
        };
        var runner = new AgyUsageRunner(() => "synthetic.exe", _ => child, TimeSpan.FromSeconds(1), 16);
        var error = await Assert.ThrowsAsync<AgyUsageException>(() => runner.ReadUsageAsync(default));
        Assert.Equal(AgyErrorKind.Timeout, error.Kind);
        Assert.True(child.Killed);
        Assert.True(child.Disposed);
    }

    [Fact]
    public async Task Deadline_KillsAndDisposesHangingChild()
    {
        var child = new Child { Hang = true };
        var runner = new AgyUsageRunner(() => "synthetic.exe", _ => child, TimeSpan.FromMilliseconds(30));
        var error = await Assert.ThrowsAsync<AgyUsageException>(() => runner.ReadUsageAsync(default).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(AgyErrorKind.Timeout, error.Kind);
        Assert.True(child.Killed);
        Assert.True(child.Disposed);
    }

    [Fact]
    public async Task CallerCancellation_IsPropagatedAndChildCleaned()
    {
        var child = new Child { Hang = true };
        using var cancellation = new CancellationTokenSource();
        var runner = new AgyUsageRunner(() => "synthetic.exe", _ => child, TimeSpan.FromSeconds(10));
        var task = runner.ReadUsageAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(child.Killed);
        Assert.True(child.Disposed);
    }

    [Fact]
    public async Task NonzeroExit_DoesNotExposeStderr()
    {
        var child = new Child { ExitCode = 1, Error = new StringReader("synthetic-private-detail") };
        var runner = new AgyUsageRunner(() => "synthetic.exe", _ => child, TimeSpan.FromSeconds(1));
        var error = await Assert.ThrowsAsync<AgyUsageException>(() => runner.ReadUsageAsync(default));
        Assert.DoesNotContain("synthetic-private-detail", error.Message);
        Assert.True(child.Disposed);
    }

    [Fact]
    public async Task MissingExecutable_DoesNotStartChild()
    {
        var runner = new AgyUsageRunner(() => null, _ => throw new Exception("must not start"), TimeSpan.FromSeconds(1));
        var error = await Assert.ThrowsAsync<AgyUsageException>(() => runner.ReadUsageAsync(default));
        Assert.Equal(AgyErrorKind.NotInstalled, error.Kind);
    }
}
