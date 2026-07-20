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

    // issue #81 — 게임의 클라우드 히스토리 캡 한도(MegaCrit.Sts2.Core.Saves.Managers.
    // RunHistorySaveManager: maxCloudFileCount=100, byteLimit=5MB). 런처-측 트림도 동일 한도.
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
                // issue #81 — 불변 history run 이 양쪽에 존재하면 내용 동일(파일명=고유 run id)
                // 이므로 다운로드·비교 없이 스킵한다. 게임 클라우드 동기화가 history 수백 개를
                // 매번 개별 다운로드(ClientFileDownload+HTTP)하던 대량 순차 왕복을 제거 — 느린
                // 동기화의 주원인 해소.
                if (IsHistoryRunFile(path))
                    return;
                CloudDiag.LogDownloadNeed(local, cloud, path);
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
                // issue #81 — 히스토리 run 은 완료 시 게임 write 경로(RunHistorySaveManager →
                // WriteFile)가 클라우드에 올린다. auto-sync 가 local-only run 을 다시 push 하면,
                // 캡으로 forget/trim 한 오래된 run 이 되살아나 클라우드 히스토리 캡이 무효화된다
                // (ping-pong). 따라서 local-only 히스토리 run 은 push 하지 않는다. progress/
                // current_run/prefs 등 mutable 파일은 종전대로 push.
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
    // onPhase (issue #81): 진행 단계 문구를 UI 로 알린다("클라우드 정리 중"/"클라우드 반영 중").
    // progress 의 (done,total) 과 조합해 프리징 오해 없이 세부 진행을 보여준다. 기본 null 이라
    // 기존 호출부(ProfileCopyFlow)는 영향 없음.
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

        // issue #81 계측 (A): 클라우드에만 존재하는 stale history .run 을 개수·목록만
        // 로깅(실삭제 없음). 이 push 는 아래 로직대로 그대로 진행됨 — 동작 불변.
        try
        {
            CloudDiag.LogPruneCandidates(localStore, cloudStore);
        }
        catch (Exception ex)
        {
            CloudDiag.Log($"prune-candidate scan failed: {ex.Message}");
        }

        // issue #81 — 업로드 전에 클라우드 히스토리를 100/5MB 로 트림해 쿼터를 확보한다.
        // 백로그(제보자 ~1000개)로 쿼터가 꽉 차 신규 업로드가 LimitExceeded 로 전멸하던
        // 상태를 여기서 자가 치유한다. 트림 삭제는 직렬 _writeQueue 에 먼저 쌓이고 아래
        // 배치 업로드는 그 뒤에 쌓이므로(FIFO), 삭제가 완료돼 쿼터가 빈 뒤 업로드가 시작된다.
        // 대량 삭제(제보자 ~900)는 오래 걸리므로, 여기서 삭제가 실제로 드레인될 때까지
        // 진행률을 폴링 보고하며 대기한다(무진행 프리징 오해 방지 + 업로드 전 쿼터 확정).
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
            onPhase?.Invoke("클라우드 정리 중");
            DrainQueueWithProgress(cloudStore, trimmed, progress, capMs: 900_000);
        }

        onPhase?.Invoke("클라우드 반영 중");
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

                // issue #81 — 불변 history run 이 이미 클라우드에 있으면 read/압축/RPC 없이
                // 스킵한다. 파일명=고유 run id 인 완결 스냅샷이라 양쪽 존재 시 내용 동일 보장.
                // 종전엔 파일마다 read+압축 후 ClientBeginFileUpload 로 DuplicateRequest 를
                // 받아야 "already up to date" 판정이 나서 수백 파일 대량 왕복이 느렸다.
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
        // issue #81 — 상한을 120초 → 15분으로. 백로그 트림+대량 업로드가 120초를 넘기면
        // 작업이 백그라운드로 계속 도는데도 UI 가 조기 해제(프리즈 풀림)돼 실패처럼 보였다.
        // 삭제/업로드는 각기 3회 재시도 후 실패-스킵하므로 큐는 유한 시간에 반드시 비워진다
        // (무한 대기 아님). EndSaveBatch 의 per-file 진행률이 대기 동안 UI 로 계속 흐른다.
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

    // 다운로드/로컬 쓰기는 인라인 await 이라 반환 시 완료가 보장된다(TimedOut 없음). issue #81:
    // 클라우드 히스토리 트림만 쓰기 큐를 쓰며, 다운로드 시작 전에 드레인까지 대기한다.
    // progress/onPhase (issue #81): push 와 동일 규약. 기본 null 이라 기존 호출부 무영향.
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

        // 트림이 클라우드 전체를 보고 판단하도록 캐시 로드를 먼저 보장(push 와 동일).
        await cloudStore.WaitForCacheReadyAsync(15_000).ConfigureAwait(false);

        // issue #81 — pull 로도 클라우드 히스토리를 100/5MB 로 트림해 백로그를 치유한다
        // (어느 클라우드 동작으로든 자가 치유되도록 push/pull 양쪽에 둔다). 트림은 클라우드
        // 서버·캐시에서만 제거(로컬 미접촉). 아래 다운로드 목록은 트림 후 캐시 기준이라
        // 방금 지운 오래된 run 을 다시 받으려 하지 않는다.
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
            onPhase?.Invoke("클라우드 정리 중");
            DrainQueueWithProgress(cloudStore, trimmed, progress, capMs: 900_000);
        }

        onPhase?.Invoke("클라우드 받는 중");
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
                // issue #81 — 불변 history run 을 로컬이 이미 갖고 있으면 다운로드 스킵.
                // 파일명=고유 run id 완결 스냅샷이라 내용 동일 보장 → 매번 재다운로드하던
                // 대량 순차 왕복(느린 pull) 제거. (mutable 파일은 종전대로 다운로드.)
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

    // issue #81 — 런처-측 클라우드 히스토리 캡. 게임의 RunHistorySaveManager 캡(100/5MB)은
    // 이제 ForgetFile 실구현으로 플레이 중 작동하지만, (1) 게임이 크래시해 못 켜지는 제보자,
    // (2) 이미 쿼터를 넘긴 백로그(제보자 ~1000개, 사용자 174개)를 능동 정리하려면 게임과
    // 무관하게 런처에서도 트림이 필요하다. 각 (프로필×모드) 히스토리를 newest-100/5MB 로
    // 줄이며 오래된 run 부터 cloudStore.DeleteFile(=ClientDeleteFile, 로컬 GodotFileIo 미접촉)
    // 로 클라우드에서만 제거한다. 반환값=삭제한 run 수. 직렬 _writeQueue 라 대량도 순차 처리.
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
                    totalDeleted += TrimOneHistoryDir(cloud, SavePathCompat.GetHistoryPath(profile));
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

        // .run 만(.backup/.tmp 제외). 게임과 동일하게 GetLastModifiedTime 최신순 정렬 후
        // 오래된 것(리스트 끝)부터 제거해 파일수·바이트 두 한도 이하로.
        var runs = files
            .Where(f =>
                f.EndsWith(".run") && !f.EndsWith(".backup") && !f.EndsWith(".tmp")
            )
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

    // issue #81 — 쓰기 큐가 비워질 때까지 진행률을 폴링 보고하며 대기한다(무진행 프리징
    // 오해 방지). total = 대기 시작 시점의 큐 항목 수(트림 삭제 개수). 삭제는 성공/실패
    // 모두 유한 재시도 후 종료되므로 PendingWriteCount 는 반드시 0 에 도달한다. capMs 는
    // 무한 대기를 막는 안전 상한(정상 경로에선 도달 전에 드레인됨).
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

    // issue #81 — history/<유닉스ts>.run 히스토리 레코드. 클라우드 캡 대상이자 auto-sync 에서
    // 재-push 를 막을 대상(ping-pong 방지). current_run.save 는 히스토리가 아님(ephemeral).
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
