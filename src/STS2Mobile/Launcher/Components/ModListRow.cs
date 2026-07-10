using System;
using Godot;
using STS2Mobile.Modding;

namespace STS2Mobile.Launcher.Components;

// One row in the Mod Hub's LOCAL tab (issue #58). Enable/order aren't surfaced
// here — activation lives in the game's own Mods menu and the launcher no longer
// manages load order. The row shows the title + an optional badge and, when
// tapped, raises DetailRequested so the pane can open a full ModDetailDialog
// (description/readme/path/version warning + Remove). Root-level "unmanaged"
// manifests are rendered the same way but the pane gives their dialog no Remove.
public class ModListRow : PanelContainer
{
    public event Action DetailRequested;

    public string ModId { get; }

    public ModListRow(ModEntryInfo info, float scale, string badge = null)
    {
        ModId = info.Id;

        AddThemeStyleboxOverride("panel", Ui.CardStyle(scale));

        var topRow = new HBoxContainer();
        topRow.AddThemeConstantOverride("separation", (int)(6 * scale));
        topRow.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(topRow);

        var titleLabel = new StyledLabel(
            BuildTitle(info),
            scale,
            fontSize: 14,
            align: HorizontalAlignment.Left
        );
        titleLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        titleLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        topRow.AddChild(titleLabel);

        if (!string.IsNullOrEmpty(badge))
            topRow.AddChild(Ui.MakePill(badge, scale, Ui.TextSecondary));

        // Chevron hint that the row opens a detail page.
        var chevron = new StyledLabel("›", scale, fontSize: 18);
        chevron.AddThemeColorOverride("font_color", Ui.TextDisabled);
        topRow.AddChild(chevron);

        // Tap opens the detail page; drags fall through to the ScrollContainer
        // (a Stop+AcceptEvent handler here was eating list scrolling).
        TapGesture.Attach(this, () => DetailRequested?.Invoke());
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
