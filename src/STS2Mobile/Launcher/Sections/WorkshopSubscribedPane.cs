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

    private static readonly Color InfoColor = Ui.TextSecondary;
    private static readonly Color WarnColor = Ui.Warn;

    private readonly float _scale;
    private readonly StyledLabel _statusLabel;
    private readonly VBoxContainer _list;

    private SteamConnection _connection;
    private WorkshopDownloadQueue _queue;
    private HashSet<ulong> _updateAvailablePfids = new();
    private List<WorkshopConflictItem> _conflicts = new();
    private Func<Task<(bool ok, SteamConnection conn)>> _ensureSession;
    private bool _loggedIn;
    private long _lastSyncTick;

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
    public void Activate(Func<Task<(bool ok, SteamConnection conn)>> ensureSession)
    {
        _ensureSession = ensureSession;
        _ = Task.Run(() => SyncAsync(ensureSession));
    }

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

        // Debounce full re-syncs on rapid tab flapping: within 15s of the last
        // successful sync, just re-render current state (registry + queue). The
        // idle-suspended connection stays warm, so a later real sync is cheap.
        if (System.Environment.TickCount64 - _lastSyncTick < 15_000)
        {
            RunOnMain(() =>
            {
                SetStatus("Synced.", InfoColor);
                RenderList();
            });
            return;
        }

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

        _lastSyncTick = System.Environment.TickCount64;
        var toDownload = plan.ToInstall.Concat(plan.ToUpdate).ToList();
        if (_queue != null)
        {
            foreach (var item in toDownload)
                _queue.Enqueue(item);
        }
        _updateAvailablePfids = new HashSet<ulong>(plan.ToUpdate.Select(i => i.PublishedFileId));
        _conflicts = plan.Conflicts;

        // Tell the user what auto-download just started (issue #58): a scrollable
        // list of the new/updated mods, queued to the Downloads tab.
        if (toDownload.Count > 0)
        {
            var titles = toDownload
                .Select(i => string.IsNullOrEmpty(i.Title) ? i.PublishedFileId.ToString() : i.Title)
                .ToList();
            int newCount = plan.ToInstall.Count;
            int updCount = plan.ToUpdate.Count;
            var header =
                updCount == 0
                    ? $"{newCount} new Workshop mod(s) detected — downloading:"
                    : newCount == 0
                        ? $"{updCount} Workshop mod update(s) detected — downloading:"
                        : $"{newCount} new + {updCount} updated Workshop mod(s) — downloading:";
            RunOnMain(() =>
            {
                var dialog = new WorkshopUpdateDialog(header, titles, _scale);
                LauncherOverlay.Show(this, dialog);
            });
        }

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

        // Subscribed items still in flight (queued/downloading/failed) that have no
        // registry entry yet — without these rows a fresh subscription is invisible
        // here until its install completes (the original "BaseLib doesn't show"
        // report).
        var registryPfids = new HashSet<ulong>(workshopMods.Select(m => m.PublishedFileId));
        var pending = queueByPfid
            .Values.Where(e =>
                !registryPfids.Contains(e.Item.PublishedFileId)
                && e.State != WorkshopDownloadState.Completed
            )
            .OrderBy(e => e.Item.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (workshopMods.Count == 0 && pending.Count == 0 && (_conflicts?.Count ?? 0) == 0)
        {
            _list.AddChild(
                Ui.MakeEmptyState(
                    null,
                    "No Workshop subscriptions yet.",
                    "Browse the WORKSHOP tab and subscribe — items download automatically.",
                    _scale
                )
            );
            return;
        }

        // Chunk the list (Miller): in-flight first, then installed, then conflicts
        // — each under its own header when the list is mixed.
        bool mixed = pending.Count > 0 && workshopMods.Count > 0;
        if (pending.Count > 0 && (mixed || (_conflicts?.Count ?? 0) > 0))
            _list.AddChild(Ui.MakeSectionHeader("IN PROGRESS", _scale));

        foreach (var q in pending)
        {
            string status;
            bool isError = false;
            switch (q.State)
            {
                case WorkshopDownloadState.Downloading:
                    status = $"Downloading {q.ProgressPercent:F0}%";
                    break;
                case WorkshopDownloadState.Failed:
                    status = $"Failed: {q.Error}";
                    isError = true;
                    break;
                default:
                    status = "Queued";
                    break;
            }

            var item = q.Item;
            var row = new SubscribedModRow(
                string.IsNullOrEmpty(item.Title) ? item.PublishedFileId.ToString() : item.Title,
                null,
                status,
                isError,
                _scale
            );
            row.UnsubscribePressed += () => OnUnsubscribePfidPressed(item);
            row.DetailRequested += () => ShowItemDetail(item);
            _list.AddChild(row);
        }

        if (mixed || (workshopMods.Count > 0 && (_conflicts?.Count ?? 0) > 0))
            _list.AddChild(Ui.MakeSectionHeader("INSTALLED", _scale));

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
            var capturedInfo = info;
            row.UnsubscribePressed += () => OnUnsubscribePressed(capturedEntry);
            row.DetailRequested += () => ShowSubscribedDetail(capturedEntry, capturedInfo);
            _list.AddChild(row);
        }

        RenderConflicts();
    }

    // Subscribed items whose mod id is also installed manually. The Workshop copy
    // isn't applied (we won't overwrite a manual install); show the version drift
    // so the user isn't silently stuck on a stale copy, and offer a one-tap switch
    // to the Workshop version.
    private void RenderConflicts()
    {
        if (_conflicts == null || _conflicts.Count == 0)
            return;

        _list.AddChild(Ui.MakeSectionHeader("ALSO INSTALLED MANUALLY — WORKSHOP COPY NOT APPLIED", _scale));

        foreach (var c in _conflicts)
        {
            var panel = new PanelContainer();
            panel.AddThemeStyleboxOverride("panel", Ui.TintedCardStyle(_scale, Ui.Warn));

            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", (int)(8 * _scale));
            panel.AddChild(row);

            var info = new VBoxContainer();
            info.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.AddChild(info);

            var titleLabel = new StyledLabel(
                c.Title ?? c.ModId,
                _scale,
                fontSize: 13,
                align: HorizontalAlignment.Left
            );
            titleLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            info.AddChild(titleLabel);

            var installed = string.IsNullOrEmpty(c.InstalledVersion)
                ? "v?"
                : LauncherModel.VersionLabel(c.InstalledVersion);
            var workshop = string.IsNullOrEmpty(c.WorkshopVersion)
                ? "v?"
                : LauncherModel.VersionLabel(c.WorkshopVersion);
            var cmp = CompareVersions(c.WorkshopVersion, c.InstalledVersion);
            var note =
                cmp > 0 ? " — Workshop is newer"
                : cmp < 0 ? " — your copy is newer"
                : " — same version";
            var verLabel = new StyledLabel(
                $"installed {installed} · Workshop {workshop}{note}",
                _scale,
                fontSize: Ui.FontMicro,
                align: HorizontalAlignment.Left
            );
            verLabel.AddThemeColorOverride(
                "font_color",
                cmp > 0 ? Ui.Warn : Ui.TextSecondary
            );
            info.AddChild(verLabel);

            var useBtn = new StyledButton(
                "USE WORKSHOP",
                _scale,
                fontSize: Ui.FontCaption,
                height: 44,
                variant: ButtonVariant.Primary
            );
            useBtn.CustomMinimumSize = new Vector2((int)(150 * _scale), (int)(44 * _scale));
            var captured = c;
            useBtn.Pressed += () => OnUseWorkshopPressed(captured);
            row.AddChild(useBtn);

            _list.AddChild(panel);
        }
    }

    private void OnUseWorkshopPressed(WorkshopConflictItem c) =>
        ConfirmationRequested?.Invoke(
            $"Replace your manually installed '{c.ModId}' with the Workshop version "
                + $"({(string.IsNullOrEmpty(c.WorkshopVersion) ? "v?" : LauncherModel.VersionLabel(c.WorkshopVersion))})?\n"
                + "Your manual copy's folder will be removed.",
            () => _ = Task.Run(() => DoUseWorkshopAsync(c)),
            null
        );

    private async Task DoUseWorkshopAsync(WorkshopConflictItem c)
    {
        if (_connection == null)
            return;
        RunOnMain(() => SetStatus($"Switching '{c.ModId}' to the Workshop version...", InfoColor));
        try
        {
            var (item, error) = await WorkshopSyncService
                .PrepareUseWorkshopAsync(_connection, c.PublishedFileId)
                .ConfigureAwait(false);
            if (item == null)
            {
                RunOnMain(() => SetStatus($"Switch failed: {error}", WarnColor));
                return;
            }

            // Download through the shared queue: progress shows in the Downloads
            // tab and the per-item gate/dedup prevents the double-download race a
            // direct download here used to cause.
            _conflicts.RemoveAll(x => x.PublishedFileId == c.PublishedFileId);
            if (_queue != null)
                _queue.Enqueue(item);
            else
                await WorkshopInstaller
                    .DownloadAndInstallAsync(_connection, item)
                    .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Workshop] Conflict resolve failed: {ex.Message}");
        }
        RunOnMain(RenderList);
    }

    // Compares dotted numeric versions ("0.2.0" vs "0.1.0"). Non-numeric segments
    // count as 0; a missing/blank version sorts lowest. Returns >0 if a>b.
    private static int CompareVersions(string a, string b)
    {
        var pa = (a ?? "").Split('.');
        var pb = (b ?? "").Split('.');
        int n = Math.Max(pa.Length, pb.Length);
        for (int i = 0; i < n; i++)
        {
            int va = i < pa.Length && int.TryParse(pa[i], out var x) ? x : 0;
            int vb = i < pb.Length && int.TryParse(pb[i], out var y) ? y : 0;
            if (va != vb)
                return va - vb;
        }
        return 0;
    }

    private void OnUnsubscribePressed(ModConfigEntry entry) =>
        ConfirmationRequested?.Invoke(
            $"Unsubscribe from '{entry.Id}'? This removes the mod from your device.",
            () => _ = Task.Run(() => DoUnsubscribeAsync(entry)),
            null
        );

    // Unsubscribe for an in-flight (not yet installed) subscription row.
    private void OnUnsubscribePfidPressed(WorkshopItemDetails item) =>
        ConfirmationRequested?.Invoke(
            $"Unsubscribe from '{item.Title}'?",
            () => _ = Task.Run(async () =>
            {
                if (_connection == null)
                    return;
                try
                {
                    await WorkshopSyncService
                        .UnsubscribeAndRemoveAsync(_connection, item.PublishedFileId)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    PatchHelper.Log($"[Workshop] Unsubscribe failed: {ex.Message}");
                }
                RunOnMain(RenderList);
            }),
            null
        );

    // Detail page for an in-flight subscription (Workshop metadata only — nothing
    // on disk yet).
    private void ShowItemDetail(WorkshopItemDetails item)
    {
        var facts = new List<(string, string)>
        {
            ("Size", LauncherModel.FormatSize((long)item.FileSize)),
            ("Workshop id", item.PublishedFileId.ToString()),
        };
        var dialog = new ModDetailDialog(
            item.Title,
            $"{item.Subscriptions} subscriber(s)",
            null,
            item.Description,
            facts,
            _scale
        );
        LauncherOverlay.Show(this, dialog);
    }

    private void ShowSubscribedDetail(ModConfigEntry entry, ModEntryInfo info)
    {
        var m = info?.Manifest;
        var title = m?.DisplayName ?? entry.Id;
        var subtitle = string.Join(
            " · ",
            new[]
            {
                string.IsNullOrWhiteSpace(m?.Author) ? null : "by " + m.Author,
                string.IsNullOrWhiteSpace(m?.Version) ? null : LauncherModel.VersionLabel(m.Version),
            }.Where(s => s != null)
        );

        var body = m?.Description ?? "";
        if (!string.IsNullOrWhiteSpace(info?.ReadmeSnippet))
            body = (body.Length > 0 ? body + "\n\n" : "") + "README: " + info.ReadmeSnippet;

        var facts = new List<(string, string)>
        {
            ("Source", "Steam Workshop"),
            ("Workshop id", entry.PublishedFileId.ToString()),
            ("Min game version", m?.MinGameVersion),
            ("Path", info?.Path),
        };

        var dialog = new ModDetailDialog(
            title,
            subtitle,
            null,
            body,
            facts,
            _scale,
            actionLabel: "Unsubscribe",
            actionCallback: () => OnUnsubscribePressed(entry),
            actionDanger: true
        );
        LauncherOverlay.Show(this, dialog);
    }

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
