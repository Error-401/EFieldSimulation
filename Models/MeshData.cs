using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Numerics;
using System.Windows.Media.Media3D;

namespace EFieldSimulation.Models;

/// <summary>
/// Loaded 3D mesh (from STL, OBJ, PLY, etc.)
/// </summary>
public sealed class MeshData
{
    public string FilePath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public Point3D[] Vertices { get; set; } = Array.Empty<Point3D>();
    public int[] TriangleIndices { get; set; } = Array.Empty<int>();
    public Vector3D[] Normals { get; set; } = Array.Empty<Vector3D>();
    /// <summary>Per-triangle material name (OBJ usemtl). Null for STL/PLY.</summary>
    public string[]? FaceMaterials { get; set; }

    // Bounding box
    public Rect3D Bounds { get; set; }

    public MeshGeometry3D ToMeshGeometry3D()
    {
        var mesh = new MeshGeometry3D();
        foreach (var v in Vertices) mesh.Positions.Add(v);
        foreach (var i in TriangleIndices) mesh.TriangleIndices.Add(i);
        foreach (var n in Normals) mesh.Normals.Add(n);
        return mesh;
    }
}