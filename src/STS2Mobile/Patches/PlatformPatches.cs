using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Platform.Null;
using MegaCrit.Sts2.Core.Saves;

namespace STS2Mobile.Patches;

// Disables desktop-only platform features that are unavailable or unnecessary on mobile:
// Steam initialization, Sentry crash reporting, system info logging, and telemetry opt-in.
public static class PlatformPatches
{
    public static void Apply(Harmony harmony)
    {
        PatchHelper.Patch(
            harmony,
            typeof(NGame),
            "InitializePlatform",
            prefix: PatchHelper.Method(typeof(PlatformPatches), nameof(InitializePlatformPrefix))
        );

        PatchHelper.Patch(
            harmony,
            typeof(OsDebugInfo),
            "LogSystemInfo",
            prefix: PatchHelper.Method(typeof(PlatformPatches), nameof(SkipPrefix))
        );

        PatchHelper.PatchGetter(
            harmony,
            typeof(PrefsSave),
            "UploadData",
            prefix: PatchHelper.Method(typeof(PlatformPatches), nameof(ReturnFalsePrefix))
        );

        // NullPlatformUtilStrategy's constructor calls CreateDirectory(".") which
        // fails on Android because "." is not a valid absolute Godot path.
        PatchHelper.Patch(
            harmony,
            typeof(GodotFileIo),
            "CreateDirectory",
            prefix: PatchHelper.Method(typeof(PlatformPatches), nameof(CreateDirectoryPrefix))
        );

        // Skip Sentry crash reporting. Not useful for our mobile port and the
        // Sentry GDExtension is not bundled in the Android build.
        PatchHelper.Patch(
            harmony,
            typeof(SentryService),
            "Initialize",
            prefix: PatchHelper.Method(typeof(PlatformPatches), nameof(SkipPrefix))
        );

        // Some Android ROMs (Lenovo ZUI) return locales with Java-style unicode
        // extension subtags from OS.GetLocale() (e.g. "ko_KR_#u-fw-mon-ms-metric-mu-celsius").
        // new CultureInfo(...) rejects that form, which kills LocManager.Initialize ->
        // OneTimeInitialization.ExecuteEssential -> NGame.GameStartup: permanent black
        // screen because the crash happens before SettingsSave.Language is first written.
        PatchHelper.Patch(
            harmony,
            typeof(NullPlatformUtilStrategy),
            "GetRawLanguage",
            postfix: PatchHelper.Method(typeof(PlatformPatches), nameof(SanitizeRawLanguagePostfix))
        );
    }

    public static bool InitializePlatformPrefix(ref Task<bool> __result)
    {
        PatchHelper.Log("Skipping Steam initialization (mobile)");
        __result = Task.FromResult(true);
        return false;
    }

    public static bool SkipPrefix() => false;

    public static bool ReturnFalsePrefix(ref bool __result)
    {
        __result = false;
        return false;
    }

    // Skip paths that aren't valid Godot absolute paths (must contain "://").
    public static bool CreateDirectoryPrefix(GodotFileIo __instance, string directoryPath)
    {
        var fullPath = __instance.GetFullPath(directoryPath);
        if (!fullPath.Contains("://"))
            return false;
        return true;
    }

    public static void SanitizeRawLanguagePostfix(ref string __result)
    {
        var sanitized = SanitizeLocale(__result);
        if (sanitized != __result)
        {
            PatchHelper.Log($"Sanitized platform locale '{__result}' -> '{sanitized}'");
            __result = sanitized;
        }
    }

    // Reduces a raw platform locale to a string new CultureInfo(...) is guaranteed
    // to accept: cut at the Java extension marker '#', drop BCP-47 extension chains
    // (first single-character subtag onward), then validate candidates from most to
    // least specific ("ko-KR" -> "ko"), falling back to "en".
    internal static string SanitizeLocale(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "en";

        var s = raw;
        var hashIndex = s.IndexOf('#');
        if (hashIndex >= 0)
            s = s.Substring(0, hashIndex);

        var kept = new List<string>();
        foreach (var part in s.Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length == 1)
                break;
            kept.Add(part);
        }

        for (var count = kept.Count; count >= 1; count--)
        {
            var candidate = string.Join("-", kept.GetRange(0, count));
            if (IsValidCulture(candidate))
                return candidate;
        }
        return "en";
    }

    private static bool IsValidCulture(string name)
    {
        try
        {
            _ = new CultureInfo(name);
            return true;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }
}
