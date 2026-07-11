using System;
using System.Collections.Generic;
using System.Text;
using MegaCrit.Sts2.Core.Saves;

namespace STS2Mobile.Steam;

// issue #64 — profile slot clone ("프로필 복제"). Overwrites one (profile ×
// modded) slot's save files with another's, entirely local (no cloud I/O —
// the caller decides separately whether to push the result; see
// LauncherPatches.ApplyChosenSideForSlotAsync). Frozen contract:
// _workspace/issue64/02_design.md §2-A.
public class ProfileCopyResult
{
    public bool Success;
    public int FileCount; // files actually copied
    public long TotalBytes;
    public string BackupPath; // pre-copy backup set path (set when the backup succeeded)
    public string Error; // user-facing, set on failure
    public bool NeedsPermission; // storage permission missing → backup (and copy) never ran
    public bool CurrentRunExcluded; // D1: whether the modded→vanilla exclusion actually applied
}

public static class ProfileCopyService
{
    private const string Tag = "[Issue64]";

    // Every path this service touches for one (profile × modded) slot,
    // resolved once while UserDataPathProvider.IsRunningModded is pinned to
    // that slot's flag (see GetSlotPaths) — the flag is a process-wide
    // static, so no file I/O may run while it's set to a value the caller
    // didn't ask for (01_cloud_map.md §1-2).
    private readonly struct SlotPaths
    {
        public readonly string Progress;
        public readonly string CurrentRun;
        public readonly string CurrentRunMp;
        public readonly string Prefs;
        public readonly string HistoryDir;
        public readonly List<string> HistoryFiles;

        public SlotPaths(
            string progress,
            string currentRun,
            string currentRunMp,
            string prefs,
            string historyDir,
            List<string> historyFiles
        )
        {
            Progress = progress;
            CurrentRun = currentRun;
            CurrentRunMp = currentRunMp;
            Prefs = prefs;
            HistoryDir = historyDir;
            HistoryFiles = historyFiles;
        }
    }

    // Synchronous — caller wraps in Task.Run (matches LocalBackupService.
    // BackupNow's convention). Overwrites dstProfile/dstModded with
    // srcProfile/srcModded's save files:
    //   (1) reject src == dst
    //   (2) LocalBackupService.BackupNow() — abort on failure; no backup
    //       means no destructive step below ever runs
    //   (3) delete every file the destination slot currently holds (5
    //       canonical files + their .backup siblings + every history/*.run
    //       (+.backup) it has)
    //   (4) copy the source's files onto the (now-empty) destination —
    //       current_run(.save/_mp.save) are skipped when modded source →
    //       vanilla destination (D1); everything else copies unconditionally
    //       when present on the source
    public static ProfileCopyResult CopyProfile(
        ISaveStore local,
        int srcProfile,
        bool srcModded,
        int dstProfile,
        bool dstModded
    )
    {
        try
        {
            if (srcProfile == dstProfile && srcModded == dstModded)
            {
                PatchHelper.Log(
                    $"{Tag} CopyProfile: rejected, src == dst (profile{srcProfile}"
                        + $"{(srcModded ? " modded" : "")})"
                );
                return new ProfileCopyResult
                {
                    Success = false,
                    Error = "원본과 대상 슬롯이 동일합니다.",
                };
            }

            PatchHelper.Log(
                $"{Tag} CopyProfile: start profile{srcProfile}{(srcModded ? "(모드)" : "")} "
                    + $"-> profile{dstProfile}{(dstModded ? "(모드)" : "")}"
            );

            var backup = LocalBackupService.BackupNow();
            if (!backup.Success)
            {
                PatchHelper.Log(
                    $"{Tag} CopyProfile: pre-copy backup failed, aborting: {backup.Error}"
                );
                return new ProfileCopyResult
                {
                    Success = false,
                    Error = backup.Error,
                    NeedsPermission = backup.NeedsPermission,
                };
            }

            var src = GetSlotPaths(local, srcProfile, srcModded);
            var dst = GetSlotPaths(local, dstProfile, dstModded);

            bool excludeCurrentRun = srcModded && !dstModded;

            DeleteSlot(local, dst);

            int fileCount = 0;
            long totalBytes = 0;
            var failedPaths = new List<string>();

            CopyFileIfExists(
                local,
                src.Progress,
                dst.Progress,
                ref fileCount,
                ref totalBytes,
                failedPaths
            );

            if (!excludeCurrentRun)
            {
                CopyFileIfExists(
                    local,
                    src.CurrentRun,
                    dst.CurrentRun,
                    ref fileCount,
                    ref totalBytes,
                    failedPaths
                );
                CopyFileIfExists(
                    local,
                    src.CurrentRunMp,
                    dst.CurrentRunMp,
                    ref fileCount,
                    ref totalBytes,
                    failedPaths
                );
            }
            else
            {
                PatchHelper.Log(
                    $"{Tag} CopyProfile: excluding current_run(.save/_mp.save) — modded source "
                        + "-> vanilla destination (D1)"
                );
            }

            CopyFileIfExists(
                local,
                src.Prefs,
                dst.Prefs,
                ref fileCount,
                ref totalBytes,
                failedPaths
            );

            foreach (var srcHistPath in src.HistoryFiles)
            {
                var fileName = srcHistPath.Substring(srcHistPath.LastIndexOf('/') + 1);
                var dstHistPath = $"{dst.HistoryDir}/{fileName}";
                CopyFileIfExists(
                    local,
                    srcHistPath,
                    dstHistPath,
                    ref fileCount,
                    ref totalBytes,
                    failedPaths
                );
            }

            PatchHelper.Log(
                $"{Tag} CopyProfile: complete, {fileCount} files, {totalBytes}B, "
                    + $"excludeCurrentRun={excludeCurrentRun}, backup={backup.DestPath}, "
                    + $"failures={failedPaths.Count}"
            );

            // A partial failure must never present itself as a completed copy —
            // DeleteSlot already emptied the destination before these copies
            // ran, so a source read/write failure here means the destination
            // is missing data it's supposed to have. Whatever DID land is
            // still counted in FileCount/TotalBytes, but Success flips to
            // false and BackupPath stays populated so the caller can point
            // the user at the pre-copy backup to recover.
            if (failedPaths.Count > 0)
            {
                var failedNames = string.Join(", ", failedPaths.ConvertAll(ShortName));
                return new ProfileCopyResult
                {
                    Success = false,
                    FileCount = fileCount,
                    TotalBytes = totalBytes,
                    BackupPath = backup.DestPath,
                    CurrentRunExcluded = excludeCurrentRun,
                    Error = $"일부 파일 복사 실패: {failedNames}. 사전 백업: {backup.DestPath}",
                };
            }

            return new ProfileCopyResult
            {
                Success = true,
                FileCount = fileCount,
                TotalBytes = totalBytes,
                BackupPath = backup.DestPath,
                CurrentRunExcluded = excludeCurrentRun,
            };
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"{Tag} CopyProfile FAIL: {ex.Message}");
            return new ProfileCopyResult { Success = false, Error = ex.Message };
        }
    }

    private static SlotPaths GetSlotPaths(ISaveStore local, int profile, bool modded)
    {
        var wasModded = UserDataPathProvider.IsRunningModded;
        try
        {
            UserDataPathProvider.IsRunningModded = modded;
            var historyDir = SavePathCompat.GetHistoryPath(profile);
            return new SlotPaths(
                SavePathCompat.GetProgressPathForProfile(profile),
                SavePathCompat.GetRunSavePath(profile, "current_run.save"),
                SavePathCompat.GetRunSavePath(profile, "current_run_mp.save"),
                SavePathCompat.GetPrefsPath(profile),
                historyDir,
                CloudSyncCoordinator.GetHistoryFilePathsForProfile(local, profile)
            );
        }
        finally
        {
            UserDataPathProvider.IsRunningModded = wasModded;
        }
    }

    // Empties the destination slot before the copy lands — 5 canonical files
    // + their .backup siblings, plus every history/*.run (+.backup) it
    // currently holds. Missing a .backup sibling would leave the game able to
    // resurrect a stale current_run from it after the copy completes (same
    // reasoning as CloudSyncCoordinator.DeleteEphemeralLocalWithBackup,
    // applied here to all 5 file kinds rather than just the ephemeral run
    // pair).
    private static void DeleteSlot(ISaveStore local, SlotPaths dst)
    {
        DeleteFileWithBackup(local, dst.Progress);
        DeleteFileWithBackup(local, dst.CurrentRun);
        DeleteFileWithBackup(local, dst.CurrentRunMp);
        DeleteFileWithBackup(local, dst.Prefs);
        foreach (var histPath in dst.HistoryFiles)
            DeleteFileWithBackup(local, histPath);
    }

    private static void DeleteFileWithBackup(ISaveStore local, string path)
    {
        TryDeleteOne(local, path);
        TryDeleteOne(local, path + ".backup");
    }

    private static void TryDeleteOne(ISaveStore local, string path)
    {
        try
        {
            if (local.FileExists(path))
            {
                local.DeleteFile(path);
                PatchHelper.Log($"{Tag} CopyProfile: deleted dest {path}");
            }
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"{Tag} CopyProfile: delete failed for {path}: {ex.Message}");
        }
    }

    // Skips silently when the source doesn't exist — an absent source file
    // just means the destination stays absent too, and DeleteSlot already
    // cleared whatever was there, so nothing stale is left behind. A source
    // that DOES exist but fails to read/write is different: DeleteSlot has
    // already emptied the destination, so swallowing that failure would
    // leave an incomplete slot silently reporting success. Appends srcPath
    // to failedPaths so CopyProfile can flip the overall result to
    // Success=false.
    private static void CopyFileIfExists(
        ISaveStore local,
        string srcPath,
        string dstPath,
        ref int fileCount,
        ref long totalBytes,
        List<string> failedPaths
    )
    {
        try
        {
            if (!local.FileExists(srcPath))
                return;

            var content = local.ReadFile(srcPath);
            if (string.IsNullOrEmpty(content))
                return;

            local.WriteFile(dstPath, content);
            fileCount++;
            totalBytes += Encoding.UTF8.GetByteCount(content);
            PatchHelper.Log(
                $"{Tag} CopyProfile: copied {srcPath} -> {dstPath} ({content.Length} chars)"
            );
        }
        catch (Exception ex)
        {
            PatchHelper.Log(
                $"{Tag} CopyProfile: copy failed {srcPath} -> {dstPath}: {ex.Message}"
            );
            failedPaths.Add(srcPath);
        }
    }

    // Basename only, for the user-facing failure summary — the full
    // profile/modded-scoped path is noise the user can't act on.
    private static string ShortName(string path)
    {
        var idx = path.LastIndexOf('/');
        return idx >= 0 ? path.Substring(idx + 1) : path;
    }
}
