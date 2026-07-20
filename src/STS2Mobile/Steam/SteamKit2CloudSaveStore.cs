using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Saves;
using SteamKit2.Internal;

namespace STS2Mobile.Steam;

// ICloudSaveStore backed by SteamKit2 CCloud unified messages.
public class SteamKit2CloudSaveStore : ICloudSaveStore, ISaveStore, IDisposable
{
    private const uint AppId = 2868840;

    internal static SteamKit2CloudSaveStore Instance { get; private set; }

    private readonly SteamConnection _connection;
    private readonly CloudFileCache _cache;
    private readonly CloudWriteQueue _writeQueue;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private volatile bool _collectingBatch;
    private readonly List<(string path, byte[] bytes)> _batchPendingFiles = new();
    private readonly object _batchLock = new();

    public SteamKit2CloudSaveStore(string accountName, string refreshToken)
    {
        _connection = new SteamConnection(accountName, refreshToken);
        _cache = new CloudFileCache(_connection);
        _writeQueue = new CloudWriteQueue();

        Instance = this;
    }

    // P0-1: propagates whether the write queue actually drained (true) or hit
    // the timeout with work still pending/in-flight (false). _connection.Flush
    // (the on-demand disconnect) always still runs regardless — that's just a
    // courtesy teardown, orthogonal to whether the writes themselves finished.
    public bool Flush(int timeoutMs = 5000)
    {
        bool drained = _writeQueue.Flush(timeoutMs);
        _connection.Flush();
        return drained;
    }

    public void Dispose()
    {
        _writeQueue.Dispose();
        _connection.Dispose();
        _http.Dispose();
        if (Instance == this)
            Instance = null;
    }

    public string ReadFile(string path)
    {
        return ReadFileAsync(path).GetAwaiter().GetResult();
    }

    public async Task<string> ReadFileAsync(string path)
    {
        path = CloudFileCache.CanonicalizePath(path);

        if (!_cache.FileExists(path))
            throw new FileNotFoundException($"Cloud file not found: {path}");

        if (_cache.GetFileSize(path) == 0)
            return string.Empty;

        var result = await _connection
            .SendCloud<CCloud_ClientFileDownload_Request, CCloud_ClientFileDownload_Response>(
                "ClientFileDownload",
                new CCloud_ClientFileDownload_Request { appid = AppId, filename = path }
            )
            .ConfigureAwait(false);

        if (result.appid != AppId || string.IsNullOrEmpty(result.url_host))
            throw new InvalidOperationException($"Cloud download failed for {path}");

        var scheme = result.use_https ? "https" : "http";
        var url = $"{scheme}://{result.url_host}{result.url_path}";

        var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var header in result.request_headers)
            httpRequest.Headers.TryAddWithoutValidation(header.name, header.value);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var httpResponse = await _http.SendAsync(httpRequest, cts.Token).ConfigureAwait(false);
        httpResponse.EnsureSuccessStatusCode();
        var data = await httpResponse.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        PatchHelper.Log(
            $"[Cloud] Downloaded {path} ({data.Length} bytes, encrypted={result.encrypted}, "
                + $"file_size={result.file_size}, raw_file_size={result.raw_file_size})"
        );

        // Only decompress if ZIP magic header present (PK\x03\x04).
        if (
            result.raw_file_size > 0
            && result.raw_file_size != result.file_size
            && data.Length >= 4
            && data[0] == 0x50
            && data[1] == 0x4B
            && data[2] == 0x03
            && data[3] == 0x04
        )
        {
            var compressedSize = data.Length;
            data = CloudCompression.Decompress(data);
            PatchHelper.Log($"[Cloud] Unzipped {path} ({compressedSize} → {data.Length} bytes)");
        }

        return Encoding.UTF8.GetString(data);
    }

    public void WriteFile(string path, string content)
    {
        WriteFile(path, Encoding.UTF8.GetBytes(content));
    }

    public void WriteFile(string path, byte[] bytes)
    {
        var canonPath = CloudFileCache.CanonicalizePath(path);

        // Issue #36 Part B — prevention guard at the single cloud write funnel.
        // MUST run BEFORE _cache.Set: the guard compares the new length against the
        // cloud's CURRENT cached size, and _cache.Set below overwrites that size
        // with the new (possibly empty) length. Blocking here early-returns before
        // both the cache update and the upload enqueue, so a destructive empty
        // write touches neither the cache nor Steam — the good cloud copy survives.
        if (CloudWriteGuard.ShouldBlockWrite(_cache, canonPath, bytes.Length, out var blockReason))
        {
            CloudWriteGuard.NotifyBlocked(canonPath, blockReason);
            return;
        }

        // Issue #36 Part C — content-integrity guard, same funnel, same
        // fail-safe shape as Part B above: a truncated/corrupt-but-non-empty
        // save (Part B only catches empty) must never reach the cloud either.
        // Also runs on the raw pre-compression bytes, before _cache.Set/enqueue.
        if (CloudWriteGuard.ShouldBlockCorruptWrite(canonPath, bytes, out var corruptReason))
        {
            CloudWriteGuard.NotifyBlocked(canonPath, corruptReason);
            return;
        }

        var truncatedNow = DateTimeOffset.FromUnixTimeSeconds(
            DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        );
        _cache.Set(canonPath, bytes.Length, truncatedNow);

        lock (_batchLock)
        {
            if (_collectingBatch)
            {
                _batchPendingFiles.Add((path, bytes));
                Issue7Diagnostics.LogWriteEnqueue(path, bytes.Length, _batchPendingFiles.Count);
                return;
            }
        }

        var ts = truncatedNow;
        _writeQueue.Enqueue(() => UploadWithRetry(path, bytes, timestamp: ts));
        Issue7Diagnostics.LogWriteEnqueue(path, bytes.Length, _writeQueue.Count);
    }

    public Task WriteFileAsync(string path, string content)
    {
        WriteFile(path, content);
        return Task.CompletedTask;
    }

    public Task WriteFileAsync(string path, byte[] bytes)
    {
        WriteFile(path, bytes);
        return Task.CompletedTask;
    }

    public bool FileExists(string path) => _cache.FileExists(path);

    public bool DirectoryExists(string path) => true;

    public void DeleteFile(string path)
    {
        var canonPath = CloudFileCache.CanonicalizePath(path);
        _cache.Remove(canonPath);

        _writeQueue.Enqueue(() =>
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    _connection
                        .SendCloud<
                            CCloud_ClientDeleteFile_Request,
                            CCloud_ClientDeleteFile_Response
                        >(
                            "ClientDeleteFile",
                            new CCloud_ClientDeleteFile_Request
                            {
                                appid = AppId,
                                filename = canonPath,
                            }
                        )
                        .GetAwaiter()
                        .GetResult();
                    break;
                }
                catch (InvalidOperationException ex)
                    when (ex.Message.Contains("TooManyPending") && attempt < 2)
                {
                    PatchHelper.Log($"[Cloud] Delete throttled for {canonPath}, retrying...");
                    Thread.Sleep(1000);
                }
                catch (Exception ex)
                {
                    PatchHelper.Log($"[Cloud] Delete failed for {canonPath}: {ex.Message}");
                    break;
                }
            }
        });
    }

    public void RenameFile(string sourcePath, string destinationPath)
    {
        var content = ReadFile(sourcePath);
        WriteFile(destinationPath, content);
        try
        {
            DeleteFile(sourcePath);
        }
        catch (Exception ex)
        {
            PatchHelper.Log(
                $"[Cloud] RenameFile: delete of {CloudFileCache.CanonicalizePath(sourcePath)} "
                    + $"failed (duplicate may exist): {ex.Message}"
            );
        }
    }

    public string[] GetFilesInDirectory(string directoryPath) =>
        _cache.GetFilesInDirectory(directoryPath);

    public string[] GetDirectoriesInDirectory(string directoryPath) =>
        _cache.GetDirectoriesInDirectory(directoryPath);

    public void CreateDirectory(string directoryPath) { }

    public void DeleteDirectory(string directoryPath) { }

    public void DeleteTemporaryFiles(string directoryPath) { }

    public DateTimeOffset GetLastModifiedTime(string path) => _cache.GetLastModifiedTime(path);

    public int GetFileSize(string path) => _cache.GetFileSize(path);

    // issue #81 계측: enumerate 의 file_sha(다운로드 없는 동일성 판정용). 진단 전용.
    public string GetFileSha(string path) => _cache.GetSha(path);

    public void SetLastModifiedTime(string path, DateTimeOffset time) =>
        throw new NotImplementedException();

    public string GetFullPath(string filename) => throw new NotImplementedException();

    public bool HasCloudFiles() => _cache.HasCloudFiles();

    // 런처는 LauncherPatches.CloudSyncEnabled==false 면 ConstructDefaultPrefix 에서
    // SaveManager 를 local-only 로 구성한다. 여기 호출이 들어왔다는 것은 cloud
    // SaveManager 가 이미 끼워졌다는 뜻이므로 항상 true. false 를 돌리면
    // SaveManager.ShouldOverwriteCloudWithLocal 가 강제 true 가 되어 로컬을
    // 클라우드로 덮어쓴다 — issue #4 류 데이터 손실 위험.
    public bool HasUserEnabledCloudSync() => true;

    // The launcher inspects this before deciding whether the cloud-wrapped
    // SaveManager is safe to construct. False means FileExists results are
    // not authoritative and any push decision based on them is unsound.
    public bool IsCacheLoaded => _cache.IsLoaded;

    public Task<bool> WaitForCacheReadyAsync(int timeoutMs = 15_000) =>
        _cache.WaitForLoadAsync(timeoutMs);

    public void ForgetFile(string path) => _cache.ForgetFile(path);

    public bool IsFilePersisted(string path) => _cache.IsFilePersisted(path);

    // P1-1 (A1, device-verified) — re-enumerates cloud files, rolling back to
    // the previous snapshot if the fresh enumerate fails (see CloudFileCache.
    // SafeRefresh). Called at the start of CloudSyncDecisions.DetermineAsync/
    // DeterminePerProfileAsync so a sync decision never compares against a
    // stale boot-time snapshot when the cloud side changed mid-session (e.g.
    // a KeepCloud pull earlier this session, or a PC-side upload since boot)
    // — confirmed on device: a KeepCloud pull landed (raw=228298) but every
    // later decision kept comparing against the 217901-byte boot snapshot,
    // re-reporting the same false Conflict indefinitely.
    public void RefreshCache() => _cache.SafeRefresh();

    // Set once the previous batch's write-queue lambda has fully run (i.e. only
    // meaningful to read AFTER Flush() has confirmed the queue drained — see
    // CloudSyncCoordinator.ManualPushAllAsync). Reflects whether every file in
    // the batch actually made it, independent of whether the CompleteAppUploadBatch
    // RPC itself succeeded (that failure is handled separately via PendingUploadBatch).
    public bool LastBatchHadFailures { get; private set; }

    // Issue #64 — see CloudWriteQueue.PendingCount. Meaningful for the
    // non-batch per-file write path (each file is its own queue action);
    // during a batch upload the whole batch is ONE queue action, so batch
    // progress is reported via EndSaveBatch's IProgress instead.
    public int PendingWriteCount => _writeQueue.PendingCount;

    public void BeginSaveBatch()
    {
        lock (_batchLock)
        {
            _collectingBatch = true;
            _batchPendingFiles.Clear();
        }
        LastBatchHadFailures = false;
    }

    // P0-1 (Valve-confirmed: see _workspace/05_steamkit2_research.md Q1) —
    // failing to call CompleteAppUploadBatch blocks ALL new uploads for this
    // user+app for several minutes ("too many pending requests"), which is the
    // confirmed cause of repeated PC desync failures. Once BeginAppUploadBatch
    // hands us a batch_id, EVERY exit from this point — normal completion, a
    // per-file upload failure, or an unexpected exception in the loop itself —
    // MUST still call CompleteAppUploadBatchBlocking. The try/finally below is
    // what makes that unconditional.
    // ICloudSaveStore's member — the game interface pins the parameterless
    // shape, so the progress-reporting variant below is a separate overload.
    public void EndSaveBatch() => EndSaveBatch(null);

    // progress (issue #64): per-file upload progress, reported from the
    // CloudSaveWriter background thread — UI handlers must marshal to the
    // main thread themselves (see ProfileCopyFlow.DeferredProgress).
    public void EndSaveBatch(IProgress<(int done, int total)> progress)
    {
        List<(string path, byte[] bytes)> files;
        lock (_batchLock)
        {
            _collectingBatch = false;

            if (_batchPendingFiles.Count == 0)
                return;

            files = new List<(string path, byte[] bytes)>(_batchPendingFiles);
            _batchPendingFiles.Clear();
        }

        _writeQueue.Enqueue(() =>
        {
            ulong batchId = 0;
            try
            {
                var request = new CCloud_BeginAppUploadBatch_Request
                {
                    appid = AppId,
                    machine_name = "android",
                };
                foreach (var (path, _) in files)
                    request.files_to_upload.Add(CloudFileCache.CanonicalizePath(path));

                var result = _connection
                    .SendCloud<
                        CCloud_BeginAppUploadBatch_Request,
                        CCloud_BeginAppUploadBatch_Response
                    >("BeginAppUploadBatch", request)
                    .GetAwaiter()
                    .GetResult();
                batchId = result.batch_id;
            }
            catch (Exception ex)
            {
                // Begin itself failed — no batch was ever opened server-side,
                // so there's nothing for Complete to close. Fall back to
                // individual (non-batched) uploads; each still goes through
                // the same retry/commit path as a normal write.
                PatchHelper.Log($"[Cloud] BeginSaveBatch failed: {ex.Message}");
                bool allOk = true;
                int fallbackDone = 0;
                foreach (var (path, bytes) in files)
                {
                    allOk &= UploadWithRetry(path, bytes);
                    fallbackDone++;
                    progress?.Report((fallbackDone, files.Count));
                }
                LastBatchHadFailures = !allOk;
                return;
            }

            // Persist the batch_id the instant Begin succeeds, BEFORE any
            // upload starts. If the process dies partway through the uploads
            // below (kill, crash, ANR), Steam is left holding this batch open;
            // the marker lets the NEXT session's first cloud connection find
            // and close it (see CloudFileCache.LoadFileList).
            PendingUploadBatch.Mark(batchId);

            bool anyUploadFailed = false;
            try
            {
                progress?.Report((0, files.Count));
                int done = 0;
                foreach (var (path, bytes) in files)
                {
                    if (!UploadWithRetry(path, bytes, batchId))
                        anyUploadFailed = true;
                    done++;
                    progress?.Report((done, files.Count));
                }
            }
            catch (Exception ex)
            {
                // UploadWithRetry already swallows its own per-file exceptions
                // — this only guards the loop/enumeration itself. Either way
                // the batch below must still be closed, so this is deliberately
                // just a flag flip, not an early return.
                PatchHelper.Log($"[Cloud] EndSaveBatch upload loop threw: {ex.Message}");
                anyUploadFailed = true;
            }
            finally
            {
                // Unconditional close. batch_eresult tells Steam whether this
                // batch's uploads all succeeded (1/OK) or not (2/Fail) — per
                // the research doc's protocol reading, Steam only needs "all
                // operations attempted" to unblock new batches, so this fires
                // regardless of anyUploadFailed.
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
                                batch_id = batchId,
                                batch_eresult = (uint)(
                                    anyUploadFailed ? SteamKit2.EResult.Fail : SteamKit2.EResult.OK
                                ),
                            }
                        )
                        .GetAwaiter()
                        .GetResult();
                    PendingUploadBatch.Clear();
                }
                catch (Exception ex)
                {
                    // Complete itself never landed — leave the marker in place.
                    // The batch may still be open server-side; next session's
                    // stale-batch cleanup will best-effort retry (fail-open).
                    PatchHelper.Log($"[Cloud] EndSaveBatch Complete failed: {ex.Message}");
                }
                LastBatchHadFailures = anyUploadFailed;
            }
        });
    }

    // Returns true once the file has actually landed (commit confirmed by
    // UploadFileAsync), false if every attempt failed — EndSaveBatch uses this
    // to decide the batch's final batch_eresult (P0-1). P1-4 (F2): also
    // retries transient cancellation (same idle-timeout/EnsureConnected race
    // the read side already retries — see CloudSyncDecisions.
    // ReadCloudFileWithRetryAsync) using the same attempt/backoff frame as
    // the existing TooManyPending retry below. Writes had no equivalent
    // retry before — a write that lost this race was silently dropped as a
    // hard failure with no second attempt.
    private bool UploadWithRetry(
        string path,
        byte[] bytes,
        ulong batchId = 0,
        DateTimeOffset? timestamp = null
    )
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                UploadFileAsync(path, bytes, batchId, timestamp).GetAwaiter().GetResult();
                return true;
            }
            catch (InvalidOperationException ex)
                when (ex.Message.Contains("TooManyPending") && attempt < 2)
            {
                PatchHelper.Log(
                    $"[Cloud] Upload throttled for {CloudFileCache.CanonicalizePath(path)}, "
                        + $"retrying in {(attempt + 1) * 2}s..."
                );
                Thread.Sleep((attempt + 1) * 2000);
            }
            catch (Exception ex) when (attempt < 2 && IsTransientCancellation(ex))
            {
                PatchHelper.Log(
                    $"[Cloud] Upload for {CloudFileCache.CanonicalizePath(path)} hit transient "
                        + $"cancellation (attempt {attempt + 1}/3): {ex.Message} — retrying..."
                );
                Thread.Sleep((attempt + 1) * 500);
            }
            catch (Exception ex)
            {
                PatchHelper.Log(
                    $"[Cloud] Upload failed for {CloudFileCache.CanonicalizePath(path)}: {ex.Message}"
                );
                // P1-4 (F3) — WriteFile already optimistically set this path's
                // cache entry (size/mtime) BEFORE this upload ever ran. Since
                // the upload never actually landed, that cached size is a
                // guess, not a confirmed fact — mark it unpersisted so a
                // future consumer of IsFilePersisted knows not to trust it.
                _cache.ForgetFile(path);
                return false;
            }
        }
        // Unreachable in practice — every path above either returns or, on the
        // final attempt, falls to a catch(Exception) block above (both
        // guarded branches require attempt < 2, so attempt==2 always falls to
        // the final unconditional catch). Kept for the compiler's
        // definite-return rule.
        _cache.ForgetFile(path);
        return false;
    }

    // The idle-timeout race manifests as an OperationCanceledException (or
    // the TaskCanceledException subclass) bubbling out of SteamKit2's unified
    // message job when the connection drops mid-request. Mirrors
    // CloudSyncDecisions.IsTransientCancellation (read side) — duplicated
    // rather than shared since it's a 3-line predicate and the two call sites
    // are otherwise unrelated. Also checks InnerException: UploadFileAsync
    // wraps a transient commit-RPC failure in a generic InvalidOperationException
    // (see its own comment) so the outer "Cloud upload failed" message stays
    // consistent for logging, but the original transient exception is kept as
    // InnerException specifically so this check can still see it.
    private static bool IsTransientCancellation(Exception ex) =>
        ex is OperationCanceledException
        || ex.Message.Contains("was canceled", StringComparison.OrdinalIgnoreCase)
        || (ex.InnerException != null && IsTransientCancellation(ex.InnerException));

    private async Task UploadFileAsync(
        string path,
        byte[] bytes,
        ulong batchId,
        DateTimeOffset? timestamp = null
    )
    {
        path = CloudFileCache.CanonicalizePath(path);

        var fileHash = SHA1.HashData(bytes);
        var rawSize = (uint)bytes.Length;
        var (uploadBytes, compressed) = CloudCompression.Compress(bytes);

        if (compressed)
            PatchHelper.Log($"[Cloud] Compressed {path} ({rawSize} → {uploadBytes.Length} bytes)");
        else
            PatchHelper.Log($"[Cloud] Uploading {path} uncompressed ({rawSize} bytes)");

        var uploadTimestamp = timestamp.HasValue
            ? (ulong)timestamp.Value.ToUnixTimeSeconds()
            : (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var beginRequest = new CCloud_ClientBeginFileUpload_Request
        {
            appid = AppId,
            filename = path,
            file_size = (uint)uploadBytes.Length,
            raw_file_size = rawSize,
            file_sha = fileHash,
            time_stamp = uploadTimestamp,
            can_encrypt = false,
            is_shared_file = false,
            // Bitfield telling Steam Cloud which platforms should sync this
            // file. Leaving it 0 (the default) makes Steam treat the upload
            // as belonging to no platform — PC clients then refuse to pull it
            // and surface a sync conflict instead. 0xFFFFFFFF marks the file
            // for every platform, matching cross-platform save behavior.
            platforms_to_sync = uint.MaxValue,
        };

        if (batchId != 0)
            beginRequest.upload_batch_id = batchId;

        CCloud_ClientBeginFileUpload_Response beginResult;
        try
        {
            beginResult = await _connection
                .SendCloud<
                    CCloud_ClientBeginFileUpload_Request,
                    CCloud_ClientBeginFileUpload_Response
                >("ClientBeginFileUpload", beginRequest)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("DuplicateRequest"))
        {
            PatchHelper.Log($"[Cloud] Skipped upload for {path} (already up to date)");
            return;
        }

        bool uploadSucceeded = false;
        // P1-4 (F3) — set only when the commit RPC itself throws, so we can
        // preserve it as the InnerException of the failure thrown below
        // (UploadWithRetry's transient-cancellation check needs to see the
        // real exception, not just the generic message it's wrapped in).
        Exception commitFailure = null;
        try
        {
            foreach (var block in beginResult.block_requests)
            {
                var scheme = block.use_https ? "https" : "http";
                var url = $"{scheme}://{block.url_host}{block.url_path}";

                var method = block.http_method == 2 ? HttpMethod.Post : HttpMethod.Put;
                var request = new HttpRequestMessage(method, url);

                byte[] bodyData =
                    block.explicit_body_data?.Length > 0
                        ? block.explicit_body_data
                        : uploadBytes[
                            (int)block.block_offset..(
                                (int)block.block_offset + (int)block.block_length
                            )
                        ];

                request.Content = new ByteArrayContent(bodyData);
                request.Content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                request.Content.Headers.ContentLength = bodyData.Length;

                foreach (var header in block.request_headers)
                    request.Headers.TryAddWithoutValidation(header.name, header.value);

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var httpResponse = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
                httpResponse.EnsureSuccessStatusCode();
            }

            uploadSucceeded = true;
        }
        finally
        {
            try
            {
                var commitResult = await _connection
                    .SendCloud<
                        CCloud_ClientCommitFileUpload_Request,
                        CCloud_ClientCommitFileUpload_Response
                    >(
                        "ClientCommitFileUpload",
                        new CCloud_ClientCommitFileUpload_Request
                        {
                            transfer_succeeded = uploadSucceeded,
                            appid = AppId,
                            file_sha = fileHash,
                            filename = path,
                        }
                    )
                    .ConfigureAwait(false);

                if (uploadSucceeded && !commitResult.file_committed)
                {
                    // P1-4 (F3) — this used to be logged and silently treated
                    // as success: the HTTP blocks landed but Steam's own
                    // commit step rejected the file, so nothing usable
                    // actually exists at `path` server-side. Flip it so the
                    // caller (UploadWithRetry → LastBatchHadFailures/
                    // batch_eresult) sees a real failure instead of a lie.
                    PatchHelper.Log($"[Cloud] Commit returned file_committed=false for {path}");
                    uploadSucceeded = false;
                }
            }
            catch (Exception ex)
            {
                // P1-4 (F3) — same reasoning: if the commit RPC itself never
                // came back, we don't actually know Steam accepted the file.
                // Treating this as success (previous behavior) could leave
                // local/cloud silently out of sync while everything
                // downstream believed the write landed.
                PatchHelper.Log($"[Cloud] Commit failed for {path}: {ex.Message}");
                uploadSucceeded = false;
                commitFailure = ex;
            }
        }

        if (!uploadSucceeded)
            throw new InvalidOperationException($"Cloud upload failed for {path}", commitFailure);

        PatchHelper.Log($"[Cloud] Wrote {bytes.Length} bytes to {path} (compressed={compressed})");
    }
}
