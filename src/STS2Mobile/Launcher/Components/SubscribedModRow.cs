using System;
using Godot;

namespace STS2Mobile.Launcher.Components;

// One row in the Mod Hub's SUBSCRIBED tab (issue #58 phase 4b). No toggle/reorder
// (that's the game's job now, see ModListRow) — just title/version/status and an
// UNSUBSCRIBE button that unsubscribes on Steam and removes the local install.
public class SubscribedModRow : PanelContainer
{
    public event Action UnsubscribePressed;
    public event Action DetailRequested;

    public SubscribedModRow(string title, string version, string status, bool statusIsError, float scale)
    {
        AddThemeStyleboxOverride("panel", Ui.CardStyle(scale));

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", (int)(8 * scale));
        row.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(row);

        var vbox = new VBoxContainer();
        vbox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        vbox.AddThemeConstantOverride("separation", (int)(2 * scale));
        row.AddChild(vbox);

        var titleText = string.IsNullOrWhiteSpace(version)
            ? title
            : $"{title} {STS2Mobile.Launcher.LauncherModel.VersionLabel(version)}";
        var titleLabel = new StyledLabel(
            titleText,
            scale,
            fontSize: 14,
            align: HorizontalAlignment.Left
        );
        titleLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        vbox.AddChild(titleLabel);

        var statusLabel = new StyledLabel(status, scale, fontSize: Ui.FontCaption, align: HorizontalAlignment.Left);
        statusLabel.AddThemeColorOverride(
            "font_color",
            statusIsError ? Ui.Danger
            : status == "Installed" ? Ui.Success
            : Ui.TextSecondary
        );
        vbox.AddChild(statusLabel);

        var unsubButton = new StyledButton(
            "UNSUBSCRIBE",
            scale,
            fontSize: Ui.FontCaption,
            height: 44,
            variant: ButtonVariant.Danger
        );
        unsubButton.CustomMinimumSize = new Vector2((int)(150 * scale), (int)(44 * scale));
        unsubButton.Pressed += () => UnsubscribePressed?.Invoke();
        row.AddChild(unsubButton);

        // Tapping the row body (not the button) opens the detail page; drags
        // fall through to the ScrollContainer so the list stays scrollable.
        TapGesture.Attach(this, () => DetailRequested?.Invoke());
    }
}
