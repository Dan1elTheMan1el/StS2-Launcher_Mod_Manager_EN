using System;
using System.IO;
using Godot;

namespace STS2Mobile;

// Shared path constants for external storage directories and permission helpers.
public static class AppPaths
{
    // Renamed in 0.3.0 to avoid sharing external storage with Ekyso's upstream
    // fork (issue #3). Users upgrading from the previous /StS2Launcher path
    // are pointed to README/release notes for the manual move — auto-migration
    // would only help upgrade-from-our-fork users while adding code complexity
    // and surprising fresh installers, so it's intentionally not implemented.
    public const string ExternalRoot = "/storage/emulated/0/StS2LauncherMM";
    public const string ExternalModsDir = ExternalRoot + "/Mods";
    public const string ExternalSaveBackupsDir = ExternalRoot + "/Saves";
    public const string ExternalLogsDir = ExternalRoot + "/Logs";

    // Issue #36 Part A redesign: backups are segregated by origin under Saves/.
    //   manual/ — user-triggered snapshots (LocalBackupService.BackupNow). Never
    //             auto-evicted (FIFO-protected) so an intentional backup is durable.
    //   auto/   — pre-PLAY handshake snapshots. FIFO-capped (newest N sets kept).
    public const string ExternalManualBackupsDir = ExternalSaveBackupsDir + "/manual";
    public const string ExternalAutoBackupsDir = ExternalSaveBackupsDir + "/auto";

    // User-editable configs that need to be reachable without root/ADB (issue #26).
    public const string ExternalConfigDir = ExternalRoot + "/Config";
    public const string ExternalModConfigFile = ExternalModsDir + "/mod_config.json";

    // Issue #58: Workshop preview-image cache. App-internal storage (not
    // ExternalRoot) since these are disposable, re-downloadable thumbnails with
    // no reason to survive an uninstall or be visible outside the app, matching
    // the existing OS.GetCacheDir() usage in ModImporter for import scratch space.
    public static string ThumbCacheDir => Path.Combine(OS.GetCacheDir(), "workshop_thumbs");

    // Issue #36 Part A: builds the on-disk backup path that mirrors a save's
    // original folder structure and filename under a backup-set directory. The
    // filename is never altered, so a restore is a plain copy of the original file
    // back to user://. Example:
    //   savePath  = "user://steam/7656.../profile1/saves/progress.save"
    //   setDir    = "<ExternalAutoBackupsDir>/20260522_153000_match"
    //   => <setDir>/steam/7656.../profile1/saves/progress.save
    public static string BuildBackupPath(string setDir, string savePath)
    {
        var relative = savePath.Replace("user://", "").Replace("\\", "/").TrimStart('/');
        var combined = setDir;
        foreach (var segment in relative.Split('/'))
        {
            if (segment.Length > 0)
                combined = Path.Combine(combined, segment);
        }
        return combined;
    }

    // True only if `dir` resolves to a direct child of ExternalModsDir. Guards mod
    // deletion against a folder name / id that escapes the Mods tree (e.g. an id of
    // ".." would make Path.Combine(ExternalModsDir, id) resolve to the parent — a
    // catastrophic recursive delete). Callers must gate every mod-folder delete on
    // this (issue #58: folder name may differ from id, so deletes are path-based).
    public static bool IsDirectChildOfModsDir(string dir)
    {
        if (string.IsNullOrEmpty(dir))
            return false;
        try
        {
            var full = Path.GetFullPath(dir).TrimEnd('/', '\\');
            var modsFull = Path.GetFullPath(ExternalModsDir).TrimEnd('/', '\\');
            var parent = Path.GetDirectoryName(full);
            return parent != null && string.Equals(parent, modsFull, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    // Returns true if the app has permission to write to shared external storage.
    public static bool HasStoragePermission()
    {
        try
        {
            var godotApp = GetGodotApp();
            if (godotApp == null)
                return false;
            return (bool)godotApp.Call("hasStoragePermission");
        }
        catch
        {
            return false;
        }
    }

    // Requests external storage permission. On Android 11+, opens the system
    // settings page. On older versions, shows the runtime permission dialog.
    public static void RequestStoragePermission()
    {
        try
        {
            var godotApp = GetGodotApp();
            godotApp?.Call("requestStoragePermission");
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"Failed to request storage permission: {ex.Message}");
        }
    }

    // Creates the external Mods and Saves directories if storage permission is granted.
    public static void EnsureExternalDirectories()
    {
        if (!HasStoragePermission())
            return;

        try
        {
            Directory.CreateDirectory(ExternalModsDir);
        }
        catch { }
        try
        {
            Directory.CreateDirectory(ExternalSaveBackupsDir);
        }
        catch { }
        try
        {
            Directory.CreateDirectory(ExternalLogsDir);
        }
        catch { }
        try
        {
            Directory.CreateDirectory(ExternalConfigDir);
        }
        catch { }
    }

    private static GodotObject GetGodotApp()
    {
        try
        {
            var jcw = Engine.GetSingleton("JavaClassWrapper");
            var wrapper = (GodotObject)
                jcw.Call("wrap", "com.game.sts2launcher.modmanager.GodotApp");
            return (GodotObject)wrapper.Call("getInstance");
        }
        catch
        {
            return null;
        }
    }
}
