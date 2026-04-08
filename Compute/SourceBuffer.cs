using System.Numerics;

namespace EFieldSimulation.Compute;

/// <summary>
/// SoA charge sources. Arrays exposed raw for zero-overhead kernel access.
/// Treat as immutable after construction.
/// </summary>
public sealed class SourceBuffer
{
    public float[] X { get; }
    public float[] Y { get; }
    public float[] Z { get; }
    public float[] Q { get; }
    public int Count { get; }

    public SourceBuffer(int count)
    {
        Count = count;
        X = new float[count];
        Y = new float[count];
        Z = new float[count];
        Q = new float[count];
    }

    public static SourceBuffer From(
        IReadOnlyList<Vector3> positions, IReadOnlyList<float> charges)
    {
        if (positions.Count != charges.Count)
            throw new ArgumentException(
                $"positions ({positions.Count}) and charges ({charges.Count}) length mismatch.");

        var b = new SourceBuffer(positions.Count);
        for (int i = 0; i < b.Count; i++)
        {
            var p = positions[i];
            b.X[i] = p.X; b.Y[i] = p.Y; b.Z[i] = p.Z;
            b.Q[i] = charges[i];
        }
        return b;
    }

    /// <summary>Convenience: pack arrays directly (skips List interface overhead).</summary>
    public static SourceBuffer From(Vector3[] positions, float[] charges)
    {
        if (positions.Length != charges.Length)
            throw new ArgumentException("positions and charges length mismatch.");

        var b = new SourceBuffer(positions.Length);
        for (int i = 0; i < b.Count; i++)
        {
            b.X[i] = positions[i].X;
            b.Y[i] = positions[i].Y;
            b.Z[i] = positions[i].Z;
            b.Q[i] = charges[i];
        }
        return b;
    }
}