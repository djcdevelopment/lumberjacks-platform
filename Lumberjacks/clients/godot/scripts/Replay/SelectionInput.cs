using Godot;
using CommunitySurvival.Core;

namespace CommunitySurvival.Replay;

/// <summary>
/// LMB click → select orb under cursor.
///   plain LMB    : replace selection (or clear if no orb hit)
///   Shift + LMB  : toggle this entity in/out of the existing selection
/// All state changes go through SelectionManager — visual consumers
/// (HighlightRing, ProfileCard) react via SelectionChanged.
/// </summary>
public partial class SelectionInput : Node
{
    [Export] public float HitRadius = 1.8f;

    private Camera3D _camera;
    private Core.World _world;
    private SelectionManager _selection;

    public override void _Ready()
    {
        _camera = GetParent().GetNodeOrNull<Camera3D>("Camera3D");
        _world = GetParent().GetNodeOrNull<Core.World>("World");
        _selection = GetNode<SelectionManager>("/root/SelectionManager");
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventMouseButton mb) return;
        if (!mb.Pressed || mb.ButtonIndex != MouseButton.Left) return;
        if (_camera == null || _world == null || _selection == null) return;

        var hitId = ReplayRaycast.FindEntityNearCursor(_camera, _world, mb.Position, HitRadius);
        var shift = Input.IsKeyPressed(Key.Shift);

        if (hitId == null)
        {
            if (!shift) _selection.Clear();
            return;
        }

        if (shift) _selection.ToggleAdd(hitId);
        else _selection.SetSingle(hitId);
    }
}
