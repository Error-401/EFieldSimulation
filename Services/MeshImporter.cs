using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Media.Media3D;
using EFieldSimulation.Models;

namespace EFieldSimulation.Services;

/// <summary>
/// Imports 3D mesh files: STL (binary and ASCII), OBJ, and PLY.
/// No external mesh library dependency — pure C# parsing.
/// </summary>
public static class MeshImporter
{
    private const string MeshImportLogPrefix = "[MESH_IMPORT]";

    public static MeshData Import(string filePath)
    {
        using var timing = new MeshImportTiming(filePath);

        try
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            timing.Mark($"Detected extension: {ext}");

            MeshData mesh = ext switch
            {
                ".stl" => ImportStl(filePath, timing),
                ".obj" => ImportObj(filePath, timing),
                ".ply" => ImportPly(filePath, timing),
                _ => throw new NotSupportedException($"Unsupported mesh format: {ext}")
            };

            timing.Finish(mesh);
            return mesh;
        }
        catch (Exception ex)
        {
            timing.Fail(ex);
            throw;
        }
    }

    #region STL

    private static MeshData ImportStl(string filePath, MeshImportTiming timing)
    {
        byte[] raw = File.ReadAllBytes(filePath);
        timing.Mark($"STL: Read file into memory ({FormatBytes(raw.Length)})");

        // Detect ASCII vs binary: ASCII STL starts with "solid"
        bool startsWithSolid = raw.Length > 5 &&
            raw[0] == 's' && raw[1] == 'o' && raw[2] == 'l' &&
            raw[3] == 'i' && raw[4] == 'd';

        timing.Mark($"STL: Detected format candidate: {(startsWithSolid ? "ASCII" : "Binary")}");

        if (startsWithSolid)
        {
            // Some binary STLs also start with "solid", so ASCII import can fail.
            // If that happens, fall back to binary import.
            try
            {
                return ImportStlAscii(filePath, timing);
            }
            catch (Exception ex)
            {
                timing.Mark($"STL: ASCII parse failed, falling back to binary. Reason: {ex.Message}");
                return ImportStlBinary(raw, filePath, timing);
            }
        }

        return ImportStlBinary(raw, filePath, timing);
    }

    private static MeshData ImportStlBinary(byte[] raw, string filePath, MeshImportTiming timing)
    {
        uint triCount = BitConverter.ToUInt32(raw, 80);
        timing.Mark($"STL Binary: Triangle count read: {triCount:n0}");

        var vertices = new List<Point3D>((int)triCount * 3);
        var normals = new List<Vector3D>((int)triCount * 3);
        var indices = new List<int>((int)triCount * 3);

        int offset = 84;
        for (uint i = 0; i < triCount; i++)
        {
            float nx = BitConverter.ToSingle(raw, offset); offset += 4;
            float ny = BitConverter.ToSingle(raw, offset); offset += 4;
            float nz = BitConverter.ToSingle(raw, offset); offset += 4;
            var normal = new Vector3D(nx, ny, nz);

            for (int v = 0; v < 3; v++)
            {
                float vx = BitConverter.ToSingle(raw, offset); offset += 4;
                float vy = BitConverter.ToSingle(raw, offset); offset += 4;
                float vz = BitConverter.ToSingle(raw, offset); offset += 4;

                int idx = vertices.Count;
                vertices.Add(new Point3D(vx, vy, vz));
                normals.Add(normal);
                indices.Add(idx);
            }

            offset += 2; // attribute byte count
        }

        timing.Mark($"STL Binary: Parsed triangles into raw mesh lists. Vertices={vertices.Count:n0}, Indices={indices.Count:n0}");
        return BuildMeshData(filePath, vertices, indices, normals, timing);
    }

    private static MeshData ImportStlAscii(string filePath, MeshImportTiming timing)
    {
        var vertices = new List<Point3D>();
        var normals = new List<Vector3D>();
        var indices = new List<int>();
        var currentNormal = new Vector3D(0, 0, 1);

        int lineCount = 0;
        int facetCount = 0;
        int vertexLineCount = 0;

        foreach (string rawLine in File.ReadLines(filePath))
        {
            lineCount++;

            string line = rawLine.Trim();
            if (line.StartsWith("facet normal"))
            {
                facetCount++;

                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 5)
                {
                    currentNormal = new Vector3D(
                        double.Parse(parts[2], CultureInfo.InvariantCulture),
                        double.Parse(parts[3], CultureInfo.InvariantCulture),
                        double.Parse(parts[4], CultureInfo.InvariantCulture));
                }
            }
            else if (line.StartsWith("vertex"))
            {
                vertexLineCount++;

                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 4)
                {
                    int idx = vertices.Count;
                    vertices.Add(new Point3D(
                        double.Parse(parts[1], CultureInfo.InvariantCulture),
                        double.Parse(parts[2], CultureInfo.InvariantCulture),
                        double.Parse(parts[3], CultureInfo.InvariantCulture)));
                    normals.Add(currentNormal);
                    indices.Add(idx);
                }
            }
        }

        timing.Mark($"STL ASCII: Parsed lines. Lines={lineCount:n0}, Facets={facetCount:n0}, VertexLines={vertexLineCount:n0}, Vertices={vertices.Count:n0}");
        return BuildMeshData(filePath, vertices, indices, normals, timing);
    }

    #endregion

    #region OBJ

    private static MeshData ImportObj(string filePath, MeshImportTiming timing)
    {
        var positions = new List<Point3D>();
        var objNormals = new List<Vector3D>();
        var finalVertices = new List<Point3D>();
        var finalNormals = new List<Vector3D>();
        var indices = new List<int>();
        var faceMaterials = new List<string>();
        string currentMaterial = "";

        int lineCount = 0;
        int vertexLineCount = 0;
        int normalLineCount = 0;
        int faceLineCount = 0;
        int triangleCount = 0;
        int materialSwitchCount = 0;

        foreach (string rawLine in File.ReadLines(filePath))
        {
            lineCount++;

            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts[0] == "v" && parts.Length >= 4)
            {
                vertexLineCount++;

                positions.Add(new Point3D(
                    double.Parse(parts[1], CultureInfo.InvariantCulture),
                    double.Parse(parts[2], CultureInfo.InvariantCulture),
                    double.Parse(parts[3], CultureInfo.InvariantCulture)));
            }
            else if (parts[0] == "vn" && parts.Length >= 4)
            {
                normalLineCount++;

                objNormals.Add(new Vector3D(
                    double.Parse(parts[1], CultureInfo.InvariantCulture),
                    double.Parse(parts[2], CultureInfo.InvariantCulture),
                    double.Parse(parts[3], CultureInfo.InvariantCulture)));
            }
            else if (parts[0] == "usemtl")
            {
                materialSwitchCount++;

                currentMaterial = parts.Length > 1
                    ? string.Join(" ", parts.Skip(1)).Trim()
                    : "";
            }
            else if (parts[0] == "f")
            {
                faceLineCount++;

                var faceVerts = new List<(int vi, int ni)>();
                for (int i = 1; i < parts.Length; i++)
                {
                    var sub = parts[i].Split('/');
                    int vi = int.Parse(sub[0]) - 1;
                    int ni = sub.Length >= 3 && sub[2].Length > 0 ?
                        int.Parse(sub[2]) - 1 : -1;
                    faceVerts.Add((vi, ni));
                }

                for (int i = 1; i < faceVerts.Count - 1; i++)
                {
                    triangleCount++;
                    faceMaterials.Add(currentMaterial);

                    var tri = new[] { faceVerts[0], faceVerts[i], faceVerts[i + 1] };
                    foreach (var (vi, ni) in tri)
                    {
                        int idx = finalVertices.Count;
                        finalVertices.Add(positions[vi]);
                        finalNormals.Add(ni >= 0 && ni < objNormals.Count ?
                            objNormals[ni] : new Vector3D(0, 1, 0));
                        indices.Add(idx);
                    }
                }
            }
        }

        timing.Mark(
            $"OBJ: Parsed file. Lines={lineCount:n0}, PositionLines={vertexLineCount:n0}, NormalLines={normalLineCount:n0}, FaceLines={faceLineCount:n0}, Triangles={triangleCount:n0}, MaterialSwitches={materialSwitchCount:n0}, FinalVertices={finalVertices.Count:n0}");

        return BuildMeshData(filePath, finalVertices, indices, finalNormals, timing, faceMaterials);
    }

    #endregion

    #region PLY

    private static MeshData ImportPly(string filePath, MeshImportTiming timing)
    {
        using var reader = new StreamReader(filePath);
        int vertexCount = 0, faceCount = 0;
        bool headerDone = false;
        int headerLineCount = 0;

        // Parse header
        while (!headerDone && reader.ReadLine() is { } line)
        {
            headerLineCount++;

            line = line.Trim();
            if (line.StartsWith("element vertex"))
                vertexCount = int.Parse(line.Split(' ')[2]);
            else if (line.StartsWith("element face"))
                faceCount = int.Parse(line.Split(' ')[2]);
            else if (line == "end_header")
                headerDone = true;
        }

        timing.Mark($"PLY: Parsed header. HeaderLines={headerLineCount:n0}, VertexCount={vertexCount:n0}, FaceCount={faceCount:n0}");

        var positions = new List<Point3D>(vertexCount);
        for (int i = 0; i < vertexCount; i++)
        {
            var parts = reader.ReadLine()!.Trim()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            positions.Add(new Point3D(
                double.Parse(parts[0], CultureInfo.InvariantCulture),
                double.Parse(parts[1], CultureInfo.InvariantCulture),
                double.Parse(parts[2], CultureInfo.InvariantCulture)));
        }

        timing.Mark($"PLY: Parsed vertex positions. Positions={positions.Count:n0}");

        var vertices = new List<Point3D>();
        var normals = new List<Vector3D>();
        var indices = new List<int>();
        int triangleCount = 0;

        for (int i = 0; i < faceCount; i++)
        {
            var parts = reader.ReadLine()!.Trim()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int count = int.Parse(parts[0]);

            var faceIdx = new int[count];
            for (int j = 0; j < count; j++)
                faceIdx[j] = int.Parse(parts[j + 1]);

            // Fan triangulate
            for (int j = 1; j < count - 1; j++)
            {
                triangleCount++;

                var p0 = positions[faceIdx[0]];
                var p1 = positions[faceIdx[j]];
                var p2 = positions[faceIdx[j + 1]];

                var e1 = p1 - p0;
                var e2 = p2 - p0;
                var n = Vector3D.CrossProduct(e1, e2);
                n.Normalize();

                int idx = vertices.Count;
                vertices.Add(p0); normals.Add(n); indices.Add(idx);
                vertices.Add(p1); normals.Add(n); indices.Add(idx + 1);
                vertices.Add(p2); normals.Add(n); indices.Add(idx + 2);
            }
        }

        timing.Mark($"PLY: Parsed faces and triangulated. Triangles={triangleCount:n0}, FinalVertices={vertices.Count:n0}");
        return BuildMeshData(filePath, vertices, indices, normals, timing);
    }

    #endregion

    private static MeshData BuildMeshData(
        string filePath,
        List<Point3D> vertices,
        List<int> indices,
        List<Vector3D> normals,
        MeshImportTiming timing,
        List<string>? faceMaterials = null)
    {
        var bounds = new Rect3D();
        if (vertices.Count > 0)
        {
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            foreach (var v in vertices)
            {
                if (v.X < minX) minX = v.X; if (v.X > maxX) maxX = v.X;
                if (v.Y < minY) minY = v.Y; if (v.Y > maxY) maxY = v.Y;
                if (v.Z < minZ) minZ = v.Z; if (v.Z > maxZ) maxZ = v.Z;
            }
            bounds = new Rect3D(minX, minY, minZ,
                maxX - minX, maxY - minY, maxZ - minZ);
        }

        timing.Mark("BuildMeshData: Calculated bounds");

        var mesh = new MeshData
        {
            FilePath = filePath,
            Name = Path.GetFileNameWithoutExtension(filePath),
            Vertices = vertices.ToArray(),
            TriangleIndices = indices.ToArray(),
            Normals = normals.ToArray(),
            Bounds = bounds,
            FaceMaterials = faceMaterials is { Count: > 0 } ? faceMaterials.ToArray() : null
        };

        timing.Mark(
            $"BuildMeshData: Converted lists to arrays. Vertices={mesh.Vertices.Length:n0}, Indices={mesh.TriangleIndices.Length:n0}, Normals={mesh.Normals.Length:n0}, Bounds=({bounds.SizeX:n3}, {bounds.SizeY:n3}, {bounds.SizeZ:n3})");

        return mesh;
    }

    private sealed class MeshImportTiming : IDisposable
    {
        private readonly Stopwatch _total = Stopwatch.StartNew();
        private readonly Stopwatch _step = Stopwatch.StartNew();
        private readonly string _filePath;
        private readonly long _startManagedMemory;
        private readonly long _startWorkingSet;
        private readonly int[] _startGcCounts;
        private bool _finished;

        public MeshImportTiming(string filePath)
        {
            _filePath = filePath;
            _startManagedMemory = GC.GetTotalMemory(false);
            _startWorkingSet = GetWorkingSetBytes();
            _startGcCounts = GetGcCounts();

            var fileInfo = new FileInfo(filePath);

            Log("START");
            Log($"File: {filePath}");
            Log($"File size: {FormatBytes(fileInfo.Length)}");
            Log($"OS: {Environment.OSVersion}");
            Log($"Process: {(Environment.Is64BitProcess ? "64-bit" : "32-bit")}");
            Log($"CPU logical processors: {Environment.ProcessorCount}");
            Log($"Managed memory before: {FormatBytes(_startManagedMemory)}");
            Log($"Process working set before: {FormatBytes(_startWorkingSet)}");
        }

        public void Mark(string message)
        {
            Log($"STEP +{_step.ElapsedMilliseconds:n0} ms | total {_total.ElapsedMilliseconds:n0} ms | {message}");
            _step.Restart();
        }

        public void Finish(MeshData mesh)
        {
            _finished = true;

            long endManagedMemory = GC.GetTotalMemory(false);
            long endWorkingSet = GetWorkingSetBytes();
            int[] endGcCounts = GetGcCounts();

            Log("FINISH");
            Log($"Imported mesh: {mesh.Name}");
            Log($"Vertices: {mesh.Vertices.Length:n0}");
            Log($"Triangles: {mesh.TriangleIndices.Length / 3:n0}");
            Log($"Total import time: {_total.ElapsedMilliseconds:n0} ms");
            Log($"Managed memory after: {FormatBytes(endManagedMemory)} | delta {FormatBytes(endManagedMemory - _startManagedMemory)}");
            Log($"Process working set after: {FormatBytes(endWorkingSet)} | delta {FormatBytes(endWorkingSet - _startWorkingSet)}");
            Log($"GC collections during import: Gen0={endGcCounts[0] - _startGcCounts[0]}, Gen1={endGcCounts[1] - _startGcCounts[1]}, Gen2={endGcCounts[2] - _startGcCounts[2]}");
        }

        public void Fail(Exception ex)
        {
            _finished = true;
            Log("FAILED");
            Log($"Failed after {_total.ElapsedMilliseconds:n0} ms");
            Log($"{ex.GetType().Name}: {ex.Message}");
        }

        public void Dispose()
        {
            if (!_finished)
                Log($"ENDED without Finish/Fail after {_total.ElapsedMilliseconds:n0} ms");
        }

        private static int[] GetGcCounts()
        {
            return new[]
            {
                GC.CollectionCount(0),
                GC.CollectionCount(1),
                GC.CollectionCount(2)
            };
        }

        private static long GetWorkingSetBytes()
        {
            using var process = Process.GetCurrentProcess();
            return process.WorkingSet64;
        }

        private static void Log(string message)
        {
            Trace.WriteLine($"{MeshImportLogPrefix} {DateTime.Now:HH:mm:ss.fff} {message}");
        }
    }

    private static string FormatBytes(long bytes)
    {
        string sign = bytes < 0 ? "-" : "";
        double value = Math.Abs((double)bytes);

        string[] units = { "B", "KB", "MB", "GB" };
        int unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{sign}{value:n2} {units[unit]}";
    }
}