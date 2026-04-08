namespace EFieldSimulation.Compute;

/// <summary>
/// Pure Coulomb summation kernels. CPU implementations; the ILGPU backend
/// will mirror these bodies with ArrayView&lt;float&gt; in place of float[].
/// No allocation, no virtual calls, no progress reporting inside.
/// </summary>
public static class CoulombKernels
{
    /// <summary>Coulomb constant (exact, 2019 SI). N·m²·C⁻².</summary>
    public const float Ke = 8.9875517923e9f;

    /// <summary>Skip sources closer than √this to avoid the singularity.</summary>
    public const float MinR2 = 1e-12f;

    /// <summary>
    /// Softening length squared. Prevents 1/r² singularity when grid points fall near source centroids.
    /// </summary>
    public static float Epsilon2 { get; set; } = 0f;

    /// <summary>
    /// E-field only, no sign tracking. Used by CoulombSolver.
    /// Processes grid indices [start, endExclusive). Parallel over that range.
    /// </summary>
    public static void ComputeRangeCpu(
        int start, int endExclusive,
        in GridSpec grid, SourceBuffer src, FieldBuffer dst)
    {
        // Hoist everything to locals — Parallel.For captures these by ref
        // but the JIT doesn't always eliminate the indirection through src/dst.
        float[] sX = src.X, sY = src.Y, sZ = src.Z, sQ = src.Q;
        int nSrc = src.Count;
        float[] ex = dst.Ex, ey = dst.Ey, ez = dst.Ez;

        int ny = grid.Ny, nz = grid.Nz, yz = ny * nz;
        float mX = grid.MinX, mY = grid.MinY, mZ = grid.MinZ;
        float dX = grid.SpX, dY = grid.SpY, dZ = grid.SpZ;
        float eps2 = Epsilon2;

        Parallel.For(start, endExclusive, idx =>
        {
            int ix = idx / yz;
            int rem = idx - ix * yz;
            int iy = rem / nz;
            int iz = rem - iy * nz;
            float px = mX + ix * dX;
            float py = mY + iy * dY;
            float pz = mZ + iz * dZ;

            float eX = 0f, eY = 0f, eZ = 0f;

            for (int c = 0; c < nSrc; c++)
            {
                float rx = px - sX[c];
                float ry = py - sY[c];
                float rz = pz - sZ[c];
                float r2 = (rx * rx + ry * ry + rz * rz) + eps2;
                if (r2 < MinR2) continue;

                float s = Ke * sQ[c] / (r2 * MathF.Sqrt(r2));
                eX += s * rx;
                eY += s * ry;
                eZ += s * rz;
            }

            ex[idx] = eX;
            ey[idx] = eY;
            ez[idx] = eZ;
        });
    }

    /// <summary>
    /// E-field + sign-contribution tracking. Used by MeshTetrahedralizer.
    /// Requires dst.TracksSign == true.
    /// </summary>
    public static void ComputeRangeWithSignCpu(
        int start, int endExclusive,
        in GridSpec grid, SourceBuffer src, FieldBuffer dst)
    {
        if (!dst.TracksSign)
            throw new ArgumentException(
                "FieldBuffer must be constructed with trackSign: true.", nameof(dst));

        float[] sX = src.X, sY = src.Y, sZ = src.Z, sQ = src.Q;
        int nSrc = src.Count;
        float[] ex = dst.Ex, ey = dst.Ey, ez = dst.Ez;
        float[] pc = dst.PosContrib!, nc = dst.NegContrib!;

        int ny = grid.Ny, nz = grid.Nz, yz = ny * nz;
        float mX = grid.MinX, mY = grid.MinY, mZ = grid.MinZ;
        float dX = grid.SpX, dY = grid.SpY, dZ = grid.SpZ;
        float eps2 = Epsilon2;

        Parallel.For(start, endExclusive, idx =>
        {
            int ix = idx / yz;
            int rem = idx - ix * yz;
            int iy = rem / nz;
            int iz = rem - iy * nz;
            float px = mX + ix * dX;
            float py = mY + iy * dY;
            float pz = mZ + iz * dZ;

            float eX = 0f, eY = 0f, eZ = 0f;
            float posC = 0f, negC = 0f;

            for (int c = 0; c < nSrc; c++)
            {
                float rx = px - sX[c];
                float ry = py - sY[c];
                float rz = pz - sZ[c];
                float r2 = (rx * rx + ry * ry + rz * rz) + eps2;
                if (r2 < MinR2) continue;

                float rMag = MathF.Sqrt(r2);
                float q = sQ[c];
                float s = Ke * q / (r2 * rMag);
                eX += s * rx;
                eY += s * ry;
                eZ += s * rz;

                // Ternary → selp on GPU (no divergence), predicted branch on CPU.
                float eMag = Ke * MathF.Abs(q) / r2;
                posC += eMag * (q > 0f ? 1f : 0f);
                negC += eMag * (q < 0f ? 1f : 0f);
            }

            ex[idx] = eX;
            ey[idx] = eY;
            ez[idx] = eZ;
            pc[idx] = posC;
            nc[idx] = negC;
        });
    }
}