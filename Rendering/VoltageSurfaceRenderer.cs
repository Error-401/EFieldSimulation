using System.Windows.Media;
using System.Windows.Media.Media3D;
using EFieldSimulation.Helpers;
using EFieldSimulation.Services;

namespace EFieldSimulation.Rendering;

/// <summary>
/// Renders a voltage surface as a vertex-coloured 3D mesh using
/// the existing Jet colour map for the voltage distribution.
/// </summary>
public static class VoltageSurfaceRenderer
{
    public static GeometryModel3D CreateModel(VoltageSurfaceResult result)
    {
        var mesh = new MeshGeometry3D();
        var verts = result.WorldVertices;
        var idx = result.TriangleIndices;
        var volts = result.Voltages;
        int n = verts.Length;

        // DIAGNOSTIC: Verify data integrity
        Console.WriteLine($"[VoltageSurfaceRenderer] n={n}, indices={idx.Length}, volts={volts.Length}");
        Console.WriteLine($"  vertex[0] = ({verts[0].X:F4}, {verts[0].Y:F4}, {verts[0].Z:F4}), V={volts[0]:E3}");
        if (n > 100)
        {
            Console.WriteLine($"  vertex[100] = ({verts[100].X:F4}, {verts[100].Y:F4}, {verts[100].Z:F4}), V={volts[100]:E3}");
        }
        int mid = n / 2;
        Console.WriteLine($"  vertex[{mid}] = ({verts[mid].X:F4}, {verts[mid].Y:F4}, {verts[mid].Z:F4}), V={volts[mid]:E3}");

        // Check: for rotated cylinder, voltage should correlate with Z position (not Y)
        // Find min/max Z vertices and compare their voltages
        int minZIdx = 0, maxZIdx = 0;
        float minZ = float.MaxValue, maxZ = float.MinValue;
        for (int i = 0; i < n; i++)
        {
            if (verts[i].Z < minZ) { minZ = verts[i].Z; minZIdx = i; }
            if (verts[i].Z > maxZ) { maxZ = verts[i].Z; maxZIdx = i; }
        }
        Console.WriteLine($"  Min Z vertex[{minZIdx}]: Z={verts[minZIdx].Z:F4}, V={volts[minZIdx]:E3}");
        Console.WriteLine($"  Max Z vertex[{maxZIdx}]: Z={verts[maxZIdx].Z:F4}, V={volts[maxZIdx]:E3}");

        // Also check Y correlation (should NOT correlate for rotated cylinder)
        int minYIdx = 0, maxYIdx = 0;
        float minY = float.MaxValue, maxY = float.MinValue;
        for (int i = 0; i < n; i++)
        {
            if (verts[i].Y < minY) { minY = verts[i].Y; minYIdx = i; }
            if (verts[i].Y > maxY) { maxY = verts[i].Y; maxYIdx = i; }
        }
        Console.WriteLine($"  Min Y vertex[{minYIdx}]: Y={verts[minYIdx].Y:F4}, V={volts[minYIdx]:E3}");
        Console.WriteLine($"  Max Y vertex[{maxYIdx}]: Y={verts[maxYIdx].Y:F4}, V={volts[maxYIdx]:E3}");

        // Voltage normalisation range
        float vMin = result.MinVoltage, vMax = result.MaxVoltage;
        float span = vMax - vMin;
        if (span < 1e-30f) span = 1f;

        // Positions + per-vertex texture coord encodes normalised voltage on U axis
        for (int i = 0; i < n; i++)
        {
            mesh.Positions.Add(new Point3D(verts[i].X, verts[i].Y, verts[i].Z));
            float t = (volts[i] - vMin) / span;         // 0..1
            // Avoid exact 0/1 so bilinear sampling doesn't wrap
            t = 0.001f + t * 0.998f;
            mesh.TextureCoordinates.Add(new System.Windows.Point(t, 0.5));
        }
        for (int i = 0; i < idx.Length; i++)
            mesh.TriangleIndices.Add(idx[i]);

        // 1D gradient texture = Jet LUT
        var brush = BuildJetBrush();

        var mat = new MaterialGroup();
        mat.Children.Add(new DiffuseMaterial(brush));
        // light emissive so colour survives shading on back faces
        mat.Children.Add(new EmissiveMaterial(brush));

        var backBrush = BuildJetBrush(0.6);
        var backMat = new DiffuseMaterial(backBrush);

        var model = new GeometryModel3D
        {
            Geometry = mesh,
            Material = mat,
            BackMaterial = backMat
        };
        return model;
    }

    /// <summary>
    /// 256×1 horizontal gradient using MathHelpers.JetColorMap.
    /// Texture U coordinate selects voltage-mapped colour.
    /// </summary>
    private static Brush BuildJetBrush(double opacity = 1.0)
    {
        var stops = new GradientStopCollection();
        const int N = 64;
        for (int i = 0; i <= N; i++)
        {
            float t = i / (float)N;
            var (r, g, b) = MathHelpers.JetColorMap(t);
            stops.Add(new GradientStop(
                Color.FromArgb((byte)(opacity * 255), r, g, b), t));
        }
        var brush = new LinearGradientBrush(stops,
            new System.Windows.Point(0, 0.5),
            new System.Windows.Point(1, 0.5))
        {
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            // Must be absolute viewport so U coord indexes the gradient:
            // WPF maps TextureCoordinates to brush space directly.
            //ViewportUnits = BrushMappingMode.RelativeToBoundingBox
        };
        brush.Freeze();
        return brush;
    }
}