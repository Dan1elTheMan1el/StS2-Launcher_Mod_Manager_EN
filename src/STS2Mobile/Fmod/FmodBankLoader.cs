using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MegaCrit.Sts2.Core.Modding;

namespace STS2Mobile.Fmod;

// Issue #78 Part A: on Android, fmod-gdextension's FMOD file I/O runs on its own
// dedicated std::thread (GodotFileRunner, see file_callbacks.cpp) using Godot's
// FileAccess. The game's own banks live in app-internal storage; a mod's .pck
// sits on external storage (AppPaths.ExternalModsDir), and loading a bank out of
// a mod pck via that thread fails ("Cannot load bank res://<mod>/banks/x.bank",
// godot::FmodCache::add_bank) even though ordinary res:// resource loads from the
// very same pck succeed fine on the main thread. A missing bank silently leaves
// its events unregistered; when a mod later asks FMOD to play one of those
// events, the native GDExtension null-derefs and the process dies with no C#
// exception in between (see _workspace/crash-regentfx/02_fmod_gdextension_analysis.md
// for the fully-traced native crash chain — a separate patch adds a guard for
// that half of the problem).
//
// This class works around the loading half: after all mods have finished
// loading (see Patches/FmodBankPatches.cs), it finds every *.bank file inside
// every loaded mod's *.pck, copies its bytes out to app-internal storage
// (user://fmod_banks/), and asks the FmodServer singleton to load it from there
// instead — on the calling (main) thread, where res:// reads already work.
//
// Godot doesn't expose an API to list a mounted pck's contents, so we implement
// a minimal reader for the GDPC container format ourselves (path enumeration
// only — no decompression/decryption needed, since we hand the actual bytes off
// to Godot's own FileAccess to read). Verified against the real godot 4.5.1
// export pipeline this game uses: header is 104 bytes (magic, format version,
// godot major/minor/patch, pack flags, 8-byte file_base, 8-byte dir_base, 64
// bytes reserved), format version 3, directory located at dir_base (not
// necessarily immediately after the header). Confirmed against
// scripts/make-bootstrap-pck.py (this repo's own known-good GDPC writer) and,
// standalone (outside this assembly), against the real
// ".userfile/2026071112144121(crashwithmod)/Mods/RegentFX/RegentFX.pck" —
// correctly enumerated its 2021 entries including
// "RegentFX/banks/RegentFx.bank" and "RegentFX/banks/GUIDs.txt", plus two other
// unrelated mod pcks from the same bug report, all format-version-3.
public static class FmodBankLoader
{
    private const uint GdpcMagic = 0x43504447; // "GDPC" read as a little-endian uint32
    private const uint ExpectedFormatVersion = 3; // this game's Godot 4.5.x export pipeline

    private const string FmodServerSingletonName = "FmodServer";
    private const string LoadBankMethodName = "load_bank";
    private const int FmodStudioLoadBankNormal = 0;

    private const string BanksUserDir = "user://fmod_banks";

    // Dest user:// path -> already attempted to load_bank this session (whether it
    // succeeded or not doesn't matter for dedup purposes — we never retry within a
    // single launch).
    private static readonly HashSet<string> _handledBankUserPaths = new(
        StringComparer.OrdinalIgnoreCase
    );

    private readonly record struct PckEntry(string Path, ulong Size);

    // Entry point. Called once from FmodBankPatches' ModManager.Initialize
    // postfix, after every mod pck is mounted. Never throws.
    public static void LoadAllModBanks()
    {
        try
        {
            RunInternal();
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[FmodBank] Aborted: {ex.Message}");
        }
    }

    private static void RunInternal()
    {
        var fmodServer = TryGetFmodServer();
        if (fmodServer == null)
        {
            PatchHelper.Log(
                "[FmodBank] FmodServer singleton not present, skipping mod bank preload."
            );
            return;
        }

        List<Mod> mods;
        try
        {
            mods = ModManager.GetLoadedMods()?.ToList() ?? new List<Mod>();
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[FmodBank] ModManager.GetLoadedMods() failed: {ex.Message}");
            return;
        }

        int totalBanks = 0;
        int totalLoaded = 0;
        foreach (var mod in mods)
        {
            try
            {
                var (found, loaded) = ProcessMod(mod, fmodServer);
                totalBanks += found;
                totalLoaded += loaded;
            }
            catch (Exception ex)
            {
                PatchHelper.Log($"[FmodBank] {SafeModId(mod)}: FAILED ({ex.Message})");
            }
        }

        if (totalBanks > 0)
            PatchHelper.Log(
                $"[FmodBank] Done: {totalLoaded}/{totalBanks} mod bank(s) loaded across {mods.Count} mod(s)."
            );
    }

    // Returns (banksFound, banksLoaded) for one mod.
    private static (int found, int loaded) ProcessMod(Mod mod, Godot.GodotObject fmodServer)
    {
        var modId = SafeModId(mod);
        var modDir = mod?.path;
        if (string.IsNullOrEmpty(modDir) || !Directory.Exists(modDir))
            return (0, 0);

        // Defensive: on Android, ModLoaderPatches/ExternalModsFileIo redirects
        // every mod's directory under AppPaths.ExternalModsDir, so this should
        // always be true. If some future path (e.g. a real Steam Workshop dir on
        // desktop) ever hands us something outside that root, skip it rather than
        // walk arbitrary filesystem locations.
        if (!IsUnderExternalMods(modDir))
            return (0, 0);

        List<string> pckFiles;
        try
        {
            pckFiles = Directory
                .EnumerateFiles(modDir, "*.pck", SearchOption.AllDirectories)
                .ToList();
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[FmodBank] {modId}: pck scan failed ({ex.Message})");
            return (0, 0);
        }

        if (pckFiles.Count == 0)
            return (0, 0);

        int found = 0;
        int loaded = 0;
        foreach (var pck in pckFiles)
        {
            List<PckEntry> entries;
            try
            {
                entries = ListPckEntries(pck);
            }
            catch (Exception ex)
            {
                PatchHelper.Log(
                    $"[FmodBank] {modId}: GDPC parse failed for {Path.GetFileName(pck)} ({ex.Message})"
                );
                continue;
            }

            foreach (var entry in entries)
            {
                if (!entry.Path.EndsWith(".bank", StringComparison.OrdinalIgnoreCase))
                    continue;
                found++;
                if (LoadOneBank(modId, entry, fmodServer))
                    loaded++;
            }
        }

        if (found > 0)
            PatchHelper.Log($"[FmodBank] {modId}: found {found} bank(s) in {pckFiles.Count} pck");

        return (found, loaded);
    }

    // Materializes one bank to user:// storage (copy or reuse) and attempts to
    // load_bank it via each candidate path in order. Logs exactly one line for
    // this bank either way.
    private static bool LoadOneBank(string modId, PckEntry entry, Godot.GodotObject fmodServer)
    {
        var fileName = Path.GetFileName(entry.Path);
        if (string.IsNullOrEmpty(fileName))
            fileName = "unknown.bank";
        var destFileName = $"{modId}__{fileName}";
        var destUserPath = $"{BanksUserDir}/{destFileName}";

        if (!_handledBankUserPaths.Add(destUserPath))
        {
            // Two mod pcks producing the same dest name (same mod id + bank
            // filename) — extremely unlikely, but don't double-load.
            PatchHelper.Log($"[FmodBank] {modId}/{fileName}: SKIP (already handled this session)");
            return true;
        }

        if (!TryMaterializeBank(entry, destUserPath, out var materializeResult))
        {
            PatchHelper.Log($"[FmodBank] {modId}/{fileName}: FAILED to materialize ({materializeResult})");
            return false;
        }

        // Candidate (1): the virtual user:// path — works for ordinary Godot
        // resource access, may not resolve on FMOD's own file-I/O thread.
        if (TryLoadBankViaCandidate(fmodServer, destUserPath, out var reason1))
        {
            PatchHelper.Log(
                $"[FmodBank] {modId}/{fileName}: OK ({materializeResult}, load_bank via user://)"
            );
            return true;
        }

        // Candidate (2): the globalized absolute host path.
        var globalPath = Godot.ProjectSettings.GlobalizePath(destUserPath);
        if (TryLoadBankViaCandidate(fmodServer, globalPath, out var reason2))
        {
            PatchHelper.Log(
                $"[FmodBank] {modId}/{fileName}: OK ({materializeResult}, load_bank via globalized path)"
            );
            return true;
        }

        PatchHelper.Log(
            $"[FmodBank] {modId}/{fileName}: FAILED load_bank (user:// -> {reason1}; globalized -> {reason2})"
        );
        return false;
    }

    // Copies the bank's bytes out of the mounted mod pck (via res://, main
    // thread) into user://fmod_banks/, unless an identically-sized copy is
    // already there (avoids a re-copy on every launch).
    private static bool TryMaterializeBank(PckEntry entry, string destUserPath, out string result)
    {
        result = null;
        try
        {
            if (!Godot.DirAccess.DirExistsAbsolute(BanksUserDir))
            {
                var mkErr = Godot.DirAccess.MakeDirRecursiveAbsolute(BanksUserDir);
                if (mkErr != Godot.Error.Ok)
                {
                    result = $"mkdir failed: {mkErr}";
                    return false;
                }
            }

            if (Godot.FileAccess.FileExists(destUserPath))
            {
                using var existing = Godot.FileAccess.Open(
                    destUserPath,
                    Godot.FileAccess.ModeFlags.Read
                );
                if (existing != null && (ulong)existing.GetLength() == entry.Size)
                {
                    result = "reused";
                    return true;
                }
            }

            var resPath = "res://" + entry.Path;
            using var src = Godot.FileAccess.Open(resPath, Godot.FileAccess.ModeFlags.Read);
            if (src == null)
            {
                result = $"open {resPath} failed: {Godot.FileAccess.GetOpenError()}";
                return false;
            }
            var bytes = src.GetBuffer((long)entry.Size);
            src.Close();

            var tmpPath = destUserPath + ".tmp";
            using (var dst = Godot.FileAccess.Open(tmpPath, Godot.FileAccess.ModeFlags.Write))
            {
                if (dst == null)
                {
                    result = $"open {tmpPath} for write failed: {Godot.FileAccess.GetOpenError()}";
                    return false;
                }
                if (!dst.StoreBuffer(bytes))
                {
                    result = "StoreBuffer failed";
                    return false;
                }
            }

            var renameErr = Godot.DirAccess.RenameAbsolute(tmpPath, destUserPath);
            if (renameErr != Godot.Error.Ok && !Godot.FileAccess.FileExists(destUserPath))
            {
                result = $"rename failed: {renameErr}";
                return false;
            }

            result = "copied";
            return true;
        }
        catch (Exception ex)
        {
            result = ex.Message;
            return false;
        }
    }

    private static bool TryLoadBankViaCandidate(
        Godot.GodotObject fmodServer,
        string path,
        out string reason
    )
    {
        reason = null;
        try
        {
            var variant = fmodServer.Call(LoadBankMethodName, path, FmodStudioLoadBankNormal);
            var obj = variant.AsGodotObject();
            if (obj != null)
                return true;
            reason = "load_bank returned null/Nil";
            return false;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    private static Godot.GodotObject TryGetFmodServer()
    {
        try
        {
            if (!Godot.Engine.HasSingleton(FmodServerSingletonName))
                return null;
            return Godot.Engine.GetSingleton(FmodServerSingletonName);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsUnderExternalMods(string dir)
    {
        try
        {
            var full = Path.GetFullPath(dir).Replace('\\', '/').TrimEnd('/');
            var root = Path.GetFullPath(AppPaths.ExternalModsDir).Replace('\\', '/').TrimEnd('/');
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string SafeModId(Mod mod)
    {
        var id = mod?.manifest?.id;
        if (string.IsNullOrEmpty(id))
            return "unknown";
        var sb = new StringBuilder(id.Length);
        foreach (var c in id)
            sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
        return sb.ToString();
    }

    // Minimal GDPC (Godot resource pack) directory reader — path + size only, no
    // offset/md5/flags handling, since actual bytes are read back out through
    // Godot's own FileAccess via the mounted res:// path rather than parsed
    // directly out of the pck here.
    //
    // Header layout (104 bytes total, all little-endian), verified against
    // scripts/make-bootstrap-pck.py and real mod pcks from this game's Godot
    // 4.5.1 export pipeline:
    //   0   uint32 magic ("GDPC" = 0x43504447)
    //   4   uint32 format_version (3 for this engine build)
    //   8   uint32 godot_major
    //   12  uint32 godot_minor
    //   16  uint32 godot_patch
    //   20  uint32 pack_flags (bit 0x02 = PACK_REL_FILEBASE)
    //   24  uint64 file_base
    //   32  uint64 dir_base   -- directory section lives here, NOT necessarily
    //                            immediately after the header
    //   40  64 bytes reserved
    // Directory section (at dir_base):
    //   uint32 file_count, then per file:
    //     uint32 path_len (padded to 4 bytes) + path_len bytes (UTF8, NUL-padded)
    //     uint64 offset, uint64 size, 16 bytes md5, uint32 flags
    private static List<PckEntry> ListPckEntries(string pckPath)
    {
        var result = new List<PckEntry>();
        using var fs = new FileStream(pckPath, FileMode.Open, System.IO.FileAccess.Read, FileShare.Read);
        using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);

        if (fs.Length < 104)
            return result;

        var magic = br.ReadUInt32();
        if (magic != GdpcMagic)
            return result;

        var formatVersion = br.ReadUInt32();
        br.ReadUInt32(); // godot major, unused
        br.ReadUInt32(); // godot minor, unused
        br.ReadUInt32(); // godot patch, unused
        br.ReadUInt32(); // pack flags, unused (we don't touch raw bytes/offsets)
        br.ReadUInt64(); // file_base, unused
        var dirBase = br.ReadUInt64();

        if (formatVersion != ExpectedFormatVersion)
            return result;
        if (dirBase >= (ulong)fs.Length)
            return result;

        fs.Seek((long)dirBase, SeekOrigin.Begin);
        var fileCount = br.ReadUInt32();
        // Sanity cap — a legitimate mod pck won't have anywhere near this many
        // entries; bail rather than spin on a corrupt/foreign file.
        if (fileCount > 500_000)
            return result;

        for (uint i = 0; i < fileCount; i++)
        {
            if (fs.Position >= fs.Length)
                break;
            var pathLen = br.ReadUInt32();
            if (pathLen > 8192 || fs.Position + pathLen > fs.Length)
                break;
            var pathBytes = br.ReadBytes((int)pathLen);
            var path = Encoding.UTF8.GetString(pathBytes).TrimEnd('\0');
            br.ReadUInt64(); // offset, unused
            var size = br.ReadUInt64();
            br.ReadBytes(16); // md5, unused
            br.ReadUInt32(); // per-file flags, unused
            result.Add(new PckEntry(path, size));
        }

        return result;
    }
}
