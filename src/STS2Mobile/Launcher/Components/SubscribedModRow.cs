using System;
using Godot;

namespace STS2Mobile.Launcher.Components;

// One row in the Mod Hub's SUBSCRIBED tab (issue #58). Title/status plus up to
// three actions with fixed semantics-to-color mapping:
//   ENABLE  (accent outline) — restore from the stash, file move only
//   DISABLE (secondary)      — move to the stash, non-destructive
//   UNSUBSCRIBE (danger)     — deletes files; confirm-gated by the pane
// A stashed row renders dimmed so the inactive state reads at a glance.
public class SubscribedModRow : PanelContainer
{
    public event Action UnsubscribePressed;
    public event Action ToggleStashPressed; // fires for both ENABLE and DISABLE
    public event Action DetailRequested;

    public SubscribedModRow(
        string title,
        string version,
        string status,
        Color statusColor,
        float scale,
        bool disabled = false,
        bool showStashToggle = false,
        bool compact = false
    )
    {
        // compact = portrait layout: smaller buttons so the text column keeps width.
        int btnFont = compact ? Ui.FontMicro : Ui.FontCaption;
        int btnHeight = compact ? 40 : 44;
        AddThemeStyleboxOverride(
            "panel",
            disabled ? Ui.CardStyle(scale, Ui.CardDown) : Ui.CardStyle(scale)
        );

        // No card-body tap handler: detail entry is the explicit DETAIL button
        // below, so the row body stays purely scrollable (a tap overlay here used
        // to eat the ScrollContainer's drag — user report).
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", (int)(8 * scale));
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
        if (disabled)
            titleLabel.AddThemeColorOverride("font_color", Ui.TextDisabled);
        vbox.AddChild(titleLabel);

        var statusLabel = new StyledLabel(
            status,
            scale,
            fontSize: Ui.FontCaption,
            align: HorizontalAlignment.Left
        );
        statusLabel.AddThemeColorOverride("font_color", statusColor);
        vbox.AddChild(statusLabel);

        var detailButton = new StyledButton(
            "DETAIL",
            scale,
            fontSize: btnFont,
            height: btnHeight,
            variant: ButtonVariant.Secondary
        );
        detailButton.CustomMinimumSize = new Vector2(0, (int)(btnHeight * scale));
        detailButton.Pressed += () => DetailRequested?.Invoke();
        row.AddChild(detailButton);

        if (showStashToggle)
        {
            var stashButton = new StyledButton(
                disabled ? "ENABLE" : "DISABLE",
                scale,
                fontSize: btnFont,
                height: btnHeight,
                variant: Ui.StashToggleVariant(disabled)
            );
            stashButton.CustomMinimumSize = new Vector2(
                (int)((compact ? 100 : 130) * scale),
                (int)(btnHeight * scale)
            );
            stashButton.Pressed += () => ToggleStashPressed?.Invoke();
            row.AddChild(stashButton);
        }

        var unsubButton = new StyledButton(
            "UNSUBSCRIBE",
            scale,
            fontSize: btnFont,
            height: btnHeight,
            variant: ButtonVariant.Danger
        );
        unsubButton.CustomMinimumSize = new Vector2(
            (int)((compact ? 120 : 150) * scale),
            (int)(btnHeight * scale)
        );
        unsubButton.Pressed += () => UnsubscribePressed?.Invoke();
        row.AddChild(unsubButton);
    }
}
