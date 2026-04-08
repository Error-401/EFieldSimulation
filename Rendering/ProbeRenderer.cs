using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using EFieldSimulation.Models;

namespace EFieldSimulation.Rendering;

/// <summary>
/// Builds 3D marker geometry for probes in the viewport.
/// </summary>
public static class ProbeRenderer
{
    /// <summary>
    /// Adds probe markers to the scene group.
    /// Point probes: cyan diamond. Line probes: yellow line + magenta endpoints.
    /// </summary>
    public static void AddProbeMarkers(
        Model3DGroup group,
        IEnumerable<ProbeDefinition> probes,
        double markerSize = 0.06)
    {
        foreach (var probe in probes)
        {
            if (!probe.IsVisible) continue;

            var ptA = new Point3D(probe.AX, probe.AY, probe.AZ);

            if (probe.Type == ProbeType.Point)
            {
                group.Children.Add(CreateDiamondMarker(
                    ptA, markerSize, Color.FromRgb(0, 220, 220)));

                // If evaluated, draw a small arrow showing E direction
                if (probe.Result?.Samples.Length > 0)
                {
                    var s = probe.Result.Samples[0];
                    if (s.TotalFieldMagnitude > 1e-10f)
                    {
                        var dir = Vector3.Normalize(s.TotalField);
                        group.Children.Add(CreateDirectionIndicator(
                            ptA, new Vector3D(dir.X, dir.Y, dir.Z),
                            markerSize * 4, Color.FromRgb(0, 255, 255)));
                    }
                }
            }
            else // LineSegment
            {
                var ptB = new Point3D(probe.BX, probe.BY, probe.BZ);

                // Endpoints
                group.Children.Add(CreateDiamondMarker(
                    ptA, markerSize, Color.FromRgb(255, 0, 200)));
                group.Children.Add(CreateDiamondMarker(
                    ptB, markerSize, Color.FromRgb(255, 0, 200)));

                // Connecting line (thin cylinder)
                group.Children.Add(CreateLineCylinder(
                    ptA, ptB, markerSize * 0.3,
                    Color.FromRgb(255, 255, 0)));

                // If evaluated, draw sample-point ticks
                if (probe.Result != null)
                {
                    foreach (var s in probe.Result.Samples)
                    {
                        var sp = new Point3D(s.Position.X, s.Position.Y, s.Position.Z);
                        group.Children.Add(CreateDiamondMarker(
                            sp, markerSize * 0.4,
                            Color.FromRgb(255, 200, 60)));
                    }
                }
            }
        }
    }

    private static GeometryModel3D CreateDiamondMarker(
        Point3D center, double size, Color color)
    {
        var mesh = new MeshGeometry3D();
        double s = size;
        var top = new Point3D(center.X, center.Y + s, center.Z);
        var bot = new Point3D(center.X, center.Y - s, center.Z);
        var fwd = new Point3D(center.X, center.Y, center.Z + s * 0.7);
        var bak = new Point3D(center.X, center.Y, center.Z - s * 0.7);
        var lft = new Point3D(center.X - s * 0.7, center.Y, center.Z);
        var rgt = new Point3D(center.X + s * 0.7, center.Y, center.Z);

        AddTri(mesh, top, fwd, rgt);
        AddTri(mesh, top, rgt, bak);
        AddTri(mesh, top, bak, lft);
        AddTri(mesh, top, lft, fwd);
        AddTri(mesh, bot, rgt, fwd);
        AddTri(mesh, bot, bak, rgt);
        AddTri(mesh, bot, lft, bak);
        AddTri(mesh, bot, fwd, lft);

        return new GeometryModel3D
        {
            Geometry = mesh,
            Material = new EmissiveMaterial(new SolidColorBrush(color)),
            BackMaterial = new EmissiveMaterial(new SolidColorBrush(color))
        };
    }

    private static GeometryModel3D CreateDirectionIndicator(
        Point3D origin, Vector3D direction, double length, Color color)
    {
        direction.Normalize();
        var tip = origin + direction * length;

        var mesh = new MeshGeometry3D();
        double r = length * 0.08;

        // Build a simple triangular prism along the direction
        var perp1 = Vector3D.CrossProduct(direction, new Vector3D(0, 1, 0));
        if (perp1.LengthSquared < 0.01)
            perp1 = Vector3D.CrossProduct(direction, new Vector3D(1, 0, 0));
        perp1.Normalize();
        var perp2 = Vector3D.CrossProduct(direction, perp1);
        perp2.Normalize();

        var b0 = origin + perp1 * r;
        var b1 = origin + perp2 * r;
        var b2 = origin - perp1 * r;

        AddTri(mesh, b0, b1, tip);
        AddTri(mesh, b1, b2, tip);
        AddTri(mesh, b2, b0, tip);

        return new GeometryModel3D
        {
            Geometry = mesh,
            Material = new DiffuseMaterial(new SolidColorBrush(color))
        };
    }

    private static GeometryModel3D CreateLineCylinder(
        Point3D a, Point3D b, double radius, Color color)
    {
        var mesh = new MeshGeometry3D();
        var dir = b - a;
        if (dir.LengthSquared < 1e-20) return new GeometryModel3D { Geometry = mesh };
        dir.Normalize();

        var perp1 = Vector3D.CrossProduct(dir, new Vector3D(0, 1, 0));
        if (perp1.LengthSquared < 0.01)
            perp1 = Vector3D.CrossProduct(dir, new Vector3D(1, 0, 0));
        perp1.Normalize();
        var perp2 = Vector3D.CrossProduct(dir, perp1);
        perp2.Normalize();

        int seg = 6;
        for (int i = 0; i < seg; i++)
        {
            double ang0 = 2 * Math.PI * i / seg;
            double ang1 = 2 * Math.PI * (i + 1) / seg;
            var off0 = perp1 * (radius * Math.Cos(ang0)) + perp2 * (radius * Math.Sin(ang0));
            var off1 = perp1 * (radius * Math.Cos(ang1)) + perp2 * (radius * Math.Sin(ang1));

            var a0 = a + off0; var a1 = a + off1;
            var b0 = b + off0; var b1 = b + off1;
            AddTri(mesh, a0, b0, a1);
            AddTri(mesh, a1, b0, b1);
        }

        return new GeometryModel3D
        {
            Geometry = mesh,
            Material = new DiffuseMaterial(new SolidColorBrush(color)),
            BackMaterial = new DiffuseMaterial(new SolidColorBrush(color))
        };
    }

    private static void AddTri(MeshGeometry3D m, Point3D p0, Point3D p1, Point3D p2)
    {
        int idx = m.Positions.Count;
        m.Positions.Add(p0);
        m.Positions.Add(p1);
        m.Positions.Add(p2);
        m.TriangleIndices.Add(idx);
        m.TriangleIndices.Add(idx + 1);
        m.TriangleIndices.Add(idx + 2);
    }
}