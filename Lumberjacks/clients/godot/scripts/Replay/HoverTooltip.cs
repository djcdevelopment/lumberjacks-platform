using Godot;
using CommunitySurvival.Core;

namespace CommunitySurvival.Replay;

/// <summary>
/// Hover-only tooltip showing entity name + class · role above the cursor.
/// Hides when the cursor is not near any orb. Cursor detection uses
/// ReplayRaycast (closest-point-to-ray, no physics).
/// </summary>
public partial class HoverTooltip : CanvasLayer
{
    [Export] public float HitRadius = 1.8f;

    private Camera3D _camera;
    private Core.World _world;
    private PanelContainer _panel;
    private Label _label;

    public override void _Ready()
    {
        _camera = GetParent().GetNodeOrNull<Camera3D>("Camera3D");
        _world = GetParent().GetNodeOrNull<Core.World>("World");

        _panel = new PanelContainer();
        AddChild(_panel);
        _panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0, 0, 0, 0.78f),
            ContentMarginLeft = 8,
            ContentMarginRight = 8,
            ContentMarginTop = 4,
            ContentMarginBottom = 4,
        });
        _label = new Label { Modulate = new Color(0.96f, 0.96f, 0.96f) };
        _panel.AddChild(_label);
        _panel.Visible = false;
    }

    public override void _Process(double delta)
    {
        if (_camera == null || _world == null) { _panel.Visible = false; return; }

        var mouse = _panel.GetViewport().GetMousePosition();
        var hitId = ReplayRaycast.FindEntityNearCursor(_camera, _world, mouse, HitRadius);

        if (hitId == null || !_world.Entities.TryGetValue(hitId, out var node))
        {
            _panel.Visible = false;
            return;
        }

        var name = node.HasMeta("display_name") ? (string)node.GetMeta("display_name") : hitId;
        var cls = node.HasMeta("wow_class") ? (string)node.GetMeta("wow_class") : "";
        var role = node.HasMeta("wow_role") ? (string)node.GetMeta("wow_role") : "";
        var subtitle = string.IsNullOrEmpty(cls)
            ? role
            : (string.IsNullOrEmpty(role) ? cls : $"{cls} · {role}");

        _label.Text = string.IsNullOrEmpty(subtitle) ? name : $"{name}\n{subtitle}";
        _panel.Visible = true;
        _panel.Position = mouse + new Vector2(14, -36);
    }
}
