using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Godot;
using STS2Mobile.Patches;
using STS2Mobile.Steam;

namespace STS2Mobile.Launcher.Components;

// Full-screen Steam-Workshop-style detail page for a Workshop item, opened when a
// card is tapped in the WORKSHOP / SUBSCRIBED tabs (issue #58). Unlike the old
// ModDetailDialog modal, this is a real page with four tabs like the Steam
// Community item page:
//   설명   (Description)   — native: full description, thumbnail, stats, tags.
//   업데이트 노트 (Change Notes) — native: PublishedFile.GetChangeHistory, lazy-loaded.
//   토론   (Discussions)   — Steam Community web feature; SteamKit2 has no RPC for
//                            it, so this tab explains that and opens the browser.
//   댓글   (Comments)      — shows the public comment count (the one datum the API
//                            exposes) and opens the browser to read/post.
//
// All network work is handed in as callbacks that run off the main thread and
// marshal back through runOnMain, so opening the page never blocks the UI.
public class WorkshopDetailPage : ColorRect
{
    private const int TabDescription = 0;
    private const int TabChanges = 1;
    private const int TabDiscussions = 2;
    private const int TabComments = 3;

    private readonly float _scale;
    private readonly ulong _pfid;
    private WorkshopItemDetails _item;

    private readonly Func<Task<WorkshopItemDetails>> _loadFullDetails;
    private readonly Func<Task<List<WorkshopChangeEntry>>> _loadChanges;
    private readonly Action<Action> _runOnMain;
    private readonly Action _onSubscribe;
    private readonly Action _onUnsubscribe;

    private readonly StyledButton[] _tabButtons = new StyledButton[4];
    private readonly Control[] _tabBodies = new Control[4];
    private int _activeTab = TabDescription;

    // Description-tab widgets rebuilt when full details arrive.
    private HFlowContainer _statsFlow;
    private HFlowContainer _tagsFlow;
    private StyledLabel _descLabel;
    private TextureRect _thumb;

    // Change-notes tab: lazily loaded on first open.
    private VBoxContainer _notesList;
    private bool _notesRequested;

    // Footer action (SUBSCRIBE / UNSUBSCRIBE).
    private StyledButton _actionButton;
    private bool _subscribed;

    public ulong PublishedFileId => _pfid;

    public void SetThumbnail(Texture2D tex)
    {
        if (_thumb != null && IsInstanceValid(_thumb))
            _thumb.Texture = tex;
    }

    private readonly bool _compact;
    private readonly bool _showAction;

    // showAction=false renders a read-only page (no SUBSCRIBE/UNSUBSCRIBE footer)
    // — used when opened from the SUBSCRIBED tab, whose rows already carry the
    // ENABLE/DISABLE/UNSUBSCRIBE actions.
    public WorkshopDetailPage(
        WorkshopItemDetails item,
        float scale,
        bool subscribed,
        bool compact,
        Func<Task<WorkshopItemDetails>> loadFullDetails,
        Func<Task<List<WorkshopChangeEntry>>> loadChanges,
        Action<Action> runOnMain,
        Action onSubscribe,
        Action onUnsubscribe,
        bool showAction = true
    )
    {
        // Register FIRST so the TouchScrolls built below capture this page's modal
        // depth (they scroll while the page is top-most; the hub's lists don't).
        ModalGate.Register(this);

        _item = item;
        _scale = scale;
        _compact = compact;
        _showAction = showAction;
        _pfid = item.PublishedFileId;
        _subscribed = subscribed;
        _loadFullDetails = loadFullDetails;
        _loadChanges = loadChanges;
        _runOnMain = runOnMain;
        _onSubscribe = onSubscribe;
        _onUnsubscribe = onUnsubscribe;

        // Opaque page background so the Mod Hub behind is fully hidden and reads as
        // a navigated-to page, not a floating modal.
        SetAnchorsPreset(LayoutPreset.FullRect);
        Color = Ui.Bg;
        MouseFilter = MouseFilterEnum.Stop;

        var pad = new MarginContainer();
        pad.SetAnchorsPreset(LayoutPreset.FullRect);
        pad.AddThemeConstantOverride("margin_left", Ui.S(scale, 20));
        pad.AddThemeConstantOverride("margin_right", Ui.S(scale, 20));
        pad.AddThemeConstantOverride("margin_top", Ui.S(scale, 14));
        pad.AddThemeConstantOverride("margin_bottom", Ui.S(scale, 14));
        AddChild(pad);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", Ui.S(scale, 10));
        pad.AddChild(root);

        root.AddChild(BuildHeader());
        root.AddChild(BuildTabBar());
        root.AddChild(BuildContentHost());
        root.AddChild(BuildFooter());

        RenderDescription();
        SelectTab(TabDescription);

        // Fill in the full description + community stats that QueryFiles doesn't
        // return (file_description, comment count, views, favorites, created date).
        KickLoadFullDetails();
    }

    private Control BuildHeader()
    {
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", Ui.S(_scale, 10));

        var back = new StyledButton(
            Loc.Tr("‹ 뒤로", "‹ BACK"),
            _scale,
            fontSize: Ui.FontBody,
            height: 44,
            variant: ButtonVariant.Ghost
        );
        back.CustomMinimumSize = new Vector2(Ui.S(_scale, 120), Ui.S(_scale, 44));
        back.Pressed += QueueFree;
        header.AddChild(back);

        var title = new StyledLabel(
            _item.Title ?? "",
            _scale,
            fontSize: Ui.FontTitle,
            align: HorizontalAlignment.Left
        );
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        title.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        title.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        header.AddChild(title);

        return header;
    }

    private Control BuildTabBar()
    {
        var bar = new HBoxContainer();
        bar.AddThemeConstantOverride("separation", Ui.S(_scale, 4));

        string[] labels =
        {
            Loc.Tr("설명", "DESCRIPTION"),
            Loc.Tr("업데이트 노트", "CHANGE NOTES"),
            Loc.Tr("토론", "DISCUSSIONS"),
            Loc.Tr("댓글", "COMMENTS"),
        };

        for (int i = 0; i < labels.Length; i++)
        {
            int idx = i;
            var btn = new StyledButton(
                labels[i],
                _scale,
                fontSize: _compact ? Ui.FontMicro : Ui.FontCaption,
                height: 44,
                variant: ButtonVariant.Ghost
            );
            btn.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            btn.Pressed += () => SelectTab(idx);
            _tabButtons[i] = btn;
            bar.AddChild(btn);
        }
        return bar;
    }

    private Control BuildContentHost()
    {
        // A plain Control host so each tab body can fill it via FullRect anchors and
        // be toggled with Visible; the host takes all the space between the tab bar
        // and the footer.
        var host = new Control();
        host.SizeFlagsVertical = SizeFlags.ExpandFill;
        host.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        host.ClipContents = true;

        _tabBodies[TabDescription] = BuildDescriptionBody();
        _tabBodies[TabChanges] = BuildChangesBody();
        _tabBodies[TabDiscussions] = BuildDiscussionsBody();
        _tabBodies[TabComments] = BuildCommentsBody();

        foreach (var body in _tabBodies)
        {
            body.SetAnchorsPreset(LayoutPreset.FullRect);
            host.AddChild(body);
        }
        return host;
    }

    private Control BuildDescriptionBody()
    {
        var scroll = new ScrollContainer();
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        TouchScroll.Attach(scroll);

        var content = new VBoxContainer();
        content.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        content.AddThemeConstantOverride("separation", Ui.S(_scale, 12));
        scroll.AddChild(content);

        // Hero: thumbnail + stats/tags.
        var hero = new HBoxContainer();
        hero.AddThemeConstantOverride("separation", Ui.S(_scale, 14));
        content.AddChild(hero);

        var thumbBg = new StyleBoxFlat { BgColor = new Color(0.20f, 0.21f, 0.26f) };
        thumbBg.SetCornerRadiusAll(Ui.S(_scale, 4));
        var thumbPanel = new PanelContainer();
        thumbPanel.AddThemeStyleboxOverride("panel", thumbBg);
        thumbPanel.CustomMinimumSize = new Vector2(Ui.S(_scale, 240), Ui.S(_scale, 135));
        thumbPanel.SizeFlagsVertical = SizeFlags.ShrinkBegin;
        _thumb = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        };
        thumbPanel.AddChild(_thumb);
        hero.AddChild(thumbPanel);

        var heroInfo = new VBoxContainer();
        heroInfo.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        heroInfo.AddThemeConstantOverride("separation", Ui.S(_scale, 8));
        hero.AddChild(heroInfo);

        _statsFlow = new HFlowContainer();
        _statsFlow.AddThemeConstantOverride("h_separation", Ui.S(_scale, 6));
        _statsFlow.AddThemeConstantOverride("v_separation", Ui.S(_scale, 6));
        heroInfo.AddChild(_statsFlow);

        _tagsFlow = new HFlowContainer();
        _tagsFlow.AddThemeConstantOverride("h_separation", Ui.S(_scale, 6));
        _tagsFlow.AddThemeConstantOverride("v_separation", Ui.S(_scale, 6));
        heroInfo.AddChild(_tagsFlow);

        var divider = new HSeparator();
        divider.AddThemeConstantOverride("separation", Ui.S(_scale, 2));
        content.AddChild(divider);

        _descLabel = new StyledLabel(
            "",
            _scale,
            fontSize: Ui.FontBody,
            align: HorizontalAlignment.Left
        );
        _descLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _descLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _descLabel.AddThemeColorOverride("font_color", Ui.TextSecondary);
        content.AddChild(_descLabel);

        return scroll;
    }

    private Control BuildChangesBody()
    {
        var scroll = new ScrollContainer();
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        TouchScroll.Attach(scroll);

        _notesList = new VBoxContainer();
        _notesList.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _notesList.AddThemeConstantOverride("separation", Ui.S(_scale, 10));
        scroll.AddChild(_notesList);

        _notesList.AddChild(
            MakeInfoLabel(Loc.Tr("업데이트 노트를 불러오는 중…", "Loading change notes…"))
        );
        return scroll;
    }

    private Control BuildDiscussionsBody() =>
        BuildWebFeatureBody(
            Loc.Tr("토론", "Discussions"),
            Loc.Tr(
                "이 모드의 토론은 Steam 커뮤니티에서 볼 수 있습니다. 아래 버튼으로 브라우저에서 여세요.",
                "Discussions for this mod live on the Steam Community. Open them in your browser below."
            ),
            $"https://steamcommunity.com/sharedfiles/filedetails/?id={_pfid}"
        );

    private Control BuildCommentsBody()
    {
        // The public comment COUNT is available (num_comments_public); the bodies
        // are a Steam Community web feature with no SteamKit2 RPC.
        uint n = _item.NumComments;
        string countLine =
            n > 0
                ? Loc.Tr($"댓글 {n:N0}개", $"{n:N0} comment(s)")
                : Loc.Tr("아직 댓글이 없습니다.", "No comments yet.");
        return BuildWebFeatureBody(
            Loc.Tr("댓글", "Comments"),
            countLine
                + "\n\n"
                + Loc.Tr(
                    "댓글은 Steam 커뮤니티에서 읽고 작성할 수 있습니다. 아래 버튼으로 브라우저에서 여세요.",
                    "Comments can be read and posted on the Steam Community. Open them in your browser below."
                ),
            $"https://steamcommunity.com/sharedfiles/filedetails/?id={_pfid}#comments"
        );
    }

    private Control BuildWebFeatureBody(string heading, string body, string url)
    {
        var center = new CenterContainer();

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", Ui.S(_scale, 14));
        col.CustomMinimumSize = new Vector2(Ui.S(_scale, 520), 0);
        center.AddChild(col);

        var h = new StyledLabel(
            heading,
            _scale,
            fontSize: Ui.FontSection,
            align: HorizontalAlignment.Center
        );
        col.AddChild(h);

        var b = new StyledLabel(
            body,
            _scale,
            fontSize: Ui.FontBody,
            align: HorizontalAlignment.Center
        );
        b.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        b.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        b.AddThemeColorOverride("font_color", Ui.TextSecondary);
        col.AddChild(b);

        var openRow = new HBoxContainer();
        openRow.Alignment = BoxContainer.AlignmentMode.Center;
        col.AddChild(openRow);

        var openButton = new StyledButton(
            Loc.Tr("브라우저에서 열기", "Open in browser"),
            _scale,
            fontSize: Ui.FontBody,
            height: 48,
            variant: ButtonVariant.Primary
        );
        openButton.CustomMinimumSize = new Vector2(Ui.S(_scale, 240), Ui.S(_scale, 48));
        openButton.Pressed += () =>
        {
            PatchHelper.Log($"[Workshop] Detail open-in-browser: {url}");
            OS.ShellOpen(url);
        };
        openRow.AddChild(openButton);

        return center;
    }

    private Control BuildFooter()
    {
        var footer = new HBoxContainer();
        footer.AddThemeConstantOverride("separation", Ui.S(_scale, 10));
        footer.Alignment = BoxContainer.AlignmentMode.End;

        if (!_showAction)
            return footer; // read-only page — actions live on the list row

        int btnHeight = _compact ? 44 : 48;
        _actionButton = new StyledButton(
            "SUBSCRIBE",
            _scale,
            fontSize: _compact ? Ui.FontCaption : Ui.FontBody,
            height: btnHeight,
            variant: ButtonVariant.Primary
        );
        _actionButton.CustomMinimumSize = new Vector2(
            Ui.S(_scale, _compact ? 150 : 200),
            Ui.S(_scale, btnHeight)
        );
        _actionButton.Pressed += OnActionPressed;
        footer.AddChild(_actionButton);

        ApplyActionButton();
        return footer;
    }

    private void OnActionPressed()
    {
        // Optimistic: disable until the pane reports back via ApplyStatus. The pane
        // owns the RPC + download queue + card refresh.
        _actionButton.Disabled = true;
        if (_subscribed)
            _onUnsubscribe?.Invoke();
        else
            _onSubscribe?.Invoke();
    }

    // Called by the owning pane after a subscribe/unsubscribe completes (or when
    // status otherwise changes) so the page's footer stays in sync with the list.
    public void ApplyStatus(bool subscribed)
    {
        _subscribed = subscribed;
        if (_actionButton == null)
            return; // read-only page
        _actionButton.Disabled = false;
        ApplyActionButton();
    }

    private void ApplyActionButton()
    {
        // SUBSCRIBE is constructive (accent); UNSUBSCRIBE deletes local files, so it
        // reads as Danger everywhere — same rule as the cards.
        _actionButton.Text = _subscribed ? "UNSUBSCRIBE" : "SUBSCRIBE";
        _actionButton.ApplyVariant(
            _scale,
            _subscribed ? ButtonVariant.Danger : ButtonVariant.Primary
        );
    }

    private void SelectTab(int index)
    {
        _activeTab = index;
        for (int i = 0; i < _tabButtons.Length; i++)
            StyleTab(_tabButtons[i], i == index);
        for (int i = 0; i < _tabBodies.Length; i++)
            _tabBodies[i].Visible = i == index;

        if (index == TabChanges && !_notesRequested)
        {
            _notesRequested = true;
            KickLoadChanges();
        }
    }

    // Material-style tab: active = accent bottom border + primary text.
    private void StyleTab(Button button, bool active)
    {
        StyleBoxFlat Make()
        {
            var box = new StyleBoxFlat { BgColor = Colors.Transparent };
            if (active)
            {
                box.BorderColor = Ui.Accent;
                box.BorderWidthBottom = Math.Max(2, Ui.S(_scale, 3));
            }
            return box;
        }

        button.AddThemeStyleboxOverride("normal", Make());
        button.AddThemeStyleboxOverride("hover", Make());
        button.AddThemeStyleboxOverride("pressed", Make());

        var fontColor = active ? Ui.TextPrimary : Ui.TextSecondary;
        button.AddThemeColorOverride("font_color", fontColor);
        button.AddThemeColorOverride("font_hover_color", fontColor);
        button.AddThemeColorOverride("font_pressed_color", fontColor);
        button.AddThemeColorOverride("font_focus_color", fontColor);
    }

    // --- Description rendering ------------------------------------------------

    private void RenderDescription()
    {
        // Stats pills.
        foreach (var c in _statsFlow.GetChildren())
            c.QueueFree();
        AddStat(
            Loc.Tr($"구독 {_item.Subscriptions:N0}", $"{_item.Subscriptions:N0} subscribers"),
            Ui.TextSecondary
        );
        AddStat(
            STS2Mobile.Launcher.LauncherModel.FormatSize((long)_item.FileSize),
            Ui.TextSecondary
        );
        AddStat($"{_item.VoteScore * 100f:F0}%", Ui.Success);
        if (_item.Favorited > 0)
            AddStat(
                Loc.Tr($"즐겨찾기 {_item.Favorited:N0}", $"{_item.Favorited:N0} favorites"),
                Ui.TextSecondary
            );
        if (_item.Views > 0)
            AddStat(Loc.Tr($"조회 {_item.Views:N0}", $"{_item.Views:N0} views"), Ui.TextSecondary);
        if (_item.TimeUpdated > 0)
            AddStat(
                Loc.Tr(
                    $"업데이트 {FormatDate(_item.TimeUpdated)}",
                    $"Updated {FormatDate(_item.TimeUpdated)}"
                ),
                Ui.TextSecondary
            );
        if (_item.TimeCreated > 0)
            AddStat(
                Loc.Tr(
                    $"게시 {FormatDate(_item.TimeCreated)}",
                    $"Posted {FormatDate(_item.TimeCreated)}"
                ),
                Ui.TextSecondary
            );

        // Tag pills.
        foreach (var c in _tagsFlow.GetChildren())
            c.QueueFree();
        if (_item.Tags != null)
        {
            foreach (var tag in _item.Tags)
                _tagsFlow.AddChild(Ui.MakePill(tag, _scale, Ui.Accent));
        }

        var desc = !string.IsNullOrWhiteSpace(_item.FullDescription)
            ? _item.FullDescription
            : _item.Description;
        _descLabel.Text = string.IsNullOrWhiteSpace(desc)
            ? Loc.Tr("(설명 없음)", "(no description)")
            : CleanBBCode(desc);
    }

    private void AddStat(string text, Color color) =>
        _statsFlow.AddChild(Ui.MakePill(text, _scale, color));

    private void KickLoadFullDetails()
    {
        if (_loadFullDetails == null)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                var full = await _loadFullDetails().ConfigureAwait(false);
                if (full == null)
                    return;
                _runOnMain(() =>
                {
                    if (!IsInstanceValid(this))
                        return;
                    _item = full;
                    RenderDescription();
                    // Rebuild the comments tab so the (now-known) count shows.
                    RebuildCommentsTab();
                    // Pages opened from the SUBSCRIBED tab start from a stub with
                    // no PreviewUrl — fetch the hero image once details arrive.
                    if (_thumb?.Texture == null && !string.IsNullOrEmpty(full.PreviewUrl))
                        _ = Task.Run(() => LoadOwnThumbnailAsync(full.PreviewUrl));
                });
            }
            catch (Exception ex)
            {
                PatchHelper.Log($"[Workshop] Detail full-load failed: {ex.Message}");
            }
        });
    }

    private async Task LoadOwnThumbnailAsync(string previewUrl)
    {
        try
        {
            var path = await WorkshopThumbnailCache
                .GetOrDownloadAsync(previewUrl)
                .ConfigureAwait(false);
            if (path == null)
                return;
            var tex = ThumbnailLoader.LoadTexture(path);
            if (tex == null)
                return;
            _runOnMain(() =>
            {
                if (IsInstanceValid(this))
                    SetThumbnail(tex);
            });
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Workshop] Detail own-thumbnail load failed: {ex.Message}");
        }
    }

    private void RebuildCommentsTab()
    {
        var host = _tabBodies[TabComments].GetParent();
        if (host == null)
            return;
        bool wasVisible = _tabBodies[TabComments].Visible;
        _tabBodies[TabComments].QueueFree();
        var rebuilt = BuildCommentsBody();
        rebuilt.SetAnchorsPreset(LayoutPreset.FullRect);
        rebuilt.Visible = wasVisible;
        host.AddChild(rebuilt);
        _tabBodies[TabComments] = rebuilt;
    }

    private void KickLoadChanges()
    {
        if (_loadChanges == null)
        {
            ShowNotes(new List<WorkshopChangeEntry>());
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var changes = await _loadChanges().ConfigureAwait(false);
                _runOnMain(() =>
                {
                    if (IsInstanceValid(this))
                        ShowNotes(changes);
                });
            }
            catch (Exception ex)
            {
                PatchHelper.Log($"[Workshop] Detail change-notes load failed: {ex.Message}");
                _runOnMain(() =>
                {
                    if (IsInstanceValid(this))
                        ShowNotesError();
                });
            }
        });
    }

    private void ShowNotes(List<WorkshopChangeEntry> changes)
    {
        foreach (var c in _notesList.GetChildren())
            c.QueueFree();

        if (changes == null || changes.Count == 0)
        {
            _notesList.AddChild(
                MakeInfoLabel(Loc.Tr("업데이트 노트가 없습니다.", "No change notes."))
            );
            return;
        }

        foreach (var entry in changes)
        {
            var card = new PanelContainer();
            card.AddThemeStyleboxOverride("panel", Ui.CardStyle(_scale));
            var col = new VBoxContainer();
            col.AddThemeConstantOverride("separation", Ui.S(_scale, 4));
            card.AddChild(col);

            var date = new StyledLabel(
                FormatDate(entry.Timestamp),
                _scale,
                fontSize: Ui.FontCaption,
                align: HorizontalAlignment.Left
            );
            date.AddThemeColorOverride("font_color", Ui.Accent);
            col.AddChild(date);

            var body = new StyledLabel(
                string.IsNullOrWhiteSpace(entry.Description)
                    ? Loc.Tr("(내용 없음)", "(no notes)")
                    : CleanBBCode(entry.Description),
                _scale,
                fontSize: Ui.FontBody,
                align: HorizontalAlignment.Left
            );
            body.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            body.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            body.AddThemeColorOverride("font_color", Ui.TextSecondary);
            col.AddChild(body);

            _notesList.AddChild(card);
        }
    }

    private void ShowNotesError()
    {
        foreach (var c in _notesList.GetChildren())
            c.QueueFree();
        _notesList.AddChild(
            MakeInfoLabel(
                Loc.Tr("업데이트 노트를 불러오지 못했습니다.", "Couldn't load change notes.")
            )
        );
    }

    private StyledLabel MakeInfoLabel(string text)
    {
        var label = new StyledLabel(
            text,
            _scale,
            fontSize: Ui.FontBody,
            align: HorizontalAlignment.Left
        );
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        label.AddThemeColorOverride("font_color", Ui.TextDisabled);
        return label;
    }

    private static string FormatDate(long unixSeconds)
    {
        try
        {
            return DateTimeOffset
                .FromUnixTimeSeconds(unixSeconds)
                .LocalDateTime.ToString("yyyy-MM-dd");
        }
        catch
        {
            return "";
        }
    }

    // Steam descriptions/change-notes are BBCode. There's no rich-text budget here,
    // so render a readable plain-text approximation: bullets for [*], blank lines
    // preserved, every other tag stripped.
    private static readonly Regex BBCodeTag = new(@"\[/?[^\]]*\]", RegexOptions.Compiled);

    private static string CleanBBCode(string s)
    {
        if (string.IsNullOrEmpty(s))
            return "";
        s = s.Replace("\r\n", "\n").Replace("\r", "\n");
        s = Regex.Replace(s, @"\[\*\]", "\n• ", RegexOptions.IgnoreCase);
        s = BBCodeTag.Replace(s, "");
        // Collapse runs of 3+ newlines the tag removal can leave behind.
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        return s.Trim();
    }
}
