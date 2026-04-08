using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using EFieldSimulation.Models;

namespace EFieldSimulation.Rendering;

/// <summary>
/// Renders a particle cloud as a single batched MeshGeometry3D.
/// Each particle is a camera-facing quad (billboard) — 4 vertices, 2 triangles.
/// Very lightweight compared to sphere or icosahedron per particle.
/// </summary>
public static class ParticleRenderer
{
    /// <summary>
    /// Create a single Model3D for the entire cloud.
    /// Particles are in entry-local space; entryTransform + shapeLocalRotation applied.
    /// </summary>
    public static GeometryModel3D CreateParticleModel(
        ParticleCloud cloud,
        ArbitraryShapeParams shapeParams,
        TransformState entryTransform,
        float particleSize = 0.03f)
    {
        bool positive = cloud.ChargePerParticle >= 0;
        var color = positive
            ? Color.FromArgb(200, 255, 60, 60)
            : Color.FromArgb(200, 60, 60, 255);

        var mesh = BuildBatchedPoints(cloud.Positions, particleSize);

        var model = new GeometryModel3D
        {
            Geometry = mesh,
            Material = new DiffuseMaterial(new SolidColorBrush(color)),
            BackMaterial = new DiffuseMaterial(new SolidColorBrush(color)),
            Transform = BuildCombinedTransform(shapeParams, entryTransform)
        };

        return model;
    }

    /// <summary>
    /// Batch all particles into one mesh using axis-aligned micro-quads.
    /// 4 verts + 6 indices per particle.
    /// </summary>
    private static MeshGeometry3D BuildBatchedPoints(
        Vector3[] positions, float size)
    {
        var mesh = new MeshGeometry3D();
        float h = size * 0.5f;

        // Pre-allocate
        int n = positions.Length;
        var pts = mesh.Positions;
        var idx = mesh.TriangleIndices;

        for (int i = 0; i < n; i++)
        {
            var p = positions[i];
            int baseVert = i * 4;

            // Quad 1: XY plane
            pts.Add(new Point3D(p.X - h, p.Y - h, p.Z));
            pts.Add(new Point3D(p.X + h, p.Y - h, p.Z));
            pts.Add(new Point3D(p.X + h, p.Y + h, p.Z));
            pts.Add(new Point3D(p.X - h, p.Y + h, p.Z));

            idx.Add(baseVert); idx.Add(baseVert + 1); idx.Add(baseVert + 2);
            idx.Add(baseVert); idx.Add(baseVert + 2); idx.Add(baseVert + 3);
        }

        return mesh;
    }

    private static Transform3D BuildCombinedTransform(
    ArbitraryShapeParams shapeParams,
    TransformState entryTransform)
    {
        var group = new Transform3DGroup();
        group.Children.Add(entryTransform.ToWpfTransform());

        return group;
    }
}