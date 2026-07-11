using System;
using Godot;
using STS2Mobile.Modding;

namespace STS2Mobile.Launcher.Components;

// One row in the Mod Hub's LOCAL tab (issue #58). Enable/order aren't surfaced
// here — activation lives in the game's own Mods menu and the launcher no longer
// manages load order. The row shows the title + an optional badge and a DETAIL
// button that raises DetailRequested so the pane can open a full ModDetailDialog
// (description/readme/path/version warning + Remove). Root-level "unmanaged"
// manifests are rendered the same way but the pane gives their dialog no Remove.
public class ModListRow : PanelContainer
{
    public event Action DetailRequested;

    public string ModId { get; }

    public ModListRow(ModEntryInfo info, float scale, string badge = null, bool compact = false)
    {
        ModId = info.Id;
        int btnFont = compact ? Ui.FontMicro : Ui.FontCaption;
        int btnHeight = compact ? 40 : 44;

        AddThemeStyleboxOverride("panel", Ui.CardStyle(scale));

        // No card-body tap handler: detail entry is the explicit DETAIL button so
        // the row body stays purely scrollable (a whole-card tap overlay here used
        // to eat the ScrollContainer's drag, so the LOCAL list neither scrolled nor
        // opened detail — user report).
        var topRow = new HBoxContainer();
        topRow.AddThemeConstantOverride("separation", (int)(6 * scale));
        AddChild(topRow);

        var titleLabel = new StyledLabel(
            BuildTitle(info),
            scale,
            fontSize: 14,
            align: HorizontalAlignment.Left
        );
        titleLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        titleLabel.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        titleLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        topRow.AddChild(titleLabel);

        if (!string.IsNullOrEmpty(badge))
            topRow.AddChild(Ui.MakePill(badge, scale, Ui.TextSecondary));

        var detailButton = new StyledButton(
            "DETAIL",
            scale,
            fontSize: btnFont,
            height: btnHeight,
            variant: ButtonVariant.Secondary
        );
        detailButton.CustomMinimumSize = new Vector2(0, (int)(btnHeight * scale));
        detailButton.Pressed += () => DetailRequested?.Invoke();
        topRow.AddChild(detailButton);
    }

    private static string BuildTitle(ModEntryInfo info)
    {
        var name = info.Manifest.DisplayName;
        var version = string.IsNullOrWhiteSpace(info.Manifest.Version)
            ? ""
            : " " + STS2Mobile.Launcher.LauncherModel.VersionLabel(info.Manifest.Version);
        var author = string.IsNullOrWhiteSpace(info.Manifest.Author)
            ? ""
            : " — " + info.Manifest.Author;
        return name + version + author;
    }
}
