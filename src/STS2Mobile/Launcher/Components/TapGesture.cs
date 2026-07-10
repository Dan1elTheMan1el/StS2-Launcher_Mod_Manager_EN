using System;
using Godot;

namespace STS2Mobile.Launcher.Components;

// Makes a Control tappable WITHOUT stealing drag scrolling from a parent
// ScrollContainer. The earlier approach (MouseFilter=Stop + AcceptEvent on every
// tap) consumed touch drags, so lists in the Mod Hub couldn't scroll (issue #58).
//
// This sets MouseFilter=Pass so events also reach the ScrollContainer, and never
// calls AcceptEvent, so a drag scrolls normally. A press-then-release that didn't
// move beyond a small threshold is treated as a tap and fires onTap; anything that
// moves is a drag and is ignored here (the ScrollContainer handles it).
public static class TapGesture
{
    private const float DragThresholdPx = 14f;

    public static void Attach(Control control, Action onTap)
    {
        control.MouseFilter = Control.MouseFilterEnum.Pass;

        var state = new State();
        control.GuiInput += ev => Handle(ev, state, onTap);
    }

    private sealed class State
    {
        public bool Tracking;
        public bool Dragged;
        public Vector2 DownPos;
    }

    private static void Handle(InputEvent ev, State s, Action onTap)
    {
        switch (ev)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } mb:
                if (mb.Pressed)
                {
                    s.Tracking = true;
                    s.Dragged = false;
                    s.DownPos = mb.Position;
                }
                else
                {
                    if (s.Tracking && !s.Dragged)
                        onTap();
                    s.Tracking = false;
                }
                break;

            case InputEventScreenTouch st:
                if (st.Pressed)
                {
                    s.Tracking = true;
                    s.Dragged = false;
                    s.DownPos = st.Position;
                }
                else
                {
                    if (s.Tracking && !s.Dragged)
                        onTap();
                    s.Tracking = false;
                }
                break;

            case InputEventMouseMotion mm:
                if (s.Tracking && mm.Position.DistanceTo(s.DownPos) > DragThresholdPx)
                    s.Dragged = true;
                break;

            case InputEventScreenDrag sd:
                if (s.Tracking && sd.Position.DistanceTo(s.DownPos) > DragThresholdPx)
                    s.Dragged = true;
                break;
        }
    }
}
