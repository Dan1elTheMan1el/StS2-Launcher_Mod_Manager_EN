using System.Collections.Generic;
using System.IO;

namespace STS2Mobile.Modding;

// Walks AppPaths.ExternalModsDir and returns one ModEntryInfo per subfolder that
// contains a valid manifest by the game's convention (any "*.json" with a non-empty
// id, searched recursively — see ModManifest.TryFindManifest). The launcher's older
// "mod_manifest.json" rule matched no real mod, so the Installed tab showed an empty
// list on device (issue #58).
public static class ModScanner
{
    public static List<ModEntryInfo> Scan()
    {
        var results = new List<ModEntryInfo>();
        if (!Directory.Exists(AppPaths.ExternalModsDir))
            return results;

        foreach (var dir in Directory.EnumerateDirectories(AppPaths.ExternalModsDir))
        {
            // Skip the Workshop download staging area — it holds partial payloads,
            // not an installed mod.
            if (Path.GetFileName(dir) == ".downloading")
                continue;

            if (!ModManifest.TryFindManifest(dir, out var manifest, out _))
                continue;

            results.Add(
                new ModEntryInfo
                {
                    Path = dir,
                    Manifest = manifest,
                    ReadmeSnippet = LoadReadmeSnippet(dir),
                }
            );
        }

        return results;
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
