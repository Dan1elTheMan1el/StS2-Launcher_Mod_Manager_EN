using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Saves;
using STS2Mobile.Launcher;
using STS2Mobile.Launcher.Components;
using STS2Mobile.Steam;

namespace STS2Mobile.Patches;

// Core patches for the mobile launcher flow. Intercepts GameStartupWrapper to show
// the Steam login UI before the game starts, injects cloud save support via SteamKit2,
// and delegates sync logic to CloudSyncCoordinator. On first PLAY, verifies that
// the cloud file cache loads before letting the game's save layer touch the cloud
// store — protects against issue #4 where a stale/unauthenticated cache would let
// freshly defaulted local saves silently overwrite real cloud progress.
public static class LauncherPatches
{
    internal static bool CloudSyncEnabled = true;
    internal static string SavedAccountName;
    internal static string SavedRefreshToken;

    // Set true after a successful cache preload during RunLauncherThenGame. Drives
    // ConstructDefaultPrefix's fork: cloud-wrapped SaveManager when true, local-only
    // SaveManager (no cloud writes possible) when false.
    private static bool _cloudCacheReady;

    public static void Apply(Harmony harmony)
    {
        PatchHelper.PatchCritical(
            harmony,
            typeof(NGame),
            "GameStartupWrapper",
            prefix: PatchHelper.Method(typeof(LauncherPatches), nameof(GameStartupWrapperPrefix))
        );

        PatchHelper.Patch(
            harmony,
            typeof(SaveManager),
            "ConstructDefault",
            prefix: PatchHelper.Method(typeof(LauncherPatches), nameof(ConstructDefaultPrefix))
        );

        PatchHelper.PatchCritical(
            harmony,
            typeof(CloudSaveStore),
            "SyncCloudToLocal",
            prefix: PatchHelper.Method(typeof(LauncherPatches), nameof(SyncCloudToLocalPrefix))
        );
    }

    public static bool GameStartupWrapperPrefix(object __instance, ref Task __result)
    {
        __result = RunLauncherThenGame(__instance);
        return false;
    }

    public static bool ConstructDefaultPrefix(ref SaveManager __result)
    {
        PatchHelper.Log(
            $"[Cloud] ConstructDefaultPrefix called. HasToken={SavedRefreshToken != null}, "
                + $"CloudSync={CloudSyncEnabled}, CacheReady={_cloudCacheReady}"
        );

        if (!CloudSyncEnabled)
        {
            PatchHelper.Log("[Cloud] Cloud sync disabled by user — using local-only SaveManager");
            return true;
        }

        if (SavedAccountName == null || SavedRefreshToken == null)
        {
            PatchHelper.Log("[Cloud] No saved credentials — using local-only SaveManager");
            return true;
        }

        // Inline blocking preload: NGame and other game systems access
        // SaveManager.Instance during early initialization, BEFORE our launcher
        // gets a chance to run RunLauncherThenGame's async preload. If we just
        // returned local-only here, the game would cache default in-memory state
        // (no progress, etc.) which later cloud pulls cannot dislodge — verified
        // 2026-04-29 in 0.3.0-rc2: pulls succeeded but main menu still showed
        // "no save". So when we don't yet have a cache, we synchronously wait
        // up to 15s here. The first SaveManager.Instance access on cold start
        // pays this latency, but the game then sees real cloud-derived state
        // from the very first read.
        if (!_cloudCacheReady)
        {
            try
            {
                var preloadStore =
                    SteamKit2CloudSaveStore.Instance
                    ?? new SteamKit2CloudSaveStore(SavedAccountName, SavedRefreshToken);
                _cloudCacheReady = preloadStore
                    .WaitForCacheReadyAsync(15_000)
                    .GetAwaiter()
                    .GetResult();
                PatchHelper.Log(
                    $"[Cloud] Inline cache preload from ConstructDefault: ready={_cloudCacheReady}"
                );
            }
            catch (Exception ex)
            {
                PatchHelper.Log($"[Cloud] Inline cache preload threw: {ex.Message}");
                _cloudCacheReady = false;
            }
        }

        // After the inline attempt, if cache is still not ready, fall back to
        // local-only — same protection as before (no cloud writes, so no
        // destructive overwrite even if the game writes defaults).
        if (!_cloudCacheReady)
        {
            PatchHelper.Log(
                "[Cloud] Cloud cache not ready after inline preload — using local-only SaveManager"
            );
            return true;
        }

        try
        {
            var localStore = new GodotFileIo(UserDataPathProvider.GetAccountScopedBasePath(null));
            // Reuse the singleton from the preload so we don't open a second SteamConnection.
            var cloudStore =
                SteamKit2CloudSaveStore.Instance
                ?? new SteamKit2CloudSaveStore(SavedAccountName, SavedRefreshToken);
            var wrappedStore = new CloudSaveStore(localStore, cloudStore);

            // P1-3 (G8) — the 1-arg ctor delegates to the 2-arg one with the
            // SAME store for both saveStore and localOnlyStore (decompile-
            // confirmed identical in both the upstream mobile build and the
            // installed PC build, v0.107.1), so settings.save was riding the
            // cloud-wrapped store right alongside profile/prefs/progress/run.
            // The game's own ConstructDefault always passes its plain local
            // GodotFileIo as the second arg — SettingsSaveManager is the only
            // consumer of that argument, so this makes mobile match PC:
            // settings stay device-local, everything else keeps syncing.
            __result = new SaveManager(wrappedStore, localStore);
            PatchHelper.Log("[Cloud] Created SaveManager with SteamKit2 cloud store");
            return false;
        }
        catch (Exception ex)
        {
            PatchHelper.Log(
                $"[Cloud] Cloud store injection failed, falling back to local: {ex.Message}"
            );
            return true;
        }
    }

    public static bool SyncCloudToLocalPrefix(
        CloudSaveStore __instance,
        string path,
        ref Task __result
    )
    {
        __result = CloudSyncCoordinator.AutoSyncFileAsync(
            __instance.LocalStore,
            __instance.CloudStore,
            path
        );
        return false;
    }

    private static async Task RunLauncherThenGame(object game)
    {
        var gameNode = (Node)game;

        var launcher = new LauncherUI();
        gameNode.AddChild(launcher);
        launcher.SetGameMode(true);
        launcher.Initialize();
        PatchHelper.Log("Launcher UI displayed");
        // Issue #23 — dismiss the native ETC2-rebuild overlay if it was shown
        // during this boot. No-op when overlay was never installed.
        LauncherModel.GetGodotApp()?.Call("hideLoadingOverlay");

        await launcher.WaitForLaunch();
        PatchHelper.Log("User launched game, proceeding to startup...");

        var instanceField = typeof(SaveManager).GetField(
            "_instance",
            BindingFlags.NonPublic | BindingFlags.Static
        );
        if (instanceField != null)
        {
            instanceField.SetValue(null, null);
            PatchHelper.Log("[Cloud] Reset SaveManager._instance for cloud store re-injection");
        }

        // Free the launcher BEFORE running the conflict dialog. The launcher is
        // ZIndex=100, so a dialog added underneath would be hidden — and even if
        // the z-ordering were fixed, leaving the launcher's PLAY button live
        // during a sync decision invites accidental re-entry. Once the user has
        // pressed PLAY, the launcher's job is done.
        launcher.MarkPlannedTeardown();
        launcher.QueueFree();

        // Issue #53 — cover the launcher→game gap with a status overlay. The cloud
        // handshake + conflict backup + sync apply below can take 1~2 min with nothing
        // else on screen; without this the black screen reads as a crash and users kill
        // the app mid-sync. Only shown when a cloud sync is actually going to run.
        CloudSyncOverlay overlay = null;
        bool cloudSyncWillRun =
            CloudSyncEnabled && SavedAccountName != null && SavedRefreshToken != null;
        if (cloudSyncWillRun)
        {
            overlay = new CloudSyncOverlay();
            gameNode.AddChild(overlay);
            overlay.Initialize();
        }

        // Pre-PLAY cloud handshake: load the file cache, classify the local/cloud
        // state, and resolve any conflict via the dialog BEFORE InitSettingsData runs.
        // After this completes, ConstructDefaultPrefix has the info it needs to pick
        // the right SaveManager flavor.
        _cloudCacheReady = false;
        if (cloudSyncWillRun)
        {
            try
            {
                overlay?.SetStatus("클라우드 상태 확인 중...");
                var existing = SteamKit2CloudSaveStore.Instance;
                var cloudStore =
                    existing ?? new SteamKit2CloudSaveStore(SavedAccountName, SavedRefreshToken);

                _cloudCacheReady = await cloudStore.WaitForCacheReadyAsync(15_000);
                if (_cloudCacheReady)
                {
                    PatchHelper.Log("[Cloud] Cache preload OK — running sync decision");
                    var localStore = new GodotFileIo(
                        UserDataPathProvider.GetAccountScopedBasePath(null)
                    );
                    await ResolveSyncDecisionAsync(gameNode, localStore, cloudStore, overlay);
                }
                else
                {
                    PatchHelper.Log(
                        "[Cloud] Cache preload failed — entering local-only mode this session"
                    );
                }
            }
            catch (Exception ex)
            {
                PatchHelper.Log(
                    $"[Cloud] Cache preload threw, entering local-only mode: {ex.Message}"
                );
                _cloudCacheReady = false;
            }
        }
        else
        {
            PatchHelper.Log("[Cloud] Skipping cloud preload (sync disabled or no credentials)");
        }

        // Sync work done — drop the overlay before the game boot path (ShaderWarmup
        // has its own full-screen UI).
        overlay?.QueueFree();

        if (ShaderWarmupScreen.NeedsWarmup())
        {
            var warmup = new ShaderWarmupScreen();
            gameNode.AddChild(warmup);
            warmup.Initialize();
            await warmup.WaitForCompletion();
            warmup.QueueFree();
        }

        SaveManager.Instance.InitSettingsData();

        var gameStartup = game.GetType()
            .GetMethod("GameStartup", BindingFlags.NonPublic | BindingFlags.Instance);

        try
        {
            await (Task)gameStartup.Invoke(game, null);
        }
        catch (TargetInvocationException ex)
        {
            PatchHelper.Log($"Game startup failed: {ex.InnerException?.Message}");
            throw ex.InnerException ?? ex;
        }
    }

    // Public entry point used by the Save Manager launcher button. Lists every
    // (profile × modded) slot that has local or cloud data — one button per
    // slot — instead of the old single aggregate dialog. That aggregate picked
    // ONE summary by priority (first-diff, then first-in-progress-run, then
    // first-non-empty), so a tiny ghost slot (e.g. an empty unmodded profile2
    // stub) could hijack the dialog and hide a real conflict on another slot
    // entirely. Per-slot listing means every slot is reachable and resolvable
    // on its own — see CloudSyncDecisions.DeterminePerProfileAsync.
    public static async Task OpenSaveSyncDialogAsync(Node parent)
    {
        if (!CloudSyncEnabled)
        {
            PatchHelper.Log("[Cloud] Save Manager: cloud sync disabled by user");
            return;
        }
        if (SavedAccountName == null || SavedRefreshToken == null)
        {
            PatchHelper.Log("[Cloud] Save Manager: no saved credentials");
            return;
        }

        var cloudStore =
            SteamKit2CloudSaveStore.Instance
            ?? new SteamKit2CloudSaveStore(SavedAccountName, SavedRefreshToken);

        bool cacheLoaded = await cloudStore.WaitForCacheReadyAsync(15_000);
        if (!cacheLoaded)
        {
            PatchHelper.Log("[Cloud] Save Manager: cloud cache failed to load");
            return;
        }

        var localStore = new GodotFileIo(UserDataPathProvider.GetAccountScopedBasePath(null));

        // Cached across loop iterations — DeterminePerProfileAsync downloads
        // every non-identical-size cloud progress.save to compare it, so
        // re-running it on every dialog close (Cancel/Unverified/Identical —
        // nothing actually changed) was silently re-downloading several MB
        // per tap. Only re-fetch after a resolution that actually applied a
        // KeepLocal/KeepCloud (state may have genuinely changed); a Cancel or
        // closing the picker reuses the same list.
        List<SyncDecisionResult> slots = null;

        // Loop so resolving one slot returns the user to a refreshed profile
        // list — they can then inspect/resolve another slot in the same visit
        // instead of having to re-tap the Save Manager button each time.
        while (true)
        {
            if (slots == null)
            {
                slots = await CloudSyncDecisions.DeterminePerProfileAsync(localStore, cloudStore);
                PatchHelper.Log($"[Cloud] Save Manager: {slots.Count} profile slot(s) with data");
            }

            if (slots.Count == 0)
            {
                // Nothing anywhere — same informational close-only dialog as
                // before. HandleConflictAsync's Cancel branch already special-
                // cases NoData/Identical to skip the local-only fallback, so
                // reusing it here is safe (no ApplyChosenSideAsync call).
                var empty = new SyncDecisionResult
                {
                    Decision = SyncDecision.NoData,
                    LocalSummary = new SaveProgressSummary(),
                    CloudSummary = new SaveProgressSummary(),
                };
                await HandleConflictAsync(parent, localStore, cloudStore, empty);
                return;
            }

            var picker = new ProfilePickerDialog(
                slots,
                LauncherUI.ResolveScale(parent),
                LauncherUI.ResolveViewportHeight(parent)
            );
            parent.AddChild(picker);
            var picked = await picker.Result;
            if (picked == null)
                return; // Closed without picking a slot.

            PatchHelper.Log(
                $"[Cloud] Save Manager: slot picked profile={picked.ProfileNumber} modded={picked.IsModded} decision={picked.Decision}"
            );
            Issue7Diagnostics.LogDialogSummary(
                "SaveManagerProfileSlot",
                picked.Decision,
                picked.LocalSummary,
                picked.CloudSummary
            );
            bool applied = await HandleProfileConflictAsync(parent, localStore, cloudStore, picked);
            if (applied)
                slots = null; // State changed — force a fresh DeterminePerProfileAsync.
        }
    }

    private static async Task ResolveSyncDecisionAsync(
        Node gameNode,
        ISaveStore localStore,
        SteamKit2CloudSaveStore cloudStore,
        CloudSyncOverlay overlay = null
    )
    {
        var result = await CloudSyncDecisions.DetermineAsync(localStore, cloudStore);
        PatchHelper.Log($"[Cloud] Sync decision: {result.Decision}");
        Issue7Diagnostics.LogDialogSummary(
            "FirstPlayAuto",
            result.Decision,
            result.LocalSummary,
            result.CloudSummary
        );

        switch (result.Decision)
        {
            case SyncDecision.NoData:
            case SyncDecision.Identical:
                // Sides agree (or neither has anything). Issue #36 Part A: take the
                // one-per-handshake auto MATCH snapshot of the local tree. No dialog,
                // nothing overwritten — fire-and-forget on a background thread.
                LocalBackupService.BackupHandshakeMatch(localStore);
                return;

            case SyncDecision.MobileOnly:
            case SyncDecision.CloudOnly:
            case SyncDecision.Conflict:
                // Anything other than full agreement gets a dialog so the user
                // can confirm the direction of the sync. Without this, a fresh
                // mobile install with cloud progress would silently pull (mostly
                // safe) but a fresh PC install with mobile progress would push
                // (also mostly safe) — and a true conflict could pick the wrong
                // side. Surfacing the choice removes both risk and surprise.
                await HandleConflictAsync(gameNode, localStore, cloudStore, result, overlay);
                return;

            case SyncDecision.Unverified:
                // Couldn't confirm whether this profile matches the cloud copy
                // (transient read failure). Do nothing here — no dialog, no
                // handshake-match snapshot, and _cloudCacheReady is left
                // untouched (cloud sync stays active for the session; the
                // game's own per-file AutoSync path does its own real-time
                // compare later and isn't affected by this stale decision).
                // We simply defer this profile's first-PLAY check to the next
                // handshake rather than guessing a direction.
                PatchHelper.Log(
                    "[Cloud] Sync decision unverified — deferring this profile's check, no auto-apply"
                );
                return;
        }
    }

    private static async Task HandleConflictAsync(
        Node gameNode,
        ISaveStore localStore,
        SteamKit2CloudSaveStore cloudStore,
        SyncDecisionResult decision,
        CloudSyncOverlay overlay = null
    )
    {
        // The dialog deliberately has no "remember my choice" option — which
        // side is correct depends on context (which device played last,
        // whether the user just restored from backup, etc.) so blanket
        // auto-apply would silently destroy data on the next conflict that
        // should have gone the other way.
        var dialog = new CloudConflictDialog(
            decision.LocalSummary,
            decision.CloudSummary,
            decision.LocalIsMoreRecent,
            LauncherUI.ResolveScale(gameNode),
            decision.Decision,
            LauncherUI.ResolveViewportHeight(gameNode),
            decision.DiffSlotCount
        );
        gameNode.AddChild(dialog);
        var choice = await dialog.Result;
        PatchHelper.Log($"[Cloud] Conflict resolved by user: choice={choice}");

        // Issue #36 Part A: one-per-handshake auto CONFLICT snapshot, in two phases
        // around the resolution. Phase 1 (discarded = the old state about to be
        // thrown away) is AWAITED before ApplyChosenSideAsync so it's on disk before
        // being overwritten. Phase 2 (kept = the resolved LOCAL tree) fires after
        // apply. Both land in one auto/<ts>_conflict folder via the handle. Cancel
        // overwrites nothing → no snapshot.
        LocalBackupService.ConflictBackupHandle conflictBackup = null;
        if (choice == CloudConflictChoice.KeepLocal || choice == CloudConflictChoice.KeepCloud)
        {
            // Issue #53 — the discarded-cloud snapshot (KeepLocal) downloads 100+ files
            // serially and is the slow phase users mistook for a hang. Feed per-file
            // progress into the overlay (the overlay marshals to the main thread itself).
            IProgress<(int done, int total)> backupProgress =
                overlay == null
                    ? null
                    : new Progress<(int done, int total)>(p =>
                        overlay.SetBackupProgress(p.done, p.total)
                    );
            overlay?.SetStatus("클라우드 백업 중", "");
            conflictBackup = await LocalBackupService.BackupConflictDiscardedAsync(
                localStore,
                cloudStore,
                keepLocal: choice == CloudConflictChoice.KeepLocal,
                backupProgress
            );
        }

        if (choice != CloudConflictChoice.Cancel)
            overlay?.SetStatus("동기화 적용 중...");

        switch (choice)
        {
            case CloudConflictChoice.KeepLocal:
                await ApplyChosenSideAsync(localStore, cloudStore, keepLocal: true);
                LocalBackupService.BackupConflictKept(conflictBackup, localStore);
                if (!await FlushAndVerifyAsync(localStore, cloudStore, keepLocal: true))
                {
                    PatchHelper.Log(
                        "[Cloud] Verification failed after KeepLocal — falling back to local-only this session"
                    );
                    _cloudCacheReady = false;
                }
                break;
            case CloudConflictChoice.KeepCloud:
                await ApplyChosenSideAsync(localStore, cloudStore, keepLocal: false);
                LocalBackupService.BackupConflictKept(conflictBackup, localStore);
                if (!await FlushAndVerifyAsync(localStore, cloudStore, keepLocal: false))
                {
                    PatchHelper.Log(
                        "[Cloud] Verification failed after KeepCloud — falling back to local-only this session"
                    );
                    _cloudCacheReady = false;
                }
                break;
            case CloudConflictChoice.Cancel:
                // Informational dialogs (Identical/NoData) only reach here via the
                // Save Manager button — there's no conflict to resolve, so closing
                // one must NOT flip the session to local-only (issue: verifying an
                // already-synced state then closing was silently killing cloud
                // writes for the session). The first-PLAY flow filters these out
                // earlier, so this branch is exactly the button+informational case.
                if (
                    decision.Decision == SyncDecision.Identical
                    || decision.Decision == SyncDecision.NoData
                )
                {
                    PatchHelper.Log(
                        "[Cloud] Sync dialog closed (sides already in sync — no action)"
                    );
                }
                else
                {
                    // User aborted an actionable conflict (Conflict/MobileOnly/
                    // CloudOnly). Force local-only mode for this session — the game
                    // still boots, but no cloud writes happen. This is the deferred-
                    // conflict safeguard; they can retry next launch.
                    _cloudCacheReady = false;
                    PatchHelper.Log("[Cloud] Conflict cancelled — falling back to local-only");
                }
                break;
        }
    }

    // Save Manager per-profile resolution: same dialog/backup/apply/verify
    // shape as HandleConflictAsync, but the apply is scoped to ONE (profile ×
    // modded) slot via ApplyChosenSideForSlotAsync — the other slots are
    // never touched. The full-tree LocalBackupService snapshot is kept as-is
    // (issue #36 Part A backups are whole-tree by design; scoping the
    // *apply* is what protects the other profiles, not the backup).
    //
    // Returns whether a KeepLocal/KeepCloud apply actually ran, so the Save
    // Manager loop knows whether the profile-slot list needs a fresh
    // DeterminePerProfileAsync (state changed) or can reuse its cached one
    // (Cancel/no-action — nothing on either side moved).
    private static async Task<bool> HandleProfileConflictAsync(
        Node gameNode,
        ISaveStore localStore,
        SteamKit2CloudSaveStore cloudStore,
        SyncDecisionResult slot
    )
    {
        var dialog = new CloudConflictDialog(
            slot.LocalSummary,
            slot.CloudSummary,
            slot.LocalIsMoreRecent,
            LauncherUI.ResolveScale(gameNode),
            slot.Decision,
            LauncherUI.ResolveViewportHeight(gameNode)
        );
        gameNode.AddChild(dialog);
        var choice = await dialog.Result;
        PatchHelper.Log(
            $"[Cloud] Profile slot conflict resolved: profile={slot.ProfileNumber} modded={slot.IsModded} choice={choice}"
        );

        LocalBackupService.ConflictBackupHandle conflictBackup = null;
        if (choice == CloudConflictChoice.KeepLocal || choice == CloudConflictChoice.KeepCloud)
        {
            conflictBackup = await LocalBackupService.BackupConflictDiscardedAsync(
                localStore,
                cloudStore,
                keepLocal: choice == CloudConflictChoice.KeepLocal,
                progress: null
            );
        }

        switch (choice)
        {
            case CloudConflictChoice.KeepLocal:
                await ApplyChosenSideForSlotAsync(
                    localStore,
                    cloudStore,
                    keepLocal: true,
                    slot.ProfileNumber,
                    slot.IsModded
                );
                LocalBackupService.BackupConflictKept(conflictBackup, localStore);
                if (
                    !await FlushAndVerifyForSlotAsync(
                        localStore,
                        cloudStore,
                        keepLocal: true,
                        slot.ProfileNumber,
                        slot.IsModded
                    )
                )
                {
                    PatchHelper.Log(
                        "[Cloud] Verification failed after profile KeepLocal — falling back to local-only this session"
                    );
                    _cloudCacheReady = false;
                }
                return true; // Apply ran (regardless of verify outcome) — state may have changed.
            case CloudConflictChoice.KeepCloud:
                await ApplyChosenSideForSlotAsync(
                    localStore,
                    cloudStore,
                    keepLocal: false,
                    slot.ProfileNumber,
                    slot.IsModded
                );
                LocalBackupService.BackupConflictKept(conflictBackup, localStore);
                if (
                    !await FlushAndVerifyForSlotAsync(
                        localStore,
                        cloudStore,
                        keepLocal: false,
                        slot.ProfileNumber,
                        slot.IsModded
                    )
                )
                {
                    PatchHelper.Log(
                        "[Cloud] Verification failed after profile KeepCloud — falling back to local-only this session"
                    );
                    _cloudCacheReady = false;
                }
                return true; // Apply ran (regardless of verify outcome) — state may have changed.
            case CloudConflictChoice.Cancel:
                // Same button-only semantics as HandleConflictAsync's Cancel
                // branch: an already-in-sync slot (Identical — NoData can't
                // reach here, empty slots aren't listed) closing is a no-op,
                // but backing out of a real per-slot conflict/one-sided slot
                // still falls back to local-only for the session so the game
                // doesn't proceed with an unresolved decision live. Unverified
                // is also purely informational here — the dialog never offered
                // KeepLocal/KeepCloud (see CloudConflictDialog), so closing it
                // isn't "backing out of a choice"; the Save Manager loop simply
                // re-checks this slot the next time the profile list is opened.
                if (
                    slot.Decision == SyncDecision.Identical
                    || slot.Decision == SyncDecision.Unverified
                )
                {
                    PatchHelper.Log(
                        $"[Cloud] Profile slot dialog closed (decision={slot.Decision} — no action)"
                    );
                }
                else
                {
                    _cloudCacheReady = false;
                    PatchHelper.Log(
                        "[Cloud] Profile slot conflict cancelled — falling back to local-only"
                    );
                }
                return false; // No apply — nothing changed, caller reuses its cached slot list.
        }
        return false;
    }

    // Blocks until queued cloud writes drain, then verifies the chosen side has
    // landed on both ends. Without this, the user could press a button and the
    // launcher would fall through to InitSettingsData while a push is still in
    // flight — risking the game's first save call racing with a half-written
    // cloud copy. We verify only progress.save (the one save the user mostly
    // cares about); other files lean on the post-decision AutoSyncFileAsync
    // path, which now sees both sides aligned for progress and either pulls or
    // pushes the rest by content comparison.
    private static async Task<bool> FlushAndVerifyAsync(
        ISaveStore localStore,
        SteamKit2CloudSaveStore cloudStore,
        bool keepLocal
    )
    {
        try
        {
            // Same rationale as QuitPrefix: wait for actual upload completion
            // signaled by CloudWriteQueue's _actionInProgress + queue depth.
            // 5-min ceiling is for unreachable-Steam scenarios; healthy uploads
            // finish in 1-15 s. The user is sitting on the dialog waiting for
            // verify and explicitly chose to commit a sync direction, so the
            // wait is expected.
            cloudStore.Flush(timeoutMs: 300_000);
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Cloud] Flush failed: {ex.Message}");
            return false;
        }

        // Re-read progress.save from both sides for the active profile layout.
        // We only sample profile1 — verifying every profile/path would balloon
        // the wait time and progress.save is the one file that actually carries
        // career state worth losing sleep over.
        var path = SavePathCompat.GetProgressPathForProfile(1);
        return await VerifyPathMatchesAsync(localStore, cloudStore, path);
    }

    // Save Manager per-profile apply: same drain-then-verify contract as
    // FlushAndVerifyAsync, but checks the picked slot's own progress.save
    // instead of always profile1 — a KeepLocal/KeepCloud on profile2 shouldn't
    // report success/failure based on profile1's unrelated state.
    private static async Task<bool> FlushAndVerifyForSlotAsync(
        ISaveStore localStore,
        SteamKit2CloudSaveStore cloudStore,
        bool keepLocal,
        int profile,
        bool modded
    )
    {
        try
        {
            cloudStore.Flush(timeoutMs: 300_000);
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Cloud] Flush failed: {ex.Message}");
            return false;
        }

        var wasModded = UserDataPathProvider.IsRunningModded;
        string path;
        try
        {
            UserDataPathProvider.IsRunningModded = modded;
            path = SavePathCompat.GetProgressPathForProfile(profile);
        }
        finally
        {
            UserDataPathProvider.IsRunningModded = wasModded;
        }

        return await VerifyPathMatchesAsync(localStore, cloudStore, path);
    }

    // Shared byte-for-byte verify used by both FlushAndVerifyAsync (fixed
    // profile1) and FlushAndVerifyForSlotAsync (the picked slot's path).
    // Assumes cloudStore.Flush already ran — this only re-reads and compares.
    private static async Task<bool> VerifyPathMatchesAsync(
        ISaveStore localStore,
        SteamKit2CloudSaveStore cloudStore,
        string path
    )
    {
        try
        {
            bool localExists = localStore.FileExists(path);
            bool cloudExists = cloudStore.FileExists(path);

            if (!localExists && !cloudExists)
            {
                PatchHelper.Log($"[Cloud] Verify: {path} absent on both sides — accepted");
                return true;
            }

            if (localExists != cloudExists)
            {
                PatchHelper.Log(
                    $"[Cloud] Verify: existence mismatch local={localExists} cloud={cloudExists}"
                );
                return false;
            }

            string localContent = localStore.ReadFile(path);
            string cloudContent = await cloudStore.ReadFileAsync(path);

            if (localContent.Length != cloudContent.Length)
            {
                PatchHelper.Log(
                    $"[Cloud] Verify: size mismatch local={localContent.Length} "
                        + $"cloud={cloudContent.Length}"
                );
                return false;
            }

            if (localContent != cloudContent)
            {
                PatchHelper.Log($"[Cloud] Verify: content mismatch (same size, different bytes)");
                return false;
            }

            PatchHelper.Log(
                $"[Cloud] Verify OK: {path} matches on both sides ({localContent.Length} bytes)"
            );
            return true;
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Cloud] Verify threw: {ex.Message}");
            return false;
        }
    }

    // Pre-syncs progress.save across all profiles so InitSettingsData's later
    // SyncCloudToLocal calls see equal content on both sides (BRANCH-A → skip).
    private static async Task ApplyChosenSideAsync(
        ISaveStore local,
        SteamKit2CloudSaveStore cloud,
        bool keepLocal
    )
    {
        // Issue #7 fix scope:
        // 1. Also process current_run.save / current_run_mp.save — previously
        //    only progress.save was synced here, leaving the in-progress run
        //    one resolved-conflict away from being silently overwritten by
        //    AutoSyncFileAsync's "cloud wins" fallback when floors match.
        // 2. After local writes (KeepCloud branch), call SetLastModifiedTime
        //    to align local mtime with cloud's, otherwise local mtime = NOW
        //    (file write timestamp) and cloud mtime stays at original cloud
        //    value, leaving the next launch with a permanent mtime asymmetry
        //    that re-triggers conflict and points the "최근" badge at the
        //    wrong side.
        // 3. After cloud writes (KeepLocal branch), align local mtime to NOW
        //    too — SteamKit2CloudSaveStore.WriteFile sets cloud mtime to NOW
        //    automatically, so matching local to NOW keeps both sides equal.
        var wasModded = UserDataPathProvider.IsRunningModded;
        try
        {
            foreach (bool modded in new[] { false, true })
            {
                UserDataPathProvider.IsRunningModded = modded;
                for (int profile = 1; profile <= 3; profile++)
                {
                    await ApplyProgressWithGateAsync(
                        local,
                        cloud,
                        keepLocal,
                        SavePathCompat.GetProgressPathForProfile(profile)
                    );
                    await ApplyOneAsync(
                        local,
                        cloud,
                        keepLocal,
                        SavePathCompat.GetRunSavePath(profile, "current_run.save")
                    );
                    await ApplyOneAsync(
                        local,
                        cloud,
                        keepLocal,
                        SavePathCompat.GetRunSavePath(profile, "current_run_mp.save")
                    );
                }
            }
        }
        finally
        {
            UserDataPathProvider.IsRunningModded = wasModded;
        }
    }

    // Save Manager per-profile apply: scoped to ONE (profile × modded) slot,
    // unlike ApplyChosenSideAsync above which walks all 3 profiles × 2 mod
    // states. Covers progress.save, current_run(.save/_mp.save), prefs, and
    // history/*.run for that slot only — the data-safety guarantee is that
    // the other 5 slots are never even resolved to a path here, so a
    // KeepLocal/KeepCloud pick for profile2 physically cannot touch profile1.
    private static async Task ApplyChosenSideForSlotAsync(
        ISaveStore local,
        SteamKit2CloudSaveStore cloud,
        bool keepLocal,
        int profile,
        bool modded
    )
    {
        var wasModded = UserDataPathProvider.IsRunningModded;
        try
        {
            UserDataPathProvider.IsRunningModded = modded;

            await ApplyProgressWithGateAsync(
                local,
                cloud,
                keepLocal,
                SavePathCompat.GetProgressPathForProfile(profile)
            );
            await ApplyOneAsync(
                local,
                cloud,
                keepLocal,
                SavePathCompat.GetRunSavePath(profile, "current_run.save")
            );
            await ApplyOneAsync(
                local,
                cloud,
                keepLocal,
                SavePathCompat.GetRunSavePath(profile, "current_run_mp.save")
            );
            await ApplyOneAsync(local, cloud, keepLocal, SavePathCompat.GetPrefsPath(profile));

            // History run files: union of whatever exists on either side so a
            // file that only lives on the "keep" side isn't silently skipped
            // (each side's own file listing only sees what it itself has).
            var historyPaths = new HashSet<string>(
                CloudSyncCoordinator.GetHistoryFilePathsForProfile(local, profile)
            );
            historyPaths.UnionWith(
                CloudSyncCoordinator.GetHistoryFilePathsForProfile(cloud, profile)
            );
            foreach (var histPath in historyPaths)
                await ApplyOneAsync(local, cloud, keepLocal, histPath);
        }
        finally
        {
            UserDataPathProvider.IsRunningModded = wasModded;
        }
    }

    // P0-2 — routes the progress.save leg of a KeepLocal apply through
    // ProgressRecoveryGate first (no-op unless this session's progress load
    // underwent recovery — see SaveDiagnosticPatches.ProgressLoadRecovered).
    // A decline skips ONLY this one progress.save push; current_run/prefs/
    // history (called separately, right after this, in both callers above)
    // are untouched by the gate and always proceed normally. KeepCloud
    // (!keepLocal) never gates — pulling from cloud never risks the cloud copy.
    private static async Task ApplyProgressWithGateAsync(
        ISaveStore local,
        SteamKit2CloudSaveStore cloud,
        bool keepLocal,
        string progressPath
    )
    {
        if (keepLocal && !await ProgressRecoveryGate.ShouldAllowPushAsync(cloud, progressPath))
        {
            PatchHelper.Log(
                $"[Cloud] Apply: skipped progress push for {progressPath} (recovered-session, user declined)"
            );
            return;
        }
        await ApplyOneAsync(local, cloud, keepLocal, progressPath);
    }

    private static async Task ApplyOneAsync(
        ISaveStore local,
        SteamKit2CloudSaveStore cloud,
        bool keepLocal,
        string path
    )
    {
        try
        {
            if (keepLocal)
            {
                if (local.FileExists(path))
                {
                    var content = local.ReadFile(path);
                    cloud.WriteFile(path, content);
                    // SteamKit2CloudSaveStore stamps cloud mtime to NOW on
                    // write; sync local to NOW too so DetermineAsync sees
                    // identical mtimes on the next pass.
                    local.SetLastModifiedTime(path, DateTimeOffset.UtcNow);
                    PatchHelper.Log($"[Cloud] Conflict apply: pushed {path}");
                }
                else if (IsEphemeralRunPath(path) && cloud.FileExists(path))
                {
                    // Issue #31: user chose "Keep Local" but local has no current_run
                    // (run completed locally). Mirror that to cloud so the other device
                    // doesn't see a zombie run.
                    cloud.DeleteFile(path);
                    PatchHelper.Log(
                        $"[Cloud] Conflict apply: deleted cloud {path} (local cleared run)"
                    );
                }
            }
            else
            {
                if (cloud.FileExists(path))
                {
                    var content = await cloud.ReadFileAsync(path);
                    var cloudTime = cloud.GetLastModifiedTime(path);
                    await local.WriteFileAsync(path, content);
                    local.SetLastModifiedTime(path, cloudTime);
                    PatchHelper.Log($"[Cloud] Conflict apply: pulled {path}");
                }
                else if (IsEphemeralRunPath(path) && local.FileExists(path))
                {
                    // Issue #31: user chose "Keep Cloud" but cloud has no current_run
                    // (run completed on the other device). Mirror that locally so the
                    // game doesn't show a "Continue" zombie run.
                    CloudSyncCoordinator.DeleteEphemeralLocalWithBackup(local, path);
                    PatchHelper.Log(
                        $"[Cloud] Conflict apply: deleted local {path} (cloud cleared run)"
                    );
                }
            }
        }
        catch (Exception ex)
        {
            // Issue #31: same stale-cache fallback as ManualPullAllAsync.
            // FileExists may say cloud has it while ClientFileDownload returns
            // FileNotFound — treat as authoritative cloud-empty signal.
            if (
                !keepLocal
                && IsEphemeralRunPath(path)
                && ex.Message.Contains("FileNotFound", StringComparison.OrdinalIgnoreCase)
                && local.FileExists(path)
            )
            {
                try
                {
                    CloudSyncCoordinator.DeleteEphemeralLocalWithBackup(local, path);
                    PatchHelper.Log(
                        $"[Cloud] Conflict apply: deleted local {path} (cloud stale-cache, actually gone)"
                    );
                    return;
                }
                catch (Exception delEx)
                {
                    PatchHelper.Log(
                        $"[Cloud] Conflict apply: stale-cache delete failed for {path}: {delEx.Message}"
                    );
                }
            }
            PatchHelper.Log($"[Cloud] Conflict apply failed for {path}: {ex.Message}");
        }
    }

    // Issue #31: ephemeral per-run save files. Game deletes these on run end;
    // conflict resolution must mirror that deletion when the user picks the
    // side that lacks the file.
    private static bool IsEphemeralRunPath(string path)
    {
        var lower = path.Replace("user://", "").Replace("\\", "/").ToLowerInvariant();
        return lower.EndsWith("/current_run.save") || lower.EndsWith("/current_run_mp.save");
    }
}
