using System;
using System.Collections.Concurrent;
using System.Threading;

namespace STS2Mobile.Steam;

// Background thread work queue for cloud write operations. Processes actions
// sequentially to avoid mid-game stutters. Supports flush with timeout for
// graceful shutdown on app background.
public class CloudWriteQueue : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private volatile bool _actionInProgress;

    public int Count => _queue.Count;

    public CloudWriteQueue()
    {
        _thread = new Thread(ProcessLoop) { IsBackground = true, Name = "CloudSaveWriter" };
        _thread.Start();
    }

    public void Enqueue(Action action)
    {
        _queue.Add(action);
    }

    // Waits for pending work to complete, up to timeoutMs. Does not break the
    // queue — new work can still be enqueued after flush returns. Crucially,
    // also waits for any action currently being executed by ProcessLoop —
    // _queue.Count drops to 0 the moment an item is dequeued, but the actual
    // upload can take many seconds; without _actionInProgress, Flush returned
    // while a write was still in flight and verification then read stale
    // server state (issue #4 verification: rc4 KeepLocal verify saw cloud=811
    // because cloud upload hadn't landed yet 3s after Flush returned).
    //
    // P0-1: returns true once everything queued (including whatever was
    // in-flight when Flush was called) has actually finished running, false
    // on timeout. Callers that need to know whether a push/pull genuinely
    // completed (not just "we stopped waiting") must check this instead of
    // assuming a return means success — see CloudSyncCoordinator.ManualPushAllAsync.
    public bool Flush(int timeoutMs = 5000)
    {
        if (_queue.Count == 0 && !_actionInProgress)
            return true;

        PatchHelper.Log(
            $"[Cloud] Flushing {_queue.Count} queued + {(_actionInProgress ? "1 in-flight" : "0 in-flight")} writes..."
        );
        var deadline = Environment.TickCount64 + timeoutMs;

        while ((_queue.Count > 0 || _actionInProgress) && Environment.TickCount64 < deadline)
            Thread.Sleep(100);

        if (_queue.Count > 0 || _actionInProgress)
        {
            PatchHelper.Log(
                $"[Cloud] Flush timed out, {_queue.Count} queued + "
                    + $"{(_actionInProgress ? "1 in-flight" : "0 in-flight")} remaining"
            );
            return false;
        }

        PatchHelper.Log("[Cloud] Flush completed");
        return true;
    }

    public void Dispose()
    {
        Flush(5000);
        _queue.CompleteAdding();
        _thread.Join(2000);
        _queue.Dispose();
    }

    private void ProcessLoop()
    {
        foreach (var action in _queue.GetConsumingEnumerable())
        {
            _actionInProgress = true;
            try
            {
                action();
            }
            catch (Exception ex)
            {
                PatchHelper.Log($"[Cloud] Background write failed: {ex.Message}");
            }
            finally
            {
                _actionInProgress = false;
            }
        }
    }
}
