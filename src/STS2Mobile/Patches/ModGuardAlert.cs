using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace STS2Mobile.Patches;

// In-game alert dialog for mod-guard attributions: "mod X threw an exception".
// Shown at most once per mod per session (the log keeps counting; the dialog
// must not nag). Everything is fail-safe — if the tree isn't ready or UI
// construction fails, the alert silently degrades to the log line that was
// already written.
//
// Mods never load in standalone-launcher mode, so a real attribution can only
// fire while the GAME scene tree is up; we still guard against LauncherUI
// being present (root-attached overlays freeze launcher input — issue #58)
// and go log-only in that case.
//
// Test trigger (QA only, no UI surface): drop a file at
//   /storage/emulated/0/StS2LauncherMM/.modguard_test_alert
// with content "ModName|ExceptionType" (both optional). A 2s watcher shows the
// exact production dialog and deletes the file, so repeated triggers work.
public static class ModGuardAlert
{
    private static readonly object _lock = new();
    private static readonly HashSet<string> _alertedMods = new();
    private static System.Threading.Timer _testWatcher;

    private const string TestTriggerFile = AppPaths.ExternalRoot + "/.modguard_test_alert";

    public static void ShowForMod(string modName, string exceptionType)
    {
        lock (_lock)
        {
            if (!_alertedMods.Add(modName))
                return;
        }
        Enqueue(modName, exceptionType);
    }

    public static void StartTestTriggerWatcher()
    {
        try
        {
            _testWatcher = new System.Threading.Timer(
                _ => PollTestTrigger(),
                null,
                dueTime: 2000,
                period: 2000
            );
            PatchHelper.Log("[ModGuard] Test-alert trigger watcher active");
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[ModGuard] Test watcher failed to start: {ex.Message}");
        }
    }

    private static void PollTestTrigger()
    {
        try
        {
            if (!File.Exists(TestTriggerFile))
                return;
            string content = "";
            try
            {
                content = File.ReadAllText(TestTriggerFile).Trim();
            }
            finally
            {
                File.Delete(TestTriggerFile);
            }
            var parts = content.Split('|');
            var mod = parts.Length > 0 && parts[0].Length > 0 ? parts[0] : "TestMod";
            var type = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : "TestException";
            PatchHelper.Log($"[ModGuard] Test alert triggered via file (mod='{mod}')");
            Enqueue(mod, type); // bypasses once-per-mod dedup on purpose
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[ModGuard] Test trigger poll error: {ex.Message}");
        }
    }

    // May be called from any thread (exception observers, timer) — marshal to
    // the main thread via Godot's deferred queue.
    private static void Enqueue(string modName, string exceptionType)
    {
        try
        {
            Callable.From(() => ShowOnMainThread(modName, exceptionType)).CallDeferred();
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[ModGuard] Alert enqueue failed: {ex.Message}");
        }
    }

    private static void ShowOnMainThread(string modName, string exceptionType)
    {
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree || tree.Root == null)
                return;
            foreach (var child in tree.Root.GetChildren())
            {
                if (child is STS2Mobile.Launcher.LauncherUI)
                {
                    PatchHelper.Log("[ModGuard] Launcher context — alert stays log-only");
                    return;
                }
            }

            var layer = new CanvasLayer { Layer = 100 };

            var dim = new ColorRect
            {
                Color = new Color(0, 0, 0, 0.55f),
                MouseFilter = Control.MouseFilterEnum.Stop,
            };
            dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            layer.AddChild(dim);

            var center = new CenterContainer();
            center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            center.MouseFilter = Control.MouseFilterEnum.Ignore;
            layer.AddChild(center);

            var panel = new PanelContainer();
            panel.AddThemeStyleboxOverride(
                "panel",
                new StyleBoxFlat
                {
                    BgColor = new Color(0.13f, 0.14f, 0.17f),
                    BorderColor = new Color(0.85f, 0.45f, 0.3f),
                    BorderWidthTop = 2,
                    BorderWidthBottom = 2,
                    BorderWidthLeft = 2,
                    BorderWidthRight = 2,
                    CornerRadiusTopLeft = 12,
                    CornerRadiusTopRight = 12,
                    CornerRadiusBottomLeft = 12,
                    CornerRadiusBottomRight = 12,
                    ContentMarginTop = 28,
                    ContentMarginBottom = 28,
                    ContentMarginLeft = 32,
                    ContentMarginRight = 32,
                }
            );
            center.AddChild(panel);

            var vbox = new VBoxContainer();
            vbox.AddThemeConstantOverride("separation", 18);
            panel.AddChild(vbox);

            var title = new Label { Text = "모드 오류 감지" };
            title.AddThemeFontSizeOverride("font_size", 34);
            title.AddThemeColorOverride("font_color", new Color(0.95f, 0.6f, 0.45f));
            vbox.AddChild(title);

            var body = new Label
            {
                Text =
                    $"'{modName}' 모드에서 오류가 발생했습니다.\n"
                    + $"({exceptionType})\n\n"
                    + "게임은 계속 진행할 수 있지만, 문제가 반복되면\n"
                    + "Mod Hub에서 해당 모드를 비활성화하세요.",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(680, 0),
            };
            body.AddThemeFontSizeOverride("font_size", 26);
            vbox.AddChild(body);

            var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
            var ok = new Button { Text = "확인", CustomMinimumSize = new Vector2(220, 68) };
            ok.AddThemeFontSizeOverride("font_size", 26);
            ok.Pressed += () => layer.QueueFree();
            row.AddChild(ok);
            vbox.AddChild(row);

            tree.Root.AddChild(layer);
            PatchHelper.Log($"[ModGuard] Alert shown for mod '{modName}'");
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[ModGuard] Alert UI failed (log-only fallback): {ex.Message}");
        }
    }
}
