using Godot;

namespace STS2Mobile.Launcher.Components;

// Full-screen input blocker shown while a Mod Hub operation runs (subscribe,
// unsubscribe, enable/disable, switch). Captures all touches so no second action
// can race the first (issue #58 — operations had no guard), with a small centered
// status card for feedback (Doherty). Shown via LauncherOverlay.Show; Dismiss()
// removes it.
public class BusyOverlay : ColorRect
{
    private readonly StyledLabel _label;

    public static BusyOverlay Show(Node context, string message, float scale)
    {
        var o = new BusyOverlay(message, scale);
        LauncherOverlay.Show(context, o);
        return o;
    }

    public BusyOverlay(string message, float scale)
    {
        // Gates TouchScroll polling that GUI blocking alone can't stop, and marks
        // itself NOT back-dismissable: Android Back is swallowed while an
        // operation is in flight instead of closing the hub underneath it.
        ModalGate.Register(this, backDismissable: false);

        SetAnchorsPreset(LayoutPreset.FullRect);
        Color = new Color(0, 0, 0, 0.5f);
        MouseFilter = MouseFilterEnum.Stop;

        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        center.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(center);

        var box = new PanelContainer();
        var style = new StyleBoxFlat { BgColor = Ui.SurfaceHigh };
        style.SetCornerRadiusAll((int)(Ui.RadiusL * scale));
        style.SetContentMarginAll((int)(20 * scale));
        box.AddThemeStyleboxOverride("panel", style);
        center.AddChild(box);

        _label = new StyledLabel(message, scale, fontSize: Ui.FontSection);
        _label.HorizontalAlignment = HorizontalAlignment.Center;
        _label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _label.CustomMinimumSize = new Vector2((int)(240 * scale), 0);
        box.AddChild(_label);
    }

    public void SetMessage(string message) => _label.Text = message;

    public void Dismiss()
    {
        if (IsInstanceValid(this))
            QueueFree();
    }
}
