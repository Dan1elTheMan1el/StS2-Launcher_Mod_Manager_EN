using Godot;

namespace STS2Mobile.Launcher.Components;

public class StyledLineEdit : LineEdit
{
    public StyledLineEdit(string placeholder, float scale, bool secret = false)
    {
        PlaceholderText = placeholder;
        Secret = secret;
        // Full-height touch target (Fitts) with a visible focus state so the
        // active field is obvious while the keyboard is up.
        CustomMinimumSize = new Vector2(0, Ui.S(scale, Ui.TouchHeight));
        AddThemeFontSizeOverride("font_size", Ui.S(scale, 15));
        ContextMenuEnabled = true;
        ShortcutKeysEnabled = true;
        SelectAllOnFocus = true;

        var normal = Ui.Filled(scale, Ui.CardDown);
        normal.BorderColor = Ui.Divider;
        normal.SetBorderWidthAll(System.Math.Max(1, Ui.S(scale, 1)));
        normal.ContentMarginLeft = Ui.S(scale, 12);
        normal.ContentMarginRight = Ui.S(scale, 12);
        AddThemeStyleboxOverride("normal", normal);

        var focus = Ui.Filled(scale, Ui.CardDown);
        focus.BorderColor = Ui.Accent;
        focus.SetBorderWidthAll(System.Math.Max(1, Ui.S(scale, 2) / 2));
        focus.ContentMarginLeft = Ui.S(scale, 12);
        focus.ContentMarginRight = Ui.S(scale, 12);
        AddThemeStyleboxOverride("focus", focus);

        AddThemeColorOverride("font_color", Ui.TextPrimary);
        AddThemeColorOverride("font_placeholder_color", Ui.TextDisabled);
        AddThemeColorOverride("caret_color", Ui.Accent);
    }
}
