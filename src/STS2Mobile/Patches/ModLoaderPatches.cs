using System;
using System.IO;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace STS2Mobile.Patches;

// Redirects the game's built-in mod loader to scan AppPaths.ExternalModsDir
// (/storage/emulated/0/StS2LauncherMM/Mods) instead of the "mods" folder next
// to the game executable. As of sts2 v0.107.0 ModManager.Initialize is async
// (Task), so the compiler hoists the body — including Path.Combine(..., "mods")
// — into a generated MoveNext state machine and the old ldstr "mods"
// transpiler against the main body no longer matches.
//
// New approach (issue #45): prefix-swap the IModManagerFileIo argument with a
// wrapper that transparently redirects any path under "mods" to our external
// directory. The game's own scanner then walks the right folder without us
// touching its IL. The Steam-only enumerator is still short-circuited because
// Android has no Steamworks runtime.
public static class ModLoaderPatches
{
    public static void Apply(Harmony harmony)
    {
        PatchHelper.Patch(
            harmony,
            typeof(ModManager),
            "Initialize",
            prefix: PatchHelper.Method(typeof(ModLoaderPatches), nameof(InitializePrefix))
        );
        PatchHelper.Patch(
            harmony,
            typeof(ModManager),
            "ReadSteamMods",
            prefix: PatchHelper.Method(typeof(ModLoaderPatches), nameof(ReadSteamModsPrefix))
        );
        PatchHelper.Patch(
            harmony,
            typeof(ModManager),
            "ReadModManifest",
            prefix: PatchHelper.Method(typeof(ModLoaderPatches), nameof(ReadModManifestPrefix))
        );
    }

    // Swap the fileIo argument the game just constructed for our redirecting
    // wrapper; the original Initialize body then continues unchanged. Using
    // `ref` keeps this signature-stable across the sync/void → async/Task
    // rewrite the game shipped in v0.107.0.
    public static bool InitializePrefix(ref IModManagerFileIo fileIo)
    {
        var originalFileIo = fileIo;
        fileIo = new ExternalModsFileIo(AppPaths.ExternalModsDir, originalFileIo);
        PatchHelper.Log(
            $"[Mods] Redirected ModManager.Initialize fileIo -> {AppPaths.ExternalModsDir}"
        );
        return true;
    }

    // Skip the Steam-backed mod enumeration on Android (no Steamworks runtime).
    public static bool ReadSteamModsPrefix() => false;

    // The launcher's mod registry (mod_config.json, numeric "version" by design)
    // lives at the root of the redirected mods dir, and the game's scanner tries
    // every *.json in the tree as a mod manifest — logging a caught JsonException
    // for the registry on every launch (issue #71). Skip it before the parse.
    public static bool ReadModManifestPrefix(string filename, ref Mod __result)
    {
        if (
            string.Equals(
                Path.GetFileName(filename),
                "mod_config.json",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            __result = null;
            return false;
        }
        return true;
    }
}
