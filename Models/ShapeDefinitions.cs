using System.Windows.Media.Media3D;

namespace EFieldSimulation.Models;

/// <summary>
/// Generates MeshGeometry3D from ArbitraryShapeParams by delegating to the
/// file-driven ShapeLibrary. All shape geometry now lives in shapes.json.
/// </summary>
public static class ShapeDefinitions
{
    public static MeshGeometry3D Generate(ArbitraryShapeParams p)
    {
        var mesh = new MeshGeometry3D();
        try
        {
            var (verts, idx, normals) = ShapeLibrary.Tessellate(p);
            for (int i = 0; i < verts.Length; i++)
            {
                mesh.Positions.Add(new Point3D(verts[i].X, verts[i].Y, verts[i].Z));
                mesh.Normals.Add(new Vector3D(normals[i].X, normals[i].Y, normals[i].Z));
            }
            for (int i = 0; i < idx.Length; i++)
                mesh.TriangleIndices.Add(idx[i]);
        }
        catch
        {
            // Missing/malformed shape → empty geometry (matches old default).
        }
        return mesh;
    }
}