using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using STS2Mobile.Modding;

namespace STS2Mobile.Steam;

public enum WorkshopDownloadState
{
    Queued,
    Downloading,
    Completed,
    Failed,
}

public class WorkshopDownloadEntry
{
    public WorkshopItemDetails Item;
    public WorkshopDownloadState State;
    public double ProgressPercent;
    public string Error;
    public string ModId;
}

// Sequential download queue backing the UI's Downloads tab. A single background
// worker processes one item at a time — installs touch the shared Mods/ tree and
// mod_config.json, so serializing avoids concurrent-write races. Changed fires
// from the worker's pool thread; UI subscribers must marshal back to the main
// thread themselves (Godot CallDeferred). No Godot dependency here.
public class WorkshopDownloadQueue
{
    private readonly SteamConnection _connection;
    private readonly object _gate = new();
    private readonly List<WorkshopDownloadEntry> _entries = new();
    private readonly SemaphoreSlim _workAvailable = new(0);

    private CancellationTokenSource _cts = new();
    private Task _worker;

    public event Action Changed;

    public WorkshopDownloadQueue(SteamConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    public IReadOnlyList<WorkshopDownloadEntry> Entries
    {
        get
        {
            lock (_gate)
                return _entries.ToList();
        }
    }

    public bool IsBusy
    {
        get
        {
            lock (_gate)
                return _entries.Any(e =>
                    e.State == WorkshopDownloadState.Queued
                    || e.State == WorkshopDownloadState.Downloading
                );
        }
    }

    // Adds item to the queue. A duplicate pfid that's already Queued/Downloading
    // is ignored; one that's Completed/Failed is reset back to Queued so it can
    // be retried.
    public void Enqueue(WorkshopItemDetails item)
    {
        if (item == null)
            throw new ArgumentNullException(nameof(item));

        lock (_gate)
        {
            var existing = _entries.FirstOrDefault(e =>
                e.Item.PublishedFileId == item.PublishedFileId
            );
            if (existing != null)
            {
                if (
                    existing.State != WorkshopDownloadState.Completed
                    && existing.State != WorkshopDownloadState.Failed
                )
                    return; // already queued/downloading — ignore

                existing.Item = item;
                existing.State = WorkshopDownloadState.Queued;
                existing.ProgressPercent = 0;
                existing.Error = null;
                existing.ModId = null;
            }
            else
            {
                _entries.Add(
                    new WorkshopDownloadEntry { Item = item, State = WorkshopDownloadState.Queued }
                );
            }

            EnsureWorkerStarted();
        }

        _workAvailable.Release();
        RaiseChanged();
    }

    // Cancels the in-flight download (if any) and drops all still-queued entries.
    // Entries already Completed/Failed remain in the list as history. The worker
    // keeps running (idle, waiting for the next Enqueue) unless it happened to be
    // parked on the cancelled wait, in which case a fresh one spins up lazily on
    // the next Enqueue.
    public void CancelAll()
    {
        lock (_gate)
        {
            _cts.Cancel();
            _cts = new CancellationTokenSource();
            _entries.RemoveAll(e => e.State == WorkshopDownloadState.Queued);
        }
        RaiseChanged();
    }

    // Called under _gate.
    private void EnsureWorkerStarted()
    {
        if (_worker != null && !_worker.IsCompleted)
            return;
        _worker = Task.Run(RunWorker);
    }

    private async Task RunWorker()
    {
        while (true)
        {
            CancellationToken ct;
            lock (_gate)
                ct = _cts.Token;

            try
            {
                await _workAvailable.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Idle worker parked on a token CancelAll just cancelled. A fresh
                // worker spins up on the next Enqueue.
                return;
            }

            WorkshopDownloadEntry entry;
            lock (_gate)
            {
                entry = _entries.FirstOrDefault(e => e.State == WorkshopDownloadState.Queued);
                if (entry == null)
                    continue; // spurious wake (e.g. CancelAll dropped it after the signal)
                entry.State = WorkshopDownloadState.Downloading;
                entry.ProgressPercent = 0;
            }
            RaiseChanged();

            try
            {
                var throttle = new ProgressThrottle(percent =>
                {
                    lock (_gate)
                        entry.ProgressPercent = percent;
                    RaiseChanged();
                });
                var progress = new Progress<DownloadProgress>(p => throttle.Report(p.Percentage));

                var result = await WorkshopInstaller
                    .DownloadAndInstallAsync(_connection, entry.Item, progress, ct)
                    .ConfigureAwait(false);

                lock (_gate)
                {
                    if (result.Success)
                    {
                        entry.State = WorkshopDownloadState.Completed;
                        entry.ProgressPercent = 100;
                        entry.ModId = result.ModId;
                    }
                    else
                    {
                        entry.State = WorkshopDownloadState.Failed;
                        entry.ModId = result.ModId;
                        entry.Error =
                            result.Conflict ? "이미 수동 설치된 모드와 충돌(덮어쓰지 않음)"
                            : result.NoManifest ? "모드 매니페스트 없음"
                            : (result.Error ?? "다운로드 실패");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                lock (_gate)
                {
                    entry.State = WorkshopDownloadState.Failed;
                    entry.Error = "취소됨";
                }
            }
            catch (Exception ex)
            {
                PatchHelper.Log($"[Workshop] Queue entry {entry.Item.PublishedFileId} threw: {ex}");
                lock (_gate)
                {
                    entry.State = WorkshopDownloadState.Failed;
                    entry.Error = ex.Message;
                }
            }

            RaiseChanged();
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Workshop] Changed subscriber threw: {ex.Message}");
        }
    }

    // Throttles progress callbacks to at most one per 250ms or a 1%-point step
    // (always reporting 100%), so the UI/log aren't flooded during a
    // many-small-file depot download.
    private sealed class ProgressThrottle
    {
        private readonly Action<double> _onReport;
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private double _lastPercent = -1;

        public ProgressThrottle(Action<double> onReport) => _onReport = onReport;

        public void Report(double percent)
        {
            if (percent < 100 && percent - _lastPercent < 1.0 && _sw.ElapsedMilliseconds < 250)
                return;
            _lastPercent = percent;
            _sw.Restart();
            _onReport(percent);
        }
    }
}
