using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;
using MegaCrit.Sts2.Core.Saves.Migrations;

namespace STS2Mobile.Patches;

// Injects diagnostic logging into ProgressSaveManager.LoadProgress() via transpiler
// to trace why the game creates a fresh default save instead of loading the pulled one.
public static class SaveDiagnosticPatches
{
    // Issue #36 Part B repro hook (4b). When the external-storage marker file
    // /storage/emulated/0/StS2LauncherMM/.repro_issue36_emptywrite exists, the
    // FIRST game save through CloudSaveStore.WriteFile fires ONE empty write down
    // the game's real cloud path (CloudStore.WriteFile, = our SteamKit2 store
    // funnel) so device-test-qa can deterministically confirm the Part B guard
    // blocks it AND that the guard covers the game's DIRECT path (not just the
    // coordinator). Absent the marker this is a pure no-op (production-safe). The
    // injected write is non-destructive — it's empty bytes that the guard blocks
    // before any upload, and the original save proceeds with the real content
    // (local + cloud both get the genuine save).
    //
    // External storage (not user://): release builds are non-debuggable, so adb
    // can't touch the app-private user:// dir. The external root is adb-reachable
    // and already accessed by the app. Mirrors the .diagnose_issue7 marker pattern.
    private static readonly string ReproMarker =
        AppPaths.ExternalRoot + "/.repro_issue36_emptywrite";
    private static bool _reproFired;

    // P0-2 (Lightweight version) — set once this session if progress.save's load underwent
    // ANY form of recovery (lossy scavenge, or a hard failure that falls back
    // to ProgressState.CreateDefault() + an immediate re-save). Session-scoped
    // and sticky: only ever flips false→true (see LogLoadResult), never
    // resets, so a later clean load of a different profile doesn't mask an
    // earlier recovery. Consumed by STS2Mobile.Steam.ProgressRecoveryGate to
    // gate a subsequent progress.save cloud push behind a one-time user
    // confirmation instead of silently letting a possibly-degraded load
    // overwrite a good cloud copy.
    internal static bool ProgressLoadRecovered;

    public static void Apply(Harmony harmony)
    {
        PatchHelper.Patch(
            harmony,
            typeof(ProgressSaveManager),
            "LoadProgress",
            transpiler: PatchHelper.Method(
                typeof(SaveDiagnosticPatches),
                nameof(LoadProgressTranspiler)
            )
        );

        // Issue #36 (4b): repro hook on the game's direct save→cloud path. Patch
        // the (string, string) overload — game saves are JSON strings. Marker-gated.
        try
        {
            var writeFileStr = typeof(CloudSaveStore).GetMethod(
                "WriteFile",
                new[] { typeof(string), typeof(string) }
            );
            if (writeFileStr != null)
            {
                harmony.Patch(
                    writeFileStr,
                    prefix: new HarmonyMethod(
                        PatchHelper.Method(
                            typeof(SaveDiagnosticPatches),
                            nameof(Issue36ReproPrefix)
                        )
                    )
                );
                PatchHelper.Log("[Issue36-GuardB] repro hook armed on CloudSaveStore.WriteFile");
            }
            else
            {
                PatchHelper.Log(
                    "[Issue36-GuardB] repro hook NOT armed: WriteFile(string,string) not found"
                );
            }
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Issue36-GuardB] repro hook arm failed: {ex.Message}");
        }
    }

    // Marker-gated, fires at most once per session. Returns true so the original
    // save always runs with the real content (non-destructive). The injected empty
    // write travels the SAME path the game uses to reach the cloud
    // (__instance.CloudStore.WriteFile), exercising the funnel guard end-to-end.
    public static bool Issue36ReproPrefix(CloudSaveStore __instance, string path)
    {
        try
        {
            if (_reproFired || __instance?.CloudStore == null)
                return true;
            // External-storage marker (real filesystem path) — System.IO, not the
            // Godot user:// VFS. False (no-op) when absent or unreadable.
            if (!System.IO.File.Exists(ReproMarker))
                return true;

            // Only target progress/run saves — the data the guard is meant to
            // protect. Skip prefs/settings/history to keep the repro focused.
            var lower = path.Replace("user://", "").Replace("\\", "/").ToLowerInvariant();
            bool worthy =
                lower.EndsWith(".save")
                && (lower.Contains("progress") || lower.Contains("current_run"));
            if (!worthy)
                return true;

            // Safety gate: only inject when the cloud copy is CURRENTLY non-empty,
            // so the guard is guaranteed to block (empty-overwrite-of-non-empty).
            // If cloud is absent/empty the guard would ALLOW the empty write and it
            // could create/clobber a cloud file — so we skip injection in that case
            // and log it. This keeps the repro provably non-destructive.
            bool cloudHasData = false;
            try
            {
                cloudHasData =
                    __instance.CloudStore.FileExists(path)
                    && __instance.CloudStore.GetFileSize(path) > 2;
            }
            catch { }

            if (!cloudHasData)
            {
                PatchHelper.Log(
                    $"[Issue36-GuardB] repro SKIPPED for {path}: cloud not non-empty yet "
                        + "(would not be a safe empty-overwrite test). Retry after a real cloud save."
                );
                _reproFired = false; // allow a later save (with cloud data) to fire
                return true;
            }

            _reproFired = true;
            PatchHelper.Log(
                $"[Issue36-GuardB] repro: injecting empty write for {path} via game cloud path"
            );
            // Empty write down the game's real cloud funnel. The Part B guard
            // inside SteamKit2CloudSaveStore.WriteFile must block this (cloud is
            // non-empty) — nothing is uploaded. We do NOT touch LocalStore, so the
            // local save is untouched and the original WriteFile below completes
            // normally with the real content.
            __instance.CloudStore.WriteFile(path, Array.Empty<byte>());

            // Remove the marker so the repro fires exactly once even across the
            // session's later saves (defensive; _reproFired already guards).
            // External path → System.IO.File.Delete.
            try
            {
                System.IO.File.Delete(ReproMarker);
            }
            catch { }
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Issue36-GuardB] repro prefix failed: {ex.Message}");
        }
        return true; // Always run the original — real save proceeds untouched.
    }

    public static IEnumerable<CodeInstruction> LoadProgressTranspiler(
        IEnumerable<CodeInstruction> instructions
    )
    {
        var codes = new List<CodeInstruction>(instructions);
        bool injectedLoadSave = false;
        bool injectedCreateDefault = false;

        for (int i = 0; i < codes.Count; i++)
        {
            var ci = codes[i];

            // Match callvirt MigrationManager::LoadSave<SerializableProgress>.
            // DeclaringType check uses Name to handle generic type resolution differences.
            if (
                !injectedLoadSave
                && (ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt)
                && ci.operand is MethodInfo loadMethod
                && loadMethod.Name == "LoadSave"
                && loadMethod.DeclaringType?.Name == nameof(MigrationManager)
            )
            {
                codes.Insert(i + 1, new CodeInstruction(OpCodes.Dup));
                codes.Insert(
                    i + 2,
                    new CodeInstruction(
                        OpCodes.Call,
                        AccessTools.Method(typeof(SaveDiagnosticPatches), nameof(LogLoadResult))
                    )
                );
                PatchHelper.Log($"[Diag] Injected LoadSave logger at IL[{i}]");
                injectedLoadSave = true;
                i += 2;
            }

            // Match call ProgressState::CreateDefault.
            if (
                !injectedCreateDefault
                && (ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt)
                && ci.operand is MethodInfo createMethod
                && createMethod.Name == "CreateDefault"
                && createMethod.DeclaringType?.Name == nameof(ProgressState)
            )
            {
                codes.Insert(
                    i,
                    new CodeInstruction(
                        OpCodes.Call,
                        AccessTools.Method(
                            typeof(SaveDiagnosticPatches),
                            nameof(LogCreatingDefault)
                        )
                    )
                );
                PatchHelper.Log($"[Diag] Injected CreateDefault logger at IL[{i}]");
                injectedCreateDefault = true;
                i++;
            }
        }

        if (!injectedLoadSave)
            PatchHelper.Log("[Diag] WARNING: LoadSave call not found in LoadProgress IL");
        if (!injectedCreateDefault)
            PatchHelper.Log("[Diag] WARNING: CreateDefault call not found in LoadProgress IL");

        return codes;
    }

    public static void LogLoadResult(object result)
    {
        try
        {
            var type = result.GetType();
            var status = type.GetProperty("Status")?.GetValue(result);
            var success = type.GetProperty("Success")?.GetValue(result);
            var saveData = type.GetProperty("SaveData")?.GetValue(result);
            var error = type.GetProperty("ErrorMessage")?.GetValue(result);

            PatchHelper.Log(
                $"[Diag] LoadProgress result: Status={status}, "
                    + $"Success={success}, HasData={saveData != null}, "
                    + $"Error={error ?? "none"}"
            );

            // P0-2 (Lightweight version) — classify Status against MegaCrit.Sts2.Core.Saves.
            // ReadSaveStatus, decompiled from sts2.dll v0.108.0 (not guessed):
            // Success, JsonParseError, FileNotFound, FileEmpty, MigrationFailed,
            // MissingSchemaVersion, FutureVersion, VersionTooOld,
            // MigrationRequired, JsonRepaired, RecoveredWithDataLoss,
            // FileAccessError, Unrecoverable, ValidationFailed.
            //
            // ReadSaveResult<T>.Success is true only for Success/MigrationRequired/
            // JsonRepaired/RecoveredWithDataLoss — everything else is Success=false,
            // which makes ProgressSaveManager.LoadProgress() fall back to
            // ProgressState.CreateDefault() + an IMMEDIATE SaveProgress() call
            // (confirmed via decompile of both types). Flagged below EXCEPT:
            //   Success           — clean load, nothing recovered.
            //   FileNotFound      — no file ever existed (fresh profile); the
            //                       CreateDefault this ALSO triggers is normal
            //                       first-time init, not a recovery from real data.
            //   MigrationRequired — file intact, cleanly forward-migrated to a
            //                       newer schema (the designed, lossless upgrade
            //                       path — LoadWithAggressiveRecovery's own log
            //                       text confirms no data was discarded).
            // Everything flagged below represents "a real file existed and
            // something went wrong with it" — either a lossy scavenge
            // (RecoveredWithDataLoss/JsonRepaired) or an outright failed
            // recovery that's about to get overwritten with a blank default
            // (the rest). String-compared (not cast to the enum type) to
            // match this file's existing reflection-only, version-resilient
            // approach — an unrecognized/renamed value simply fails open
            // (flag stays whatever it already was).
            var statusName = status?.ToString();
            bool thisLoadRecovered = statusName switch
            {
                "RecoveredWithDataLoss" => true,
                "JsonRepaired" => true,
                "JsonParseError" => true,
                "FileEmpty" => true,
                "MigrationFailed" => true,
                "MissingSchemaVersion" => true,
                "FutureVersion" => true,
                "VersionTooOld" => true,
                "FileAccessError" => true,
                "Unrecoverable" => true,
                "ValidationFailed" => true,
                _ => false,
            };
            if (thisLoadRecovered)
            {
                ProgressLoadRecovered = true;
                PatchHelper.Log($"[Diag] ProgressLoadRecovered=true (Status={statusName})");
            }
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Diag] LogLoadResult failed: {ex.Message}");
        }
    }

    public static void LogCreatingDefault()
    {
        PatchHelper.Log(
            "[Diag] LoadProgress: creating default empty progress (load failed or file missing)"
        );
    }
}
