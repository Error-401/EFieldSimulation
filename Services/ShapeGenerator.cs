using System.Windows.Media;
using System.Windows.Media.Media3D;
using EFieldSimulation.Models;

namespace EFieldSimulation.Services;

public static class ShapeGenerator
{
    public static GeometryModel3D CreateShapeModel(
        ArbitraryShapeParams shapeParams,
        Color diffuseColor, double opacity = 0.5,
        TransformState? entryTransform = null)
    {
        var mesh = ShapeDefinitions.Generate(shapeParams);

        var material = new MaterialGroup();
        material.Children.Add(new DiffuseMaterial(
            new SolidColorBrush(diffuseColor) { Opacity = opacity }));
        material.Children.Add(new SpecularMaterial(
            new SolidColorBrush(Colors.White) { Opacity = 0.3 }, 40));

        var model = new GeometryModel3D
        {
            Geometry = mesh,
            Material = material,
            BackMaterial = new DiffuseMaterial(
                new SolidColorBrush(diffuseColor) { Opacity = opacity * 0.5 })
        };

        // Combine shape-local rotation with entry world transform
        var group = new Transform3DGroup();
        if (entryTransform != null)
            group.Children.Add(entryTransform.ToWpfTransform());
        model.Transform = group;

        return model;
    }
}