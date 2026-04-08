using System.Numerics;
using EFieldSimulation.Compute;
using EFieldSimulation.Models;

namespace EFieldSimulation.Services;

public static class CoulombSolver
{
    private const float K = 8.9875517873681764e9f;
    private const float MIN_R2 = 1e-12f;

    /// <summary>
    /// Compute Coulomb E-field from point charges onto a structured grid
    /// with explicitly provided world-space bounds.
    /// </summary>
    public static ElectricFieldData ComputeField(
        IReadOnlyList<(SceneEntry entry, ParticleCloud cloud)> sources,
        Vector3 gridMin, Vector3 gridMax,
        int gridPerAxis = 32,
        IProgress<double>? progress = null,
        IComputeBackend? backend = null)
    {
        backend ??= ComputeBackend.Default;

        // ── 1) Collect charges in world space ──
        int totalCharges = sources.Sum(s => s.cloud.Count);
        var worldPositions = new Vector3[totalCharges];
        var charges = new float[totalCharges];
        int offset = 0;

        Console.WriteLine($"[CoulombSolver] Grid: {gridMin} → {gridMax}");
        Console.WriteLine($"[CoulombSolver] First 5 world positions:");
        for (int i = 0; i < Math.Min(5, worldPositions.Length); i++)
            Console.WriteLine($"  [{i}] = {worldPositions[i]}");

        foreach (var (entry, cloud) in sources)
        {
            var mat = entry.Transform.ToMatrix4x4();
            //var shapeMat = BuildShapeLocalMatrix(entry.ShapeParams!);
            //var fullMat = shapeMat * mat;

            for (int i = 0; i < cloud.Count; i++)
            {
                worldPositions[offset] = Vector3.Transform(cloud.Positions[i], mat);
                charges[offset] = cloud.ChargePerParticle;
                offset++;
            }
        }

        // ── 2) Build grid spec ──
        int n = gridPerAxis;
        Vector3 range = gridMax - gridMin;
        var sp = new Vector3(
            n > 1 ? range.X / (n - 1) : 1f,
            n > 1 ? range.Y / (n - 1) : 1f,
            n > 1 ? range.Z / (n - 1) : 1f);

        var gridSpec = new GridSpec(
            gridMin.X, gridMin.Y, gridMin.Z,
            sp.X, sp.Y, sp.Z,
            n, n, n);

        // ── 3) Pack sources, allocate output, dispatch ──
        var srcBuf = SourceBuffer.From(worldPositions, charges);
        var fldBuf = new FieldBuffer(gridSpec.Total, trackSign: true);

        backend.ComputeCoulombField(srcBuf, gridSpec, fldBuf, progress);

        // ── 4) Repack to public shape ──
        return new ElectricFieldData
        {
            GridPoints = gridSpec.GenerateGridPoints(),
            FieldVectors = fldBuf.PackFieldVectors(),
            GridBounds = gridSpec.ToBoundsArray(),
            GridShape = gridSpec.ToShapeArray(),
            Description = $"Coulomb field from {totalCharges} particles"
        };
    }
}