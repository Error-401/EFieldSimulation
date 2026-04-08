using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace EFieldSimulation.Models;

public enum ProbeType { Point, LineSegment }

public sealed class ProbeDefinition : INotifyPropertyChanged
{
    public string Id { get; } = Guid.NewGuid().ToString();

    private string _name = "";
    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    public ProbeType Type { get; init; }

    private float _ax, _ay, _az;
    public float AX { get => _ax; set { _ax = value; OnPropertyChanged(); OnPropertyChanged(nameof(PointA)); } }
    public float AY { get => _ay; set { _ay = value; OnPropertyChanged(); OnPropertyChanged(nameof(PointA)); } }
    public float AZ { get => _az; set { _az = value; OnPropertyChanged(); OnPropertyChanged(nameof(PointA)); } }

    private float _bx, _by, _bz;
    public float BX { get => _bx; set { _bx = value; OnPropertyChanged(); OnPropertyChanged(nameof(PointB)); } }
    public float BY { get => _by; set { _by = value; OnPropertyChanged(); OnPropertyChanged(nameof(PointB)); } }
    public float BZ { get => _bz; set { _bz = value; OnPropertyChanged(); OnPropertyChanged(nameof(PointB)); } }

    private int _sampleCount = 50;
    public int SampleCount
    {
        get => _sampleCount;
        set { _sampleCount = Math.Clamp(value, 2, 500); OnPropertyChanged(); }
    }

    private bool _isVisible = true;
    public bool IsVisible
    {
        get => _isVisible;
        set { _isVisible = value; OnPropertyChanged(); }
    }

    public Vector3 PointA => new(_ax, _ay, _az);
    public Vector3 PointB => new(_bx, _by, _bz);

    private ProbeResult? _result;
    public ProbeResult? Result
    {
        get => _result;
        set { _result = value; OnPropertyChanged(); OnPropertyChanged(nameof(Summary)); }
    }

    public string Summary
    {
        get
        {
            if (Result == null) return "Not evaluated";
            if (Type == ProbeType.Point && Result.Samples.Length > 0)
            {
                var s = Result.Samples[0];
                return $"|E|={s.TotalFieldMagnitude:E3} V/m, V={s.TotalVoltage:E3} V";
            }
            return $"{Result.Samples.Length} samples evaluated";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}