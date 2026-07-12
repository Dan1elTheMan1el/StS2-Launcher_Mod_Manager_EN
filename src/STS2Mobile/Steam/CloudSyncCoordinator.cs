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
    public static async Task<CloudBatchOutcome> ManualPushAllAsync(
        string accountName,
        string refreshToken,
        IProgress<(int done, int total)> progress = null
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
        bool drained = cloudStore.Flush(timeoutMs: 120_000);

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

    // Unlike Push, Pull has no batch/write-queue involved — every download and
    // local write below is already awaited in-line, so by the time this
    // method returns everything has genuinely finished (no TimedOut case).
    // The only honesty gap was the return value never reflecting per-file
    // failures — fixed by tracking anyFailure below.
    public static async Task<CloudBatchOutcome> ManualPullAllAsync(
        string accountName,
        string refreshToken
    )
    {
        var localStore = new GodotFileIo(UserDataPathProvider.GetAccountScopedBasePath(null));
        var cloudStore =
            SteamKit2CloudSaveStore.Instance
            ?? new SteamKit2CloudSaveStore(accountName, refreshToken);

        var paths = GetSaveFilePaths(cloudStore);
        PatchHelper.Log($"[Cloud] Pull: starting ({paths.Count} files)");

        int downloaded = 0;
        int skipped = 0;
        int deletedLocal = 0;
        bool anyFailure = false;
        foreach (var path in paths)
        {
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
