using System;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Saves;
using STS2Mobile.Launcher;
using STS2Mobile.Launcher.Components;
using STS2Mobile.Patches;

namespace STS2Mobile.Steam;

// P0-2 (경량판) — gates a progress.save cloud push behind a one-time user
// confirmation when SaveDiagnosticPatches.ProgressLoadRecovered is set (this
// session's progress load underwent some form of recovery — see that flag's
// doc comment for the exact trigger conditions). Two call sites only, both
// KeepLocal-direction pushes: CloudSyncCoordinator.ManualPushAllAsync and
// LauncherPatches.ApplyChosenSideAsync/ApplyChosenSideForSlotAsync — never
// the background AutoSync path (no UI available mid-gameplay).
//
// Zero-touch in the common case: when the flag is unset (the overwhelming
// majority of sessions), ShouldAllowPushAsync short-circuits to true without
// touching the cloud store or showing anything.
public static class ProgressRecoveryGate
{
    // Latches true on user confirmation and never resets — "그 세션은 이후
    // 재질문 없음" (per spec). A decline does NOT set this: it only skips the
    // one push that triggered it, so the next opportunity asks again.
    private static volatile bool _confirmedThisSession;

    // Bytes at or below this are "nothing to protect" — matches
    // CloudWriteGuard's EmptyByteThreshold convention (a couple of stray
    // bytes can't be a real save).
    private const int EmptyByteThreshold = 2;

    public static async Task<bool> ShouldAllowPushAsync(ICloudSaveStore cloud, string progressPath)
    {
        if (!SaveDiagnosticPatches.ProgressLoadRecovered)
            return true; // Common case: nothing recovered this session.

        if (_confirmedThisSession)
            return true; // Already confirmed once — no more asking.

        // Only worth asking if we'd actually be overwriting something on the
        // cloud side — an absent/trivial cloud copy has nothing to protect,
        // so gating it would just be an annoying no-op confirmation.
        bool cloudHasExisting;
        try
        {
            cloudHasExisting =
                cloud.FileExists(progressPath) && cloud.GetFileSize(progressPath) > EmptyByteThreshold;
        }
        catch (Exception ex)
        {
            // Fail open — a guard that can't verify cloud state must never
            // block a normal push.
            PatchHelper.Log(
                $"[Cloud] Recovered-session gate check failed for {progressPath}, allowing push: {ex.Message}"
            );
            return true;
        }
        if (!cloudHasExisting)
            return true;

        PatchHelper.Log("[Cloud] Recovered-session progress push gated (awaiting user confirm)");
        bool confirmed = await AskUserAsync().ConfigureAwait(false);
        if (confirmed)
        {
            _confirmedThisSession = true;
            PatchHelper.Log("[Cloud] Recovered-session progress push confirmed by user");
            return true;
        }

        PatchHelper.Log("[Cloud] Recovered-session progress push skipped by user");
        return false;
    }

    // Schedules the confirmation dialog onto the main thread (CallDeferred)
    // and returns a Task that resolves once the user picks — or immediately
    // with true (fail-open) if there's nowhere to show it. Never blocks a
    // thread synchronously: RunContinuationsAsynchronously plus a genuine
    // await on the caller side means this is safe to call from any thread,
    // including one that's itself running as the continuation of another
    // main-thread-resolved dialog (see CloudConflictDialog's own TCS) —
    // blocking-wait here would risk deadlocking against the very main-thread
    // frame loop that has to process the deferred call.
    private static Task<bool> AskUserAsync()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree || tree.Root == null)
            {
                // No UI to show — fail open rather than leave the push stuck
                // waiting on a confirmation that can never arrive.
                tcs.TrySetResult(true);
                return tcs.Task;
            }

            float scale = LauncherUI.ResolveScale(tree.Root);
            Callable.From(() => ShowDialog(tree.Root, scale, tcs)).CallDeferred();
        }
        catch (Exception ex)
        {
            PatchHelper.Log(
                $"[Cloud] Recovered-session confirm dialog scheduling failed, allowing push: {ex.Message}"
            );
            tcs.TrySetResult(true); // fail-open
        }
        return tcs.Task;
    }

    private static void ShowDialog(Node parent, float scale, TaskCompletionSource<bool> tcs)
    {
        try
        {
            var dialog = new StyledDialog(
                "이번 세션은 세이브를 복구 모드로 열었습니다(게임 버전 차이). "
                    + "로컬을 클라우드로 덮으시겠습니까?",
                scale,
                okLabel: "덮어쓰기",
                cancelLabel: "취소"
            );
            // Above CloudConflictDialog (200) since this can appear mid
            // pre-PLAY handshake, stacked over that dialog's own
            // conflict-resolution flow.
            dialog.ZIndex = 201;
            dialog.Confirmed += () => tcs.TrySetResult(true);
            dialog.Cancelled += () => tcs.TrySetResult(false);
            parent.AddChild(dialog);
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Cloud] Recovered-session confirm dialog failed, allowing push: {ex.Message}");
            tcs.TrySetResult(true); // fail-open
        }
    }
}
