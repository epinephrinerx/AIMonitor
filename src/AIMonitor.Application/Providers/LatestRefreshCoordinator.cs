namespace AIMonitor.Application.Providers;

/// <summary>Runs one refresh at a time and retains only the newest request queued while busy.
/// Every caller whose queued request was coalesced receives the result of that newest request.</summary>
public sealed class LatestRefreshCoordinator : IAsyncDisposable
{
    private readonly RefreshProvidersUseCase _useCase;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Batch? _pending;
    private Task? _runner;
    private bool _disposed;

    public LatestRefreshCoordinator(RefreshProvidersUseCase useCase) =>
        _useCase = useCase ?? throw new ArgumentNullException(nameof(useCase));

    public Task<ProviderRefreshResult> QueueAsync(
        long requestId,
        ProviderSnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var completion = new TaskCompletionSource<ProviderRefreshResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_runner is null)
            {
                // Scheduling outside the current call stack prevents a synchronously-completing
                // use case from clearing _runner before this assignment stores the task.
                var first = new Batch(requestId, request, [completion]);
                _runner = Task.Run(() => RunAsync(first));
            }
            else if (_pending is null)
            {
                _pending = new Batch(requestId, request, [completion]);
            }
            else
            {
                _pending = new Batch(requestId, request, [.. _pending.Waiters, completion]);
            }
        }

        if (cancellationToken.CanBeCanceled)
        {
            var registration = cancellationToken.Register(
                static state =>
                {
                    var tuple = ((TaskCompletionSource<ProviderRefreshResult>, CancellationToken))state!;
                    tuple.Item1.TrySetCanceled(tuple.Item2);
                },
                (completion, cancellationToken));
            _ = completion.Task.ContinueWith(
                static (_, state) => ((CancellationTokenRegistration)state!).Dispose(),
                registration,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        return completion.Task;
    }

    private async Task RunAsync(Batch batch)
    {
        while (true)
        {
            try
            {
                var result = await _useCase.ExecuteAsync(batch.RequestId, batch.Request, _lifetime.Token).ConfigureAwait(false);
                foreach (var waiter in batch.Waiters)
                {
                    waiter.TrySetResult(result);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                foreach (var waiter in batch.Waiters)
                {
                    waiter.TrySetCanceled(_lifetime.Token);
                }
            }

            lock (_gate)
            {
                if (_pending is null || _lifetime.IsCancellationRequested)
                {
                    if (_pending is not null)
                    {
                        foreach (var waiter in _pending.Waiters)
                        {
                            waiter.TrySetCanceled(_lifetime.Token);
                        }
                        _pending = null;
                    }
                    _runner = null;
                    return;
                }

                batch = _pending;
                _pending = null;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? runner;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _lifetime.Cancel();
            runner = _runner;
        }

        if (runner is not null)
        {
            await runner.ConfigureAwait(false);
        }
        _lifetime.Dispose();
    }

    private sealed record Batch(
        long RequestId,
        ProviderSnapshotRequest Request,
        IReadOnlyList<TaskCompletionSource<ProviderRefreshResult>> Waiters);
}
