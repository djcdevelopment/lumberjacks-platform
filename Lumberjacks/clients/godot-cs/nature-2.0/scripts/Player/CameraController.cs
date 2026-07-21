using Godot;

namespace CommunitySurvival.Player;

/// <summary>
/// WoW-style camera: right-click hold to orbit, scroll to zoom.
/// Attached to the CameraPivot node (parent of Camera3D).
/// </summary>
public partial class CameraController : Node3D
{
    [Export] public float Sensitivity = 0.3f;
    [Export] public float MinPitch = -80f;
    [Export] public float MaxPitch = 80f;
    [Export] public float MinDistance = 3f;
    [Export] public float MaxDistance = 40f;
    [Export] public float ZoomSpeed = 2f;

    private Camera3D _camera;
    private bool _orbiting;
    private float _yaw;
    private float _pitch = -25f; // Start looking slightly down
    private float _distance = 15f;

    public override void _Ready()
    {
        _camera = GetNode<Camera3D>("Camera3D");
        UpdateCamera();
    }

    public override void _UnhandledInput(InputEvent ev)
    {
        if (ev is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.Right)
            {
                _orbiting = mb.Pressed;
                Input.MouseMode = _orbiting ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
            }
            else if (mb.ButtonIndex == MouseButton.WheelUp)
            {
                _distance = Mathf.Max(MinDistance, _distance - ZoomSpeed);
                UpdateCamera();
            }
            else if (mb.ButtonIndex == MouseButton.WheelDown)
            {
                _distance = Mathf.Min(MaxDistance, _distance + ZoomSpeed);
                UpdateCamera();
            }
        }
        else if (ev is InputEventMouseMotion mm && _orbiting)
        {
            _yaw -= mm.Relative.X * Sensitivity;
            _pitch -= mm.Relative.Y * Sensitivity;
            _pitch = Mathf.Clamp(_pitch, MinPitch, MaxPitch);
            UpdateCamera();
        }
    }

    private void UpdateCamera()
    {
        // Pivot rotates with yaw/pitch, camera sits at distance behind
        RotationDegrees = new Vector3(_pitch, _yaw, 0);
        if (_camera != null)
            _camera.Position = new Vector3(0, 0, _distance);
    }
}
