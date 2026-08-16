using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Saves;

namespace STS2Mobile.Steam;

// P0-1 — honest outcome for a manual push/pull instead of the caller seeing
// "complete" the instant the method returns while uploads are still queued.
//   Success  — everything that should have moved, did.
//   Failed   — the operation finished running, but at least one file didn't
//              make it (per-file loop failure, or — Push only — the upload
//              batch itself reported a failure: see SteamKit2CloudSaveStore.
//              LastBatchHadFailures).
//   TimedOut — Push only: the write queue never drained within the wait
//              budget, so the fate of the in-flight/queued uploads is unknown.
public enum CloudBatchOutcome
{
    Success,
    Failed,
    TimedOut,
}

// Stateless cloud sync coordinator: auto sync and manual push/pull.
//
// Issue #36 Part A redesign: per-sync victim backups were REMOVED from this class.
// Backups are now full-tree snapshots owned by LocalBackupService — taken once per
// pre-PLAY handshake (auto) or on the user's action button (manual), not on every
// push/pull/autosync. The old LocalBackupEnabled gate and Begin/EndBackupSession
// machinery are gone with them.
public static class CloudSyncCoordinator
{
    private const int HistoryFileLimit = 100;

    // issue #81 — Game's cloud history cap limit (MegaCrit.Sts2.Core.Saves.Managers.
    // RunHistorySaveManager: maxCloudFileCount=100, byteLimit=5MB). Launcher-side trim has the same limit.
    private const int CloudHistoryFileCap = 100;
    private const long CloudHistoryByteCap = 5L * 1024 * 1024;

    public static async Task PushFileAsync(ISaveStore local, ICloudSaveStore cloud, string path)
    {
        if (!local.FileExists(path))
            return;

        string content = local.ReadFile(path);

        if (cloud.FileExists(path))
        {
            string cloudContent = await cloud.ReadFileAsync(path);
            if (content == cloudContent)
            {
                PatchHelper.Log($"[Cloud] Push: skipping {path} (identical)");
                return;
            }
        }

        cloud.WriteFile(path, content);
        PatchHelper.Log($"[Cloud] Push: uploaded {path}");
    }

    public static async Task PullFileAsync(ISaveStore local, ICloudSaveStore cloud, string path)
    {
        if (!cloud.FileExists(path))
            return;

        string cloudContent = await cloud.ReadFileAsync(path);

        if (local.FileExists(path))
        {
            string localContent = local.ReadFile(path);
            if (localContent == cloudContent)
            {
                PatchHelper.Log($"[Cloud] Pull: skipping {path} (identical)");
                return;
            }
        }

        var pullTime = cloud.GetLastModifiedTime(path);
        await local.WriteFileAsync(path, cloudContent);
        local.SetLastModifiedTime(path, pullTime);
        PatchHelper.Log($"[Cloud] Pull: downloaded {path}");
    }

    // Uses content comparison only — timestamps are unreliable on mobile (game init
    // rewrites files, OS touches metadata). Progress/run files use SaveProgressComparer;
    // non-progress files default to cloud wins; history files sync bidirectionally.
    public static async Task AutoSyncFileAsync(ISaveStore local, ICloudSaveStore cloud, string path)
    {
        try
        {
            bool cloudExists = cloud.FileExists(path);
            bool localExists = local.FileExists(path);

            if (cloudExists && localExists)
            {
                // issue #81 — If immutable history runs exist on both sides with identical content (filename = unique run id),
                // skip them without downloading or comparing. Eliminates massive sequential round-trips where game cloud sync
                // used to download hundreds of histories individually (ClientFileDownload+HTTP) every time — resolving the main cause of slow sync.
                if (IsHistoryRunFile(path))
                    return;
                string localContent = local.ReadFile(path);
                string cloudContent = await cloud.ReadFileAsync(path);

                if (IsCorrupt(localContent))
                {
                    PatchHelper.Log($"[Cloud] Sync: local {path} is corrupt, pulling from cloud");
                    Issue7Diagnostics.LogIsCorruptDetected(path, localContent);
                    var cloudTime = cloud.GetLastModifiedTime(path);
                    await local.WriteFileAsync(path, cloudContent);
                    local.SetLastModifiedTime(path, cloudTime);
                    return;
                }

                if (localContent == cloudContent)
                {
                    PatchHelper.Log($"[Cloud] Sync: {path} identical, skipping");
                    return;
                }

                var result = SaveProgressComparer.Compare(path, localContent, cloudContent);

                if (result == CompareResult.CloudWins)
                {
                    PatchHelper.Log($"[Cloud] Sync: cloud wins for {path}");
                    Issue7Diagnostics.LogCurrentRunSyncDetail(
                        path,
                        localContent,
                        cloudContent,
                        "CloudWins"
                    );
                    var cloudTime = cloud.GetLastModifiedTime(path);
                    await local.WriteFileAsync(path, cloudContent);
                    local.SetLastModifiedTime(path, cloudTime);
                }
                else if (result == CompareResult.LocalWins)
                {
                    PatchHelper.Log($"[Cloud] Sync: local wins for {path}, uploading");
                    Issue7Diagnostics.LogCurrentRunSyncDetail(
                        path,
                        localContent,
                        cloudContent,
                        "LocalWins"
                    );
                    cloud.WriteFile(path, localContent);
                }
                else
                {
                    // Cloud wins on equal progress or non-progress files to preserve PC as primary.
                    PatchHelper.Log($"[Cloud] Sync: contents differ for {path}, cloud wins");
                    Issue7Diagnostics.LogCurrentRunSyncDetail(
                        path,
                        localContent,
                        cloudContent,
                        "EqualOrNonProgress→CloudWins"
                    );
                    var cloudTime = cloud.GetLastModifiedTime(path);
                    await local.WriteFileAsync(path, cloudContent);
                    local.SetLastModifiedTime(path, cloudTime);
                }
            }
            else if (cloudExists)
            {
                Issue7Diagnostics.LogCurrentRunSyncDetail(path, null, null, "CloudOnly→Pull");
                await PullFileAsync(local, cloud, path);
            }
            else if (localExists)
            {
                // issue #81 — History runs are uploaded to cloud by the game write path (RunHistorySaveManager →
                // WriteFile) upon completion. If auto-sync pushes local-only runs back, old runs forgotten/trimmed
                // by the cap will be resurrected, invalidating the cloud history cap (ping-pong).
                // Therefore, do not push local-only history runs. Push mutable files like progress/current_run/prefs as before.
                if (IsHistoryRunFile(path))
                {
                    PatchHelper.Log(
                        $"[Cloud] Sync: skip push for local-only history run {path} (cloud cap)"
                    );
                    return;
                }
                Issue7Diagnostics.LogCurrentRunSyncDetail(path, null, null, "LocalOnly→Push");
                await PushFileAsync(local, cloud, path);
            }
            // (neither exists — no-op)
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Cloud] Sync failed for {path}: {ex.Message}");
        }
    }

    // Genuinely async (unlike before P0-2): the recovered-session gate below
    // needs to await a user confirmation dialog without ever blocking a
    // thread synchronously (see ProgressRecoveryGate). Still only ever called
    // from inside a Task.Run (LauncherController), so the rest of the body
    // stays exactly as blocking as it was — this is what turns EndSaveBatch's
    // fire-and-forget enqueue into an honest, awaitable result instead of the
    // previous "returns instantly, always says complete" lie.
    // progress (issue #64): forwarded to EndSaveBatch — per-file upload
    // progress from the batch loop, reported on the CloudSaveWriter thread.
    // onPhase (issue #81): Notifies the UI of progress phase phrases ("Cleaning up cloud" / "Applying to cloud").
    // Combined with progress's (done, total) to show detailed progress without causing freezing misconceptions. Defaults to null,
    // so existing callers (ProfileCopyFlow) are unaffected.
    public static async Task<CloudBatchOutcome> ManualPushAllAsync(
        string accountName,
        string refreshToken,
        IProgress<(int done, int total)> progress = null,
        Action<string> onPhase = null
    )
    {
        var localStore = new GodotFileIo(UserDataPathProvider.GetAccountScopedBasePath(null));
        var cloudStore =
            SteamKit2CloudSaveStore.Instance
            ?? new SteamKit2CloudSaveStore(accountName, refreshToken);

        // P0-1: force the cloud file cache to load now (if it hasn't already
        // this session) BEFORE opening a new batch below. Cache-loading is
        // what triggers the stale-upload-batch cleanup (see CloudFileCache.
        // LoadFileList / CleanStaleUploadBatchIfAny). Pull gets this for free
        // via GetSaveFilePaths(cloudStore) touching the cache first, but Push
        // walks the LOCAL store for its path list and — when every local file
        // is non-trivial in size — WriteFile's GuardB never touches the cache
        // either. Without this, pressing Push as the very first cloud action
        // of a fresh launcher session could open a new batch while a batch
        // left dangling by a prior session's crash is still open server-side.
        await cloudStore.WaitForCacheReadyAsync(15_000).ConfigureAwait(false);

        var paths = GetSaveFilePaths(localStore);
        PatchHelper.Log($"[Cloud] Push: starting ({paths.Count} files)");

        // issue #81 — Trim cloud history to 100/5MB before uploading to secure quota.
        // Self-heals the state where new uploads were completely failing with LimitExceeded because
        // the quota was full due to backlogs (~1000 files for reporter). Since trim deletions are queued to the serial _writeQueue first
        // and batch uploads are queued after them (FIFO), uploads start after deletions complete and quota is freed.
        // Since mass deletion (~900 for reporter) takes a while, wait here polling progress until deletions actually drain
        // (prevents progress-freezing misconceptions + confirms quota before uploading).
        int trimmed = 0;
        try
        {
            trimmed = TrimCloudHistoryToCap(cloudStore);
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Cloud] Push: history trim failed (continuing): {ex.Message}");
        }
        if (trimmed > 0)
        {
            onPhase?.Invoke("Cleaning up cloud");
            DrainQueueWithProgress(cloudStore, trimmed, progress, capMs: 900_000);
        }

        onPhase?.Invoke("Applying to cloud");
        cloudStore.BeginSaveBatch();
        int count = 0;
        int deletedCloud = 0;
        bool anyLoopFailure = false;
        foreach (var path in paths)
        {
            try
            {
                if (!localStore.FileExists(path))
                {
                    if (IsEphemeralRunFile(path) && cloudStore.FileExists(path))
                    {
                        cloudStore.DeleteFile(path);
                        PatchHelper.Log($"[Cloud] Push: deleted cloud {path} (local cleared run)");
                        deletedCloud++;
                    }
                    continue;
                }

                // issue #81 — If immutable history runs already exist on the cloud, skip without read/compression/RPC.
                // Since it's a complete snapshot where filename = unique run id, existence on both sides guarantees identical content.
                // Previously, every file required read+compression followed by ClientBeginFileUpload and a DuplicateRequest
                // to determine "already up to date", making mass round-trips for hundreds of files slow.
                if (IsHistoryRunFile(path) && cloudStore.FileExists(path))
                    continue;

                // P0-2 — a recovered-session progress.save push gets a
                // one-time user confirmation before it's allowed to overwrite
                // an existing cloud copy (see ProgressRecoveryGate). No-op
                // (returns true immediately) unless this session's load
                // actually underwent recovery.
                if (
                    IsProgressFile(path)
                    && !await ProgressRecoveryGate
                        .ShouldAllowPushAsync(cloudStore, path)
                        .ConfigureAwait(false)
                )
                {
                    PatchHelper.Log(
                        $"[Cloud] Push: skipped {path} (recovered-session, user declined)"
                    );
                    continue;
                }

                string content = localStore.ReadFile(path);
                PatchHelper.Log($"[Cloud] Push: queuing {path} ({content.Length} bytes)");
                cloudStore.WriteFile(path, content);
                count++;
            }
            catch (Exception ex)
            {
                PatchHelper.Log($"[Cloud] Push: failed for {path}: {ex.Message}");
                anyLoopFailure = true;
            }
        }
        cloudStore.EndSaveBatch(progress);

        // P0-1: EndSaveBatch only enqueues the batch upload — without waiting
        // for it to drain, "complete" below would still be the same lie the
        // UI used to see. Flush blocks until the write queue (the batch
        // upload plus any mirror-deletes queued above) finishes or the budget
        // runs out; only once it reports drained can LastBatchHadFailures be
        // trusted (see SteamKit2CloudSaveStore.Flush / EndSaveBatch).
        //
        // issue #81 — Increased cap from 120 seconds to 15 minutes. If backlog trimming + mass uploads exceeded 120 seconds,
        // the UI would prematurely release (freeze released) even though work continued in the background, making it look like a failure.
        // Since deletes/uploads each retry 3 times before failing and skipping, the queue is guaranteed to empty in finite time
        // (not an infinite wait). EndSaveBatch's per-file progress continues to flow to the UI during the wait.
        bool drained = cloudStore.Flush(timeoutMs: 900_000);

        CloudBatchOutcome outcome;
        if (!drained)
        {
            outcome = CloudBatchOutcome.TimedOut;
            PatchHelper.Log("[Cloud] Push: timed out waiting for upload queue to drain");
        }
        else if (anyLoopFailure || cloudStore.LastBatchHadFailures)
        {
            outcome = CloudBatchOutcome.Failed;
        }
        else
        {
            outcome = CloudBatchOutcome.Success;
        }

        PatchHelper.Log(
            $"[Cloud] Push complete: {count} files batched for upload, {deletedCloud} cloud files "
                + $"mirror-deleted, outcome={outcome}"
        );
        return outcome;
    }

    // Downloads/local writes use inline await, guaranteeing completion upon return (no TimedOut). issue #81:
    // Only cloud history trimming uses the write queue, and it awaits drainage before downloads begin.
    // progress/onPhase (issue #81): Same contract as push. Defaults to null, leaving existing callers unaffected.
    public static async Task<CloudBatchOutcome> ManualPullAllAsync(
        string accountName,
        string refreshToken,
        IProgress<(int done, int total)> progress = null,
        Action<string> onPhase = null
    )
    {
        var localStore = new GodotFileIo(UserDataPathProvider.GetAccountScopedBasePath(null));
        var cloudStore =
            SteamKit2CloudSaveStore.Instance
            ?? new SteamKit2CloudSaveStore(accountName, refreshToken);

        // Ensure cache is loaded first so trim can evaluate the entire cloud (same as push).
        await cloudStore.WaitForCacheReadyAsync(15_000).ConfigureAwait(false);

        // issue #81 — Trim cloud history to 100/5MB on pull as well to heal backlogs
        // (placed on both push and pull so it self-heals via whichever cloud operation is performed).
        // Trim removes items only from the cloud server/cache (local untouched). The download list below is based on the post-trim cache,
        // so it won't try to re-download old runs that were just deleted.
        int trimmed = 0;
        try
        {
            trimmed = TrimCloudHistoryToCap(cloudStore);
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Cloud] Pull: history trim failed (continuing): {ex.Message}");
        }
        if (trimmed > 0)
        {
            onPhase?.Invoke("Cleaning up cloud");
            DrainQueueWithProgress(cloudStore, trimmed, progress, capMs: 900_000);
        }

        onPhase?.Invoke("Receiving from cloud");
        var paths = GetSaveFilePaths(cloudStore);
        PatchHelper.Log($"[Cloud] Pull: starting ({paths.Count} files)");

        int downloaded = 0;
        int skipped = 0;
        int deletedLocal = 0;
        int done = 0;
        bool anyFailure = false;
        foreach (var path in paths)
        {
            done++;
            progress?.Report((done, paths.Count));
            try
            {
                if (!cloudStore.FileExists(path))
                {
                    if (IsEphemeralRunFile(path) && localStore.FileExists(path))
                    {
                        DeleteEphemeralLocalWithBackup(localStore, path);
                        PatchHelper.Log($"[Cloud] Pull: deleted local {path} (cloud cleared run)");
                        deletedLocal++;
                    }
                    else
                    {
                        skipped++;
                    }
                    continue;
                }
                // issue #81 — If local already has the immutable history run, skip download.
                // Since filename = unique run id complete snapshot, content equality is guaranteed → eliminates
                // mass sequential round-trips (slow pull) that re-downloaded every time. (Mutable files are downloaded as before.)
                if (IsHistoryRunFile(path) && localStore.FileExists(path))
                {
                    skipped++;
                    continue;
                }
                PatchHelper.Log($"[Cloud] Pull: downloading {path}");
                var pullTime = cloudStore.GetLastModifiedTime(path);
                string content = await cloudStore.ReadFileAsync(path);
                await localStore.WriteFileAsync(path, content);
                localStore.SetLastModifiedTime(path, pullTime);
                PatchHelper.Log($"[Cloud] Pull: wrote {path} ({content.Length} bytes)");
                downloaded++;
            }
            catch (Exception ex)
            {
                // Issue #31: stale-cache fallback. Steam's EnumerateUserFiles RPC
                // keeps remotely-deleted files in the manifest for a while after
                // the actual storage is wiped, so cloudStore.FileExists can return
                // true while ClientFileDownload returns FileNotFound. The download
                // failure is the authoritative signal that cloud is empty — mirror
                // that locally for ephemeral run files.
                if (
                    IsEphemeralRunFile(path)
                    && ex.Message.Contains("FileNotFound", StringComparison.OrdinalIgnoreCase)
                    && localStore.FileExists(path)
                )
                {
                    try
                    {
                        DeleteEphemeralLocalWithBackup(localStore, path);
                        deletedLocal++;
                        PatchHelper.Log(
                            $"[Cloud] Pull: deleted local {path} (cloud stale-cache, actually gone)"
                        );
                    }
                    catch (Exception delEx)
                    {
                        PatchHelper.Log(
                            $"[Cloud] Pull: stale-cache delete failed for {path}: {delEx.Message}"
                        );
                        anyFailure = true;
                    }
                }
                else
                {
                    PatchHelper.Log($"[Cloud] Pull: failed for {path}: {ex.Message}");
                    anyFailure = true;
                }
            }
        }

        var outcome = anyFailure ? CloudBatchOutcome.Failed : CloudBatchOutcome.Success;
        PatchHelper.Log(
            $"[Cloud] Pull complete: {downloaded} downloaded, {skipped} not in cloud, "
                + $"{deletedLocal} local files mirror-deleted, outcome={outcome}"
        );
        return outcome;
    }

    // issue #81 — Launcher-side cloud history cap. While the game's RunHistorySaveManager cap (100/5MB)
    // now works during play via actual ForgetFile implementation, actively cleaning up (1) reporters whose games crash and won't launch,
    // and (2) backlogs that already exceeded the quota (~1000 files for reporter, 174 for user) requires trimming in the launcher
    // independently of the game. Reduces each (profile × mode) history to newest-100/5MB, removing from the cloud only
    // starting from the oldest runs via cloudStore.DeleteFile (=ClientDeleteFile, local GodotFileIo untouched).
    // Return value = number of deleted runs. Serial _writeQueue processes mass items sequentially as well.
    public static int TrimCloudHistoryToCap(ICloudSaveStore cloud)
    {
        int totalDeleted = 0;
        var wasModded = UserDataPathProvider.IsRunningModded;
        try
        {
            foreach (bool modded in new[] { false, true })
            {
                UserDataPathProvider.IsRunningModded = modded;
                for (int profile = 1; profile <= 3; profile++)
                {
                    totalDeleted += TrimOneHistoryDir(
                        cloud,
                        SavePathCompat.GetHistoryPath(profile)
                    );
                }
            }
        }
        finally
        {
            UserDataPathProvider.IsRunningModded = wasModded;
        }
        if (totalDeleted > 0)
            PatchHelper.Log(
                $"[Cloud] History cap: trimmed {totalDeleted} old run(s) from cloud (cap {CloudHistoryFileCap}/5MB)"
            );
        return totalDeleted;
    }

    private static int TrimOneHistoryDir(ICloudSaveStore cloud, string historyDir)
    {
        string[] files;
        try
        {
            files = cloud.GetFilesInDirectory(historyDir);
        }
        catch
        {
            return 0;
        }

        // Only .run (excluding .backup/.tmp). Same as the game: sort by GetLastModifiedTime descending,
        // then remove starting from the oldest (end of list) until both file count and byte limits are met.
        var runs = files
            .Where(f => f.EndsWith(".run") && !f.EndsWith(".backup") && !f.EndsWith(".tmp"))
            .Select(f => $"{historyDir}/{f}")
            .OrderByDescending(p => cloud.GetLastModifiedTime(p))
            .ToList();

        long totalBytes = 0;
        foreach (var p in runs)
            totalBytes += cloud.GetFileSize(p);

        int count = runs.Count;
        int deleted = 0;
        int oldestIdx = runs.Count - 1;
        while ((count > CloudHistoryFileCap || totalBytes > CloudHistoryByteCap) && oldestIdx >= 0)
        {
            var victim = runs[oldestIdx];
            totalBytes -= cloud.GetFileSize(victim);
            count--;
            cloud.DeleteFile(victim);
            deleted++;
            oldestIdx--;
        }
        return deleted;
    }

    // issue #81 — Wait for the write queue to empty while polling progress (prevents progress-freezing
    // misconceptions). total = number of queue items at the start of waiting (number of trim deletions). Since deletions end after finite retries
    // for both success/failure, PendingWriteCount is guaranteed to reach 0. capMs is a safety ceiling to prevent infinite waiting
    // (drains before reaching it in normal paths).
    private static void DrainQueueWithProgress(
        SteamKit2CloudSaveStore store,
        int total,
        IProgress<(int done, int total)> progress,
        int capMs
    )
    {
        long deadline = Environment.TickCount64 + capMs;
        while (store.PendingWriteCount > 0 && Environment.TickCount64 < deadline)
        {
            int done = Math.Max(0, total - store.PendingWriteCount);
            progress?.Report((done, total));
            System.Threading.Thread.Sleep(250);
        }
        if (store.PendingWriteCount == 0)
            progress?.Report((total, total));
    }

    public static List<string> GetSaveFilePaths(ISaveStore store)
    {
        var paths = new List<string>();
        CollectProfilePaths(paths, store.GetFilesInDirectory, store.DirectoryExists);
        return paths;
    }

    public static List<string> GetSaveFilePaths(ICloudSaveStore store)
    {
        var paths = new List<string>();
        CollectProfilePaths(paths, store.GetFilesInDirectory, store.DirectoryExists);
        return paths;
    }

    // Save Manager per-profile apply: history run files for ONE profile, under
    // whatever mod state UserDataPathProvider.IsRunningModded is currently set
    // to (caller owns the toggle — same convention as SavePathCompat callers
    // elsewhere in this file). Unlike GetSaveFilePaths, this never walks all
    // 3 profiles × 2 mod states — the whole point is to scope a resolve to a
    // single slot so it can't touch the other 5.
    public static List<string> GetHistoryFilePathsForProfile(ISaveStore store, int profileId)
    {
        var paths = new List<string>();
        AddHistoryFiles(paths, store.GetFilesInDirectory, store.DirectoryExists, profileId);
        return paths;
    }

    public static List<string> GetHistoryFilePathsForProfile(ICloudSaveStore store, int profileId)
    {
        var paths = new List<string>();
        AddHistoryFiles(paths, store.GetFilesInDirectory, store.DirectoryExists, profileId);
        return paths;
    }

    // Collects save paths for both vanilla and modded profile directories.
    private static void CollectProfilePaths(
        List<string> paths,
        Func<string, string[]> getFiles,
        Func<string, bool> dirExists
    )
    {
        var wasModded = UserDataPathProvider.IsRunningModded;
        try
        {
            foreach (bool modded in new[] { false, true })
            {
                UserDataPathProvider.IsRunningModded = modded;
                for (int i = 1; i <= 3; i++)
                {
                    paths.Add(SavePathCompat.GetProgressPathForProfile(i));
                    paths.Add(SavePathCompat.GetRunSavePath(i, "current_run.save"));
                    paths.Add(SavePathCompat.GetRunSavePath(i, "current_run_mp.save"));
                    paths.Add(SavePathCompat.GetPrefsPath(i));
                    AddHistoryFiles(paths, getFiles, dirExists, i);
                }
            }
        }
        finally
        {
            UserDataPathProvider.IsRunningModded = wasModded;
        }
    }

    private static void AddHistoryFiles(
        List<string> paths,
        Func<string, string[]> getFiles,
        Func<string, bool> dirExists,
        int profileId
    )
    {
        var historyDir = SavePathCompat.GetHistoryPath(profileId);
        if (!dirExists(historyDir))
            return;

        var runFiles = getFiles(historyDir)
            .Where(f => f.EndsWith(".run") && !f.EndsWith(".backup") && !f.EndsWith(".tmp"))
            .OrderByDescending(f => f) // Filenames are Unix timestamps — descending = newest first
            .Take(HistoryFileLimit);

        foreach (var file in runFiles)
            paths.Add($"{historyDir}/{file}");
    }

    // Save files are JSON; a non-JSON opener indicates corruption (e.g., unencrypted write).
    private static bool IsCorrupt(string content)
    {
        if (string.IsNullOrEmpty(content))
            return false;
        return content[0] != '{' && content[0] != '[';
    }

    // Issue #31: ephemeral per-run save files. The game deletes these from cloud
    // when a run ends (clear/abandon) — manual Pull/Push must mirror that deletion
    // to the other side so completed runs don't reappear as "Continue" zombies.
    // progress.save is intentionally excluded: it's persistent meta progress and
    // mirror-deleting it would risk catastrophic data loss on fresh-install pushes.
    internal static bool IsEphemeralRunFile(string path)
    {
        var lower = path.Replace("user://", "").Replace("\\", "/").ToLowerInvariant();
        return lower.EndsWith("/current_run.save") || lower.EndsWith("/current_run_mp.save");
    }

    // issue #81 — history/<Unix ts>.run history record. Target for cloud cap and target to prevent
    // re-push in auto-sync (prevents ping-pong). current_run.save is not history (ephemeral).
    internal static bool IsHistoryRunFile(string path)
    {
        var lower = path.Replace("user://", "").Replace("\\", "/").ToLowerInvariant();
        return lower.Contains("/history/") && lower.EndsWith(".run") && !lower.EndsWith(".backup");
    }

    // P0-2: identifies progress.save among the various per-profile paths
    // ManualPushAllAsync walks, so only that file (not current_run/prefs/
    // history) gets routed through ProgressRecoveryGate. Same test
    // SaveProgressComparer.cs already uses to recognize progress.save.
    private static bool IsProgressFile(string path)
    {
        var lower = path.Replace("user://", "").Replace("\\", "/").ToLowerInvariant();
        return lower.Contains("progress") && lower.EndsWith(".save");
    }

    // RunSaveManager keeps a .backup sibling per save and falls back to it when
    // the primary is missing. Mirror-deleting the primary alone leaves the game
    // restoring the run from the backup — we must remove both.
    internal static void DeleteEphemeralLocalWithBackup(ISaveStore local, string path)
    {
        local.DeleteFile(path);
        var backupPath = path + ".backup";
        if (local.FileExists(backupPath))
        {
            try
            {
                local.DeleteFile(backupPath);
                PatchHelper.Log($"[Cloud] Mirror-delete: also removed local {backupPath}");
            }
            catch (Exception ex)
            {
                PatchHelper.Log(
                    $"[Cloud] Mirror-delete: backup removal failed for {backupPath}: {ex.Message}"
                );
            }
        }
    }
}
