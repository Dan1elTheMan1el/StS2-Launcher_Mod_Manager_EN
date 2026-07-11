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

    // Set when a button resolved the dialog. Any other teardown (Android Back via
    // ModalGate, parent teardown) counts as Cancel — without this, code awaiting
    // Confirmed/Cancelled (ConfirmAsync in the Workshop panes) would hang forever
    // when Back closes the dialog.
    private bool _resolved;

    public StyledDialog(
        string message,
        float scale,
        string okLabel = null,
        string cancelLabel = null
    )
    {
        okLabel ??= Loc.Tr("확인", "OK");
        cancelLabel ??= Loc.Tr("취소", "Cancel");

        // While this dialog is up, lists underneath must not react to drags, and
        // Android Back closes it (= Cancel) before anything else.
        ModalGate.Register(this);
        TreeExiting += () =>
        {
            if (!_resolved)
            {
                _resolved = true;
                Cancelled?.Invoke();
            }
        };

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

        var scroll = new ScrollContainer();
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        vbox.AddChild(scroll);
        TouchScroll.Attach(scroll);

        var label = new StyledLabel(message, scale, fontSize: 16);
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.CustomMinimumSize = new Vector2((int)(320 * scale), 0);
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        label.HorizontalAlignment = HorizontalAlignment.Center;
        scroll.AddChild(label);

        // Size the scroll to the message AFTER layout (deferred — a Resized handler
        // fired too early and collapsed the scroll, clipping the text). Capped at
        // 55% of the viewport so a long message scrolls and the buttons stay put.
        Callable
            .From(() =>
            {
                if (!IsInstanceValid(scroll) || !IsInstanceValid(label))
                    return;
                var vpH = scroll.GetViewport()?.GetVisibleRect().Size.Y ?? 1080f;
                var cap = vpH * 0.55f;
                var natural = label.GetCombinedMinimumSize().Y;
                scroll.CustomMinimumSize = new Vector2(
                    (int)(320 * scale),
                    (int)Mathf.Min(natural, cap)
                );
            })
            .CallDeferred();

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
            _resolved = true;
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
            _resolved = true;
            QueueFree();
            Confirmed?.Invoke();
        };
        buttonRow.AddChild(okButton);

        center.AddChild(dialogBox);
        AddChild(center);
    }
}
