namespace EFieldSimulation.Compute;

/// <summary>
/// Low-contention progress tracking for data-parallel workloads.
///
/// Instead of one atomic increment per work item (cache-line ping-pong),
/// workers report once per *chunk*. With a chunk size of 4096 on 100K items,
/// that's ~25 atomic ops total instead of 100K — negligible contention,
/// same observable granularity as the old (done & 0xFFF) gate.
///
/// GPU mapping: launch kernel per chunk, call <see cref="ReportChunk"/>
/// after each Accelerator.Synchronize(). Same interface, no kernel-side atomics.
/// </summary>
public sealed class ChunkedProgressTracker
{
    private readonly IProgress<double>? _sink;
    private readonly int _totalItems;
    private readonly int _chunkSize;
    private readonly int _chunkCount;
    private long _itemsDone;   // atomic

    public ChunkedProgressTracker(IProgress<double>? sink, int totalItems, int chunkSize = 4096)
    {
        _sink = sink;
        _totalItems = totalItems;
        _chunkSize = Math.Max(1, chunkSize);
        _chunkCount = (totalItems + _chunkSize - 1) / _chunkSize;
    }

    public int ChunkSize => _chunkSize;
    public int ChunkCount => _chunkCount;

    /// <summary>Half-open range [start, end) for the given chunk index.</summary>
    public (int start, int end) GetChunk(int chunkIndex)
    {
        int start = chunkIndex * _chunkSize;
        int end = Math.Min(start + _chunkSize, _totalItems);
        return (start, end);
    }

    /// <summary>
    /// Call once after a chunk finishes. One atomic add + one progress report.
    /// Thread-safe.
    /// </summary>
    public void ReportChunk(int itemsInChunk)
    {
        if (_sink is null) return;
        long d = Interlocked.Add(ref _itemsDone, itemsInChunk);
        _sink.Report((double)d / _totalItems);
    }

    /// <summary>Force a final 100% report — covers rounding on the last partial chunk.</summary>
    public void Complete() => _sink?.Report(1.0);
}