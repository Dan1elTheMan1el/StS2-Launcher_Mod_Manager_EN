using System;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using STS2Mobile.Fmod;

namespace STS2Mobile.Patches;

// Issue #78 Part A: hooks ModManager.Initialize (postfix) to preload every
// loaded mod's FMOD bank(s) once every mod pck is mounted. See
// Fmod/FmodBankLoader.cs for the actual scan/copy/load_bank logic and the full
// rationale (fmod-gdextension's FMOD file I/O runs on its own thread and can't
// see mod pcks on external storage).
//
// As of sts2 v0.107.0, ModManager.Initialize is `async Task`. On Android this
// method's body always runs to synchronous completion in practice: the only two
// internal `await`s are gated by SteamInitializer.Initialized (always false —
// no Steamworks runtime) and OS.IsDebugBuild() (Task.Yield only in debug
// builds), and ModLoaderPatches already short-circuits the Steam mod
// enumerator. So by the time this postfix's calling frame observes the method
// return, __result is already a completed Task and every mod's pck has already
// been mounted and scanned by TryLoadMod. We still defensively check
// __result.IsCompleted rather than assume it, in case a future game version
// changes that.
public static class FmodBankPatches
{
    public static void Apply(Harmony harmony)
    {
        PatchHelper.Patch(
            harmony,
            typeof(ModManager),
            "Initialize",
            postfix: PatchHelper.Method(typeof(FmodBankPatches), nameof(InitializePostfix))
        );
    }

    public static void InitializePostfix(Task __result)
    {
        try
        {
            if (__result != null && !__result.IsCompleted)
            {
                PatchHelper.Log(
                    "[FmodBank] ModManager.Initialize returned an incomplete Task (unexpected on "
                        + "Android); skipping mod bank preload for this launch."
                );
                return;
            }
            FmodBankLoader.LoadAllModBanks();
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[FmodBank] Postfix failed: {ex.Message}");
        }
    }
}
