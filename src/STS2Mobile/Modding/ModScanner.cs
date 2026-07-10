using System;
using System.Collections.Generic;
using System.IO;

namespace STS2Mobile.Modding;

// Walks AppPaths.ExternalModsDir by the game's mod convention (issue #58): each
// top-level folder is searched recursively for every "*.json" that parses with a
// non-empty id (see ModManifest.EnumerateManifests). The game loads all of them, so
// the scanner surfaces one ModEntryInfo per manifest. Global id collisions keep the
// first and warn. The launcher's older "mod_manifest.json" rule matched no real mod,
// leaving the Installed tab empty on device.
public static class ModScanner
{
    // Scan() runs on every list refresh (including throttled download-progress
    // refreshes), so per-scan warnings about the same duplicate/root manifest
    // would flood logcat. Warn once per unique path per session.
    private static readonly HashSet<string> _warnedPaths = new(StringComparer.Ordinal);

    // Scans BOTH roots: Mods/ (enabled — what the game loads) and ModsDisabled/
    // (the stash — invisible to the game). Enabled mods are scanned first so on a
    // duplicate id the enabled copy wins and the stashed one is warn-skipped.
    public static List<ModEntryInfo> Scan()
    {
        var results = new List<ModEntryInfo>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        WarnRootLevelManifests();
        ScanRoot(AppPaths.ExternalModsDir, disabled: false, seenIds, results);
        ScanRoot(AppPaths.DisabledModsDir, disabled: true, seenIds, results);
        return results;
    }

    private static void ScanRoot(
        string root,
        bool disabled,
        HashSet<string> seenIds,
        List<ModEntryInfo> results
    )
    {
        if (!Directory.Exists(root))
            return;

        foreach (var topDir in Directory.EnumerateDirectories(root))
        {
            // Skip staging areas (".downloading" and any future dot-dirs) — they
            // hold partial payloads, not installed mods.
            if (Path.GetFileName(topDir).StartsWith("."))
                continue;

            foreach (var (manifest, manifestPath) in ModManifest.EnumerateManifests(topDir))
            {
                if (!seenIds.Add(manifest.Id))
                {
                    lock (_warnedPaths)
                    {
                        if (_warnedPaths.Add("dup:" + manifestPath))
                            PatchHelper.Log(
                                $"[Mods] Duplicate mod id '{manifest.Id}' — keeping the first, "
                                    + $"ignoring {manifestPath}"
                            );
                    }
                    continue;
                }

                var manifestDir = Path.GetDirectoryName(manifestPath);
                results.Add(
                    new ModEntryInfo
                    {
                        Path = manifestDir,
                        TopLevelDir = topDir,
                        Manifest = manifest,
                        ReadmeSnippet = LoadReadmeSnippet(manifestDir),
                        Disabled = disabled,
                    }
                );
            }
        }
    }

    // A "*.json" manifest sitting directly in Mods/ (not inside a folder) is loaded
    // by the game but can't be managed by the launcher — enable/order/delete are
    // keyed to a top-level folder. Surface it as a one-line warning instead of a row.
    private static void WarnRootLevelManifests()
    {
        try
        {
            foreach (var json in Directory.GetFiles(AppPaths.ExternalModsDir, "*.json"))
            {
                if (
                    string.Equals(
                        Path.GetFileName(json),
                        "mod_config.json",
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                    continue;

                var m = ModManifest.TryParse(json);
                if (m != null && m.IsValid())
                {
                    lock (_warnedPaths)
                    {
                        if (_warnedPaths.Add("root:" + json))
                            PatchHelper.Log(
                                $"[Mods] Root-level manifest '{Path.GetFileName(json)}' (id={m.Id}) is "
                                    + "loaded by the game but not managed by the launcher (no containing folder)."
                            );
                    }
                }
            }
        }
        catch { }
    }

    private static string LoadReadmeSnippet(string modDir)
    {
        foreach (var name in new[] { "README.md", "readme.md", "README.txt", "readme.txt" })
        {
            var path = Path.Combine(modDir, name);
            if (!File.Exists(path))
                continue;
            try
            {
                var text = File.ReadAllText(path).Trim();
                return text.Length > 300 ? text.Substring(0, 300) + "..." : text;
            }
            catch { }
        }
        return null;
    }
}
