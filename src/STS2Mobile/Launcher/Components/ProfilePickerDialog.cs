using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using STS2Mobile.Steam;

namespace STS2Mobile.Launcher.Components;

// Save Manager entry screen. Lists every (profile × modded) slot that has
// local or cloud data as a tappable row, instead of collapsing everything
// into one aggregate summary. Fixes the "ghost slot hijacking" bug: a 1.5KB
// empty unmodded profile2 stub used to be picked by DetermineAsync's priority
// rules and hide a real 228KB modded profile1 conflict behind it — the user
// saw the wrong (empty) summary and had no way to reach the real one.
// Tapping a row resolves with that slot's SyncDecisionResult so the caller can
// show CloudConflictDialog for the details and apply KeepLocal/KeepCloud
// scoped to that single slot. Closing without picking a row resolves null.
public class ProfilePickerDialog : ColorRect
{
    private readonly TaskCompletionSource<SyncDecisionResult> _result = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    public Task<SyncDecisionResult> Result => _result.Task;

    public ProfilePickerDialog(IReadOnlyList<SyncDecisionResult> slots, float scale, float viewportHeight)
    {
        // Same compact-viewport handling as CloudConflictDialog so the list
        // still fits (and its rows stay tappable) on a folded Fold cover screen.
        float d = Mathf.Clamp(viewportHeight / 1700f, 0.55f, 1.0f);
        scale *= d;

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

        var title = new StyledLabel("Save Manager", scale, fontSize: 20);
        vbox.AddChild(title);

        var hint = new StyledLabel(
            "프로필별로 로컬과 Steam Cloud의 진행도를 확인하고 개별적으로 동기화할 수 있습니다.",
            scale,
            fontSize: 12
        );
        hint.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.75f));
        hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        hint.CustomMinimumSize = new Vector2((int)(440 * scale), 0);
        vbox.AddChild(hint);

        var rows = new VBoxContainer();
        rows.AddThemeConstantOverride("separation", (int)(8 * scale));
        rows.CustomMinimumSize = new Vector2((int)(440 * scale), 0);
        vbox.AddChild(rows);

        foreach (var slot in slots)
            rows.AddChild(BuildRow(slot, scale, Resolve));

        var buttonRow = new HBoxContainer();
        buttonRow.Alignment = BoxContainer.AlignmentMode.Center;
        vbox.AddChild(buttonRow);

        var closeButton = new StyledButton("닫기", scale, fontSize: 14, height: 44);
        closeButton.CustomMinimumSize = new Vector2(
            (int)(120 * scale),
            closeButton.CustomMinimumSize.Y
        );
        closeButton.Pressed += () => Resolve(null);
        buttonRow.AddChild(closeButton);

        center.AddChild(dialogBox);
        AddChild(center);
    }

    private void Resolve(SyncDecisionResult slot)
    {
        QueueFree();
        _result.TrySetResult(slot);
    }

    private static Control BuildRow(SyncDecisionResult slot, float scale, Action<SyncDecisionResult> onPick)
    {
        var row = new Button();
        row.Flat = true;
        row.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
        row.CustomMinimumSize = new Vector2(0, (int)(60 * scale));

        var r = (int)(6 * scale);
        row.AddThemeStyleboxOverride("normal", StyledButton.MakeFilled(new Color(0.18f, 0.18f, 0.22f), r));
        row.AddThemeStyleboxOverride("hover", StyledButton.MakeFilled(new Color(0.22f, 0.22f, 0.27f), r));
        row.AddThemeStyleboxOverride("pressed", StyledButton.MakeFilled(new Color(0.15f, 0.15f, 0.18f), r));
        row.Pressed += () => onPick(slot);

        var hbox = new HBoxContainer();
        hbox.SetAnchorsPreset(LayoutPreset.FullRect);
        hbox.AddThemeConstantOverride("separation", (int)(12 * scale));
        hbox.MouseFilter = Control.MouseFilterEnum.Ignore;
        row.AddChild(hbox);

        var textCol = new VBoxContainer();
        textCol.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        textCol.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        textCol.AddThemeConstantOverride("separation", (int)(2 * scale));
        textCol.MouseFilter = Control.MouseFilterEnum.Ignore;
        hbox.AddChild(textCol);

        // ProfileLabel is always non-null here — DeterminePerProfileAsync fills
        // a placeholder summary (with ProfileNumber/IsModded set) on whichever
        // side has no data, so the label reads correctly even for a
        // MobileOnly/CloudOnly slot.
        var titleLabel = new StyledLabel(
            slot.LocalSummary.ProfileLabel,
            scale,
            fontSize: 15,
            align: HorizontalAlignment.Left
        );
        textCol.AddChild(titleLabel);

        var subtitleLabel = new StyledLabel(
            DescribeSlot(slot),
            scale,
            fontSize: 11,
            align: HorizontalAlignment.Left
        );
        subtitleLabel.Modulate = new Color(1, 1, 1, 0.55f);
        subtitleLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
        textCol.AddChild(subtitleLabel);

        var (badgeText, badgeColor) = slot.Decision switch
        {
            SyncDecision.Identical => ("일치", new Color(0.3f, 0.75f, 0.5f)),
            SyncDecision.Conflict => ("충돌", new Color(0.85f, 0.4f, 0.3f)),
            SyncDecision.MobileOnly => ("로컬만", new Color(0.4f, 0.6f, 0.85f)),
            SyncDecision.CloudOnly => ("클라우드만", new Color(0.75f, 0.6f, 0.25f)),
            SyncDecision.Unverified => ("확인 불가", new Color(0.6f, 0.6f, 0.6f)),
            _ => ("—", new Color(0.6f, 0.6f, 0.6f)),
        };
        var badge = new PanelContainer();
        var badgeStyle = new StyleBoxFlat();
        badgeStyle.BgColor = badgeColor;
        badgeStyle.SetCornerRadiusAll((int)(4 * scale));
        badgeStyle.SetContentMarginAll((int)(6 * scale));
        badge.AddThemeStyleboxOverride("panel", badgeStyle);
        badge.MouseFilter = Control.MouseFilterEnum.Ignore;
        badge.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        var badgeLabel = new StyledLabel(badgeText, scale, fontSize: 11);
        badgeLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
        badge.AddChild(badgeLabel);
        hbox.AddChild(badge);

        return row;
    }

    // One-line "at a glance" summary so the user can spot a ghost slot (tiny
    // size, no playtime) versus a real conflict before even tapping in.
    private static string DescribeSlot(SyncDecisionResult slot)
    {
        return slot.Decision switch
        {
            SyncDecision.MobileOnly => $"로컬에만 있음 · {slot.LocalSummary.FormatSize()}",
            SyncDecision.CloudOnly => $"Cloud에만 있음 · {slot.CloudSummary.FormatSize()}",
            SyncDecision.Identical =>
                $"동기화됨 · {slot.LocalSummary.FormatSize()} · {slot.LocalSummary.FormatPlaytime()}",
            SyncDecision.Conflict =>
                $"로컬 {slot.LocalSummary.FormatSize()} vs Cloud {slot.CloudSummary.FormatSize()}",
            SyncDecision.Unverified => "일시적으로 클라우드를 확인하지 못함 · 다시 시도해 주세요",
            _ => "",
        };
    }
}
