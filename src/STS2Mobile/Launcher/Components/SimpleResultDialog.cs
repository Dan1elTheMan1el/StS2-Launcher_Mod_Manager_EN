using System;
using System.Threading.Tasks;
using Godot;

namespace STS2Mobile.Launcher.Components;

// Issue #64: generic OK-only outcome modal for the profile-copy / backup-restore
// flows. StyledDialog always shows Cancel+OK (wrong shape for "here's what
// happened, acknowledge and move on"); BackupResultDialog is shaped specifically
// around a backup's file-count/path breakdown. This is the freeform-message
// equivalent ProfileCopyFlow uses for every terminal step (copy/restore success
// or failure, cloud-reflect outcome, bypass warnings).
public class SimpleResultDialog : ColorRect
{
    public event Action Closed;

    // Awaitable convenience wrapper — mirrors the TCS-around-an-event pattern
    // LauncherController.ConfirmAsync uses for StyledDialog (LauncherController.cs
    // :795-807), just for the single Closed event instead of Confirmed/Cancelled.
    public static Task ShowAsync(Node parent, bool success, string message, float scale)
    {
        var tcs = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var dialog = new SimpleResultDialog(success, message, scale);
        dialog.Closed += () => tcs.TrySetResult(true);
        parent.AddChild(dialog);
        return tcs.Task;
    }

    public SimpleResultDialog(bool success, string message, float scale)
    {
        ModalGate.Register(this);

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

        var title = new StyledLabel(success ? "Complete" : "Failed", scale, fontSize: 20);
        vbox.AddChild(title);

        // Same fixed-buttons + scrolling-body house rule as StyledDialog — these
        // messages can be multi-line (cloud-reflect bypass warnings).
        var scroll = new ScrollContainer();
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        vbox.AddChild(scroll);
        TouchScroll.Attach(scroll);

        var label = new StyledLabel(message, scale, fontSize: 14, align: HorizontalAlignment.Left);
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.CustomMinimumSize = new Vector2((int)(340 * scale), 0);
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(label);

        // Deferred sizing pass — same reasoning as StyledDialog.cs:76-89 (a
        // Resized handler fired too early there and clipped the text).
        Callable
            .From(() =>
            {
                if (!IsInstanceValid(scroll) || !IsInstanceValid(label))
                    return;
                var vpH = scroll.GetViewport()?.GetVisibleRect().Size.Y ?? 1080f;
                var cap = vpH * 0.4f;
                var natural = label.GetCombinedMinimumSize().Y;
                scroll.CustomMinimumSize = new Vector2(
                    (int)(340 * scale),
                    (int)Mathf.Min(natural, cap)
                );
            })
            .CallDeferred();

        var buttonRow = new HBoxContainer();
        buttonRow.Alignment = BoxContainer.AlignmentMode.Center;
        vbox.AddChild(buttonRow);

        var okButton = new StyledButton("OK", scale, fontSize: 14, height: 44);
        okButton.CustomMinimumSize = new Vector2((int)(140 * scale), okButton.CustomMinimumSize.Y);
        okButton.Pressed += () =>
        {
            QueueFree();
            Closed?.Invoke();
        };
        buttonRow.AddChild(okButton);

        center.AddChild(dialogBox);
        AddChild(center);
    }
}
