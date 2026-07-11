using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using HarmonyLib;

namespace STS2Mobile.Patches;

// EXPERIMENTAL mod-guard: some Workshop mods gate their DLL behind "am I on
// Windows?" and silently degrade on Android even though Harmony patching works
// fine here (proven on-device with Aeonglass Feminization, issue #65 follow-up).
//
// Answers the specific question "is this Windows?" with YES — but ONLY when the
// immediate caller is a mod assembly (tracked by ModAssemblyRegistry). The
// game, SteamKit2, BaseLib-as-infrastructure callers asking about OTHER
// platforms, and every non-mod caller always get the truth, so Steam session
// OS reporting and platform-dependent BCL behavior are unaffected.
//
// Minimal-lie principle: only IsOSPlatform(Windows) and OperatingSystem
// .IsWindows() are intercepted. IsAndroid()/IsLinux()/IsOSPlatform(other)
// stay truthful even for mods, so a mod that ALSO has Android-aware behavior
// keeps it.
//
// Fail-safe: every hook falls through to the original (truthful) behavior on
// any internal error.
public static class ModPlatformSpoofPatches
{
    private static int _truthLogBudget = 10; // first N decisions logged for field diagnosis

    public static void Apply(Harmony harmony)
    {
        TryPatch(
            harmony,
            AccessTools.Method(typeof(RuntimeInformation), nameof(RuntimeInformation.IsOSPlatform)),
            nameof(IsOSPlatformPrefix),
            "RuntimeInformation.IsOSPlatform"
        );
        TryPatch(
            harmony,
            AccessTools.Method(typeof(OperatingSystem), nameof(OperatingSystem.IsWindows)),
            nameof(IsWindowsPrefix),
            "OperatingSystem.IsWindows"
        );
    }

    private static void TryPatch(Harmony harmony, MethodBase target, string prefix, string label)
    {
        try
        {
            if (target == null)
            {
                PatchHelper.Log($"[ModGuard] {label} not found — spoof skipped");
                return;
            }
            harmony.Patch(
                target,
                prefix: new HarmonyMethod(typeof(ModPlatformSpoofPatches), prefix)
            );
            PatchHelper.Log($"[ModGuard] Patched {label} (caller-aware Windows spoof)");
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[ModGuard] {label} spoof failed to install: {ex.Message}");
        }
    }

    private static bool IsOSPlatformPrefix(OSPlatform osPlatform, ref bool __result)
    {
        try
        {
            if (osPlatform != OSPlatform.Windows)
                return true; // truth for every non-Windows question

            var modCaller = FindModCaller(out var callerName);
            if (_truthLogBudget > 0 && modCaller == null)
            {
                Interlocked.Decrement(ref _truthLogBudget);
                PatchHelper.Log($"[ModGuard] IsOSPlatform(WINDOWS) caller={callerName} -> truth");
            }
            if (modCaller == null)
                return true;

            __result = true;
            PatchHelper.Log($"[ModGuard] Spoofed IsOSPlatform(WINDOWS)=true for mod '{modCaller}'");
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static bool IsWindowsPrefix(ref bool __result)
    {
        try
        {
            var modCaller = FindModCaller(out _);
            if (modCaller == null)
                return true;

            __result = true;
            PatchHelper.Log($"[ModGuard] Spoofed OperatingSystem.IsWindows()=true for mod '{modCaller}'");
            return false;
        }
        catch
        {
            return true;
        }
    }

    // Walks up the stack to the first frame that isn't launcher/Harmony/BCL
    // plumbing; that frame's assembly decides. Returns the mod name, or null
    // when the caller isn't a tracked mod (or can't be determined — treated as
    // "not a mod" so the answer stays truthful).
    private static string FindModCaller(out string callerName)
    {
        callerName = "?";
        var st = new StackTrace(1, false);
        int frames = Math.Min(st.FrameCount, 16);
        for (int i = 0; i < frames; i++)
        {
            var method = st.GetFrame(i)?.GetMethod();
            var asm = method?.DeclaringType?.Assembly ?? method?.Module?.Assembly;
            if (asm == null)
                continue;
            if (asm == typeof(ModPlatformSpoofPatches).Assembly)
                continue;
            var name = asm.GetName().Name ?? "";
            if (
                name == "0Harmony"
                || name.StartsWith("MonoMod", StringComparison.Ordinal)
                // Harmony's replacement of the patched method itself sits on the
                // stack as a MonoMod DMD ("DMDASM.<hash>" assembly, dynamic OR
                // byte-loaded via the Cecil backend) — plumbing, not the caller.
                // Missing this filter made the spike misattribute the caller to
                // the trampoline and answer truth (device round 1).
                || asm.IsDynamic
                || name.StartsWith("DMDASM", StringComparison.Ordinal)
                || name == "System.Private.CoreLib"
                || name == "mscorlib"
                || name == "netstandard"
                || name.StartsWith("System.", StringComparison.Ordinal)
            )
                continue;

            callerName = name;
            return ModAssemblyRegistry.IsModAssembly(asm) ? name : null;
        }
        return null;
    }
}
