using Godot;
using System.Collections.Generic;
using CommunitySurvival.Core;

namespace CommunitySurvival.Replay;

/// <summary>
/// Bottom-right card listing every selected entity with a Highlight toggle.
/// Subscribes to SelectionManager and rebuilds on every change. Slice 1
/// ships only the Highlight toggle; future slices add Defensives, Trinkets,
/// Target-switching, etc. at the same insertion point.
/// </summary>
public partial class ProfileCard : CanvasLayer
{
    private SelectionManager _selection;
    private Core.World _world;
    private PanelContainer _panel;
    private VBoxContainer _vbox;

    public override void _Ready()
    {
        _selection = GetNode<SelectionManager>("/root/SelectionManager");
        _world = GetParent().GetNodeOrNull<Core.World>("World");

        _panel = new PanelContainer
        {
            AnchorLeft = 1,
            AnchorRight = 1,
            AnchorTop = 1,
            AnchorBottom = 1,
            OffsetLeft = -340,
            OffsetRight = -16,
            OffsetTop = -380,
            OffsetBottom = -72, // leaves room above the scrubber bar
            GrowHorizontal = Control.GrowDirection.Begin,
            GrowVertical = Control.GrowDirection.Begin,
        };
        AddChild(_panel);
        _panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.06f, 0.09f, 0.88f),
            ContentMarginLeft = 12,
            ContentMarginRight = 12,
            ContentMarginTop = 10,
            ContentMarginBottom = 10,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderColor = new Color(0.30f, 0.34f, 0.42f, 0.6f),
        });

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        _panel.AddChild(scroll);
        _vbox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        scroll.AddChild(_vbox);

        _selection.SelectionChanged += Rebuild;
        Rebuild();
    }

    public override void _ExitTree()
    {
        if (_selection != null) _selection.SelectionChanged -= Rebuild;
    }

    private void Rebuild()
    {
        foreach (var child in _vbox.GetChildren())
        {
            child.QueueFree();
        }

        if (_selection.Selected.Count == 0)
        {
            _panel.Visible = false;
            return;
        }
        _panel.Visible = true;

        var header = new Label { Text = $"Selection ({_selection.Selected.Count})" };
        header.AddThemeColorOverride("font_color", new Color(0.65f, 0.70f, 0.78f));
        _vbox.AddChild(header);
        _vbox.AddChild(new HSeparator());

        foreach (var id in _selection.Selected)
        {
            _vbox.AddChild(BuildEntitySection(id));
            _vbox.AddChild(new HSeparator());
        }
    }

    private Control BuildEntitySection(string entityId)
    {
        var section = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };

        var name = entityId;
        var cls = "";
        var role = "";
        Color swatch = WowClassColors.Neutral;
        if (_world != null && _world.Entities.TryGetValue(entityId, out var node))
        {
            if (node.HasMeta("display_name")) name = (string)node.GetMeta("display_name");
            if (node.HasMeta("wow_class")) cls = (string)node.GetMeta("wow_class");
            if (node.HasMeta("wow_role")) role = (string)node.GetMeta("wow_role");
            swatch = WowClassColors.For(cls, role);
        }

        var topRow = new HBoxContainer();
        section.AddChild(topRow);

        var swatchRect = new ColorRect
        {
            Color = swatch,
            CustomMinimumSize = new Vector2(14, 14),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        topRow.AddChild(swatchRect);

        var labels = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        topRow.AddChild(labels);

        labels.AddChild(new Label { Text = name });
        var subtitle = string.IsNullOrEmpty(cls)
            ? role
            : (string.IsNullOrEmpty(role) ? cls : $"{cls} · {role}");
        if (!string.IsNullOrEmpty(subtitle))
        {
            var subLabel = new Label { Text = subtitle };
            subLabel.AddThemeColorOverride("font_color", new Color(0.6f, 0.65f, 0.72f));
            labels.AddChild(subLabel);
        }

        var highlightToggle = new CheckBox
        {
            Text = "Highlight",
            ButtonPressed = _selection.IsHighlightEnabled(entityId),
        };
        highlightToggle.Toggled += (bool on) => _selection.SetHighlightEnabled(entityId, on);
        section.AddChild(highlightToggle);

        return section;
    }
}
