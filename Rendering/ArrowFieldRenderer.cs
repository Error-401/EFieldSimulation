using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using EFieldSimulation.Helpers;

namespace EFieldSimulation.Rendering;

/// <summary>
/// Creates 3D arrow glyphs for field visualization in the HelixToolkit viewport.
/// </summary>
public static class ArrowFieldRenderer
{
    /// <summary>
    /// Create a batch of arrows from position/direction/magnitude data.
    /// Returns a single Model3DGroup containing all arrows for efficiency.
    /// </summary>
    public static Model3DGroup CreateArrowField(
        IReadOnlyList<(Point3D position, Vector3D direction, double magnitude)> arrows,
        double maxMagnitude, double arrowScale = 1.0)
    {
        var group = new Model3DGroup();

        foreach (var (pos, dir, mag) in arrows)
        {
            if (mag < 1e-12) continue;

            double normalizedMag = mag / maxMagnitude;
            var (r, g, b) = MathHelpers.JetColorMap((float)normalizedMag);
            var color = Color.FromRgb(r, g, b);

            double length = arrowScale * normalizedMag;
            var normalizedDir = dir;
            normalizedDir.Normalize();

            var mesh = CreateSingleArrow(pos, normalizedDir, length, length * 0.05);
            group.Children.Add(new GeometryModel3D
            {
                Geometry = mesh,
                Material = new DiffuseMaterial(new SolidColorBrush(color))
            });
        }

        return group;
    }

    private static MeshGeometry3D CreateSingleArrow(
        Point3D origin, Vector3D direction, double length, double thickness)
    {
        var mesh = new MeshGeometry3D();

        Vector3D up = Math.Abs(direction.Y) < 0.9 ?
            new Vector3D(0, 1, 0) : new Vector3D(1, 0, 0);
        Vector3D right = Vector3D.CrossProduct(direction, up);
        right.Normalize();
        up = Vector3D.CrossProduct(right, direction);
        up.Normalize();

        // 6-sided prism shaft
        int sides = 6;
        double shaftLen = length * 0.75;
        Point3D tip = origin + direction * length;
        Point3D shaftEnd = origin + direction * shaftLen;

        int baseStart = mesh.Positions.Count;
        for (int i = 0; i <= sides; i++)
        {
            double angle = 2 * Math.PI * i / sides;
            Vector3D offset = right * (thickness * Math.Cos(angle)) +
                             up * (thickness * Math.Sin(angle));

            mesh.Positions.Add(origin + offset);
            mesh.Positions.Add(shaftEnd + offset);
        }

        for (int i = 0; i < sides; i++)
        {
            int b0 = baseStart + i * 2;
            int t0 = b0 + 1;
            int b1 = baseStart + (i + 1) * 2;
            int t1 = b1 + 1;

            mesh.TriangleIndices.Add(b0);
            mesh.TriangleIndices.Add(b1);
            mesh.TriangleIndices.Add(t0);
            mesh.TriangleIndices.Add(t0);
            mesh.TriangleIndices.Add(b1);
            mesh.TriangleIndices.Add(t1);
        }

        // Cone head
        int tipIdx = mesh.Positions.Count;
        mesh.Positions.Add(tip);

        int headBase = mesh.Positions.Count;
        double headR = thickness * 2.5;
        for (int i = 0; i <= sides; i++)
        {
            double angle = 2 * Math.PI * i / sides;
            mesh.Positions.Add(shaftEnd +
                right * (headR * Math.Cos(angle)) +
                up * (headR * Math.Sin(angle)));
        }

        for (int i = 0; i < sides; i++)
        {
            mesh.TriangleIndices.Add(tipIdx);
            mesh.TriangleIndices.Add(headBase + i);
            mesh.TriangleIndices.Add(headBase + i + 1);
        }

        return mesh;
    }
}