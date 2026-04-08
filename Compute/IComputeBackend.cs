namespace EFieldSimulation.Compute;

/// <summary>
/// Abstraction over compute device. The ILGPU backend will own a Context +
/// Accelerator and keep device buffers warm across calls.
/// </summary>
public interface IComputeBackend : IDisposable
{
    string Name { get; }

    /// <summary>
    /// Direct Coulomb sum. Fills dst.Ex/Ey/Ez, and PosContrib/NegContrib
    /// iff dst.TracksSign. Progress reported between chunks.
    /// </summary>
    /// <param name="progressChunks">
    /// Number of progress reports. Trades granularity vs dispatch overhead.
    /// 32 is a good default (≈3% granularity, &lt;2 ms total overhead on GPU).
    /// </param>
    void ComputeCoulombField(
        SourceBuffer sources,
        in GridSpec grid,
        FieldBuffer dst,
        IProgress<double>? progress = null,
        int progressChunks = 32);
}

/// <summary>Global default backend. Set once at app startup.</summary>
public static class ComputeBackend
{
    private static IComputeBackend _default = new CpuComputeBackend();

    public static IComputeBackend Default
    {
        get => _default;
        set => _default = value ?? throw new ArgumentNullException(nameof(value));
    }
}

/// <summary>
/// Reference CPU backend. Also used in tests to validate GPU output.
/// </summary>
public sealed class CpuComputeBackend : IComputeBackend
{
    public string Name => $"CPU ({Environment.ProcessorCount} threads)";

    public void ComputeCoulombField(
        SourceBuffer sources, in GridSpec grid, FieldBuffer dst,
        IProgress<double>? progress, int progressChunks)
    {
        int total = grid.Total;
        if (total != dst.Count)
            throw new ArgumentException(
                $"GridSpec.Total ({total}) must equal FieldBuffer.Count ({dst.Count}).");

        // If no progress sink, don't pay the chunking barrier cost at all.
        int chunks = progress is null
            ? 1
            : Math.Clamp(progressChunks, 1, total);
        int chunkSize = (total + chunks - 1) / chunks;

        bool withSign = dst.TracksSign;

        for (int start = 0; start < total; start += chunkSize)
        {
            int end = Math.Min(start + chunkSize, total);

            if (withSign)
                CoulombKernels.ComputeRangeWithSignCpu(start, end, grid, sources, dst);
            else
                CoulombKernels.ComputeRangeCpu(start, end, grid, sources, dst);

            progress?.Report((double)end / total);
        }
    }

    public void Dispose() { /* stateless */ }
}