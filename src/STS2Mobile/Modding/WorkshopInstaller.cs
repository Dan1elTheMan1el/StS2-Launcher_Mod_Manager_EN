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

        // The downloaded payload had no manifest .json with an 'id' (unexpected layout).
        public bool NoManifest;
    }

    // Serializes downloads of the SAME item: the conflict-resolve path and a
    // sync-triggered queue download can race on the shared
    // Mods/.downloading/<pfid>/ temp dir (observed on device as
    // DirectoryNotFoundException when one deleted the dir mid-download of the
    // other). One gate per published file id.
    private static readonly object _gatesLock = new();
    private static readonly System.Collections.Generic.Dictionary<ulong, SemaphoreSlim> _gates =
        new();

    private static SemaphoreSlim GetGate(ulong pfid)
    {
        lock (_gatesLock)
        {
            if (!_gates.TryGetValue(pfid, out var gate))
            {
                gate = new SemaphoreSlim(1, 1);
                _gates[pfid] = gate;
            }
            return gate;
        }
    }

    // Clears leftovers in Mods/.downloading from a previous app session that was
    // killed mid-download. Call once when the download queue is (re)created —
    // nothing can be in flight at that moment, so everything in there is stale.
    public static void CleanStaleDownloads()
    {
        try
        {
            var root = Path.Combine(AppPaths.ExternalModsDir, ".downloading");
            if (!Directory.Exists(root))
                return;
            foreach (var dir in Directory.EnumerateDirectories(root))
                TryDeleteDir(dir);
            PatchHelper.Log("[Workshop] Cleared stale download staging.");
        }
        catch { }
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

        var gate = GetGate(item.PublishedFileId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
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
            PatchHelper.Log($"[Workshop] Download/install failed for {item.PublishedFileId}: {ex}");
            return new WorkshopInstallResult { Success = false, Error = ex.Message };
        }
        finally
        {
            TryDeleteDir(itemDir);
            gate.Release();
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

            // Detect an existing install of this mod id ANYWHERE under Mods/, not
            // just at Mods/<id>/. The game keys mods by manifest id, not folder
            // name, so a manual install can live in a folder whose name differs
            // from the id (e.g. savemanager/ holding SaveMerger.json). A
            // folder-name-only check missed those and silently created a duplicate
            // folder AND hijacked the manual mod's registry entry into a workshop
            // entry (issue #58 — the SaveMerger case).
            var sameId = ModScanner
                .Scan()
                .FirstOrDefault(m => string.Equals(m.Id, manifest.Id, StringComparison.Ordinal));

            // We may (re)install to Mods/<id>/ only when the id isn't present, or
            // the only present copy is our own prior Workshop download of THIS same
            // item. Anything else (a manual install, or a different Workshop item
            // claiming the id) is a conflict we refuse to overwrite.
            bool ourPriorCopy =
                existing != null
                && existing.IsWorkshop
                && existing.PublishedFileId == item.PublishedFileId;

            // Our own prior copy, but currently stashed (ModsDisabled/): installing
            // now would create an ENABLED duplicate next to the stashed one and
            // silently un-stash the mod. The user disables on purpose — respect it.
            if (sameId != null && sameId.Disabled && ourPriorCopy)
            {
                PatchHelper.Log(
                    $"[Workshop] Item {item.PublishedFileId}: '{manifest.Id}' is stashed "
                        + "(disabled) — not installing over it."
                );
                return new WorkshopInstallResult
                {
                    Success = false,
                    ModId = manifest.Id,
                    Error = "Mod is disabled (stashed). Enable it first, then update.",
                };
            }

            if (sameId != null && !ourPriorCopy)
            {
                var installedVersion = sameId.Manifest?.Version ?? "?";
                PatchHelper.Log(
                    $"[Workshop] Item {item.PublishedFileId}: mod id '{manifest.Id}' is already "
                        + $"installed (at {sameId.TopLevelDir}, v{installedVersion}) from another "
                        + $"source; Workshop copy is v{manifest.Version ?? "?"} — not overwriting "
                        + "(conflict)."
                );
                // Remember so the sync engine stops re-downloading this every visit
                // until the item is updated on Steam. The version is captured so the
                // UI can show which side is stale and offer to switch.
                cfg.RememberConflict(
                    item.PublishedFileId,
                    manifest.Id,
                    item.TimeUpdated,
                    manifest.Version
                );
                cfg.Save();
                return new WorkshopInstallResult
                {
                    Success = false,
                    ModId = manifest.Id,
                    Conflict = true,
                };
            }

            Directory.CreateDirectory(AppPaths.ExternalModsDir);
            if (Directory.Exists(dest))
                Directory.Delete(dest, recursive: true);
            // Commit by rename, not by copy: a copy interrupted by app death leaves
            // a half-written mod the game would happily load. A same-volume move is
            // near-atomic; if death lands between the delete above and this move,
            // the mod is simply absent and the next sync re-downloads it.
            Directory.Move(modRoot, dest);

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
            cfg.ClearConflict(item.PublishedFileId);
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

    private static bool IsValidId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;
        // "." and ".." are path segments, not mod ids — reject them so Mods/<id>
        // can never resolve to the Mods dir itself or its parent.
        if (id == "." || id == "..")
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
