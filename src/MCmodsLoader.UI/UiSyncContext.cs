using System.Collections.Concurrent;
using MCmodsLoader.UI.Interop;

namespace MCmodsLoader.UI;

/// <summary>
/// Marshals async continuations back onto the message loop, so the services in
/// MCmodsLoader.Core can be awaited exactly the way the WPF version awaited them.
/// Without this, a continuation after an await would resume on a thread-pool
/// thread and touch window handles from the wrong thread.
/// </summary>
internal sealed class UiSyncContext : SynchronizationContext
{
    public const uint WM_RUN_CALLBACK = Native.WM_APP + 1;

    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();
    private readonly int _uiThreadId;
    private nint _hwnd;

    public UiSyncContext() => _uiThreadId = Environment.CurrentManagedThreadId;

    /// <summary>The window is created after the context, so the handle is attached later.</summary>
    public void Attach(nint hwnd)
    {
        _hwnd = hwnd;
        // Anything queued before the window existed can be delivered now.
        if (!_queue.IsEmpty) Native.PostMessageW(_hwnd, WM_RUN_CALLBACK, 0, 0);
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        _queue.Enqueue((d, state));
        if (_hwnd != 0) Native.PostMessageW(_hwnd, WM_RUN_CALLBACK, 0, 0);
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (Environment.CurrentManagedThreadId == _uiThreadId)
        {
            d(state);
            return;
        }

        using var done = new ManualResetEventSlim(false);
        Exception? error = null;
        Post(_ =>
        {
            try { d(state); }
            catch (Exception ex) { error = ex; }
            finally { done.Set(); }
        }, null);
        done.Wait();
        if (error != null) throw error;
    }

    public override SynchronizationContext CreateCopy() => this;

    /// <summary>Runs every callback queued so far. Called from the window procedure.</summary>
    public void Drain()
    {
        while (_queue.TryDequeue(out var work))
        {
            work.Callback(work.State);
        }
    }
}
