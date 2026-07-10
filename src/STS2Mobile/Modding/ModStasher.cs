using System;
using System.IO;
using System.Linq;

namespace STS2Mobile.Modding;

// Enable/disable ("stash") a mod by moving its top-level folder between
// AppPaths.ExternalModsDir (game-visible) and AppPaths.DisabledModsDir (invisible
// to the game's recursive mods scan). Same volume ⇒ Directory.Move is an instant
// rename; nothing is downloaded or deleted, and the user can equally move folders
// by hand — the scanner derives state from folder location either way (issue #58).
public static class ModStasher
{
    // Mods/<dir> → ModsDisabled/<dir>. Never overwrites an existing stash entry.
    public static (bool Ok, string Error) Disable(ModEntryInfo info) =>
        Move(
            info,
            expectRoot: AppPaths.ExternalModsDir,
            destRoot: AppPaths.DisabledModsDir,
            action: "disable"
        );

    // ModsDisabled/<dir> → Mods/<dir>. Refuses when the id is already provided by
    // an enabled mod (two active copies of one id would shadow-fight at runtime).
    public static (bool Ok, string Error) Enable(ModEntryInfo info)
    {
        if (info?.Id != null)
        {
            var activeSameId = ModScanner
                .Scan()
                .FirstOrDefault(m => !m.Disabled && m.Id == info.Id);
            if (activeSameId != null)
                return (
                    false,
                    $"'{info.Id}' is already enabled (at {activeSameId.TopLevelDir})."
                );
        }

        return Move(
            info,
            expectRoot: AppPaths.DisabledModsDir,
            destRoot: AppPaths.ExternalModsDir,
            action: "enable"
        );
    }

    private static (bool Ok, string Error) Move(
        ModEntryInfo info,
        string expectRoot,
        string destRoot,
        string action
    )
    {
        var src = info?.TopLevelDir;
        if (string.IsNullOrEmpty(src))
            return (false, "Mod has no containing folder the launcher can move.");
        if (!AppPaths.IsDirectChildOf(expectRoot, src))
            return (false, $"Refusing to {action}: not a direct child of {expectRoot}.");
        if (!Directory.Exists(src))
            return (false, "Mod folder no longer exists — refresh the list.");

        var dest = Path.Combine(destRoot, Path.GetFileName(src));
        if (Directory.Exists(dest) || File.Exists(dest))
            return (
                false,
                $"'{Path.GetFileName(src)}' already exists in the destination folder — resolve it in a file manager first."
            );

        try
        {
            Directory.CreateDirectory(destRoot);
            Directory.Move(src, dest);
            PatchHelper.Log($"[Mods] {action}: {src} -> {dest}");
            return (true, null);
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Mods] {action} failed for {src}: {ex.Message}");
            return (false, ex.Message);
        }
    }
}
