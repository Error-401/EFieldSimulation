using System.Numerics;
using EFieldSimulation.Models;

namespace EFieldSimulation.Services;

/// <summary>
/// Evaluates E-field and voltage at probe points with per-source breakdown.
/// Static (grid-based) sources use trilinear interpolation + path-integral voltage.
/// Particle sources use direct Coulomb summation + analytic voltage (kq/r).
/// </summary>
public static class ProbeSolver
{
    private const float Ke = 8.9875517923e9f;

    public static ProbeResult Evaluate(
        ProbeDefinition probe,
        IReadOnlyList<SceneEntry> allEntries,
        int voltageSteps = 64,
        IProgress<double>? progress = null)
    {
        // Separate source types
        var staticEntries = allEntries
            .Where(e => e.Field != null && e.IsVisible && !e.IsCoulombDerived)
            .ToList();

        var chargeVolumes = allEntries
            .Where(e => e.Particles != null && e.IsVisible
                        && e.Kind == SceneEntryKind.ChargeVolume)
            .ToList();

        // Union bounds for static-field voltage integration path
        var allFieldEntries = allEntries
            .Where(e => e.Field != null && e.IsVisible).ToList();
        var bounds = GetFieldUnionBounds(allFieldEntries);
        Vector3 center = Vector3.Zero;
        float infRadius = 10f;
        if (bounds.HasValue)
        {
            center = (bounds.Value.min + bounds.Value.max) * 0.5f;
            infRadius = Vector3.Distance(bounds.Value.min, bounds.Value.max) * 1.25f;
        }

        var samplePoints = GenerateSamplePoints(probe);
        var samples = new ProbePointSample[samplePoints.Length];

        Parallel.For(0, samplePoints.Length, i =>
        {
            var (t, pos, dist) = samplePoints[i];
            var contribs = new List<ProbeSourceContribution>();
            Vector3 staticFieldSum = Vector3.Zero;
            Vector3 particleFieldSum = Vector3.Zero;
            float vStaticSum = 0f, vParticleSum = 0f;

            // Infinity direction for voltage path integral
            Vector3 dir = pos - center;
            float len = dir.Length();
            if (len < 1e-9f) dir = Vector3.UnitX; else dir /= len;
            Vector3 infPoint = center + dir * infRadius;

            // ── Static entries: grid interpolation + path-integral voltage ──
            foreach (var entry in staticEntries)
            {
                Vector3 e = FieldSuperposition.SampleEntry(entry, pos);
                float v = bounds.HasValue
                    ? IntegrateEdlSingleEntry(entry, pos, infPoint, voltageSteps)
                    : 0f;

                contribs.Add(new ProbeSourceContribution
                {
                    SourceName = entry.Name,
                    SourceCategory = "Static",
                    IsCoulombDerived = false,
                    FieldVector = e,
                    VoltageContribution = v
                });
                staticFieldSum += e;
                vStaticSum += v;
            }

            // ── Charge volumes: direct Coulomb E + analytic V (kq/r) ──
            foreach (var cv in chargeVolumes)
            {
                var (e, v) = DirectCoulombAtPoint(cv, pos);

                contribs.Add(new ProbeSourceContribution
                {
                    SourceName = cv.Name,
                    SourceCategory = "Particles",
                    IsCoulombDerived = true,
                    FieldVector = e,
                    VoltageContribution = v
                });
                particleFieldSum += e;
                vParticleSum += v;
            }

            samples[i] = new ProbePointSample
            {
                T = t,
                Position = pos,
                Distance = dist,
                TotalField = staticFieldSum + particleFieldSum,
                TotalVoltage = vStaticSum + vParticleSum,
                Contributions = contribs.ToArray(),
                StaticFieldMagnitude = staticFieldSum.Length(),
                ParticleFieldMagnitude = particleFieldSum.Length(),
                StaticVoltage = vStaticSum,
                ParticleVoltage = vParticleSum
            };

            progress?.Report((double)(i + 1) / samplePoints.Length);
        });

        return new ProbeResult { Samples = samples };
    }

    // ── Direct Coulomb at a single world point from one charge volume ──
    private static (Vector3 field, float voltage) DirectCoulombAtPoint(
        SceneEntry chargeVol, Vector3 probePos)
    {
        var particles = chargeVol.Particles!;
        var mat = chargeVol.Transform.ToMatrix4x4();
        float q = particles.ChargePerParticle;

        Vector3 eTotal = Vector3.Zero;
        double vTotal = 0.0;

        for (int i = 0; i < particles.Count; i++)
        {
            Vector3 worldP = Vector3.Transform(particles.Positions[i], mat);
            Vector3 r = probePos - worldP;
            float r2 = r.LengthSquared();
            if (r2 < 1e-12f) continue;
            float rMag = MathF.Sqrt(r2);

            eTotal += (Ke * q / (r2 * rMag)) * r;
            vTotal += Ke * q / rMag;          // V = kq/r (exact, no integration needed)
        }

        return (eTotal, (float)vTotal);
    }

    // ── Simpson ∫E·dl for a single grid-based field entry ──
    private static float IntegrateEdlSingleEntry(
        SceneEntry entry, Vector3 a, Vector3 b, int steps)
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
            Vector3 e = FieldSuperposition.SampleEntry(entry, p);
            double f = Vector3.Dot(e, dir);
            int w = (k == 0 || k == steps) ? 1 : (k % 2 == 1 ? 4 : 2);
            sum += w * f;
        }
        return (float)(segLen / 3.0 * sum);
    }

    // ── Sample-point generation ──
    private static (float t, Vector3 pos, float dist)[] GenerateSamplePoints(
        ProbeDefinition probe)
    {
        if (probe.Type == ProbeType.Point)
            return new[] { (0f, probe.PointA, 0f) };

        int n = Math.Max(2, probe.SampleCount);
        var pts = new (float, Vector3, float)[n];
        Vector3 a = probe.PointA, b = probe.PointB;
        float totalDist = Vector3.Distance(a, b);

        for (int i = 0; i < n; i++)
        {
            float t = (float)i / (n - 1);
            pts[i] = (t, Vector3.Lerp(a, b, t), t * totalDist);
        }
        return pts;
    }

    // ── Union bounds (mirrors VoltageSolver logic) ──
    private static (Vector3 min, Vector3 max)? GetFieldUnionBounds(
        IReadOnlyList<SceneEntry> entries)
    {
        if (entries.Count == 0) return null;
        Vector3 umin = new(float.MaxValue), umax = new(float.MinValue);
        foreach (var e in entries)
        {
            if (e.Field == null) continue;
            var (fmin, fmax) = e.Field.GetBounds();
            var mat = e.Transform.ToMatrix4x4();
            Span<Vector3> corners = stackalloc Vector3[8];
            corners[0] = new(fmin.X, fmin.Y, fmin.Z); corners[1] = new(fmax.X, fmin.Y, fmin.Z);
            corners[2] = new(fmin.X, fmax.Y, fmin.Z); corners[3] = new(fmax.X, fmax.Y, fmin.Z);
            corners[4] = new(fmin.X, fmin.Y, fmax.Z); corners[5] = new(fmax.X, fmin.Y, fmax.Z);
            corners[6] = new(fmin.X, fmax.Y, fmax.Z); corners[7] = new(fmax.X, fmax.Y, fmax.Z);
            for (int i = 0; i < 8; i++)
            {
                var tp = Vector3.Transform(corners[i], mat);
                umin = Vector3.Min(umin, tp);
                umax = Vector3.Max(umax, tp);
            }
        }
        return umin.X < umax.X ? (umin, umax) : null;
    }
}