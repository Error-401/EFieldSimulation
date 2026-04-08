using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EFieldSimulation.Models;

namespace EFieldSimulation.ViewModels;

/// <summary>
/// ViewModel wrapping a TransformState with property change notifications
/// so sliders and text boxes update the 3D view in real time.
/// </summary>
public sealed class TransformViewModel : BaseViewModel
{
    private TransformState _state;
    public TransformState State => _state;

    public TransformViewModel(TransformState state)
    {
        _state = state;
    }

    public float X
    {
        get => _state.X;
        set { _state.X = value; OnPropertyChanged(); RaiseTransformChanged(); }
    }

    public float Y
    {
        get => _state.Y;
        set { _state.Y = value; OnPropertyChanged(); RaiseTransformChanged(); }
    }

    public float Z
    {
        get => _state.Z;
        set { _state.Z = value; OnPropertyChanged(); RaiseTransformChanged(); }
    }

    public float RotX
    {
        get => _state.RotX;
        set { _state.RotX = value; OnPropertyChanged(); RaiseTransformChanged(); }
    }

    public float RotY
    {
        get => _state.RotY;
        set { _state.RotY = value; OnPropertyChanged(); RaiseTransformChanged(); }
    }

    public float RotZ
    {
        get => _state.RotZ;
        set { _state.RotZ = value; OnPropertyChanged(); RaiseTransformChanged(); }
    }

    public event Action? TransformChanged;

    private void RaiseTransformChanged()
    {
        _state.RaiseChanged();
        TransformChanged?.Invoke();
    }

    public void Reset()
    {
        X = 0; Y = 0; Z = 0;
        RotX = 0; RotY = 0; RotZ = 0;
    }
    public void SetTarget(TransformState state)
    {
        if (_state != null) _state.Changed -= OnExternalChange;
        _state = state;
        if (_state != null) _state.Changed += OnExternalChange;
        OnPropertyChanged(nameof(X));
        OnPropertyChanged(nameof(Y));
        OnPropertyChanged(nameof(Z));
        OnPropertyChanged(nameof(RotX));
        OnPropertyChanged(nameof(RotY));
        OnPropertyChanged(nameof(RotZ));
    }

    private void OnExternalChange()
    {
        OnPropertyChanged(nameof(X));
        OnPropertyChanged(nameof(Y));
        OnPropertyChanged(nameof(Z));
        OnPropertyChanged(nameof(RotX));
        OnPropertyChanged(nameof(RotY));
        OnPropertyChanged(nameof(RotZ));
    }
}