using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Saves;

namespace STS2Mobile.Steam;

// Cross-store progress comparison for the launcher's first-PLAY decision.
// Inspects progress.save in profile1/2/3 (vanilla and modded layouts) and
// classifies the situation into a single state — drives whether we silently
// auto-sync or ask the user (issue #4 follow-up: avoid both data loss and
// over-prompting). Only the canonical progress.save is consulted; other save
// files (settings, prefs, history runs) follow the post-decision sync path.
public enum SyncDecision
{
    // Neither side has a meaningful progress.save anywhere. New install on both
    // ends. No prompt — let normal AutoSync handle the empty case.
    NoData,

    // Local has progress, cloud is empty for every profile. Mobile is the
    // first device. Silently push without prompting.
    MobileOnly,

    // Cloud has progress, local is empty for every profile. PC user installing
    // launcher for the first time. Silently pull.
    CloudOnly,

    // Both sides have progress, but at least one profile differs. Needs a user
    // decision — ConflictDialog must be shown.
    Conflict,

    // Both sides have content and every profile matches byte-for-byte. Treat as
    // "everything is fine" — fall through to the existing AutoSync logic.
    Identical,

    // Both sides have progress.save of the SAME size, but the cloud byte-read
    // needed to confirm a match kept failing (even after retry) — typically a
    // Steam connection idle-timeout racing with EnsureConnected (see
    // SteamConnection.cs). We genuinely don't know whether this profile
    // matches or differs. MUST NOT be treated as Identical (would silently
    // skip a real conflict) or Conflict (would let the user pick a side based
    // on data we never actually compared) — no destructive choice is offered
    // for this state; the caller just informs the user and lets them retry.
    Unverified,
}

public class SyncDecisionResult
{
    public SyncDecision Decision { get; init; }

    // For Conflict only: the per-profile summaries are aggregated for the
    // dialog. Local takes the union of every non-empty profile; cloud likewise.
    public SaveProgressSummary LocalSummary { get; init; }
    public SaveProgressSummary CloudSummary { get; init; }

    // Total number of (profile × modded) slots that differ between local and
    // cloud. The dialog renders only ONE profile's stats but ApplyChosenSide
    // pushes/pulls every diff. This counter lets the dialog flag the rest:
    // "프로필 3 (외 K개 차이)". 0 when there's no conflict at all.
    public int DiffSlotCount { get; init; }

    // Set only by DeterminePerProfileAsync — identifies which single (profile ×
    // modded) slot this result represents, so the Save Manager profile list can
    // label/scope-apply per slot without digging into LocalSummary/CloudSummary
    // (which may themselves be empty placeholders on a MobileOnly/CloudOnly slot).
    // Left at the default (0/false) for the aggregate DetermineAsync result, which
    // doesn't represent a single slot.
    public int ProfileNumber { get; init; }
    public bool IsModded { get; init; }

    // Drives which side gets visual emphasis ("most recent" highlight). When one
    // side is empty, the side with data is the de-facto "most recent" — there's
    // nothing to compare timestamps against.
    //
    // Issue #7 fix: compare the *latest* timestamp on each side across BOTH
    // progress.save and current_run.save. Pre-fix only progress.save mtime was
    // considered, which mispointed the badge whenever the in-progress run was
    // newer than the accumulator file (verified: strict-swipe scenario where
    // local current_run.save was 21:49 but its progress.save was a stale
    // 21:14 from a prior KeepCloud, while cloud progress.save was a fresh
    // 21:55 from a manual KeepLocal — old code said cloud, true newer = local).
    public bool LocalIsMoreRecent
    {
        get
        {
            bool localHas = LocalSummary != null && !LocalSummary.IsEmpty;
            bool cloudHas = CloudSummary != null && !CloudSummary.IsEmpty;
            if (localHas && !cloudHas)
                return true;
            if (!localHas && cloudHas)
                return false;
            if (localHas && cloudHas)
                return Latest(LocalSummary) > Latest(CloudSummary);
            return false;
        }
    }

    private static DateTimeOffset Latest(SaveProgressSummary s)
    {
        var p = s.LastModified;
        var r = s.HasCurrentRun ? s.CurrentRunLastModified : DateTimeOffset.MinValue;
        return p > r ? p : r;
    }
}

public static class CloudSyncDecisions
{
    private const int MaxProfiles = 3;

    public static async Task<SyncDecisionResult> DetermineAsync(
        ISaveStore local,
        ICloudSaveStore cloud
    )
    {
        // P1-1 (A1) — re-enumerate before deciding. See RefreshCloudCacheIfPossible.
        RefreshCloudCacheIfPossible(cloud, nameof(DetermineAsync));

        await Issue7Diagnostics.AuditDecisionStateAsync(local, cloud, "DetermineAsync");

        bool anyLocal = false;
        bool anyCloud = false;
        bool anyDiff = false;
        bool anyUnverified = false;

        SaveProgressSummary aggregateLocal = null;
        SaveProgressSummary aggregateCloud = null;

        // Walk both vanilla and modded layouts so a player who plays modded on
        // PC and vanilla on mobile (or vice versa) gets the same conflict
        // handling. UserDataPathProvider.IsRunningModded is process-wide state;
        // restore it after we're done so we don't leak the toggle into the
        // game's own startup logic.
        // Track which profile triggered anyDiff so we can pick the most
        // user-relevant summary for the dialog (issue #7: previously we always
        // showed profile1's stats even when profile2/3 was the one that
        // differed, leaving the user with no idea what triggered the conflict).
        SaveProgressSummary firstDiffLocal = null;
        SaveProgressSummary firstDiffCloud = null;
        SaveProgressSummary firstRunLocal = null;
        SaveProgressSummary firstRunCloud = null;
        SaveProgressSummary firstUnverifiedLocal = null;
        SaveProgressSummary firstUnverifiedCloud = null;
        int diffSlots = 0;

        var wasModded = UserDataPathProvider.IsRunningModded;
        try
        {
            foreach (bool modded in new[] { false, true })
            {
                UserDataPathProvider.IsRunningModded = modded;
                for (int profile = 1; profile <= MaxProfiles; profile++)
                {
                    var slot = await EvaluateSlotAsync(local, cloud, profile, modded)
                        .ConfigureAwait(false);

                    if (slot.HasLocal)
                        anyLocal = true;
                    if (slot.HasCloud)
                        anyCloud = true;
                    if (slot.Differs)
                        anyDiff = true;
                    if (slot.ReadUnverified)
                        anyUnverified = true;

                    // Aggregate summaries: build a per-profile summary so we
                    // can pick the most-relevant one to render. Priority:
                    //   1. profile that triggered anyDiff (matches what
                    //      Conflict actually means)
                    //   2. profile with an in-progress run
                    //   3. first non-empty progress.save (legacy fallback)
                    if (slot.Differs)
                    {
                        diffSlots++;
                        firstDiffLocal ??=
                            slot.LocalSummary
                            ?? new SaveProgressSummary
                            {
                                ProfileNumber = profile,
                                IsModded = modded,
                            };
                        firstDiffCloud ??=
                            slot.CloudSummary
                            ?? new SaveProgressSummary
                            {
                                ProfileNumber = profile,
                                IsModded = modded,
                            };
                    }
                    if (slot.ReadUnverified)
                    {
                        firstUnverifiedLocal ??=
                            slot.LocalSummary
                            ?? new SaveProgressSummary
                            {
                                ProfileNumber = profile,
                                IsModded = modded,
                            };
                        firstUnverifiedCloud ??=
                            slot.CloudSummary
                            ?? new SaveProgressSummary
                            {
                                ProfileNumber = profile,
                                IsModded = modded,
                            };
                    }
                    if (slot.LocalSummary?.HasCurrentRun == true)
                        firstRunLocal ??= slot.LocalSummary;
                    if (slot.CloudSummary?.HasCurrentRun == true)
                        firstRunCloud ??= slot.CloudSummary;

                    if (aggregateLocal == null && slot.LocalSummary != null)
                        aggregateLocal = slot.LocalSummary;
                    if (aggregateCloud == null && slot.CloudSummary != null)
                        aggregateCloud = slot.CloudSummary;
                }
            }
        }
        finally
        {
            UserDataPathProvider.IsRunningModded = wasModded;
        }

        // Resolution priority: show the user the data that actually changed.
        // 1) profile that triggered anyDiff — the user can see *which* save is
        //    the source of conflict, not generic profile1 stats.
        // 2) profile with an in-progress run — even when sides match, surface
        //    the run state so "Identical" doesn't visually contradict the
        //    obvious presence of a current run.
        // 3) first non-empty (legacy) — for boring NoData / Identical cases
        //    where there's no in-progress run on either side.
        var resolvedLocal = firstDiffLocal ?? firstRunLocal ?? aggregateLocal;
        var resolvedCloud = firstDiffCloud ?? firstRunCloud ?? aggregateCloud;

        if (Issue7Diagnostics.Enabled)
            PatchHelper.Log(
                $"[Diag-i7] DetermineAsync result: anyLocal={anyLocal} anyCloud={anyCloud} anyDiff={anyDiff} "
                    + $"anyUnverified={anyUnverified} "
                    + $"summarySource={(firstDiffLocal != null ? "diff" : firstRunLocal != null ? "run" : "first-nonempty")}"
            );

        if (!anyLocal && !anyCloud)
            return new SyncDecisionResult { Decision = SyncDecision.NoData };
        if (anyLocal && !anyCloud)
            return new SyncDecisionResult
            {
                Decision = SyncDecision.MobileOnly,
                LocalSummary = resolvedLocal,
                CloudSummary = new SaveProgressSummary(),
            };
        if (!anyLocal && anyCloud)
            return new SyncDecisionResult
            {
                Decision = SyncDecision.CloudOnly,
                LocalSummary = new SaveProgressSummary(),
                CloudSummary = resolvedCloud,
            };
        if (anyDiff)
            return new SyncDecisionResult
            {
                Decision = SyncDecision.Conflict,
                LocalSummary = resolvedLocal,
                CloudSummary = resolvedCloud,
                DiffSlotCount = diffSlots,
            };

        // No confirmed diff anywhere, but at least one profile's byte-compare
        // never actually completed (transient cloud read failure). Returning
        // Identical here would be a lie — we never verified this profile — and
        // Identical falls straight through to auto-apply in ResolveSyncDecisionAsync.
        // Surface Unverified instead so the caller skips auto-apply this round
        // and simply retries on the next launch/visit.
        if (anyUnverified)
            return new SyncDecisionResult
            {
                Decision = SyncDecision.Unverified,
                LocalSummary = firstUnverifiedLocal,
                CloudSummary = firstUnverifiedCloud,
            };

        // Identical: still attach summaries so the Save Manager button can
        // display the (matching) state with real numbers instead of placeholders.
        return new SyncDecisionResult
        {
            Decision = SyncDecision.Identical,
            LocalSummary = aggregateLocal,
            CloudSummary = aggregateCloud,
        };
    }

    // Save Manager profile list: unlike DetermineAsync (which collapses every
    // profile × modded slot into ONE aggregate summary picked by priority),
    // this returns one SyncDecisionResult PER non-empty slot so the user can
    // inspect and resolve each profile independently. This is what fixes the
    // "ghost slot hijacking" bug — a 1.5KB unmodded profile2 stub no longer
    // hides a 228KB modded profile1 conflict; both show up as separate rows.
    // Ordered profile-major (profile1 unmodded/modded, then profile2, ...) to
    // match how the user thinks about "프로필 N" — DetermineAsync keeps its own
    // modded-major loop order since that's unobserved (aggregate only).
    public static async Task<List<SyncDecisionResult>> DeterminePerProfileAsync(
        ISaveStore local,
        ICloudSaveStore cloud
    )
    {
        // P1-1 (A1) — re-enumerate before deciding. See RefreshCloudCacheIfPossible.
        // This also covers the Save Manager "resolve one slot, re-show the
        // list" loop (OpenSaveSyncDialogAsync) automatically — every re-entry
        // calls DeterminePerProfileAsync again, so the just-applied change is
        // always visible on the next pass without any extra plumbing there.
        RefreshCloudCacheIfPossible(cloud, nameof(DeterminePerProfileAsync));

        await Issue7Diagnostics.AuditDecisionStateAsync(local, cloud, "DeterminePerProfileAsync");

        var results = new List<SyncDecisionResult>();
        var wasModded = UserDataPathProvider.IsRunningModded;
        try
        {
            for (int profile = 1; profile <= MaxProfiles; profile++)
            {
                foreach (bool modded in new[] { false, true })
                {
                    UserDataPathProvider.IsRunningModded = modded;
                    var slot = await EvaluateSlotAsync(local, cloud, profile, modded)
                        .ConfigureAwait(false);

                    if (!slot.HasLocal && !slot.HasCloud)
                        continue; // Truly empty slot — nothing to show a button for.

                    var localSummary =
                        slot.LocalSummary
                        ?? new SaveProgressSummary { ProfileNumber = profile, IsModded = modded };
                    var cloudSummary =
                        slot.CloudSummary
                        ?? new SaveProgressSummary { ProfileNumber = profile, IsModded = modded };

                    SyncDecision decision;
                    if (slot.HasLocal && !slot.HasCloud)
                        decision = SyncDecision.MobileOnly;
                    else if (!slot.HasLocal && slot.HasCloud)
                        decision = SyncDecision.CloudOnly;
                    else if (slot.ReadUnverified)
                        // Both sides have progress.save but we couldn't confirm
                        // whether it matches — never surface this as Conflict
                        // (would offer a destructive KeepLocal/KeepCloud choice
                        // based on data we never actually compared) or Identical
                        // (would hide a possible real conflict).
                        decision = SyncDecision.Unverified;
                    else
                        decision = slot.Differs ? SyncDecision.Conflict : SyncDecision.Identical;

                    results.Add(
                        new SyncDecisionResult
                        {
                            Decision = decision,
                            LocalSummary = localSummary,
                            CloudSummary = cloudSummary,
                            ProfileNumber = profile,
                            IsModded = modded,
                        }
                    );
                }
            }
        }
        finally
        {
            UserDataPathProvider.IsRunningModded = wasModded;
        }
        return results;
    }

    // Issue #64 — Save Manager entry points that must work even when cloud
    // sync is unavailable (disabled, no credentials, cache load failed; D7
    // "bypass"): a local-only inventory of every (profile × modded) slot, no
    // ICloudSaveStore touch at all. This is the local half of EvaluateSlotAsync
    // pulled out on its own — reuses the same BuildSummary/GetSize helpers so
    // the numbers match exactly what DeterminePerProfileAsync would show for
    // the local side. Unlike DeterminePerProfileAsync, empty slots are
    // INCLUDED (SaveProgressSummary.IsEmpty distinguishes them) — the profile-
    // copy destination picker needs to list and label empty slots too.
    public static Task<List<SaveProgressSummary>> SummarizeLocalSlotsAsync(ISaveStore local)
    {
        var results = new List<SaveProgressSummary>();
        var wasModded = UserDataPathProvider.IsRunningModded;
        try
        {
            for (int profile = 1; profile <= MaxProfiles; profile++)
            {
                foreach (bool modded in new[] { false, true })
                {
                    UserDataPathProvider.IsRunningModded = modded;

                    var progressPath = SavePathCompat.GetProgressPathForProfile(profile);
                    var runPath = SavePathCompat.GetRunSavePath(profile, "current_run.save");

                    int progressSize = local.FileExists(progressPath)
                        ? GetSize(local, progressPath)
                        : 0;
                    int runSize = local.FileExists(runPath) ? GetSize(local, runPath) : 0;

                    var summary = BuildSummary(
                        () => progressSize > 0 ? local.ReadFile(progressPath) : null,
                        progressSize,
                        progressSize > 0
                            ? local.GetLastModifiedTime(progressPath)
                            : DateTimeOffset.MinValue,
                        () => runSize > 0 ? local.ReadFile(runPath) : null,
                        runSize,
                        runSize > 0 ? local.GetLastModifiedTime(runPath) : DateTimeOffset.MinValue,
                        "local"
                    );
                    summary.ProfileNumber = profile;
                    summary.IsModded = modded;
                    results.Add(summary);
                }
            }
        }
        finally
        {
            UserDataPathProvider.IsRunningModded = wasModded;
        }
        // Purely local/synchronous — Task.FromResult keeps the signature
        // consistent with DetermineAsync/DeterminePerProfileAsync (both
        // genuinely async due to cloud reads) without an unnecessary state
        // machine here.
        return Task.FromResult(results);
    }

    // P1-1 (A1) — CloudFileCache loads once lazily on first access and
    // (before this) was never refreshed again for the rest of the session.
    // A KeepCloud pull, a PC-side upload, or anything else that changes the
    // cloud side mid-session was invisible to later decisions, which kept
    // comparing against the original boot-time snapshot — device-verified:
    // a KeepCloud pull landed (raw=228298) but the launcher kept reporting
    // the same false Conflict against a stale 217901-byte snapshot.
    //
    // Soft-cast: only SteamKit2CloudSaveStore exposes RefreshCache (it's not
    // part of the game's ICloudSaveStore contract) — anything else just skips
    // this, matching the previous (no-refresh) behavior. RefreshCache/
    // SafeRefresh already fail open on their own (roll back to the prior
    // snapshot rather than clearing it), but this is wrapped again here so a
    // decision computation can never be blocked/aborted by a refresh problem
    // — worst case, we fall through with whatever the cache already had.
    //
    // Deliberately NOT called from ManualPushAllAsync/mirror-delete paths —
    // a freshly-visible cloud-side file created mid-session there (e.g. a PC
    // current_run.save) could get mirror-deleted by the issue #31 logic
    // before the user ever sees it (steam risk B1). Push keeps its existing
    // WaitForCacheReadyAsync-only behavior.
    private static void RefreshCloudCacheIfPossible(ICloudSaveStore cloud, string context)
    {
        if (cloud is not SteamKit2CloudSaveStore refreshable)
            return;
        try
        {
            refreshable.RefreshCache();
        }
        catch (Exception ex)
        {
            PatchHelper.Log(
                $"[Cloud] {context}: cache refresh failed, using existing cache: {ex.Message}"
            );
        }
    }

    // Holds the raw per-slot comparison a single (profile × modded) pair
    // produces. Shared by DetermineAsync (aggregates every slot into one
    // summary) and DeterminePerProfileAsync (keeps each slot separate).
    private sealed class SlotEvaluation
    {
        public bool HasLocal;
        public bool HasCloud;
        public bool Differs;

        // Set when the progress.save byte-compare below could not complete —
        // both sides have the same cached size but the cloud content read
        // kept failing (even after ReadCloudFileWithRetryAsync's retry). We
        // never actually learned whether the profiles match, so callers must
        // not fold this into Differs (false Conflict) or leave it as a silent
        // Identical (false "everything matches").
        public bool ReadUnverified;
        public SaveProgressSummary LocalSummary;
        public SaveProgressSummary CloudSummary;
    }

    // Evaluates one (profile × modded) slot: existence, content diff, and
    // built summaries for both sides. Assumes the caller has already set
    // UserDataPathProvider.IsRunningModded = modded — SavePathCompat resolves
    // paths from that ambient state, same convention as the rest of this file.
    private static async Task<SlotEvaluation> EvaluateSlotAsync(
        ISaveStore local,
        ICloudSaveStore cloud,
        int profile,
        bool modded
    )
    {
        var path = SavePathCompat.GetProgressPathForProfile(profile);

        int localSize = local.FileExists(path) ? GetSize(local, path) : 0;
        int cloudSize = cloud.FileExists(path) ? cloud.GetFileSize(path) : 0;

        // Issue #7: also check current_run.save. progress.save alone misses
        // the most common cross-device case — an in-progress run on one
        // device with empty/identical progress on the other. Without this,
        // decision falls to NoData/Identical and the user is never prompted
        // to sync the in-progress run.
        var runPath = SavePathCompat.GetRunSavePath(profile, "current_run.save");
        int localRunSize = local.FileExists(runPath) ? GetSize(local, runPath) : 0;
        int cloudRunSize = cloud.FileExists(runPath) ? cloud.GetFileSize(runPath) : 0;

        bool hasLocal = localSize > 0 || localRunSize > 0;
        bool hasCloud = cloudSize > 0 || cloudRunSize > 0;

        bool profileDiffers = false;
        bool readUnverified = false;

        // Set when the byte-compare below successfully reads the cloud
        // progress.save content — reused for the summary build further down
        // so a same-size slot doesn't download progress.save twice (once to
        // compare, once to build the summary shown in the picker/dialog).
        string prefetchedCloudProgressContent = null;

        // Compare progress.save first (cheap size check, byte comparison only
        // when sizes match).
        if (localSize > 0 && cloudSize > 0)
        {
            if (localSize != cloudSize)
            {
                profileDiffers = true;
            }
            else
            {
                try
                {
                    var localContent = local.ReadFile(path);
                    var cloudContent = await ReadCloudFileWithRetryAsync(cloud, path)
                        .ConfigureAwait(false);
                    prefetchedCloudProgressContent = cloudContent;
                    if (localContent != cloudContent)
                        profileDiffers = true;
                }
                catch (Exception ex)
                {
                    // Do NOT set profileDiffers here — sizes match, so we have
                    // no evidence these actually differ. Claiming "diff" on a
                    // failed read is what causes a false Conflict (and, if the
                    // user picks KeepLocal, a silent overwrite of an
                    // identical-or-newer cloud copy). Mark unverified instead;
                    // the caller keeps this out of both Conflict and Identical.
                    PatchHelper.Log(
                        $"[Cloud] Decision: read unverified for {path}, not treating as diff: {ex.Message}"
                    );
                    readUnverified = true;
                }
            }
        }
        else if (localSize > 0 || cloudSize > 0)
        {
            // One side has progress.save, other doesn't.
            profileDiffers = true;
        }

        // Same comparison for current_run.save. Size-only check is sufficient
        // for triggering Conflict — actual sync direction is decided later by
        // SaveProgressComparer.
        if (localRunSize > 0 && cloudRunSize > 0)
        {
            if (localRunSize != cloudRunSize)
                profileDiffers = true;
        }
        else if (localRunSize > 0 || cloudRunSize > 0)
        {
            profileDiffers = true;
        }

        // issue #81 계측 (C): 이 슬롯의 differs 판정과 함께 양쪽 size/sha 실측.
        // sha 동일한데 differs=true 면 false-conflict 확정 + 트리거 크기 가시화.
        // 로컬 읽기만 하고 클라우드 다운로드는 없음 — 동작 불변.
        CloudDiag.LogSlot(
            local,
            cloud,
            profile,
            modded,
            path,
            localSize,
            cloudSize,
            runPath,
            localRunSize,
            cloudRunSize,
            profileDiffers
        );

        SaveProgressSummary localSummary = null;
        SaveProgressSummary cloudSummary = null;

        if (localSize > 0 || localRunSize > 0)
        {
            localSummary = BuildSummary(
                () => localSize > 0 ? local.ReadFile(path) : null,
                localSize,
                localSize > 0 ? local.GetLastModifiedTime(path) : DateTimeOffset.MinValue,
                () => localRunSize > 0 ? local.ReadFile(runPath) : null,
                localRunSize,
                localRunSize > 0 ? local.GetLastModifiedTime(runPath) : DateTimeOffset.MinValue,
                "local"
            );
            localSummary.ProfileNumber = profile;
            localSummary.IsModded = modded;
        }
        if (cloudSize > 0 || cloudRunSize > 0)
        {
            cloudSummary = await BuildCloudSummaryAsync(
                cloud,
                path,
                cloudSize,
                runPath,
                cloudRunSize,
                prefetchedCloudProgressContent
            );
            cloudSummary.ProfileNumber = profile;
            cloudSummary.IsModded = modded;
        }

        return new SlotEvaluation
        {
            HasLocal = hasLocal,
            HasCloud = hasCloud,
            Differs = profileDiffers,
            ReadUnverified = readUnverified,
            LocalSummary = localSummary,
            CloudSummary = cloudSummary,
        };
    }

    private static int GetSize(ISaveStore store, string path)
    {
        try
        {
            // ISaveStore doesn't expose a size primitive, but reading is cheap
            // for save files which are <1MB. Used only to decide whether a
            // file is non-empty.
            var content = store.ReadFile(path);
            return content?.Length ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static SaveProgressSummary BuildSummary(
        Func<string> readProgress,
        int progressSize,
        DateTimeOffset progressLastModified,
        Func<string> readRun,
        int runSize,
        DateTimeOffset runLastModified,
        string sideLabel
    )
    {
        SaveProgressSummary summary;
        try
        {
            var progressContent = progressSize > 0 ? readProgress() : null;
            summary = SaveProgressSummary.FromContent(
                progressContent ?? string.Empty,
                progressSize,
                progressLastModified
            );
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Cloud] Decision: {sideLabel} progress read failed: {ex.Message}");
            summary = new SaveProgressSummary();
        }

        if (runSize > 0)
        {
            try
            {
                summary.MergeCurrentRun(readRun(), runSize, runLastModified);
            }
            catch (Exception ex)
            {
                PatchHelper.Log(
                    $"[Cloud] Decision: {sideLabel} current_run read failed: {ex.Message}"
                );
            }
        }
        return summary;
    }

    private static async Task<SaveProgressSummary> BuildCloudSummaryAsync(
        ICloudSaveStore cloud,
        string progressPath,
        int progressSize,
        string runPath,
        int runSize,
        // Set when EvaluateSlotAsync's byte-compare already downloaded this
        // exact progress.save content (same-size slot, successful read) —
        // reuse it here instead of downloading progress.save a second time
        // just to build the summary. Null whenever no compare read happened
        // or it failed, in which case this falls back to its own read exactly
        // as before.
        string prefetchedProgressContent = null
    )
    {
        SaveProgressSummary summary;
        try
        {
            string progressContent = string.Empty;
            DateTimeOffset progressMtime = DateTimeOffset.MinValue;
            if (progressSize > 0)
            {
                progressContent =
                    prefetchedProgressContent
                    ?? await ReadCloudFileWithRetryAsync(cloud, progressPath).ConfigureAwait(false);
                progressMtime = cloud.GetLastModifiedTime(progressPath);
            }
            summary = SaveProgressSummary.FromContent(progressContent, progressSize, progressMtime);
        }
        catch (Exception ex)
        {
            // Don't collapse this to an empty (RawSize=0) summary — the cache
            // already told us this file is progressSize bytes, we just failed
            // to fetch its content. Reporting RawSize=0 here is exactly what
            // used to render as "Steam Cloud 비어있음", which is false and (if
            // acted on) would push local over a cloud copy we never read.
            // FromContent with empty content + the real cached size produces a
            // ParseSucceeded=false summary that IsEmpty correctly reports as
            // non-empty, distinguishing "couldn't verify" from "genuinely empty".
            PatchHelper.Log(
                $"[Cloud] Decision: cloud progress read unverified, falling back to cached size: {ex.Message}"
            );
            var mtime =
                progressSize > 0
                    ? cloud.GetLastModifiedTime(progressPath)
                    : DateTimeOffset.MinValue;
            summary = SaveProgressSummary.FromContent(string.Empty, progressSize, mtime);
        }

        if (runSize > 0)
        {
            try
            {
                var runContent = await ReadCloudFileWithRetryAsync(cloud, runPath)
                    .ConfigureAwait(false);
                var runMtime = cloud.GetLastModifiedTime(runPath);
                summary.MergeCurrentRun(runContent, runSize, runMtime);
            }
            catch (Exception ex)
            {
                PatchHelper.Log($"[Cloud] Decision: cloud current_run read failed: {ex.Message}");
            }
        }
        return summary;
    }

    // Wraps a single cloud file read with a bounded retry against the
    // transient cancellation SteamConnection can throw when its idle-timeout
    // disconnect races EnsureConnected's "already Connected, skip reconnect"
    // fast path (see SteamConnection.cs — EnsureConnected is checked before
    // SendMessage is issued, but the idle Timer can tear the socket down in
    // between). SendCloud already calls EnsureConnected on every attempt, so a
    // plain retry after a short delay is enough to let the connection recover
    // — no separate "force reconnect" call needed.
    //
    // Any exception that survives every attempt (transient or not) is
    // rethrown as-is; callers must NOT treat that as "file differs" or "file
    // empty" — see the Unverified handling in EvaluateSlotAsync / BuildCloudSummaryAsync.
    private static async Task<string> ReadCloudFileWithRetryAsync(
        ICloudSaveStore cloud,
        string path,
        int maxAttempts = 3
    )
    {
        // Unbounded `for(;;)` by design: every iteration either returns (success)
        // or, once attempt reaches maxAttempts, the `when` guard below stops
        // matching and the exception propagates out of the method directly —
        // so the loop itself never "runs out" and falls through.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                var content = await cloud.ReadFileAsync(path).ConfigureAwait(false);
                if (attempt > 1)
                    PatchHelper.Log($"[Cloud] Read for {path} succeeded on retry {attempt}");
                return content;
            }
            catch (Exception ex) when (attempt < maxAttempts && IsTransientCancellation(ex))
            {
                PatchHelper.Log(
                    $"[Cloud] Read for {path} failed (attempt {attempt}/{maxAttempts}, transient): "
                        + $"{ex.Message} — retrying..."
                );
                await Task.Delay(attempt * 500).ConfigureAwait(false);
            }
        }
    }

    // The idle-timeout race manifests as an OperationCanceledException (or the
    // TaskCanceledException subclass) bubbling out of SteamKit2's unified
    // message job when the connection drops mid-request.
    private static bool IsTransientCancellation(Exception ex) =>
        ex is OperationCanceledException
        || ex.Message.Contains("was canceled", StringComparison.OrdinalIgnoreCase);
}
