#nullable enable

using System;
using System.Collections.Generic;
using Godot;

namespace CommunitySurvival.Forest;

public sealed record ForestAsset(
    ForestArchetype Archetype,
    Mesh Mesh,
    float ModelHeight,
    float ArchetypeBend,
    string SourcePath);

/// <summary>Loads the curated CC0 tree meshes and replaces their materials with the shared wind shader.</summary>
public sealed class ForestAssetLibrary
{
    private const string Root = "res://assets/third_party/quaternius/stylized-nature-standard";
    public const string AssetSetSha256 = "be57b22120225badfce94349b5694ecf7bca078f4a8310bcdc916bbeaf7790b0";

    private readonly Shader _windShader;
    private readonly Dictionary<ForestArchetype, ForestAsset> _assets = new();
    private readonly List<ShaderMaterial> _materials = new();

    public ForestAssetLibrary()
    {
        _windShader = GD.Load<Shader>("res://shaders/forest_wind.gdshader")
            ?? throw new InvalidOperationException("Forest wind shader could not be loaded.");
        Load(ForestArchetype.Pine, "Pine_1.gltf", 7.1f, 0.90f);
        Load(ForestArchetype.Common, "CommonTree_1.gltf", 7.1f, 1.16f);
        Load(ForestArchetype.Twisted, "TwistedTree_1.gltf", 16.6f, 0.72f);
    }

    public ForestAsset this[ForestArchetype archetype] => _assets[archetype];
    public IReadOnlyList<ShaderMaterial> Materials => _materials;

    private void Load(ForestArchetype archetype, string fileName, float modelHeight, float bend)
    {
        var path = $"{Root}/{fileName}";
        var packed = GD.Load<PackedScene>(path)
            ?? throw new InvalidOperationException($"CC0 tree asset could not be loaded: {path}");
        var root = packed.Instantiate();
        try
        {
            var source = FindMesh(root)
                ?? throw new InvalidOperationException($"CC0 tree asset contains no mesh: {path}");
            var mesh = source.Duplicate(true) as ArrayMesh
                ?? throw new InvalidOperationException($"CC0 tree asset is not an ArrayMesh: {path}");

            for (var surface = 0; surface < mesh.GetSurfaceCount(); surface++)
            {
                var original = source.SurfaceGetMaterial(surface) as BaseMaterial3D;
                var foliage = surface > 0 ||
                    (original?.ResourceName?.Contains("leaf", StringComparison.OrdinalIgnoreCase) ?? false);
                var material = new ShaderMaterial { Shader = _windShader };
                if (original?.AlbedoTexture is not null)
                    material.SetShaderParameter("albedo_texture", original.AlbedoTexture);
                material.SetShaderParameter("model_height", modelHeight);
                material.SetShaderParameter("is_foliage", foliage ? 1f : 0f);
                material.SetShaderParameter("archetype_bend", bend);
                mesh.SurfaceSetMaterial(surface, material);
                _materials.Add(material);
            }

            _assets[archetype] = new ForestAsset(archetype, mesh, modelHeight, bend, path);
        }
        finally
        {
            root.Free();
        }
    }

    private static Mesh? FindMesh(Node node)
    {
        if (node is MeshInstance3D { Mesh: not null } meshInstance) return meshInstance.Mesh;
        foreach (Node child in node.GetChildren())
        {
            var found = FindMesh(child);
            if (found is not null) return found;
        }
        return null;
    }
}
