using System;
using Godot;

namespace STS2Mobile.Launcher.Components;

// One row in the Mod Hub's SUBSCRIBED tab (issue #58 phase 4b). No toggle/reorder
// (that's the game's job now, see ModListRow) — just title/version/status and an
// UNSUBSCRIBE button that unsubscribes on Steam and removes the local install.
public class SubscribedModRow : PanelContainer
{
    public event Action UnsubscribePressed;
    public event Action DetailRequested;

    public SubscribedModRow(string title, string version, string status, bool statusIsError, float scale)
    {
        MouseFilter = MouseFilterEnum.Stop;

        var bg = new StyleBoxFlat();
        bg.BgColor = new Color(0.18f, 0.18f, 0.22f);
        bg.SetCornerRadiusAll((int)(4 * scale));
        bg.SetContentMarginAll((int)(8 * scale));
        AddThemeStyleboxOverride("panel", bg);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", (int)(8 * scale));
        row.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(row);

        var vbox = new VBoxContainer();
        vbox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        vbox.AddThemeConstantOverride("separation", (int)(2 * scale));
        row.AddChild(vbox);

        var titleText = string.IsNullOrWhiteSpace(version) ? title : $"{title} v{version}";
        var titleLabel = new StyledLabel(
            titleText,
            scale,
            fontSize: 14,
            align: HorizontalAlignment.Left
        );
        titleLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        vbox.AddChild(titleLabel);

        var statusLabel = new StyledLabel(status, scale, fontSize: 12, align: HorizontalAlignment.Left);
        statusLabel.AddThemeColorOverride(
            "font_color",
            statusIsError ? new Color(0.95f, 0.55f, 0.4f) : new Color(0.65f, 0.65f, 0.7f)
        );
        vbox.AddChild(statusLabel);

        var unsubButton = new StyledButton("UNSUBSCRIBE", scale, fontSize: 12, height: 36);
        unsubButton.Pressed += () => UnsubscribePressed?.Invoke();
        row.AddChild(unsubButton);

        // Tapping the row body (not the button) opens the detail page.
        GuiInput += ev =>
        {
            if (
                ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }
                or InputEventScreenTouch { Pressed: true }
            )
            {
                DetailRequested?.Invoke();
                AcceptEvent();
            }
        };
    }
}
