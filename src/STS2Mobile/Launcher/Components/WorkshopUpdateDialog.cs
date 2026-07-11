using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace STS2Mobile.Launcher.Components;

// Informational modal shown when a subscription sync detects Workshop mods to
// download or update (issue #58). Lists the affected mod titles in a scrollable
// area (so a long list stays usable) with a header and a fixed CLOSE button at
// the bottom. The items are already queued for download when this appears; the
// dialog just tells the user what's happening.
public class WorkshopUpdateDialog : ColorRect
{
    public WorkshopUpdateDialog(
        string header,
        IEnumerable<string> titles,
        float scale,
        Action onClose = null
    )
    {
        var list = titles?.ToList() ?? new List<string>();

        // While this dialog is up, lists underneath must not react to drags, and
        // Android Back closes it. TreeExiting is the single guaranteed callback
        // path (covers Back/teardown, where the button handler never runs).
        ModalGate.Register(this);
        bool closeFired = false;
        void FireClose()
        {
            if (closeFired)
                return;
            closeFired = true;
            onClose?.Invoke();
        }
        TreeExiting += FireClose;

        SetAnchorsPreset(LayoutPreset.FullRect);
        Color = new Color(0, 0, 0, 0.6f);
        MouseFilter = MouseFilterEnum.Stop;
        // Tapping the dimmed area outside the panel dismisses (and fires onClose),
        // so the user is never trapped by the overlay.
        GuiInput += ev =>
        {
            if (
                ev
                is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }
                    or InputEventScreenTouch { Pressed: true }
            )
            {
                QueueFree();
                FireClose();
            }
        };

        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        center.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(center);

        var box = new PanelContainer();
        box.MouseFilter = MouseFilterEnum.Stop;
        var boxStyle = new StyleBoxFlat();
        boxStyle.BgColor = Ui.SurfaceHigh;
        boxStyle.SetCornerRadiusAll((int)(8 * scale));
        boxStyle.SetContentMarginAll((int)(20 * scale));
        box.AddThemeStyleboxOverride("panel", boxStyle);
        center.AddChild(box);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", (int)(14 * scale));
        box.AddChild(vbox);

        var headerLabel = new StyledLabel(header, scale, fontSize: 16);
        headerLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        headerLabel.CustomMinimumSize = new Vector2((int)(320 * scale), 0);
        vbox.AddChild(headerLabel);

        var scroll = new ScrollContainer();
        // Cap the list viewport so a long update list scrolls instead of pushing
        // the fixed CLOSE button off-screen; short lists stay compact.
        var rowH = 26 * scale;
        var capH = 300 * scale;
        var listH = Math.Min(capH, Math.Max(1, list.Count) * rowH + 8 * scale);
        scroll.CustomMinimumSize = new Vector2((int)(320 * scale), (int)listH);
        vbox.AddChild(scroll);
        TouchScroll.Attach(scroll);

        var listBox = new VBoxContainer();
        listBox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        listBox.AddThemeConstantOverride("separation", (int)(4 * scale));
        scroll.AddChild(listBox);

        foreach (var t in list)
        {
            var row = new StyledLabel(
                "• " + t,
                scale,
                fontSize: 13,
                align: HorizontalAlignment.Left
            );
            row.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            row.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            listBox.AddChild(row);
        }

        var closeButton = new StyledButton(
            "CLOSE",
            scale,
            fontSize: 14,
            height: 52,
            variant: ButtonVariant.Primary
        );
        closeButton.CustomMinimumSize = new Vector2((int)(160 * scale), (int)(52 * scale));
        closeButton.Pressed += () =>
        {
            QueueFree();
            FireClose();
        };
        var buttonRow = new HBoxContainer();
        buttonRow.Alignment = BoxContainer.AlignmentMode.Center;
        buttonRow.AddChild(closeButton);
        vbox.AddChild(buttonRow);
    }
}
