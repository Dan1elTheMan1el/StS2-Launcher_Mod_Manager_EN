using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using STS2Mobile.Launcher.Components;
using STS2Mobile.Modding;
using STS2Mobile.Steam;

namespace STS2Mobile.Launcher.Sections;

// Full-screen Mod Hub shown when the user taps "MOD MANAGER" on the launch screen
// (issue #58). Four tabs:
//   WORKSHOP   — WorkshopBrowserPane: search/sort/tag browser, subscribe/unsubscribe.
//   SUBSCRIBED — WorkshopSubscribedPane: synced subscription list + unsubscribe.
//   LOCAL      — this class: ModScanner-based list of non-Workshop mods, import/remove.
//   DOWNLOADS  — WorkshopDownloadsPane: live view of the shared WorkshopDownloadQueue.
// The Workshop tabs share a single SteamConnection (via the LauncherModel injected
// through Configure()) and a single WorkshopDownloadQueue (created lazily on first
// successful connection, see EnsureSessionAsync), so download progress only ever
// shows in one place regardless of which tab kicked a download off.
public class ModManagerSection : VBoxContainer
{
    public event Action BackPressed;
    public event Action<string, Action, Action> ConfirmationRequested;

    // Raised with true=portrait / false=landscape; the controller applies it to
    // the launcher window and restores landscape when the hub closes.
    public event Action<bool> OrientationChangeRequested;

    private const int TabWorkshop = 0;
    private const int TabSubscribed = 1;
    private const int TabLocal = 2;
    private const int TabDownloads = 3;

    private static readonly Color InfoColor = Ui.TextSecondary;
    private static readonly Color WarnColor = Ui.Warn;
    private static readonly Color ErrorColor = Ui.Danger;

    private readonly float _scale;
    private readonly StyledButton[] _tabButtons;
    private readonly WorkshopBrowserPane _workshopPane;
    private readonly WorkshopSubscribedPane _subscribedPane;
    private readonly WorkshopDownloadsPane _downloadsPane;

    // --- LOCAL tab widgets (non-Workshop mods; Import/Remove) ------------------
    private readonly VBoxContainer _localPane;
    private readonly VBoxContainer _listContainer;
    private readonly StyledLabel _statusLabel;
    private readonly StyledButton _importButton;
    private readonly StyledButton _refreshButton;
    private readonly StyledButton _permissionButton;

    private readonly StyledButton _backButton;
    private readonly StyledButton _orientButton;
    private bool _portrait;
    private BusyOverlay _busy;

    private LauncherModel _model;
    private WorkshopDownloadQueue _queue;
    private readonly object _queueLock = new();
    private int _activeTab = TabLocal;
    private bool _importInFlight;
    private bool _idleSuspended;

    public ModManagerSection(float scale)
    {
        _scale = scale;
        Visible = false;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", (int)(8 * scale));

        // Header row: top-left back (the position every Android screen puts it —
        // Jakob's law) + screen title. The old bottom-anchored BACK is gone; the
        // hardware back button still routes here via LauncherUI.
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", (int)(Ui.GapM * scale));
        AddChild(header);

        _backButton = new StyledButton(
            "‹  BACK",
            scale,
            fontSize: Ui.FontBody,
            height: Ui.TouchHeight,
            variant: ButtonVariant.Ghost
        );
        _backButton.CustomMinimumSize = new Vector2(
            (int)(120 * scale),
            (int)(Ui.TouchHeight * scale)
        );
        _backButton.Pressed += () => BackPressed?.Invoke();
        header.AddChild(_backButton);

        var title = new StyledLabel(
            "Mod Hub",
            scale,
            fontSize: Ui.FontTitle,
            align: HorizontalAlignment.Left
        );
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(title);

        // Rotate toggle (issue #58): the launcher runs landscape, where a phone
        // (non-fold) shows only ~3 cards. This flips the Mod Hub to portrait so a
        // narrow single-column list shows many more; leaving the hub restores
        // landscape. Default stays landscape.
        _orientButton = new StyledButton(
            Loc.Tr("⤢ 세로", "⤢ Portrait"),
            scale,
            fontSize: Ui.FontCaption,
            height: Ui.TouchHeight,
            variant: ButtonVariant.Ghost
        );
        _orientButton.CustomMinimumSize = new Vector2((int)(110 * scale), (int)(Ui.TouchHeight * scale));
        _orientButton.Pressed += OnOrientToggle;
        header.AddChild(_orientButton);

        // Tab bar: four equal-width tabs with an accent underline on the active
        // one (Material tabs — the pattern mobile users already know).
        var tabRow = new HBoxContainer();
        tabRow.AddThemeConstantOverride("separation", (int)(Ui.GapS * scale));
        AddChild(tabRow);

        var tabNames = new[] { "WORKSHOP", "SUBSCRIBED", "LOCAL", "DOWNLOADS" };
        _tabButtons = new StyledButton[tabNames.Length];
        for (int i = 0; i < tabNames.Length; i++)
        {
            var idx = i;
            var btn = new StyledButton(
                tabNames[i],
                scale,
                fontSize: 13,
                height: Ui.TouchHeight,
                variant: ButtonVariant.Ghost
            );
            btn.ToggleMode = true;
            btn.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            btn.Pressed += () => SelectTab(idx);
            tabRow.AddChild(btn);
            _tabButtons[i] = btn;
        }

        _workshopPane = new WorkshopBrowserPane(scale);
        _workshopPane.ConfirmationRequested += (msg, ok, cancel) =>
            ConfirmationRequested?.Invoke(msg, ok, cancel);
        AddChild(_workshopPane);

        _subscribedPane = new WorkshopSubscribedPane(scale);
        _subscribedPane.ConfirmationRequested += (msg, ok, cancel) =>
            ConfirmationRequested?.Invoke(msg, ok, cancel);
        AddChild(_subscribedPane);

        // --- LOCAL pane ----------------------------------------------------
        _localPane = new VBoxContainer();
        _localPane.SizeFlagsVertical = SizeFlags.ExpandFill;
        _localPane.AddThemeConstantOverride("separation", (int)(8 * scale));
        AddChild(_localPane);

        var localHint = new StyledLabel(
            Loc.Tr("모드 활성화는 게임 내 Mods 메뉴에서 관리됩니다.","Mod activation is managed in the game's Mods menu."),
            scale,
            fontSize: 12
        );
        localHint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        localHint.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.65f));
        _localPane.AddChild(localHint);

        _statusLabel = new StyledLabel("", scale, fontSize: 12);
        _statusLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _localPane.AddChild(_statusLabel);

        _permissionButton = new StyledButton("Grant Storage Permission", scale, fontSize: 14);
        _permissionButton.Visible = false;
        _permissionButton.Pressed += OnGrantPermissionPressed;
        _localPane.AddChild(_permissionButton);

        var actionRow = new HBoxContainer();
        actionRow.AddThemeConstantOverride("separation", (int)(6 * scale));
        _localPane.AddChild(actionRow);

        _importButton = new StyledButton("Import Mod (.zip)...", scale, fontSize: 14);
        _importButton.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _importButton.Pressed += OnImportPressed;
        actionRow.AddChild(_importButton);

        _refreshButton = new StyledButton("Refresh", scale, fontSize: 14);
        _refreshButton.CustomMinimumSize = new Vector2((int)(100 * scale), 0);
        _refreshButton.Pressed += RefreshLocal;
        actionRow.AddChild(_refreshButton);

        var localScroll = new ScrollContainer();
        localScroll.SizeFlagsVertical = SizeFlags.ExpandFill;
        localScroll.CustomMinimumSize = new Vector2(0, (int)(220 * scale));
        _localPane.AddChild(localScroll);

        _listContainer = new VBoxContainer();
        _listContainer.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _listContainer.AddThemeConstantOverride("separation", (int)(6 * scale));
        localScroll.AddChild(_listContainer);

        // --- DOWNLOADS pane --------------------------------------------------
        _downloadsPane = new WorkshopDownloadsPane(scale);
        AddChild(_downloadsPane);

        SelectTab(TabLocal);
    }

    // Injects the launcher's session/connection so the Workshop tabs can issue
    // PublishedFile RPCs. Called once from LauncherController.Start() — see
    // LauncherModel.Connection for why this doesn't hold the SteamConnection
    // itself (it may not exist yet on the fast/ReadyToLaunch path).
    public void Configure(LauncherModel model) => _model = model;

    // True while the Workshop download queue has queued/in-flight items. Used by
    // the Back handler to warn before leaving (leaving cancels the download).
    public bool HasActiveDownload
    {
        get
        {
            lock (_queueLock)
                return _queue?.IsBusy == true;
        }
    }

    public void CancelDownloads()
    {
        lock (_queueLock)
            _queue?.CancelAll();
    }

    // Called by LauncherView.ShowModManager() every time the hub is opened.
    // Re-activates whichever tab is currently selected (LOCAL always rescans;
    // WORKSHOP/SUBSCRIBED/DOWNLOADS re-check the session and refresh).
    public void Refresh() => SelectTab(_activeTab);

    private static readonly string[] TabLogNames = { "WORKSHOP", "SUBSCRIBED", "LOCAL", "DOWNLOADS" };

    private void SelectTab(int index)
    {
        PatchHelper.Log($"[Mods] Tab -> {(index >= 0 && index < TabLogNames.Length ? TabLogNames[index] : index.ToString())}");
        _activeTab = index;
        for (int i = 0; i < _tabButtons.Length; i++)
        {
            _tabButtons[i].SetPressedNoSignal(i == index);
            ApplyTabStyle(_tabButtons[i], i == index);
        }
        _workshopPane.Visible = index == TabWorkshop;
        _subscribedPane.Visible = index == TabSubscribed;
        _localPane.Visible = index == TabLocal;
        _downloadsPane.Visible = index == TabDownloads;

        switch (index)
        {
            case TabWorkshop:
                _workshopPane.Activate(EnsureSessionAsync);
                break;
            case TabSubscribed:
                _subscribedPane.Activate(EnsureSessionAsync);
                break;
            case TabLocal:
                RefreshLocal();
                break;
            case TabDownloads:
                _downloadsPane.RenderFromQueue();
                break;
        }
    }

    // Material-style tab state: active = accent underline + primary text,
    // inactive = plain secondary text.
    private void ApplyTabStyle(Button button, bool active)
    {
        StyleBoxFlat Make()
        {
            var box = new StyleBoxFlat { BgColor = Colors.Transparent };
            if (active)
            {
                box.BorderColor = Ui.Accent;
                box.BorderWidthBottom = Math.Max(2, (int)(3 * _scale));
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

    // Ensures the launcher's Steam session is connected and logged in, then lazily
    // creates the single WorkshopDownloadQueue shared by all Workshop tabs on first
    // success. Safe to call from any thread (Godot node touches are deferred).
    private async Task<(bool ok, SteamConnection conn)> EnsureSessionAsync()
    {
        if (_model == null)
            return (false, null);

        await _model.EnsureConnectedAsync().ConfigureAwait(false);
        if (_model.SessionState != SessionState.LoggedIn || _model.Connection == null)
            return (false, null);

        // Keep the connection warm while the Mod Hub is open. Without this the
        // 30s idle timeout drops the session between tab switches, so every tab
        // change reconnects (log churn + a stuck "Connecting..." on return). We
        // suspend the idle timer on first successful connect and resume it when
        // the hub closes (NotifyClosed).
        if (!_idleSuspended)
        {
            _idleSuspended = true;
            _model.Connection.SuspendIdleTimeout();
        }

        lock (_queueLock)
        {
            if (_queue == null)
            {
                // Nothing can be in flight at queue creation, so anything left in
                // the staging dir is a leftover from a killed session — clear it.
                WorkshopInstaller.CleanStaleDownloads();
                var q = new WorkshopDownloadQueue(_model.Connection);
                q.Changed += OnQueueChanged;
                _queue = q;
                Callable
                    .From(() =>
                    {
                        _downloadsPane.SetQueue(_queue);
                        _subscribedPane.SetQueue(_queue);
                        _workshopPane.SetQueue(_queue);
                    })
                    .CallDeferred();
            }
        }
        return (true, _model.Connection);
    }

    // Called when the Mod Hub is closed (BACK). Resumes the idle timeout that was
    // suspended while browsing, and restores landscape if the user rotated.
    public void NotifyClosed()
    {
        if (_idleSuspended && _model?.Connection != null)
        {
            _model.Connection.ResumeIdleTimeout();
            _idleSuspended = false;
        }
        if (_portrait)
        {
            _portrait = false;
            OrientationChangeRequested?.Invoke(false);
        }
    }

    private void OnOrientToggle()
    {
        _portrait = !_portrait;
        PatchHelper.Log($"[Mods] Orientation -> {(_portrait ? "portrait" : "landscape")}");
        _orientButton.Text = _portrait ? Loc.Tr("⤢ 가로", "⤢ Landscape") : Loc.Tr("⤢ 세로", "⤢ Portrait");
        OrientationChangeRequested?.Invoke(_portrait);
    }

    private void BeginBusy(string message)
    {
        _busy?.Dismiss();
        _busy = BusyOverlay.Show(this, message, _scale);
    }

    private void EndBusy()
    {
        _busy?.Dismiss();
        _busy = null;
    }

    // WorkshopDownloadQueue.Changed fires from its worker's pool thread. Progress
    // events arrive up to ~4/s; re-rendering the SUBSCRIBED list re-scans external
    // storage each time (log spam + IO), so mid-download refreshes are gated to
    // one per 600ms. Terminal transitions (queue went idle) always render so the
    // final state is never missed.
    private long _lastQueueUiTick;

    private void OnQueueChanged()
    {
        bool busy;
        lock (_queueLock)
            busy = _queue?.IsBusy == true;

        var now = System.Environment.TickCount64;
        if (busy && now - _lastQueueUiTick < 600)
            return;
        _lastQueueUiTick = now;

        Callable
            .From(() =>
            {
                _downloadsPane.RenderFromQueue();
                if (_subscribedPane.Visible)
                    _subscribedPane.RenderList();
                if (_workshopPane.Visible)
                    _workshopPane.NotifyInstallsChanged();
            })
            .CallDeferred();
    }

    // --- LOCAL tab ---------------------------------------------------------

    private void RefreshLocal()
    {
        ClearList();

        if (!AppPaths.HasStoragePermission())
        {
            SetStatus(
                Loc.Tr("모드를 관리하려면 저장소 권한이 필요합니다.","Storage permission is required to manage mods."),
                WarnColor
            );
            _permissionButton.Visible = true;
            _importButton.Disabled = true;
            return;
        }

        _permissionButton.Visible = false;
        _importButton.Disabled = _importInFlight;
        AppPaths.EnsureExternalDirectories();

        var scanned = ModScanner.Scan();
        var cfg = ModConfig.Load();
        // Reconcile keeps the registry (mod_config.json) in sync with what's on
        // disk — including deriving each entry's disabled state from which root
        // (Mods/ vs ModsDisabled/) its folder lives in. Disk is the truth.
        cfg.Reconcile(scanned);

        var localInfos = scanned
            .Where(m =>
            {
                var entry = cfg.Get(m.Id);
                return entry == null || !entry.IsWorkshop;
            })
            .OrderBy(m => m.Disabled)
            .ThenBy(m => m.Manifest.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var rootManifests = ScanRootLevelManifests();

        if (localInfos.Count == 0 && rootManifests.Count == 0)
        {
            SetStatus("", InfoColor);
            _listContainer.AddChild(
                Ui.MakeEmptyState(
                    null,
                    Loc.Tr("설치된 로컬 모드가 없습니다.","No local mods installed."),
                    Loc.Tr("\"Import Mod (.zip)\"를 누르거나 WORKSHOP 탭에서 구독하세요.","Tap \"Import Mod (.zip)\", or subscribe on the WORKSHOP tab."),
                    _scale
                )
            );
            return;
        }

        SetStatus(Loc.Tr($"로컬 모드 {localInfos.Count}개 설치됨.",$"{localInfos.Count} local mod(s) installed."), InfoColor);

        var gameVersion = TryReadGameVersion();
        foreach (var info in localInfos)
        {
            string warning = null;
            if (
                !string.IsNullOrWhiteSpace(info.Manifest.MinGameVersion)
                && gameVersion != null
                && CompareVersions(info.Manifest.MinGameVersion, gameVersion) > 0
            )
                warning = $"Requires game {info.Manifest.MinGameVersion}+";

            var row = new ModListRow(info, _scale, badge: info.Disabled ? "Disabled" : null);
            var capturedInfo = info;
            var capturedWarning = warning;
            row.DetailRequested += () => ShowLocalDetail(capturedInfo, capturedWarning, removable: true);
            _listContainer.AddChild(row);
        }

        foreach (var (manifest, path) in rootManifests)
        {
            var info = new ModEntryInfo
            {
                Path = path,
                TopLevelDir = null,
                Manifest = manifest,
                ReadmeSnippet = null,
            };
            var row = new ModListRow(info, _scale, badge: "Unmanaged — root files");
            var capturedInfo = info;
            row.DetailRequested += () => ShowLocalDetail(capturedInfo, null, removable: false);
            _listContainer.AddChild(row);
        }
    }

    // Full detail page for a local mod, opened by tapping its row.
    private void ShowLocalDetail(ModEntryInfo info, string warning, bool removable)
    {
        PatchHelper.Log($"[Mods] LOCAL row tapped -> detail: '{info.Id}' (disabled={info.Disabled})");
        var m = info.Manifest;
        var subtitle = string.Join(
            " · ",
            new[]
            {
                string.IsNullOrWhiteSpace(m.Author) ? null : "by " + m.Author,
                string.IsNullOrWhiteSpace(m.Version) ? null : LauncherModel.VersionLabel(m.Version),
            }.Where(s => s != null)
        );

        var body = m.Description ?? "";
        if (!string.IsNullOrWhiteSpace(info.ReadmeSnippet))
            body = (body.Length > 0 ? body + "\n\n" : "") + "README: " + info.ReadmeSnippet;

        var facts = new List<(string, string)>
        {
            ("Min game version", m.MinGameVersion),
            ("Path", info.Path),
        };

        var dialog = new ModDetailDialog(
            m.DisplayName,
            subtitle,
            warning,
            body,
            facts,
            _scale,
            actionLabel: removable ? "Remove Mod" : null,
            actionCallback: removable ? () => OnRowRemovePressed(info) : null,
            actionDanger: true,
            action2Label: removable ? (info.Disabled ? "Enable" : "Disable") : null,
            action2Callback: removable ? () => OnLocalStashTogglePressed(info) : null
        );
        LauncherOverlay.Show(this, dialog);
    }

    // Stash toggle for a local mod: same folder-move mechanism as workshop mods.
    private void OnLocalStashTogglePressed(ModEntryInfo info)
    {
        bool wasDisabled = info.Disabled;
        BeginBusy(
            wasDisabled
                ? Loc.Tr($"'{info.Id}' 활성화 중…", $"Enabling '{info.Id}'…")
                : Loc.Tr($"'{info.Id}' 비활성화 중…", $"Disabling '{info.Id}'…")
        );
        var (ok, error) = wasDisabled ? ModStasher.Enable(info) : ModStasher.Disable(info);
        EndBusy();
        SetStatus(
            ok
                ? (
                    wasDisabled
                        ? Loc.Tr($"'{info.Id}' 활성화됨.", $"'{info.Id}' enabled.")
                        : Loc.Tr($"'{info.Id}' 비활성화됨(보관).", $"'{info.Id}' disabled (stashed).")
                )
                : error,
            ok ? InfoColor : WarnColor
        );
        RefreshLocal();
    }

    // Root-level "*.json" manifests directly under Mods/ (not inside a folder) are
    // loaded by the game but have no folder the launcher can delete — ModScanner
    // only logs a warning for these (WarnRootLevelManifests); this mirrors that
    // scan to surface them as read-only rows instead.
    private static List<(ModManifest Manifest, string Path)> ScanRootLevelManifests()
    {
        var result = new List<(ModManifest, string)>();
        try
        {
            foreach (var json in Directory.GetFiles(AppPaths.ExternalModsDir, "*.json"))
            {
                if (
                    string.Equals(
                        Path.GetFileName(json),
                        "mod_config.json",
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                    continue;

                var m = ModManifest.TryParse(json);
                if (m != null && m.IsValid())
                    result.Add((m, json));
            }
        }
        catch { }
        return result;
    }

    // Reads the currently downloaded game's version straight from
    // <DataDir>/game/release_info.json — the same file ReleaseInfoPatches falls
    // back to for the game's own version display. No game-assembly dependency.
    private static string TryReadGameVersion()
    {
        try
        {
            var path = Path.Combine(OS.GetDataDir(), "game", "release_info.json");
            if (!File.Exists(path))
                return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("version", out var v))
                return v.GetString();
        }
        catch { }
        return null;
    }

    // Dotted-numeric version comparison with a graceful fallback (non-numeric
    // segments compare as 0) — good enough for a "requires game X+" warning badge.
    private static int CompareVersions(string a, string b)
    {
        try
        {
            var pa = a.Split('.').Select(s => int.TryParse(s, out var n) ? n : 0).ToArray();
            var pb = b.Split('.').Select(s => int.TryParse(s, out var n) ? n : 0).ToArray();
            var len = Math.Max(pa.Length, pb.Length);
            for (int i = 0; i < len; i++)
            {
                var na = i < pa.Length ? pa[i] : 0;
                var nb = i < pb.Length ? pb[i] : 0;
                if (na != nb)
                    return na.CompareTo(nb);
            }
            return 0;
        }
        catch
        {
            return 0;
        }
    }

    private void ClearList()
    {
        for (int i = _listContainer.GetChildCount() - 1; i >= 0; i--)
        {
            var child = _listContainer.GetChild(i);
            _listContainer.RemoveChild(child);
            child.QueueFree();
        }
    }

    private void OnRowRemovePressed(ModEntryInfo info)
    {
        var id = info.Id;
        var topLevelDir = info.TopLevelDir;
        ConfirmationRequested?.Invoke(
            Loc.Tr(
                $"'{info.Manifest.DisplayName}'을(를) 삭제할까요?\n저장소에서 모드 폴더가 삭제됩니다.",
                $"Remove '{info.Manifest.DisplayName}'?\nThis deletes the mod folder from storage."
            ),
            () =>
            {
                BeginBusy(Loc.Tr($"'{id}' 삭제 중…", $"Removing '{id}'…"));
                bool ok = ModImporter.DeleteMod(topLevelDir, id);
                EndBusy();
                SetStatus(
                    ok
                        ? Loc.Tr($"{id} 삭제됨.", $"Removed {id}.")
                        : Loc.Tr($"{id} 삭제 실패.", $"Failed to remove {id}."),
                    ok ? InfoColor : ErrorColor
                );
                RefreshLocal();
            },
            null
        );
    }

    private void OnGrantPermissionPressed()
    {
        AppPaths.RequestStoragePermission();
        SetStatus(
            Loc.Tr("권한을 허용한 뒤 여기로 돌아와 Refresh 를 누르세요.","After granting permission, return here and tap Refresh."),
            WarnColor
        );
    }

    private void OnImportPressed()
    {
        if (_importInFlight)
            return;
        PatchHelper.Log("[Mods] Import button tapped");
        _importInFlight = true;
        _importButton.Disabled = true;
        SetStatus(Loc.Tr("파일 선택기 여는 중…","Opening file picker…"), InfoColor);

        // Run the whole import pipeline on the thread pool to avoid Godot's
        // SynchronizationContext being disrupted by the SAF picker's OnPause/OnResume.
        // Any UI touches inside the pipeline must go through SetStatus/FinishImport
        // (which CallDeferred onto the main thread).
        _ = Task.Run(RunImportPipelineAsync);
    }

    private async Task RunImportPipelineAsync()
    {
        try
        {
            PatchHelper.Log("[Mods] RunImportPipelineAsync started");
            string[] zipPaths;
            try
            {
                zipPaths = await SafBridge
                    .PickZipsToCacheAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                PatchHelper.Log($"[Mods] SAF pick failed: {ex}");
                FinishImport("Import failed: " + ex.Message, error: true, refresh: false);
                return;
            }

            PatchHelper.Log(
                $"[Mods] SAF returned {(zipPaths == null ? "null" : zipPaths.Length.ToString())} path(s)"
            );

            if (zipPaths == null || zipPaths.Length == 0)
            {
                FinishImport("Import cancelled.", error: false, refresh: false);
                return;
            }

            PatchHelper.Log($"[Mods] Starting sequential import of {zipPaths.Length} file(s)");
            await ImportSequentially(zipPaths, 0, imported: 0, failed: 0).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Mods] RunImportPipelineAsync fatal: {ex}");
            FinishImport("Import failed: " + ex.Message, error: true, refresh: false);
        }
    }

    private async Task ImportSequentially(string[] zipPaths, int index, int imported, int failed)
    {
        PatchHelper.Log($"[Mods] ImportSequentially enter index={index}/{zipPaths.Length}");
        if (index >= zipPaths.Length)
        {
            var msg =
                zipPaths.Length == 1
                    ? (imported == 1 ? $"Imported 1 mod." : "Import failed.")
                    : $"Imported {imported} / {zipPaths.Length} mod(s)"
                        + (failed > 0 ? $" ({failed} failed)." : ".");
            FinishImport(msg, error: imported == 0, refresh: imported > 0);
            return;
        }

        var zipPath = zipPaths[index];
        SetStatus(Loc.Tr($"가져오는 중 {index + 1}/{zipPaths.Length}…",$"Importing {index + 1}/{zipPaths.Length}…"), InfoColor);

        try
        {
            PatchHelper.Log($"[Mods] ImportZipAsync start: {zipPath}");
            var result = await ModImporter.ImportZipAsync(zipPath, overwrite: false);
            PatchHelper.Log(
                $"[Mods] ImportZipAsync done: success={result.Success} exists={result.AlreadyExists} id={result.ModId} err={result.Error}"
            );
            if (result.AlreadyExists)
            {
                var idx = index;
                var imp = imported;
                var fail = failed;
                // ConfirmationRequested creates a Godot Dialog; the subscriber is on the
                // main thread, so dispatch the invocation there explicitly. The confirm
                // callbacks continue the import on the thread pool again.
                Callable
                    .From(() =>
                    {
                        ConfirmationRequested?.Invoke(
                            Loc.Tr($"'{result.ModId}'은(는) 이미 설치되어 있습니다. 덮어쓸까요?",$"'{result.ModId}' is already installed. Overwrite?"),
                            () =>
                                _ = Task.Run(async () =>
                                {
                                    var overwritten = await ModImporter.ImportZipAsync(
                                        zipPath,
                                        overwrite: true
                                    );
                                    if (overwritten.Success)
                                        imp++;
                                    else
                                        fail++;
                                    await ImportSequentially(zipPaths, idx + 1, imp, fail);
                                }),
                            () =>
                                _ = Task.Run(async () =>
                                {
                                    ModImporter.CleanupImportZip(zipPath);
                                    await ImportSequentially(zipPaths, idx + 1, imp, fail + 1);
                                })
                        );
                    })
                    .CallDeferred();
                return;
            }

            if (result.Success)
                imported++;
            else
                failed++;
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Mods] Import exception for {zipPath}: {ex}");
            failed++;
        }

        await ImportSequentially(zipPaths, index + 1, imported, failed);
    }

    private void FinishImport(string message, bool error, bool refresh)
    {
        SetStatus(message, error ? ErrorColor : InfoColor);
        _importInFlight = false;
        Callable
            .From(() =>
            {
                _importButton.Disabled = false;
                if (refresh)
                    RefreshLocal();
            })
            .CallDeferred();
    }

    // Marshals to the Godot main thread because import continuations may resume
    // on the thread pool after SAF picker round-trip.
    private void SetStatus(string text, Godot.Color color)
    {
        Callable
            .From(() =>
            {
                _statusLabel.Text = text;
                _statusLabel.AddThemeColorOverride("font_color", color);
            })
            .CallDeferred();
    }
}
