using System;
using System.Reflection;
using System.Runtime.ExceptionServices;
using MegaCrit.Sts2.Core.Saves.Managers;

namespace STS2Mobile.Steam;

// Runtime reflection bridge for the game's static save-path helpers (issue #55,
// beta v0.108.0 / 2026-07-02, game commit 58694f64). That build added an
// optional trailing `bool? forceModState = null` parameter to three helpers:
//
//   ProgressSaveManager.GetProgressPathForProfile(int)
//       → GetProgressPathForProfile(int, bool? = null)
//   RunSaveManager.GetRunSavePath(int, string)
//       → GetRunSavePath(int, string, bool? = null)
//   PrefsSaveManager.GetPrefsPath(int)
//       → GetPrefsPath(int, bool? = null)
//   RunHistorySaveManager.GetHistoryPath(int)
//       → GetHistoryPath(int, bool? = null)
//
// C# default arguments are compile-time sugar: our IL — compiled against the
// ≤0.107 reference sts2.dll — emits a call to the OLD-arity signature, so on a
// v0.108 game the runtime throws MissingMethodException ("Method not found:
// string ...GetProgressPathForProfile(int)") the instant the calling method
// JITs. That ripples straight into the cloud-sync decision engine.
//
// The launcher must run against BOTH ≤0.107 and 0.108+ game builds (branch
// switching), so we can't just swap the reference DLL. Instead we resolve each
// helper's MethodInfo once — preferring the old exact signature, falling back
// to the new arity invoked with forceModState=null (semantically identical to
// the default) — cache it, and Invoke. Only the call is reflected; the game
// types are still reached via typeof (bound to the "sts2" assembly by name at
// runtime), so a signature change there surfaces as a compile error, not this.
public static class SavePathCompat
{
    // A resolved game helper plus whether it's the v0.108+ arity (in which case
    // we append forceModState=null to the invocation args).
    private sealed class Bridge
    {
        public MethodInfo Method;
        public bool NewOverload;
    }

    // Resolve-once caches. Lazy(ExecutionAndPublication) resolves under a lock,
    // logs the chosen overload exactly once, and caches a MissingMethodException
    // if neither arity is present so failures stay loud but don't spam the log.
    private static readonly Lazy<Bridge> _progressPath = new(() =>
        Resolve(
            typeof(ProgressSaveManager),
            "GetProgressPathForProfile",
            oldParams: new[] { typeof(int) },
            newParams: new[] { typeof(int), typeof(bool?) }
        )
    );

    private static readonly Lazy<Bridge> _runPath = new(() =>
        Resolve(
            typeof(RunSaveManager),
            "GetRunSavePath",
            oldParams: new[] { typeof(int), typeof(string) },
            newParams: new[] { typeof(int), typeof(string), typeof(bool?) }
        )
    );

    private static readonly Lazy<Bridge> _prefsPath = new(() =>
        Resolve(
            typeof(PrefsSaveManager),
            "GetPrefsPath",
            oldParams: new[] { typeof(int) },
            newParams: new[] { typeof(int), typeof(bool?) }
        )
    );

    private static readonly Lazy<Bridge> _historyPath = new(() =>
        Resolve(
            typeof(RunHistorySaveManager),
            "GetHistoryPath",
            oldParams: new[] { typeof(int) },
            newParams: new[] { typeof(int), typeof(bool?) }
        )
    );

    public static string GetProgressPathForProfile(int profileId)
    {
        var bridge = _progressPath.Value;
        var args = bridge.NewOverload
            ? new object[] { profileId, null }
            : new object[] { profileId };
        return Invoke(bridge, args);
    }

    public static string GetRunSavePath(int profileId, string fileName)
    {
        var bridge = _runPath.Value;
        var args = bridge.NewOverload
            ? new object[] { profileId, fileName, null }
            : new object[] { profileId, fileName };
        return Invoke(bridge, args);
    }

    public static string GetPrefsPath(int profileId)
    {
        var bridge = _prefsPath.Value;
        var args = bridge.NewOverload
            ? new object[] { profileId, null }
            : new object[] { profileId };
        return Invoke(bridge, args);
    }

    public static string GetHistoryPath(int profileId)
    {
        var bridge = _historyPath.Value;
        var args = bridge.NewOverload
            ? new object[] { profileId, null }
            : new object[] { profileId };
        return Invoke(bridge, args);
    }

    private static Bridge Resolve(Type type, string methodName, Type[] oldParams, Type[] newParams)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;

        // 1. Old exact signature — what our reference-DLL IL expects.
        var method = type.GetMethod(methodName, flags, binder: null, oldParams, modifiers: null);
        if (method != null)
        {
            PatchHelper.Log($"[Cloud] SavePathCompat: {methodName} → ≤v0.107 overload");
            return new Bridge { Method = method, NewOverload = false };
        }

        // 2. v0.108+ arity — trailing `bool? forceModState = null`; pass null.
        method = type.GetMethod(methodName, flags, binder: null, newParams, modifiers: null);
        if (method != null)
        {
            PatchHelper.Log($"[Cloud] SavePathCompat: {methodName} → v0.108+ overload");
            return new Bridge { Method = method, NewOverload = true };
        }

        // 3. Neither arity present — the game moved the goalposts again. Fail
        //    loudly so we notice, never silently return a wrong/empty path.
        var msg =
            $"[Cloud] SavePathCompat: no compatible overload for {type.FullName}.{methodName} "
            + $"(tried old arity {oldParams.Length}, new arity {newParams.Length})";
        PatchHelper.Log(msg);
        throw new MissingMethodException(msg);
    }

    private static string Invoke(Bridge bridge, object[] args)
    {
        try
        {
            return (string)bridge.Method.Invoke(null, args);
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            // Unwrap so callers' catch blocks log the game method's real
            // exception instead of the reflection wrapper. Preserves the
            // behaviour of the original direct call.
            ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw; // unreachable — Throw() above never returns.
        }
    }
}
