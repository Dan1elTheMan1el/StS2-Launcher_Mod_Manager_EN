using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using SteamKit2.Internal;

namespace STS2Mobile.Steam;

// In-memory cache of cloud file metadata (size, timestamp, persistence flag).
// Loaded lazily from Steam on first access with exponential backoff on failure.
public class CloudFileCache
{
    private const uint AppId = 2868840;
    private const int MaxRetries = 5;

    private readonly SteamConnection _connection;
    private readonly ConcurrentDictionary<string, CloudFileInfo> _files = new();
    private volatile bool _loaded;
    private int _retries;
    private DateTimeOffset _nextRetryTime = DateTimeOffset.MinValue;
    private readonly object _loadLock = new();

    // P0-1 — guards CleanStaleUploadBatchIfAny so it only ever runs once per
    // instance (i.e. once per app session), even though LoadFileList itself
    // can run again later (Refresh()) or be retried on failure.
    private bool _staleBatchCleanupAttempted;

    public CloudFileCache(SteamConnection connection)
    {
        _connection = connection;
    }

    public static string CanonicalizePath(string path)
    {
        return path.Replace("user://", "").Replace("\\", "/");
    }

    // True only after EnumerateUserFiles has completed at least once. Callers
    // making sync decisions must check this — when false, FileExists/GetFileSize
    // results are not authoritative and should not be used to choose between
    // push, pull, or no-op (see issue #4).
    public bool IsLoaded => _loaded;

    public bool FileExists(string path)
    {
        EnsureLoaded();
        return _files.ContainsKey(CanonicalizePath(path));
    }

    public DateTimeOffset GetLastModifiedTime(string path)
    {
        EnsureLoaded();
        return _files.TryGetValue(CanonicalizePath(path), out var info)
            ? info.Timestamp
            : DateTimeOffset.MinValue;
    }

    public int GetFileSize(string path)
    {
        EnsureLoaded();
        return _files.TryGetValue(CanonicalizePath(path), out var info) ? info.Size : 0;
    }

    // issue #81 계측: enumerate 가 준 file_sha(raw 내용 SHA1 hex). 미로드/미존재/
    // 로컬-write 로만 채워진 항목은 null. 진단 전용.
    public string GetSha(string path)
    {
        EnsureLoaded();
        return _files.TryGetValue(CanonicalizePath(path), out var info) ? info.Sha : null;
    }

    public bool HasCloudFiles()
    {
        EnsureLoaded();
        if (!_loaded)
            return true; // Assume cloud has files to prevent destructive sync
        return _files.Count > 0;
    }

    public void ForgetFile(string path)
    {
        if (_files.TryGetValue(CanonicalizePath(path), out var info))
            info.Persisted = false;
    }

    public bool IsFilePersisted(string path)
    {
        return _files.TryGetValue(CanonicalizePath(path), out var info) && info.Persisted;
    }

    public void Set(string path, int size, DateTimeOffset timestamp)
    {
        _files[CanonicalizePath(path)] = new CloudFileInfo { Size = size, Timestamp = timestamp };
    }

    public void Remove(string path)
    {
        _files.TryRemove(CanonicalizePath(path), out _);
    }

    public string[] GetFilesInDirectory(string directoryPath)
    {
        directoryPath = CanonicalizePath(directoryPath);
        EnsureLoaded();

        var prefix = directoryPath.Length > 0 ? directoryPath + "/" : "";
        var result = new List<string>();

        foreach (var key in _files.Keys)
        {
            if (key.StartsWith(prefix) && key.Length > prefix.Length)
            {
                var remainder = key.Substring(prefix.Length);
                if (!remainder.Contains('/') && !remainder.Contains('\\'))
                    result.Add(remainder);
            }
        }

        return result.ToArray();
    }

    public string[] GetDirectoriesInDirectory(string directoryPath)
    {
        directoryPath = CanonicalizePath(directoryPath);
        EnsureLoaded();

        var prefix = directoryPath.Length > 0 ? directoryPath + "/" : "";
        var dirs = new HashSet<string>();

        foreach (var key in _files.Keys)
        {
            if (key.StartsWith(prefix) && key.Length > prefix.Length)
            {
                var remainder = key.Substring(prefix.Length);
                var slashIndex = remainder.IndexOf('/');
                if (slashIndex >= 0)
                    dirs.Add(remainder.Substring(0, slashIndex));
            }
        }

        return [.. dirs];
    }

    public void Refresh()
    {
        _files.Clear();
        _loaded = false;
        _retries = 0;
        _nextRetryTime = DateTimeOffset.MinValue;
        EnsureLoaded();
    }

    // P1-1 (A1) — rollback-safe variant for callers that must never let a
    // failed re-enumerate leave the cache emptier than it was before calling
    // this. Refresh() above unconditionally clears _files before retrying —
    // if that retry then fails (transient network hiccup), every
    // FileExists/GetFileSize call for the rest of the caller's pass would see
    // an EMPTY cache instead of just stale data, which could misfire a sync
    // decision into "cloud has nothing, push everything". This snapshots the
    // current (good) state first and rolls back to it if the fresh enumerate
    // doesn't actually succeed, so a failed refresh degrades to exactly the
    // pre-refresh stale-snapshot behavior instead of an empty one.
    public void SafeRefresh()
    {
        if (!_loaded)
        {
            // Nothing successfully loaded yet this session — nothing to
            // protect, just attempt a normal load.
            EnsureLoaded();
            return;
        }

        var previousFiles = new Dictionary<string, CloudFileInfo>(_files);
        Refresh();

        if (!_loaded)
        {
            PatchHelper.Log("[Cloud] Refresh failed, restoring previous cache snapshot");
            _files.Clear();
            foreach (var kvp in previousFiles)
                _files[kvp.Key] = kvp.Value;
            _loaded = true;
        }
    }

    // Drives EnumerateUserFiles on a worker thread and waits for either success
    // or MaxRetries exhaustion. The launcher gates SaveManager construction on
    // this — when it returns false, the launcher falls back to a local-only
    // SaveManager so the game's writes never reach the cloud (issue #4).
    public async Task<bool> WaitForLoadAsync(int timeoutMs = 15_000)
    {
        if (_loaded)
            return true;

        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        await Task.Run(EnsureLoaded).ConfigureAwait(false);

        while (!_loaded && _retries < MaxRetries && DateTimeOffset.UtcNow < deadline)
        {
            var sleepMs = (int)
                Math.Min(
                    1_000,
                    Math.Max(50, (_nextRetryTime - DateTimeOffset.UtcNow).TotalMilliseconds)
                );
            await Task.Delay(sleepMs).ConfigureAwait(false);
            await Task.Run(EnsureLoaded).ConfigureAwait(false);
        }

        return _loaded;
    }

    private void EnsureLoaded()
    {
        if (_loaded)
            return;
        if (_retries >= MaxRetries)
            return;
        if (_retries > 0 && DateTimeOffset.UtcNow < _nextRetryTime)
            return;

        lock (_loadLock)
        {
            if (_loaded || _retries >= MaxRetries)
                return;
            if (_retries > 0 && DateTimeOffset.UtcNow < _nextRetryTime)
                return;

            try
            {
                LoadFileList();
                _loaded = true;
            }
            catch (Exception ex)
            {
                _retries++;
                var backoffSeconds = Math.Pow(2, _retries);
                _nextRetryTime = DateTimeOffset.UtcNow.AddSeconds(backoffSeconds);
                PatchHelper.Log(
                    $"[Cloud] Failed to enumerate cloud files (attempt {_retries}/{MaxRetries}): {ex.Message}"
                );

                if (_retries >= MaxRetries)
                    PatchHelper.Log(
                        "[Cloud] Max retries reached for cloud file enumeration this session."
                    );
            }
        }
    }

    private void LoadFileList()
    {
        uint startIndex = 0;
        const uint pageSize = 500;

        while (true)
        {
            var result = _connection
                .SendCloud<CCloud_EnumerateUserFiles_Request, CCloud_EnumerateUserFiles_Response>(
                    "EnumerateUserFiles",
                    new CCloud_EnumerateUserFiles_Request
                    {
                        appid = AppId,
                        start_index = startIndex,
                        count = pageSize,
                    }
                )
                .GetAwaiter()
                .GetResult();

            if (result.files == null || result.files.Count == 0)
                break;

            foreach (var file in result.files)
            {
                _files[file.filename] = new CloudFileInfo
                {
                    Size = (int)file.file_size,
                    Timestamp = DateTimeOffset.FromUnixTimeSeconds((long)file.timestamp),
                    Sha = file.file_sha, // issue #81: 다운로드 없는 동일성 판정용
                };
            }

            startIndex += (uint)result.files.Count;
            if (result.files.Count < pageSize)
                break;
        }

        PatchHelper.Log($"[Cloud] Enumerated {_files.Count} cloud files");

        // P0-1 — this is the first confirmed-successful cloud RPC of the
        // session (connection is up, logged on, and just proved it works), so
        // it's the safest point to also check for a batch left open by a
        // prior session that died mid-upload. See PendingUploadBatch for why.
        CleanStaleUploadBatchIfAny();
    }

    // Best-effort, once per session: if a marker from PendingUploadBatch.Mark
    // survived (previous session ended before EndSaveBatch's finally could
    // call CompleteAppUploadBatchBlocking), close that batch now with a
    // failure eresult so Steam stops treating it as still-pending. Whether
    // this RPC succeeds or fails, the marker is cleared either way — retrying
    // forever every session on a batch Steam may have already expired
    // server-side would be pointless, and the whole point of this path is
    // fail-open cleanup, not a guaranteed-delivery mechanism.
    private void CleanStaleUploadBatchIfAny()
    {
        if (_staleBatchCleanupAttempted)
            return;
        _staleBatchCleanupAttempted = true;

        if (!PendingUploadBatch.TryRead(out var staleBatchId))
            return;

        try
        {
            _connection
                .SendCloud<
                    CCloud_CompleteAppUploadBatch_Request,
                    CCloud_CompleteAppUploadBatch_Response
                >(
                    "CompleteAppUploadBatchBlocking",
                    new CCloud_CompleteAppUploadBatch_Request
                    {
                        appid = AppId,
                        batch_id = staleBatchId,
                        batch_eresult = (uint)SteamKit2.EResult.Fail,
                    }
                )
                .GetAwaiter()
                .GetResult();
            PatchHelper.Log($"[Cloud] Cleaned stale upload batch {staleBatchId}");
        }
        catch (Exception ex)
        {
            PatchHelper.Log(
                $"[Cloud] Stale upload batch {staleBatchId} cleanup failed (will not retry): {ex.Message}"
            );
        }
        finally
        {
            PendingUploadBatch.Clear();
        }
    }

    private class CloudFileInfo
    {
        public int Size;
        public DateTimeOffset Timestamp;

        // issue #81 계측/최적화 후보: enumerate 의 file_sha(raw 내용 SHA1 hex).
        // 로컬 SHA1 과 비교하면 다운로드 없이 동일성 판정 가능. 로컬 write 경로의
        // Set() 는 이 값을 채우지 않으므로 null 일 수 있음(진단은 null 을 허용).
        public string Sha;
        public volatile bool Persisted = true;
    }
}
