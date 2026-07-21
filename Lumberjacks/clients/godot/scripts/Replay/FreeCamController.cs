using Godot;

namespace CommunitySurvival.Replay;

/// <summary>
/// Slice 1 free-cam. Editor-style controls:
///   WASD                        — horizontal movement (camera-local, projected to XZ)
///   Space                       — up
///   Ctrl+Space                  — down
///   Shift                       — 5× boost
///   Hold RMB + drag             — look (yaw + clamped pitch)
///
/// Future-proofed for a godmode toggle: when GodModeActive is false, all
/// motion is suppressed (Space becomes available for jump in some future
/// non-spectator mode without rewiring keybinds).
/// </summary>
public partial class FreeCamController : Camera3D
{
    [Export] public float MoveSpeed = 14.0f;
    [Export] public float BoostMultiplier = 5.0f;
    [Export] public float MouseSensitivity = 0.0035f;
    [Export] public float MaxPitch = 1.4f; // ~80° each side

    public bool GodModeActive { get; set; } = true;

    private bool _looking = false;

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!GodModeActive) return;

        if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Right)
        {
            _looking = mb.Pressed;
            return;
        }

        if (@event is InputEventMouseMotion mm && _looking)
        {
            var euler = Rotation;
            euler.Y -= mm.Relative.X * MouseSensitivity;
            euler.X -= mm.Relative.Y * MouseSensitivity;
            euler.X = Mathf.Clamp(euler.X, -MaxPitch, MaxPitch);
            Rotation = euler;
        }
    }

    public override void _Process(double delta)
    {
        if (!GodModeActive) return;

        var input = Vector3.Zero;
        if (Input.IsActionPressed("move_forward")) input.Z -= 1;
        if (Input.IsActionPressed("move_back"))    input.Z += 1;
        if (Input.IsActionPressed("move_left"))    input.X -= 1;
        if (Input.IsActionPressed("move_right"))   input.X += 1;

        // Horizontal movement projected from camera-local to the world XZ plane —
        // pitching the camera shouldn't steer WASD into or out of the ground.
        var horiz = Basis * new Vector3(input.X, 0, input.Z);
        horiz.Y = 0;
        if (horiz.LengthSquared() > 0) horiz = horiz.Normalized();

        var vertical = 0f;
        if (Input.IsKeyPressed(Key.Space))
        {
            vertical = Input.IsKeyPressed(Key.Ctrl) ? -1f : 1f;
        }

        var motion = horiz + new Vector3(0, vertical, 0);
        var speed = Input.IsKeyPressed(Key.Shift) ? MoveSpeed * BoostMultiplier : MoveSpeed;
        Position += motion * speed * (float)delta;
    }
}
