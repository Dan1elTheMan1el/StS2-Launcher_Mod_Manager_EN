using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Godot;
using STS2Mobile.Launcher;
using STS2Mobile.Steam;

namespace STS2Mobile.Launcher.Components;

// Issue #64: snapshot picker for the "백업 복원" flow. Unlike ProfileCopyPickerDialog
// (bounded at 6 rows), manual backup sets are never auto-evicted (LocalBackupService's
// FIFO cap only prunes auto/ — see LocalBackupService.cs "Manual sets are never
// auto-evicted"), so this list can grow without bound. Wrapped in a ScrollContainer
// + TouchScroll (StyledDialog's scrolling-body house rule) instead of the plain
// unbounded VBoxContainer ProfilePickerDialog/ProfileCopyPickerDialog use for their
// fixed 6-row-or-fewer lists.
public class BackupRestorePickerDialog : ColorRect
{
    private readonly TaskCompletionSource<LocalBackupService.SnapshotInfo> _result = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    public Task<LocalBackupService.SnapshotInfo> Result => _result.Task;

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
        public int ScrollHeight;
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
            RowHeight = PxFloor(56, StyledButton.MainActionHeight),
            RowTitleFs = PxFloor(14, StyledButton.MainActionFontSize),
            RowSubtitleFs = PxFloor(11, 12),
            BadgeFs = PxFloor(11, 12),
            CloseFs = PxFloor(14, StyledButton.MainActionFontSize),
            CloseHeight = PxFloor(44, StyledButton.MainActionHeight),
            ScrollHeight = Px(360),
        };
    }

    public BackupRestorePickerDialog(
        IReadOnlyList<LocalBackupService.SnapshotInfo> snapshots,
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

        var title = new StyledLabel("Restore Backup", scale, fontSize: sz.TitleFs);
        vbox.AddChild(title);

        var hint = new StyledLabel(
            "Select a local backup point to restore. The current state will be automatically backed up right before restoration.",
            scale,
            fontSize: sz.HintFs
        );
        hint.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.75f));
        hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        hint.CustomMinimumSize = new Vector2((int)(440 * scale), 0);
        vbox.AddChild(hint);

        var scroll = new ScrollContainer();
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        scroll.CustomMinimumSize = new Vector2((int)(440 * scale), sz.ScrollHeight * scale);
        vbox.AddChild(scroll);
        TouchScroll.Attach(scroll);

        var rows = new VBoxContainer();
        rows.AddThemeConstantOverride("separation", (int)(8 * scale));
        rows.CustomMinimumSize = new Vector2((int)(440 * scale), 0);
        scroll.AddChild(rows);

        foreach (var snap in snapshots)
            rows.AddChild(BuildRow(snap, scale, sz, Resolve));

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

    private void Resolve(LocalBackupService.SnapshotInfo snap)
    {
        QueueFree();
        _result.TrySetResult(snap);
    }

    private static Control BuildRow(
        LocalBackupService.SnapshotInfo snap,
        float scale,
        DialogSizing sz,
        Action<LocalBackupService.SnapshotInfo> onPick
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
        row.Pressed += () => onPick(snap);

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
            FormatTimestamp(snap.Timestamp),
            scale,
            fontSize: sz.RowTitleFs,
            align: HorizontalAlignment.Left
        );
        textCol.AddChild(titleLabel);

        var subtitleLabel = new StyledLabel(
            $"{snap.FileCount} files · {LauncherModel.FormatSize(snap.TotalBytes)}",
            scale,
            fontSize: sz.RowSubtitleFs,
            align: HorizontalAlignment.Left
        );
        subtitleLabel.Modulate = new Color(1, 1, 1, 0.55f);
        subtitleLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
        textCol.AddChild(subtitleLabel);

        var (badgeText, badgeColor) = DescribeKind(snap.Kind);
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

    // yyyyMMdd_HHmmss (folder-name convention, LocalBackupService.MakeTimestamp)
    // -> a readable local string. Falls back to the raw value if parsing fails —
    // a malformed/unexpected folder name must still produce a pickable row.
    private static string FormatTimestamp(string raw)
    {
        if (
            DateTime.TryParseExact(
                raw,
                "yyyyMMdd_HHmmss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var dt
            )
        )
            return dt.ToString("yyyy-MM-dd HH:mm:ss");
        return raw ?? "—";
    }

    private static (string text, Color color) DescribeKind(string kind) =>
        kind switch
        {
            "manual" => ("Manual", new Color(0.4f, 0.6f, 0.85f)),
            "auto-match" => ("Auto · Match", new Color(0.3f, 0.75f, 0.5f)),
            "auto-conflict-kept" => ("Auto · Conflict (Kept)", new Color(0.3f, 0.75f, 0.5f)),
            "auto-conflict-discarded" => (
                "Auto · Conflict (Discarded)",
                new Color(0.6f, 0.6f, 0.6f)
            ),
            _ => (kind ?? "—", new Color(0.6f, 0.6f, 0.6f)),
        };
}
