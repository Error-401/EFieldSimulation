using ILGPU;
using ILGPU.Algorithms;
using ILGPU.Runtime;
using ILGPU.Runtime.CPU;
using ILGPU.Runtime.Cuda;
using ILGPU.Runtime.OpenCL;

namespace EFieldSimulation.Compute;

/// <summary>
/// ILGPU-backed Coulomb solver. Owns one Context + Accelerator for its lifetime.
/// Kernel shape: one GPU thread per grid point, serial loop over all sources.
/// </summary>
public sealed class GpuComputeBackend : IComputeBackend
{
    private readonly object _computeLock = new();

    // ILGPU infrastructure
    private readonly Context _ctx;
    private readonly Accelerator _acc;
    private readonly bool _isRealGpu;
    private bool _disposed;

    // Cached loaded kernels
    // Grouped-kernel signature: first arg is KernelConfig, then the kernel's
    // parameters. We use explicit grouping so we can chunk the grid for
    // progress without re-uploading sources.
    private readonly Action<
        KernelConfig,
        int, int,                     // start, endExclusive (grid-point indices)
        GridSpec,                     // passed by value (blittable)
        ArrayView1D<float, Stride1D.Dense>, // srcX
        ArrayView1D<float, Stride1D.Dense>, // srcY
        ArrayView1D<float, Stride1D.Dense>, // srcZ
        ArrayView1D<float, Stride1D.Dense>, // srcQ
        int,                          // nSources
        float,                                    // eps2
        ArrayView1D<float, Stride1D.Dense>, // outEx
        ArrayView1D<float, Stride1D.Dense>, // outEy
        ArrayView1D<float, Stride1D.Dense>  // outEz
    > _kNoSign;

    private readonly Action<
        KernelConfig,
        int, int,
        GridSpec,
        ArrayView1D<float, Stride1D.Dense>,
        ArrayView1D<float, Stride1D.Dense>,
        ArrayView1D<float, Stride1D.Dense>,
        ArrayView1D<float, Stride1D.Dense>,
        int,
        float,
        ArrayView1D<float, Stride1D.Dense>,
        ArrayView1D<float, Stride1D.Dense>,
        ArrayView1D<float, Stride1D.Dense>,
        ArrayView1D<float, Stride1D.Dense>, // outPos
        ArrayView1D<float, Stride1D.Dense>  // outNeg
    > _kWithSign;

    // Pooled device buffers
    // Reallocated only when incoming data exceeds current capacity.
    // This avoids cudaMalloc/cudaFree churn on repeated calls.
    private MemoryBuffer1D<float, Stride1D.Dense>? _dSrcX, _dSrcY, _dSrcZ, _dSrcQ;
    private MemoryBuffer1D<float, Stride1D.Dense>? _dEx, _dEy, _dEz;
    private MemoryBuffer1D<float, Stride1D.Dense>? _dPos, _dNeg;
    private int _srcCapacity, _dstCapacity;
    private bool _signCapacity; // whether _dPos/_dNeg are currently allocated

    // Tuning knobs 
    /// <summary>Threads per block.
    public int GroupSize { get; init; } = 512;

    // ═════════════════════════════════════════════════════════
    //  Construction / Device selection
    // ═════════════════════════════════════════════════════════

    private GpuComputeBackend(Context ctx, Accelerator acc, bool isRealGpu)
    {
        _ctx = ctx;
        _acc = acc;
        _isRealGpu = isRealGpu;

        // LoadStreamKernel → explicit grouping. Control (gridDim, blockDim).
        _kNoSign = _acc.LoadStreamKernel<
            int, int, GridSpec,
            ArrayView1D<float, Stride1D.Dense>,
            ArrayView1D<float, Stride1D.Dense>,
            ArrayView1D<float, Stride1D.Dense>,
            ArrayView1D<float, Stride1D.Dense>,
            int,
            float,
            ArrayView1D<float, Stride1D.Dense>,
            ArrayView1D<float, Stride1D.Dense>,
            ArrayView1D<float, Stride1D.Dense>>(KernelNoSign);

        _kWithSign = _acc.LoadStreamKernel<
            int, int, GridSpec,
            ArrayView1D<float, Stride1D.Dense>,
            ArrayView1D<float, Stride1D.Dense>,
            ArrayView1D<float, Stride1D.Dense>,
            ArrayView1D<float, Stride1D.Dense>,
            int, float,
            ArrayView1D<float, Stride1D.Dense>,
            ArrayView1D<float, Stride1D.Dense>,
            ArrayView1D<float, Stride1D.Dense>,
            ArrayView1D<float, Stride1D.Dense>,
            ArrayView1D<float, Stride1D.Dense>>(KernelWithSign);
    }

    /// <summary>
    /// Try CUDA first, then OpenCL, then ILGPU's CPU accelerator.
    /// </summary>
    public static GpuComputeBackend? TryCreate(
        bool allowCpuFallback = false,
        Action<string>? log = null)
    {
        log ??= _ => { };
        Context ctx;
        try
        {
            // EnableAlgorithms() is REQUIRED for XMath.* to work. Without it will throw on first launch.
            ctx = Context.Create(b => b.Default().EnableAlgorithms());
        }
        catch (Exception ex)
        {
            log($"ILGPU context creation failed: {ex.Message}");
            return null;
        }

        var cudaDevs = ctx.GetCudaDevices();
        if (cudaDevs.Count > 0)
        {
            try
            {
                var acc = cudaDevs[0].CreateCudaAccelerator(ctx);
                log($"ILGPU: CUDA accelerator '{acc.Name}' " +
                    $"({acc.MemorySize / (1024 * 1024)} MB, " +
                    $"{acc.MaxNumThreadsPerGroup} max threads/group)");
                return new GpuComputeBackend(ctx, acc, isRealGpu: true);
            }
            catch (Exception ex)
            {
                log($"ILGPU: CUDA device present but init failed: {ex.Message}");
            }
        }
        else
        {
            log("ILGPU: no CUDA devices found.");
        }

        var clDevs = ctx.GetCLDevices();
        if (clDevs.Count > 0)
        {
            try
            {
                var acc = clDevs[0].CreateCLAccelerator(ctx);
                log($"ILGPU: OpenCL accelerator '{acc.Name}'");
                return new GpuComputeBackend(ctx, acc, isRealGpu: true);
            }
            catch (Exception ex)
            {
                log($"ILGPU: OpenCL init failed: {ex.Message}");
            }
        }

        if (allowCpuFallback)
        {
            var acc = ctx.CreateCPUAccelerator(0);
            log($"ILGPU: CPU accelerator fallback '{acc.Name}' " +
                "(slower than CpuComputeBackend — use for correctness only)");
            return new GpuComputeBackend(ctx, acc, isRealGpu: false);
        }

        log("ILGPU: no usable accelerator. Falling back to CpuComputeBackend.");
        ctx.Dispose();
        return null;
    }

    public string Name => _isRealGpu
        ? $"GPU ({_acc.AcceleratorType}: {_acc.Name})"
        : $"ILGPU-CPU ({_acc.Name})";

    /// <summary>True when running on actual GPU hardware (CUDA or OpenCL).</summary>
    public bool IsRealGpu => _isRealGpu;

    public long DeviceMemoryBytes => _acc.MemorySize;

    public void ComputeCoulombField(
        SourceBuffer sources, in GridSpec grid, FieldBuffer dst,
        IProgress<double>? progress = null, int progressChunks = 32)
    {
        // GridSpec is a readonly struct passed by 'in'; copy it before
        // the lock so we can pass by value inside without CS1628.
        var gridCopy = grid;

        lock (_computeLock)
        {
            ComputeCoulombFieldLocked(sources, gridCopy, dst, progress, progressChunks);
        }
    }

    private void ComputeCoulombFieldLocked(
        SourceBuffer sources, GridSpec grid, FieldBuffer dst,
        IProgress<double>? progress, int progressChunks)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int total = grid.Total;
        if (total != dst.Count)
            throw new ArgumentException(
                $"GridSpec.Total ({total}) must equal FieldBuffer.Count ({dst.Count}).");
        if (sources.Count == 0)
        {
            // Degenerate: no sources → zero field. Host arrays are already
            // zero-initialised by new float[n].
            progress?.Report(1.0);
            return;
        }

        bool withSign = dst.TracksSign;

        // Ensure device buffers are big enough
        EnsureSourceCapacity(sources.Count);
        EnsureOutputCapacity(total, withSign);

        // Upload sources
        // CopyFromCPU with a length arg uploads only the live portion;
        // the pooled buffer may be larger than sources.Count.
        _dSrcX!.View.SubView(0, sources.Count).CopyFromCPU(sources.X);
        _dSrcY!.View.SubView(0, sources.Count).CopyFromCPU(sources.Y);
        _dSrcZ!.View.SubView(0, sources.Count).CopyFromCPU(sources.Z);
        _dSrcQ!.View.SubView(0, sources.Count).CopyFromCPU(sources.Q);

        // Trimmed views — kernel sees exactly [0, count).
        var vSrcX = _dSrcX.View.SubView(0, sources.Count);
        var vSrcY = _dSrcY.View.SubView(0, sources.Count);
        var vSrcZ = _dSrcZ.View.SubView(0, sources.Count);
        var vSrcQ = _dSrcQ.View.SubView(0, sources.Count);

        var vEx = _dEx!.View.SubView(0, total);
        var vEy = _dEy!.View.SubView(0, total);
        var vEz = _dEz!.View.SubView(0, total);
        ArrayView1D<float, Stride1D.Dense> vPos = default, vNeg = default;
        if (withSign)
        {
            vPos = _dPos!.View.SubView(0, total);
            vNeg = _dNeg!.View.SubView(0, total);
        }

        // Chunked dispatch 
        // Each chunk is an independent kernel launch covering a contiguous
        // slice of grid points. Sources stay on-device across all chunks.
        int chunks = progress is null
            ? 1
            : Math.Clamp(progressChunks, 1, total);
        int chunkSize = (total + chunks - 1) / chunks;

        int nSrc = sources.Count;
        int group = Math.Min(GroupSize, _acc.MaxNumThreadsPerGroup);
        float eps2 = CoulombKernels.Epsilon2;

        for (int start = 0; start < total; start += chunkSize)
        {
            int end = Math.Min(start + chunkSize, total);
            int span = end - start;
            int numGroups = (span + group - 1) / group;
            var cfg = new KernelConfig(numGroups, group);

            if (withSign)
            {
                _kWithSign(cfg, start, end, grid,
                    vSrcX, vSrcY, vSrcZ, vSrcQ, nSrc, eps2,
                    vEx, vEy, vEz, vPos, vNeg);
            }
            else
            {
                _kNoSign(cfg, start, end, grid,
                    vSrcX, vSrcY, vSrcZ, vSrcQ, nSrc, eps2,
                    vEx, vEy, vEz);
            }

            if (progress is not null)
            {
                _acc.Synchronize();
                progress.Report((double)end / total);
            }
        }

        // If we skipped per-chunk sync (no progress), sync once before download.
        if (progress is null)
            _acc.Synchronize();

        // Download results
        // SubView → CopyToCPU writes directly into the caller's arrays;
        // no intermediate allocation.
        vEx.CopyToCPU(dst.Ex);
        vEy.CopyToCPU(dst.Ey);
        vEz.CopyToCPU(dst.Ez);
        if (withSign)
        {
            vPos.CopyToCPU(dst.PosContrib!);
            vNeg.CopyToCPU(dst.NegContrib!);
        }
    }

    // ═════════════════════════════════════════════════════════
    //  Buffer pool
    // ═════════════════════════════════════════════════════════

    private void EnsureSourceCapacity(int needed)
    {
        if (needed <= _srcCapacity) return;

        // Grow geometrically so repeated slightly-larger calls don't thrash the allocator.
        int newCap = Math.Max(needed, _srcCapacity + _srcCapacity / 2);

        _dSrcX?.Dispose(); _dSrcY?.Dispose();
        _dSrcZ?.Dispose(); _dSrcQ?.Dispose();

        _dSrcX = _acc.Allocate1D<float>(newCap);
        _dSrcY = _acc.Allocate1D<float>(newCap);
        _dSrcZ = _acc.Allocate1D<float>(newCap);
        _dSrcQ = _acc.Allocate1D<float>(newCap);
        _srcCapacity = newCap;
    }

    private void EnsureOutputCapacity(int needed, bool needSign)
    {
        bool growMain = needed > _dstCapacity;
        bool growSign = needSign && (!_signCapacity || needed > _dstCapacity);

        if (!growMain && !growSign) return;

        int newCap = growMain
            ? Math.Max(needed, _dstCapacity + _dstCapacity / 2)
            : _dstCapacity;

        if (growMain)
        {
            _dEx?.Dispose(); _dEy?.Dispose(); _dEz?.Dispose();
            _dEx = _acc.Allocate1D<float>(newCap);
            _dEy = _acc.Allocate1D<float>(newCap);
            _dEz = _acc.Allocate1D<float>(newCap);
            _dstCapacity = newCap;

            // If sign buffers exist but are now undersized, force regrow.
            if (_signCapacity) growSign = true;
        }

        if (growSign)
        {
            _dPos?.Dispose(); _dNeg?.Dispose();
            _dPos = _acc.Allocate1D<float>(_dstCapacity);
            _dNeg = _acc.Allocate1D<float>(_dstCapacity);
            _signCapacity = true;
        }
    }

    // ═════════════════════════════════════════════════════════
    //  Kernels
    // ═════════════════════════════════════════════════════════

    private static void KernelNoSign(
        int start, int endExclusive,
        GridSpec g,
        ArrayView1D<float, Stride1D.Dense> srcX,
        ArrayView1D<float, Stride1D.Dense> srcY,
        ArrayView1D<float, Stride1D.Dense> srcZ,
        ArrayView1D<float, Stride1D.Dense> srcQ,
        int nSrc, float eps2,
        ArrayView1D<float, Stride1D.Dense> outEx,
        ArrayView1D<float, Stride1D.Dense> outEy,
        ArrayView1D<float, Stride1D.Dense> outEz)
    {
        // Global linear thread id within this launch
        int tid = Grid.GlobalIndex.X;
        int idx = start + tid;
        if (idx >= endExclusive) return;

        // Reconstruct grid position from flat index.
        int yz = g.Ny * g.Nz;
        int ix = idx / yz;
        int rem = idx - ix * yz;
        int iy = rem / g.Nz;
        int iz = rem - iy * g.Nz;
        float px = g.MinX + ix * g.SpX;
        float py = g.MinY + iy * g.SpY;
        float pz = g.MinZ + iz * g.SpZ;

        float eX = 0f, eY = 0f, eZ = 0f;

        // All threads in a warp read srcX[c], srcY[c], ... at the same c
        // → broadcast from L1/L2, not 32 separate transactions. This is why
        // SoA wins here: each of the 4 reads is a single coalesced access.
        for (int c = 0; c < nSrc; c++)
        {
            float rx = px - srcX[c];
            float ry = py - srcY[c];
            float rz = pz - srcZ[c];
            float r2 = (rx * rx + ry * ry + rz * rz) + eps2;

            // Singularity guard. On GPU this compiles to predication, not a
            // real branch — warps stay converged. Fires ~never in practice.
            if (r2 < CoulombKernels.MinR2) continue;

            // XMath.Sqrt → sqrt.approx.f32 on CUDA (or sqrt.rn.f32 depending
            // on context opts). Good enough for this physics.
            float s = CoulombKernels.Ke * srcQ[c] / (r2 * XMath.Sqrt(r2));
            eX += s * rx;
            eY += s * ry;
            eZ += s * rz;
        }

        // One coalesced write per output array (consecutive idx → consecutive
        // addresses across the warp).
        outEx[idx] = eX;
        outEy[idx] = eY;
        outEz[idx] = eZ;
    }

    private static void KernelWithSign(
        int start, int endExclusive,
        GridSpec g,
        ArrayView1D<float, Stride1D.Dense> srcX,
        ArrayView1D<float, Stride1D.Dense> srcY,
        ArrayView1D<float, Stride1D.Dense> srcZ,
        ArrayView1D<float, Stride1D.Dense> srcQ,
        int nSrc, float eps2,
        ArrayView1D<float, Stride1D.Dense> outEx,
        ArrayView1D<float, Stride1D.Dense> outEy,
        ArrayView1D<float, Stride1D.Dense> outEz,
        ArrayView1D<float, Stride1D.Dense> outPos,
        ArrayView1D<float, Stride1D.Dense> outNeg)
    {
        int tid = Grid.GlobalIndex.X;
        int idx = start + tid;
        if (idx >= endExclusive) return;

        int yz = g.Ny * g.Nz;
        int ix = idx / yz;
        int rem = idx - ix * yz;
        int iy = rem / g.Nz;
        int iz = rem - iy * g.Nz;
        float px = g.MinX + ix * g.SpX;
        float py = g.MinY + iy * g.SpY;
        float pz = g.MinZ + iz * g.SpZ;

        float eX = 0f, eY = 0f, eZ = 0f;
        float posC = 0f, negC = 0f;

        for (int c = 0; c < nSrc; c++)
        {
            float rx = px - srcX[c];
            float ry = py - srcY[c];
            float rz = pz - srcZ[c];
            float r2 = (rx * rx + ry * ry + rz * rz) + eps2;
            if (r2 < CoulombKernels.MinR2) continue;

            float rMag = XMath.Sqrt(r2);
            float q = srcQ[c];
            float s = CoulombKernels.Ke * q / (r2 * rMag);
            eX += s * rx;
            eY += s * ry;
            eZ += s * rz;

            // Branchless sign masking. ILGPU lowers (cond ? 1f : 0f) to a
            // selp.f32 — no warp divergence regardless of charge distribution.
            // q==0 contributes to neither side, matching CPU semantics.
            float eMag = CoulombKernels.Ke * XMath.Abs(q) / r2;
            posC += eMag * (q > 0f ? 1f : 0f);
            negC += eMag * (q < 0f ? 1f : 0f);
        }

        outEx[idx] = eX;
        outEy[idx] = eY;
        outEz[idx] = eZ;
        outPos[idx] = posC;
        outNeg[idx] = negC;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _dSrcX?.Dispose(); _dSrcY?.Dispose();
        _dSrcZ?.Dispose(); _dSrcQ?.Dispose();
        _dEx?.Dispose(); _dEy?.Dispose(); _dEz?.Dispose();
        _dPos?.Dispose(); _dNeg?.Dispose();

        _acc.Dispose();
        _ctx.Dispose();
    }
}