using System;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace InventorMcpServer.Services;

/// <summary>
/// Dedicated STA (Single-Threaded Apartment) thread dispatcher for Autodesk Inventor COM interop.
/// All Inventor API COM calls must execute on this dedicated thread to guarantee COM apartment consistency.
/// </summary>
public sealed class StaThreadDispatcher : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public StaThreadDispatcher()
    {
        _thread = new Thread(ThreadLoop)
        {
            Name = "Inventor-STA-Dispatcher",
            IsBackground = true
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void ThreadLoop()
    {
        // Install STA synchronization context so async/await continuations marshal back to this STA thread
        SynchronizationContext.SetSynchronizationContext(new StaSynchronizationContext(_queue));

        try
        {
            foreach (var workItem in _queue.GetConsumingEnumerable(_cts.Token))
            {
                try
                {
                    workItem();
                }
                catch (Exception ex)
                {
                    // Exceptions from work items are captured by their TaskCompletionSource,
                    // but we log any unhandled dispatcher exceptions to stderr.
                    Console.Error.WriteLine($"[StaThreadDispatcher] Uncaught exception in work item: {ex}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation/shutdown
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[StaThreadDispatcher] Fatal STA message pump error: {ex}");
        }
    }

    /// <summary>
    /// Dispatches a function onto the STA thread and asynchronously awaits its result.
    /// </summary>
    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            _queue.Add(() =>
            {
                try
                {
                    var result = func();
                    tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });
        }
        catch (InvalidOperationException)
        {
            tcs.TrySetCanceled();
        }

        return tcs.Task;
    }

    /// <summary>
    /// Dispatches an action onto the STA thread and asynchronously awaits its completion.
    /// </summary>
    public Task InvokeAsync(Action action)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            _queue.Add(() =>
            {
                try
                {
                    action();
                    tcs.TrySetResult();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });
        }
        catch (InvalidOperationException)
        {
            tcs.TrySetCanceled();
        }

        return tcs.Task;
    }

    /// <summary>
    /// Dispatches an asynchronous function onto the STA thread and awaits the inner task completion.
    /// </summary>
    public Task<T> InvokeAsync<T>(Func<Task<T>> asyncFunc)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            _queue.Add(async () =>
            {
                try
                {
                    var result = await asyncFunc();
                    tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });
        }
        catch (InvalidOperationException)
        {
            tcs.TrySetCanceled();
        }

        return tcs.Task;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _queue.CompleteAdding();
        _cts.Cancel();

        if (_thread.IsAlive)
        {
            _thread.Join(TimeSpan.FromSeconds(3));
        }

        _queue.Dispose();
        _cts.Dispose();
    }

    private sealed class StaSynchronizationContext : SynchronizationContext
    {
        private readonly BlockingCollection<Action> _queue;

        public StaSynchronizationContext(BlockingCollection<Action> queue)
        {
            _queue = queue;
        }

        public override void Post(SendOrPostCallback d, object? state)
        {
            try
            {
                _queue.Add(() => d(state));
            }
            catch (InvalidOperationException)
            {
                // Queue was disposed
            }
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (Thread.CurrentThread.ManagedThreadId == Environment.CurrentManagedThreadId)
            {
                d(state);
                return;
            }

            using var mre = new ManualResetEventSlim();
            Exception? caught = null;

            try
            {
                _queue.Add(() =>
                {
                    try
                    {
                        d(state);
                    }
                    catch (Exception ex)
                    {
                        caught = ex;
                    }
                    finally
                    {
                        mre.Set();
                    }
                });

                mre.Wait();
                if (caught != null)
                {
                    ExceptionDispatchInfo.Capture(caught).Throw();
                }
            }
            catch (InvalidOperationException)
            {
                // Queue disposed
            }
        }
    }
}
