using Godot;

namespace STS2Mobile.Launcher.Components;

// Attaches a full-screen overlay (a modal dialog or a detail page) to the
// launcher's top-level Control so it renders inside the launcher's ZIndex layer
// and receives input.
//
// The target is the LauncherUI root specifically — the Control whose ZIndex=100
// owns the launcher's render+input layer, and the node LauncherView parents all
// its working dialogs to. Walking to the HIGHEST Control ancestor instead can
// land on a game Control ABOVE LauncherUI (a high canvas layer), where the
// overlay renders on top but receives NO touch input — a full modal freeze
// (issue #58, observed on device).
//
// Android-Back handling for overlays lives in ModalGate (each modal registers
// itself in its constructor); this class only does the parenting.
public static class LauncherOverlay
{
    public static void Show(Node from, Control overlay)
    {
        Control target = null;
        for (Node n = from; n != null; n = n.GetParent())
        {
            if (n is STS2Mobile.Launcher.LauncherUI launcherRoot)
            {
                target = launcherRoot;
                break;
            }
            if (target == null && n is Control c)
                target = c;
        }

        if (target != null)
            target.AddChild(overlay);
        else
            from.GetTree()?.Root?.AddChild(overlay);
    }
}
