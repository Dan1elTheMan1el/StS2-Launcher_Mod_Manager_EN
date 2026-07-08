using Godot;

namespace STS2Mobile.Launcher.Components;

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

    public StyledButton(string text, float scale, int fontSize = 14, int height = 42)
    {
        Text = text;
        CustomMinimumSize = new Vector2(0, (int)(height * scale));
        AddThemeFontSizeOverride("font_size", (int)(fontSize * scale));

        var r = (int)(4 * scale);
        AddThemeStyleboxOverride("normal", MakeFilled(new Color(0.25f, 0.25f, 0.3f), r));
        AddThemeStyleboxOverride("hover", MakeFilled(new Color(0.3f, 0.3f, 0.36f), r));
        AddThemeStyleboxOverride("pressed", MakeFilled(new Color(0.2f, 0.2f, 0.25f), r));
        AddThemeStyleboxOverride("disabled", MakeFilled(new Color(0.2f, 0.2f, 0.22f), r));
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
