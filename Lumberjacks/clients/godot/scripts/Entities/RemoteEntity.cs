using Godot;
using System;

namespace CommunitySurvival.Entities;

/// <summary>
/// Base class for all network-synced entities.
/// Handles smooth interpolation of server position/rotation updates (ADR 0017).
/// </summary>
public partial class RemoteEntity : Node3D
{
    [Export] public float InterpolationSpeed = 10.0f;

    protected Vector3 _targetPosition;
    protected float _targetRotationY;
    private double _lastUpdateTimestamp;
    private double _updateInterval = 0.05; // Default 20Hz (50ms)

    public void Initialize(Vector3 position, float headingRad)
    {
        GlobalPosition = position;
        _targetPosition = position;
        Rotation = new Vector3(0, headingRad, 0);
        _targetRotationY = headingRad;
    }

    public void UpdateFromServer(Vector3 position, Vector3 velocity, float headingRad, long tick)
    {
        _targetPosition = position;
        _targetRotationY = headingRad;

        // Track update frequency (ADR 0017)
        double currentTickTime = Time.GetTicksMsec() / 1000.0;
        if (_lastUpdateTimestamp > 0)
        {
            _updateInterval = currentTickTime - _lastUpdateTimestamp;
        }
        _lastUpdateTimestamp = currentTickTime;
    }

    public override void _Process(double delta)
    {
        // ADR 0017: If update rate is slower (5Hz Zone), slow down the lerp
        // to avoid "reaching the target too soon" and waiting for the next packet.
        float alpha = (float)(delta * InterpolationSpeed);

        if (_updateInterval > 0.1) // 5Hz zone threshold
        {
            alpha *= (float)(0.05 / _updateInterval);
        }

        GlobalPosition = GlobalPosition.Lerp(_targetPosition, alpha);

        float currentY = Rotation.Y;
        Rotation = new Vector3(Rotation.X, Mathf.LerpAngle(currentY, _targetRotationY, alpha), Rotation.Z);
    }
}
