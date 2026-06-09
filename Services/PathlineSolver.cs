using System.Numerics;
using EFieldSimulation.Models;

namespace EFieldSimulation.Services;

public sealed class PathlineParams
{
    public Vector3 PlaneCenter { get; set; } = Vector3.Zero;
    public Vector3 PlaneNormal { get; set; } = Vector3.UnitZ;
    public float PlaneWidth { get; set; } = 2f;
    public float PlaneHeight { get; set; } = 2f;
    public int GridDensity { get; set; } = 5;
    public float InitialSpeed { get; set; } = 1e6f;   // m/s along normal
    public bool IsElectron { get; set; } = true;
    public int MaxSteps { get; set; } = 400;
    public float TimeStep { get; set; } = 1e-12f; // seconds
}

public static class PathlineSolver
{
    private const float ElectronCharge = -1.6022e-19f;
    private const float ElectronMass = 9.1094e-31f;
    private const float ProtonCharge = 1.6022e-19f;
    private const float ProtonMass = 1.6726e-27f;
    private const float MaxSpeedSq = (3e8f * 0.99f) * (3e8f * 0.99f);

    /// <summary>
    /// Integrate particle paths launched from a regular grid on a plane.
    /// Each path point is in world space. Uses RK4 on F = qE.
    /// </summary>
    public static List<List<Vector3>> Compute(
    IReadOnlyList<SceneEntry> fieldEntries,
    PathlineParams p)
    {
        if (fieldEntries.Count == 0) return new();

        Vector3 normal = Vector3.Normalize(p.PlaneNormal);
        Vector3 tmp = MathF.Abs(normal.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        Vector3 uAxis = Vector3.Normalize(Vector3.Cross(normal, tmp));
        Vector3 vAxis = Vector3.Cross(normal, uAxis);

        float qOverM = (p.IsElectron ? ElectronCharge : ProtonCharge)
                     / (p.IsElectron ? ElectronMass : ProtonMass);
        float particleChargeSign = p.IsElectron ? -1f : 1f;

        int n = Math.Max(1, p.GridDensity);
        var result = new List<List<Vector3>>(n * n);

        for (int iu = 0; iu < n; iu++)
            for (int iv = 0; iv < n; iv++)
            {
                float fu = n > 1 ? iu / (float)(n - 1) - 0.5f : 0f;
                float fv = n > 1 ? iv / (float)(n - 1) - 0.5f : 0f;

                Vector3 startPos = p.PlaneCenter
                    + fu * p.PlaneWidth * uAxis
                    + fv * p.PlaneHeight * vAxis;

                var path = Integrate(fieldEntries, startPos, normal * p.InitialSpeed,
                                     qOverM, particleChargeSign, p.MaxSteps, p.TimeStep);
                if (path.Count > 1) result.Add(path);
            }
        return result;
    }

    // ── RK4 integrator ──────────────────────────────────────────────────────
    private static List<Vector3> Integrate(
     IReadOnlyList<SceneEntry> entries,
     Vector3 pos, Vector3 vel,
     float qOverM, float particleChargeSign, int maxSteps, float dt)
    {
        var path = new List<Vector3>(maxSteps + 1) { pos };

        for (int s = 0; s < maxSteps; s++)
        {
            // k1
            Vector3 e1 = SampleWithChargeSign(entries, pos, particleChargeSign);
            Vector3 k1v = e1 * qOverM;
            Vector3 k1p = vel;

            // k2
            Vector3 e2 = SampleWithChargeSign(entries, pos + k1p * (dt * 0.5f), particleChargeSign);
            Vector3 k2v = e2 * qOverM;
            Vector3 k2p = vel + k1v * (dt * 0.5f);

            // k3
            Vector3 e3 = SampleWithChargeSign(entries, pos + k2p * (dt * 0.5f), particleChargeSign);
            Vector3 k3v = e3 * qOverM;
            Vector3 k3p = vel + k2v * (dt * 0.5f);

            // k4
            Vector3 e4 = SampleWithChargeSign(entries, pos + k3p * dt, particleChargeSign);
            Vector3 k4v = e4 * qOverM;
            Vector3 k4p = vel + k3v * dt;

            vel += (dt / 6f) * (k1v + 2f * k2v + 2f * k3v + k4v);
            pos += (dt / 6f) * (k1p + 2f * k2p + 2f * k3p + k4p);

            float vSq = vel.LengthSquared();
            if (vSq > MaxSpeedSq) vel *= MathF.Sqrt(MaxSpeedSq / vSq);

            path.Add(pos);

            // Only stop when the interpolator confirms we are inside bounds but
            // the field is genuinely negligible — not merely outside the grid.
            // Out-of-bounds returns exactly Vector3.Zero; we guard with s > 4 to
            // allow particles that start near the edge to enter the domain first.
            bool insideDomain = IsInsideDomain(entries, pos);
            if (insideDomain && s > 4 && Sample(entries, pos).Length() < 1e-20f) break;
            if (!insideDomain && s > 20) break;  // left domain entirely, no point continuing
        }
        return path;
    }

    // qOverM already carries the particle charge sign. The charge sign field only
    // tells us the polarity of the source region so we can orient the stored field
    // vector consistently — it does NOT additionally scale the force.
    private static Vector3 SampleWithChargeSign(IReadOnlyList<SceneEntry> entries, Vector3 pos, float particleChargeSign)
    {
        var v = Vector3.Zero;
        foreach (var e in entries)
        {
            var (field, chargeSign) = FieldSuperposition.SampleEntryWithChargeSign(e, pos);

            if (field.LengthSquared() < 1e-50f)
                continue;

            // The stored field vectors point away from positive sources by convention.
            // If this region is negative, the vectors were generated from a negative
            // source and point inward (toward the source), which is the opposite of
            // the convention, so we flip to restore consistent outward-from-positive
            // orientation before qOverM applies the particle's own sign.

            // This may actually cause bugs, so use cautiously
            float orientationFlip;
            if (chargeSign < -0.5f)
            {
                orientationFlip = -1f;  // negative source: flip to positive-source convention
            }
            else if (chargeSign > 0.5f)
            {
                orientationFlip = 1f;   // positive source: already correct convention
            }
            else
            {
                // Stagnation/neutral region with non-zero field.
                // Assume the field orientation matches the particle being traced,
                // meaning no additional flip is needed beyond what qOverM provides.
                orientationFlip = 1f;
            }

            v += field * orientationFlip;
        }
        return v;
    }
    private static Vector3 Sample(IReadOnlyList<SceneEntry> entries, Vector3 pos)
    {
        var v = Vector3.Zero;
        foreach (var e in entries) v += FieldSuperposition.SampleEntry(e, pos);
        return v;
    }

    private static bool IsInsideDomain(IReadOnlyList<SceneEntry> entries, Vector3 pos)
    {
        foreach (var e in entries)
        {
            if (e.Field == null) continue;
            var (min, max) = e.Field.GetBounds();
            var localPos = e.Transform.WorldToLocal(pos);
            if (localPos.X >= min.X && localPos.X <= max.X &&
                localPos.Y >= min.Y && localPos.Y <= max.Y &&
                localPos.Z >= min.Z && localPos.Z <= max.Z)
                return true;
        }
        return false;
    }
}