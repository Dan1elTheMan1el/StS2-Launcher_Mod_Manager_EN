using System;
using Godot;
using STS2Mobile.Steam;

namespace STS2Mobile.Launcher.Components;

// One search-result card in the Mod Hub's WORKSHOP browser tab (issue #58 phase
// 4b). Shows a lazily-loaded thumbnail, title/subscriber/size/rating stats, a
// status badge (Subscribed / Installed / Update available), and a single
// SUBSCRIBE/UNSUBSCRIBE action button whose label follows the current
// subscription state. WorkshopBrowserPane owns all RPC calls; this class only
// renders and raises intent events.
public class WorkshopBrowseCard : PanelContainer
{
    public event Action SubscribeRequested;
    public event Action UnsubscribeRequested;
    public event Action DetailRequested;

    public ulong PublishedFileId { get; }

    private readonly float _scale;
    private readonly TextureRect _thumb;
    private readonly StyledLabel _badgeLabel;
    private readonly StyledButton _actionButton;
    private bool _subscribed;

    public WorkshopBrowseCard(WorkshopItemDetails item, float scale, string badge, bool subscribed)
    {
        _scale = scale;
        PublishedFileId = item.PublishedFileId;
        MouseFilter = MouseFilterEnum.Stop;

        var bg = new StyleBoxFlat();
        bg.BgColor = new Color(0.18f, 0.18f, 0.22f);
        bg.SetCornerRadiusAll((int)(4 * scale));
        bg.SetContentMarginAll((int)(8 * scale));
        AddThemeStyleboxOverride("panel", bg);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", (int)(8 * scale));
        row.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(row);

        _thumb = new TextureRect();
        _thumb.CustomMinimumSize = new Vector2((int)(96 * scale), (int)(54 * scale));
        _thumb.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
        _thumb.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
        _thumb.MouseFilter = MouseFilterEnum.Ignore;
        var thumbBg = new StyleBoxFlat();
        thumbBg.BgColor = new Color(0.28f, 0.28f, 0.32f);
        thumbBg.SetCornerRadiusAll((int)(3 * scale));
        var thumbPanel = new PanelContainer();
        thumbPanel.AddThemeStyleboxOverride("panel", thumbBg);
        thumbPanel.CustomMinimumSize = new Vector2((int)(96 * scale), (int)(54 * scale));
        thumbPanel.MouseFilter = MouseFilterEnum.Ignore;
        thumbPanel.AddChild(_thumb);
        row.AddChild(thumbPanel);

        var info = new VBoxContainer();
        info.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        info.AddThemeConstantOverride("separation", (int)(2 * scale));
        info.MouseFilter = MouseFilterEnum.Ignore;
        row.AddChild(info);

        var titleLabel = new StyledLabel(item.Title, scale, fontSize: 14, align: HorizontalAlignment.Left);
        titleLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        info.AddChild(titleLabel);

        var statsText =
            $"{item.Subscriptions} subscriber(s) · {STS2Mobile.Launcher.LauncherModel.FormatSize((long)item.FileSize)} · {(item.VoteScore * 100f):F0}% rated";
        var statsLabel = new StyledLabel(statsText, scale, fontSize: 11, align: HorizontalAlignment.Left);
        statsLabel.AddThemeColorOverride("font_color", new Color(0.65f, 0.65f, 0.7f));
        info.AddChild(statsLabel);

        _badgeLabel = new StyledLabel("", scale, fontSize: 11, align: HorizontalAlignment.Left);
        _badgeLabel.AddThemeColorOverride("font_color", new Color(0.55f, 0.65f, 0.85f));
        info.AddChild(_badgeLabel);

        _actionButton = new StyledButton("SUBSCRIBE", scale, fontSize: 12, height: 36);
        _actionButton.CustomMinimumSize = new Vector2((int)(110 * scale), (int)(36 * scale));
        _actionButton.Pressed += () =>
        {
            if (_subscribed)
                UnsubscribeRequested?.Invoke();
            else
                SubscribeRequested?.Invoke();
        };
        row.AddChild(_actionButton);

        // Tapping the card body (not the action button) opens the detail page.
        GuiInput += ev =>
        {
            if (
                ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }
                or InputEventScreenTouch { Pressed: true }
            )
            {
                DetailRequested?.Invoke();
                AcceptEvent();
            }
        };

        ApplyStatus(badge, subscribed);
    }

    public void SetThumbnail(Texture2D tex) => _thumb.Texture = tex;

    public void SetBusy(bool busy) => _actionButton.Disabled = busy;

    public void ApplyStatus(string badge, bool subscribed)
    {
        _subscribed = subscribed;
        _badgeLabel.Text = badge ?? "";
        _badgeLabel.Visible = !string.IsNullOrEmpty(badge);
        _actionButton.Text = subscribed ? "UNSUBSCRIBE" : "SUBSCRIBE";
    }
}
