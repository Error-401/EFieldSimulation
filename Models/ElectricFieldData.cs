using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Numerics;

namespace EFieldSimulation.Models;

/// <summary>
/// Mirrors the Python ElectricFieldData structure stored in HDF5.
/// grid_points: Nx3 array of (x,y,z) sample positions
/// field_vectors: Nx3 array of (Ex,Ey,Ez) at each grid point
/// grid_bounds: (xmin,xmax,ymin,ymax,zmin,zmax)
/// grid_shape: (nx,ny,nz) if structured grid
/// </summary>
public sealed class ElectricFieldData
{
    public float[,] GridPoints { get; set; } = null!;   // [N, 3]
    public float[,] FieldVectors { get; set; } = null!;  // [N, 3]
    public float[] GridBounds { get; set; } = null!;      // length 6
    public int[]? GridShape { get; set; }                  // length 3 or null
    public float[,]? MeshVertices { get; set; }
    public int[,]? MeshFaces { get; set; }
    public double? ChargeDensity { get; set; }
    public string? CreationTime { get; set; }
    public string? Description { get; set; }

    // Computed properties
    public int PointCount => GridPoints.GetLength(0);

    public Vector3 GetGridPoint(int i) =>
        new(GridPoints[i, 0], GridPoints[i, 1], GridPoints[i, 2]);

    public Vector3 GetFieldVector(int i) =>
        new(FieldVectors[i, 0], FieldVectors[i, 1], FieldVectors[i, 2]);

    public (Vector3 min, Vector3 max) GetBounds() => (
        new Vector3(GridBounds[0], GridBounds[2], GridBounds[4]),
        new Vector3(GridBounds[1], GridBounds[3], GridBounds[5])
    );

    /// <summary>
    /// Build a fast lookup structure for structured grids.
    /// Returns null if grid is unstructured.
    /// </summary>
    public StructuredGridAccessor? BuildStructuredAccessor()
    {
        if (GridShape == null || GridShape.Length != 3) return null;
        return new StructuredGridAccessor(this);
    }
}

/// <summary>
/// Fast O(1) accessor for structured (regular) grids.
/// Supports trilinear interpolation.
/// </summary>
public sealed class StructuredGridAccessor
{
    private readonly ElectricFieldData _data;
    private readonly int _nx, _ny, _nz;
    private readonly Vector3 _min, _max, _spacing;

    public StructuredGridAccessor(ElectricFieldData data)
    {
        _data = data;
        _nx = data.GridShape![0];
        _ny = data.GridShape![1];
        _nz = data.GridShape![2];

        var (min, max) = data.GetBounds();
        _min = min;
        _max = max;
        _spacing = new Vector3(
            _nx > 1 ? (max.X - min.X) / (_nx - 1) : 1f,
            _ny > 1 ? (max.Y - min.Y) / (_ny - 1) : 1f,
            _nz > 1 ? (max.Z - min.Z) / (_nz - 1) : 1f
        );
    }

    public int Nx => _nx;
    public int Ny => _ny;
    public int Nz => _nz;
    public Vector3 Min => _min;
    public Vector3 Max => _max;
    public Vector3 Spacing => _spacing;

    private int FlatIndex(int ix, int iy, int iz) =>
        ix * _ny * _nz + iy * _nz + iz;

    public Vector3 GetField(int ix, int iy, int iz)
    {
        int idx = FlatIndex(
            Math.Clamp(ix, 0, _nx - 1),
            Math.Clamp(iy, 0, _ny - 1),
            Math.Clamp(iz, 0, _nz - 1));
        return _data.GetFieldVector(idx);
    }

    /// <summary>
    /// Trilinear interpolation of the field at an arbitrary world point.
    /// Returns zero if outside bounds.
    /// </summary>
    public Vector3 Interpolate(Vector3 worldPos)
    {
        Vector3 local = (worldPos - _min);
        float fx = local.X / _spacing.X;
        float fy = local.Y / _spacing.Y;
        float fz = local.Z / _spacing.Z;

        if (fx < 0 || fx > _nx - 1 || fy < 0 || fy > _ny - 1 || fz < 0 || fz > _nz - 1)
            return Vector3.Zero;

        int ix = Math.Min((int)fx, _nx - 2);
        int iy = Math.Min((int)fy, _ny - 2);
        int iz = Math.Min((int)fz, _nz - 2);

        float tx = fx - ix;
        float ty = fy - iy;
        float tz = fz - iz;

        // Trilinear
        Vector3 c000 = GetField(ix, iy, iz);
        Vector3 c100 = GetField(ix + 1, iy, iz);
        Vector3 c010 = GetField(ix, iy + 1, iz);
        Vector3 c110 = GetField(ix + 1, iy + 1, iz);
        Vector3 c001 = GetField(ix, iy, iz + 1);
        Vector3 c101 = GetField(ix + 1, iy, iz + 1);
        Vector3 c011 = GetField(ix, iy + 1, iz + 1);
        Vector3 c111 = GetField(ix + 1, iy + 1, iz + 1);

        Vector3 c00 = Vector3.Lerp(c000, c100, tx);
        Vector3 c10 = Vector3.Lerp(c010, c110, tx);
        Vector3 c01 = Vector3.Lerp(c001, c101, tx);
        Vector3 c11 = Vector3.Lerp(c011, c111, tx);

        Vector3 c0 = Vector3.Lerp(c00, c10, ty);
        Vector3 c1 = Vector3.Lerp(c01, c11, ty);

        return Vector3.Lerp(c0, c1, tz);
    }
}
