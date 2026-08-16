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

    // Subscribed items whose mod id is already installed from another source
    // (e.g. a manual install). Not re-downloaded; surfaced so the UI can tell the
    // user the Workshop copy isn't being applied.
    public List<WorkshopConflictItem> Conflicts = new();

    // Subscribed items that are stashed (disabled) locally AND have a newer
    // version on the Workshop. Deliberately NOT downloaded — installing would
    // land in Mods/ and silently un-stash them. The UI badges these and asks for
    // consent when the user enables the mod (issue #58 stash policy).
    public List<WorkshopItemDetails> DisabledUpdates = new();

    public bool HasInstallWork => ToInstall.Count > 0 || ToUpdate.Count > 0;
    public bool HasOrphans => Orphans.Count > 0;
    public bool HasAnyWork => HasInstallWork || Orphans.Count > 0 || StaleEntries.Count > 0;
}

// A subscribed item skipped because its mod id is already installed from another
// source (manual, or a differently-named folder holding the same id). Carries both
// versions so the UI can show whether the installed (manual) copy has gone stale
// relative to the Workshop copy, and offer to switch.
public class WorkshopConflictItem
{
    public ulong PublishedFileId;
    public string ModId;
    public string Title;
    public string WorkshopVersion; // from the remembered conflict record
    public string InstalledVersion; // filled from the live on-disk scan
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
    public IEnumerable<WorkshopSyncItemResult> EntriesCleaned =>
        Of(WorkshopSyncAction.EntryCleaned);
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
            scanned.Where(e => e.Id != null && !e.Disabled).Select(e => e.Id),
            StringComparer.Ordinal
        );
        var disabledIds = new HashSet<string>(
            scanned.Where(e => e.Id != null && e.Disabled).Select(e => e.Id),
            StringComparer.Ordinal
        );
        var byId = scanned
            .Where(e => e.Id != null)
            .GroupBy(e => e.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var plan = ComputePlan(
            subscriptions,
            cfg.Mods,
            installedIds,
            id =>
                byId.TryGetValue(id, out var e) && e.Manifest?.DisplayName != null
                    ? e.Manifest.DisplayName
                    : id,
            cfg.ConflictRecords,
            disabledIds
        );

        // Fill the installed (manual) version from the live scan so the UI can
        // compare it against the remembered Workshop version.
        foreach (var c in plan.Conflicts)
        {
            if (c.ModId != null && byId.TryGetValue(c.ModId, out var info))
                c.InstalledVersion = info.Manifest?.Version;
        }

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
                + $"skipped={plan.Skipped.Count} conflicts={plan.Conflicts.Count}"
        );
        return plan;
    }

    // Pure diff. Side-effect free and injectable so the safety invariants can be
    // tested deterministically without Android storage.
    public static WorkshopSyncPlan ComputePlan(
        IReadOnlyList<WorkshopItemDetails> subscriptions,
        IEnumerable<ModConfigEntry> localEntries,
        ISet<string> installedFolderIds,
        Func<string, string> displayNameResolver = null,
        IReadOnlyList<WorkshopConflictEntry> conflicts = null,
        ISet<string> disabledFolderIds = null
    )
    {
        disabledFolderIds ??= new HashSet<string>(StringComparer.Ordinal);
        var plan = new WorkshopSyncPlan();
        displayNameResolver ??= id => id;

        var conflictByPfid = new Dictionary<ulong, WorkshopConflictEntry>();
        if (conflicts != null)
        {
            foreach (var c in conflicts)
                if (c != null && c.PublishedFileId != 0)
                    conflictByPfid[c.PublishedFileId] = c;
        }

        var subsById = new Dictionary<ulong, WorkshopItemDetails>();
        foreach (var s in subscriptions)
        {
            if (s.PublishedFileId != 0 && !subsById.ContainsKey(s.PublishedFileId))
                subsById[s.PublishedFileId] = s;
        }

        var workshopEntries = localEntries
            .Where(e => e != null && e.IsWorkshop && e.PublishedFileId != 0)
            .ToList();

        // Recover pre-existing pollution: duplicate entries sharing one pfid are
        // residue of a manifest-id rename that happened before rename detection
        // existed in WorkshopInstaller.Install (issue #70). Pick one winner per
        // pfid — folder-present beats folder-absent, then higher TimeUpdated,
        // then later position in the input list — and route every loser through
        // the existing Orphans/StaleEntries paths so already-affected users
        // self-heal (Orphans still requires the caller's removeOrphans
        // confirmation before anything is deleted).
        var duplicateGroups = workshopEntries
            .GroupBy(e => e.PublishedFileId)
            .Where(g => g.Count() > 1)
            .ToList();
        if (duplicateGroups.Count > 0)
        {
            var losers = new HashSet<ModConfigEntry>();
            foreach (var group in duplicateGroups)
            {
                var ranked = group
                    .Select(
                        (e, idx) =>
                            (
                                Entry: e,
                                Idx: idx,
                                FolderPresent: installedFolderIds.Contains(e.Id)
                                    || disabledFolderIds.Contains(e.Id)
                            )
                    )
                    .OrderByDescending(x => x.FolderPresent)
                    .ThenByDescending(x => x.Entry.TimeUpdated)
                    .ThenByDescending(x => x.Idx)
                    .ToList();

                foreach (var loser in ranked.Skip(1))
                {
                    losers.Add(loser.Entry);
                    var mod = new WorkshopLocalMod
                    {
                        Id = loser.Entry.Id,
                        PublishedFileId = loser.Entry.PublishedFileId,
                        DisplayName = displayNameResolver(loser.Entry.Id),
                    };
                    if (
                        installedFolderIds.Contains(loser.Entry.Id)
                        || disabledFolderIds.Contains(loser.Entry.Id)
                    )
                        plan.Orphans.Add(mod);
                    else
                        plan.StaleEntries.Add(mod);
                }
            }
            // Everything downstream (localByPfid, the subscription-driven loop,
            // the local-driven loop) must see only the winners — losers are
            // already accounted for above and must not be double-added there.
            workshopEntries = workshopEntries.Where(e => !losers.Contains(e)).ToList();
        }

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

            // Stashed (disabled) locally: never download — the install would land in
            // Mods/ and silently un-stash the mod. If the Workshop copy is newer,
            // record it so the UI can badge it and ask on enable.
            if (entry != null && !folderPresent && disabledFolderIds.Contains(entry.Id))
            {
                if (sub.TimeUpdated > entry.TimeUpdated)
                    plan.DisabledUpdates.Add(sub);
                continue;
            }

            // Known id-collision with an already-installed mod: don't re-download
            // unless the item has been updated on Steam since we last saw it
            // (time_updated advanced). Prevents the every-visit re-download loop
            // for a subscribed item that can never be committed.
            if (
                (entry == null || !folderPresent)
                && conflictByPfid.TryGetValue(sub.PublishedFileId, out var conf)
                && conf.TimeUpdated >= sub.TimeUpdated
            )
            {
                plan.Conflicts.Add(
                    new WorkshopConflictItem
                    {
                        PublishedFileId = sub.PublishedFileId,
                        ModId = conf.ModId,
                        Title = sub.Title,
                        WorkshopVersion = conf.WorkshopVersion,
                    }
                );
                continue;
            }

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

            // A stashed folder counts as present too — unsubscribing while disabled
            // still needs the (confirmed) folder cleanup, just from ModsDisabled/.
            if (installedFolderIds.Contains(entry.Id) || disabledFolderIds.Contains(entry.Id))
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

            var action =
                install.Success
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
        // Drop any remembered id-collision for this item so it doesn't linger.
        cfg.ClearConflict(publishedFileId);
        // ALL entries sharing this pfid, not just one: a manifest-id rename
        // (issue #70) that occurred before rename detection existed (or whose
        // cleanup failed) can leave two config entries pointing at the same pfid.
        // A FirstOrDefault here would remove one and strand the other forever.
        var entries = cfg
            .Mods.Where(m => m.IsWorkshop && m.PublishedFileId == publishedFileId)
            .ToList();
        if (entries.Count == 0)
        {
            PatchHelper.Log(
                $"[Workshop] Unsubscribed {publishedFileId}; no local workshop entry to remove."
            );
            cfg.Save();
            return true;
        }

        var result = new WorkshopSyncResult();
        bool allRemoved = true;
        foreach (var entry in entries)
        {
            var mod = new WorkshopLocalMod { Id = entry.Id, PublishedFileId = publishedFileId };
            if (!RemoveWorkshopMod(cfg, mod, deleteFolder: true, result))
                allRemoved = false;
        }
        cfg.Save();
        return allRemoved;
    }

    // Conflict resolution — "use the Workshop copy", step 1 of 2. The user has
    // both a manual install and a subscription of the same mod id; this removes
    // the manual copy/copies (a confirmed, destructive-to-the-manual-folder
    // action) and clears the remembered conflict, then returns the item details.
    // The CALLER downloads it — via the shared WorkshopDownloadQueue, so progress
    // shows in the Downloads tab and the same item can't be downloaded twice
    // concurrently. Guarded to Mods/ direct children with valid ids only.
    public static async Task<(WorkshopItemDetails Item, string Error)> PrepareUseWorkshopAsync(
        SteamConnection conn,
        ulong publishedFileId,
        CancellationToken ct = default
    )
    {
        if (conn == null)
            throw new ArgumentNullException(nameof(conn));

        var details = await conn.GetPublishedFileDetailsAsync(new[] { publishedFileId })
            .ConfigureAwait(false);
        var item = details.FirstOrDefault(d =>
            d.PublishedFileId == publishedFileId && !string.IsNullOrEmpty(d.Title)
        );
        if (item == null)
            return (null, "Workshop item not found or inaccessible.");
        ct.ThrowIfCancellationRequested();

        var cfg = ModConfig.Load();
        var conf = cfg.ConflictRecords.FirstOrDefault(c => c.PublishedFileId == publishedFileId);
        var targetId = conf?.ModId;
        if (targetId != null && ModIdValidator.IsValidId(targetId))
        {
            foreach (var m in ModScanner.Scan().Where(s => s.Id == targetId).ToList())
            {
                if (!AppPaths.IsDirectChildOfModsDir(m.TopLevelDir))
                    continue;
                try
                {
                    Directory.Delete(m.TopLevelDir, recursive: true);
                    PatchHelper.Log(
                        $"[Workshop] Conflict resolve: removed existing copy at {m.TopLevelDir}"
                    );
                }
                catch (Exception ex)
                {
                    PatchHelper.Log($"[Workshop] Conflict resolve delete failed: {ex.Message}");
                }
            }
            cfg.Remove(targetId); // drop the manual entry so the fresh install owns the id
        }
        cfg.ClearConflict(publishedFileId);
        cfg.Save();

        return (item, null);
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
        if (string.IsNullOrEmpty(mod.Id) || !ModIdValidator.IsValidId(mod.Id))
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
                // The mod may live in Mods/ OR in the stash (ModsDisabled/) — check
                // both roots. Each candidate is re-verified as a direct child of its
                // root before deleting, guarding against an id escaping the tree.
                foreach (var root in new[] { AppPaths.ExternalModsDir, AppPaths.DisabledModsDir })
                {
                    var dir = Path.Combine(root, mod.Id);
                    if (!AppPaths.IsDirectChildOf(root, dir))
                    {
                        PatchHelper.Log(
                            $"[Workshop] Refusing to delete '{dir}': not a direct child of {root}."
                        );
                        return false;
                    }
                    if (Directory.Exists(dir))
                        Directory.Delete(dir, recursive: true);
                }
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
                Action = deleteFolder
                    ? WorkshopSyncAction.Removed
                    : WorkshopSyncAction.EntryCleaned,
            }
        );
        return true;
    }
}
