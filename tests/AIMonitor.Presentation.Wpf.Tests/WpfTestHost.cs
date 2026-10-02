using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using AIMonitor.Presentation.Wpf.Theme;

namespace AIMonitor.Presentation.Wpf.Tests;

[CollectionDefinition("Wpf", DisableParallelization = true)]
public class WpfCollectionDefinition
{
}

/// <summary>
/// Shared test host owning a single long-lived STA thread with a WPF Dispatcher and Application instance.
/// Prevents multiple Application creation errors and cross-thread access across test classes.
/// </summary>
public static class WpfTestHost
{
    private static readonly Lazy<Dispatcher> s_dispatcher = new(InitializeDispatcher, LazyThreadSafetyMode.ExecutionAndPublication);

    private static Dispatcher InitializeDispatcher()
    {
        var ready = new ManualResetEventSlim(false);
        Dispatcher? dispatcher = null;
        Exception? initException = null;

        var thread = new Thread(() =>
        {
            try
            {
                if (System.Windows.Application.Current is null)
                {
                    _ = new System.Windows.Application
                    {
                        ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown
                    };
                }

                ThemeManager.Instance.ApplyTheme("dark");

                dispatcher = System.Windows.Application.Current!.Dispatcher;
            }
            catch (Exception ex)
            {
                initException = ex;
            }
            finally
            {
                ready.Set();
            }

            if (initException is null)
            {
                Dispatcher.Run();
            }
        })
        {
            IsBackground = true,
            Name = "WpfTestHost_STA"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        ready.Wait();
        ready.Dispose();

        if (initException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(initException).Throw();
        }

        return dispatcher!;
    }

    public static void Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var dispatcher = s_dispatcher.Value;
        if (Dispatcher.CurrentDispatcher == dispatcher)
        {
            action();
            return;
        }

        Exception? thrown = null;
        dispatcher.Invoke(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                thrown = ex;
            }
        });

        if (thrown is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(thrown).Throw();
        }
    }

    public static T Run<T>(Func<T> func)
    {
        ArgumentNullException.ThrowIfNull(func);

        var dispatcher = s_dispatcher.Value;
        if (Dispatcher.CurrentDispatcher == dispatcher)
        {
            return func();
        }

        Exception? thrown = null;
        T result = default!;

        dispatcher.Invoke(() =>
        {
            try
            {
                result = func();
            }
            catch (Exception ex)
            {
                thrown = ex;
            }
        });

        if (thrown is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(thrown).Throw();
        }

        return result;
    }

    public static async Task RunAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var dispatcher = s_dispatcher.Value;
        if (Dispatcher.CurrentDispatcher == dispatcher)
        {
            await action().ConfigureAwait(false);
            return;
        }

        await dispatcher.InvokeAsync(action).Task.Unwrap().ConfigureAwait(false);
    }

    public static void Realize(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        Run(() =>
        {
            element.ApplyTemplate();
            var measureSize = element is Window
                ? new Size(1000, 800)
                : new Size(double.PositiveInfinity, double.PositiveInfinity);
            element.Measure(measureSize);
            element.Arrange(new Rect(element.DesiredSize));
            element.UpdateLayout();
            element.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        });
    }

    public static IDisposable ShowOffscreen(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        return Run(() =>
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -32000;
            window.Top = -32000;
            window.ShowInTaskbar = false;
            window.ShowActivated = false;

            window.Show();
            Realize(window);

            return new ActionDisposable(() =>
            {
                try
                {
                    window.Close();
                }
                catch
                {
                    // ignore
                }
            });
        });
    }

    private sealed class ActionDisposable : IDisposable
    {
        private Action? _dispose;

        public ActionDisposable(Action dispose)
        {
            _dispose = dispose;
        }

        public void Dispose()
        {
            var action = Interlocked.Exchange(ref _dispose, null);
            if (action is not null)
            {
                Run(action);
            }
        }
    }
}
