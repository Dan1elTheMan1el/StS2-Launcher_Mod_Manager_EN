using System;
using System.Threading.Tasks;
using Godot;

namespace STS2Mobile.Launcher.Components;

// Issue #64 (D7): shown by LauncherPatches.OpenSaveSyncDialogAsync instead of a
// silent no-op when cloud sync is disabled, unauthenticated, or the cloud cache
// failed to load this session. Profile copy and backup restore are pure local
// filesystem operations, so both stay reachable with no cloud connection — this
// is a standalone entry menu (not a slot list, there's nothing to compare)
// offering exactly those two actions plus close, reusing PickerAction so the
// caller can share its branch logic with ProfilePickerDialog's two extra buttons.
public class LocalOnlyMenuDialog : ColorRect
{
    private readonly TaskCompletionSource<PickerAction> _result = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    public Task<PickerAction> Result => _result.Task;

    public LocalOnlyMenuDialog(float scale, float viewportHeight)
    {
        ModalGate.Register(this);

        // Same compact-mode formula as ProfilePickerDialog.ResolveSizing, inlined
        // here since this dialog only needs a handful of values.
        float d = Mathf.Clamp(viewportHeight / 1700f, 0.55f, 1.0f);
        int PxFloor(int v, int floor) => Math.Max(floor, (int)Math.Round(v * d));
        int titleFs = PxFloor(20, 16);
        int hintFs = Math.Max(1, (int)Math.Round(12 * d));
        int btnFs = PxFloor(14, StyledButton.MainActionFontSize);
        int btnHeight = PxFloor(44, StyledButton.MainActionHeight);

        SetAnchorsPreset(LayoutPreset.FullRect);
        Color = new Color(0, 0, 0, 0.7f);
        ZIndex = 200;

        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);

        var dialogBox = new PanelContainer();
        var boxStyle = new StyleBoxFlat();
        boxStyle.BgColor = new Color(0.13f, 0.13f, 0.16f);
        boxStyle.SetCornerRadiusAll((int)(10 * scale));
        boxStyle.SetContentMarginAll((int)(24 * scale));
        dialogBox.AddThemeStyleboxOverride("panel", boxStyle);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", (int)(14 * scale));
        dialogBox.AddChild(vbox);

        var title = new StyledLabel("Save Manager", scale, fontSize: titleFs);
        vbox.AddChild(title);

        var hint = new StyledLabel(
            "Cloud sync is disabled, so only local features are available.",
            scale,
            fontSize: hintFs
        );
        hint.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.75f));
        hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        hint.CustomMinimumSize = new Vector2((int)(360 * scale), 0);
        vbox.AddChild(hint);

        var buttonCol = new VBoxContainer();
        buttonCol.AddThemeConstantOverride("separation", (int)(8 * scale));
        vbox.AddChild(buttonCol);

        var copyButton = new StyledButton(
            "Copy Profile",
            scale,
            fontSize: btnFs,
            height: btnHeight
        );
        copyButton.Pressed += () => Resolve(PickerAction.Copy);
        buttonCol.AddChild(copyButton);

        var restoreButton = new StyledButton(
            "Restore Backup",
            scale,
            fontSize: btnFs,
            height: btnHeight
        );
        restoreButton.Pressed += () => Resolve(PickerAction.Restore);
        buttonCol.AddChild(restoreButton);

        var closeButton = new StyledButton(
            "Close",
            scale,
            fontSize: btnFs,
            height: btnHeight,
            variant: ButtonVariant.Ghost
        );
        closeButton.Pressed += () => Resolve(PickerAction.None);
        buttonCol.AddChild(closeButton);

        center.AddChild(dialogBox);
        AddChild(center);
    }

    private void Resolve(PickerAction action)
    {
        QueueFree();
        _result.TrySetResult(action);
    }
}
