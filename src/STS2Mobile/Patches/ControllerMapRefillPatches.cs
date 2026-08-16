using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;

namespace STS2Mobile.Patches;

// issue #85: NInputManager._controllerInputMap can stay permanently empty for
// an entire session, killing both controller action dispatch (_UnhandledInput
// iterates the map and does nothing when it's empty) and hotkey glyphs
// (GetHotkeyIcon's TryGetValue always misses, so every UI prompt falls back to
// the same placeholder icon -- reported as "every button shows A").
//
// Root cause (game bug, confirmed via decompile of both sts2.dll v0.108 and
// v0.111 -- see _workspace/issue85/00_triage.md): NInputManager.Init() is a
// fire-and-forget async method (TaskHelper.RunSafely) that reads
// SaveManager.Instance.SettingsSave before SaveManager finishes its own boot
// init. That NRE races on effectively every launch (harmless for touch play,
// where _controllerInputMap is never read) and aborts Init() before it reaches
// the tail statements that populate _controllerInputMap. The game's own
// self-heal, NInputManager.OnControllerTypeChanged(), only refills the map
// when ControllerManager.ControllerMappingType differs from the last-saved
// SettingsSave.ControllerMappingType -- so a player who has ever used this
// controller family before (mapping type already saved from a prior session)
// gets stuck with a permanently empty map; a first-time user only gets healed
// by luck on their first button press.
//
// OnControllerTypeChanged() is guaranteed to run at least once per session
// with a connected controller: GodotControllerInputStrategy.UpdateControllerConfig()
// unconditionally calls it the first time ProcessInput() sees a controller
// button, because its _currentControllerType field starts null (the "type
// changed" branch always fires once per fresh strategy instance). That makes
// it a safe, reliably-invoked sync postfix point -- unlike Init() itself,
// which is async and must not be patched directly (issue #47 lesson) -- to
// add an unconditional "still empty after the game's own logic ran? refill
// from defaults" safety net.
public static class ControllerMapRefillPatches
{
    private static FieldInfo _controllerInputMapField;
    private static bool _loggedRefillOnce;

    public static void Apply(Harmony harmony)
    {
        var asm = typeof(MegaCrit.Sts2.Core.Nodes.NGame).Assembly;

        var inputManagerType = asm.GetType("MegaCrit.Sts2.Core.Nodes.CommonUi.NInputManager");
        if (inputManagerType == null)
        {
            PatchHelper.Log("FAILED ControllerMapRefillPatches: NInputManager type not found");
            return;
        }

        _controllerInputMapField = AccessTools.Field(inputManagerType, "_controllerInputMap");
        if (_controllerInputMapField == null)
        {
            PatchHelper.Log(
                "FAILED ControllerMapRefillPatches: _controllerInputMap field not found"
            );
            return;
        }

        PatchHelper.Patch(
            harmony,
            inputManagerType,
            "OnControllerTypeChanged",
            postfix: PatchHelper.Method(typeof(ControllerMapRefillPatches), nameof(RefillIfEmpty))
        );
    }

    // Harmony postfix on NInputManager.OnControllerTypeChanged() (private void,
    // no args -- identical signature in v0.108 and v0.111). Runs after the
    // game's own conditional refill regardless of which branch it took inside,
    // so this only has to check the field's end state, not re-derive the
    // game's gating logic.
    public static void RefillIfEmpty(MegaCrit.Sts2.Core.Nodes.CommonUi.NInputManager __instance)
    {
        try
        {
            if (_controllerInputMapField == null)
                return;

            var map = _controllerInputMapField.GetValue(__instance) as IDictionary;
            if (map != null && map.Count > 0)
                return;

            var defaultMap = __instance.ControllerManager?.GetDefaultControllerInputMap;
            if (defaultMap == null || defaultMap.Count == 0)
                return;

            _controllerInputMapField.SetValue(__instance, defaultMap);

            // Cap to one log line ever: OnControllerTypeChanged can legitimately
            // fire again later (player swaps controller families mid-session),
            // and we don't want a broken-defaults edge case to spam every time.
            if (!_loggedRefillOnce)
            {
                _loggedRefillOnce = true;
                PatchHelper.Log($"[Issue85] controller map refilled ({defaultMap.Count} entries)");
            }
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Issue85] refill failed: {ex.Message}");
        }
    }
}
