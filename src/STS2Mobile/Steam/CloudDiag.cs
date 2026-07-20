using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using MegaCrit.Sts2.Core.Saves;

namespace STS2Mobile.Steam;

// issue #81 계측 (관찰 전용 · 동작 불변 · 실삭제/실스킵 없음).
// A(프루닝/쿼터)·B(느린 동기화)·C(충돌 오판정)를 한 번에 실측하기 위한 임시 진단.
// 베이스라인 확보 후 제거 예정. 모든 로그 접두사 [CloudDiag].
internal static class CloudDiag
{
    private static bool _enumerateDumped;

    internal static void Log(string msg) => PatchHelper.Log($"[CloudDiag] {msg}");

    // enumerate 전체 목록을 세션 1회 덤프. file_size(raw)와 compressed_file_size를
    // 분리 출력 → C의 raw/compressed 의문 확정. sha 유무 → B의 SHA-skip 가능성 확정.
    internal static void DumpEnumerate(
        List<(string name, uint fileSize, uint compressedSize, string sha, ulong ts)> files
    )
    {
        if (_enumerateDumped)
            return;
        _enumerateDumped = true;

        int runCount = 0;
        int shaPresent = 0;
        Log($"=== Enumerate dump start: {files.Count} files ===");
        foreach (var f in files)
        {
            if (f.name.EndsWith(".run", StringComparison.OrdinalIgnoreCase))
                runCount++;
            if (!string.IsNullOrEmpty(f.sha))
                shaPresent++;
            Log(
                $"FILE {f.name} size={f.fileSize} csize={f.compressedSize} sha={Short(f.sha)} ts={f.ts}"
            );
        }
        Log(
            $"=== Enumerate dump end: total={files.Count} run={runCount} sha_present={shaPresent} ==="
        );
    }

    // raw UTF-8 내용의 SHA1 hex(소문자). 업로드 시 file_sha=SHA1(raw bytes) 이므로
    // enumerate 의 file_sha 와 직접 비교 가능(다운로드 불필요).
    internal static string Sha1Hex(string content)
    {
        try
        {
            return Convert
                .ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(content)))
                .ToLowerInvariant();
        }
        catch
        {
            return "?";
        }
    }

    internal static string Short(string sha) =>
        string.IsNullOrEmpty(sha) ? "-" : (sha.Length > 12 ? sha.Substring(0, 12) : sha.ToLowerInvariant());

    // 다운로드 전 호출: 로컬이 이미 클라우드와 SHA 동일한가? (다운로드 없이 캐시 sha 사용)
    // "WOULD-SKIP" 이 많을수록 B(불필요 다운로드)의 낭비가 큼을 정량화.
    internal static void LogDownloadNeed(ISaveStore local, ICloudSaveStore cloud, string path)
    {
        try
        {
            bool isRun = path.Replace("\\", "/").EndsWith(".run", StringComparison.OrdinalIgnoreCase);
            if (!local.FileExists(path))
            {
                Log($"DL {path} download-needed(local-missing){(isRun ? " [run]" : "")}");
                return;
            }
            var cloudSha = (cloud as SteamKit2CloudSaveStore)?.GetFileSha(path);
            var localSha = Sha1Hex(local.ReadFile(path));
            bool same =
                !string.IsNullOrEmpty(cloudSha)
                && string.Equals(cloudSha, localSha, StringComparison.OrdinalIgnoreCase);
            Log(
                $"DL {path} {(same ? "WOULD-SKIP(sha-identical)" : "download-needed")}"
                    + $"{(isRun ? " [run]" : "")} local={Short(localSha)} cloud={Short(cloudSha)}"
            );
        }
        catch (Exception ex)
        {
            Log($"DL diag failed {path}: {ex.Message}");
        }
    }

    // 충돌 판정 슬롯 실측: differs 결과와 함께 양쪽 size/sha 를 남김.
    // sha 는 같은데 differs=true 면 false-conflict 확정 + 트리거가 어느 크기였는지 가시화.
    internal static void LogSlot(
        ISaveStore local,
        ICloudSaveStore cloud,
        int profile,
        bool modded,
        string progressPath,
        int localProgSize,
        int cloudProgSize,
        string runPath,
        int localRunSize,
        int cloudRunSize,
        bool differs
    )
    {
        try
        {
            var store = cloud as SteamKit2CloudSaveStore;
            string localProgSha = localProgSize > 0 ? Sha1Hex(local.ReadFile(progressPath)) : "-";
            string cloudProgSha = store?.GetFileSha(progressPath);
            string localRunSha = localRunSize > 0 ? Sha1Hex(local.ReadFile(runPath)) : "-";
            string cloudRunSha = store?.GetFileSha(runPath);
            Log(
                $"SLOT p{profile} modded={modded} differs={differs}"
                    + $" | progress local={localProgSize}/{Short(localProgSha)} cloud={cloudProgSize}/{Short(cloudProgSha)}"
                    + $" | run local={localRunSize}/{Short(localRunSha)} cloud={cloudRunSize}/{Short(cloudRunSha)}"
            );
        }
        catch (Exception ex)
        {
            Log($"SLOT diag failed p{profile} modded={modded}: {ex.Message}");
        }
    }

    // prune 후보 = 클라우드에만 존재하는 history .run (로컬 newest-100 밖).
    // 클라우드 enumerate 기준으로 walk 하므로 로컬-walk 가 놓치는 stale run 을 포착.
    // 실삭제 절대 안 함 — 개수와 목록만 로깅해 A(누적)를 정량화.
    internal static void LogPruneCandidates(ISaveStore local, ICloudSaveStore cloud)
    {
        int total = 0;
        var wasModded = UserDataPathProvider.IsRunningModded;
        try
        {
            foreach (bool modded in new[] { false, true })
            {
                UserDataPathProvider.IsRunningModded = modded;
                for (int p = 1; p <= 3; p++)
                {
                    var histDir = SavePathCompat.GetHistoryPath(p);
                    string[] cloudRuns;
                    try
                    {
                        cloudRuns = cloud.GetFilesInDirectory(histDir);
                    }
                    catch
                    {
                        continue;
                    }
                    foreach (var f in cloudRuns)
                    {
                        if (
                            !f.EndsWith(".run", StringComparison.OrdinalIgnoreCase)
                            || f.EndsWith(".backup", StringComparison.OrdinalIgnoreCase)
                            || f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                        )
                            continue;
                        var full = $"{histDir}/{f}";
                        if (!local.FileExists(full))
                        {
                            total++;
                            Log($"PRUNE-CANDIDATE {full}");
                        }
                    }
                }
            }
        }
        finally
        {
            UserDataPathProvider.IsRunningModded = wasModded;
        }
        Log($"PRUNE-CANDIDATE total={total} (클라우드에만 존재하는 history .run · 실삭제 안 함)");
    }
}
