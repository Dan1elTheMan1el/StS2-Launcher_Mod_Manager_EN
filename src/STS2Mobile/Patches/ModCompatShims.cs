using System;
using System.Reflection;
using HarmonyLib;

namespace STS2Mobile.Patches;

// EXPERIMENT (issue #65 follow-up): some Workshop mods gate their entire DLL
// behind RuntimeInformation.IsOSPlatform(Windows) and silently degrade to
// "resource-only" mode on Android, even though this runtime patches with
// Harmony just fine (the launcher itself and BaseLib do). This shim bypasses
// one such gate — Aeonglass Feminization (pfid 3747661487) — to test whether
// the mod's full patch set actually works on-device. If it does, the result
// is evidence for asking the author to relax the gate; this shim is NOT meant
// to ship as the long-term answer.
//
// Mod assemblies aren't loaded yet when Apply() runs (the game's ModManager
// loads them at game start), so we watch AssemblyLoad and patch the gate
// method the moment the target assembly appears — before the game calls the
// mod's initializer.
public static class ModCompatShims
{
    private const string TargetAssembly = "Aeonglass Feminization";
    private const string TargetType = "AeonglassFeminization.ModInit";
    private const string TargetMethod = "IsFullPatchPlatformSupported";

    private static Harmony _harmony;

    public static void Apply(Harmony harmony)
    {
        _harmony = harmony;
        AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
        PatchHelper.Log("[ModCompat] Watching for Aeonglass assembly (gate-bypass experiment)");
    }

    private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
    {
        try
        {
            var asm = args.LoadedAssembly;
            if (!string.Equals(asm.GetName().Name, TargetAssembly, StringComparison.Ordinal))
                return;

            var gate = asm.GetType(TargetType)
                ?.GetMethod(TargetMethod, BindingFlags.Static | BindingFlags.NonPublic);
            if (gate == null)
            {
                PatchHelper.Log(
                    "[ModCompat] Aeonglass gate method not found (mod updated?) — shim skipped"
                );
                return;
            }

            _harmony.Patch(
                gate,
                prefix: new HarmonyMethod(typeof(ModCompatShims), nameof(ForceTruePrefix))
            );
            PatchHelper.Log(
                $"[ModCompat] Aeonglass Windows gate bypassed (asm v{asm.GetName().Version}) — experimental"
            );
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[ModCompat] Aeonglass gate shim failed: {ex.Message}");
        }
    }

    private static bool ForceTruePrefix(ref bool __result)
    {
        __result = true;
        return false;
    }
}
