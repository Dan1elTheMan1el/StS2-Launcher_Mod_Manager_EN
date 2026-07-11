using Godot;

namespace STS2Mobile.Launcher.Components;

public class StyledLabel : Label
{
    public StyledLabel(
        string text,
        float scale,
        int fontSize = 15,
        HorizontalAlignment align = HorizontalAlignment.Center
    )
    {
        Text = text;
        HorizontalAlignment = align;
        // Labels are never interactive; let taps fall through to a parent panel so
        // tappable rows/cards (Mod Hub detail-on-tap) work regardless of where on
        // the row the tap lands.
        MouseFilter = MouseFilterEnum.Ignore;
        AddThemeFontSizeOverride("font_size", (int)(fontSize * scale));
    }
}
