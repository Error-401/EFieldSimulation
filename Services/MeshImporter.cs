using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Globalization;
using System.Windows.Media.Media3D;
using EFieldSimulation.Models;
using System.IO;

namespace EFieldSimulation.Services;

/// <summary>
/// Imports 3D mesh files: STL (binary and ASCII), OBJ, and PLY.
/// No external mesh library dependency — pure C# parsing.
/// </summary>
public static class MeshImporter
{
    public static MeshData Import(string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".stl" => ImportStl(filePath),
            ".obj" => ImportObj(filePath),
            ".ply" => ImportPly(filePath),
            _ => throw new NotSupportedException($"Unsupported mesh format: {ext}")
        };
    }

    #region STL

    private static MeshData ImportStl(string filePath)
    {
        byte[] raw = File.ReadAllBytes(filePath);

        // Detect ASCII vs binary: ASCII STL starts with "solid"
        bool isAscii = raw.Length > 5 &&
            raw[0] == 's' && raw[1] == 'o' && raw[2] == 'l' &&
            raw[3] == 'i' && raw[4] == 'd';

        // But some binary files also start with "solid" in the header
        // Binary STL: 80-byte header + 4-byte triangle count
        if (isAscii && raw.Length > 84)
        {
            uint triCount = BitConverter.ToUInt32(raw, 80);
            int expectedBinarySize = 84 + (int)triCount * 50;
            if (Math.Abs(raw.Length - expectedBinarySize) < 10)
                isAscii = false;
        }

        return isAscii ? ImportStlAscii(filePath) : ImportStlBinary(raw, filePath);
    }

    private static MeshData ImportStlBinary(byte[] raw, string filePath)
    {
        uint triCount = BitConverter.ToUInt32(raw, 80);
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

        return BuildMeshData(filePath, vertices, indices, normals);
    }

    private static MeshData ImportStlAscii(string filePath)
    {
        var vertices = new List<Point3D>();
        var normals = new List<Vector3D>();
        var indices = new List<int>();
        var currentNormal = new Vector3D(0, 0, 1);

        foreach (string rawLine in File.ReadLines(filePath))
        {
            string line = rawLine.Trim();
            if (line.StartsWith("facet normal"))
            {
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

        return BuildMeshData(filePath, vertices, indices, normals);
    }

    #endregion

    #region OBJ

    private static MeshData ImportObj(string filePath)
    {
        var positions = new List<Point3D>();
        var objNormals = new List<Vector3D>();
        var finalVertices = new List<Point3D>();
        var finalNormals = new List<Vector3D>();
        var indices = new List<int>();
        var faceMaterials = new List<string>();
        string currentMaterial = "";

        foreach (string rawLine in File.ReadLines(filePath))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts[0] == "v" && parts.Length >= 4)
            {
                positions.Add(new Point3D(
                    double.Parse(parts[1], CultureInfo.InvariantCulture),
                    double.Parse(parts[2], CultureInfo.InvariantCulture),
                    double.Parse(parts[3], CultureInfo.InvariantCulture)));
            }
            else if (parts[0] == "vn" && parts.Length >= 4)
            {
                objNormals.Add(new Vector3D(
                    double.Parse(parts[1], CultureInfo.InvariantCulture),
                    double.Parse(parts[2], CultureInfo.InvariantCulture),
                    double.Parse(parts[3], CultureInfo.InvariantCulture)));
            }
            else if (parts[0] == "usemtl")
            {
                currentMaterial = parts.Length > 1
                    ? string.Join(" ", parts.Skip(1)).Trim()
                    : "";
            }
            else if (parts[0] == "f")
            {
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

        return BuildMeshData(filePath, finalVertices, indices, finalNormals, faceMaterials);
    }

    #endregion

    #region PLY

    private static MeshData ImportPly(string filePath)
    {
        using var reader = new StreamReader(filePath);
        int vertexCount = 0, faceCount = 0;
        bool headerDone = false;

        // Parse header
        while (!headerDone && reader.ReadLine() is { } line)
        {
            line = line.Trim();
            if (line.StartsWith("element vertex"))
                vertexCount = int.Parse(line.Split(' ')[2]);
            else if (line.StartsWith("element face"))
                faceCount = int.Parse(line.Split(' ')[2]);
            else if (line == "end_header")
                headerDone = true;
        }

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

        var vertices = new List<Point3D>();
        var normals = new List<Vector3D>();
        var indices = new List<int>();

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

        return BuildMeshData(filePath, vertices, indices, normals);
    }

    #endregion

    private static MeshData BuildMeshData(string filePath,
        List<Point3D> vertices, List<int> indices, List<Vector3D> normals,
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

        return new MeshData
        {
            FilePath = filePath,
            Name = Path.GetFileNameWithoutExtension(filePath),
            Vertices = vertices.ToArray(),
            TriangleIndices = indices.ToArray(),
            Normals = normals.ToArray(),
            Bounds = bounds,
            FaceMaterials = faceMaterials is { Count: > 0 } ? faceMaterials.ToArray() : null
        };
    }
}