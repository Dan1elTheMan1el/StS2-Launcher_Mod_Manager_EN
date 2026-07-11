using Godot;

namespace STS2Mobile.Launcher.Components;

// Visual role of a button. One Primary (filled accent) per view — everything
// else stays Secondary/Ghost/outline so the emphasized action is rare
// (Von Restorff). Every variant defines normal/hover/pressed/disabled boxes so
// a tap answers within a frame (Doherty).
public enum ButtonVariant
{
    Secondary, // filled card surface + hairline border (default)
    Primary, // filled accent — THE action of the view
    Danger, // red outline — destructive, still needs a confirm dialog
    Ghost, // borderless, text-only chrome (tabs, back, tertiary)
    Accent, // accent outline — positive restore/secondary-affirmative (ENABLE)
}

public class StyledButton : Button
{
    // Baseline size the main launcher screen's own action buttons render at
    // (ActionSection's Local Backup/Auto Sync/Push/Pull — see also the
    // similarly-sized SAVE MANAGER button in LauncherView). Modal dialogs
    // (CloudConflictDialog, ProfilePickerDialog) reuse these as a floor for
    // their own compact-viewport shrink, so a short screen can never render a
    // dialog button/text smaller than what's already on the main screen
    // (user report: "save manager 글자가 너무 작아").
    public const int MainActionFontSize = 14;
    public const int MainActionHeight = 44;

    public StyledButton(
        string text,
        float scale,
        int fontSize = Ui.FontBody,
        int height = Ui.TouchHeight,
        ButtonVariant variant = ButtonVariant.Secondary
    )
    {
        Text = text;
        CustomMinimumSize = new Vector2(0, Ui.S(scale, height));
        AddThemeFontSizeOverride("font_size", Ui.S(scale, fontSize));
        ApplyVariant(scale, variant);
    }

    public void ApplyVariant(float scale, ButtonVariant variant)
    {
        switch (variant)
        {
            case ButtonVariant.Primary:
                AddThemeStyleboxOverride("normal", Ui.Filled(scale, Ui.Accent));
                AddThemeStyleboxOverride("hover", Ui.Filled(scale, Ui.AccentHover));
                AddThemeStyleboxOverride("pressed", Ui.Filled(scale, Ui.AccentDown));
                AddThemeStyleboxOverride("disabled", Ui.Filled(scale, Ui.CardDown));
                SetFontColors(Colors.White, Ui.TextDisabled);
                break;

            case ButtonVariant.Danger:
                AddThemeStyleboxOverride("normal", Ui.Outline(scale, Ui.Danger));
                AddThemeStyleboxOverride("hover", Ui.Outline(scale, Ui.Danger));
                AddThemeStyleboxOverride(
                    "pressed",
                    Ui.Outline(
                        scale,
                        Ui.Danger,
                        bg: new Color(Ui.Danger.R, Ui.Danger.G, Ui.Danger.B, 0.18f)
                    )
                );
                AddThemeStyleboxOverride("disabled", Ui.Outline(scale, Ui.Divider));
                SetFontColors(Ui.Danger, Ui.TextDisabled);
                break;

            case ButtonVariant.Accent:
                AddThemeStyleboxOverride("normal", Ui.Outline(scale, Ui.Accent));
                AddThemeStyleboxOverride("hover", Ui.Outline(scale, Ui.AccentHover));
                AddThemeStyleboxOverride(
                    "pressed",
                    Ui.Outline(
                        scale,
                        Ui.Accent,
                        bg: new Color(Ui.Accent.R, Ui.Accent.G, Ui.Accent.B, 0.18f)
                    )
                );
                AddThemeStyleboxOverride("disabled", Ui.Outline(scale, Ui.Divider));
                SetFontColors(Ui.Accent, Ui.TextDisabled);
                break;

            case ButtonVariant.Ghost:
                AddThemeStyleboxOverride("normal", Ui.Filled(scale, Colors.Transparent));
                AddThemeStyleboxOverride("hover", Ui.Filled(scale, new Color(1, 1, 1, 0.06f)));
                AddThemeStyleboxOverride("pressed", Ui.Filled(scale, new Color(1, 1, 1, 0.10f)));
                AddThemeStyleboxOverride("disabled", Ui.Filled(scale, Colors.Transparent));
                SetFontColors(Ui.TextSecondary, Ui.TextDisabled);
                break;

            default: // Secondary
                var normal = Ui.Filled(scale, Ui.Card);
                normal.BorderColor = Ui.Divider;
                normal.SetBorderWidthAll(System.Math.Max(1, Ui.S(scale, 1)));
                AddThemeStyleboxOverride("normal", normal);
                AddThemeStyleboxOverride("hover", Ui.Filled(scale, Ui.CardHover));
                AddThemeStyleboxOverride("pressed", Ui.Filled(scale, Ui.CardDown));
                AddThemeStyleboxOverride("disabled", Ui.Filled(scale, Ui.CardDown));
                SetFontColors(Ui.TextPrimary, Ui.TextDisabled);
                break;
        }
    }

    private void SetFontColors(Color normal, Color disabled)
    {
        AddThemeColorOverride("font_color", normal);
        AddThemeColorOverride("font_hover_color", normal);
        AddThemeColorOverride("font_pressed_color", normal);
        AddThemeColorOverride("font_focus_color", normal);
        AddThemeColorOverride("font_disabled_color", disabled);
    }

    public static StyleBoxFlat MakeFilled(Color bg, int cornerRadius)
    {
        var style = new StyleBoxFlat();
        style.BgColor = bg;
        style.SetCornerRadiusAll(cornerRadius);
        return style;
    }

    public static StyleBoxFlat MakeOutline(Color borderColor, int cornerRadius, int borderWidth)
    {
        var style = new StyleBoxFlat();
        style.BgColor = Colors.Transparent;
        style.BorderColor = borderColor;
        style.SetBorderWidthAll(borderWidth);
        style.SetCornerRadiusAll(cornerRadius);
        return style;
    }
}
