using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using STS2Mobile.Steam;

namespace STS2Mobile.Modding;

// Commits a downloaded Workshop item into the Mods/ tree and records its
// provenance in mod_config.json. Bridges WorkshopDownloader (raw bytes into a
// temp dir) and the launcher's mod registry.
//
// Safety: only ever replaces a mod folder whose existing config entry is already
// source=workshop. A manually installed mod occupying the same id is never
// overwritten — the caller is told via Conflict so it can prompt the user. This
// upholds the "never auto-delete manual mods" rule (issue #58 / Mods mirror-delete
// prohibition).
public static class WorkshopInstaller
{
    public class WorkshopInstallResult
    {
        public bool Success;
        public string ModId;
        public string Error;

        // A manually installed mod already occupies this id — not overwritten.
        public bool Conflict;

        // The downloaded payload had no mod_manifest.json (unexpected layout).
        public bool NoManifest;
    }

    // Downloads the item into Mods/.downloading/<id>/, installs it, then removes
    // the temp dir. This is the single entry point the sync/browser layers call.
    public static async Task<WorkshopInstallResult> DownloadAndInstallAsync(
        SteamConnection connection,
        WorkshopItemDetails item,
        IProgress<DownloadProgress> progress = null,
        CancellationToken ct = default
    )
    {
        if (connection == null)
            throw new ArgumentNullException(nameof(connection));
        if (item == null)
            throw new ArgumentNullException(nameof(item));

        var downloadingRoot = Path.Combine(AppPaths.ExternalModsDir, ".downloading");
        var itemDir = Path.Combine(downloadingRoot, item.PublishedFileId.ToString());

        try
        {
            TryDeleteDir(itemDir); // clear any stale partial from a previous run
            Directory.CreateDirectory(itemDir);

            using (var downloader = new WorkshopDownloader(connection))
                await downloader.DownloadAsync(item, itemDir, progress, ct).ConfigureAwait(false);

            return await InstallAsync(item, itemDir).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            PatchHelper.Log(
                $"[Workshop] Download/install failed for {item.PublishedFileId}: {ex}"
            );
            return new WorkshopInstallResult { Success = false, Error = ex.Message };
        }
        finally
        {
            TryDeleteDir(itemDir);
        }
    }

    // Finds the mod tree inside downloadedDir, commits it to Mods/<id>/, and
    // upserts a source=workshop entry in mod_config.json (preserving enabled/order
    // for an existing workshop mod).
    public static Task<WorkshopInstallResult> InstallAsync(
        WorkshopItemDetails item,
        string downloadedDir
    ) => Task.Run(() => Install(item, downloadedDir));

    private static WorkshopInstallResult Install(WorkshopItemDetails item, string downloadedDir)
    {
        try
        {
            // Find the mod's manifest by the game's convention (any "*.json" with an
            // id — real Workshop items ship e.g. BaseLib.json, not mod_manifest.json).
            if (!ModManifest.TryFindManifest(downloadedDir, out var manifest, out var manifestPath))
            {
                PatchHelper.Log(
                    $"[Workshop] Item {item.PublishedFileId}: no manifest .json with an 'id' in "
                        + $"payload (root={downloadedDir}) — leaving nothing installed."
                );
                return new WorkshopInstallResult
                {
                    Success = false,
                    NoManifest = true,
                    Error = "Downloaded Workshop item has no manifest .json with an 'id'.",
                };
            }

            var modRoot = Path.GetDirectoryName(manifestPath);

            if (!IsValidId(manifest.Id))
                return new WorkshopInstallResult
                {
                    Success = false,
                    Error = $"Invalid mod id: '{manifest.Id}'",
                };

            var cfg = ModConfig.Load();
            var existing = cfg.Get(manifest.Id);
            var dest = Path.Combine(AppPaths.ExternalModsDir, manifest.Id);
            bool destExists = Directory.Exists(dest);

            // Conflict: an id folder is present but not tracked as a workshop mod —
            // treat as a manual install and refuse to overwrite it.
            if (destExists && (existing == null || !existing.IsWorkshop))
            {
                PatchHelper.Log(
                    $"[Workshop] Item {item.PublishedFileId}: id '{manifest.Id}' already installed "
                        + "as a non-workshop mod — not overwriting (conflict)."
                );
                return new WorkshopInstallResult
                {
                    Success = false,
                    ModId = manifest.Id,
                    Conflict = true,
                };
            }

            Directory.CreateDirectory(AppPaths.ExternalModsDir);
            if (destExists)
                Directory.Delete(dest, recursive: true);
            CopyDirectory(modRoot, dest);

            // Upsert the registry entry, preserving user enabled/order for updates.
            if (existing == null)
            {
                int nextOrder = cfg.Mods.Count == 0 ? 0 : cfg.Mods.Max(m => m.Order) + 1;
                existing = new ModConfigEntry
                {
                    Id = manifest.Id,
                    Enabled = true,
                    Order = nextOrder,
                };
                cfg.Mods.Add(existing);
            }
            existing.Source = ModConfigEntry.SourceWorkshop;
            existing.PublishedFileId = item.PublishedFileId;
            existing.TimeUpdated = item.TimeUpdated;
            cfg.Save();

            PatchHelper.Log(
                $"[Workshop] Installed '{manifest.Id}' from item {item.PublishedFileId} "
                    + $"(timeUpdated={item.TimeUpdated})"
            );
            return new WorkshopInstallResult { Success = true, ModId = manifest.Id };
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Workshop] Install failed for {item.PublishedFileId}: {ex}");
            return new WorkshopInstallResult { Success = false, Error = ex.Message };
        }
    }

    private static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(src))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        foreach (var sub in Directory.EnumerateDirectories(src))
            CopyDirectory(sub, Path.Combine(dest, Path.GetFileName(sub)));
    }

    private static bool IsValidId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;
        foreach (var c in id)
        {
            if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.'))
                return false;
        }
        return true;
    }

    private static void TryDeleteDir(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch { }
    }
}
