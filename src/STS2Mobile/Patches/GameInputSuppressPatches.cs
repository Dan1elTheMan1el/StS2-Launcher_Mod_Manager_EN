using HarmonyLib;

namespace STS2Mobile.Patches;

// Suppresses the game's global unhandled-input handlers while the launcher UI is
// on screen (issue #58).
//
// Root cause, from device log 2026-07-11 11:51:33: Samsung's edge-swipe gesture
// injects KEYCODE_BACK as a raw KEY event (SGPController "action=key_back"),
// which does NOT arrive as Godot's WM_GO_BACK_REQUEST (the path LauncherUI
// handles). It fell through to the game's NInputManager._UnhandledKeyInput /
// NHotkeyManager._UnhandledInput, which ran game-side back/hotkey logic while
// the launcher owned the screen ("Dev console used before being created" spam,
// then the game tore down its node tree — taking the launcher, and the open Mod
// Hub, with it: "The window completely disappears").
//
// While LauncherUI.LauncherActive, both handlers are skipped entirely: no game
// hotkey/debug-key/back processing can run under the launcher. Normal behaviour
// resumes the moment the launcher hands off to the game.
public static class GameInputSuppressPatches
{
    public static void Apply(Harmony harmony)
    {
        var asm = typeof(MegaCrit.Sts2.Core.Nodes.NGame).Assembly;

        var inputManager = asm.GetType("MegaCrit.Sts2.Core.Nodes.CommonUi.NInputManager");
        if (inputManager != null)
        {
            PatchHelper.Patch(
                harmony,
                inputManager,
                "_UnhandledKeyInput",
                prefix: PatchHelper.Method(
                    typeof(GameInputSuppressPatches),
                    nameof(SkipWhileLauncherActive)
                )
            );
        }

        var hotkeyManager = asm.GetType("MegaCrit.Sts2.Core.Nodes.CommonUi.NHotkeyManager");
        if (hotkeyManager != null)
        {
            PatchHelper.Patch(
                harmony,
                hotkeyManager,
                "_UnhandledInput",
                prefix: PatchHelper.Method(
                    typeof(GameInputSuppressPatches),
                    nameof(SkipWhileLauncherActive)
                )
            );
        }

        // NGame.OnWindowChange NREs on EVERY window/scale change while the game
        // is still pre-run (device log 12:02:27 — fired by the Mod Hub rotation
        // toggle and by every multi-window resize). During a split-screen resize
        // storm that's a stream of half-executed window handlers right before the
        // 12:04 hard freeze — suppress it while the launcher owns the screen; the
        // next window change after PLAY runs it normally.
        PatchHelper.Patch(
            harmony,
            typeof(MegaCrit.Sts2.Core.Nodes.NGame),
            "OnWindowChange",
            prefix: PatchHelper.Method(
                typeof(GameInputSuppressPatches),
                nameof(SkipWhileLauncherActive)
            )
        );
    }

    // Harmony prefix: returning false skips the original method.
    public static bool SkipWhileLauncherActive() => !STS2Mobile.Launcher.LauncherUI.LauncherActive;
}
