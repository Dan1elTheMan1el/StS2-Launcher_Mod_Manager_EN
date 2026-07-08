using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using STS2Mobile.Launcher.Components;
using STS2Mobile.Modding;
using STS2Mobile.Steam;

namespace STS2Mobile.Launcher.Sections;

// SUBSCRIBED tab of the Mod Hub (issue #58 phase 4b). Every time this tab is
// selected it polls the user's Workshop subscriptions (WorkshopSyncService),
// enqueues installs/updates into the shared WorkshopDownloadQueue (so progress is
// visible in the DOWNLOADS tab instead of duplicating it here), auto-cleans stale
// registry entries, and — only after an explicit confirmation — removes orphaned
// mods whose folder is still present but the subscription is gone.
public class WorkshopSubscribedPane : VBoxContainer
{
    public event Action<string, Action, Action> ConfirmationRequested;

    private static readonly Color InfoColor = new(0.75f, 0.75f, 0.8f);
    private static readonly Color WarnColor = new(0.95f, 0.6f, 0.3f);

    private readonly float _scale;
    private readonly StyledLabel _statusLabel;
    private readonly VBoxContainer _list;

    private SteamConnection _connection;
    private WorkshopDownloadQueue _queue;
    private HashSet<ulong> _updateAvailablePfids = new();
    private bool _loggedIn;

    public WorkshopSubscribedPane(float scale)
    {
        _scale = scale;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", (int)(8 * scale));

        _statusLabel = new StyledLabel("", scale, fontSize: 12);
        _statusLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        AddChild(_statusLabel);

        var scroll = new ScrollContainer();
        scroll.SizeFlagsVertical = SizeFlags.ExpandFill;
        scroll.CustomMinimumSize = new Vector2(0, (int)(220 * scale));
        AddChild(scroll);

        _list = new VBoxContainer();
        _list.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _list.AddThemeConstantOverride("separation", (int)(6 * scale));
        scroll.AddChild(_list);
    }

    public void SetQueue(WorkshopDownloadQueue queue) => _queue = queue;

    // Called every time SUBSCRIBED becomes the active tab — always re-syncs (see
    // class comment). ModManagerSection also calls RenderList() directly on queue
    // Changed events while this pane is visible, for live download progress.
    public void Activate(Func<Task<(bool ok, SteamConnection conn)>> ensureSession) =>
        _ = Task.Run(() => SyncAsync(ensureSession));

    private async Task SyncAsync(Func<Task<(bool ok, SteamConnection conn)>> ensureSession)
    {
        RunOnMain(() => SetStatus("Connecting to Steam...", InfoColor));
        var (ok, conn) = await ensureSession().ConfigureAwait(false);
        _loggedIn = ok;
        if (!ok)
        {
            _connection = null;
            RunOnMain(() =>
            {
                SetStatus("Steam login is required for Workshop features.", WarnColor);
                RenderList();
            });
            return;
        }
        _connection = conn;

        RunOnMain(() => SetStatus("Syncing subscriptions...", InfoColor));

        WorkshopSyncPlan plan;
        try
        {
            plan = await WorkshopSyncService.ComputePlanAsync(conn).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Workshop] SUBSCRIBED sync failed: {ex}");
            RunOnMain(() =>
            {
                SetStatus("Sync failed (offline?)", WarnColor);
                RenderList();
            });
            return;
        }

        if (_queue != null)
        {
            foreach (var item in plan.ToInstall.Concat(plan.ToUpdate))
                _queue.Enqueue(item);
        }
        _updateAvailablePfids = new HashSet<ulong>(plan.ToUpdate.Select(i => i.PublishedFileId));

        if (plan.StaleEntries.Count > 0)
        {
            var cleanupPlan = new WorkshopSyncPlan { StaleEntries = plan.StaleEntries };
            try
            {
                await WorkshopSyncService
                    .ExecuteAsync(conn, cleanupPlan, removeOrphans: false)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                PatchHelper.Log($"[Workshop] Stale entry cleanup failed: {ex.Message}");
            }
        }

        var skippedSummary = plan.Skipped.Count > 0 ? $" {plan.Skipped.Count} item(s) skipped." : "";
        RunOnMain(() =>
        {
            SetStatus($"Synced.{skippedSummary}", InfoColor);
            RenderList();
        });

        if (plan.Orphans.Count > 0)
        {
            var names = string.Join("\n", plan.Orphans.Select(o => "- " + o.DisplayName));
            RunOnMain(() =>
                ConfirmationRequested?.Invoke(
                    $"These mods are no longer subscribed on Steam and will be removed:\n{names}",
                    () => _ = Task.Run(() => RemoveOrphansAsync(conn, plan)),
                    null
                )
            );
        }
    }

    private async Task RemoveOrphansAsync(SteamConnection conn, WorkshopSyncPlan plan)
    {
        var orphanPlan = new WorkshopSyncPlan { Orphans = plan.Orphans };
        try
        {
            await WorkshopSyncService.ExecuteAsync(conn, orphanPlan, removeOrphans: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Workshop] Orphan removal failed: {ex.Message}");
        }
        RunOnMain(RenderList);
    }

    // Must run on the main thread. Also called by ModManagerSection on queue
    // Changed events while this tab is visible, to reflect live download progress.
    public void RenderList()
    {
        ClearList();

        if (!_loggedIn)
        {
            var loginLabel = new StyledLabel(
                "Steam login is required for Workshop features.",
                _scale,
                fontSize: 12
            );
            loginLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _list.AddChild(loginLabel);
            return;
        }

        var cfg = ModConfig.Load();
        var scanned = ModScanner.Scan();
        var scannedById = scanned
            .Where(s => s.Id != null)
            .GroupBy(s => s.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var queueByPfid = (_queue?.Entries ?? Array.Empty<WorkshopDownloadEntry>()).ToDictionary(
            e => e.Item.PublishedFileId,
            e => e
        );

        var workshopMods = cfg.Mods.Where(m => m.IsWorkshop).OrderBy(m => m.Id, StringComparer.Ordinal).ToList();
        if (workshopMods.Count == 0)
        {
            var empty = new StyledLabel("No Workshop subscriptions installed.", _scale, fontSize: 12);
            empty.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _list.AddChild(empty);
            return;
        }

        foreach (var entry in workshopMods)
        {
            scannedById.TryGetValue(entry.Id, out var info);
            queueByPfid.TryGetValue(entry.PublishedFileId, out var qEntry);

            string status;
            bool isError = false;
            if (qEntry != null && qEntry.State == WorkshopDownloadState.Downloading)
                status = $"Downloading {qEntry.ProgressPercent:F0}%";
            else if (qEntry != null && qEntry.State == WorkshopDownloadState.Failed)
            {
                status = $"Failed: {qEntry.Error}";
                isError = true;
            }
            else if (qEntry != null && qEntry.State == WorkshopDownloadState.Queued)
                status = "Queued";
            else if (_updateAvailablePfids.Contains(entry.PublishedFileId))
                status = "Update available";
            else if (info != null)
                status = "Installed";
            else
                status = "Pending download";

            var title = info?.Manifest?.DisplayName ?? entry.Id;
            var version = info?.Manifest?.Version;
            var row = new SubscribedModRow(title, version, status, isError, _scale);
            var capturedEntry = entry;
            row.UnsubscribePressed += () => OnUnsubscribePressed(capturedEntry);
            _list.AddChild(row);
        }
    }

    private void OnUnsubscribePressed(ModConfigEntry entry) =>
        ConfirmationRequested?.Invoke(
            $"Unsubscribe from '{entry.Id}'? This removes the mod from your device.",
            () => _ = Task.Run(() => DoUnsubscribeAsync(entry)),
            null
        );

    private async Task DoUnsubscribeAsync(ModConfigEntry entry)
    {
        if (_connection == null)
            return;
        try
        {
            await WorkshopSyncService
                .UnsubscribeAndRemoveAsync(_connection, entry.PublishedFileId)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Workshop] SUBSCRIBED unsubscribe failed: {ex.Message}");
        }
        RunOnMain(RenderList);
    }

    // Must run on the main thread.
    private void ClearList()
    {
        foreach (var child in _list.GetChildren().ToList())
        {
            _list.RemoveChild(child);
            child.QueueFree();
        }
    }

    // Must run on the main thread.
    private void SetStatus(string text, Color color)
    {
        _statusLabel.Text = text;
        _statusLabel.AddThemeColorOverride("font_color", color);
    }

    private static void RunOnMain(Action action) => Callable.From(action).CallDeferred();
}
