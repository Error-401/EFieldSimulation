using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using EFieldSimulation.Models;

namespace EFieldSimulation.Rendering;

/// <summary>
/// Creates WPF Model3D objects from MeshData, with customizable materials.
/// </summary>
public static class MeshRenderer
{
    public static GeometryModel3D CreateModel(
        MeshData mesh, Color diffuseColor, double opacity = 1.0,
        Transform3D? transform = null)
    {
        var geometry = mesh.ToMeshGeometry3D();

        var material = new MaterialGroup();
        material.Children.Add(new DiffuseMaterial(
            new SolidColorBrush(diffuseColor) { Opacity = opacity }));
        material.Children.Add(new SpecularMaterial(
            new SolidColorBrush(Colors.White) { Opacity = 0.3 }, 40));

        var model = new GeometryModel3D
        {
            Geometry = geometry,
            Material = material,
            BackMaterial = new DiffuseMaterial(
                new SolidColorBrush(diffuseColor) { Opacity = opacity * 0.5 })
        };

        if (transform != null)
            model.Transform = transform;

        return model;
    }

    /// <summary>
    /// Create a wireframe overlay from mesh edges.
    /// Returns line segments as pairs of Point3D.
    /// </summary>
    public static Point3DCollection GetWireframeLines(MeshData mesh)
    {
        var lines = new Point3DCollection();
        var indices = mesh.TriangleIndices;
        var verts = mesh.Vertices;

        for (int i = 0; i < indices.Length; i += 3)
        {
            var p0 = verts[indices[i]];
            var p1 = verts[indices[i + 1]];
            var p2 = verts[indices[i + 2]];

            lines.Add(p0); lines.Add(p1);
            lines.Add(p1); lines.Add(p2);
            lines.Add(p2); lines.Add(p0);
        }

        return lines;
    }
}