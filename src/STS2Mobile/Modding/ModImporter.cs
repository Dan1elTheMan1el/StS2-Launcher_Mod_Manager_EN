using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using Godot;

namespace STS2Mobile.Modding;

// Converts a user-supplied zip into a normalized mod folder under
// AppPaths.ExternalModsDir. Handles the two common zip layouts (manifest at root,
// or one level deep inside a wrapping folder) to match how mods are distributed
// on Nexus/GitHub.
public static class ModImporter
{
    public class ImportResult
    {
        public bool Success;
        public string ModId;
        public string Error;
        public bool AlreadyExists;
    }

    public static Task<ImportResult> ImportZipAsync(string zipPath, bool overwrite) =>
        Task.Run(() => ImportZip(zipPath, overwrite));

    private static ImportResult ImportZip(string zipPath, bool overwrite)
    {
        var tempRoot = Path.Combine(OS.GetCacheDir(), "mod_import_" + Guid.NewGuid().ToString("N"));
        // Keep the cached zip around when the caller needs to retry with overwrite;
        // otherwise delete it so we don't leak zips in /cache.
        bool keepZip = false;
        try
        {
            Directory.CreateDirectory(tempRoot);
            SafeExtract(zipPath, tempRoot);

            if (!ModManifest.TryFindManifest(tempRoot, out var manifest, out var manifestPath))
                return Fail("Selected zip is not a StS2 mod (no manifest .json with an 'id').");

            var modRoot = Path.GetDirectoryName(manifestPath);

            if (!ModIdValidator.IsValidId(manifest.Id))
                return Fail($"Invalid mod id: '{manifest.Id}'");

            Directory.CreateDirectory(AppPaths.ExternalModsDir);
            var dest = Path.Combine(AppPaths.ExternalModsDir, manifest.Id);
            if (Directory.Exists(dest))
            {
                if (!overwrite)
                {
                    keepZip = true;
                    return new ImportResult
                    {
                        Success = false,
                        ModId = manifest.Id,
                        AlreadyExists = true,
                    };
                }
                Directory.Delete(dest, recursive: true);
            }

            CopyDirectory(modRoot, dest);

            var cfg = ModConfig.Load();
            cfg.Add(manifest.Id, enabled: true);
            cfg.Save();

            return new ImportResult { Success = true, ModId = manifest.Id };
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Mods] Import failed: {ex}");
            return Fail("Import failed: " + ex.Message);
        }
        finally
        {
            TryDeleteDir(tempRoot);
            if (!keepZip)
                TryDeleteFile(zipPath);
        }
    }

    public static void CleanupImportZip(string zipPath) => TryDeleteFile(zipPath);

    // Extracts with Zip Slip protection — any entry whose resolved path escapes
    // the destination root is rejected.
    private static void SafeExtract(string zipPath, string destRoot)
    {
        var fullRoot = Path.GetFullPath(destRoot);
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(fullRoot, entry.FullName));
            AssertWithin(fullRoot, target);

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static void AssertWithin(string root, string target)
    {
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar.ToString())
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!target.StartsWith(rootWithSep, StringComparison.Ordinal) && target != root)
            throw new InvalidOperationException("Zip entry escapes extraction root: " + target);
    }

    private static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(src))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        foreach (var sub in Directory.EnumerateDirectories(src))
            CopyDirectory(sub, Path.Combine(dest, Path.GetFileName(sub)));
    }

    // Deletes a mod by its top-level folder (folder name is no longer assumed to
    // equal the id — issue #58) and drops its config entry. The folder must resolve
    // to a direct child of ExternalModsDir, guarding against a traversal escape.
    public static bool DeleteMod(string topLevelDir, string modId)
    {
        try
        {
            if (!string.IsNullOrEmpty(topLevelDir))
            {
                if (!AppPaths.IsDirectChildOfModsDir(topLevelDir))
                {
                    PatchHelper.Log(
                        $"[Mods] Refusing to delete '{topLevelDir}': not a direct child of Mods."
                    );
                    return false;
                }
                if (Directory.Exists(topLevelDir))
                    Directory.Delete(topLevelDir, recursive: true);
            }

            if (!string.IsNullOrEmpty(modId))
            {
                var cfg = ModConfig.Load();
                cfg.Remove(modId);
                cfg.Save();
            }
            return true;
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Mods] DeleteMod failed: {ex}");
            return false;
        }
    }

    private static ImportResult Fail(string error) => new() { Success = false, Error = error };

    private static void TryDeleteDir(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch { }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }
}
