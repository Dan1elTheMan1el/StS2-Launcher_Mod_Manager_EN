using System;
using System.Collections.Generic;
using Godot;

namespace STS2Mobile.Launcher.Components;

// Full-screen detail "page" for a mod, shown when a row/card is tapped in the Mod
// Hub (issue #58). Title + subtitle + optional warning, a scrollable body
// (description / readme / fact lines), a fixed CLOSE button, and an optional
// action button (e.g. "Remove Mod" for a local mod). Used across the WORKSHOP,
// SUBSCRIBED and LOCAL tabs so every list has a consistent tap-to-detail.
public class ModDetailDialog : ColorRect
{
    public ModDetailDialog(
        string title,
        string subtitle,
        string warning,
        string body,
        IEnumerable<(string Label, string Value)> facts,
        float scale,
        string actionLabel = null,
        Action actionCallback = null,
        bool actionDanger = false,
        string action2Label = null,
        Action action2Callback = null,
        ButtonVariant action2Variant = ButtonVariant.Accent
    )
    {
        // While this dialog is up, lists underneath must not react to drags.
        ModalGate.Register(this);

        SetAnchorsPreset(LayoutPreset.FullRect);
        Color = new Color(0, 0, 0, 0.6f);
        MouseFilter = MouseFilterEnum.Stop;
        // Safety net so a modal can never trap the user: tapping the dimmed area
        // outside the panel closes it.
        GuiInput += ev =>
        {
            if (
                ev
                is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }
                    or InputEventScreenTouch { Pressed: true }
            )
                QueueFree();
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
        vbox.AddThemeConstantOverride("separation", (int)(10 * scale));
        vbox.CustomMinimumSize = new Vector2((int)(340 * scale), 0);
        box.AddChild(vbox);

        var titleLabel = new StyledLabel(
            title,
            scale,
            fontSize: 18,
            align: HorizontalAlignment.Left
        );
        titleLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        titleLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        vbox.AddChild(titleLabel);

        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            var sub = new StyledLabel(
                subtitle,
                scale,
                fontSize: 12,
                align: HorizontalAlignment.Left
            );
            sub.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            sub.AddThemeColorOverride("font_color", new Color(0.65f, 0.65f, 0.72f));
            vbox.AddChild(sub);
        }

        if (!string.IsNullOrWhiteSpace(warning))
        {
            var warn = new StyledLabel(
                warning,
                scale,
                fontSize: 12,
                align: HorizontalAlignment.Left
            );
            warn.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            warn.AddThemeColorOverride("font_color", new Color(0.95f, 0.75f, 0.3f));
            vbox.AddChild(warn);
        }

        var scroll = new ScrollContainer();
        scroll.CustomMinimumSize = new Vector2((int)(340 * scale), (int)(300 * scale));
        vbox.AddChild(scroll);
        TouchScroll.Attach(scroll);

        var content = new VBoxContainer();
        content.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        content.CustomMinimumSize = new Vector2((int)(340 * scale), 0);
        content.AddThemeConstantOverride("separation", (int)(8 * scale));
        scroll.AddChild(content);

        if (!string.IsNullOrWhiteSpace(body))
        {
            var bodyLabel = new StyledLabel(
                body,
                scale,
                fontSize: 13,
                align: HorizontalAlignment.Left
            );
            bodyLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            bodyLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            bodyLabel.AddThemeColorOverride("font_color", new Color(0.82f, 0.82f, 0.87f));
            content.AddChild(bodyLabel);
        }

        if (facts != null)
        {
            foreach (var (label, value) in facts)
            {
                if (string.IsNullOrWhiteSpace(value))
                    continue;
                var fact = new StyledLabel(
                    $"{label}: {value}",
                    scale,
                    fontSize: 11,
                    align: HorizontalAlignment.Left
                );
                fact.AutowrapMode = TextServer.AutowrapMode.Arbitrary;
                fact.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                fact.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.66f));
                content.AddChild(fact);
            }
        }

        var buttonRow = new HBoxContainer();
        buttonRow.AddThemeConstantOverride("separation", (int)(10 * scale));
        buttonRow.Alignment = BoxContainer.AlignmentMode.Center;
        vbox.AddChild(buttonRow);

        // Optional second action (e.g. Disable/Enable next to Remove) — rendered
        // first so the destructive primary action stays right-most.
        if (!string.IsNullOrEmpty(action2Label) && action2Callback != null)
        {
            var action2Button = new StyledButton(
                action2Label,
                scale,
                fontSize: 13,
                height: 48,
                variant: action2Variant
            );
            action2Button.CustomMinimumSize = new Vector2((int)(150 * scale), (int)(48 * scale));
            action2Button.Pressed += () =>
            {
                QueueFree();
                action2Callback();
            };
            buttonRow.AddChild(action2Button);
        }

        if (!string.IsNullOrEmpty(actionLabel) && actionCallback != null)
        {
            var actionButton = new StyledButton(
                actionLabel,
                scale,
                fontSize: 13,
                height: 48,
                variant: actionDanger ? ButtonVariant.Danger : ButtonVariant.Primary
            );
            actionButton.CustomMinimumSize = new Vector2((int)(150 * scale), (int)(48 * scale));
            actionButton.Pressed += () =>
            {
                QueueFree();
                actionCallback();
            };
            buttonRow.AddChild(actionButton);
        }

        var closeButton = new StyledButton(
            "CLOSE",
            scale,
            fontSize: 13,
            height: 48,
            variant: ButtonVariant.Ghost
        );
        closeButton.CustomMinimumSize = new Vector2((int)(150 * scale), (int)(48 * scale));
        closeButton.Pressed += QueueFree;
        buttonRow.AddChild(closeButton);
    }
}
