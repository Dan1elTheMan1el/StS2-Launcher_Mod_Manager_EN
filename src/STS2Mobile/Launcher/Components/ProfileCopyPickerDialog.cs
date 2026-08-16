using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using STS2Mobile.Steam;

namespace STS2Mobile.Launcher.Components;

// Issue #64: slot picker for the profile-copy ("copy") flow — reused for both the
// source-slot step and the destination-slot step of ProfileCopyFlow.RunCopyAsync
// (different `title`/`hint`/pre-filtered `slots` each time; same class either
// way). Skeleton cloned from ProfilePickerDialog (same TCS Result pattern, same
// DialogSizing/compact-mode formula, same row style) rather than sharing code
// with it, since ProfilePickerDialog's Result type (SyncDecisionResult, a
// local-vs-cloud pair) and row content (sync-decision badge) don't fit a plain
// per-slot SaveProgressSummary (one side only).
public class ProfileCopyPickerDialog : ColorRect
{
    private readonly TaskCompletionSource<SaveProgressSummary> _result = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    public Task<SaveProgressSummary> Result => _result.Task;

    // Same struct/formula as ProfilePickerDialog.DialogSizing — see that file
    // for the rationale behind the "font size is too small" floors.
    private struct DialogSizing
    {
        public int TitleFs;
        public int HintFs;
        public int RowHeight;
        public int RowTitleFs;
        public int RowSubtitleFs;
        public int BadgeFs;
        public int CloseFs;
        public int CloseHeight;
    }

    private static DialogSizing ResolveSizing(float viewportHeight)
    {
        float d = Mathf.Clamp(viewportHeight / 1700f, 0.55f, 1.0f);
        int Px(int v) => Math.Max(1, (int)Math.Round(v * d));
        int PxFloor(int v, int floor) => Math.Max(floor, (int)Math.Round(v * d));
        return new DialogSizing
        {
            TitleFs = PxFloor(20, 16),
            HintFs = Px(12),
            RowHeight = PxFloor(60, StyledButton.MainActionHeight),
            RowTitleFs = PxFloor(15, StyledButton.MainActionFontSize),
            RowSubtitleFs = PxFloor(11, 12),
            BadgeFs = PxFloor(11, 12),
            CloseFs = PxFloor(14, StyledButton.MainActionFontSize),
            CloseHeight = PxFloor(44, StyledButton.MainActionHeight),
        };
    }

    public ProfileCopyPickerDialog(
        IReadOnlyList<SaveProgressSummary> slots,
        string title,
        string hint,
        float scale,
        float viewportHeight
    )
    {
        ModalGate.Register(this);

        var sz = ResolveSizing(viewportHeight);

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

        var titleLabel = new StyledLabel(title, scale, fontSize: sz.TitleFs);
        vbox.AddChild(titleLabel);

        if (!string.IsNullOrEmpty(hint))
        {
            var hintLabel = new StyledLabel(hint, scale, fontSize: sz.HintFs);
            hintLabel.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.75f));
            hintLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            hintLabel.CustomMinimumSize = new Vector2((int)(440 * scale), 0);
            vbox.AddChild(hintLabel);
        }

        var rows = new VBoxContainer();
        rows.AddThemeConstantOverride("separation", (int)(8 * scale));
        rows.CustomMinimumSize = new Vector2((int)(440 * scale), 0);
        vbox.AddChild(rows);

        foreach (var slot in slots)
            rows.AddChild(BuildRow(slot, scale, sz, Resolve));

        var buttonRow = new HBoxContainer();
        buttonRow.Alignment = BoxContainer.AlignmentMode.Center;
        vbox.AddChild(buttonRow);

        var closeButton = new StyledButton(
            "Close",
            scale,
            fontSize: sz.CloseFs,
            height: sz.CloseHeight
        );
        closeButton.CustomMinimumSize = new Vector2(
            (int)(120 * scale),
            closeButton.CustomMinimumSize.Y
        );
        closeButton.Pressed += () => Resolve(null);
        buttonRow.AddChild(closeButton);

        center.AddChild(dialogBox);
        AddChild(center);
    }

    private void Resolve(SaveProgressSummary slot)
    {
        QueueFree();
        _result.TrySetResult(slot);
    }

    private static Control BuildRow(
        SaveProgressSummary slot,
        float scale,
        DialogSizing sz,
        Action<SaveProgressSummary> onPick
    )
    {
        var row = new Button();
        row.Flat = true;
        row.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
        row.CustomMinimumSize = new Vector2(0, (int)(sz.RowHeight * scale));

        var r = (int)(6 * scale);
        row.AddThemeStyleboxOverride(
            "normal",
            StyledButton.MakeFilled(new Color(0.18f, 0.18f, 0.22f), r)
        );
        row.AddThemeStyleboxOverride(
            "hover",
            StyledButton.MakeFilled(new Color(0.22f, 0.22f, 0.27f), r)
        );
        row.AddThemeStyleboxOverride(
            "pressed",
            StyledButton.MakeFilled(new Color(0.15f, 0.15f, 0.18f), r)
        );
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

        var titleLabel = new StyledLabel(
            slot.ProfileLabel ?? "Profile",
            scale,
            fontSize: sz.RowTitleFs,
            align: HorizontalAlignment.Left
        );
        textCol.AddChild(titleLabel);

        var subtitleLabel = new StyledLabel(
            DescribeSlot(slot),
            scale,
            fontSize: sz.RowSubtitleFs,
            align: HorizontalAlignment.Left
        );
        subtitleLabel.Modulate = new Color(1, 1, 1, 0.55f);
        subtitleLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
        textCol.AddChild(subtitleLabel);

        // Same "at a glance" purpose as ProfilePickerDialog's decision badge —
        // here there's no local-vs-cloud decision to show, just whether this
        // slot has anything at all, which is exactly the distinction that
        // matters when picking a DESTINATION (overwrite-empty vs. overwrite-data).
        var (badgeText, badgeColor) = slot.IsEmpty
            ? ("Empty", new Color(0.6f, 0.6f, 0.6f))
            : ("Has Data", new Color(0.3f, 0.75f, 0.5f));
        var badge = new PanelContainer();
        var badgeStyle = new StyleBoxFlat();
        badgeStyle.BgColor = badgeColor;
        badgeStyle.SetCornerRadiusAll((int)(4 * scale));
        badgeStyle.SetContentMarginAll((int)(6 * scale));
        badge.AddThemeStyleboxOverride("panel", badgeStyle);
        badge.MouseFilter = Control.MouseFilterEnum.Ignore;
        badge.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        var badgeLabel = new StyledLabel(badgeText, scale, fontSize: sz.BadgeFs);
        badgeLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
        badge.AddChild(badgeLabel);
        hbox.AddChild(badge);

        return row;
    }

    // Mirrors ProfilePickerDialog.DescribeSlot but for a single SaveProgressSummary
    // (source/destination pick each show ONE side, not a local-vs-cloud pair).
    private static string DescribeSlot(SaveProgressSummary slot)
    {
        if (slot.IsEmpty)
            return "Empty";
        var parts = new List<string> { slot.FormatSize() };
        if (slot.ParseSucceeded)
            parts.Add(slot.FormatPlaytime());
        if (slot.HasCurrentRun)
            parts.Add(slot.FormatCurrentRun());
        return string.Join(" · ", parts);
    }
}
