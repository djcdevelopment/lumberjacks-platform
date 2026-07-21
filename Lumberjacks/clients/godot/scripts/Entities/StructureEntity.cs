using Godot;

namespace CommunitySurvival.Entities;

/// <summary>
/// Structure entity — box/cylinder mesh, color by type. Does not interpolate.
/// </summary>
public partial class StructureEntity : RemoteEntity
{
    private MeshInstance3D _meshInstance;

    public override void _Ready()
    {
        _meshInstance = GetNode<MeshInstance3D>("MeshInstance3D");
    }

    public void Initialize(Vector3 position, float heading, Godot.Collections.Dictionary metadata)
    {
        GlobalPosition = position;
        Rotation = new Vector3(0, heading, 0);

        var structureType = metadata.ContainsKey("type") ? (string)metadata["type"] : "unknown";

        Mesh mesh;
        Color color;
        float meshOffsetY;

        switch (structureType)
        {
            case "campfire":
                var cyl = new CylinderMesh();
                cyl.TopRadius = 0.5f;
                cyl.BottomRadius = 0.5f;
                cyl.Height = 0.3f;
                mesh = cyl;
                color = new Color(0.9f, 0.5f, 0.1f);
                meshOffsetY = 0.15f;
                break;
            case "wooden_wall":
                var box = new BoxMesh();
                box.Size = new Vector3(2.0f, 1.5f, 0.3f);
                mesh = box;
                color = new Color(0.55f, 0.35f, 0.15f);
                meshOffsetY = 0.75f;
                break;
            case "test_beacon":
                var sphere = new SphereMesh();
                sphere.Radius = 0.4f;
                sphere.Height = 0.8f;
                mesh = sphere;
                color = new Color(0.9f, 0.9f, 0.1f);
                meshOffsetY = 0.4f;
                break;
            default:
                var defBox = new BoxMesh();
                defBox.Size = new Vector3(1, 1, 1);
                mesh = defBox;
                color = new Color(0.5f, 0.5f, 0.5f);
                meshOffsetY = 0.5f;
                break;
        }

        _meshInstance.Mesh = mesh;
        var mat = new StandardMaterial3D { AlbedoColor = color };
        _meshInstance.MaterialOverride = mat;
        _meshInstance.Position = new Vector3(0, meshOffsetY, 0);
    }
}
