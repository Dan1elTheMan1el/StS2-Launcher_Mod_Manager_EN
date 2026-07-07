using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using STS2Mobile.Modding;

namespace STS2Mobile.Steam;

// Outcome of executing a single item during a sync.
public enum WorkshopSyncAction
{
    Installed,
    Updated,
    Removed,
    EntryCleaned,
    Conflict,
    NoManifest,
    Failed,
}

// A local mod that originated from the Workshop (a source=workshop config entry).
// Describes removals/orphans to the UI without exposing the raw config entry.
public class WorkshopLocalMod
{
    public string Id;
    public ulong PublishedFileId;
    public string DisplayName;
}

// A subscribed item deliberately left out of install/update, with the reason so
// the UI can explain it (e.g. banned, no downloadable content).
public class WorkshopSkippedItem
{
    public ulong PublishedFileId;
    public string Title;
    public string Reason;
}

// The three-way diff of subscriptions vs local config entries vs folders on disk.
// Installs/updates are applied automatically by ExecuteAsync; orphan removals are
// gated behind an explicit removeOrphans flag (they delete user-visible folders).
public class WorkshopSyncPlan
{
    // Subscribed but not installed, or the entry exists yet the folder is gone.
    public List<WorkshopItemDetails> ToInstall = new();

    // Subscribed and installed, but the subscription's time_updated is newer.
    public List<WorkshopItemDetails> ToUpdate = new();

    // source=workshop entry whose id is no longer subscribed AND whose folder is
    // present → delete folder + entry, but only after the caller confirms.
    public List<WorkshopLocalMod> Orphans = new();

    // source=workshop entry whose folder is already gone → drop the stale entry
    // (nothing to delete; always safe to clean).
    public List<WorkshopLocalMod> StaleEntries = new();

    // Subscribed items excluded from install/update, with a reason.
    public List<WorkshopSkippedItem> Skipped = new();

    public bool HasInstallWork => ToInstall.Count > 0 || ToUpdate.Count > 0;
    public bool HasOrphans => Orphans.Count > 0;
    public bool HasAnyWork =>
        HasInstallWork || Orphans.Count > 0 || StaleEntries.Count > 0;
}

public class WorkshopSyncItemResult
{
    public ulong PublishedFileId;
    public string ModId;
    public string Title;
    public WorkshopSyncAction Action;
    public string Error;
}

public class WorkshopSyncResult
{
    public List<WorkshopSyncItemResult> Items = new();

    private IEnumerable<WorkshopSyncItemResult> Of(WorkshopSyncAction a) =>
        Items.Where(i => i.Action == a);

    public IEnumerable<WorkshopSyncItemResult> Installed => Of(WorkshopSyncAction.Installed);
    public IEnumerable<WorkshopSyncItemResult> Updated => Of(WorkshopSyncAction.Updated);
    public IEnumerable<WorkshopSyncItemResult> Removed => Of(WorkshopSyncAction.Removed);
    public IEnumerable<WorkshopSyncItemResult> EntriesCleaned => Of(WorkshopSyncAction.EntryCleaned);
    public IEnumerable<WorkshopSyncItemResult> Conflicts => Of(WorkshopSyncAction.Conflict);
    public IEnumerable<WorkshopSyncItemResult> NoManifest => Of(WorkshopSyncAction.NoManifest);
    public IEnumerable<WorkshopSyncItemResult> Failed => Of(WorkshopSyncAction.Failed);
}

// Reconciles the user's Steam Workshop subscriptions with the launcher's local mod
// state (issue #58 stage 3). Steam pushes no subscription-change events, so the UI
// polls this once on entry to the Mod Hub. Engine only — no Godot/UI dependency;
// stage 4 consumes this API.
//
// SAFETY (this deletes mod folders):
//   * Only entries whose ModConfig source == "workshop" are ever removable. Manual
//     mods are structurally invisible to the removal path — the Mods/ mirror-delete
//     prohibition is absolute.
//   * A failed subscription poll throws (GetSubscribedFilesAsync surfaces RPC
//     errors); it never degrades to an empty list, so a network blip cannot be read
//     as "unsubscribed from everything".
//   * Even a genuinely empty subscription list deletes nothing on its own: orphan
//     removal only runs when ExecuteAsync is called with removeOrphans==true (the UI
//     sets that after confirming with the user). StaleEntries only drop json entries
//     whose folder is already gone.
public static class WorkshopSyncService
{
    // Polls subscriptions and diffs them against on-disk mods + mod_config.json. No
    // side effects. Throws if the subscription poll fails (by design — see the class
    // safety note).
    public static async Task<WorkshopSyncPlan> ComputePlanAsync(
        SteamConnection conn,
        CancellationToken ct = default
    )
    {
        if (conn == null)
            throw new ArgumentNullException(nameof(conn));

        var subscriptions = await conn.GetSubscribedFilesAsync().ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var cfg = ModConfig.Load();
        var scanned = ModScanner.Scan();
        var installedIds = new HashSet<string>(
            scanned.Where(e => e.Id != null).Select(e => e.Id),
            StringComparer.Ordinal
        );
        var displayNames = scanned
            .Where(e => e.Id != null)
            .GroupBy(e => e.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Manifest?.DisplayName, StringComparer.Ordinal);

        var plan = ComputePlan(
            subscriptions,
            cfg.Mods,
            installedIds,
            id => displayNames.TryGetValue(id, out var n) && n != null ? n : id
        );

        if (subscriptions.Count == 0 && (plan.Orphans.Count > 0 || plan.StaleEntries.Count > 0))
        {
            PatchHelper.Log(
                "[Workshop] Subscription poll returned 0 items while local workshop mods exist — "
                    + "treating as unsubscribe-all, but orphan removal still requires an explicit "
                    + "removeOrphans confirmation."
            );
        }

        PatchHelper.Log(
            $"[Workshop] Sync plan: install={plan.ToInstall.Count} update={plan.ToUpdate.Count} "
                + $"orphans={plan.Orphans.Count} staleEntries={plan.StaleEntries.Count} "
                + $"skipped={plan.Skipped.Count}"
        );
        return plan;
    }

    // Pure diff. Side-effect free and injectable so the safety invariants can be
    // tested deterministically without Android storage.
    public static WorkshopSyncPlan ComputePlan(
        IReadOnlyList<WorkshopItemDetails> subscriptions,
        IEnumerable<ModConfigEntry> localEntries,
        ISet<string> installedFolderIds,
        Func<string, string> displayNameResolver = null
    )
    {
        var plan = new WorkshopSyncPlan();
        displayNameResolver ??= id => id;

        var subsById = new Dictionary<ulong, WorkshopItemDetails>();
        foreach (var s in subscriptions)
        {
            if (s.PublishedFileId != 0 && !subsById.ContainsKey(s.PublishedFileId))
                subsById[s.PublishedFileId] = s;
        }

        var workshopEntries = localEntries
            .Where(e => e != null && e.IsWorkshop && e.PublishedFileId != 0)
            .ToList();
        var localByPfid = new Dictionary<ulong, ModConfigEntry>();
        foreach (var e in workshopEntries)
            localByPfid[e.PublishedFileId] = e;

        // Subscription-driven: install new, re-install missing, update stale.
        foreach (var sub in subsById.Values)
        {
            if (!IsDownloadable(sub, out var reason))
            {
                plan.Skipped.Add(
                    new WorkshopSkippedItem
                    {
                        PublishedFileId = sub.PublishedFileId,
                        Title = sub.Title,
                        Reason = reason,
                    }
                );
                continue;
            }

            localByPfid.TryGetValue(sub.PublishedFileId, out var entry);
            bool folderPresent = entry != null && installedFolderIds.Contains(entry.Id);

            if (entry == null || !folderPresent)
                plan.ToInstall.Add(sub);
            else if (sub.TimeUpdated > entry.TimeUpdated)
                plan.ToUpdate.Add(sub);
        }

        // Local-driven: workshop entries no longer subscribed.
        foreach (var entry in workshopEntries)
        {
            if (subsById.ContainsKey(entry.PublishedFileId))
                continue;

            var mod = new WorkshopLocalMod
            {
                Id = entry.Id,
                PublishedFileId = entry.PublishedFileId,
                DisplayName = displayNameResolver(entry.Id),
            };

            if (installedFolderIds.Contains(entry.Id))
                plan.Orphans.Add(mod); // folder present → delete after confirmation
            else
                plan.StaleEntries.Add(mod); // folder already gone → entry cleanup only
        }

        return plan;
    }

    // A subscribed item is downloadable when Steam actually returns content for it
    // and it isn't banned. Visibility is intentionally NOT hard-filtered: a user
    // subscribed to an unlisted/friends item can still receive it, and the absence
    // of content (HasContent==false) already catches anything Steam won't serve.
    private static bool IsDownloadable(WorkshopItemDetails item, out string reason)
    {
        if (item.Banned)
        {
            reason = string.IsNullOrEmpty(item.BanReason)
                ? "item is banned on the Workshop"
                : $"item is banned: {item.BanReason}";
            return false;
        }
        if (!item.HasContent)
        {
            reason = "no downloadable content (item may be hidden or removed)";
            return false;
        }
        reason = null;
        return true;
    }

    // Applies a plan: installs/updates sequentially (per-item failures are recorded
    // and the run continues), then removes orphans (only if removeOrphans) and always
    // cleans stale entries. Every removal re-verifies source=workshop against a fresh
    // config before touching disk.
    public static async Task<WorkshopSyncResult> ExecuteAsync(
        SteamConnection conn,
        WorkshopSyncPlan plan,
        bool removeOrphans,
        IProgress<DownloadProgress> progress = null,
        CancellationToken ct = default
    )
    {
        if (conn == null)
            throw new ArgumentNullException(nameof(conn));
        if (plan == null)
            throw new ArgumentNullException(nameof(plan));

        var result = new WorkshopSyncResult();

        var updateSet = new HashSet<ulong>(plan.ToUpdate.Select(i => i.PublishedFileId));
        foreach (var item in plan.ToInstall.Concat(plan.ToUpdate))
        {
            ct.ThrowIfCancellationRequested();
            bool isUpdate = updateSet.Contains(item.PublishedFileId);

            WorkshopInstaller.WorkshopInstallResult install;
            try
            {
                install = await WorkshopInstaller
                    .DownloadAndInstallAsync(conn, item, progress, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                PatchHelper.Log(
                    $"[Workshop] {(isUpdate ? "Update" : "Install")} failed for "
                        + $"{item.PublishedFileId}: {ex.Message}"
                );
                result.Items.Add(
                    new WorkshopSyncItemResult
                    {
                        PublishedFileId = item.PublishedFileId,
                        Title = item.Title,
                        Action = WorkshopSyncAction.Failed,
                        Error = ex.Message,
                    }
                );
                continue;
            }

            var action = install.Success
                ? (isUpdate ? WorkshopSyncAction.Updated : WorkshopSyncAction.Installed)
                : install.Conflict ? WorkshopSyncAction.Conflict
                : install.NoManifest ? WorkshopSyncAction.NoManifest
                : WorkshopSyncAction.Failed;

            result.Items.Add(
                new WorkshopSyncItemResult
                {
                    PublishedFileId = item.PublishedFileId,
                    ModId = install.ModId,
                    Title = item.Title,
                    Action = action,
                    Error = install.Error,
                }
            );
        }

        // Removals: reload config AFTER installs (they mutated mod_config.json).
        var cfg = ModConfig.Load();
        bool changed = false;

        if (removeOrphans)
        {
            foreach (var mod in plan.Orphans)
            {
                if (RemoveWorkshopMod(cfg, mod, deleteFolder: true, result))
                    changed = true;
            }
        }
        foreach (var mod in plan.StaleEntries)
        {
            if (RemoveWorkshopMod(cfg, mod, deleteFolder: false, result))
                changed = true;
        }

        if (changed)
            cfg.Save();

        return result;
    }

    // In-app unsubscribe: unsubscribes on Steam, then removes the matching local
    // workshop mod (folder + entry). The user's click is the confirmation. If the
    // unsubscribe RPC fails the exception propagates and nothing local is touched —
    // we never delete a mod that is still subscribed. Returns true if the local mod
    // was removed (or there was nothing local to remove); false if a removal was
    // attempted but failed.
    public static async Task<bool> UnsubscribeAndRemoveAsync(
        SteamConnection conn,
        ulong publishedFileId,
        CancellationToken ct = default
    )
    {
        if (conn == null)
            throw new ArgumentNullException(nameof(conn));

        await conn.SetSubscriptionAsync(publishedFileId, subscribe: false).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var cfg = ModConfig.Load();
        var entry = cfg.Mods.FirstOrDefault(m =>
            m.IsWorkshop && m.PublishedFileId == publishedFileId
        );
        if (entry == null)
        {
            PatchHelper.Log(
                $"[Workshop] Unsubscribed {publishedFileId}; no local workshop entry to remove."
            );
            return true;
        }

        var mod = new WorkshopLocalMod { Id = entry.Id, PublishedFileId = publishedFileId };
        var result = new WorkshopSyncResult();
        bool removed = RemoveWorkshopMod(cfg, mod, deleteFolder: true, result);
        if (removed)
            cfg.Save();
        return removed;
    }

    // Guarded single-mod removal. Verifies the current config entry is source=workshop
    // before deleting — the hard gate against ever removing a manual mod.
    private static bool RemoveWorkshopMod(
        ModConfig cfg,
        WorkshopLocalMod mod,
        bool deleteFolder,
        WorkshopSyncResult result
    )
    {
        if (string.IsNullOrEmpty(mod.Id) || !IsValidId(mod.Id))
        {
            PatchHelper.Log($"[Workshop] Refusing removal of invalid id '{mod.Id}'");
            return false;
        }

        // Hard gate: only an entry that currently exists AND is source=workshop may
        // be removed. A null entry means the registry no longer tracks this id — a
        // folder with that name could be anything (e.g. a manual install that never
        // gets a registry entry), so refuse rather than delete on a stale plan.
        var entry = cfg.Get(mod.Id);
        if (entry == null || !entry.IsWorkshop)
        {
            PatchHelper.Log(
                $"[Workshop] Skipping removal of '{mod.Id}': no current source=workshop entry."
            );
            return false;
        }

        if (deleteFolder)
        {
            try
            {
                var dir = Path.Combine(AppPaths.ExternalModsDir, mod.Id);
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex)
            {
                PatchHelper.Log($"[Workshop] Failed to delete folder for '{mod.Id}': {ex.Message}");
                result.Items.Add(
                    new WorkshopSyncItemResult
                    {
                        PublishedFileId = mod.PublishedFileId,
                        ModId = mod.Id,
                        Title = mod.DisplayName,
                        Action = WorkshopSyncAction.Failed,
                        Error = ex.Message,
                    }
                );
                return false;
            }
        }

        cfg.Remove(mod.Id);
        PatchHelper.Log(
            $"[Workshop] {(deleteFolder ? "Removed" : "Cleaned entry for")} workshop mod "
                + $"'{mod.Id}' (pfid={mod.PublishedFileId})"
        );
        result.Items.Add(
            new WorkshopSyncItemResult
            {
                PublishedFileId = mod.PublishedFileId,
                ModId = mod.Id,
                Title = mod.DisplayName,
                Action = deleteFolder ? WorkshopSyncAction.Removed : WorkshopSyncAction.EntryCleaned,
            }
        );
        return true;
    }

    private static bool IsValidId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;
        foreach (var c in id)
        {
            if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.'))
                return false;
        }
        return true;
    }
}
