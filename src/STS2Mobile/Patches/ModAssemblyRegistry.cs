using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace STS2Mobile.Patches;

// Tracks which loaded assemblies came from the external Mods/ tree, so the
// mod-guard patches (platform spoof, crash attribution) can answer "is this
// code a mod?". Observe-only and fail-safe: any error simply leaves an
// assembly untracked, which means "not a mod" and current behavior.
public static class ModAssemblyRegistry
{
    private static readonly object _lock = new();
    private static readonly HashSet<Assembly> _modAssemblies = new();

    public static void Install()
    {
        AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
        // Mods can't have loaded before the launcher's entry point, but sweep
        // anything already present so Install order can never matter.
        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                Track(asm);
        }
        catch { }
        PatchHelper.Log("[ModGuard] Mod assembly registry installed");
    }

    public static bool IsModAssembly(Assembly asm)
    {
        if (asm == null)
            return false;
        lock (_lock)
            return _modAssemblies.Contains(asm);
    }

    private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
    {
        try
        {
            Track(args.LoadedAssembly);
        }
        catch { }
    }

    private static void Track(Assembly asm)
    {
        if (asm == null || asm.IsDynamic)
            return;

        bool isMod = false;
        try
        {
            var loc = asm.Location;
            if (!string.IsNullOrEmpty(loc))
            {
                isMod = loc.Replace('\\', '/')
                    .StartsWith(AppPaths.ExternalModsDir + "/", StringComparison.Ordinal);
            }
            else
            {
                // Byte-loaded assemblies have no Location; fall back to "a dll of
                // this name exists somewhere under Mods/".
                var name = asm.GetName().Name;
                if (!string.IsNullOrEmpty(name) && Directory.Exists(AppPaths.ExternalModsDir))
                {
                    isMod =
                        Directory
                            .GetFiles(
                                AppPaths.ExternalModsDir,
                                name + ".dll",
                                SearchOption.AllDirectories
                            )
                            .Length > 0;
                }
            }
        }
        catch
        {
            return;
        }

        if (!isMod)
            return;

        lock (_lock)
        {
            if (!_modAssemblies.Add(asm))
                return;
        }
        PatchHelper.Log($"[ModGuard] Tracking mod assembly '{asm.GetName().Name}'");
    }
}
