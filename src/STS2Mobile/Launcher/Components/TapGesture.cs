using System;
using Godot;

namespace STS2Mobile.Launcher.Components;

// Makes a card/row tappable while keeping the parent ScrollContainer scrollable.
//
// The gui_input + drag-threshold approach didn't work on device: inside a
// ScrollContainer the touch-release is captured by the scroll logic, so a Pass
// control never sees the "up" and the tap never fires (issue #58 — WORKSHOP and
// LOCAL rows didn't open detail). Godot's Button already resolves tap-vs-scroll
// correctly (the scroll deadzone lets a drag scroll and a tap click), so we lay a
// transparent, full-area flat Button BEHIND the card's content: labels/containers
// use MouseFilter.Ignore so taps fall through to it, while the card's own action
// button (Subscribe/Disable/…) sits on top with MouseFilter.Stop and wins its area.
public static class TapGesture
{
    public static void Attach(Control control, Action onTap)
    {
        var btn = new Button
        {
            Flat = true,
            FocusMode = Control.FocusModeEnum.None,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        var empty = new StyleBoxEmpty();
        btn.AddThemeStyleboxOverride("normal", empty);
        btn.AddThemeStyleboxOverride("hover", empty);
        btn.AddThemeStyleboxOverride("pressed", empty);
        btn.AddThemeStyleboxOverride("focus", empty);
        btn.AddThemeStyleboxOverride("disabled", empty);
        btn.Pressed += onTap;

        control.AddChild(btn);
        // Render/behind the content that was already added by the caller.
        control.MoveChild(btn, 0);
    }
}
