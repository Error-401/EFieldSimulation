using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media.Media3D;
using EFieldSimulation.Models;

namespace EFieldSimulation.Services;

/// <summary>
/// Saves/loads the entire scene as a self-contained .efproj file (ZIP archive).
/// Layout:
///   manifest.json          – scene entries, global settings, blob references
///   blobs/mesh_{id}.bin    – interleaved vertex/index/normal/material data
///   blobs/field_{id}.bin   – grid points + field vectors + metadata
/// 
/// Large float[,] arrays are stored as raw little-endian binary for compactness.
/// The ZIP container provides CRC-based integrity and deflate compression.
/// No raw tetrahedral data is stored — only the final structured grid field.
/// </summary>
public static class ProjectSerializer
{
    private const string ManifestName = "manifest.json";
    private const string BlobPrefix = "blobs/";
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,                      // keep manifest compact
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // ═══════════════════════════════════════════════════════════
    //  SAVE
    // ═══════════════════════════════════════════════════════════

    public static void Save(string path, ProjectManifest manifest,
                            IReadOnlyList<SceneEntry> entries)
    {
        // Write to a temp file first, then move — protects against partial writes
        string tmp = path + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
            {
                var entryDtos = new List<SceneEntryDto>();

                foreach (var se in entries)
                {
                    var dto = SceneEntryToDto(se);

                    // Mesh blob
                    if (se.Mesh != null)
                    {
                        string blobName = $"{BlobPrefix}mesh_{se.Id}.bin";
                        dto.MeshBlob = blobName;
                        WriteMeshBlob(zip, blobName, se.Mesh);
                    }

                    // Field blob (structured grid only — no raw tet data)
                    if (se.Field != null)
                    {
                        string blobName = $"{BlobPrefix}field_{se.Id}.bin";
                        dto.FieldBlob = blobName;
                        WriteFieldBlob(zip, blobName, se.Field);
                    }

                    entryDtos.Add(dto);
                }

                manifest.Version = FormatVersion;
                manifest.Entries = entryDtos;
                manifest.SavedUtc = DateTime.UtcNow.ToString("O");

                var manifestEntry = zip.CreateEntry(ManifestName, CompressionLevel.Optimal);
                using var ms = manifestEntry.Open();
                JsonSerializer.Serialize(ms, manifest, JsonOpts);
            }

            // Atomic replace
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  LOAD
    // ═══════════════════════════════════════════════════════════

    public static (ProjectManifest manifest, List<SceneEntry> entries) Load(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

        var manifestEntry = zip.GetEntry(ManifestName)
            ?? throw new InvalidDataException("Missing manifest.json in project file.");

        ProjectManifest manifest;
        using (var ms = manifestEntry.Open())
        {
            manifest = JsonSerializer.Deserialize<ProjectManifest>(ms, JsonOpts)
                ?? throw new InvalidDataException("Failed to deserialize manifest.");
        }

        if (manifest.Version > FormatVersion)
            throw new InvalidDataException(
                $"Project file version {manifest.Version} is newer than supported ({FormatVersion}).");

        var entries = new List<SceneEntry>();
        foreach (var dto in manifest.Entries ?? Enumerable.Empty<SceneEntryDto>())
        {
            var se = DtoToSceneEntry(dto);

            if (dto.MeshBlob != null)
            {
                var blob = zip.GetEntry(dto.MeshBlob)
                    ?? throw new InvalidDataException($"Missing blob: {dto.MeshBlob}");
                using var bs = blob.Open();
                se.Mesh = ReadMeshBlob(bs);
            }

            if (dto.FieldBlob != null)
            {
                var blob = zip.GetEntry(dto.FieldBlob)
                    ?? throw new InvalidDataException($"Missing blob: {dto.FieldBlob}");
                using var bs = blob.Open();
                se.Field = ReadFieldBlob(bs);
                se.FieldAccessor = se.Field.BuildStructuredAccessor();
            }

            entries.Add(se);
        }

        return (manifest, entries);
    }

    // ═══════════════════════════════════════════════════════════
    //  DTO ↔ SceneEntry mapping
    // ═══════════════════════════════════════════════════════════

    private static SceneEntryDto SceneEntryToDto(SceneEntry se) => new()
    {
        Id = se.Id,
        Name = se.Name,
        Kind = se.Kind.ToString(),
        IsVisible = se.IsVisible,
        IsCoulombDerived = se.IsCoulombDerived,
        Transform = TransformToDto(se.Transform),
        Shape = se.ShapeParams != null ? ShapeToDto(se.ShapeParams) : null,
        HasParticles = se.Particles != null,
        ParticleChargePerParticle = se.Particles?.ChargePerParticle,
        ParticleCount = se.Particles?.Count,
    };

    private static SceneEntry DtoToSceneEntry(SceneEntryDto dto)
    {
        var kind = Enum.TryParse<SceneEntryKind>(dto.Kind, true, out var k)
            ? k : SceneEntryKind.Static;

        // SceneEntry.Kind is init-only, Id is read-only with default
        // We reconstruct; the Id won't match the original but that's fine
        // since Ids are only used for in-session blob naming.
        var se = new SceneEntry
        {
            Kind = kind,
            Name = dto.Name ?? "",
            IsVisible = dto.IsVisible,
            IsCoulombDerived = dto.IsCoulombDerived,
        };

        if (dto.Transform != null)
            ApplyTransformDto(dto.Transform, se.Transform);

        if (dto.Shape != null)
            se.ShapeParams = DtoToShape(dto.Shape);

        // Note: Particles are NOT saved as blobs. They are cheap to regenerate
        // via PopulateShape. We store only the parameters needed.

        return se;
    }

    // ── Transform ────────────────────────────────────────────

    private static TransformDto TransformToDto(TransformState t) => new()
    {
        X = t.X,
        Y = t.Y,
        Z = t.Z,
        RotX = t.RotX,
        RotY = t.RotY,
        RotZ = t.RotZ
    };

    private static void ApplyTransformDto(TransformDto dto, TransformState t)
    {
        t.X = dto.X; t.Y = dto.Y; t.Z = dto.Z;
        t.RotX = dto.RotX; t.RotY = dto.RotY; t.RotZ = dto.RotZ;
    }

    // ── Shape ────────────────────────────────────────────────

    private static ShapeDto ShapeToDto(ArbitraryShapeParams p) => new()
    {
        Type = p.Type,
        CenterX = p.CenterX,
        CenterY = p.CenterY,
        CenterZ = p.CenterZ,
        //RotationX = p.RotationX,
        //RotationY = p.RotationY,
        //RotationZ = p.RotationZ,
        Radius = p.Radius,
        Height = p.Height,
        MajorRadius = p.MajorRadius,
        MinorRadius = p.MinorRadius,
        AngleStartDeg = p.AngleStartDeg,
        AngleSpanDeg = p.AngleSpanDeg,
        HelixTurns = p.HelixTurns,
        HelixPitch = p.HelixPitch,
        RadialSegments = p.RadialSegments,
        TubularSegments = p.TubularSegments,
        SphereRadius = p.SphereRadius,
        ConeTopRadius = p.ConeTopRadius,
        ConeBottomRadius = p.ConeBottomRadius,
        ConeHeight = p.ConeHeight,
        VolChargeDensity = p.VolChargeDensity,
        IsPositive = p.IsPositive,
        VolParticleCount = p.VolParticleCount
    };

    private static ArbitraryShapeParams DtoToShape(ShapeDto d) => new()
    {
        Type = d.Type ?? "Cylinder",
        CenterX = d.CenterX,
        CenterY = d.CenterY,
        CenterZ = d.CenterZ,
        //RotationX = d.RotationX,
        //RotationY = d.RotationY,
        //RotationZ = d.RotationZ,
        Radius = d.Radius,
        Height = d.Height,
        MajorRadius = d.MajorRadius,
        MinorRadius = d.MinorRadius,
        AngleStartDeg = d.AngleStartDeg,
        AngleSpanDeg = d.AngleSpanDeg,
        HelixTurns = d.HelixTurns,
        HelixPitch = d.HelixPitch,
        RadialSegments = d.RadialSegments,
        TubularSegments = d.TubularSegments,
        SphereRadius = d.SphereRadius,
        ConeTopRadius = d.ConeTopRadius,
        ConeBottomRadius = d.ConeBottomRadius,
        ConeHeight = d.ConeHeight,
        VolChargeDensity = d.VolChargeDensity,
        IsPositive = d.IsPositive,
        VolParticleCount = d.VolParticleCount
    };

    // ═══════════════════════════════════════════════════════════
    //  Binary blob: Mesh
    //  Format: [int vertCount][int triIndexCount][int normalCount][int faceMaterialCount]
    //          [vertices: 3×double each][triIndices: int each][normals: 3×double each]
    //          [faceMaterials: UTF8 length-prefixed strings]
    //          [string name (UTF8 length-prefixed)]
    // ═══════════════════════════════════════════════════════════

    private static void WriteMeshBlob(ZipArchive zip, string entryName, MeshData mesh)
    {
        var ze = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = ze.Open();
        using var bw = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);

        bw.Write(mesh.Vertices.Length);
        bw.Write(mesh.TriangleIndices.Length);
        bw.Write(mesh.Normals.Length);
        bw.Write(mesh.FaceMaterials?.Length ?? 0);

        // Vertices (Point3D = 3 doubles)
        foreach (var v in mesh.Vertices)
        {
            bw.Write(v.X); bw.Write(v.Y); bw.Write(v.Z);
        }

        // Triangle indices
        foreach (var idx in mesh.TriangleIndices)
            bw.Write(idx);

        // Normals (Vector3D = 3 doubles)
        foreach (var n in mesh.Normals)
        {
            bw.Write(n.X); bw.Write(n.Y); bw.Write(n.Z);
        }

        // Face materials
        if (mesh.FaceMaterials != null)
        {
            foreach (var mat in mesh.FaceMaterials)
                bw.Write(mat ?? "");
        }

        // Name
        bw.Write(mesh.Name ?? "");
    }

    private static MeshData ReadMeshBlob(Stream stream)
    {
        using var br = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

        int vertCount = br.ReadInt32();
        int triCount = br.ReadInt32();
        int normalCount = br.ReadInt32();
        int faceMatCount = br.ReadInt32();

        var vertices = new Point3D[vertCount];
        for (int i = 0; i < vertCount; i++)
            vertices[i] = new Point3D(br.ReadDouble(), br.ReadDouble(), br.ReadDouble());

        var triIndices = new int[triCount];
        for (int i = 0; i < triCount; i++)
            triIndices[i] = br.ReadInt32();

        var normals = new Vector3D[normalCount];
        for (int i = 0; i < normalCount; i++)
            normals[i] = new Vector3D(br.ReadDouble(), br.ReadDouble(), br.ReadDouble());

        string[]? faceMaterials = null;
        if (faceMatCount > 0)
        {
            faceMaterials = new string[faceMatCount];
            for (int i = 0; i < faceMatCount; i++)
                faceMaterials[i] = br.ReadString();
        }

        string name = br.ReadString();

        var mesh = new MeshData
        {
            Name = name,
            Vertices = vertices,
            TriangleIndices = triIndices,
            Normals = normals,
            FaceMaterials = faceMaterials
        };

        // Recompute bounds
        if (vertices.Length > 0)
        {
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            foreach (var v in vertices)
            {
                if (v.X < minX) minX = v.X; if (v.X > maxX) maxX = v.X;
                if (v.Y < minY) minY = v.Y; if (v.Y > maxY) maxY = v.Y;
                if (v.Z < minZ) minZ = v.Z; if (v.Z > maxZ) maxZ = v.Z;
            }
            mesh.Bounds = new Rect3D(minX, minY, minZ,
                maxX - minX, maxY - minY, maxZ - minZ);
        }

        return mesh;
    }

    // ═══════════════════════════════════════════════════════════
    //  Binary blob: Field (structured grid E-field, no raw tet data)
    //  Format: [int pointCount][int hasShape(0/1)][int nx][int ny][int nz]
    //          [6× float gridBounds]
    //          [pointCount × 3 float gridPoints]
    //          [pointCount × 3 float fieldVectors]
    //          [double chargeDensity or NaN]
    //          [string description]
    // ═══════════════════════════════════════════════════════════

    private static void WriteFieldBlob(ZipArchive zip, string entryName, ElectricFieldData field)
    {
        var ze = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = ze.Open();
        using var bw = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);

        int n = field.PointCount;
        bool hasShape = field.GridShape != null && field.GridShape.Length == 3;

        bw.Write(n);
        bw.Write(hasShape ? 1 : 0);
        if (hasShape)
        {
            bw.Write(field.GridShape![0]);
            bw.Write(field.GridShape![1]);
            bw.Write(field.GridShape![2]);
        }

        // Grid bounds (always 6 floats)
        for (int i = 0; i < 6; i++)
            bw.Write(field.GridBounds[i]);

        // Grid points [N,3] as raw floats
        WriteFloat2D(bw, field.GridPoints, n, 3);

        // Field vectors [N,3] as raw floats
        WriteFloat2D(bw, field.FieldVectors, n, 3);

        // Charge density
        bw.Write(field.ChargeDensity ?? double.NaN);

        // Description
        bw.Write(field.Description ?? "");
    }

    private static ElectricFieldData ReadFieldBlob(Stream stream)
    {
        using var br = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

        int n = br.ReadInt32();
        int hasShapeFlag = br.ReadInt32();
        int[]? gridShape = null;
        if (hasShapeFlag == 1)
            gridShape = new[] { br.ReadInt32(), br.ReadInt32(), br.ReadInt32() };

        var gridBounds = new float[6];
        for (int i = 0; i < 6; i++)
            gridBounds[i] = br.ReadSingle();

        var gridPoints = ReadFloat2D(br, n, 3);
        var fieldVectors = ReadFloat2D(br, n, 3);

        double cd = br.ReadDouble();
        double? chargeDensity = double.IsNaN(cd) ? null : cd;

        string description = br.ReadString();

        return new ElectricFieldData
        {
            GridPoints = gridPoints,
            FieldVectors = fieldVectors,
            GridBounds = gridBounds,
            GridShape = gridShape,
            ChargeDensity = chargeDensity,
            Description = description.Length > 0 ? description : null
        };
    }

    // ── Raw float array I/O ──────────────────────────────────

    private static void WriteFloat2D(BinaryWriter bw, float[,] arr, int rows, int cols)
    {
        // Write as contiguous floats — BinaryWriter is little-endian on all .NET platforms
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                bw.Write(arr[r, c]);
    }

    private static float[,] ReadFloat2D(BinaryReader br, int rows, int cols)
    {
        var arr = new float[rows, cols];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                arr[r, c] = br.ReadSingle();
        return arr;
    }
}

// ═══════════════════════════════════════════════════════════
//  DTOs — lightweight JSON-serializable records
// ═══════════════════════════════════════════════════════════

public sealed class ProjectManifest
{
    public int Version { get; set; }
    public string? SavedUtc { get; set; }

    // Global VM settings
    public int SliceAxis { get; set; }
    public float SlicePosition { get; set; }
    public int SliceResolution { get; set; } = 256;
    public double EmaxDisplayPercent { get; set; } = 100;
    public int ArrowGridDensity { get; set; } = 8;
    public bool ShowFieldArrows { get; set; } = true;
    public bool ShowShape { get; set; } = true;
    public bool ShowParticles { get; set; } = true;
    public bool ShowVoltageSurfaces { get; set; } = true;

    // Tet parameters
    public double TetMaxVolumeCm3 { get; set; } = 0.01;
    public double TetChargeDensity { get; set; } = 1e15;
    public int TetFieldGridDensity { get; set; } = 96;
    public bool IsTetInnerPositive { get; set; } = true;
    public int TetChargeInputMode { get; set; }
    public double TetSurfaceFieldStrength { get; set; }
    public int CoulombGridDensity { get; set; } = 96;

    // Pathline parameters
    public double PathlinePlaneCenterX { get; set; }
    public double PathlinePlaneCenterY { get; set; }
    public double PathlinePlaneCenterZ { get; set; }
    public double PathlineNormalX { get; set; }
    public double PathlineNormalY { get; set; }
    public double PathlineNormalZ { get; set; } = 1;
    public double PathlinePlaneWidth { get; set; } = 2;
    public double PathlinePlaneHeight { get; set; } = 2;
    public int PathlineGridDensity { get; set; } = 5;
    public double PathlineInitialSpeed { get; set; } = 1e6;
    public double PathlineTimeStep { get; set; } = 1e-12;
    public int PathlineMaxSteps { get; set; } = 400;
    public bool PathlineIsElectron { get; set; } = true;

    // Voltage
    public int VoltageIntegrationSteps { get; set; } = 64;

    public List<SceneEntryDto>? Entries { get; set; }
    /// <summary>Serialized probe definitions (point + line probes).</summary>
    public List<ProbeDto>? Probes { get; set; }
}

public sealed class SceneEntryDto
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Kind { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsCoulombDerived { get; set; }
    public TransformDto? Transform { get; set; }
    public ShapeDto? Shape { get; set; }
    public string? MeshBlob { get; set; }
    public string? FieldBlob { get; set; }
    public bool HasParticles { get; set; }
    public float? ParticleChargePerParticle { get; set; }
    public int? ParticleCount { get; set; }
}

public sealed class TransformDto
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float RotX { get; set; }
    public float RotY { get; set; }
    public float RotZ { get; set; }
}

public sealed class ShapeDto
{
    public string? Type { get; set; }
    public double CenterX { get; set; }
    public double CenterY { get; set; }
    public double CenterZ { get; set; }
    public double RotationX { get; set; }
    public double RotationY { get; set; }
    public double RotationZ { get; set; }
    public double Radius { get; set; } = 1;
    public double Height { get; set; } = 2;
    public double MajorRadius { get; set; } = 2;
    public double MinorRadius { get; set; } = 0.5;
    public double AngleStartDeg { get; set; }
    public double AngleSpanDeg { get; set; } = 360;
    public double HelixTurns { get; set; } = 3;
    public double HelixPitch { get; set; } = 1;
    public int RadialSegments { get; set; } = 64;
    public int TubularSegments { get; set; } = 32;
    public double SphereRadius { get; set; } = 1;
    public double ConeTopRadius { get; set; }
    public double ConeBottomRadius { get; set; } = 1;
    public double ConeHeight { get; set; } = 2;
    public double VolChargeDensity { get; set; } = 1;
    public bool IsPositive { get; set; } = true;
    public int VolParticleCount { get; set; } = 1000;
}