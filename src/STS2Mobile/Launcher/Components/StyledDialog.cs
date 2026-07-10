using System;
using Godot;

namespace STS2Mobile.Launcher.Components;

// Modal confirmation dialog built from styled launcher components.
// Renders as a dimmed overlay with a centered panel, message, and OK/Cancel buttons.
public class StyledDialog : ColorRect
{
    public event Action Confirmed;
    public event Action Cancelled;

    public StyledDialog(
        string message,
        float scale,
        string okLabel = "OK",
        string cancelLabel = "Cancel"
    )
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        Color = new Color(0, 0, 0, 0.6f);

        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);

        var dialogBox = new PanelContainer();
        var boxStyle = new StyleBoxFlat();
        boxStyle.BgColor = Ui.SurfaceHigh;
        boxStyle.SetCornerRadiusAll((int)(Ui.RadiusL * scale));
        boxStyle.SetContentMarginAll((int)(24 * scale));
        dialogBox.AddThemeStyleboxOverride("panel", boxStyle);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", (int)(16 * scale));
        dialogBox.AddChild(vbox);

        var label = new StyledLabel(message, scale, fontSize: 16);
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.CustomMinimumSize = new Vector2((int)(300 * scale), 0);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        vbox.AddChild(label);

        var buttonRow = new HBoxContainer();
        buttonRow.AddThemeConstantOverride("separation", (int)(12 * scale));
        buttonRow.Alignment = BoxContainer.AlignmentMode.Center;
        vbox.AddChild(buttonRow);

        // Cancel is the quiet ghost, OK is the single emphasized action of the
        // dialog (Von Restorff) — and both are full-height touch targets.
        var cancelButton = new StyledButton(
            cancelLabel,
            scale,
            fontSize: 14,
            height: 52,
            variant: ButtonVariant.Ghost
        );
        cancelButton.CustomMinimumSize = new Vector2(
            (int)(140 * scale),
            cancelButton.CustomMinimumSize.Y
        );
        cancelButton.Pressed += () =>
        {
            QueueFree();
            Cancelled?.Invoke();
        };
        buttonRow.AddChild(cancelButton);

        var okButton = new StyledButton(
            okLabel,
            scale,
            fontSize: 14,
            height: 52,
            variant: ButtonVariant.Primary
        );
        okButton.CustomMinimumSize = new Vector2((int)(140 * scale), okButton.CustomMinimumSize.Y);
        okButton.Pressed += () =>
        {
            QueueFree();
            Confirmed?.Invoke();
        };
        buttonRow.AddChild(okButton);

        center.AddChild(dialogBox);
        AddChild(center);
    }
}
