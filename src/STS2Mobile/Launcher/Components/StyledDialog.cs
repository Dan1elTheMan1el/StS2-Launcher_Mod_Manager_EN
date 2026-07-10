using System;
using Godot;

namespace STS2Mobile.Launcher.Components;

// Modal confirmation dialog: dimmed overlay, centered panel, message, OK/Cancel.
// The message sits in a height-capped ScrollContainer and the buttons are pinned
// below it (outside the scroll), so a long message — e.g. "these 20 mods will be
// removed" — scrolls instead of pushing the buttons off-screen (issue #58). This
// fixed-buttons + scrolling-body pattern is the house rule for every dialog.
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

        // Height-capped scroll so a long message can't push the buttons away.
        var scroll = new ScrollContainer();
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        scroll.CustomMinimumSize = new Vector2((int)(320 * scale), 0);
        scroll.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        scroll.CustomMinimumSize = new Vector2((int)(320 * scale), (int)(0));
        vbox.AddChild(scroll);

        var label = new StyledLabel(message, scale, fontSize: 16);
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.CustomMinimumSize = new Vector2((int)(320 * scale), 0);
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(label);

        // Cap the scroll viewport once laid out, so short messages stay compact
        // and long ones scroll within ~60% of the screen height.
        scroll.Resized += () =>
        {
            var vpH = scroll.GetViewport()?.GetVisibleRect().Size.Y ?? (1080f);
            var cap = vpH * 0.6f;
            var natural = label.GetCombinedMinimumSize().Y;
            scroll.CustomMinimumSize = new Vector2(
                (int)(320 * scale),
                (int)Mathf.Min(cap, natural)
            );
        };

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
