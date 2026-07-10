using Godot;

namespace STS2Mobile.Launcher.Components;

// Design tokens for the launcher front-end (issue #58 redesign). One place for
// color, type, spacing and touch metrics so every screen reads as one system
// (aesthetic-usability) instead of per-file ad-hoc grays.
//
// Grounding (UX laws the redesign was reviewed against):
//  * Fitts — the launcher renders on a fixed 1920×1080 logical canvas mapped to
//    a ~150mm-wide landscape phone (≈12.8 logical px per mm, scale is 2.0). A
//    9-10mm touch target therefore needs ≥~120 logical px ⇒ TouchHeight 48pt
//    (=96px ≈ 7.5mm) minimum, TouchHeightBig 56pt (≈8.8mm) for primary actions,
//    and ≥GapS between adjacent targets.
//  * Von Restorff — exactly one filled-accent (Primary) action per view; all
//    other buttons are Secondary/Ghost/outline so the emphasized one stays rare.
//  * Doherty — every control has distinct normal/hover/pressed/disabled state
//    boxes so a tap gives sub-100ms visual feedback.
public static class Ui
{
    // --- Color -----------------------------------------------------------
    public static readonly Color Bg = new(0.07f, 0.08f, 0.10f);
    public static readonly Color Surface = new(0.11f, 0.12f, 0.15f);
    public static readonly Color Card = new(0.15f, 0.165f, 0.205f);
    public static readonly Color CardHover = new(0.185f, 0.20f, 0.25f);
    public static readonly Color CardDown = new(0.125f, 0.135f, 0.17f);
    public static readonly Color SurfaceHigh = new(0.17f, 0.185f, 0.23f);
    public static readonly Color Divider = new(0.245f, 0.265f, 0.32f);

    public static readonly Color Accent = new(0.28f, 0.51f, 0.96f);
    public static readonly Color AccentHover = new(0.34f, 0.57f, 1.0f);
    public static readonly Color AccentDown = new(0.22f, 0.41f, 0.80f);

    public static readonly Color Success = new(0.30f, 0.72f, 0.46f);
    public static readonly Color Warn = new(0.95f, 0.68f, 0.30f);
    public static readonly Color Danger = new(0.88f, 0.36f, 0.32f);

    public static readonly Color TextPrimary = new(0.92f, 0.93f, 0.96f);
    public static readonly Color TextSecondary = new(0.66f, 0.68f, 0.74f);
    public static readonly Color TextDisabled = new(0.42f, 0.44f, 0.50f);

    // --- Type scale (pt at scale 1; multiply by scale) ---------------------
    public const int FontTitle = 20;
    public const int FontSection = 16;
    public const int FontBody = 14;
    public const int FontCaption = 12;
    public const int FontMicro = 11;

    // --- Touch & spacing ---------------------------------------------------
    public const int TouchHeight = 48;
    public const int TouchHeightBig = 56;
    public const int GapS = 6;
    public const int GapM = 10;
    public const int GapL = 16;
    public const int RadiusS = 6;
    public const int RadiusM = 8;
    public const int RadiusL = 12;
    public const int PadCard = 12;

    public static int S(float scale, int v) => (int)(v * scale);

    // --- Style factories ----------------------------------------------------
    public static StyleBoxFlat Filled(float scale, Color bg, int radius = RadiusS)
    {
        var s = new StyleBoxFlat { BgColor = bg };
        s.SetCornerRadiusAll(S(scale, radius));
        return s;
    }

    public static StyleBoxFlat Outline(
        float scale,
        Color border,
        int radius = RadiusS,
        int width = 2,
        Color? bg = null
    )
    {
        var s = new StyleBoxFlat { BgColor = bg ?? Colors.Transparent, BorderColor = border };
        s.SetBorderWidthAll(System.Math.Max(1, S(scale, width) / 2));
        s.SetCornerRadiusAll(S(scale, radius));
        return s;
    }

    // Standard list card/row background.
    public static StyleBoxFlat CardStyle(float scale, Color? bg = null)
    {
        var s = Filled(scale, bg ?? Card, RadiusM);
        s.SetContentMarginAll(S(scale, PadCard));
        return s;
    }

    // Card tinted by a semantic color (e.g. conflict = Warn) at low alpha, with
    // a matching border so the tint reads even on small rows.
    public static StyleBoxFlat TintedCardStyle(float scale, Color color)
    {
        var s = new StyleBoxFlat
        {
            BgColor = new Color(color.R, color.G, color.B, 0.10f),
            BorderColor = new Color(color.R, color.G, color.B, 0.45f),
        };
        s.SetBorderWidthAll(System.Math.Max(1, S(scale, 1)));
        s.SetCornerRadiusAll(S(scale, RadiusM));
        s.SetContentMarginAll(S(scale, PadCard));
        return s;
    }

    // Small status pill ("Installed", "Update available", ...).
    public static PanelContainer MakePill(string text, float scale, Color color)
    {
        var pill = new PanelContainer();
        var s = new StyleBoxFlat { BgColor = new Color(color.R, color.G, color.B, 0.16f) };
        s.SetCornerRadiusAll(S(scale, 999));
        s.ContentMarginLeft = S(scale, 10);
        s.ContentMarginRight = S(scale, 10);
        s.ContentMarginTop = S(scale, 3);
        s.ContentMarginBottom = S(scale, 3);
        pill.AddThemeStyleboxOverride("panel", s);
        pill.MouseFilter = Control.MouseFilterEnum.Ignore;

        var label = new StyledLabel(text, scale, fontSize: FontMicro);
        label.AddThemeColorOverride("font_color", color);
        pill.AddChild(label);
        return pill;
    }

    // Section header used to chunk long lists (Miller): caption text + divider.
    public static Control MakeSectionHeader(string text, float scale)
    {
        var box = new VBoxContainer();
        box.MouseFilter = Control.MouseFilterEnum.Ignore;
        box.AddThemeConstantOverride("separation", S(scale, 4));

        var label = new StyledLabel(
            text,
            scale,
            fontSize: FontCaption,
            align: HorizontalAlignment.Left
        );
        label.AddThemeColorOverride("font_color", TextSecondary);
        box.AddChild(label);

        var line = new ColorRect { Color = Divider };
        line.CustomMinimumSize = new Vector2(0, System.Math.Max(1, S(scale, 1)));
        line.MouseFilter = Control.MouseFilterEnum.Ignore;
        box.AddChild(line);
        return box;
    }

    // Centered empty-state block (peak-end: a friendly guide instead of a blank
    // void) — a large glyph, a headline and an optional hint.
    public static Control MakeEmptyState(string glyph, string headline, string hint, float scale)
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", S(scale, 6));
        box.Alignment = BoxContainer.AlignmentMode.Center;
        box.MouseFilter = Control.MouseFilterEnum.Ignore;

        if (!string.IsNullOrEmpty(glyph))
        {
            var g = new StyledLabel(glyph, scale, fontSize: 30);
            g.AddThemeColorOverride("font_color", TextDisabled);
            box.AddChild(g);
        }

        var h = new StyledLabel(headline, scale, fontSize: FontBody);
        h.AddThemeColorOverride("font_color", TextSecondary);
        h.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        h.HorizontalAlignment = HorizontalAlignment.Center;
        box.AddChild(h);

        if (!string.IsNullOrEmpty(hint))
        {
            var t = new StyledLabel(hint, scale, fontSize: FontCaption);
            t.AddThemeColorOverride("font_color", TextDisabled);
            t.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            t.HorizontalAlignment = HorizontalAlignment.Center;
            box.AddChild(t);
        }
        return box;
    }
}
