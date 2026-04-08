using System;
using System.IO;
using System.Runtime.InteropServices;
using EFieldSimulation.Models;
using HDF.PInvoke;

namespace EFieldSimulation.Services;

/// <summary>
/// Writes ElectricFieldData to HDF5 format compatible with the existing
/// Hdf5FieldLoader reader. Stores only structured grid data.
/// 
/// Datasets written:
///   grid_points   : float32 [N, 3]
///   field_vectors  : float32 [N, 3]
///   grid_bounds    : float32 [6]
///   grid_shape     : int32   [3]       (if structured)
///   charge_density : float64 scalar    (if present)
/// Attributes:
///   description    : string
///   creation_time  : string (ISO 8601)
/// </summary>
public static class Hdf5FieldWriter
{
    public static void Write(string path, ElectricFieldData field)
    {
        // Delete existing to avoid appending into a corrupted file
        if (File.Exists(path)) File.Delete(path);

        long fileId = H5F.create(path, H5F.ACC_TRUNC);
        if (fileId < 0) throw new IOException($"Failed to create HDF5 file: {path}");

        try
        {
            int n = field.PointCount;

            WriteFloat2D(fileId, "grid_points", field.GridPoints, n, 3);
            WriteFloat2D(fileId, "field_vectors", field.FieldVectors, n, 3);
            WriteFloat1D(fileId, "grid_bounds", field.GridBounds);

            if (field.GridShape != null && field.GridShape.Length == 3)
                WriteInt1D(fileId, "grid_shape", field.GridShape);

            if (field.ChargeDensity.HasValue)
                WriteScalarDouble(fileId, "charge_density", field.ChargeDensity.Value);

            WriteStringAttribute(fileId, "description",
                field.Description ?? "Exported from EFieldSimulation");
            WriteStringAttribute(fileId, "creation_time",
                DateTime.UtcNow.ToString("O"));
        }
        finally
        {
            H5F.close(fileId);
        }
    }

    // ── Dataset writers ──────────────────────────────────────

    private static void WriteFloat2D(long fileId, string name, float[,] data, int rows, int cols)
    {
        // Flatten to 1D for pinning
        var flat = new float[rows * cols];
        Buffer.BlockCopy(data, 0, flat, 0, flat.Length * sizeof(float));

        var dims = new ulong[] { (ulong)rows, (ulong)cols };
        long spaceId = H5S.create_simple(2, dims, null);
        long typeId = H5T.copy(H5T.NATIVE_FLOAT);

        // Enable chunking + compression for large arrays
        long dcpl = H5P.create(H5P.DATASET_CREATE);
        var chunkDims = new ulong[]
        {
            (ulong)Math.Min(rows, 4096),
            (ulong)cols
        };
        H5P.set_chunk(dcpl, 2, chunkDims);
        H5P.set_deflate(dcpl, 4); // gzip level 4 — good balance

        long dsId = H5D.create(fileId, name, typeId, spaceId, dcpl_id: dcpl);

        var handle = GCHandle.Alloc(flat, GCHandleType.Pinned);
        try
        {
            H5D.write(dsId, typeId, H5S.ALL, H5S.ALL, H5P.DEFAULT, handle.AddrOfPinnedObject());
        }
        finally
        {
            handle.Free();
            H5D.close(dsId);
            H5P.close(dcpl);
            H5T.close(typeId);
            H5S.close(spaceId);
        }
    }

    private static void WriteFloat1D(long fileId, string name, float[] data)
    {
        var dims = new ulong[] { (ulong)data.Length };
        long spaceId = H5S.create_simple(1, dims, null);
        long typeId = H5T.copy(H5T.NATIVE_FLOAT);
        long dsId = H5D.create(fileId, name, typeId, spaceId);

        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            H5D.write(dsId, typeId, H5S.ALL, H5S.ALL, H5P.DEFAULT, handle.AddrOfPinnedObject());
        }
        finally
        {
            handle.Free();
            H5D.close(dsId);
            H5T.close(typeId);
            H5S.close(spaceId);
        }
    }

    private static void WriteInt1D(long fileId, string name, int[] data)
    {
        var dims = new ulong[] { (ulong)data.Length };
        long spaceId = H5S.create_simple(1, dims, null);
        long typeId = H5T.copy(H5T.NATIVE_INT32);
        long dsId = H5D.create(fileId, name, typeId, spaceId);

        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            H5D.write(dsId, typeId, H5S.ALL, H5S.ALL, H5P.DEFAULT, handle.AddrOfPinnedObject());
        }
        finally
        {
            handle.Free();
            H5D.close(dsId);
            H5T.close(typeId);
            H5S.close(spaceId);
        }
    }

    private static void WriteScalarDouble(long fileId, string name, double value)
    {
        long spaceId = H5S.create(H5S.class_t.SCALAR);
        long typeId = H5T.copy(H5T.NATIVE_DOUBLE);
        long dsId = H5D.create(fileId, name, typeId, spaceId);

        var data = new double[] { value };
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            H5D.write(dsId, typeId, H5S.ALL, H5S.ALL, H5P.DEFAULT, handle.AddrOfPinnedObject());
        }
        finally
        {
            handle.Free();
            H5D.close(dsId);
            H5T.close(typeId);
            H5S.close(spaceId);
        }
    }

    private static void WriteStringAttribute(long fileId, string name, string value)
    {
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(value);

        long typeId = H5T.copy(H5T.C_S1);
        H5T.set_size(typeId, new IntPtr(utf8.Length));
        H5T.set_cset(typeId, H5T.cset_t.UTF8);

        long spaceId = H5S.create(H5S.class_t.SCALAR);
        long attrId = H5A.create(fileId, name, typeId, spaceId);

        var handle = GCHandle.Alloc(utf8, GCHandleType.Pinned);
        try
        {
            H5A.write(attrId, typeId, handle.AddrOfPinnedObject());
        }
        finally
        {
            handle.Free();
            H5A.close(attrId);
            H5S.close(spaceId);
            H5T.close(typeId);
        }
    }
}