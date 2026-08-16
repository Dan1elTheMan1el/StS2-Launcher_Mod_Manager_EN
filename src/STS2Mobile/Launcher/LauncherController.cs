using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using STS2Mobile.Debug;
using STS2Mobile.Launcher.Components;
using STS2Mobile.Patches;
using STS2Mobile.Steam;

namespace STS2Mobile.Launcher;

// Wires model events to view updates and handles the launcher UI state machine.
// All model callbacks are marshalled to the main thread before updating the view.
public class LauncherController
{
    private readonly LauncherModel _model;
    private readonly LauncherView _view;
    private readonly Action<Action> _runOnMainThread;
    private volatile bool _checkingForGameUpdate;
    private volatile bool _checkingForLauncherUpdate;
    private bool _launchStageShown;
    private string _lastLaunchText = "LAUNCH";
    private bool _lastShowCloudSync;
    private bool _lastShowUpdate;

    // Issue #45: Marked true when passing picked != current branch check in OnCheckGameUpdatePressed,
    // consumed in DownloadCompleted callback. If true, sets NeedsRestartAfterBranchSwitch
    // → Play button text branches to "App restart required".
    private bool _pendingBranchSwitch;

    // Reentrancy guard shared by every handler that touches local saves or
    // Steam Cloud (Save Manager, Local Backup, Push, Pull) — all of them
    // toggle the global UserDataPathProvider.IsRunningModded, so two of them
    // running at once risks one seeing the other's mid-flip mod state. A
    // device log caught the Save Manager button re-tapped while its own
    // KeepCloud apply was still mid-file-pull (SetSyncBusy didn't cover that
    // button — see LauncherView.SetCloudOpBusy). Checked-and-set as the very
    // first thing each handler's actual work does; disabling the buttons via
    // SetCloudOpBusy is the visible half of the same guard, this bool is the
    // backstop that doesn't depend on Godot's disabled-button-blocks-signal
    // timing.
    private bool _cloudOpInProgress;

    public LauncherController(
        LauncherModel model,
        LauncherView view,
        Action<Action> runOnMainThread
    )
    {
        _model = model;
        _view = view;
        _runOnMainThread = runOnMainThread;
    }

    public void Start()
    {
        _model.SessionStateChanged += s => _runOnMainThread(() => UpdateUI(s));
        _model.LogReceived += msg => _runOnMainThread(() => _view.AppendLog(msg));
        PatchHelper.LogEmitted += msg =>
        {
            if (msg.StartsWith("[Cloud]"))
                _runOnMainThread(() => _view.AppendLog(msg));
        };
        _model.CodeNeeded += wasIncorrect =>
            _runOnMainThread(() =>
            {
                _view.Login.Visible = false;
                _view.Code.Show(wasIncorrect);
            });
        _model.DownloadProgressChanged += p =>
            _runOnMainThread(() =>
            {
                _view.Download.SetProgress(
                    p.Percentage,
                    $"{LauncherModel.FormatSize(p.DownloadedBytes)} / {LauncherModel.FormatSize(p.TotalBytes)} ({p.Percentage:F1}%)"
                );
                _view.AppendLog(p.CurrentFile);
            });
        _model.DownloadLogReceived += msg => _runOnMainThread(() => _view.AppendLog(msg));
        _model.DownloadCompleted += () =>
            _runOnMainThread(() =>
            {
                bool wasBranchSwitch = _pendingBranchSwitch;
                _view.SetStatus("Download complete! Restart to play.");
                _view.Download.Visible = false;
                // Issue #45: If download completes right after a branch switch, there is a risk of
                // mismatch with dst dll — explicit restart rather than Play is the only safe path.
                if (_pendingBranchSwitch)
                {
                    _pendingBranchSwitch = false;
                    _model.NeedsRestartAfterBranchSwitch = true;
                    PatchHelper.Log(
                        "[Launcher] Branch-switch download complete — flagging restart"
                    );
                }

                // Issue #53: If an in-session same-branch update completes while booted into the game PCK,
                // the process is the old sts2.dll and the disk is the new PCK — mixing old assembly/new PCK
                // on in-process PLAY. Branch switches are handled above, so here we do pure
                // updates: auto-restart only if actually booted into the game PCK (InGameMode) and
                // the assembly was actually replaced. First installation (bootstrap, InGameMode=false)
                // keeps the existing RESTART APP flow.
                if (!wasBranchSwitch && _model.InGameMode && LauncherModel.GameAssemblyReplaced())
                {
                    PatchHelper.Log(
                        "[Launcher] In-session update replaced game assembly — auto-restarting"
                    );
                    PromptUpdateRestart();
                    return;
                }

                if (LauncherModel.GameFilesReady())
                {
                    var text = ResolveLaunchButtonText();
                    ShowLaunchStage(text, showCloudSync: false, showUpdate: false);
                }
                else
                    _view.Actions.ShowRetry();
            });
        _model.DownloadFailed += msg =>
            _runOnMainThread(() =>
            {
                if (msg == null)
                {
                    _view.Download.Reset();
                    return;
                }
                _view.SetStatus($"Download failed: {msg}");
                _view.Download.Reset("RETRY DOWNLOAD");
            });
        _model.DownloadCancelled += () =>
            _runOnMainThread(() =>
            {
                _view.SetStatus("Download cancelled");
                _view.Download.SetButtonDisabled(false);
            });
        _model.UpdateCheckCompleted += hasUpdate =>
            _runOnMainThread(() =>
            {
                if (hasUpdate)
                {
                    _view.Actions.HideAll();
                    _view.Download.Visible = true;
                    _view.Download.Reset("UPDATE GAME FILES");
                    _view.SetStatus("Update available!");
                }
                else
                {
                    _view.Actions.SetGameUpdateButtonText("UP TO DATE");
                }
            });
        _model.UpdateCheckFailed += msg =>
            _runOnMainThread(() =>
            {
                _view.Actions.SetGameUpdateButtonText("CHECK FAILED");
                _view.Actions.SetGameUpdateButtonDisabled(false);
                _view.AppendLog($"Update check failed: {msg}");
            });

        _view.Login.LoginRequested += OnLoginPressed;
        _view.Code.CodeSubmitted += OnCodeSubmitPressed;
        _view.Download.DownloadRequested += OnDownloadPressed;
        _view.Actions.LaunchPressed += OnLaunchPressed;
        _view.Actions.RetryPressed += OnRetryPressed;
        _view.Actions.LocalBackupPressed += OnLocalBackupPressed;
        _view.Actions.CloudSyncToggled += OnCloudSyncToggled;
        _view.Actions.CloudPushPressed += OnCloudPushPressed;
        _view.Actions.CloudPullPressed += OnCloudPullPressed;
        _view.Actions.CheckGameUpdatePressed += OnCheckGameUpdatePressed;
        _view.Actions.CheckLauncherUpdatePressed += OnCheckLauncherUpdatePressed;
        _view.ModManagerButton.Pressed += OnModManagerPressed;
        _view.ModsButton.Pressed += OnModsPressed;
        _view.ModManager.BackPressed += OnModManagerBackPressed;
        _view.ModManager.OrientationChangeRequested += portrait =>
            _view.SetModHubOrientation(portrait);
        // Issue #58 phase 4b: the Mod Hub's Workshop/Subscribed/Downloads tabs need
        // the launcher's SteamConnection + session state to issue PublishedFile RPCs.
        _view.ModManager.Configure(_model);
        _view.DebugButton.Pressed += OnDebugTogglePressed;
        UpdateDebugButtonLabel();

        // Issue #36 Part A: Local Backup is no longer a persisted toggle —
        // there's nothing to restore on boot. It's a one-shot action button
        // (OnLocalBackupPressed) that snapshots the save tree on demand.
        // Always ensure the external StS2LauncherMM/{Mods,Saves} tree exists when
        // the user has granted storage permission — the Mods directory in
        // particular is needed for ModLoaderPatches to find user-installed mods,
        // independently of the Local Backup toggle. Internally a no-op when
        // permission isn't granted yet.
        AppPaths.EnsureExternalDirectories();
        _view.Actions.SetCloudSyncChecked(LauncherModel.LoadCloudSyncPref());

        var result = _model.StartSession();
        HandleFastPath(result);
        MaybePromptStoragePermission();
    }

    // Re-prompt every launch until storage permission is actually granted.
    // Mods, save backup, and debug logs all live under
    // /storage/emulated/0/StS2LauncherMM/, so a stuck-on-no state silently
    // breaks half the launcher. The previous one-time marker meant a single
    // misclick on Cancel left the user permanently locked out with no way
    // back from inside the launcher.
    private void MaybePromptStoragePermission()
    {
        if (AppPaths.HasStoragePermission())
            return;

        _view.ShowConfirmation(
            "Allow 'All Files Access'?\n\nNeeded for installing mods, saving local game backups, and writing debug logs under /storage/emulated/0/StS2LauncherMM/.\n\nIf you cancel, this prompt will appear again on the next launch.",
            onConfirmed: AppPaths.RequestStoragePermission,
            onCancelled: null
        );
    }

    private void HandleFastPath(FastPathResult result)
    {
        PatchHelper.Log($"[Mods] HandleFastPath result={result}");
        switch (result)
        {
            case FastPathResult.ReadyToLaunch:
                // issue #59 — expired saved token: a boot-time choice dialog
                // (re-login vs continue offline), exactly once per app launch since
                // the fast path runs once. An earlier draft revealed the login
                // form next to the launch stage instead, but the login form
                // has no PLAY button — the mixed stage read as broken UI
                // (owner feedback). Offline choice (or Back) proceeds to the
                // normal launch stage; auth-gated features are then blocked
                // with a restart notice (BlockIfTokenExpired) for the rest of
                // the session.
                if (_model.SavedTokenExpired)
                {
                    ShowTokenExpiredChoice();
                    break;
                }
                _view.SetStatus(
                    _model.SavedTokenExpiringSoon
                        ? $"Welcome back, {_model.AccountName} (Steam login expiring soon — re-login recommended)"
                        : $"Welcome back, {_model.AccountName}"
                );
                var text = ResolveLaunchButtonText();
                ShowLaunchStage(text, showCloudSync: true, showUpdate: true);
                break;

            case FastPathResult.AutoConnect:
                _model.Connect();
                StartConnectionTimeout();
                break;

            case FastPathResult.ShowLogin:
                ShowLoginStage("Enter your Steam credentials");
                break;
        }
    }

    private void ShowLoginStage(string status)
    {
        _view.SetStatus(status);
        _view.Login.Visible = true;
        _view.Login.SetDisabled(false);
    }

    private void ShowLaunchStage(string text, bool showCloudSync, bool showUpdate)
    {
        PatchHelper.Log(
            $"[Mods] ShowLaunchStage fired (text='{text}', inGameMode={_model.InGameMode})"
        );
        var firstShow = !_launchStageShown;
        _launchStageShown = true;
        _lastLaunchText = text;
        _lastShowCloudSync = showCloudSync;
        _lastShowUpdate = showUpdate;
        _view.Actions.ShowLaunch(text, showCloudSync, showUpdate);
        _view.ModManagerButton.Visible = true;
        _view.ModsButton.Visible = true;

        // Kick off the launcher self-update check the first time we land on the
        // launch stage. Only once per session, silent if already on latest.
        if (firstShow && showUpdate && !_autoUpdateChecked)
        {
            _autoUpdateChecked = true;
            _ = AutoCheckLauncherUpdateOnStartup();
        }

        if (firstShow)
            DispatchDebugIntents();
    }

    // Debug-only: GodotApp.java drops marker files when started with
    // `adb shell am start --es debug_force_<dialog> 1` (only on -debug builds).
    // Convert them into real dialog calls so we can verify UI / English copy /
    // marker extraction without round-tripping through GitHub or Steam.
    private void DispatchDebugIntents()
    {
        try
        {
            var dataDir = OS.GetDataDir();
            var updateMarker = Path.Combine(dataDir, ".debug_force_update_dialog");
            if (File.Exists(updateMarker))
            {
                var lines = File.ReadAllLines(updateMarker);
                var fakeVersion = lines.Length > 0 ? lines[0] : "0.0.0";
                var fakeBody =
                    lines.Length > 1 ? string.Join("\n", lines, 1, lines.Length - 1) : "";
                var fakeNotes = ReleaseNotes.ExtractDialogBody(fakeBody);
                var fakeResult = new AppUpdateResult(
                    fakeVersion,
                    "https://example.invalid/fake.apk",
                    fakeNotes
                );
                File.Delete(updateMarker);
                PatchHelper.Log("[Debug] Forcing PromptLauncherUpdate via debug intent");
                PromptLauncherUpdate(fakeResult);
            }
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Debug] DispatchDebugIntents failed: {ex.Message}");
        }
    }

    private bool _autoUpdateChecked;

    // Repurposed in 0.3.0 to open the Save Sync dialog instead of the WIP mod
    // manager screen. That screen is now the Mod Hub, reachable via its own
    // button (OnModsPressed, issue #58).
    private async void OnModManagerPressed()
    {
        if (_cloudOpInProgress)
            return;
        _cloudOpInProgress = true;

        PatchHelper.Log("[Mods] Save Manager button tapped");
        _view.SetCloudOpBusy(true);
        _view.SetStatus("Save Manager");
        try
        {
            await LauncherPatches.OpenSaveSyncDialogAsync(_view.RootControl);
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Cloud] Save Manager error: {ex.Message}");
        }
        finally
        {
            _view.SetCloudOpBusy(false);
            _cloudOpInProgress = false;
        }
    }

    // Issue #58: the original mod-manager navigation, revived as the Mod Hub
    // entry point (its own button — SAVE MANAGER above keeps its 0.3.0 role).
    private void OnModsPressed()
    {
        if (BlockIfTokenExpired())
            return;
        PatchHelper.Log("[Mods] Mod Manager button tapped");
        _view.SetStatus("Mod Manager");
        _view.ShowModManager();
    }

    public void OnModManagerBackPressed()
    {
        // Leaving the Mod Hub tears down the download queue's session, cancelling
        // any in-flight Workshop download. Warn first so the user doesn't lose a
        // download to a stray Back press.
        if (_view.ModManager.HasActiveDownload)
        {
            _view.ShowConfirmation(
                "A Workshop download is still in progress. Leaving the Mod Manager will "
                    + "cancel it. Leave anyway?",
                onConfirmed: () =>
                {
                    _view.ModManager.CancelDownloads();
                    CloseModManager();
                },
                onCancelled: null,
                okLabel: "Leave",
                cancelLabel: "Stay"
            );
            return;
        }
        CloseModManager();
    }

    private void CloseModManager()
    {
        PatchHelper.Log(
            $"[Mods] Back pressed (launchStageShown={_launchStageShown}, sessionState={_model.SessionState})"
        );
        // Resume the Steam idle timeout suspended while the hub was open.
        _view.ModManager.NotifyClosed();
        // Must hide mod manager first, otherwise UpdateUI's ModManager.Visible guard
        // refuses to redraw — that was making BACK a no-op.
        _view.HideModManager();
        _view.ModManagerButton.Visible = false;
        _view.ModsButton.Visible = false;

        // Fast path (ReadyToLaunch) shows the launch UI without changing SessionState,
        // so we can't rely on SessionState==LoggedIn to know if we were on the launch screen.
        if (_launchStageShown)
        {
            _view.SetStatus($"Welcome back, {_model.AccountName}");
            ShowLaunchStage(_lastLaunchText, _lastShowCloudSync, _lastShowUpdate);
        }
        else
        {
            ShowLoginStage("Enter your Steam credentials");
        }
    }

    public bool IsModManagerOpen => _view.ModManager.Visible;

    private async void StartConnectionTimeout()
    {
        await Task.Delay(10000);

        if (_model.ConnectionResolved)
            return;

        var state = _model.SessionState;
        if (
            state
            is SessionState.Connecting
                or SessionState.Authenticating
                or SessionState.VerifyingOwnership
        )
        {
            if (_model.HasOwnershipMarker() && LauncherModel.GameFilesReady())
            {
                _runOnMainThread(() =>
                {
                    _view.SetStatus("No connection — saved credentials will be used");
                    _view.AppendLog("Connection timed out. Valid ownership marker found.");
                    var text = ResolveLaunchButtonText();
                    ShowLaunchStage(text, showCloudSync: true, showUpdate: false);
                });
            }
            else
            {
                _runOnMainThread(() =>
                {
                    _view.SetStatus("Connection failed. Internet required for first launch.");
                    _view.Actions.ShowRetry();
                });
            }
        }
    }

    // Updates visible sections and status text based on session state transitions.
    private void UpdateUI(SessionState state)
    {
        if (
            _model.AwaitingCode
            && state
                is SessionState.Connecting
                    or SessionState.WaitingForCredentials
                    or SessionState.Authenticating
        )
            return;

        if (_checkingForGameUpdate)
            return;

        // After successful login, ignore session disconnects — cloud ops use
        // their own token-based connections, so the launcher session dropping is expected.
        if (state == SessionState.Disconnected && _model.ConnectionResolved)
            return;

        if (_view.ModManager.Visible)
            return;

        _view.HideAllSections();

        switch (state)
        {
            case SessionState.Connecting:
                _view.SetStatus("Connecting to Steam...");
                break;

            case SessionState.WaitingForCredentials:
                ShowLoginStage("Enter your Steam credentials");
                break;

            case SessionState.Authenticating:
                _view.SetStatus("Authenticating...");
                break;

            case SessionState.VerifyingOwnership:
                _view.SetStatus("Verifying game ownership...");
                break;

            case SessionState.LoggedIn:
                _model.ConnectionResolved = true;
                _view.SetStatus($"Logged in as {_model.AccountName}");
                if (LauncherModel.GameFilesReady())
                {
                    var text = ResolveLaunchButtonText();
                    ShowLaunchStage(text, showCloudSync: true, showUpdate: true);
                }
                else
                {
                    _view.Download.Visible = true;
                    _view.Download.SetButtonDisabled(false);
                }
                break;

            case SessionState.Failed:
                _model.ConnectionResolved = true;
                ShowLoginStage($"Error: {_model.FailReason}");
                break;

            case SessionState.Disconnected:
                ShowLoginStage("Enter your Steam credentials");
                break;
        }
    }

    private async void OnLoginPressed(string username, string password)
    {
        _view.Login.SetDisabled(true);
        _view.Login.ClearPassword();
        await _model.LoginAsync(username, password);
    }

    private void OnCodeSubmitPressed(string code)
    {
        _view.SetStatus("Verifying code...");
        _model.SubmitCode(code);
    }

    private async void OnDownloadPressed()
    {
        _view.Download.ShowProgress("Loading branches...");

        System.Collections.Generic.List<SteamBranchInfo> branches;
        try
        {
            branches = await _model.ListBranchesAsync();
        }
        catch (Exception ex)
        {
            _view.AppendLog($"Branch list failed: {ex.Message}");
            _view.Download.Reset();
            return;
        }

        var current = LauncherModel.LoadSelectedBranch();
        string picked;
        if (branches.Count <= 1)
        {
            picked = branches.Count == 1 ? branches[0].Name : "public";
        }
        else
        {
            picked = await ShowBranchPickerAsync(branches, current);
            if (picked == null)
            {
                _view.Download.Reset();
                return;
            }
        }

        LauncherModel.SaveSelectedBranch(picked);
        _view.Download.ShowProgress(
            picked == "public" ? "Connecting to Steam..." : $"Connecting to Steam ({picked})..."
        );
        await _model.StartDownloadAsync(picked);
    }

    private async void OnCheckGameUpdatePressed()
    {
        _checkingForGameUpdate = true;
        _view.Actions.SetGameUpdateButtonDisabled(true);
        _view.Actions.SetGameUpdateButtonText("Loading branches...");

        System.Collections.Generic.List<SteamBranchInfo> branches;
        try
        {
            branches = await _model.ListBranchesAsync();
        }
        catch (Exception ex)
        {
            _view.AppendLog($"Branch list failed: {ex.Message}");
            ResetGameUpdateButton();
            _checkingForGameUpdate = false;
            return;
        }

        var current = LauncherModel.LoadSelectedBranch();
        string picked;
        if (branches.Count <= 1)
        {
            picked = branches.Count == 1 ? branches[0].Name : "public";
        }
        else
        {
            picked = await ShowBranchPickerAsync(branches, current);
            if (picked == null)
            {
                ResetGameUpdateButton();
                _checkingForGameUpdate = false;
                return;
            }
        }

        LauncherModel.SaveSelectedBranch(picked);

        // Branch switch + existing files = force a fresh download. The delta path
        // has produced broken installs (e.g. card art mismatches) when going from
        // public ↔ public-beta even though every file passes its manifest SHA-1,
        // so we sidestep it for branch transitions.
        if (picked != current && LauncherModel.GameFilesReady())
        {
            var confirmed = await ConfirmAsync(
                $"Switch to '{picked}'?\n\nGame files (~3GB) will be redownloaded. Login and saves are kept."
            );
            if (!confirmed)
            {
                ResetGameUpdateButton();
                _checkingForGameUpdate = false;
                return;
            }
            _model.WipeGameFiles();
            // Issue #45: The user will soon press the DOWNLOAD button, and upon download completion
            // the PCK is updated in-process, causing a mismatch with dst dll. The download completed callback
            // sees this flag and sets NeedsRestartAfterBranchSwitch.
            _pendingBranchSwitch = true;
            _runOnMainThread(() =>
            {
                _view.Actions.HideAll();
                _view.Download.Visible = true;
                _view.Download.Reset("DOWNLOAD GAME FILES");
                _view.SetStatus($"Switched to {picked}. Tap DOWNLOAD GAME FILES to redownload.");
            });
            _checkingForGameUpdate = false;
            return;
        }

        _view.Actions.SetGameUpdateButtonText(
            picked == "public" ? "Checking..." : $"Checking {picked}..."
        );

        await _model.CheckForUpdatesAsync(picked);

        _checkingForGameUpdate = false;
    }

    private const string ReleasesPageUrl =
        "https://github.com/iunius612/StS2-Launcher_Mod_Manager/releases/latest";

    private async void OnCheckLauncherUpdatePressed() =>
        await RunLauncherUpdateCheck(showLatestDialog: true);

    // Runs at startup once the launch stage is shown so the user is informed
    // about a new launcher version without having to remember to tap the button.
    // Silent on "already on latest" to avoid an unsolicited dialog every boot.
    private async Task AutoCheckLauncherUpdateOnStartup()
    {
        await Task.Delay(1500);
        await RunLauncherUpdateCheck(showLatestDialog: false);
    }

    private async Task RunLauncherUpdateCheck(bool showLatestDialog)
    {
        if (_checkingForLauncherUpdate)
            return;
        _checkingForLauncherUpdate = true;
        _view.Actions.SetLauncherUpdateButtonDisabled(true);
        _view.Actions.SetLauncherUpdateButtonText("Checking...");
        PatchHelper.Log("[Launcher] Checking for launcher update...");

        AppUpdateResult result;
        try
        {
            result = await AppUpdateChecker.CheckAsync();
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Launcher] Update check failed: {ex.Message}");
            _runOnMainThread(() =>
            {
                _view.AppendLog($"Launcher update check failed: {ex.Message}");
                _view.Actions.SetLauncherUpdateButtonText("CHECK LAUNCHER UPDATE");
                _view.Actions.SetLauncherUpdateButtonDisabled(false);
                if (showLatestDialog)
                    _view.ShowConfirmation(
                        $"Failed to check for launcher updates.\n\n{ex.Message}",
                        onConfirmed: () => { },
                        onCancelled: null
                    );
            });
            _checkingForLauncherUpdate = false;
            return;
        }

        PatchHelper.Log(
            $"[Launcher] Update check result: HasUpdate={result.HasUpdate}, latest={result.LatestVersion}"
        );

        if (!result.HasUpdate)
        {
            _runOnMainThread(() =>
            {
                _view.Actions.SetLauncherUpdateButtonText("CHECK LAUNCHER UPDATE");
                _view.Actions.SetLauncherUpdateButtonDisabled(false);
                if (showLatestDialog)
                    _view.ShowConfirmation(
                        "You're already on the latest launcher version.\n\nOpen the GitHub releases page anyway?",
                        onConfirmed: () => OS.ShellOpen(ReleasesPageUrl),
                        onCancelled: null
                    );
            });
            _checkingForLauncherUpdate = false;
            return;
        }

        _runOnMainThread(() =>
        {
            _view.Actions.SetLauncherUpdateButtonText($"v{result.LatestVersion} available");
            _view.Actions.SetLauncherUpdateButtonDisabled(false);
            PromptLauncherUpdate(result);
        });
        _checkingForLauncherUpdate = false;
    }

    private void PromptLauncherUpdate(AppUpdateResult result)
    {
        // No APK asset attached to the release — fall back to opening the GitHub page.
        if (string.IsNullOrEmpty(result.DownloadUrl))
        {
            _view.ShowConfirmation(
                $"Launcher v{result.LatestVersion} is available, but no APK asset was attached.\n\nOpen the GitHub releases page in a browser?",
                onConfirmed: () => OS.ShellOpen(ReleasesPageUrl),
                onCancelled: null
            );
            return;
        }

        // System "install unknown apps" toggle is per-source on Android 8+. Without it
        // the install Intent silently no-ops, so route the user to settings first.
        if (!AppUpdateInstaller.CanRequestInstallPackages())
        {
            _view.ShowConfirmation(
                $"Launcher v{result.LatestVersion} is available.\n\nTo install it, allow this app to install other apps. Open system settings?",
                onConfirmed: AppUpdateInstaller.RequestInstallPackagesPermission,
                onCancelled: null
            );
            return;
        }

        // Release notes excerpt (between <!-- launcher-dialog --> markers) is
        // shown verbatim if present. Authors keep these short — the full
        // changelog lives on the GitHub release page.
        var msg = string.IsNullOrEmpty(result.ReleaseNotes)
            ? $"Launcher v{result.LatestVersion} is available.\n\nDownload and install now?"
            : $"Launcher v{result.LatestVersion} is available.\n\n{result.ReleaseNotes}\n\nDownload and install now?";
        _view.ShowConfirmation(
            msg,
            onConfirmed: () => StartLauncherDownload(result),
            onCancelled: null
        );
    }

    private void StartLauncherDownload(AppUpdateResult result)
    {
        var dialog = _view.ShowLauncherUpdateDialog(result.LatestVersion);
        var cts = new CancellationTokenSource();
        dialog.Cancelled += () => cts.Cancel();

        var progress = new Progress<ApkDownloadProgress>(p =>
            _runOnMainThread(() =>
                dialog.SetProgress(p.DownloadedBytes, p.TotalBytes, p.Percentage)
            )
        );

        Task.Run(async () =>
        {
            try
            {
                var apkPath = await AppUpdateInstaller.DownloadApkAsync(
                    result.DownloadUrl,
                    progress,
                    cts.Token
                );
                _runOnMainThread(() =>
                {
                    dialog.Close();
                    _view.AppendLog(
                        $"Launcher update v{result.LatestVersion} downloaded; opening installer..."
                    );
                    AppUpdateInstaller.LaunchInstall(apkPath);
                });
            }
            catch (OperationCanceledException)
            {
                _runOnMainThread(() =>
                {
                    dialog.Close();
                    _view.AppendLog("Launcher update download cancelled.");
                });
            }
            catch (Exception ex)
            {
                _runOnMainThread(() =>
                {
                    dialog.Close();
                    _view.AppendLog($"Launcher update download failed: {ex.Message}");
                });
            }
        });
    }

    private Task<bool> ConfirmAsync(string message)
    {
        var tcs = new TaskCompletionSource<bool>();
        _runOnMainThread(() =>
        {
            _view.ShowConfirmation(
                message,
                onConfirmed: () => tcs.TrySetResult(true),
                onCancelled: () => tcs.TrySetResult(false)
            );
        });
        return tcs.Task;
    }

    private void ResetGameUpdateButton()
    {
        _view.Actions.SetGameUpdateButtonText("CHECK GAME UPDATE");
        _view.Actions.SetGameUpdateButtonDisabled(false);
    }

    private Task<string> ShowBranchPickerAsync(
        System.Collections.Generic.IReadOnlyList<SteamBranchInfo> branches,
        string currentBranch
    )
    {
        var tcs = new TaskCompletionSource<string>();
        _runOnMainThread(() =>
        {
            _view.ShowBranchPicker(
                branches,
                currentBranch,
                onConfirmed: name => tcs.TrySetResult(name),
                onCancelled: () => tcs.TrySetResult(null),
                // Issue #23 — manual atlas-cache wipe entrypoint. The branch
                // picker closes itself before raising the event; here we
                // resolve the picker's task as a cancel and chain the
                // confirm-and-restart flow.
                onAtlasWipeRequested: () =>
                {
                    tcs.TrySetResult(null);
                    ShowAtlasWipeConfirm();
                }
            );
        });
        return tcs.Task;
    }

    private void ShowAtlasWipeConfirm()
    {
        _view.ShowConfirmation(
            "Clear Image Index Cache\n\n"
                + "Use this when potions, cards, relics, etc., are displayed incorrectly.\n"
                + "Deletes the game texture cache (~660 items) and restarts the app.\n\n"
                + "* The next launch will take 30-60 seconds longer (re-import)\n"
                + "* Game files will not be re-downloaded\n"
                + "* Saves / progress / login info are preserved",
            onConfirmed: () =>
            {
                try
                {
                    var marker = Path.Combine(OS.GetDataDir(), ".atlas_wipe_pending");
                    File.Create(marker).Dispose();
                    PatchHelper.Log("[AtlasWipe] manual marker written, restarting");
                }
                catch (Exception ex)
                {
                    PatchHelper.Log($"[AtlasWipe] failed to write marker: {ex.Message}");
                }
                FlushCloudThenRestart();
            },
            onCancelled: null
        );
    }

    private void OnDebugTogglePressed()
    {
        if (DebugLogger.IsEnabled())
        {
            var path = DebugLogger.GetCurrentFilePath() ?? DebugLogger.GetLogsDirPath();
            _view.ShowConfirmation(
                $"Debug logging is ON.\n\nCurrent log file:\n{path}\n\nTurn off?",
                onConfirmed: () =>
                {
                    DebugLogger.Disable();
                    UpdateDebugButtonLabel();
                    _view.AppendLog("Debug logging disabled.");
                },
                onCancelled: null
            );
        }
        else
        {
            var dir = DebugLogger.GetLogsDirPath();
            _view.ShowConfirmation(
                $"Turn debug logging on?\n\nLogs will be written under:\n{dir}\n\nFor full launch-to-gameplay logs, restart the app after enabling.",
                onConfirmed: () =>
                {
                    var path = DebugLogger.Enable();
                    UpdateDebugButtonLabel();
                    _view.AppendLog($"Debug logging enabled → {path ?? "(failed to start)"}");
                },
                onCancelled: null
            );
        }
    }

    private void UpdateDebugButtonLabel() =>
        _view.DebugButton.Text = DebugLogger.IsEnabled() ? "Debug: ON" : "Debug: OFF";

    // Issue #36 Part A: one-shot manual backup. Confirm → background snapshot
    // of the whole save tree via LocalBackupService.BackupNow() → result modal.
    private void OnLocalBackupPressed()
    {
        // Backups live under external storage (StS2LauncherMM/Saves). Without
        // the permission there's nowhere to write — request it and bail so the
        // user can grant and retry, rather than firing a guaranteed failure.
        // (BackupNow also re-checks and returns NeedsPermission, but pre-checking
        // lets us prompt up front instead of showing a failure dialog.)
        if (!AppPaths.HasStoragePermission())
        {
            AppPaths.RequestStoragePermission();
            _view.ShowConfirmation(
                "Storage access permission is required to back up.\nPlease grant the permission and try again.",
                onConfirmed: null,
                okLabel: "OK",
                cancelLabel: "Close"
            );
            return;
        }

        ShowConfirmation(
            "Do you want to back up current save data locally?",
            () =>
            {
                if (_cloudOpInProgress)
                    return;
                _cloudOpInProgress = true;

                AppPaths.EnsureExternalDirectories();
                _view.SetCloudOpBusy(true);
                _view.AppendLog("Backing up saves locally...");
                // BackupNow() is synchronous and does file I/O — run it off the
                // main thread, then marshal the result back for UI. Wrapped in
                // try/finally so an unexpected throw still releases the busy
                // lock/guard instead of leaving every save-touching button
                // disabled for the rest of the session.
                Task.Run(() =>
                {
                    try
                    {
                        var result = LocalBackupService.BackupNow();
                        _runOnMainThread(() =>
                        {
                            // Permission can be revoked between the pre-check above
                            // and the call; surface that path explicitly.
                            if (!result.Success && result.NeedsPermission)
                            {
                                AppPaths.RequestStoragePermission();
                                _view.AppendLog("Local backup needs storage permission.");
                                _view.ShowConfirmation(
                                    "Storage access permission is required to back up.\nPlease grant the permission and try again.",
                                    onConfirmed: null,
                                    okLabel: "OK",
                                    cancelLabel: "Close"
                                );
                                return;
                            }

                            _view.AppendLog(
                                result.Success
                                    ? $"Local backup complete: {result.FileCount} file(s)."
                                    : $"Local backup failed: {result.Error}"
                            );
                            _view.ShowBackupResult(
                                result.Success,
                                result.FileCount,
                                result.TotalBytes,
                                result.DestPath,
                                result.Error
                            );
                        });
                    }
                    catch (Exception ex)
                    {
                        _runOnMainThread(() =>
                            _view.AppendLog($"Local backup threw: {ex.Message}")
                        );
                    }
                    finally
                    {
                        _runOnMainThread(() =>
                        {
                            _view.SetCloudOpBusy(false);
                            _cloudOpInProgress = false;
                        });
                    }
                });
            }
        );
    }

    private void OnCloudSyncToggled(bool pressed)
    {
        LauncherModel.SaveCloudSyncPref(pressed);
        LauncherPatches.CloudSyncEnabled = pressed;
    }

    // issue #81 — Minimal implementation of IProgress<T>. Report is called from background threads (drain polling/CloudSaveWriter),
    // so marshal to main thread using _runOnMainThread inside the callback.
    private sealed class MainThreadProgress : IProgress<(int done, int total)>
    {
        private readonly Action<(int done, int total)> _report;

        public MainThreadProgress(Action<(int done, int total)> report) => _report = report;

        public void Report((int done, int total) value) => _report(value);
    }

    private void OnCloudPushPressed()
    {
        if (BlockIfTokenExpired())
            return;
        ShowConfirmation(
            "Push local saves to cloud?\nThis will overwrite your cloud saves.",
            () =>
            {
                if (_cloudOpInProgress)
                    return;
                _cloudOpInProgress = true;

                _view.SetCloudOpBusy(true);
                _view.AppendLog("Pushing local saves to cloud...");
                Task.Run(async () =>
                {
                    // issue #81 — Display progress stages (cleaning/reflecting) and file counts on the status line
                    // so it's not mistaken for freezing during bulk cleanup/upload. onPhase updates stage
                    // text and progress updates done/total (both marshalled to main thread).
                    string phase = "Syncing with cloud";
                    var progress = new MainThreadProgress(p =>
                        _runOnMainThread(() => _view.SetStatus($"{phase}... {p.done}/{p.total}"))
                    );
                    try
                    {
                        var outcome = await CloudSyncCoordinator.ManualPushAllAsync(
                            LauncherPatches.SavedAccountName,
                            LauncherPatches.SavedRefreshToken,
                            progress,
                            ph =>
                            {
                                phase = ph;
                                _runOnMainThread(() => _view.SetStatus(ph + "..."));
                            }
                        );
                        _runOnMainThread(() =>
                            _view.AppendLog(
                                outcome switch
                                {
                                    CloudBatchOutcome.Success => "Push complete.",
                                    CloudBatchOutcome.TimedOut =>
                                        "Push timed out — some saves may not have finished uploading. Check your connection and try again.",
                                    CloudBatchOutcome.Failed =>
                                        "Push finished with errors — some saves may not have uploaded. Check the log.",
                                    _ => "Push finished.",
                                }
                            )
                        );
                    }
                    catch (Exception ex)
                    {
                        _runOnMainThread(() => _view.AppendLog($"Push failed: {ex.Message}"));
                    }
                    finally
                    {
                        _runOnMainThread(() =>
                        {
                            _view.SetStatus("");
                            _view.SetCloudOpBusy(false);
                            _cloudOpInProgress = false;
                        });
                    }
                });
            }
        );
    }

    private void OnCloudPullPressed()
    {
        if (BlockIfTokenExpired())
            return;
        ShowConfirmation(
            "Pull cloud saves to local?\nThis will overwrite your local saves.",
            () =>
            {
                if (_cloudOpInProgress)
                    return;
                _cloudOpInProgress = true;

                _view.SetCloudOpBusy(true);
                _view.AppendLog("Pulling cloud saves to local...");
                Task.Run(async () =>
                {
                    // issue #81 — Display stages/counts on the status line just like push (to prevent freeze misunderstandings).
                    string phase = "Syncing with cloud";
                    var progress = new MainThreadProgress(p =>
                        _runOnMainThread(() => _view.SetStatus($"{phase}... {p.done}/{p.total}"))
                    );
                    try
                    {
                        var outcome = await CloudSyncCoordinator.ManualPullAllAsync(
                            LauncherPatches.SavedAccountName,
                            LauncherPatches.SavedRefreshToken,
                            progress,
                            ph =>
                            {
                                phase = ph;
                                _runOnMainThread(() => _view.SetStatus(ph + "..."));
                            }
                        );
                        _runOnMainThread(() =>
                            _view.AppendLog(
                                outcome switch
                                {
                                    CloudBatchOutcome.Success => "Pull complete.",
                                    CloudBatchOutcome.Failed =>
                                        "Pull finished with errors — some saves may not have downloaded. Check the log.",
                                    _ => "Pull finished.",
                                }
                            )
                        );
                    }
                    catch (Exception ex)
                    {
                        _runOnMainThread(() => _view.AppendLog($"Pull failed: {ex.Message}"));
                    }
                    finally
                    {
                        _runOnMainThread(() =>
                        {
                            _view.SetStatus("");
                            _view.SetCloudOpBusy(false);
                            _cloudOpInProgress = false;
                        });
                    }
                });
            }
        );
    }

    private void ShowConfirmation(string message, Action onConfirmed)
    {
        _view.ShowConfirmation(message, onConfirmed);
    }

    // issue #59 — boot-time choice for an expired saved token (fast path only,
    // so exactly once per app launch). "Log in again" → login stage; "Continue
    // offline" (or Android Back, which StyledDialog maps to Cancel) → the normal
    // launch stage. No re-prompt this session: auth-gated features show the
    // restart notice instead (BlockIfTokenExpired), since the fast path never
    // built a login-capable session to hand re-auth mid-flight.
    private void ShowTokenExpiredChoice()
    {
        var dialog = new StyledDialog(
            "Steam login has expired.\n"
                + "You can log in again, or continue offline without cloud sync and Workshop.",
            LauncherUI.ResolveScale(_view.RootControl),
            okLabel: "Log in again",
            cancelLabel: "Continue offline"
        );
        dialog.Confirmed += () => ShowLoginStage("Steam login has expired. Please log in again.");
        dialog.Cancelled += () =>
        {
            PatchHelper.Log("[Issue59] Expired-token dialog: offline chosen");
            _view.SetStatus("Offline mode — Cloud sync and Workshop require re-login");
            ShowLaunchStage(ResolveLaunchButtonText(), showCloudSync: true, showUpdate: true);
        };
        _view.RootControl.AddChild(dialog);
    }

    // issue #59 — gate for auth-required features (Mod Hub, cloud Push/Pull)
    // while the saved token is expired. Shown on EVERY attempt (owner-
    // specified). Mid-session re-auth isn't wired for the fast path, so the
    // honest instruction is an app restart; a successful re-login clears
    // SavedTokenExpired (LauncherModel) and next boot re-evaluates.
    private bool BlockIfTokenExpired()
    {
        if (!_model.SavedTokenExpired)
            return false;
        _ = SimpleResultDialog.ShowAsync(
            _view.RootControl,
            false,
            "Cannot use this feature because Steam login has expired.\nPlease restart the app and log in again.",
            LauncherUI.ResolveScale(_view.RootControl)
        );
        return true;
    }

    private void OnRetryPressed()
    {
        var result = _model.Retry();
        HandleFastPath(result);
    }

    private void OnLaunchPressed()
    {
        // Issue #45: If there was a PCK in-process update due to a branch switch, there is a risk of
        // mismatch with dst dll — terminate process instead of Launch (clean exit, disappears from recents).
        // When the user re-taps the launcher icon, GodotApp.setupAssemblies() copies the new dll.
        if (_model.NeedsRestartAfterBranchSwitch)
        {
            PatchHelper.Log("[Launcher] Restart-required button tapped — exiting app");
            LauncherModel.GetGodotApp()?.Call("exitApp");
            return;
        }
        _model.Launch();
    }

    // Issue #53: When an in-session game update replaces the assembly, show a 1-line message to the user
    // and auto-restart after a short delay. restartApp uses the exact same mechanism as AtlasWipe/ShaderWarmup/Quit
    // — on reboot, Java setupAssemblies copies the new sts2.dll to dst, and then the game boots with the new assembly.
    // Delayed with a timer (2s) to give time for the message to be read, while hiding the action buttons
    // to prevent re-entering PLAY during the delay.
    private void PromptUpdateRestart()
    {
        _view.Actions.HideAll();
        _view.SetStatus("Restarting to apply updates...");
        try
        {
            var timer = _view.RootControl.GetTree().CreateTimer(2.0);
            timer.Timeout += FlushCloudThenRestart;
        }
        catch (Exception ex)
        {
            // Timer path unavailable (e.g. detached tree) — restart immediately.
            PatchHelper.Log(
                $"[Launcher] Update-restart timer failed, restarting now: {ex.Message}"
            );
            FlushCloudThenRestart();
        }
    }

    // P1-2 (G7) — restartApp bypasses NGame.Quit entirely (that's where
    // QuitPrefix's own Flush(300s) lives), so any cloud writes still queued
    // at these points (AtlasWipe confirm, update-restart) would be silently
    // dropped — the cloud stays stale until the NEXT session's handshake
    // self-heals it, and in the meantime another device could pull the stale
    // copy. Flush is a blocking wait (Thread.Sleep polling under the hood),
    // so it must run off the main thread — every call site above is a
    // main-thread button/timer callback. Fail-open: the restart proceeds
    // whether Flush drains in time or times out — nothing here is worth
    // blocking a restart over, since the local save is intact either way and
    // will resync on next launch.
    private void FlushCloudThenRestart()
    {
        Task.Run(() =>
        {
            try
            {
                bool drained = SteamKit2CloudSaveStore.Instance?.Flush(60_000) ?? true;
                if (!drained)
                    PatchHelper.Log("[Cloud] Pre-restart flush timed out, restarting anyway");
            }
            catch (Exception ex)
            {
                PatchHelper.Log(
                    $"[Cloud] Pre-restart flush failed, restarting anyway: {ex.Message}"
                );
            }
            _runOnMainThread(() => LauncherModel.GetGodotApp()?.Call("restartApp"));
        });
    }

    // Issue #45: Play button label forced to Korean "App restart required" if NeedsRestartAfterBranchSwitch is set,
    // otherwise preserves existing InGameMode logic.
    private string ResolveLaunchButtonText()
    {
        if (_model.NeedsRestartAfterBranchSwitch)
            return "App restart required";
        return _model.InGameMode ? "PLAY" : "RESTART APP";
    }
}
