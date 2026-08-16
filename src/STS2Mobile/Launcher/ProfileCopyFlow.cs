using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Saves;
using STS2Mobile.Launcher.Components;
using STS2Mobile.Patches;
using STS2Mobile.Steam;

namespace STS2Mobile.Launcher;

// Issue #64: orchestrates the "Profile Copy" (profile-slot copy) and "Backup Restore"
// (local backup restore) flows launched from the Save Manager screen — from
// ProfilePickerDialog's two extra buttons (cloud-available path) and from
// LocalOnlyMenuDialog (D7 local-only bypass path, cloudStore == null). Called by
// LauncherPatches.OpenSaveSyncDialogAsync; owns every dialog/BusyOverlay it opens.
public static class ProfileCopyFlow
{
    private const string Tag = "[Issue64]";

    // ---- profile copy ("Copy") --------------------------------------------------

    // Returns true if CopyProfile actually ran and succeeded — the caller uses
    // this to force a fresh CloudSyncDecisions.DeterminePerProfileAsync (the
    // Save Manager slot list is now stale), mirroring HandleProfileConflictAsync's
    // `applied` contract (LauncherPatches.cs:558-672).
    // cloudStore == null skips the cloud-reflect step (§3-2 step 7) entirely —
    // D7 bypass, called from the local-only menu.
    public static async Task<bool> RunCopyAsync(
        Node parent,
        ISaveStore localStore,
        SteamKit2CloudSaveStore cloudStore
    )
    {
        float scale = LauncherUI.ResolveScale(parent);
        float vpH = LauncherUI.ResolveViewportHeight(parent);

        List<SaveProgressSummary> slots;
        var busy = BusyOverlay.Show(parent, "Checking slot information...", scale);
        try
        {
            slots = await CloudSyncDecisions.SummarizeLocalSlotsAsync(localStore);
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"{Tag} RunCopyAsync: SummarizeLocalSlotsAsync failed: {ex.Message}");
            busy.Dismiss();
            await SimpleResultDialog.ShowAsync(
                parent,
                false,
                "Failed to check slot information.",
                scale
            );
            return false;
        }
        busy.Dismiss();

        var sourceCandidates = slots.Where(s => !s.IsEmpty).ToList();
        if (sourceCandidates.Count == 0)
        {
            await SimpleResultDialog.ShowAsync(
                parent,
                false,
                "There are no slots with data to copy.",
                scale
            );
            return false;
        }

        var srcPicker = new ProfileCopyPickerDialog(
            sourceCandidates,
            "Source Slot to Copy",
            "Select the profile to copy.",
            scale,
            vpH
        );
        parent.AddChild(srcPicker);
        var src = await srcPicker.Result;
        if (src == null)
            return false;

        // Prevent selecting the same slot from the beginning — exclude from list.
        var destCandidates = slots
            .Where(s => !(s.ProfileNumber == src.ProfileNumber && s.IsModded == src.IsModded))
            .ToList();
        var dstPicker = new ProfileCopyPickerDialog(
            destCandidates,
            "Target Slot to Overwrite",
            "Select the target profile to overwrite with the copy.",
            scale,
            vpH
        );
        parent.AddChild(dstPicker);
        var dst = await dstPicker.Result;
        if (dst == null)
            return false;

        // D1 — only a modded source copied onto a vanilla destination excludes
        // current_run(.save/_mp.save); every other direction copies it normally.
        bool excludesCurrentRun = src.IsModded && !dst.IsModded;
        var confirmMsg =
            $"Copy Profile {src.ProfileNumber}{(src.IsModded ? " · Modded" : "")} → "
            + $"Profile {dst.ProfileNumber}{(dst.IsModded ? " · Modded" : "")}.\n\n"
            + "Current data in the target slot will be overwritten. A local backup will be automatically created before proceeding.";
        if (excludesCurrentRun)
            confirmMsg += "\n\nThe run in progress (current_run) will not be copied.";

        if (!await ShowConfirmAsync(parent, confirmMsg, scale))
            return false;

        var busy2 = BusyOverlay.Show(parent, "Copying profile...", scale);
        ProfileCopyResult result;
        try
        {
            result = await RunBlockingAsync(() =>
                ProfileCopyService.CopyProfile(
                    localStore,
                    src.ProfileNumber,
                    src.IsModded,
                    dst.ProfileNumber,
                    dst.IsModded
                )
            );
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"{Tag} CopyProfile threw: {ex.Message}");
            busy2.Dismiss();
            await SimpleResultDialog.ShowAsync(
                parent,
                false,
                $"Error during copy: {ex.Message}",
                scale
            );
            return false;
        }
        busy2.Dismiss();

        if (!result.Success)
        {
            PatchHelper.Log(
                $"{Tag} CopyProfile failed: {result.Error} (NeedsPermission={result.NeedsPermission})"
            );
            if (result.NeedsPermission)
                AppPaths.RequestStoragePermission();
            await SimpleResultDialog.ShowAsync(
                parent,
                false,
                result.NeedsPermission
                    ? "Storage access permission is required to back up.\nGrant the permission and try again."
                    : (result.Error ?? "An error occurred during copying."),
                scale
            );
            return false;
        }

        PatchHelper.Log(
            $"{Tag} CopyProfile OK: profile{src.ProfileNumber}(modded={src.IsModded}) -> "
                + $"profile{dst.ProfileNumber}(modded={dst.IsModded}), {result.FileCount} files, "
                + $"{result.TotalBytes}B, currentRunExcluded={result.CurrentRunExcluded}"
        );

        // §3-2 step 7 — bypass when the caller has no usable cloud store (D7
        // local-only entry, or cloud disabled mid-flow).
        if (cloudStore == null || !LauncherPatches.CloudSyncEnabled)
        {
            await SimpleResultDialog.ShowAsync(
                parent,
                true,
                $"Copy complete ({result.FileCount} files).",
                scale
            );
            return true;
        }

        bool pushToCloud = await ShowConfirmAsync(
            parent,
            "Would you like to reflect this to the cloud as well?",
            scale,
            okLabel: "Yes",
            cancelLabel: "No"
        );
        if (pushToCloud)
        {
            var busy3 = BusyOverlay.Show(parent, "Reflecting to cloud...", scale);
            bool verified;
            try
            {
                // Issue #64 UX round 2 — runs off the main thread:
                // FlushAndVerifyForSlotAsync's Flush is a Thread.Sleep polling
                // loop (up to 300 s while the write queue drains), and awaited
                // inline on the Godot context it pins the main thread so the
                // overlay never repaints (device report: cloud reflect read as
                // a hard freeze). The main thread stays free to tick the
                // remaining-files counter below instead.
                var work = Task.Run(async () =>
                {
                    await LauncherPatches.ApplyChosenSideForSlotAsync(
                        localStore,
                        cloudStore,
                        keepLocal: true,
                        dst.ProfileNumber,
                        dst.IsModded
                    );
                    return await LauncherPatches.FlushAndVerifyForSlotAsync(
                        localStore,
                        cloudStore,
                        keepLocal: true,
                        dst.ProfileNumber,
                        dst.IsModded
                    );
                });
                while (!work.IsCompleted)
                {
                    // Non-batch path: every file is its own write-queue action,
                    // so the queue depth IS the remaining-file count.
                    int pending = cloudStore.PendingWriteCount;
                    if (pending > 0)
                        busy3.SetMessage($"Reflecting to cloud... {pending} files remaining");
                    await Task.Delay(400);
                }
                verified = await work;
            }
            catch (Exception ex)
            {
                PatchHelper.Log($"{Tag} cloud reflect threw: {ex.Message}");
                verified = false;
            }
            busy3.Dismiss();

            if (!verified)
            {
                // Same "verify failed -> local-only for the rest of this session"
                // degradation HandleConflictAsync/HandleProfileConflictAsync apply
                // on their own KeepLocal/KeepCloud verify failures
                // (LauncherPatches.cs:504,515,614).
                LauncherPatches.DegradeToLocalOnlySession();
                PatchHelper.Log($"{Tag} cloud reflect verify failed — degraded to local-only");
                await SimpleResultDialog.ShowAsync(
                    parent,
                    false,
                    "Failed to reflect to the cloud. This session will be switched to local-only.",
                    scale
                );
            }
            else
            {
                await SimpleResultDialog.ShowAsync(
                    parent,
                    true,
                    $"Copy complete and reflected to cloud ({result.FileCount} files).",
                    scale
                );
            }
        }
        else
        {
            await SimpleResultDialog.ShowAsync(
                parent,
                true,
                "Copying is complete, but it was not reflected to the cloud.\n"
                    + "If cloud progress is higher on the next sync, the copy may be reverted.",
                scale
            );
        }

        return true;
    }

    // ---- backup restore ("Backup Restore") -------------------------------------------

    public static async Task<bool> RunRestoreAsync(
        Node parent,
        ISaveStore localStore,
        SteamKit2CloudSaveStore cloudStore
    )
    {
        float scale = LauncherUI.ResolveScale(parent);
        float vpH = LauncherUI.ResolveViewportHeight(parent);

        List<LocalBackupService.SnapshotInfo> snapshots;
        var busy = BusyOverlay.Show(parent, "Checking backup list...", scale);
        try
        {
            snapshots = await RunBlockingAsync(LocalBackupService.ListSnapshots);
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"{Tag} RunRestoreAsync: ListSnapshots failed: {ex.Message}");
            busy.Dismiss();
            await SimpleResultDialog.ShowAsync(
                parent,
                false,
                "Failed to check the backup list.",
                scale
            );
            return false;
        }
        busy.Dismiss();

        if (snapshots.Count == 0)
        {
            await SimpleResultDialog.ShowAsync(parent, false, "There are no backups.", scale);
            return false;
        }

        var picker = new BackupRestorePickerDialog(snapshots, scale, vpH);
        parent.AddChild(picker);
        var picked = await picker.Result;
        if (picked == null)
            return false;

        bool confirmed = await ShowConfirmAsync(
            parent,
            "Revert all saves to this backup point. The current state will be automatically backed up right before restoration.",
            scale
        );
        if (!confirmed)
            return false;

        var busy2 = BusyOverlay.Show(parent, "Restoring...", scale);
        LocalBackupService.RestoreResult result;
        try
        {
            result = await RunBlockingAsync(() =>
                LocalBackupService.RestoreSnapshot(picked.SetRoot)
            );
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"{Tag} RestoreSnapshot threw: {ex.Message}");
            busy2.Dismiss();
            await SimpleResultDialog.ShowAsync(
                parent,
                false,
                $"Error during restoration: {ex.Message}",
                scale
            );
            return false;
        }
        busy2.Dismiss();

        if (!result.Success)
        {
            PatchHelper.Log(
                $"{Tag} RestoreSnapshot failed: {result.Error} (NeedsPermission={result.NeedsPermission})"
            );
            if (result.NeedsPermission)
                AppPaths.RequestStoragePermission();
            await SimpleResultDialog.ShowAsync(
                parent,
                false,
                result.NeedsPermission
                    ? "Storage access permission is required to back up.\nGrant the permission and try again."
                    : (result.Error ?? "An error occurred during restoration."),
                scale
            );
            return false;
        }

        PatchHelper.Log(
            $"{Tag} RestoreSnapshot OK: {picked.SetRoot}, {result.FileCount} files, "
                + $"{result.TotalBytes}B, preRestoreBackup={result.PreRestoreBackupPath}"
        );

        if (cloudStore == null || !LauncherPatches.CloudSyncEnabled)
        {
            await SimpleResultDialog.ShowAsync(
                parent,
                true,
                $"Restoration complete ({result.FileCount} files).",
                scale
            );
            return true;
        }

        // D6 — restore touches the whole tree, so the cloud-reflect step reuses
        // the same full-tree push the manual "Push to Cloud" button uses, not a
        // slot-scoped apply.
        bool pushToCloud = await ShowConfirmAsync(
            parent,
            "Would you like to reflect this to the cloud as well?",
            scale,
            okLabel: "Yes",
            cancelLabel: "No"
        );
        if (pushToCloud)
        {
            var busy3 = BusyOverlay.Show(parent, "Reflecting to cloud...", scale);
            CloudBatchOutcome outcome;
            try
            {
                // Issue #64 UX round 2 — a 120+ file backup made the static
                // overlay read as a hard freeze during the batch upload
                // (device report). EndSaveBatch reports per-file progress from
                // the CloudSaveWriter thread; DeferredProgress marshals each
                // update onto the main thread before touching the overlay.
                var progress = new DeferredProgress(p =>
                {
                    if (GodotObject.IsInstanceValid(busy3))
                        busy3.SetMessage($"Reflecting to cloud... {p.done}/{p.total}");
                });
                outcome = await CloudSyncCoordinator.ManualPushAllAsync(
                    LauncherPatches.SavedAccountName,
                    LauncherPatches.SavedRefreshToken,
                    progress
                );
            }
            catch (Exception ex)
            {
                PatchHelper.Log($"{Tag} ManualPushAllAsync threw: {ex.Message}");
                outcome = CloudBatchOutcome.Failed;
            }
            busy3.Dismiss();

            var msg = outcome switch
            {
                CloudBatchOutcome.Success =>
                    $"Restoration complete and reflected to cloud ({result.FileCount} files).",
                CloudBatchOutcome.TimedOut =>
                    "Cloud reflection timed out. Some files may not have been reflected.",
                _ => "An error occurred while reflecting to the cloud. Check the logs.",
            };
            await SimpleResultDialog.ShowAsync(
                parent,
                outcome == CloudBatchOutcome.Success,
                msg,
                scale
            );
        }
        else
        {
            await SimpleResultDialog.ShowAsync(
                parent,
                true,
                "Restoration is complete, but it was not reflected to the cloud.\n"
                    + "If cloud progress is higher on the next sync, the copy may be reverted.",
                scale
            );
        }

        return true;
    }

    // ---- shared helpers ---------------------------------------------------------

    private static Task<bool> ShowConfirmAsync(
        Node parent,
        string message,
        float scale,
        string okLabel = null,
        string cancelLabel = null
    )
    {
        var tcs = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var dialog = new StyledDialog(message, scale, okLabel, cancelLabel);
        dialog.Confirmed += () => tcs.TrySetResult(true);
        dialog.Cancelled += () => tcs.TrySetResult(false);
        parent.AddChild(dialog);
        return tcs.Task;
    }

    // ProfileCopyService.CopyProfile / LocalBackupService.ListSnapshots /
    // RestoreSnapshot are synchronous, disk-bound calls (design §2-A/§2-B —
    // "synchronous, callers wrap in Task.Run"). LauncherController's Task.Run handlers
    // (e.g. OnLocalBackupPressed, LauncherController.cs:906-994) marshal their
    // post-Task.Run UI work back with the `_runOnMainThread` closure LauncherUI
    // injects (a ConcurrentQueue<Action> drained on SceneTree.ProcessFrame). This
    // static class lives outside LauncherController and has no such closure, so
    // it uses Godot's CallDeferred primitive instead to guarantee the continuation
    // — and therefore every dialog/BusyOverlay call that follows an `await` on
    // this helper — actually runs on the main thread. Same "defer to the next
    // idle frame on the main thread" mechanism StyledDialog.cs already relies on
    // for its post-layout sizing pass (StyledDialog.cs:76-89).
    // IProgress<T> whose handler always runs on the Godot main thread.
    // EndSaveBatch reports from the CloudSaveWriter background thread, and
    // Progress<T>'s captured-SynchronizationContext marshalling is exactly
    // the kind of implicit threading this flow already avoids elsewhere
    // (see RunBlockingAsync) — CallDeferred makes the hop explicit.
    private sealed class DeferredProgress : IProgress<(int done, int total)>
    {
        private readonly Action<(int done, int total)> _onMainThread;

        public DeferredProgress(Action<(int done, int total)> onMainThread) =>
            _onMainThread = onMainThread;

        public void Report((int done, int total) value)
        {
            var handler = _onMainThread;
            Callable
                .From(() =>
                {
                    handler(value);
                })
                .CallDeferred();
        }
    }

    private static Task<T> RunBlockingAsync<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(() =>
        {
            try
            {
                var result = work();
                // Block-bodied (not `() => tcs.TrySetResult(result)`) so the
                // lambda is unambiguously Action — TrySetResult/TrySetException
                // return bool, and an expression-bodied lambda here would be
                // convertible to both Callable.From(Action) and
                // Callable.From(Func<bool>) overloads (ambiguous call).
                Callable
                    .From(() =>
                    {
                        tcs.TrySetResult(result);
                    })
                    .CallDeferred();
            }
            catch (Exception ex)
            {
                Callable
                    .From(() =>
                    {
                        tcs.TrySetException(ex);
                    })
                    .CallDeferred();
            }
        });
        return tcs.Task;
    }
}
