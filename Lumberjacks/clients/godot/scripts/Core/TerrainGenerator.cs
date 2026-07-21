using Godot;
using System;

namespace CommunitySurvival.Core;

/// <summary>
/// Generates a heightmap mesh from a RegionProfile altitude grid.
/// The grid is 50x50 covering a 1000x1000 world region (-500 to +500).
/// Each grid cell maps to a 20x20 unit quad.
/// </summary>
public static class TerrainGenerator
{
    // Scale factor for altitude — raw values are 0-110, which would make
    // a very steep mountain. Scale down for natural-looking terrain.
    private const float AltitudeScale = 0.3f;

    // Region bounds (matching server's region-spawn default)
    private const float RegionMin = -500f;
    private const float RegionMax = 500f;

    public static ArrayMesh Generate(double[] altitudeGrid, int gridWidth, int gridHeight)
    {
        if (altitudeGrid == null || altitudeGrid.Length != gridWidth * gridHeight)
        {
            GD.PrintErr($"TerrainGenerator: Invalid grid data ({altitudeGrid?.Length} vs {gridWidth * gridHeight})");
            return null;
        }

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);

        float cellWidth = (RegionMax - RegionMin) / gridWidth;
        float cellHeight = (RegionMax - RegionMin) / gridHeight;

        // Build quads from the grid
        for (int gy = 0; gy < gridHeight - 1; gy++)
        {
            for (int gx = 0; gx < gridWidth - 1; gx++)
            {
                // Grid positions to world coordinates (ADR 0018: negate Z for Godot)
                float x0 = RegionMin + gx * cellWidth;
                float x1 = RegionMin + (gx + 1) * cellWidth;
                float z0 = -(RegionMin + gy * cellHeight);       // Negate Z for Godot
                float z1 = -(RegionMin + (gy + 1) * cellHeight); // Negate Z for Godot

                // Altitude values scaled
                float y00 = (float)altitudeGrid[gy * gridWidth + gx] * AltitudeScale;
                float y10 = (float)altitudeGrid[gy * gridWidth + (gx + 1)] * AltitudeScale;
                float y01 = (float)altitudeGrid[(gy + 1) * gridWidth + gx] * AltitudeScale;
                float y11 = (float)altitudeGrid[(gy + 1) * gridWidth + (gx + 1)] * AltitudeScale;

                var v00 = new Vector3(x0, y00, z0);
                var v10 = new Vector3(x1, y10, z0);
                var v01 = new Vector3(x0, y01, z1);
                var v11 = new Vector3(x1, y11, z1);

                // Color based on altitude (lower = darker green, higher = rocky gray)
                var c00 = AltitudeToColor(y00);
                var c10 = AltitudeToColor(y10);
                var c01 = AltitudeToColor(y01);
                var c11 = AltitudeToColor(y11);

                // Triangle 1: v00, v01, v10
                st.SetColor(c00); st.AddVertex(v00);
                st.SetColor(c01); st.AddVertex(v01);
                st.SetColor(c10); st.AddVertex(v10);

                // Triangle 2: v10, v01, v11
                st.SetColor(c10); st.AddVertex(v10);
                st.SetColor(c01); st.AddVertex(v01);
                st.SetColor(c11); st.AddVertex(v11);
            }
        }

        st.GenerateNormals();

        var mesh = st.Commit();

        // Apply a material that uses vertex colors
        var mat = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            Roughness = 0.9f,
        };
        mesh.SurfaceSetMaterial(0, mat);

        GD.Print($"TerrainGenerator: Generated {gridWidth}x{gridHeight} terrain mesh ({(gridWidth - 1) * (gridHeight - 1) * 2} triangles)");
        return mesh;
    }

    private static Color AltitudeToColor(float altitude)
    {
        // altitude here is already scaled (0 to ~33 after 0.3x scale)
        float normalized = Mathf.Clamp(altitude / (110f * AltitudeScale), 0f, 1f);

        // Low altitude: dark green (forest floor)
        // Mid altitude: lighter green (meadow)
        // High altitude: gray-brown (rocky)
        if (normalized < 0.3f)
        {
            // Dark green to medium green
            return new Color(
                Mathf.Lerp(0.15f, 0.25f, normalized / 0.3f),
                Mathf.Lerp(0.3f, 0.45f, normalized / 0.3f),
                Mathf.Lerp(0.1f, 0.15f, normalized / 0.3f));
        }
        else if (normalized < 0.7f)
        {
            // Medium green to yellow-green
            float t = (normalized - 0.3f) / 0.4f;
            return new Color(
                Mathf.Lerp(0.25f, 0.4f, t),
                Mathf.Lerp(0.45f, 0.42f, t),
                Mathf.Lerp(0.15f, 0.12f, t));
        }
        else
        {
            // Yellow-green to gray-brown (rocky)
            float t = (normalized - 0.7f) / 0.3f;
            return new Color(
                Mathf.Lerp(0.4f, 0.45f, t),
                Mathf.Lerp(0.42f, 0.4f, t),
                Mathf.Lerp(0.12f, 0.3f, t));
        }
    }

    /// <summary>
    /// Bilinear interpolation of altitude at a given world position.
    /// Useful for snapping entities to terrain height.
    /// </summary>
    public static float GetAltitudeAt(double[] altitudeGrid, int gridWidth, int gridHeight, float worldX, float worldZ)
    {
        if (altitudeGrid == null) return 0f;

        // World to grid coordinates
        float gxf = (worldX - RegionMin) / (RegionMax - RegionMin) * gridWidth;
        float gyf = (-worldZ - RegionMin) / (RegionMax - RegionMin) * gridHeight; // Negate Z back to server space

        int gx = Mathf.Clamp((int)gxf, 0, gridWidth - 2);
        int gy = Mathf.Clamp((int)gyf, 0, gridHeight - 2);
        float fx = gxf - gx;
        float fy = gyf - gy;

        float y00 = (float)altitudeGrid[gy * gridWidth + gx];
        float y10 = (float)altitudeGrid[gy * gridWidth + (gx + 1)];
        float y01 = (float)altitudeGrid[(gy + 1) * gridWidth + gx];
        float y11 = (float)altitudeGrid[(gy + 1) * gridWidth + (gx + 1)];

        float top = Mathf.Lerp(y00, y10, fx);
        float bottom = Mathf.Lerp(y01, y11, fx);
        return Mathf.Lerp(top, bottom, fy) * AltitudeScale;
    }
}
