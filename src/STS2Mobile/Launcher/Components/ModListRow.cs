using System;
using Godot;
using STS2Mobile.Modding;

namespace STS2Mobile.Launcher.Components;

// One row in the Mod Hub's LOCAL tab (issue #58 phase 4b). Enable/order are no
// longer surfaced here: activation now lives in the game's own Mods menu, and the
// launcher no longer manages load order — so the ON/OFF toggle and ▲/▼ reorder
// buttons that earlier revisions of this row had are gone. Shows the title, an
// expandable detail panel (description/readme/path/min-version warning), and a
// Remove button for launcher-managed mods (removable=true). Root-level "unmanaged"
// manifests — loaded by the game but with no containing folder the launcher can
// delete (ModScanner.WarnRootLevelManifests) — are rendered read-only instead via
// removable=false.
public class ModListRow : PanelContainer
{
    public event Action RemovePressed;

    public string ModId { get; }

    private readonly Button _infoButton;
    private readonly VBoxContainer _detail;

    public ModListRow(
        ModEntryInfo info,
        float scale,
        string versionWarning = null,
        bool removable = true,
        string badge = null
    )
    {
        ModId = info.Id;

        var bg = new StyleBoxFlat();
        bg.BgColor = new Color(0.18f, 0.18f, 0.22f);
        bg.SetCornerRadiusAll((int)(4 * scale));
        bg.SetContentMarginAll((int)(8 * scale));
        AddThemeStyleboxOverride("panel", bg);

        var outer = new VBoxContainer();
        outer.AddThemeConstantOverride("separation", (int)(6 * scale));
        AddChild(outer);

        var topRow = new HBoxContainer();
        topRow.AddThemeConstantOverride("separation", (int)(6 * scale));
        outer.AddChild(topRow);

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
        {
            var badgeLabel = new StyledLabel(badge, scale, fontSize: 10);
            badgeLabel.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.65f));
            topRow.AddChild(badgeLabel);
        }

        _infoButton = new StyledButton("ⓘ", scale, fontSize: 14, height: 36);
        _infoButton.CustomMinimumSize = new Vector2((int)(36 * scale), (int)(36 * scale));
        _infoButton.ToggleMode = true;
        _infoButton.Toggled += pressed => _detail.Visible = pressed;
        topRow.AddChild(_infoButton);

        _detail = new VBoxContainer();
        _detail.Visible = false;
        _detail.AddThemeConstantOverride("separation", (int)(4 * scale));
        outer.AddChild(_detail);

        if (!string.IsNullOrWhiteSpace(info.Manifest.Description))
        {
            var descLabel = new StyledLabel(info.Manifest.Description, scale, fontSize: 12);
            descLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            descLabel.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.85f));
            _detail.AddChild(descLabel);
        }

        if (!string.IsNullOrWhiteSpace(info.ReadmeSnippet))
        {
            var readmeLabel = new StyledLabel("README: " + info.ReadmeSnippet, scale, fontSize: 11);
            readmeLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            readmeLabel.AddThemeColorOverride("font_color", new Color(0.65f, 0.65f, 0.7f));
            _detail.AddChild(readmeLabel);
        }

        var pathLabel = new StyledLabel("Path: " + info.Path, scale, fontSize: 10);
        pathLabel.AutowrapMode = TextServer.AutowrapMode.Arbitrary;
        pathLabel.AddThemeColorOverride("font_color", new Color(0.5f, 0.5f, 0.55f));
        _detail.AddChild(pathLabel);

        if (!string.IsNullOrEmpty(versionWarning))
        {
            var warnLabel = new StyledLabel(versionWarning, scale, fontSize: 11);
            warnLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            warnLabel.AddThemeColorOverride("font_color", new Color(0.95f, 0.75f, 0.3f));
            _detail.AddChild(warnLabel);
        }

        if (removable)
        {
            var removeButton = new StyledButton("Remove Mod", scale, fontSize: 12, height: 36);
            var r = (int)(4 * scale);
            var bw = Math.Max(1, (int)(2 * scale));
            var dangerStyle = StyledButton.MakeOutline(new Color(0.85f, 0.3f, 0.3f), r, bw);
            removeButton.AddThemeStyleboxOverride("normal", dangerStyle);
            removeButton.AddThemeStyleboxOverride("hover", dangerStyle);
            removeButton.AddThemeStyleboxOverride("pressed", dangerStyle);
            removeButton.Pressed += () => RemovePressed?.Invoke();
            _detail.AddChild(removeButton);
        }
        else
        {
            var note = new StyledLabel(
                "Not managed by the launcher (no containing folder).",
                scale,
                fontSize: 11
            );
            note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            note.AddThemeColorOverride("font_color", new Color(0.55f, 0.65f, 0.85f));
            _detail.AddChild(note);
        }
    }

    private static string BuildTitle(ModEntryInfo info)
    {
        var name = info.Manifest.DisplayName;
        var version = string.IsNullOrWhiteSpace(info.Manifest.Version)
            ? ""
            : " v" + info.Manifest.Version;
        var author = string.IsNullOrWhiteSpace(info.Manifest.Author)
            ? ""
            : " — " + info.Manifest.Author;
        return name + version + author;
    }
}
