namespace EFieldSimulation.Compute;

/// <summary>
/// SoA output buffer. Optionally carries pos/neg contribution sums for sign classification.
/// </summary>
public sealed class FieldBuffer
{
    public float[] Ex { get; }
    public float[] Ey { get; }
    public float[] Ez { get; }

    /// <summary>Σ|E| from positive charges at each point. Null if tracking disabled.</summary>
    public float[]? PosContrib { get; }
    /// <summary>Σ|E| from negative charges at each point. Null if tracking disabled.</summary>
    public float[]? NegContrib { get; }

    public int Count { get; }
    public bool TracksSign => PosContrib != null;

    public FieldBuffer(int count, bool trackSign)
    {
        Count = count;
        Ex = new float[count];
        Ey = new float[count];
        Ez = new float[count];
        if (trackSign)
        {
            PosContrib = new float[count];
            NegContrib = new float[count];
        }
    }

    /// <summary>Repack to the [N,3] shape ElectricFieldData.FieldVectors wants.</summary>
    public float[,] PackFieldVectors()
    {
        var f = new float[Count, 3];
        for (int i = 0; i < Count; i++)
        {
            f[i, 0] = Ex[i];
            f[i, 1] = Ey[i];
            f[i, 2] = Ez[i];
        }
        return f;
    }

    /// <summary>
    /// Derive per-point dominant charge sign from contribution ratios.
    /// Matches the original threshold logic.
    /// </summary>
    public sbyte[] ComputeSignField(float threshold = 0.33f)
    {
        if (PosContrib is null || NegContrib is null)
            throw new InvalidOperationException(
                "Sign tracking was not enabled on this FieldBuffer.");

        var sign = new sbyte[Count];
        for (int i = 0; i < Count; i++)
        {
            float p = PosContrib[i], n = NegContrib[i];
            float tot = p + n;
            if (tot > 1e-30f)
            {
                float s = (p - n) / tot;
                sign[i] = s > threshold ? (sbyte)1
                        : s < -threshold ? (sbyte)-1
                        : (sbyte)0;
            }
            // else leave at 0 (default)
        }
        return sign;
    }
}