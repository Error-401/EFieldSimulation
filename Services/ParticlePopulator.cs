using System.Numerics;
using EFieldSimulation.Models;

namespace EFieldSimulation.Services;

/// <summary>
/// Generates uniformly distributed random points inside arbitrary shapes
/// using parallelised rejection sampling.
/// </summary>
public static class ParticlePopulator
{
    public static ParticleCloud Populate(ArbitraryShapeParams p, TransformState transform)
    {
        int n = p.VolParticleCount;
        double sign = p.IsPositive ? 1.0 : -1.0;
        double volume = EstimateVolume(p);
        float qpp = (float)(p.VolChargeDensity * volume * sign / n);

        var positions = new Vector3[n];
        var (bmin, bmax) = GetShapeBounds(p, transform);

        int threads = Math.Max(1, Environment.ProcessorCount);
        int perThread = n / threads;

        Parallel.For(0, threads, tid =>
        {
            var rng = new Random(tid * 31337 + Environment.TickCount);
            int start = tid * perThread;
            int end = (tid == threads - 1) ? n : start + perThread;
            int filled = start;

            while (filled < end)
            {
                float x = Lerp(bmin.X, bmax.X, rng);
                float y = Lerp(bmin.Y, bmax.Y, rng);
                float z = Lerp(bmin.Z, bmax.Z, rng);

                var candidate = InverseLocalRotate(new Vector3(x, y, z), p, transform);

                if (IsInsideShape(candidate, p))
                {
                    positions[filled++] = candidate;
                }
            }
        });

        return new ParticleCloud { Positions = positions, ChargePerParticle = qpp };
    }


    private static bool IsInsideShape(Vector3 pt, ArbitraryShapeParams p)
    {
        double cx = p.CenterX, cy = p.CenterY, cz = p.CenterZ;
        double dx = pt.X - cx, dy = pt.Y - cy, dz = pt.Z - cz;

        switch (p.Type)
        {
            case "Cylinder":
                {
                    double r2 = dx * dx + dz * dz;
                    return r2 <= p.Radius * p.Radius &&
                           Math.Abs(dy) <= p.Height / 2.0;
                }

            case "Sphere":
                {
                    double r2 = dx * dx + dy * dy + dz * dz;
                    return r2 <= p.SphereRadius * p.SphereRadius;
                }

            case "Cone":
                {
                    if (Math.Abs(dy) > p.ConeHeight / 2.0) return false;
                    double t = (dy + p.ConeHeight / 2.0) / p.ConeHeight;
                    double rAtY = p.ConeBottomRadius + t * (p.ConeTopRadius - p.ConeBottomRadius);
                    return dx * dx + dz * dz <= rAtY * rAtY;
                }

            case "Torus":
            case "BoundedToroid":
                {
                    double R = p.MajorRadius, rr = p.MinorRadius;
                    double distXZ = Math.Sqrt(dx * dx + dz * dz);
                    double tubeR2 = (distXZ - R) * (distXZ - R) + dy * dy;
                    if (tubeR2 > rr * rr) return false;
                    if (p.Type == "Torus") return true;

                    double angle = Math.Atan2(dz, dx) * 180.0 / Math.PI;
                    if (angle < 0) angle += 360;
                    double start = p.AngleStartDeg % 360;
                    if (start < 0) start += 360;
                    double end = start + p.AngleSpanDeg;
                    if (end > 360)
                        return angle >= start || angle <= (end - 360);
                    return angle >= start && angle <= end;
                }

            case "Helix":
                {
                    // A tube of radius MinorRadius centred on the helix:
                    //   C(t) = (R·cos t, Pitch·t/(2π), R·sin t), t ∈ [0, 2π·Turns]
                    // We find the closest point on the helix to pt by scanning t,
                    // then check distance ≤ MinorRadius.
                    double R = p.MajorRadius;
                    double rr = p.MinorRadius;
                    double pitch = p.HelixPitch;
                    double turns = p.HelixTurns;
                    double tMax = 2.0 * Math.PI * turns;

                    // Approximate closest t via the azimuthal angle of (dx, dz)
                    double angle = Math.Atan2(dz, dx);
                    if (angle < 0) angle += 2.0 * Math.PI;

                    double bestDist2 = double.MaxValue;
                    // Check every full-turn copy of this angle, plus a fine local search
                    for (int turn = 0; turn <= (int)Math.Ceiling(turns); turn++)
                    {
                        double tCenter = angle + 2.0 * Math.PI * turn;
                        // Fine search around tCenter
                        for (int step = -4; step <= 4; step++)
                        {
                            double t = tCenter + step * (Math.PI / 32.0);
                            if (t < 0 || t > tMax) continue;
                            double hx = R * Math.Cos(t);
                            double hy = pitch * t / (2.0 * Math.PI);
                            double hz = R * Math.Sin(t);
                            double d2 = (dx - hx) * (dx - hx) + (dy - hy) * (dy - hy) + (dz - hz) * (dz - hz);
                            if (d2 < bestDist2) bestDist2 = d2;
                        }
                    }
                    return bestDist2 <= rr * rr;
                }

            case "HelicalToroid":
                {
                    double R = p.MajorRadius;
                    double rr = p.MinorRadius;
                    double turns = p.HelixTurns;
                    int steps = Math.Max(256, (int)(turns * 64));
                    double bestDist2 = double.MaxValue;

                    double angle = Math.Atan2(dz, dx) * 180.0 / Math.PI;
                    if (angle < 0) angle += 360;
                    double start = p.AngleStartDeg % 360;
                    if (start < 0) start += 360;
                    double end = start + p.AngleSpanDeg;
                    if (end > 360)
                    {
                        if (angle < start || angle > (end - 360))
                            return false;
                    }
                    else 
                    {
                        if (angle < start || angle > end)
                            return false;
                    }

                    for (int i = 0; i <= steps; i++)
                    {
                        double t = 2.0 * Math.PI * i / steps;
                        double cx2 = (R + rr * Math.Cos(turns * t)) * Math.Cos(t);
                        double cy2 = rr * Math.Sin(turns * t);
                        double cz2 = (R + rr * Math.Cos(turns * t)) * Math.Sin(t);
                        double d2 = (dx - cx2) * (dx - cx2) + (dy - cy2) * (dy - cy2) + (dz - cz2) * (dz - cz2);
                        if (d2 < bestDist2) bestDist2 = d2;
                    }

                    // Wire thickness is radius
                    double wireRadius = p.Radius;
                    return bestDist2 <= wireRadius * wireRadius;
                }

            default:
                return false;
        }
    }

    private static (Vector3 min, Vector3 max) GetShapeBounds(ArbitraryShapeParams p, TransformState transform)
    {
        double cx = p.CenterX, cy = p.CenterY, cz = p.CenterZ;
        float ex, ey, ez;

        switch (p.Type)
        {
            case "Cylinder":
                ex = (float)p.Radius;
                ey = (float)(p.Height / 2.0);
                ez = (float)p.Radius;
                break;
            case "Sphere":
                ex = ey = ez = (float)p.SphereRadius;
                break;
            case "Cone":
                ex = (float)Math.Max(p.ConeBottomRadius, p.ConeTopRadius);
                ey = (float)(p.ConeHeight / 2.0);
                ez = ex;
                break;
            case "Torus":
            case "BoundedToroid":
                ex = (float)(p.MajorRadius + p.MinorRadius);
                ey = (float)p.MinorRadius;
                ez = ex;
                break;
            case "Helix":
                ex = (float)(p.MajorRadius + p.MinorRadius);
                ey = (float)(p.HelixPitch * p.HelixTurns + p.MinorRadius);
                ez = ex;
                break;
            case "HelicalToroid":
                ex = (float)(p.MajorRadius + p.MinorRadius);
                ey = (float)p.MinorRadius;
                ez = ex;
                break;
            default:
                ex = ey = ez = 2f;
                break;
        }

        // Pad slightly
        ex *= 1.01f; ey *= 1.01f; ez *= 1.01f;

        var center = new Vector3((float)p.CenterX, (float)p.CenterY, (float)p.CenterZ);
        var rotMat = BuildLocalRotationMatrix(transform);

        // Transform canonical axis-aligned bbox corners through rotation
        Vector3[] corners = new Vector3[8];
        int ci = 0;
        for (int sx = -1; sx <= 1; sx += 2)
            for (int sy = -1; sy <= 1; sy += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                    corners[ci++] = center + Vector3.Transform(
                        new Vector3(sx * ex, sy * ey, sz * ez), rotMat);

        Vector3 bmin = corners[0], bmax = corners[0];
        for (int i = 1; i < 8; i++)
        {
            bmin = Vector3.Min(bmin, corners[i]);
            bmax = Vector3.Max(bmax, corners[i]);
        }
        return (bmin, bmax);
    }

    private static Matrix4x4 BuildLocalRotationMatrix(TransformState transform)
    {
        float rx = transform.RotX * MathF.PI / 180f;
        float ry = transform.RotY * MathF.PI / 180f;
        float rz = transform.RotZ * MathF.PI / 180f;
        return Matrix4x4.CreateRotationX(rx) *
               Matrix4x4.CreateRotationY(ry) *
               Matrix4x4.CreateRotationZ(rz);
    }

    private static Vector3 InverseLocalRotate(Vector3 pt, ArbitraryShapeParams p, TransformState transform)
    {
        var center = new Vector3((float)p.CenterX, (float)p.CenterY, (float)p.CenterZ);
        var mat = BuildLocalRotationMatrix(transform);
        if (!Matrix4x4.Invert(mat, out var inv)) return pt;
        return Vector3.Transform(pt - center, inv) + center;
    }

    private static double EstimateVolume(ArbitraryShapeParams p)
    {
        return p != null ? ShapeLibrary.EvaluateVolume(p) : 1.0;
    }

    private static float Lerp(float a, float b, Random rng) =>
        a + (b - a) * (float)rng.NextDouble();
}