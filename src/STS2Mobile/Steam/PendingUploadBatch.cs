using System;
using System.IO;
using Godot;

namespace STS2Mobile.Steam;

// P0-1 (issue #36 follow-up) — tiny on-disk marker for an AppUploadBatch that
// has been opened (BeginAppUploadBatch succeeded) but not yet confirmed closed
// (CompleteAppUploadBatchBlocking succeeded). Per Valve's ICloudService docs
// (see _workspace/05_steamkit2_research.md Q1), an unclosed batch blocks ALL
// new uploads for this user+app for "several minutes" with a "too many
// pending requests" response — the confirmed root cause of repeated PC
// desync failures. SteamKit2CloudSaveStore.EndSaveBatch's try/finally already
// closes the batch on every in-process exit path, but if the process itself
// dies mid-batch (kill, crash, ANR) that finally never runs. This marker is
// the only way the NEXT session can find out a batch was left open and close
// it — see CloudFileCache.LoadFileList for the cleanup side.
public static class PendingUploadBatch
{
    private static string MarkerPath => Path.Combine(OS.GetDataDir(), "pending_upload_batch");

    // Called the instant BeginAppUploadBatch returns a batch_id — before any
    // file upload starts, so even a crash on the very first file still leaves
    // a marker pointing at the right batch to close next time.
    public static void Mark(ulong batchId)
    {
        try
        {
            // tmp-then-move so a kill mid-write can't leave a truncated marker
            // that fails to parse next session (same pattern as CacheStamp).
            var tmpPath = MarkerPath + ".tmp";
            File.WriteAllText(tmpPath, batchId.ToString());
            if (File.Exists(MarkerPath))
                File.Delete(MarkerPath);
            File.Move(tmpPath, MarkerPath);
        }
        catch (Exception ex)
        {
            // Best-effort only — failing to persist the marker must never
            // block the actual upload batch from proceeding.
            PatchHelper.Log($"[Cloud] PendingUploadBatch.Mark failed: {ex.Message}");
        }
    }

    // Called once EndSaveBatch's finally block has attempted
    // CompleteAppUploadBatchBlocking for this batch — success or failure
    // eresult, either way Steam has been told the batch is done, so there's
    // nothing left to clean up next session.
    public static void Clear()
    {
        try
        {
            if (File.Exists(MarkerPath))
                File.Delete(MarkerPath);
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Cloud] PendingUploadBatch.Clear failed: {ex.Message}");
        }
    }

    // Reads a leftover marker from a prior session, if any. Does NOT delete it
    // — the caller only knows the cleanup RPC actually reached Steam once
    // that call returns (or fails), so it clears the marker itself afterward
    // regardless of outcome (fail-open — see CloudFileCache.LoadFileList).
    public static bool TryRead(out ulong batchId)
    {
        batchId = 0;
        try
        {
            if (!File.Exists(MarkerPath))
                return false;
            var text = File.ReadAllText(MarkerPath).Trim();
            return ulong.TryParse(text, out batchId);
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Cloud] PendingUploadBatch.TryRead failed: {ex.Message}");
            return false;
        }
    }
}
