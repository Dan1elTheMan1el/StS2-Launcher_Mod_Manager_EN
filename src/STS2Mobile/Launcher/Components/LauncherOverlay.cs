using Godot;

namespace STS2Mobile.Launcher.Components;

// Attaches a full-screen overlay (a modal dialog) to the launcher's top-level
// Control so it renders inside the launcher's ZIndex layer and receives input.
//
// Adding overlays to GetTree().Root instead put them in the game's window root,
// at a layer where the dim ColorRect blocked ALL touch input without delivering
// taps to its own buttons — a full UI freeze the user couldn't escape (issue #58).
// This mirrors how StyledDialog is parented to the launcher root.
public static class LauncherOverlay
{
    public static void Show(Node from, Control overlay)
    {
        Control top = null;
        for (Node n = from; n != null; n = n.GetParent())
        {
            if (n is Control c)
                top = c;
        }

        if (top != null)
            top.AddChild(overlay);
        else
            from.GetTree()?.Root?.AddChild(overlay);
    }
}
