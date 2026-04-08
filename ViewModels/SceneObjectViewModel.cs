using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Media3D;
using ElectricFieldSimulator.Models;

namespace ElectricFieldSimulator.ViewModels;

public class SceneObjectViewModel : INotifyPropertyChanged
{
    private readonly SceneObject _sceneObject;

    public SceneObjectViewModel(SceneObject sceneObject)
    {
        _sceneObject = sceneObject;
    }

    public string Id => _sceneObject.Id;
    public string Name
    {
        get => _sceneObject.Name;
        set
        {
            _sceneObject.Name = value;
            OnPropertyChanged();
        }
    }

    public SceneObjectType Type => _sceneObject.Type;

    public bool IsVisible
    {
        get => _sceneObject.IsVisible;
        set
        {
            _sceneObject.IsVisible = value;
            OnPropertyChanged();
            VisibilityChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool IsMovable
    {
        get => _sceneObject.IsMovable;
        set
        {
            _sceneObject.IsMovable = value;
            OnPropertyChanged();
        }
    }

    // Transform properties for movable objects
    private double _posX, _posY, _posZ;
    private double _rotX, _rotY, _rotZ;

    public double PositionX
    {
        get => _posX;
        set { _posX = value; OnPropertyChanged(); OnTransformChanged(); }
    }

    public double PositionY
    {
        get => _posY;
        set { _posY = value; OnPropertyChanged(); OnTransformChanged(); }
    }

    public double PositionZ
    {
        get => _posZ;
        set { _posZ = value; OnPropertyChanged(); OnTransformChanged(); }
    }

    public double RotationX
    {
        get => _rotX;
        set { _rotX = value; OnPropertyChanged(); OnTransformChanged(); }
    }

    public double RotationY
    {
        get => _rotY;
        set { _rotY = value; OnPropertyChanged(); OnTransformChanged(); }
    }

    public double RotationZ
    {
        get => _rotZ;
        set { _rotZ = value; OnPropertyChanged(); OnTransformChanged(); }
    }

    public SceneObject SceneObject => _sceneObject;

    public event EventHandler? TransformChanged;
    public event EventHandler? VisibilityChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    public Transform3D GetTransform()
    {
        var group = new Transform3DGroup();
        group.Children.Add(new RotateTransform3D(
            new AxisAngleRotation3D(new Vector3D(1, 0, 0), RotationX)));
        group.Children.Add(new RotateTransform3D(
            new AxisAngleRotation3D(new Vector3D(0, 1, 0), RotationY)));
        group.Children.Add(new RotateTransform3D(
            new AxisAngleRotation3D(new Vector3D(0, 0, 1), RotationZ)));
        group.Children.Add(new TranslateTransform3D(PositionX, PositionY, PositionZ));
        return group;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    protected void OnTransformChanged()
    {
        TransformChanged?.Invoke(this, EventArgs.Empty);
    }
}