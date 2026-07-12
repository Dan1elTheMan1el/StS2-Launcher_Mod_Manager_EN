using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using HarmonyLib;

namespace STS2Mobile.Patches;

// issue #78 Part B: safety-net crash guard for the fmod-gdextension 6.1.0-4.5.0
// native singleton bundled in the APK (libGodotFmod.android...so).
//
// Root cause (confirmed from fmod-gdextension source, tag 6.1.0-4.5.0, commit
// fda1f89):
//   - FmodServer::_get_event_description(guid|path) (fmod_server.cpp:691-710)
//     returns a null Ref<FmodEventDescription> when the event isn't in the
//     bank cache, after logging "WARNING: Event {id} can't be found".
//   - FmodServer::_create_event_instance<T> (fmod_server.h:288-291) and
//     FmodServer::_play_one_shot<T> (fmod_server.h:304-308) both dereference
//     that Ref via fetch_event_description<T>(identifier)->get_wrapped()...
//     with NO null check -> this==nullptr member load -> SIGSEGV, always,
//     not conditionally.
//   - Real incident: mod RegentFX called
//     GodotObject.Call("create_event_instance_with_guid", "{e0f4...}") for an
//     event whose bank had failed to load -> instant app death.
//
// Safe pre-check exists and is already bound (fmod_server.cpp:40-41):
// check_event_guid / check_event_path, which only do a FmodCache hashmap
// .has() lookup (fmod_cache.cpp:162-168) - no dereference risk.
//
// Choke point: both mods and the game only reach the GDExtension singleton
// through Godot.GodotObject.Call(StringName, params Variant[]) - there is no
// other managed entry point - so we prefix that method, and for the 10
// vulnerable method names (guid-identified and path-identified variants of
// create_event_instance / play_one_shot), pre-verify existence and skip the
// native call when the event is missing instead of letting it crash.
//
// The "..._using_event_description" family (play_one_shot_using_event_description*,
// create_event_instance_from_description) is intentionally NOT guarded here:
// their first argument is an Object reference (a Ref<FmodEventDescription>
// obtained from a prior get_event/get_event_from_guid call), not a
// guid/path string, so check_event_guid/check_event_path cannot validate it -
// out of scope for this string-identifier guard.
public static class FmodEventGuardPatches
{
    private enum GuardKind
    {
        Guid,
        Path,
    }

    // Lazily built on first prefix invocation. Never touched from Apply():
    // constructing StringName instances marshals into native Godot code, and
    // Apply() runs from the gd_mono.cpp init context where such calls can
    // silently kill the whole patch chain.
    private static Dictionary<StringName, GuardKind> _guardedMethods;

    // FmodServer singleton identity, resolved once on first guarded hit.
    private static bool _singletonResolved;
    private static bool _singletonMissing;
    private static ulong _singletonInstanceId;

    // Existence cache: an event that is found once stays found for the
    // process lifetime (FmodCache only grows via bank loads; it is not
    // pruned mid-session in normal play), so we cache "true" results
    // permanently to fully eliminate repeated native round-trips on the hot
    // path (combat SFX replay the same event ids constantly - issue #15 perf
    // sensitivity). We deliberately do NOT cache "false" results: the
    // underlying check_event_* call is itself just an O(1) FmodCache hashmap
    // lookup (no dereference, no allocation), so re-checking a missing event
    // is still constant-time, and it lets a bank that finishes loading late
    // (async load_bank) self-heal instead of being permanently blacklisted
    // by a stale negative cache entry.
    private static readonly Dictionary<string, bool> _knownExisting = new();

    // Log noise control: warn at most once per distinct missing event key,
    // independent of the existence cache above.
    private static readonly HashSet<string> _loggedMissing = new();

    private static readonly object _lock = new();

    public static void Apply(Harmony harmony)
    {
        try
        {
            var target = AccessTools.Method(
                typeof(GodotObject),
                "Call",
                new[] { typeof(StringName), typeof(Variant[]) }
            );
            if (target == null)
            {
                PatchHelper.Log(
                    "FAILED GodotObject.Call(StringName, Variant[]): method not found"
                );
                return;
            }

            var prefix = typeof(FmodEventGuardPatches).GetMethod(
                nameof(CallPrefix),
                BindingFlags.NonPublic | BindingFlags.Static
            );
            var postfix = typeof(FmodEventGuardPatches).GetMethod(
                nameof(CallPostfix),
                BindingFlags.NonPublic | BindingFlags.Static
            );
            harmony.Patch(
                target,
                prefix: new HarmonyMethod(prefix),
                postfix: new HarmonyMethod(postfix)
            );
            PatchHelper.Log("Patched GodotObject.Call (Fmod event guard)");
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"FAILED GodotObject.Call (Fmod event guard): {ex.Message}");
        }
    }

    // Prefix on Godot.GodotObject.Call(StringName, params Variant[]). This is
    // the single managed choke point every GDExtension singleton call
    // (FmodServer included) goes through, from both mod code and game code.
    // It runs for EVERY Call() invocation across the whole app, so step 1
    // (the dictionary lookup) must stay allocation-free and O(1).
    private static bool CallPrefix(GodotObject __instance, StringName method, Variant[] args, ref Variant __result)
    {
        try
        {
            // Step 1: fast filter. StringName equality compares interned
            // native handles (see Godot.StringName.Equals), so this dictionary
            // lookup does not allocate or convert to a managed string.
            if (method is null)
                return true;

            var map = _guardedMethods;
            if (map == null)
            {
                lock (_lock)
                {
                    map = _guardedMethods ??= BuildGuardMap();
                }
            }

            if (!map.TryGetValue(method, out var kind))
                return true;

            // Step 2: confirm __instance is actually the FmodServer singleton
            // (guard names could theoretically collide with an unrelated
            // GDExtension class exposing the same method name).
            if (!IsFmodSingleton(__instance))
                return true;

            if (args == null || args.Length == 0)
                return true;

            var arg0 = args[0];
            if (arg0.VariantType != Variant.Type.String && arg0.VariantType != Variant.Type.StringName)
                return true; // not the shape we expect; let the original run.

            var identifier = arg0.AsString();
            if (string.IsNullOrEmpty(identifier))
                return true;

            // Step 3: existence pre-check via the safe check_event_* helpers,
            // cached so repeated calls for the same event never re-enter the
            // native lookup.
            var cacheKey = (kind == GuardKind.Guid ? "g:" : "p:") + identifier;
            bool exists;
            bool shouldLog;
            lock (_lock)
            {
                if (!_knownExisting.TryGetValue(cacheKey, out exists))
                {
                    var checkMethod = kind == GuardKind.Guid ? "check_event_guid" : "check_event_path";
                    // Re-enters this very prefix for "check_event_guid"/"check_event_path",
                    // which are not in _guardedMethods, so it falls through
                    // step 1 immediately - no recursion risk.
                    exists = __instance.Call(checkMethod, identifier).AsBool();
                    if (exists)
                        _knownExisting[cacheKey] = true;
                }

                shouldLog = !exists && _loggedMissing.Add(cacheKey);
            }

            if (exists)
                return true;

            if (shouldLog)
            {
                PatchHelper.Log(
                    $"[FmodGuard] blocked missing event ({(kind == GuardKind.Guid ? "guid" : "path")}={identifier}) via {method}"
                );
            }

            __result = default;
            return false;
        }
        catch
        {
            // We couldn't safely judge this call - never let the guard itself
            // take down normal sound playback.
            return true;
        }
    }

    // Postfix on the same Call(): record every bank a mod loads successfully by
    // itself, so FmodBankLoader's materialize-and-retry fallback doesn't re-load
    // it from a second path (FMOD rejects that with error 70, "bank already
    // loaded", which surfaces as a native push_error).
    private static void CallPostfix(GodotObject __instance, StringName method, Variant[] args, ref Variant __result)
    {
        try
        {
            if (method is null || args == null || args.Length == 0)
                return;

            // Lazy, for the same reason as _guardedMethods: constructing a
            // StringName marshals into native Godot, which must not happen from
            // Apply()'s gd_mono init context.
            var loadBank = _loadBankMethod ??= new StringName("load_bank");
            if (method != loadBank)
                return;

            if (!IsFmodSingleton(__instance))
                return;

            // load_bank returns the FmodBank object on success, Nil on failure.
            if (__result.VariantType == Variant.Type.Nil)
                return;

            var path = args[0].AsString();
            if (!string.IsNullOrEmpty(path))
                Fmod.FmodBankRegistry.RecordLoaded(path);
        }
        catch
        {
            // Recording is best-effort: worst case the fallback tries a
            // duplicate load and FMOD refuses it, which is what happened before.
        }
    }

    private static StringName _loadBankMethod;

    private static Dictionary<StringName, GuardKind> BuildGuardMap() =>
        new()
        {
            // guid-identified (fmod_server.cpp:104,107,110,116,122-125)
            ["create_event_instance_with_guid"] = GuardKind.Guid,
            ["play_one_shot_using_guid"] = GuardKind.Guid,
            ["play_one_shot_using_guid_with_params"] = GuardKind.Guid,
            ["play_one_shot_using_guid_attached"] = GuardKind.Guid,
            ["play_one_shot_using_guid_attached_with_params"] = GuardKind.Guid,

            // path-identified (fmod_server.cpp:105,108,111,117,126)
            ["create_event_instance"] = GuardKind.Path,
            ["play_one_shot"] = GuardKind.Path,
            ["play_one_shot_with_params"] = GuardKind.Path,
            ["play_one_shot_attached"] = GuardKind.Path,
            ["play_one_shot_attached_with_params"] = GuardKind.Path,
        };

    private static bool IsFmodSingleton(GodotObject instance)
    {
        if (instance == null)
            return false;

        if (!_singletonResolved)
        {
            lock (_lock)
            {
                if (!_singletonResolved)
                {
                    try
                    {
                        if (Engine.HasSingleton("FmodServer"))
                        {
                            var singleton = Engine.GetSingleton("FmodServer");
                            if (singleton != null)
                            {
                                _singletonInstanceId = singleton.GetInstanceId();
                            }
                            else
                            {
                                _singletonMissing = true;
                            }
                        }
                        else
                        {
                            _singletonMissing = true;
                        }
                    }
                    catch
                    {
                        _singletonMissing = true;
                    }
                    finally
                    {
                        _singletonResolved = true;
                    }
                }
            }
        }

        return !_singletonMissing && instance.GetInstanceId() == _singletonInstanceId;
    }
}
