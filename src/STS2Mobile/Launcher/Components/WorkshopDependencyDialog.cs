using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using STS2Mobile.Steam;

namespace STS2Mobile.Launcher.Components;

// Lightweight overlay listing a Workshop item's dependency children after the
// user subscribes to a parent item with Children (issue #58 phase 4b). Not a
// StyledDialog (that's a plain OK/Cancel confirmation) — this needs one row per
// dependency with its own SUBSCRIBE action, so it's a small bespoke PanelContainer
// overlay in the same visual language.
public class WorkshopDependencyDialog : ColorRect
{
    public event Action Closed;

    public WorkshopDependencyDialog(
        List<WorkshopItemDetails> dependencies,
        HashSet<ulong> alreadySubscribed,
        float scale,
        Func<WorkshopItemDetails, Task<bool>> onSubscribe
    )
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        Color = new Color(0, 0, 0, 0.6f);

        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);

        var box = new PanelContainer();
        var boxStyle = new StyleBoxFlat();
        boxStyle.BgColor = new Color(0.15f, 0.15f, 0.18f);
        boxStyle.SetCornerRadiusAll((int)(8 * scale));
        boxStyle.SetContentMarginAll((int)(20 * scale));
        box.AddThemeStyleboxOverride("panel", boxStyle);
        box.CustomMinimumSize = new Vector2((int)(360 * scale), 0);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", (int)(10 * scale));
        box.AddChild(vbox);

        var title = new StyledLabel("This mod requires:", scale, fontSize: 16);
        vbox.AddChild(title);

        var scroll = new ScrollContainer();
        scroll.CustomMinimumSize = new Vector2(0, (int)(220 * scale));
        vbox.AddChild(scroll);

        var list = new VBoxContainer();
        list.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        list.AddThemeConstantOverride("separation", (int)(6 * scale));
        scroll.AddChild(list);

        foreach (var dep in dependencies)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", (int)(8 * scale));
            list.AddChild(row);

            var nameLabel = new StyledLabel(
                dep.Title,
                scale,
                fontSize: 13,
                align: HorizontalAlignment.Left
            );
            nameLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            nameLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            row.AddChild(nameLabel);

            if (alreadySubscribed.Contains(dep.PublishedFileId))
            {
                var subscribedLabel = new StyledLabel("Subscribed", scale, fontSize: 12);
                subscribedLabel.AddThemeColorOverride("font_color", new Color(0.55f, 0.75f, 0.55f));
                row.AddChild(subscribedLabel);
                continue;
            }

            var depButton = new StyledButton("SUBSCRIBE", scale, fontSize: 12, height: 32);
            depButton.CustomMinimumSize = new Vector2((int)(100 * scale), (int)(32 * scale));
            depButton.Pressed += () =>
            {
                depButton.Disabled = true;
                _ = Task.Run(async () =>
                {
                    bool ok = false;
                    try
                    {
                        ok = await onSubscribe(dep).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        PatchHelper.Log($"[Workshop] Dependency subscribe failed: {ex.Message}");
                    }
                    var success = ok;
                    Callable
                        .From(() =>
                        {
                            if (!IsInstanceValid(depButton))
                                return;
                            if (success)
                            {
                                depButton.Text = "Subscribed";
                                depButton.Disabled = true;
                            }
                            else
                            {
                                depButton.Disabled = false;
                            }
                        })
                        .CallDeferred();
                });
            };
            row.AddChild(depButton);
        }

        var closeButton = new StyledButton("CLOSE", scale, fontSize: 14, height: 40);
        closeButton.Pressed += () =>
        {
            QueueFree();
            Closed?.Invoke();
        };
        vbox.AddChild(closeButton);

        center.AddChild(box);
        AddChild(center);
    }
}
