using Godot;

namespace CommunitySurvival.Core;

/// <summary>
/// Generates a heightmap mesh from RegionProfile altitude grid (50x50).
/// Region covers -500 to +500 (1000x1000 units). Each grid cell = 20x20 units.
/// Altitude scaled down for natural-looking terrain.
/// </summary>
public static class TerrainGenerator
{
    private const float AltitudeScale = 0.3f;
    private const float RegionMin = -500f;
    private const float RegionMax = 500f;

    public static ArrayMesh Generate(double[] grid, int width, int height)
    {
        if (grid == null || grid.Length != width * height) return null;

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);

        float cellW = (RegionMax - RegionMin) / width;
        float cellH = (RegionMax - RegionMin) / height;

        for (int gy = 0; gy < height - 1; gy++)
        {
            for (int gx = 0; gx < width - 1; gx++)
            {
                float x0 = RegionMin + gx * cellW;
                float x1 = RegionMin + (gx + 1) * cellW;
                float z0 = -(RegionMin + gy * cellH);
                float z1 = -(RegionMin + (gy + 1) * cellH);

                float y00 = (float)grid[gy * width + gx] * AltitudeScale;
                float y10 = (float)grid[gy * width + (gx + 1)] * AltitudeScale;
                float y01 = (float)grid[(gy + 1) * width + gx] * AltitudeScale;
                float y11 = (float)grid[(gy + 1) * width + (gx + 1)] * AltitudeScale;

                var v00 = new Vector3(x0, y00, z0);
                var v10 = new Vector3(x1, y10, z0);
                var v01 = new Vector3(x0, y01, z1);
                var v11 = new Vector3(x1, y11, z1);

                var c00 = AltColor(y00); var c10 = AltColor(y10);
                var c01 = AltColor(y01); var c11 = AltColor(y11);

                st.SetColor(c00); st.AddVertex(v00);
                st.SetColor(c01); st.AddVertex(v01);
                st.SetColor(c10); st.AddVertex(v10);

                st.SetColor(c10); st.AddVertex(v10);
                st.SetColor(c01); st.AddVertex(v01);
                st.SetColor(c11); st.AddVertex(v11);
            }
        }

        st.GenerateNormals();
        var mesh = st.Commit();
        mesh.SurfaceSetMaterial(0, new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            Roughness = 0.9f,
        });

        GD.Print($"TerrainGenerator: {width}x{height} mesh, {(width - 1) * (height - 1) * 2} tris");
        return mesh;
    }

    private static Color AltColor(float y)
    {
        float n = Mathf.Clamp(y / (110f * AltitudeScale), 0f, 1f);
        if (n < 0.3f)
            return new Color(
                Mathf.Lerp(0.15f, 0.25f, n / 0.3f),
                Mathf.Lerp(0.3f, 0.45f, n / 0.3f),
                Mathf.Lerp(0.1f, 0.15f, n / 0.3f));
        if (n < 0.7f)
        {
            float t = (n - 0.3f) / 0.4f;
            return new Color(
                Mathf.Lerp(0.25f, 0.4f, t),
                Mathf.Lerp(0.45f, 0.42f, t),
                Mathf.Lerp(0.15f, 0.12f, t));
        }
        {
            float t = (n - 0.7f) / 0.3f;
            return new Color(
                Mathf.Lerp(0.4f, 0.45f, t),
                Mathf.Lerp(0.42f, 0.4f, t),
                Mathf.Lerp(0.12f, 0.3f, t));
        }
    }

    /// <summary>
    /// Bilinear interpolation of altitude at a world position.
    /// </summary>
    public static float GetAltitudeAt(double[] grid, int width, int height, float worldX, float worldZ)
    {
        if (grid == null) return 0f;
        float gxf = (worldX - RegionMin) / (RegionMax - RegionMin) * width;
        float gyf = (-worldZ - RegionMin) / (RegionMax - RegionMin) * height;
        int gx = Mathf.Clamp((int)gxf, 0, width - 2);
        int gy = Mathf.Clamp((int)gyf, 0, height - 2);
        float fx = gxf - gx, fy = gyf - gy;
        float top = Mathf.Lerp((float)grid[gy * width + gx], (float)grid[gy * width + gx + 1], fx);
        float bot = Mathf.Lerp((float)grid[(gy + 1) * width + gx], (float)grid[(gy + 1) * width + gx + 1], fx);
        return Mathf.Lerp(top, bot, fy) * AltitudeScale;
    }
}
