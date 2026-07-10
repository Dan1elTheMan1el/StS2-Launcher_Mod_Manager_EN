using System;
using Godot;
using STS2Mobile.Launcher;

namespace STS2Mobile.Launcher.Components;

// Modal showing APK download progress with bar, status text, and Cancel button.
// Caller drives progress via SetProgress and tears down via Close.
public class LauncherUpdateDialog : ColorRect
{
    public event Action Cancelled;

    private readonly StyledLabel _statusLabel;
    private readonly StyledProgressBar _progressBar;

    public LauncherUpdateDialog(string version, float scale)
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        Color = new Color(0, 0, 0, 0.6f);

        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);

        var dialogBox = new PanelContainer();
        var boxStyle = new StyleBoxFlat();
        boxStyle.BgColor = Ui.SurfaceHigh;
        boxStyle.SetCornerRadiusAll((int)(8 * scale));
        boxStyle.SetContentMarginAll((int)(24 * scale));
        dialogBox.AddThemeStyleboxOverride("panel", boxStyle);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", (int)(14 * scale));
        dialogBox.AddChild(vbox);

        var title = new StyledLabel($"Downloading launcher v{version}...", scale, fontSize: 16);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.CustomMinimumSize = new Vector2((int)(360 * scale), 0);
        vbox.AddChild(title);

        _progressBar = new StyledProgressBar(scale);
        _progressBar.MinValue = 0;
        _progressBar.MaxValue = 100;
        _progressBar.Value = 0;
        _progressBar.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        vbox.AddChild(_progressBar);

        _statusLabel = new StyledLabel("Starting download...", scale, fontSize: 13);
        _statusLabel.HorizontalAlignment = HorizontalAlignment.Center;
        vbox.AddChild(_statusLabel);

        var buttonRow = new HBoxContainer();
        buttonRow.AddThemeConstantOverride("separation", (int)(12 * scale));
        buttonRow.Alignment = BoxContainer.AlignmentMode.Center;
        vbox.AddChild(buttonRow);

        var cancelButton = new StyledButton("Cancel", scale, fontSize: 14, height: 44);
        cancelButton.CustomMinimumSize = new Vector2(
            (int)(120 * scale),
            cancelButton.CustomMinimumSize.Y
        );
        cancelButton.Pressed += () =>
        {
            cancelButton.Disabled = true;
            Cancelled?.Invoke();
        };
        buttonRow.AddChild(cancelButton);

        center.AddChild(dialogBox);
        AddChild(center);
    }

    public void SetProgress(long downloadedBytes, long totalBytes, float percentage)
    {
        if (totalBytes > 0)
        {
            _progressBar.Value = percentage;
            _statusLabel.Text =
                $"{LauncherModel.FormatSize(downloadedBytes)} / {LauncherModel.FormatSize(totalBytes)} ({percentage:F1}%)";
        }
        else
        {
            _progressBar.Value = 0;
            _statusLabel.Text = $"{LauncherModel.FormatSize(downloadedBytes)} downloaded";
        }
    }

    public void Close() => QueueFree();
}
