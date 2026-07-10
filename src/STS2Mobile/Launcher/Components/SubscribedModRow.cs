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
        bool statusIsError,
        float scale,
        bool disabled = false,
        bool showStashToggle = false,
        bool statusGood = false
    )
    {
        AddThemeStyleboxOverride(
            "panel",
            disabled ? Ui.CardStyle(scale, Ui.CardDown) : Ui.CardStyle(scale)
        );

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
        if (disabled)
            titleLabel.AddThemeColorOverride("font_color", Ui.TextDisabled);
        vbox.AddChild(titleLabel);

        var statusLabel = new StyledLabel(status, scale, fontSize: Ui.FontCaption, align: HorizontalAlignment.Left);
        statusLabel.AddThemeColorOverride(
            "font_color",
            statusIsError ? Ui.Danger
            : disabled ? Ui.TextDisabled
            : statusGood ? Ui.Success
            : Ui.TextSecondary
        );
        vbox.AddChild(statusLabel);

        if (showStashToggle)
        {
            var stashButton = new StyledButton(
                disabled ? "ENABLE" : "DISABLE",
                scale,
                fontSize: Ui.FontCaption,
                height: 44,
                variant: disabled ? ButtonVariant.Accent : ButtonVariant.Secondary
            );
            stashButton.CustomMinimumSize = new Vector2((int)(130 * scale), (int)(44 * scale));
            stashButton.Pressed += () => ToggleStashPressed?.Invoke();
            row.AddChild(stashButton);
        }

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

        // Tapping the row body (not the buttons) opens the detail page; drags
        // fall through to the ScrollContainer so the list stays scrollable.
        TapGesture.Attach(this, () => DetailRequested?.Invoke());
    }
}
