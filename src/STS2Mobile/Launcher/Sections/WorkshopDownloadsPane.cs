using System;
using System.Linq;
using Godot;
using STS2Mobile.Launcher.Components;
using STS2Mobile.Steam;

namespace STS2Mobile.Launcher.Sections;

// DOWNLOADS tab of the Mod Hub (issue #58 phase 4b) — a live view of the single
// WorkshopDownloadQueue shared by the WORKSHOP and SUBSCRIBED tabs. ModManagerSection
// owns the queue and calls RenderFromQueue() on every Changed event (marshalled to
// the main thread there), so this pane itself never touches the queue off-thread.
public class WorkshopDownloadsPane : VBoxContainer
{
    private static readonly Color InfoColor = Ui.TextSecondary;
    private static readonly Color WarnColor = Ui.Warn;

    private readonly float _scale;
    private readonly StyledLabel _statusLabel;
    private readonly StyledButton _cancelAllButton;
    private readonly VBoxContainer _list;

    private WorkshopDownloadQueue _queue;

    public WorkshopDownloadsPane(float scale)
    {
        _scale = scale;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", (int)(8 * scale));

        var headerRow = new HBoxContainer();
        headerRow.AddThemeConstantOverride("separation", (int)(8 * scale));
        AddChild(headerRow);

        _statusLabel = new StyledLabel("", scale, fontSize: 12, align: HorizontalAlignment.Left);
        _statusLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _statusLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        headerRow.AddChild(_statusLabel);

        _cancelAllButton = new StyledButton(
            "CANCEL ALL",
            scale,
            fontSize: 13,
            height: 44,
            variant: ButtonVariant.Danger
        );
        _cancelAllButton.Disabled = true;
        _cancelAllButton.Pressed += () => _queue?.CancelAll();
        headerRow.AddChild(_cancelAllButton);

        var scroll = new ScrollContainer();
        scroll.SizeFlagsVertical = SizeFlags.ExpandFill;
        scroll.CustomMinimumSize = new Vector2(0, (int)(220 * scale));
        AddChild(scroll);

        _list = new VBoxContainer();
        _list.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _list.AddThemeConstantOverride("separation", (int)(6 * scale));
        scroll.AddChild(_list);
    }

    public void SetQueue(WorkshopDownloadQueue queue)
    {
        _queue = queue;
        RenderFromQueue();
    }

    // Must run on the main thread.
    public void RenderFromQueue()
    {
        ClearList();

        if (_queue == null)
        {
            SetStatus("No downloads. Steam login is required for Workshop features.", WarnColor);
            _cancelAllButton.Disabled = true;
            return;
        }

        var entries = _queue.Entries;
        _cancelAllButton.Disabled = !_queue.IsBusy;

        if (entries.Count == 0)
        {
            SetStatus("", InfoColor);
            _list.AddChild(
                Ui.MakeEmptyState(
                    null,
                    "No downloads.",
                    "Workshop items you subscribe to are downloaded here.",
                    _scale
                )
            );
            return;
        }

        SetStatus($"{entries.Count} item(s).", InfoColor);
        foreach (var entry in entries)
            _list.AddChild(new DownloadQueueRow(entry, _scale));
    }

    private void ClearList()
    {
        foreach (var child in _list.GetChildren().ToList())
        {
            _list.RemoveChild(child);
            child.QueueFree();
        }
    }

    private void SetStatus(string text, Color color)
    {
        _statusLabel.Text = text;
        _statusLabel.AddThemeColorOverride("font_color", color);
    }
}
