using System.Numerics;
using EFieldSimulation.Models;

namespace EFieldSimulation.Services;

/// <summary>
/// Computes electrostatic potential on surfaces by integrating the total
/// electric field along radial paths to infinity (practically: to outside
/// the field grid union), assuming V(∞) = 0.
///   V(p) = ∫[p → ∞] E · dl
/// Includes ALL visible field entries (imported + Coulomb-derived).
/// </summary>
public static class VoltageSolver
{
    public static List<VoltageSurfaceResult> ComputeAll(
    IReadOnlyList<SceneEntry> voltageSurfaces,
    IReadOnlyList<SceneEntry> fieldEntries,
    int integrationSteps = 96,
    IProgress<double>? progress = null)
    {
        var results = new List<VoltageSurfaceResult>(voltageSurfaces.Count);
        if (voltageSurfaces.Count == 0 || fieldEntries.Count == 0)
            return results;

        Console.WriteLine($"[VoltageSolver] Field entries for sampling:");
        foreach (var e in fieldEntries)
        {
            Console.WriteLine($"  - '{e.Name}' IsCoulomb={e.IsCoulombDerived} " +
                              $"Transform=({e.Transform.X},{e.Transform.Y},{e.Transform.Z}) " +
                              $"Rot=({e.Transform.RotX},{e.Transform.RotY},{e.Transform.RotZ})");
            if (e.Field != null)
            {
                var (fmin, fmax) = e.Field.GetBounds();
                Console.WriteLine($"    Field bounds: {fmin} → {fmax}");
            }
        }

        var (unionMin, unionMax) = GetFieldUnionBounds(fieldEntries);
        Vector3 unionCenter = (unionMin + unionMax) * 0.5f;
        float unionDiag = Vector3.Distance(unionMin, unionMax);
        float infinityRadius = unionDiag * 1.25f; //1.5f

        int surfaceIdx = 0;
        foreach (var surf in voltageSurfaces)
        {
            if (surf.ShapeParams == null) { surfaceIdx++; continue; }

            var (verts, indices) = TessellateSurfaceWorld(surf);

            var voltages = new float[verts.Length];
            var emags = new float[verts.Length];

            Parallel.For(0, verts.Length, i =>
            {
                Vector3 p = verts[i];

                // |E| at the surface point (same sampling used for voltage)
                Vector3 e = SampleTotalField(fieldEntries, p);
                emags[i] = e.Length();

                if (i == 0 || i == verts.Length / 2)
                {
                    Console.WriteLine($"[VoltageSolver] Sampling at vertex[{i}] = {p}");
                    Console.WriteLine($"  → E = {e}, |E| = {emags[i]:E3}");
                }

                // Radial path p → ∞ for V
                Vector3 dir = p - unionCenter;
                float len = dir.Length();
                if (len < 1e-9f) dir = Vector3.UnitX; else dir /= len;
                Vector3 infinity = unionCenter + dir * infinityRadius;

                voltages[i] = IntegrateEdl(fieldEntries, p, infinity, integrationSteps);
            });

            // Diagnostic: count vertices where the field returned exactly zero
            // (indicates the surface extends outside the sampled field volume)
            int zeroCoverageCount = 0;
            for (int i = 0; i < emags.Length; i++)
                if (emags[i] < 1e-30f) zeroCoverageCount++;

            if (zeroCoverageCount > 0)
                Console.WriteLine(
                    $"[VoltageSolver] WARNING: {zeroCoverageCount}/{verts.Length} surface vertices " +
                    $"returned |E|=0 — surface may extend outside field grid bounds.");

            float vMin = float.MaxValue, vMax = float.MinValue;
            float eMin = float.MaxValue, eMax = float.MinValue;
            for (int i = 0; i < verts.Length; i++)
            {
                if (voltages[i] < vMin) vMin = voltages[i];
                if (voltages[i] > vMax) vMax = voltages[i];
                if (emags[i] < eMin) eMin = emags[i];
                if (emags[i] > eMax) eMax = emags[i];
            }
            if (vMin > vMax) { vMin = 0; vMax = 0; }
            if (eMin > eMax) { eMin = 0; eMax = 0; }

            int minVI = Array.IndexOf(voltages, vMin);
            int maxVI = Array.IndexOf(voltages, vMax);
            Console.WriteLine($"[VoltageSolver] Min V={vMin:E3} at vertex[{minVI}] pos={verts[minVI]}");
            Console.WriteLine($"[VoltageSolver] Max V={vMax:E3} at vertex[{maxVI}] pos={verts[maxVI]}");

            // Also check symmetry: for a symmetric charge, vertices at same radius 
            // but different Z should have very different voltages after 90° rotation
            // Find two side vertices at similar XY but different Z
            var sideVerts = verts.Select((v, i) => (v, i))
                .Where(x => Math.Abs(x.v.X) > 0.1f && Math.Abs(x.v.Y) < 0.05f)
                .Take(5)
                .ToList();
            Console.WriteLine($"[VoltageSolver] Side vertices (high X, low Y):");
            foreach (var (v, i) in sideVerts)
                Console.WriteLine($"  [{i}] pos={v} V={voltages[i]:E3}");

            results.Add(new VoltageSurfaceResult
            {
                Entry = surf,
                WorldVertices = verts,
                TriangleIndices = indices,
                Voltages = voltages,
                MinVoltage = vMin,
                MaxVoltage = vMax,
                FieldMagnitudes = emags,
                MinFieldMag = eMin,
                MaxFieldMag = eMax
            });

            surfaceIdx++;
            progress?.Report(surfaceIdx / (double)voltageSurfaces.Count);
        }
        return results;
    }

    /// <summary>Simpson ∫[a→b] E·dl along straight segment.</summary>
    private static float IntegrateEdl(
        IReadOnlyList<SceneEntry> fieldEntries,
        Vector3 a, Vector3 b, int steps)
    {
        if (steps < 2) steps = 2;
        if ((steps & 1) == 1) steps++;

        Vector3 dl = (b - a) / steps;
        float segLen = dl.Length();
        if (segLen < 1e-20f) return 0f;
        Vector3 dir = dl / segLen;

        double sum = 0;
        for (int k = 0; k <= steps; k++)
        {
            Vector3 p = a + dl * k;
            Vector3 e = SampleTotalField(fieldEntries, p);
            double f = Vector3.Dot(e, dir);
            int w = (k == 0 || k == steps) ? 1 : (k % 2 == 1 ? 4 : 2);
            sum += w * f;
        }
        return (float)((segLen / 3.0) * sum);
    }

    private static Vector3 SampleTotalField(
        IReadOnlyList<SceneEntry> entries, Vector3 worldPos)
    {
        Vector3 total = Vector3.Zero;
        foreach (var e in entries)
        {
            // DIAGNOSTIC: Check if any field entry has unexpected rotation
            if (e.IsCoulombDerived &&
                (Math.Abs(e.Transform.RotX) > 0.01f ||
                 Math.Abs(e.Transform.RotY) > 0.01f ||
                 Math.Abs(e.Transform.RotZ) > 0.01f))
            {
                Console.WriteLine($"[SampleTotalField] WARNING: Coulomb field '{e.Name}' " +
                                  $"has rotation ({e.Transform.RotX}, {e.Transform.RotY}, {e.Transform.RotZ})");
            }
            total += FieldSuperposition.SampleEntry(e, worldPos);
        }
        return total;
    }

    private static (Vector3 min, Vector3 max) GetFieldUnionBounds(
    IReadOnlyList<SceneEntry> fieldEntries)
    {
        // Each entry contributes 8 transformed corners; compute all in parallel
        var entryBounds = new (Vector3 min, Vector3 max)[fieldEntries.Count];

        Parallel.For(0, fieldEntries.Count, ei =>
        {
            var e = fieldEntries[ei];
            if (e.Field == null)
            {
                entryBounds[ei] = (new Vector3(float.MaxValue), new Vector3(float.MinValue));
                return;
            }
            var (fmin, fmax) = e.Field.GetBounds();
            var mat = e.Transform.ToMatrix4x4();
            Vector3 emin = new(float.MaxValue), emax = new(float.MinValue);
            Span<Vector3> c = stackalloc Vector3[8];
            c[0] = new(fmin.X, fmin.Y, fmin.Z); c[1] = new(fmax.X, fmin.Y, fmin.Z);
            c[2] = new(fmin.X, fmax.Y, fmin.Z); c[3] = new(fmax.X, fmax.Y, fmin.Z);
            c[4] = new(fmin.X, fmin.Y, fmax.Z); c[5] = new(fmax.X, fmin.Y, fmax.Z);
            c[6] = new(fmin.X, fmax.Y, fmax.Z); c[7] = new(fmax.X, fmax.Y, fmax.Z);
            for (int i = 0; i < 8; i++)
            {
                var tp = Vector3.Transform(c[i], mat);
                emin = Vector3.Min(emin, tp);
                emax = Vector3.Max(emax, tp);
            }
            entryBounds[ei] = (emin, emax);
        });

        Vector3 umin = new(float.MaxValue), umax = new(float.MinValue);
        for (int i = 0; i < entryBounds.Length; i++)
        {
            umin = Vector3.Min(umin, entryBounds[i].min);
            umax = Vector3.Max(umax, entryBounds[i].max);
        }
        if (umin.X > umax.X) { umin = -Vector3.One; umax = Vector3.One; }
        return (umin, umax);
    }

    // ── Surface tessellation with triangle indices ──────────
    private static (Vector3[] verts, int[] indices) TessellateSurfaceWorld(SceneEntry entry)
    {
        var p = entry.ShapeParams!;
        var (local, indices) = TessellateCanonical(p);

        // DIAGNOSTIC: Print AABB of local verts before any rotation
        var lmin = local.Aggregate(Vector3.Min);
        var lmax = local.Aggregate(Vector3.Max);
        Console.WriteLine($"[TessellateSurface] Local AABB before rotation: " +
                          $"min={lmin} max={lmax}");
        Console.WriteLine($"[TessellateSurface] ShapeParams: Type={p.Type}, " +
                          $"Center=({p.CenterX},{p.CenterY},{p.CenterZ}), " +
                          $"RotX={entry.Transform.RotX}, RotY={entry.Transform.RotY}, RotZ={entry.Transform.RotZ}");

        // bug
        var shapeRot = BuildShapeLocalRotation(p, entry.Transform);
        var entryMat = entry.Transform.ToMatrix4x4();

        var translationOnly = Matrix4x4.CreateTranslation(
            entry.Transform.X, entry.Transform.Y, entry.Transform.Z);
        var full = shapeRot * translationOnly;

        var world = new Vector3[local.Count];
        for (int i = 0; i < local.Count; i++)
            world[i] = Vector3.Transform(local[i], full);

        // DIAGNOSTIC: Print AABB of world verts after full transform  
        var wmin = world.Aggregate(Vector3.Min);
        var wmax = world.Aggregate(Vector3.Max);
        Console.WriteLine($"[TessellateSurface] World AABB after transform: " +
                          $"min={wmin} max={wmax}");

        return (world, indices);
    }

    private static (List<Vector3> verts, int[] indices) TessellateCanonical(
    ArbitraryShapeParams p)
    {
        try
        {
            var (v, i, _) = ShapeLibrary.Tessellate(p);
            return (new List<Vector3>(v), i);
        }
        catch
        {
            // Old default-case behaviour: single point at centre, no triangles.
            return (new List<Vector3>
                { new((float)p.CenterX, (float)p.CenterY, (float)p.CenterZ) },
                    Array.Empty<int>());
        }
    }

    private static Matrix4x4 BuildShapeLocalRotation(ArbitraryShapeParams p, TransformState transform)
    {
        // Use rotation from TransformState, not ShapeParams
        float rx = transform.RotX * MathF.PI / 180f;
        float ry = transform.RotY * MathF.PI / 180f;
        float rz = transform.RotZ * MathF.PI / 180f;

        var center = new Vector3((float)p.CenterX, (float)p.CenterY, (float)p.CenterZ);

        return Matrix4x4.CreateTranslation(-center)
             * Matrix4x4.CreateRotationX(rx)
             * Matrix4x4.CreateRotationY(ry)
             * Matrix4x4.CreateRotationZ(rz)
             * Matrix4x4.CreateTranslation(center);
    }
}

public sealed class VoltageSurfaceResult
{
    public SceneEntry Entry { get; init; } = null!;
    public Vector3[] WorldVertices { get; init; } = Array.Empty<Vector3>();
    public int[] TriangleIndices { get; init; } = Array.Empty<int>();
    public float[] Voltages { get; init; } = Array.Empty<float>();
    public float MinVoltage { get; init; }
    public float MaxVoltage { get; init; }

    // |E| sampled at the same surface vertices
    public float[] FieldMagnitudes { get; init; } = Array.Empty<float>();
    public float MinFieldMag { get; init; }
    public float MaxFieldMag { get; init; }

    public float AverageVoltage
    {
        get
        {
            if (Voltages.Length == 0) return 0f;
            double s = 0; foreach (var v in Voltages) s += v;
            return (float)(s / Voltages.Length);
        }
    }

    public float AverageFieldMag
    {
        get
        {
            if (FieldMagnitudes.Length == 0) return 0f;
            double s = 0; foreach (var v in FieldMagnitudes) s += v;
            return (float)(s / FieldMagnitudes.Length);
        }
    }
}