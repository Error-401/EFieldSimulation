using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media.Media3D;

namespace ElectricFieldSimulator.Models;

/// <summary>
/// Represents an object in the 3D scene
/// </summary>
public class SceneObject
{
    public string Id { get; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public SceneObjectType Type { get; set; }
    public Model3D? Model { get; set; }
    public object? Data { get; set; } // MeshData, ArbitraryShape, or ElectricFieldData
    public bool IsVisible { get; set; } = true;
    public bool IsMovable { get; set; } = false;
}

public enum SceneObjectType
{
    Mesh,
    ElectricField,
    ArbitraryShape
}