namespace EFieldSimulation.Compute;

/// <summary>
/// Structured 3D sampling grid descriptor. Blittable — passed by value into kernels.
/// Flattening: idx = ix*(Ny*Nz) + iy*Nz + iz  (matches existing code).
/// </summary>
public readonly struct GridSpec
{
    public readonly float MinX, MinY, MinZ;
    public readonly float SpX, SpY, SpZ;   // spacing
    public readonly int Nx, Ny, Nz;

    public GridSpec(
        float minX, float minY, float minZ,
        float spX, float spY, float spZ,
        int nx, int ny, int nz)
    {
        MinX = minX; MinY = minY; MinZ = minZ;
        SpX = spX; SpY = spY; SpZ = spZ;
        Nx = nx; Ny = ny; Nz = nz;
    }

    public int Total => Nx * Ny * Nz;

    public float MaxX => MinX + (Nx - 1) * SpX;
    public float MaxY => MinY + (Ny - 1) * SpY;
    public float MaxZ => MinZ + (Nz - 1) * SpZ;

    /// <summary>Synthesise the [N,3] grid-point array that ElectricFieldData expects.</summary>
    public float[,] GenerateGridPoints()
    {
        int total = Total;
        int yz = Ny * Nz;
        var pts = new float[total, 3];
        for (int idx = 0; idx < total; idx++)
        {
            int ix = idx / yz;
            int rem = idx - ix * yz;     // cheaper than second %
            int iy = rem / Nz;
            int iz = rem - iy * Nz;
            pts[idx, 0] = MinX + ix * SpX;
            pts[idx, 1] = MinY + iy * SpY;
            pts[idx, 2] = MinZ + iz * SpZ;
        }
        return pts;
    }

    public float[] ToBoundsArray() => new[] { MinX, MaxX, MinY, MaxY, MinZ, MaxZ };

    public int[] ToShapeArray() => new[] { Nx, Ny, Nz };
}