using System;
using System.IO;
using System.Linq;
using MegaCrit.Sts2.Core.Modding;
using GodotFileAccess = Godot.FileAccess;

namespace STS2Mobile.Patches;

// Wraps the IModManagerFileIo the game hands ModManager.Initialize so any path
// pointing into the executable-adjacent "mods" directory is transparently
// redirected to AppPaths.ExternalModsDir (the launcher's external storage
// folder, e.g. /storage/emulated/0/StS2LauncherMM/Mods). All other paths are
// delegated to the original implementation so non-"mods" access (e.g. future
// steam/workshop probes) still works.
//
// This replaces the previous ldstr "mods" transpiler in ModLoaderPatches: as of
// sts2 v0.107.0, ModManager.Initialize is async (Task) so the compiler hoists
// the Path.Combine(..., "mods") call into a generated state-machine MoveNext
// and the main-body transpiler can no longer find the ldstr. Swapping the
// fileIo argument via prefix is signature-stable across that lowering.
// Not sealed: MakeDirRecursive/CopyFile below must be declared `virtual` (see
// comment there), and C# forbids new virtual members in a sealed class.
public class ExternalModsFileIo : IModManagerFileIo
{
    private readonly string _externalRoot;
    private readonly IModManagerFileIo _inner;

    public ExternalModsFileIo(string externalRoot, IModManagerFileIo inner)
    {
        _externalRoot = externalRoot ?? throw new ArgumentNullException(nameof(externalRoot));
        _inner = inner;
    }

    // Returns the rewritten external-storage path if `path` targets the game's
    // "mods" directory or anything beneath it; otherwise null (delegate to inner).
    // Matching is purely suffix-based on the final "mods" segment so we don't
    // care what the game's exe directory happens to be.
    private string TryRedirect(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        var normalized = path.Replace('\\', '/');
        const string needle = "/mods";
        var idx = normalized.LastIndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            // Bare "mods" with no parent separator — treat as the root itself.
            if (normalized.Equals("mods", StringComparison.OrdinalIgnoreCase))
                return _externalRoot;
            return null;
        }

        // Must be either the trailing segment or followed by a path separator,
        // so we don't accidentally redirect "/modsetting" or similar.
        var tail = normalized.Substring(idx + needle.Length);
        if (tail.Length > 0 && tail[0] != '/')
            return null;

        return tail.Length == 0 ? _externalRoot : _externalRoot + tail;
    }

    public string[] GetFilesAt(string path)
    {
        var redirected = TryRedirect(path);
        if (redirected == null)
            return _inner?.GetFilesAt(path) ?? Array.Empty<string>();
        try
        {
            return Directory.Exists(redirected)
                ? Directory.GetFiles(redirected)
                : Array.Empty<string>();
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Mods] GetFilesAt({redirected}) failed: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    public string[] GetDirectoriesAt(string path)
    {
        var redirected = TryRedirect(path);
        if (redirected == null)
            return _inner?.GetDirectoriesAt(path) ?? Array.Empty<string>();
        try
        {
            if (!Directory.Exists(redirected))
                return Array.Empty<string>();
            // Hide launcher staging dirs (".downloading" etc.) from the game's
            // recursive mod scan — without this filter, booting the game during a
            // Workshop download lets it load a half-written mod payload (issue #58).
            return Directory
                .EnumerateDirectories(redirected)
                .Where(d => !System.IO.Path.GetFileName(d).StartsWith("."))
                .ToArray();
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Mods] GetDirectoriesAt({redirected}) failed: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    public bool FileExists(string path)
    {
        var redirected = TryRedirect(path);
        if (redirected == null)
            return _inner != null && _inner.FileExists(path);
        try
        {
            return File.Exists(redirected);
        }
        catch
        {
            return false;
        }
    }

    public bool DirectoryExists(string path)
    {
        var redirected = TryRedirect(path);
        if (redirected == null)
            return _inner != null && _inner.DirectoryExists(path);
        try
        {
            return Directory.Exists(redirected);
        }
        catch
        {
            return false;
        }
    }

    public Stream OpenStream(string path, GodotFileAccess.ModeFlags mode)
    {
        var redirected = TryRedirect(path);
        if (redirected == null)
            return _inner?.OpenStream(path, mode);

        // Map Godot ModeFlags to standard FileMode/FileAccess. Godot's flags are
        // Read=1, Write=2, ReadWrite=3, WriteRead=7 — only Read/Write matter for
        // mod file ingestion (manifest read, asset open). Default to Read.
        FileMode fileMode;
        FileAccess fileAccess;
        switch (mode)
        {
            case GodotFileAccess.ModeFlags.Write:
                fileMode = FileMode.Create;
                fileAccess = FileAccess.Write;
                break;
            case GodotFileAccess.ModeFlags.ReadWrite:
                fileMode = FileMode.OpenOrCreate;
                fileAccess = FileAccess.ReadWrite;
                break;
            case GodotFileAccess.ModeFlags.WriteRead:
                fileMode = FileMode.Create;
                fileAccess = FileAccess.ReadWrite;
                break;
            case GodotFileAccess.ModeFlags.Read:
            default:
                fileMode = FileMode.Open;
                fileAccess = FileAccess.Read;
                break;
        }

        try
        {
            var dir = Path.GetDirectoryName(redirected);
            if (!string.IsNullOrEmpty(dir) && fileAccess != FileAccess.Read)
                Directory.CreateDirectory(dir);
            return new FileStream(redirected, fileMode, fileAccess, FileShare.Read);
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Mods] OpenStream({redirected}, {mode}) failed: {ex.Message}");
            return null;
        }
    }

    // sts2 v0.111.0 added MakeDirRecursive/CopyFile to IModManagerFileIo; without
    // them this class fails VTable setup under 0.111 and GameStartup dies (issue
    // #86). We compile against the pre-0.111 reference dll, so `_inner` cannot be
    // called for these — non-redirected paths instead mirror the game's own
    // ModManagerFileIo verbatim (DirAccess statics), which is what `_inner` is at
    // runtime. The game uses these for the first-time unmodded→modded save copy
    // (user:// paths, never redirected) and both members are simply unused on
    // pre-0.111 games, so this stays dual-version safe.
    //
    // MUST be `virtual`: the compile-time interface doesn't declare these, so the
    // compiler emits them as plain non-virtual methods — and the runtime can only
    // bind interface slots to virtual methods (ECMA-335), so VTable setup still
    // failed on device (code-337 QA, 2026-08-15) until the flag was forced. The
    // pre-existing five members get `virtual final newslot` from the compiler
    // automatically; these two need it spelled out.
    public virtual void MakeDirRecursive(string path)
    {
        var redirected = TryRedirect(path);
        if (redirected == null)
        {
            Godot.DirAccess.MakeDirRecursiveAbsolute(path);
            return;
        }
        try
        {
            Directory.CreateDirectory(redirected);
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Mods] MakeDirRecursive({redirected}) failed: {ex.Message}");
        }
    }

    public virtual Godot.Error CopyFile(string sourcePath, string destinationPath)
    {
        var src = TryRedirect(sourcePath);
        var dst = TryRedirect(destinationPath);
        if (src == null && dst == null)
            return Godot.DirAccess.CopyAbsolute(sourcePath, destinationPath);

        // At least one side lives in the external mods dir; a mixed copy with a
        // user:// path on the other side is not expected from any current caller.
        try
        {
            File.Copy(src ?? sourcePath, dst ?? destinationPath, overwrite: true);
            return Godot.Error.Ok;
        }
        catch (Exception ex)
        {
            PatchHelper.Log(
                $"[Mods] CopyFile({src ?? sourcePath} -> {dst ?? destinationPath}) failed: {ex.Message}"
            );
            return Godot.Error.Failed;
        }
    }
}
