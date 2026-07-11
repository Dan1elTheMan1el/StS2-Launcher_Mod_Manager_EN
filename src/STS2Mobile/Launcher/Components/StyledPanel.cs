using System;
using Godot;

namespace STS2Mobile.Launcher.Components;

public class StyledPanel : CenterContainer
{
    public VBoxContainer Content { get; }

    public StyledPanel(float scale, float widthRatio = 0.7f, float heightRatio = 0.92f)
    {
        _heightRatio = heightRatio;
        SetAnchorsPreset(LayoutPreset.FullRect);

        var vpSize = new Vector2(1920, 1080); // fallback, overridden after AddChild
        var panelContainer = new PanelContainer();
        panelContainer.CustomMinimumSize = new Vector2(vpSize.X * widthRatio, 0);

        var style = new StyleBoxFlat();
        style.BgColor = Ui.Surface;
        style.SetCornerRadiusAll(S(scale, Ui.RadiusL));
        style.ContentMarginLeft = S(scale, 28);
        style.ContentMarginRight = S(scale, 28);
        style.ContentMarginTop = S(scale, 24);
        style.ContentMarginBottom = S(scale, 24);
        panelContainer.AddThemeStyleboxOverride("panel", style);
        AddChild(panelContainer);

        Content = new VBoxContainer();
        Content.SizeFlagsVertical = SizeFlags.ExpandFill;
        Content.AddThemeConstantOverride("separation", S(scale, 10));
        panelContainer.AddChild(Content);

        // Defer viewport-based sizing until in tree
        _panelContainer = panelContainer;
        _widthRatio = widthRatio;
    }

    public PanelContainer Panel => _panelContainer;
    private readonly PanelContainer _panelContainer;
    private readonly float _widthRatio;
    private readonly float _heightRatio;

    public void UpdateSizeFromViewport(Vector2 vpSize)
    {
        var w = vpSize.X * _widthRatio;
        var h = vpSize.Y * _heightRatio;
        _panelContainer.CustomMinimumSize = new Vector2(w, h);
        _panelContainer.Size = new Vector2(w, h);
    }

    private static int S(float scale, int v) => (int)(v * scale);
}
