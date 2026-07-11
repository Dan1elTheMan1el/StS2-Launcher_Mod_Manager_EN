using System;
using System.Collections.Generic;
using Godot;
using STS2Mobile.Patches;

namespace STS2Mobile.Launcher.Components;

// Registry of open modal layers (dialogs, busy overlay, detail page), stacked in
// open order. Two consumers:
//   • TouchScroll depth-gating — _Input/polling layers bypass normal GUI
//     occlusion (a dim ColorRect stops GUI events but not a poller), so lists
//     under an open modal need this explicit gate to ignore drags.
//   • Android Back — TryHandleBack closes the top-most modal FIRST, so Back
//     always dismisses detail pages/dialogs before any hub-close or app-exit
//     handling (user-requested priority: 모달 닫기 > 허브 닫기 > 앱 종료).
public static class ModalGate
{
    private class Entry
    {
        public Control Modal;
        public bool BackDismissable;
    }

    private static readonly List<Entry> _stack = new();

    public static int Depth => _stack.Count;

    // Call from the modal's constructor. The entry pops automatically when the
    // modal leaves the tree (button close, QueueFree, parent teardown).
    // backDismissable=false marks modals Back must NOT close (BusyOverlay guards
    // an in-flight operation) — Back is swallowed while they're up.
    public static void Register(Control modal, bool backDismissable = true)
    {
        var e = new Entry { Modal = modal, BackDismissable = backDismissable };
        _stack.Add(e);
        modal.TreeExiting += () => _stack.Remove(e);
    }

    // Android Back: closes the top-most valid modal. Returns true when the press
    // was consumed — including when the top modal is non-dismissable (busy), so
    // Back can never fall through to hub-close mid-operation.
    public static bool TryHandleBack()
    {
        for (int i = _stack.Count - 1; i >= 0; i--)
        {
            var e = _stack[i];
            if (!GodotObject.IsInstanceValid(e.Modal))
            {
                _stack.RemoveAt(i);
                continue;
            }
            if (e.BackDismissable)
                e.Modal.QueueFree();
            return true;
        }
        return false;
    }
}

// Android-style drag scrolling for a ScrollContainer (issue #58), implemented by
// POLLING the emulated mouse each frame — not by input events.
//
// History, so nobody re-walks this path:
//   1) Godot's built-in ScrollContainer touch scroll depends on a chain we don't
//      control inside the game's runtime (input emulation settings → GUI event
//      propagation through every card child → viewport mouse-focus capture).
//      On device it worked only intermittently — e.g. exactly one gesture right
//      after an orientation flip — which is what shipped builds kept showing.
//   2) A rewrite on Node._Input never ran AT ALL on device (zero takeover logs):
//      engine-virtual overrides are not reliably dispatched for these embedded
//      launcher classes. Every callback that provably works in this codebase is
//      SIGNAL-based (ProcessFrame +=, Pressed +=, SizeChanged +=, TreeExiting +=).
//
// So this implementation uses only device-proven primitives:
//   • SceneTree.ProcessFrame signal as the tick source
//   • Input.IsMouseButtonPressed + Viewport.GetMousePosition — valid because the
//     game runs with emulate_mouse_from_touch=true (confirmed in device log:
//     "[Mods] Input env: emulate_mouse_from_touch=true"), so the first finger
//     always drives the emulated mouse, in canvas coordinates.
//   • Control.PropagateNotification(SCROLL_BEGIN) to cancel a half-pressed
//     Button when a tap turns into a drag (same signal the engine's own
//     ScrollContainer uses).
//
// Gesture model (RecyclerView-like):
//   press inside list  → arm (GUI still sees the press, so taps click normally)
//   travel ≥ slop      → takeover: cancel button press, then drive
//                        ScrollVertical ABSOLUTELY from the finger offset —
//                        idempotent per frame, so even if the built-in scroll
//                        machinery occasionally fires it cannot fight us
//   release            → fling with decaying velocity; any new press stops it
public class TouchScroll : Node
{
    // Finger travel (canvas px) that turns a tap into a scroll. Canvas is
    // 1920x1080 at UI scale 2.0 → 20 px ≈ Android's standard 8dp touch slop.
    private const float TakeoverSlopPx = 20f;
    private const float FlingDecayPerSecond = 0.05f; // velocity ×0.05 per second
    private const float FlingStopSpeed = 60f; // canvas px/s

    private enum Phase
    {
        Idle,
        Armed,
        Scrolling,
        Fling,
    }

    private ScrollContainer _sc;
    private int _gateDepth;
    private bool _hooked;

    private Phase _phase = Phase.Idle;
    private bool _prevDown;
    private Vector2 _startPos;
    private int _startScroll;
    private float _lastY;
    private ulong _lastMsec;
    private float _velocity; // canvas px/s, positive = finger moving down
    private float _pending; // sub-pixel fling remainder

    public static void Attach(ScrollContainer sc)
    {
        var ts = new TouchScroll { _sc = sc, _gateDepth = ModalGate.Depth };
        // Subscribe BEFORE AddChild: if sc is already in the tree, TreeEntered
        // fires synchronously during AddChild and hooks the tick immediately.
        ts.TreeEntered += ts.Hook;
        ts.TreeExiting += ts.Unhook;
        sc.AddChild(ts);
    }

    private void Hook()
    {
        if (_hooked)
            return;
        var tree = GetTree();
        if (tree == null)
            return;
        tree.ProcessFrame += Tick;
        _hooked = true;
    }

    private void Unhook()
    {
        if (!_hooked)
            return;
        try
        {
            GetTree().ProcessFrame -= Tick;
        }
        catch
        {
            // tree already torn down
        }
        _hooked = false;
    }

    private void Tick()
    {
        if (_sc == null || !GodotObject.IsInstanceValid(_sc))
        {
            Unhook();
            return;
        }

        // A hidden list or a modal stacked above kills any gesture in progress.
        if (!_sc.IsVisibleInTree() || ModalGate.Depth != _gateDepth)
        {
            _phase = Phase.Idle;
            _prevDown = Input.IsMouseButtonPressed(MouseButton.Left);
            return;
        }

        bool down = Input.IsMouseButtonPressed(MouseButton.Left);
        var vp = _sc.GetViewport();
        if (vp == null)
        {
            _prevDown = down;
            return;
        }
        Vector2 pos = vp.GetMousePosition();
        ulong now = Time.GetTicksMsec();

        switch (_phase)
        {
            case Phase.Idle:
                if (down && !_prevDown && CanArm(pos))
                {
                    _phase = Phase.Armed;
                    _startPos = pos;
                    _startScroll = _sc.ScrollVertical;
                    _velocity = 0f;
                    _lastY = pos.Y;
                    _lastMsec = now;
                }
                break;

            case Phase.Armed:
                if (!down)
                {
                    _phase = Phase.Idle; // plain tap — the GUI handled the click
                    break;
                }
                UpdateVelocity(pos.Y, now);
                if ((pos - _startPos).Length() >= TakeoverSlopPx)
                {
                    _phase = Phase.Scrolling;
                    // Cancel the Button press under the finger — the same
                    // notification the engine's ScrollContainer uses.
                    _sc.PropagateNotification((int)Control.NotificationScrollBegin);
                    PatchHelper.Log("[Scroll] takeover(poll)");
                    ApplyAbsolute(pos.Y);
                }
                break;

            case Phase.Scrolling:
                UpdateVelocity(pos.Y, now);
                ApplyAbsolute(pos.Y);
                if (!down)
                {
                    _sc.PropagateNotification((int)Control.NotificationScrollEnd);
                    _pending = 0f;
                    _phase = Mathf.Abs(_velocity) > FlingStopSpeed ? Phase.Fling : Phase.Idle;
                    _lastMsec = now;
                }
                break;

            case Phase.Fling:
                if (down)
                {
                    // New touch stops the fling; if it's on the list it can arm a
                    // fresh gesture next frame.
                    _phase = Phase.Idle;
                    break;
                }
                float dt = Mathf.Max(0.001f, (now - _lastMsec) / 1000f);
                _lastMsec = now;
                int before = _sc.ScrollVertical;
                _pending += _velocity * dt;
                int step = (int)_pending;
                if (step != 0)
                {
                    _pending -= step;
                    _sc.ScrollVertical -= step;
                }
                _velocity *= Mathf.Pow(FlingDecayPerSecond, dt);
                // Stop when slow or pinned against an edge.
                if (
                    Mathf.Abs(_velocity) < FlingStopSpeed
                    || (step != 0 && _sc.ScrollVertical == before)
                )
                    _phase = Phase.Idle;
                break;
        }

        _prevDown = down;
    }

    private bool CanArm(Vector2 pos)
    {
        if (!_sc.GetGlobalRect().HasPoint(pos))
            return false;
        // Leave the scrollbar strip to the scrollbar itself — arming there would
        // double-drive the scroll while the user drags the grabber.
        var vb = _sc.GetVScrollBar();
        if (vb == null || vb.MaxValue <= vb.Page)
            return false; // nothing to scroll
        if (vb.IsVisibleInTree() && vb.GetGlobalRect().HasPoint(pos))
            return false;
        return true;
    }

    private void UpdateVelocity(float y, ulong now)
    {
        float dt = Mathf.Max(0.001f, (now - _lastMsec) / 1000f);
        float dy = y - _lastY;
        // EMA smooths Android's uneven inter-frame timing.
        _velocity = 0.75f * _velocity + 0.25f * (dy / dt);
        _lastY = y;
        _lastMsec = now;
    }

    // Finger offset drives the scroll position absolutely: same input → same
    // position, no drift, and no double-apply if anything else nudges the SC.
    private void ApplyAbsolute(float y) =>
        _sc.ScrollVertical = _startScroll - (int)(y - _startPos.Y);
}
