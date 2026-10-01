using AIMonitor.Infrastructure.Windows;

namespace AIMonitor.Infrastructure.Tests.Windows;

public sealed class NamedMutexSingleInstanceTests
{
    [Fact]
    public async Task TryAcquireAsync_FirstInstance_AcquiresOwnership()
    {
        var testId = Guid.NewGuid().ToString("N");
        var mutexName = $@"Local\TestMutex_{testId}";
        var pipeName = $"TestPipe_{testId}";

        await using var coordinator = new NamedMutexSingleInstance(mutexName, pipeName);
        var acquired = await coordinator.TryAcquireAsync();

        Assert.True(acquired);
        Assert.True(coordinator.HasOwnership);
    }

    [Fact]
    public async Task TryAcquireAsync_SecondInstance_ReturnsFalse()
    {
        var testId = Guid.NewGuid().ToString("N");
        var mutexName = $@"Local\TestMutex_{testId}";
        var pipeName = $"TestPipe_{testId}";

        var primaryReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var primaryStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondaryResult = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Keep the primary thread alive synchronously to preserve OS thread ownership of the mutex
        var primaryThread = new Thread(() =>
        {
            using var primary = new NamedMutexSingleInstance(mutexName, pipeName);
            var firstAcquired = primary.TryAcquire();
            if (firstAcquired)
            {
                primaryReady.TrySetResult();
            }
            else
            {
                primaryReady.TrySetException(new InvalidOperationException("Primary failed to acquire"));
            }

            primaryStop.Task.Wait();
        })
        {
            IsBackground = true
        };
        primaryThread.Start();

        await primaryReady.Task;

        // Secondary instance on another thread must see the mutex held by primary
        var secondThread = new Thread(() =>
        {
            using var secondary = new NamedMutexSingleInstance(mutexName, pipeName);
            var secondAcquired = secondary.TryAcquire();
            secondaryResult.TrySetResult(secondAcquired);
        })
        {
            IsBackground = true
        };
        secondThread.Start();

        var acquired = await secondaryResult.Task;
        Assert.False(acquired);

        primaryStop.TrySetResult();
        primaryThread.Join();
        secondThread.Join();
    }

    [Fact]
    public async Task NotifyExistingInstanceAsync_DeliversArgumentsToPrimaryInstance()
    {
        var testId = Guid.NewGuid().ToString("N");
        var mutexName = $@"Local\TestMutex_{testId}";
        var pipeName = $"TestPipe_{testId}";

        var primaryReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var primaryStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activationTcs = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondaryResult = new TaskCompletionSource<(bool Acquired, bool Signaled)>(TaskCreationOptions.RunContinuationsAsynchronously);

        var primaryThread = new Thread(() =>
        {
            using var primary = new NamedMutexSingleInstance(mutexName, pipeName);
            var firstAcquired = primary.TryAcquire();
            primary.Activated += args => activationTcs.TrySetResult(args);
            if (firstAcquired)
            {
                primaryReady.TrySetResult();
            }
            else
            {
                primaryReady.TrySetException(new InvalidOperationException("Primary failed to acquire"));
            }

            primaryStop.Task.Wait();
        })
        {
            IsBackground = true
        };
        primaryThread.Start();

        await primaryReady.Task;

        // Give the named pipe server a brief moment to start listening
        await Task.Delay(100);

        var secondaryThread = new Thread(async () =>
        {
            await using var secondary = new NamedMutexSingleInstance(mutexName, pipeName);
            var secondaryAcquired = secondary.TryAcquire();
            var signaled = false;
            if (!secondaryAcquired)
            {
                signaled = await secondary.NotifyExistingInstanceAsync(["--show-dashboard", "--focus"]);
            }
            secondaryResult.TrySetResult((secondaryAcquired, signaled));
        })
        {
            IsBackground = true
        };
        secondaryThread.Start();

        var (acquired, signaled) = await secondaryResult.Task;
        Assert.False(acquired);
        Assert.True(signaled);

        var received = await activationTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["--show-dashboard", "--focus"], received);

        primaryStop.TrySetResult();
        primaryThread.Join();
        secondaryThread.Join();
    }

    [Fact]
    public async Task DisposeAsync_ReleasesMutex_AllowingSubsequentInstanceToAcquire()
    {
        var testId = Guid.NewGuid().ToString("N");
        var mutexName = $@"Local\TestMutex_{testId}";
        var pipeName = $"TestPipe_{testId}";

        var first = new NamedMutexSingleInstance(mutexName, pipeName);
        var firstAcquired = first.TryAcquire();
        Assert.True(firstAcquired);

        await first.DisposeAsync();
        Assert.False(first.HasOwnership);

        var secondaryResult = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondThread = new Thread(() =>
        {
            using var second = new NamedMutexSingleInstance(mutexName, pipeName);
            var secondAcquired = second.TryAcquire();
            secondaryResult.TrySetResult(secondAcquired);
        })
        {
            IsBackground = true
        };
        secondThread.Start();

        var secondAcquired = await secondaryResult.Task;
        Assert.True(secondAcquired);

        secondThread.Join();
    }

    [Fact]
    public void TryAcquire_WhenAbandonedMutex_RecoversOwnershipSafely()
    {
        var testId = Guid.NewGuid().ToString("N");
        var mutexName = $@"Local\TestMutex_{testId}";
        var pipeName = $"TestPipe_{testId}";

        using var acquiredSignal = new ManualResetEventSlim(false);
        Mutex? keepAliveHandle = null;

        var thread = new Thread(() =>
        {
            var m = new Mutex(true, mutexName, out _);
            acquiredSignal.Set();
            // Intentionally exit thread without calling ReleaseMutex() -> abandons mutex
        })
        {
            IsBackground = true
        };

        thread.Start();
        Assert.True(acquiredSignal.Wait(TimeSpan.FromSeconds(5)));

        // Open an existing handle while thread is alive so the kernel object does not get destroyed
        keepAliveHandle = Mutex.OpenExisting(mutexName);

        thread.Join();
        // Allow the Windows kernel to complete thread cleanup and transition the mutex to abandoned state
        Thread.Sleep(50);

        try
        {
            using var coordinator = new NamedMutexSingleInstance(mutexName, pipeName);
            var acquired = coordinator.TryAcquire();

            Assert.True(acquired);
            Assert.True(coordinator.HasOwnership);
            Assert.True(coordinator.WasAbandoned);
        }
        finally
        {
            keepAliveHandle.Dispose();
        }
    }
}
