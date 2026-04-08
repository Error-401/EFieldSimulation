using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media.Media3D;

namespace EFieldSimulation.Models;

/// <summary>
/// Full 6-DOF transform: translation + rotation (Euler angles in degrees).
/// Fires change events so the UI updates in real-time.
/// </summary>
public sealed class TransformState : ICloneable
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float RotX { get; set; } // degrees
    public float RotY { get; set; }
    public float RotZ { get; set; }

    public event Action? Changed;
    public void RaiseChanged() => Changed?.Invoke();

    public Vector3 Translation => new(X, Y, Z);
    public Vector3 RotationDegrees => new(RotX, RotY, RotZ);

    /// <summary>
    /// Build a System.Numerics.Matrix4x4 for transforming field sample points
    /// </summary>
    public Matrix4x4 ToMatrix4x4()
    {
        float rx = RotX * MathF.PI / 180f;
        float ry = RotY * MathF.PI / 180f;
        float rz = RotZ * MathF.PI / 180f;

        return Matrix4x4.CreateRotationX(rx) *
               Matrix4x4.CreateRotationY(ry) *
               Matrix4x4.CreateRotationZ(rz) *
               Matrix4x4.CreateTranslation(X, Y, Z);
    }

    /// <summary>
    /// Build a WPF Transform3DGroup for the HelixToolkit viewport
    /// </summary>
    public Transform3DGroup ToWpfTransform()
    {
        var group = new Transform3DGroup();
        group.Children.Add(new RotateTransform3D(
            new AxisAngleRotation3D(new Vector3D(1, 0, 0), RotX)));
        group.Children.Add(new RotateTransform3D(
            new AxisAngleRotation3D(new Vector3D(0, 1, 0), RotY)));
        group.Children.Add(new RotateTransform3D(
            new AxisAngleRotation3D(new Vector3D(0, 0, 1), RotZ)));
        group.Children.Add(new TranslateTransform3D(X, Y, Z));
        return group;
    }

    /// <summary>
    /// Transform a world-space point into this object's local space
    /// (for field lookup of the movable field)
    /// </summary>
    public Vector3 WorldToLocal(Vector3 worldPoint)
    {
        if (!Matrix4x4.Invert(ToMatrix4x4(), out var inv))
            return worldPoint;
        return Vector3.Transform(worldPoint, inv);
    }

    /// <summary>
    /// Transform a local-space vector (field direction) into world space
    /// </summary>
    public Vector3 LocalVectorToWorld(Vector3 localVec)
    {
        // Rotation only (no translation for vectors)
        float rx = RotX * MathF.PI / 180f;
        float ry = RotY * MathF.PI / 180f;
        float rz = RotZ * MathF.PI / 180f;

        var rotMat = Matrix4x4.CreateRotationX(rx) *
                     Matrix4x4.CreateRotationY(ry) *
                     Matrix4x4.CreateRotationZ(rz);

        return Vector3.TransformNormal(localVec, rotMat);
    }

    public object Clone() => new TransformState
    {
        X = X,
        Y = Y,
        Z = Z,
        RotX = RotX,
        RotY = RotY,
        RotZ = RotZ
    };
}