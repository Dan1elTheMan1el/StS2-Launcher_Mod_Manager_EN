using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Godot;
using STS2Mobile.Patches;

namespace STS2Mobile.Launcher;

// Thin wrapper Control that initializes the MVC launcher components and
// processes a main-thread action queue so SteamKit callbacks can update the UI.
public class LauncherUI : Control
{
    // True while the launcher owns the screen. GameInputSuppressPatches skips the
    // game's global key/hotkey handlers while set, so an injected KEYCODE_BACK
    // (Samsung edge-swipe) can't trigger game-side back/debug logic under us.
    public static bool LauncherActive { get; private set; }

    // Set right before the PLAY handoff frees this node, so OnExitTree can tell a
    // planned teardown from the game unexpectedly removing the launcher (observed
    // during multi-window churn — the whole Mod Hub "disappeared").
    private bool _plannedTeardown;

    public void MarkPlannedTeardown() => _plannedTeardown = true;

    private readonly ConcurrentQueue<Action> _mainThreadQueue = new();
    private LauncherModel _model;
    private LauncherView _view;
    private LauncherController _controller;
    private bool _inGameMode;
    private bool _windowScaleOverridden;
    private Vector2I _origScaleSize;
    private Window.ContentScaleModeEnum _origScaleMode;
    private Window.ContentScaleAspectEnum _origScaleAspect;

    // Logical canvas the launcher targets. Window.ContentScale is pinned to
    // these dims with CanvasItems + Expand, so widget scale is computed from
    // the base size (always 2.0) instead of from the visible rect — the visible
    // rect grows along the wider physical axis under Expand and would otherwise
    // give a wildly different scale on fold/unfold/rotate.
    public const int LogicalWidth = 1920;
    public const int LogicalHeight = 1080;
    public const float UiScale = LogicalHeight / 540f; // 2.0

    public void Initialize()
    {
        ZIndex = 100;

        // NOTE: an earlier build raised gui/common/default_scroll_deadzone to 30 to
        // arbitrate a card-body tap overlay vs. scrolling. That overlay is gone
        // (detail entry is now an explicit DETAIL button), and the raised deadzone
        // was itself killing touch drag-to-scroll on the card body — only the
        // scrollbar worked (user report). Leaving the deadzone at the engine default
        // (0) restores the first-Mod-Hub-build behaviour where any body drag scrolls.

        // The game PCK's project.godot pins display/window/handheld/orientation to
        // landscape, which Godot applies at runtime and silently overrides the
        // activity's android:screenOrientation="sensorLandscape". Force sensor
        // landscape from C# so the user can flip the device 180° (USB-C charging
        // angle) and have the screen rotate.
        try
        {
            DisplayServer.ScreenSetOrientation(DisplayServer.ScreenOrientation.SensorLandscape);
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Launcher] Failed to set sensor landscape: {ex.Message}");
        }

        // Pin Window content scale to a fixed logical 1920×1080 with canvas_items +
        // expand. Godot then auto-stretches the entire UI tree to whatever physical
        // viewport the foldable / rotation / window resize produces, so widget
        // sizes computed once at construction (font, button height, padding) keep
        // looking right after fold/unfold without rebuilding the tree.
        // Original game-set values are stashed and restored in OnExitTree.
        try
        {
            var window = GetWindow();
            if (window != null)
            {
                _origScaleSize = window.ContentScaleSize;
                _origScaleMode = window.ContentScaleMode;
                _origScaleAspect = window.ContentScaleAspect;
                window.ContentScaleSize = new Vector2I(1920, 1080);
                window.ContentScaleMode = Window.ContentScaleModeEnum.CanvasItems;
                window.ContentScaleAspect = Window.ContentScaleAspectEnum.Expand;
                _windowScaleOverridden = true;
                PatchHelper.Log(
                    $"[Launcher] Window ContentScale overridden (orig size={_origScaleSize}, mode={_origScaleMode}, aspect={_origScaleAspect})"
                );
            }
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Launcher] Failed to set Window ContentScale: {ex.Message}");
        }

        try
        {
            // ContentScaleAspect.Expand makes GetVisibleRect grow along whatever
            // physical axis exceeds the project aspect, so don't compute scale
            // from it (would give different values on fold/unfold/rotate). Use
            // the base logical size — scale stays a stable 2.0.
            var vpSize =
                GetViewport()?.GetVisibleRect().Size ?? new Vector2(LogicalWidth, LogicalHeight);
            SetAnchorsPreset(LayoutPreset.FullRect);
            // Required because LauncherUI's parent is the game's gameNode (a
            // plain Node, not a Control), so anchors don't drive auto-sizing —
            // we have to set Size explicitly. Without this, every child Control
            // sees a 0×0 parent and the launcher collapses into the corner.
            // (Removed in v0.3.7, restored in v0.3.8 — that removal was the
            // observed top-left-collapse regression.)
            Size = vpSize;
            var scale = UiScale;

            _model = new LauncherModel(OS.GetDataDir());
            _model.InGameMode = _inGameMode;
            _view = new LauncherView(this, scale);
            _controller = new LauncherController(_model, _view, a => _mainThreadQueue.Enqueue(a));

            PatchHelper.Log($"LauncherUI initialized. Viewport={vpSize}");
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"BuildUI FAILED: {ex}");
            return;
        }

        LauncherPatches.CloudSyncEnabled = LauncherModel.LoadCloudSyncPref();

        // Prevent Android back button from quitting while the launcher is active.
        GetTree().AutoAcceptQuit = false;

        GetTree().ProcessFrame += OnProcessFrame;
        TreeExiting += OnExitTree;
        LauncherActive = true;
        _controller.Start();
    }

    public void SetGameMode(bool inGameMode) => _inGameMode = inGameMode;

    public Task WaitForLaunch() => _model.WaitForLaunch();

    // Mirrors the scale formula used in Initialize so other launcher-spawned
    // overlays (e.g. cloud conflict dialog) match the rest of the UI sizing.
    public static float ResolveScale(Node sceneRef)
    {
        // Pinned scale matches LauncherUI's UiScale so overlays sized off this
        // value (CloudConflictDialog etc.) stay visually consistent with the
        // launcher across fold/unfold/rotate.
        return UiScale;
    }

    // Used by overlays that need to know the actual viewport height (not the
    // scale) — e.g. CloudConflictDialog drops to compact font/padding when the
    // viewport is short, so foldable cover-screen / folded landscape doesn't
    // clip the buttons off the bottom.
    public static float ResolveViewportHeight(Node sceneRef)
    {
        try
        {
            return sceneRef?.GetViewport()?.GetVisibleRect().Size.Y ?? 1080f;
        }
        catch
        {
            return 1080f;
        }
    }

    // Heartbeat: one log line every 30 s from the engine main loop. During the
    // 12:04 hard freeze (device, split-screen) the app went silent with no
    // exception/ANR/tombstone — this line is the discriminator between "idle but
    // alive" and "main loop wedged" in the next capture.
    private ulong _lastHeartbeatMsec;

    private void OnProcessFrame()
    {
        var nowMsec = Time.GetTicksMsec();
        if (nowMsec - _lastHeartbeatMsec >= 30_000)
        {
            _lastHeartbeatMsec = nowMsec;
            PatchHelper.Log("[Launcher] hb");
        }

        while (_mainThreadQueue.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                PatchHelper.Log($"UI update error: {ex.Message}");
            }
        }

        _view?.UpdateKeyboardOffset();
    }

    private void OnExitTree()
    {
        // Every step is independently guarded: this ran with a half-torn-down
        // tree on device (v312 log: NRE inside OnExitTree during multi-window
        // churn) and an early throw must not skip the restore/dispose below.
        LauncherActive = false;
        PatchHelper.Log(
            _plannedTeardown
                ? "[Launcher] OnExitTree (planned PLAY handoff)"
                : "[Launcher] OnExitTree UNEXPECTED — launcher removed without PLAY "
                    + "(likely game-side teardown; see GameInputSuppressPatches)"
        );

        try
        {
            var tree = GetTree();
            if (tree != null)
            {
                tree.ProcessFrame -= OnProcessFrame;
                tree.AutoAcceptQuit = true;
            }
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Launcher] OnExitTree tree cleanup failed: {ex.Message}");
        }

        // Issue #38: the view's ctor hooked the root viewport's SizeChanged; the
        // viewport outlives this node, so disconnect here or every fold/unfold
        // during gameplay fires the handler against the disposed LauncherUI.
        try
        {
            _view?.DetachViewportHook();
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Launcher] OnExitTree viewport hook detach failed: {ex.Message}");
        }

        // Hand the Window's content scale back to whatever the game set so the
        // launcher exit doesn't break the game's own UI sizing.
        if (_windowScaleOverridden)
        {
            try
            {
                var window = GetWindow();
                if (window != null)
                {
                    window.ContentScaleSize = _origScaleSize;
                    window.ContentScaleMode = _origScaleMode;
                    window.ContentScaleAspect = _origScaleAspect;
                }
            }
            catch (Exception ex)
            {
                PatchHelper.Log($"[Launcher] Failed to restore Window ContentScale: {ex.Message}");
            }
            _windowScaleOverridden = false;
        }

        try
        {
            _model?.Dispose();
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Launcher] OnExitTree model dispose failed: {ex.Message}");
        }
    }

    public override void _Notification(int what)
    {
        if (what != NotificationWMGoBackRequest)
            return;

        // Android convention: if the soft keyboard is up, Back dismisses it first
        // rather than navigating. Without this, tapping Back while typing in the
        // Workshop search box fell straight through to the Mod-Hub-close handler,
        // yanking the user out of the Mod Hub mid-search.
        if (DisplayServer.VirtualKeyboardGetHeight() > 0)
        {
            GetViewport()?.GuiReleaseFocus();
            DisplayServer.VirtualKeyboardHide();
            return;
        }

        // Back priority (Android convention, user-requested): keyboard →
        // top-most modal (detail page / any dialog; a busy overlay swallows the
        // press) → Mod Hub close → only then whatever app-level double-back-exit
        // applies. ModalGate holds the stack; every modal registers itself.
        if (Launcher.Components.ModalGate.TryHandleBack())
            return;

        if (_controller is { IsModManagerOpen: true })
            _controller.OnModManagerBackPressed();
    }
}
