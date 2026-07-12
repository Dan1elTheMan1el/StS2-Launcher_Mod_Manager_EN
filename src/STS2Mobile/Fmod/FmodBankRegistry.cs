using System;
using System.Collections.Generic;

namespace STS2Mobile.Fmod;

// Tracks which FMOD banks a mod already loaded successfully through its own
// FmodServer.load_bank() call, so FmodBankLoader's materialize-and-retry
// fallback can skip them.
//
// Without this, the fallback re-loads a bank that is already in FMOD from a
// second path, which FMOD rejects with error 70 (EVENT_ALREADY_LOADED) and the
// GDExtension reports as a push_error — two noisy ERROR lines per bank per
// launch, plus a pointless 600KB copy.
//
// Banks are keyed by file name, not full path: the mod loads from
// res://<Mod>/banks/X.bank while the fallback would load the same bank from
// user://fmod_banks/<Mod>__X.bank, and FMOD dedupes on bank content either way.
public static class FmodBankRegistry
{
    private static readonly HashSet<string> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _lock = new();

    public static void RecordLoaded(string bankPath)
    {
        var name = FileName(bankPath);
        if (string.IsNullOrEmpty(name))
            return;

        lock (_lock)
        {
            _loaded.Add(name);
        }
    }

    public static bool IsLoaded(string bankFileName)
    {
        var name = FileName(bankFileName);
        if (string.IsNullOrEmpty(name))
            return false;

        lock (_lock)
        {
            return _loaded.Contains(name);
        }
    }

    private static string FileName(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        var slash = path.LastIndexOfAny(new[] { '/', '\\' });
        return slash >= 0 ? path.Substring(slash + 1) : path;
    }
}
