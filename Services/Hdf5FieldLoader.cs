using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using HDF.PInvoke;
using System.Runtime.InteropServices;
using EFieldSimulation.Models;
using System.IO;

namespace EFieldSimulation.Services;

/// <summary>
/// Reads the HDF5 electric field files matching the Python format exactly.
/// Uses HDF.PInvoke directly for reliable interop with the HDF5 C library.
/// </summary>
public static class Hdf5FieldLoader
{
    public static ElectricFieldData Load(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"HDF5 file not found: {filePath}");

        long fileId = H5F.open(filePath, H5F.ACC_RDONLY);
        if (fileId < 0) throw new IOException($"Cannot open HDF5 file: {filePath}");

        try
        {
            var data = new ElectricFieldData
            {
                GridPoints = ReadFloat2D(fileId, "grid_points"),
                FieldVectors = ReadFloat2D(fileId, "field_vectors"),
                GridBounds = ReadFloat1D(fileId, "grid_bounds")
            };

            if (DatasetExists(fileId, "grid_shape"))
            {
                var raw = ReadInt1D(fileId, "grid_shape");
                data.GridShape = raw;
            }

            if (DatasetExists(fileId, "mesh_vertices"))
                data.MeshVertices = ReadFloat2D(fileId, "mesh_vertices");

            if (DatasetExists(fileId, "mesh_faces"))
                data.MeshFaces = ReadInt2D(fileId, "mesh_faces");

            //if (DatasetExists(fileId, "charge_sign_field"))
            //    data.ChargeSignField = ReadSByte1D(fileId, "charge_sign_field");

            // Attributes
            data.ChargeDensity = ReadDoubleAttribute(fileId, "charge_density");
            data.CreationTime = ReadStringAttribute(fileId, "creation_time");
            data.Description = ReadStringAttribute(fileId, "description");

            return data;
        }
        finally
        {
            H5F.close(fileId);
        }
    }

    private static bool DatasetExists(long fileId, string name)
    {
        return H5L.exists(fileId, name) > 0;
    }

    private static float[,] ReadFloat2D(long fileId, string name)
    {
        long dset = H5D.open(fileId, name);
        long space = H5D.get_space(dset);
        int ndims = H5S.get_simple_extent_ndims(space);

        ulong[] dims = new ulong[ndims];
        H5S.get_simple_extent_dims(space, dims, null);

        int rows = (int)dims[0];
        int cols = ndims > 1 ? (int)dims[1] : 1;

        float[] flat = new float[rows * cols];
        GCHandle handle = GCHandle.Alloc(flat, GCHandleType.Pinned);
        try
        {
            H5D.read(dset, H5T.NATIVE_FLOAT, H5S.ALL, H5S.ALL,
                H5P.DEFAULT, handle.AddrOfPinnedObject());
        }
        finally
        {
            handle.Free();
        }

        H5S.close(space);
        H5D.close(dset);

        float[,] result = new float[rows, cols];
        Buffer.BlockCopy(flat, 0, result, 0, flat.Length * sizeof(float));
        return result;
    }

    private static float[] ReadFloat1D(long fileId, string name)
    {
        long dset = H5D.open(fileId, name);
        long space = H5D.get_space(dset);
        ulong[] dims = new ulong[1];
        H5S.get_simple_extent_dims(space, dims, null);

        float[] result = new float[(int)dims[0]];
        GCHandle handle = GCHandle.Alloc(result, GCHandleType.Pinned);
        try
        {
            H5D.read(dset, H5T.NATIVE_FLOAT, H5S.ALL, H5S.ALL,
                H5P.DEFAULT, handle.AddrOfPinnedObject());
        }
        finally
        {
            handle.Free();
        }

        H5S.close(space);
        H5D.close(dset);
        return result;
    }

    private static int[] ReadInt1D(long fileId, string name)
    {
        long dset = H5D.open(fileId, name);
        long space = H5D.get_space(dset);
        ulong[] dims = new ulong[1];
        H5S.get_simple_extent_dims(space, dims, null);

        int[] result = new int[(int)dims[0]];
        GCHandle handle = GCHandle.Alloc(result, GCHandleType.Pinned);
        try
        {
            H5D.read(dset, H5T.NATIVE_INT32, H5S.ALL, H5S.ALL,
                H5P.DEFAULT, handle.AddrOfPinnedObject());
        }
        finally
        {
            handle.Free();
        }

        H5S.close(space);
        H5D.close(dset);
        return result;
    }

    private static int[,] ReadInt2D(long fileId, string name)
    {
        long dset = H5D.open(fileId, name);
        long space = H5D.get_space(dset);
        ulong[] dims = new ulong[2];
        H5S.get_simple_extent_dims(space, dims, null);

        int rows = (int)dims[0];
        int cols = (int)dims[1];

        int[] flat = new int[rows * cols];
        GCHandle handle = GCHandle.Alloc(flat, GCHandleType.Pinned);
        try
        {
            H5D.read(dset, H5T.NATIVE_INT32, H5S.ALL, H5S.ALL,
                H5P.DEFAULT, handle.AddrOfPinnedObject());
        }
        finally
        {
            handle.Free();
        }

        H5S.close(space);
        H5D.close(dset);

        int[,] result = new int[rows, cols];
        Buffer.BlockCopy(flat, 0, result, 0, flat.Length * sizeof(int));
        return result;
    }
    private static sbyte[] ReadSByte1D(long fileId, string name)
    {
        long dset = H5D.open(fileId, name);
        long space = H5D.get_space(dset);
        ulong[] dims = new ulong[1];
        H5S.get_simple_extent_dims(space, dims, null);

        sbyte[] result = new sbyte[(int)dims[0]];
        GCHandle handle = GCHandle.Alloc(result, GCHandleType.Pinned);
        try
        {
            H5D.read(dset, H5T.NATIVE_INT8, H5S.ALL, H5S.ALL,
                H5P.DEFAULT, handle.AddrOfPinnedObject());
        }
        finally
        {
            handle.Free();
        }

        H5S.close(space);
        H5D.close(dset);
        return result;
    }
    private static double? ReadDoubleAttribute(long fileId, string name)
    {
        if (H5A.exists(fileId, name) <= 0) return null;
        long attr = H5A.open(fileId, name);
        double[] val = new double[1];
        GCHandle handle = GCHandle.Alloc(val, GCHandleType.Pinned);
        try
        {
            H5A.read(attr, H5T.NATIVE_DOUBLE, handle.AddrOfPinnedObject());
        }
        finally
        {
            handle.Free();
        }
        H5A.close(attr);
        return val[0];
    }

    private static string? ReadStringAttribute(long fileId, string name)
    {
        if (H5A.exists(fileId, name) <= 0) return null;

        long attr = H5A.open(fileId, name);
        long typeId = H5A.get_type(attr);
        long size = (long)H5T.get_size(typeId).ToInt64();

        byte[] buffer = new byte[size];
        GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            // For variable-length strings
            if (H5T.is_variable_str(typeId) > 0)
            {
                IntPtr[] ptrs = new IntPtr[1];
                GCHandle ptrHandle = GCHandle.Alloc(ptrs, GCHandleType.Pinned);
                try
                {
                    H5A.read(attr, typeId, ptrHandle.AddrOfPinnedObject());
                    if (ptrs[0] != IntPtr.Zero)
                    {
                        string result = Marshal.PtrToStringAnsi(ptrs[0]) ?? string.Empty;
                        H5A.close(attr);
                        H5T.close(typeId);
                        return result;
                    }
                }
                finally
                {
                    ptrHandle.Free();
                }
            }
            else
            {
                H5A.read(attr, typeId, handle.AddrOfPinnedObject());
            }
        }
        finally
        {
            handle.Free();
        }

        H5A.close(attr);
        H5T.close(typeId);

        return System.Text.Encoding.UTF8.GetString(buffer).TrimEnd('\0');
    }
}