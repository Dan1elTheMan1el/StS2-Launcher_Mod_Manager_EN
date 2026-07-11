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

    // compact = portrait layout: smaller action buttons so the text column keeps
    // enough width (landscape sizes squeezed portrait rows into clipping).
    public WorkshopBrowseCard(
        WorkshopItemDetails item,
        float scale,
        string badge,
        bool subscribed,
        bool compact = false
    )
    {
        _scale = scale;
        PublishedFileId = item.PublishedFileId;
        int btnFont = compact ? Ui.FontMicro : Ui.FontCaption;
        int btnHeight = compact ? 40 : 44;

        AddThemeStyleboxOverride("panel", Ui.CardStyle(scale));

        // The card body carries NO tap handler: an earlier tap-to-detail overlay
        // (a full-area Button, then a Pass detector) intercepted the touch-down and
        // killed the ScrollContainer's drag-to-scroll — the list only scrolled via
        // the scrollbar (user report). Detail entry is an explicit DETAIL button
        // instead, so the body is purely scrollable, exactly like the first working
        // Mod Hub build (commit ab4ec9c).
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", (int)(8 * scale));
        AddChild(row);

        _thumb = new TextureRect();
        _thumb.CustomMinimumSize = new Vector2((int)(96 * scale), (int)(54 * scale));
        _thumb.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
        _thumb.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
        var thumbBg = new StyleBoxFlat();
        thumbBg.BgColor = new Color(0.28f, 0.28f, 0.32f);
        thumbBg.SetCornerRadiusAll((int)(3 * scale));
        var thumbPanel = new PanelContainer();
        thumbPanel.AddThemeStyleboxOverride("panel", thumbBg);
        thumbPanel.CustomMinimumSize = new Vector2((int)(96 * scale), (int)(54 * scale));
        thumbPanel.AddChild(_thumb);
        row.AddChild(thumbPanel);

        var info = new VBoxContainer();
        info.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        info.AddThemeConstantOverride("separation", (int)(2 * scale));
        row.AddChild(info);

        var titleLabel = new StyledLabel(
            item.Title,
            scale,
            fontSize: 14,
            align: HorizontalAlignment.Left
        );
        titleLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        info.AddChild(titleLabel);

        var statsText =
            $"{item.Subscriptions} subscriber(s) · {STS2Mobile.Launcher.LauncherModel.FormatSize((long)item.FileSize)} · {(item.VoteScore * 100f):F0}% rated";
        var statsLabel = new StyledLabel(
            statsText,
            scale,
            fontSize: Ui.FontMicro,
            align: HorizontalAlignment.Left
        );
        // Wrap instead of clipping — in portrait the text column is narrow and this
        // single stats line used to run off the edge (user report: 세로 글자 잘림).
        statsLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        statsLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        statsLabel.AddThemeColorOverride("font_color", Ui.TextSecondary);
        info.AddChild(statsLabel);

        _badgeLabel = new StyledLabel(
            "",
            scale,
            fontSize: Ui.FontMicro,
            align: HorizontalAlignment.Left
        );
        info.AddChild(_badgeLabel);

        // Explicit detail entry, left of the action button — replaces card-body
        // tap so scrolling and tapping never fight over the same touch.
        var detailButton = new StyledButton(
            "DETAIL",
            scale,
            fontSize: btnFont,
            height: btnHeight,
            variant: ButtonVariant.Secondary
        );
        // Fit-to-text width so it doesn't steal width from the title/stats column.
        detailButton.CustomMinimumSize = new Vector2(0, (int)(btnHeight * scale));
        detailButton.Pressed += () => DetailRequested?.Invoke();
        row.AddChild(detailButton);

        _actionButton = new StyledButton(
            "SUBSCRIBE",
            scale,
            fontSize: btnFont,
            height: btnHeight,
            variant: ButtonVariant.Primary
        );
        _actionButton.CustomMinimumSize = new Vector2(
            (int)((compact ? 110 : 140) * scale),
            (int)(btnHeight * scale)
        );
        _actionButton.Pressed += () =>
        {
            if (_subscribed)
                UnsubscribeRequested?.Invoke();
            else
                SubscribeRequested?.Invoke();
        };
        row.AddChild(_actionButton);

        ApplyStatus(badge, subscribed);
    }

    public void SetThumbnail(Texture2D tex) => _thumb.Texture = tex;

    // Lets a rebuild (orientation flip re-render) carry the already-decoded
    // thumbnail over to the replacement card instead of re-hitting the cache.
    public Texture2D CurrentThumbnail => _thumb.Texture;

    public void SetBusy(bool busy) => _actionButton.Disabled = busy;

    // badgeKey is an English semantic key ("Installed" / "Update available" /
    // "Disabled" / "Subscribed") so color logic stays stable; display text is
    // localized here.
    public void ApplyStatus(string badgeKey, bool subscribed)
    {
        _subscribed = subscribed;
        _badgeLabel.Visible = !string.IsNullOrEmpty(badgeKey);
        _badgeLabel.Text = badgeKey switch
        {
            "Installed" => Loc.Tr("설치됨", "Installed"),
            "Update available" => Loc.Tr("업데이트 있음", "Update available"),
            "Disabled" => Loc.Tr("비활성", "Disabled"),
            "Subscribed" => Loc.Tr("구독 중", "Subscribed"),
            _ => badgeKey ?? "",
        };
        _badgeLabel.AddThemeColorOverride(
            "font_color",
            badgeKey switch
            {
                "Installed" => Ui.Success,
                "Update available" => Ui.Warn,
                "Disabled" => Ui.TextDisabled,
                _ => Ui.Accent,
            }
        );

        // SUBSCRIBE is the filled accent (the card's constructive action); once
        // subscribed the action becomes UNSUBSCRIBE, which deletes local files —
        // destructive actions read as Danger EVERYWHERE, same as the SUBSCRIBED
        // tab (user report: the two screens disagreed).
        _actionButton.Text = subscribed ? "UNSUBSCRIBE" : "SUBSCRIBE";
        _actionButton.ApplyVariant(
            _scale,
            subscribed ? ButtonVariant.Danger : ButtonVariant.Primary
        );
    }
}
