using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using STS2Mobile.Launcher.Components;
using STS2Mobile.Modding;
using STS2Mobile.Steam;

namespace STS2Mobile.Launcher.Sections;

// WORKSHOP tab of the Mod Hub (issue #58 phase 4b): search/sort/tag-filter browser
// over QueryWorkshopAsync, with per-card SUBSCRIBE/UNSUBSCRIBE actions. All Steam
// RPCs and disk reads run on the thread pool (Task.Run); every Godot node touch is
// marshalled back via Callable.From(...).CallDeferred(), mirroring
// ModManagerSection's existing import-pipeline pattern.
public class WorkshopBrowserPane : VBoxContainer
{
    public event Action<string, Action, Action> ConfirmationRequested;

    private const uint PerPage = 20;
    private const ulong LargeDownloadWarningBytes = 50 * 1024 * 1024;

    private static readonly Color InfoColor = new(0.75f, 0.75f, 0.8f);
    private static readonly Color WarnColor = new(0.95f, 0.6f, 0.3f);

    private readonly float _scale;
    private readonly StyledLineEdit _searchEdit;
    private readonly StyledButton _searchButton;
    private readonly OptionButton _sortOption;
    private readonly StyledButton _tagsToggleButton;
    private readonly HFlowContainer _tagsPanel;
    private readonly StyledLabel _statusLabel;
    private readonly VBoxContainer _resultsList;
    private readonly StyledButton _loadMoreButton;

    private readonly HashSet<string> _selectedTags = new(StringComparer.Ordinal);
    private readonly HashSet<string> _knownTags = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, WorkshopBrowseCard> _cardsByPfid = new();
    private readonly Dictionary<ulong, WorkshopItemDetails> _itemsByPfid = new();

    private Dictionary<ulong, WorkshopItemDetails> _subscribedByPfid = new();
    private Dictionary<ulong, ModConfigEntry> _installedByPfid = new();

    private SteamConnection _connection;
    private WorkshopDownloadQueue _queue;
    private bool _initialized;
    private uint _page = 1;
    private uint _totalLoaded;
    private uint _totalAvailable;

    public WorkshopBrowserPane(float scale)
    {
        _scale = scale;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", (int)(6 * scale));

        var searchRow = new HBoxContainer();
        searchRow.AddThemeConstantOverride("separation", (int)(6 * scale));
        AddChild(searchRow);

        _searchEdit = new StyledLineEdit("Search Workshop or paste item URL/ID...", scale);
        _searchEdit.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _searchEdit.TextSubmitted += _ => OnSearchPressed();
        searchRow.AddChild(_searchEdit);

        _searchButton = new StyledButton("SEARCH", scale, fontSize: 13, height: 38);
        _searchButton.CustomMinimumSize = new Vector2((int)(90 * scale), 0);
        _searchButton.Pressed += OnSearchPressed;
        searchRow.AddChild(_searchButton);

        var filterRow = new HBoxContainer();
        filterRow.AddThemeConstantOverride("separation", (int)(6 * scale));
        AddChild(filterRow);

        _sortOption = new OptionButton();
        _sortOption.AddThemeFontSizeOverride("font_size", (int)(13 * scale));
        _sortOption.CustomMinimumSize = new Vector2((int)(150 * scale), (int)(38 * scale));
        _sortOption.AddItem("Popular", (int)WorkshopQuerySort.Popular);
        _sortOption.AddItem("Newest", (int)WorkshopQuerySort.Newest);
        _sortOption.AddItem("Trending", (int)WorkshopQuerySort.Trending);
        _sortOption.AddItem("Last Updated", (int)WorkshopQuerySort.LastUpdated);
        _sortOption.AddItem("Top Rated", (int)WorkshopQuerySort.TopRated);
        _sortOption.Selected = 0;
        _sortOption.ItemSelected += _ => OnSearchPressed();
        filterRow.AddChild(_sortOption);

        _tagsToggleButton = new StyledButton("TAGS", scale, fontSize: 13, height: 38);
        _tagsToggleButton.ToggleMode = true;
        _tagsToggleButton.Toggled += pressed => _tagsPanel.Visible = pressed;
        filterRow.AddChild(_tagsToggleButton);

        _statusLabel = new StyledLabel("", scale, fontSize: 12);
        _statusLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _statusLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        AddChild(_statusLabel);

        _tagsPanel = new HFlowContainer();
        _tagsPanel.Visible = false;
        AddChild(_tagsPanel);

        var scroll = new ScrollContainer();
        scroll.SizeFlagsVertical = SizeFlags.ExpandFill;
        scroll.CustomMinimumSize = new Vector2(0, (int)(220 * scale));
        AddChild(scroll);

        _resultsList = new VBoxContainer();
        _resultsList.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _resultsList.AddThemeConstantOverride("separation", (int)(6 * scale));
        scroll.AddChild(_resultsList);

        _loadMoreButton = new StyledButton("LOAD MORE", scale, fontSize: 13, height: 40);
        _loadMoreButton.Visible = false;
        _loadMoreButton.Pressed += OnLoadMorePressed;
        AddChild(_loadMoreButton);
    }

    public void SetQueue(WorkshopDownloadQueue queue) => _queue = queue;

    // Called by ModManagerSection every time the WORKSHOP tab is selected. Only
    // does the real work (status poll + initial query) the first time a session is
    // available in this pane's lifetime — see the class comment on "탭 진입 시 1회"
    // in the phase-4b spec. Subsequent visits reuse the cached results; SEARCH /
    // sort / tag changes always requery regardless of this flag.
    public void Activate(Func<Task<(bool ok, SteamConnection conn)>> ensureSession) =>
        _ = Task.Run(() => ActivateAsync(ensureSession));

    private async Task ActivateAsync(Func<Task<(bool ok, SteamConnection conn)>> ensureSession)
    {
        RunOnMain(() => SetStatus("Connecting to Steam...", InfoColor));
        var (ok, conn) = await ensureSession().ConfigureAwait(false);
        if (!ok)
        {
            _connection = null;
            RunOnMain(() => SetStatus("Steam login is required for Workshop features.", WarnColor));
            return;
        }
        _connection = conn;

        if (_initialized)
            return;
        _initialized = true;

        await LoadStatusAsync().ConfigureAwait(false);
        await RunQueryAsync(resetPage: true).ConfigureAwait(false);
    }

    private async Task LoadStatusAsync()
    {
        try
        {
            var subs = await _connection.GetSubscribedFilesAsync().ConfigureAwait(false);
            var subsByPfid = subs.ToDictionary(s => s.PublishedFileId, s => s);
            var cfg = ModConfig.Load();
            var installed = cfg
                .Mods.Where(m => m.IsWorkshop && m.PublishedFileId != 0)
                .ToDictionary(m => m.PublishedFileId, m => m);
            _subscribedByPfid = subsByPfid;
            _installedByPfid = installed;
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Workshop] Browser status load failed: {ex.Message}");
        }
    }

    private void OnSearchPressed() => _ = Task.Run(() => RunQueryAsync(resetPage: true));

    private void OnLoadMorePressed() => _ = Task.Run(() => RunQueryAsync(resetPage: false));

    private async Task RunQueryAsync(bool resetPage)
    {
        if (_connection == null)
            return;

        var searchText = _searchEdit.Text?.Trim() ?? "";

        // Direct add by URL/ID (issue #58 follow-up): unlisted items are excluded
        // from QueryFiles results server-side, so a pasted workshop URL or bare id
        // bypasses search and resolves via GetDetails instead — access is decided
        // by Steam per account, so unlisted/friends-only items the user can reach
        // work here.
        if (TryParsePublishedFileId(searchText, out var directPfid))
        {
            await RunDirectLookupAsync(directPfid).ConfigureAwait(false);
            return;
        }

        var sort = (WorkshopQuerySort)_sortOption.GetSelectedId();
        var tags = _selectedTags.ToList();

        if (resetPage)
        {
            _page = 1;
            RunOnMain(ClearResults);
        }

        RunOnMain(() =>
        {
            SetStatus("Loading...", InfoColor);
            _searchButton.Disabled = true;
            _loadMoreButton.Disabled = true;
        });

        try
        {
            var (items, total) = await _connection
                .QueryWorkshopAsync(sort, searchText, tags, _page, PerPage)
                .ConfigureAwait(false);

            _totalAvailable = total;
            if (resetPage)
                _totalLoaded = 0;
            _totalLoaded += (uint)items.Count;

            RunOnMain(() =>
            {
                foreach (var item in items)
                    AddResultCard(item);
                UpdateTagChips(items);
                SetStatus($"{_totalLoaded} / {_totalAvailable} item(s)", InfoColor);
                _loadMoreButton.Visible = _totalLoaded < _totalAvailable;
                _searchButton.Disabled = false;
                _loadMoreButton.Disabled = false;
            });
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Workshop] QueryWorkshopAsync failed: {ex}");
            RunOnMain(() =>
            {
                SetStatus($"Workshop query failed: {ex.Message}", WarnColor);
                _searchButton.Disabled = false;
                _loadMoreButton.Disabled = false;
            });
        }
    }

    // Accepts a bare numeric published-file id or any URL carrying "id=<digits>"
    // (e.g. https://steamcommunity.com/sharedfiles/filedetails/?id=3737335127).
    // An all-digits mod title can't be text-searched as a side effect — acceptable;
    // no real mod title is a bare 6+-digit number.
    private static bool TryParsePublishedFileId(string text, out ulong pfid)
    {
        pfid = 0;
        if (string.IsNullOrEmpty(text))
            return false;

        var m = System.Text.RegularExpressions.Regex.Match(text, @"[?&]id=(\d+)");
        if (m.Success)
            return ulong.TryParse(m.Groups[1].Value, out pfid) && pfid > 0;

        return text.Length >= 6
            && text.All(char.IsDigit)
            && ulong.TryParse(text, out pfid)
            && pfid > 0;
    }

    private async Task RunDirectLookupAsync(ulong pfid)
    {
        RunOnMain(() =>
        {
            ClearResults();
            SetStatus("Looking up item...", InfoColor);
            _searchButton.Disabled = true;
        });

        try
        {
            var items = await _connection
                .GetPublishedFileDetailsAsync(new[] { pfid })
                .ConfigureAwait(false);
            // A nonexistent/inaccessible id still yields a details row, just an
            // empty one — an absent Title is the "not found" signal.
            var item = items.FirstOrDefault(i =>
                i.PublishedFileId == pfid && !string.IsNullOrEmpty(i.Title)
            );

            RunOnMain(() =>
            {
                if (item == null)
                {
                    SetStatus(
                        $"No Workshop item found for id {pfid} (or this account cannot access it).",
                        WarnColor
                    );
                }
                else
                {
                    AddResultCard(item);
                    SetStatus("1 item (direct lookup)", InfoColor);
                }
                _loadMoreButton.Visible = false;
                _searchButton.Disabled = false;
                _loadMoreButton.Disabled = false;
            });
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Workshop] Direct lookup failed for {pfid}: {ex}");
            RunOnMain(() =>
            {
                SetStatus($"Item lookup failed: {ex.Message}", WarnColor);
                _searchButton.Disabled = false;
                _loadMoreButton.Disabled = false;
            });
        }
    }

    // Must run on the main thread — mutates Godot nodes.
    private void AddResultCard(WorkshopItemDetails item)
    {
        _itemsByPfid[item.PublishedFileId] = item;
        var (badge, subscribed) = DetermineStatus(item);
        var card = new WorkshopBrowseCard(item, _scale, badge, subscribed);
        card.SubscribeRequested += () => _ = Task.Run(() => OnSubscribeAsync(item.PublishedFileId));
        card.UnsubscribeRequested += () => _ = Task.Run(() => OnUnsubscribeAsync(item.PublishedFileId));
        _resultsList.AddChild(card);
        _cardsByPfid[item.PublishedFileId] = card;

        if (!string.IsNullOrEmpty(item.PreviewUrl))
            _ = Task.Run(() => LoadThumbnailAsync(item.PublishedFileId, item.PreviewUrl));
    }

    private async Task LoadThumbnailAsync(ulong pfid, string previewUrl)
    {
        try
        {
            var path = await WorkshopThumbnailCache.GetOrDownloadAsync(previewUrl).ConfigureAwait(false);
            if (path == null)
                return;

            // Decode off the main thread (file read + image decode); extension-
            // independent magic-byte loader since cached files may be ".img".
            var tex = ThumbnailLoader.LoadTexture(path);
            if (tex == null)
                return;

            RunOnMain(() =>
            {
                if (!_cardsByPfid.TryGetValue(pfid, out var card) || !IsInstanceValid(card))
                    return;
                card.SetThumbnail(tex);
            });
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Workshop] Thumbnail load failed: {ex.Message}");
        }
    }

    private (string badge, bool subscribed) DetermineStatus(WorkshopItemDetails item)
    {
        if (!_subscribedByPfid.ContainsKey(item.PublishedFileId))
            return (null, false);
        if (_installedByPfid.TryGetValue(item.PublishedFileId, out var entry))
        {
            return item.TimeUpdated > entry.TimeUpdated
                ? ("Update available", true)
                : ("Installed", true);
        }
        return ("Subscribed", true);
    }

    private async Task OnSubscribeAsync(ulong pfid)
    {
        if (_connection == null || !_itemsByPfid.TryGetValue(pfid, out var item))
            return;

        if (item.FileSize > LargeDownloadWarningBytes)
        {
            var confirmed = await ConfirmAsync(
                $"'{item.Title}' is {STS2Mobile.Launcher.LauncherModel.FormatSize((long)item.FileSize)}. Subscribe and download?"
            );
            if (!confirmed)
                return;
        }

        RunOnMain(() => SetCardBusy(pfid, true));

        try
        {
            await _connection.SetSubscriptionAsync(pfid, subscribe: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Workshop] Subscribe failed for {pfid}: {ex.Message}");
            RunOnMain(() =>
            {
                SetCardBusy(pfid, false);
                SetStatus($"Subscribe failed: {ex.Message}", WarnColor);
            });
            return;
        }

        _subscribedByPfid[pfid] = item;
        _queue?.Enqueue(item);

        RunOnMain(() =>
        {
            SetCardBusy(pfid, false);
            RefreshCardStatus(pfid);
        });

        if (item.Children.Count > 0)
            await ShowDependenciesAsync(item).ConfigureAwait(false);
    }

    private async Task ShowDependenciesAsync(WorkshopItemDetails item)
    {
        List<WorkshopItemDetails> deps;
        try
        {
            deps = await _connection.GetPublishedFileDetailsAsync(item.Children).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Workshop] Dependency lookup failed for {item.PublishedFileId}: {ex.Message}");
            return;
        }

        if (deps.Count == 0)
            return;

        var alreadySubscribed = new HashSet<ulong>(_subscribedByPfid.Keys);
        RunOnMain(() =>
        {
            var dialog = new WorkshopDependencyDialog(
                deps,
                alreadySubscribed,
                _scale,
                dep => SubscribeDependencyAsync(dep)
            );
            GetTree()?.Root?.AddChild(dialog);
        });
    }

    private async Task<bool> SubscribeDependencyAsync(WorkshopItemDetails dep)
    {
        if (_connection == null)
            return false;
        try
        {
            await _connection.SetSubscriptionAsync(dep.PublishedFileId, subscribe: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Workshop] Dependency subscribe failed for {dep.PublishedFileId}: {ex.Message}");
            return false;
        }
        _subscribedByPfid[dep.PublishedFileId] = dep;
        _queue?.Enqueue(dep);
        RunOnMain(() => RefreshCardStatus(dep.PublishedFileId));
        return true;
    }

    private async Task OnUnsubscribeAsync(ulong pfid)
    {
        if (_connection == null || !_itemsByPfid.TryGetValue(pfid, out var item))
            return;

        var confirmed = await ConfirmAsync(
            $"Unsubscribe from '{item.Title}'? This removes the mod from your device."
        );
        if (!confirmed)
            return;

        RunOnMain(() => SetCardBusy(pfid, true));

        bool removed;
        try
        {
            removed = await WorkshopSyncService
                .UnsubscribeAndRemoveAsync(_connection, pfid)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Workshop] Unsubscribe failed for {pfid}: {ex.Message}");
            RunOnMain(() =>
            {
                SetCardBusy(pfid, false);
                SetStatus($"Unsubscribe failed: {ex.Message}", WarnColor);
            });
            return;
        }

        _subscribedByPfid.Remove(pfid);
        _installedByPfid.Remove(pfid);

        RunOnMain(() =>
        {
            SetCardBusy(pfid, false);
            RefreshCardStatus(pfid);
            SetStatus(removed ? "Unsubscribed." : "Unsubscribed on Steam; local cleanup skipped.", InfoColor);
        });
    }

    // Must run on the main thread.
    private void SetCardBusy(ulong pfid, bool busy)
    {
        if (_cardsByPfid.TryGetValue(pfid, out var card) && IsInstanceValid(card))
            card.SetBusy(busy);
    }

    // Must run on the main thread.
    private void RefreshCardStatus(ulong pfid)
    {
        if (
            _cardsByPfid.TryGetValue(pfid, out var card)
            && IsInstanceValid(card)
            && _itemsByPfid.TryGetValue(pfid, out var item)
        )
        {
            var (badge, subscribed) = DetermineStatus(item);
            card.ApplyStatus(badge, subscribed);
        }
    }

    // Must run on the main thread.
    private void UpdateTagChips(List<WorkshopItemDetails> items)
    {
        bool added = false;
        foreach (var item in items)
        {
            foreach (var tag in item.Tags)
            {
                if (_knownTags.Add(tag))
                    added = true;
            }
        }
        if (!added)
            return;

        foreach (var child in _tagsPanel.GetChildren().ToList())
        {
            _tagsPanel.RemoveChild(child);
            child.QueueFree();
        }
        foreach (var tag in _knownTags.OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
        {
            var chip = new StyledButton(tag, _scale, fontSize: 11, height: 30);
            chip.ToggleMode = true;
            chip.SetPressedNoSignal(_selectedTags.Contains(tag));
            chip.Toggled += pressed =>
            {
                if (pressed)
                    _selectedTags.Add(tag);
                else
                    _selectedTags.Remove(tag);
                _ = Task.Run(() => RunQueryAsync(resetPage: true));
            };
            _tagsPanel.AddChild(chip);
        }
    }

    // Must run on the main thread.
    private void ClearResults()
    {
        foreach (var child in _resultsList.GetChildren().ToList())
        {
            _resultsList.RemoveChild(child);
            child.QueueFree();
        }
        _cardsByPfid.Clear();
        _itemsByPfid.Clear();
        _loadMoreButton.Visible = false;
    }

    // Must run on the main thread.
    private void SetStatus(string text, Color color)
    {
        _statusLabel.Text = text;
        _statusLabel.AddThemeColorOverride("font_color", color);
    }

    private Task<bool> ConfirmAsync(string message)
    {
        var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        RunOnMain(() =>
            ConfirmationRequested?.Invoke(
                message,
                () => tcs.TrySetResult(true),
                () => tcs.TrySetResult(false)
            )
        );
        return tcs.Task;
    }

    private static void RunOnMain(Action action) => Callable.From(action).CallDeferred();
}
