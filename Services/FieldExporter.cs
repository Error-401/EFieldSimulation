using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using EFieldSimulation.Models;

namespace EFieldSimulation.Services;

/// <summary>
/// Exports a structured-grid ElectricFieldData to a standalone HDF5-compatible file.
/// Uses the same binary layout as the project blob but wrapped in a self-describing
/// header so external tools (Python/h5py) can read it.
/// 
/// If full HDF5 writing is available via Hdf5FieldLoader (write path), delegate there.
/// Otherwise this writes a minimal custom binary format with .h5 extension note:
/// we reuse the existing Hdf5FieldLoader infrastructure for HDF5 I/O.
/// </summary>
public static class FieldExporter
{
    /// <summary>
    /// Export the field data from a SceneEntry to an HDF5 file.
    /// Saves only the structured grid field — no raw tetrahedral data.
    /// </summary>
    public static void ExportToHdf5(string path, ElectricFieldData field)
    {
        if (field == null)
            throw new ArgumentNullException(nameof(field));

        // Use the existing HDF5 infrastructure (Hdf5FieldLoader handles reading;
        // we add a symmetric write path using the same library).
        Hdf5FieldWriter.Write(path, field);
    }
}