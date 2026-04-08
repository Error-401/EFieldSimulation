using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EFieldSimulation.Models;

public enum SceneEntryKind
{
    Static,
    Movable,
    ChargeVolume,
    VoltageSurface
}

/// <summary>
/// A single scene object that owns one transform and optionally carries
/// a mesh, an E-field dataset, and/or a charge-volume shape.
/// Everything attached transforms together.
/// </summary>
public sealed class SceneEntry : INotifyPropertyChanged
{
    public string Id { get; } = Guid.NewGuid().ToString();

    private string _name = "";
    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }
    /// <summary>Diagnostic statistics from the last tetrahedralization.</summary>
    public TetStatistics? TetStats { get; set; }
    public SceneEntryKind Kind { get; init; }

    // ── Optional payloads (any combination) ──────────────────
    public MeshData? Mesh { get; set; }
    public ElectricFieldData? Field { get; set; }
    public StructuredGridAccessor? FieldAccessor { get; set; }
    public ArbitraryShapeParams? ShapeParams { get; set; }
    /// <summary>Most recent voltage evaluation on this surface (post-process).</summary>
    public Services.VoltageSurfaceResult? VoltageResult { get; set; }

    // ── Single transform shared by all payloads ──────────────
    public TransformState Transform { get; } = new();

    private bool _isVisible = true;
    public bool IsVisible
    {
        get => _isVisible;
        set { _isVisible = value; OnPropertyChanged(); }
    }
    public ParticleCloud? Particles { get; set; }
    /// <summary>
    /// True if this entry's Field was computed from particles (auto-recalculated).
    /// </summary>
    public bool IsCoulombDerived { get; set; }

    /// <summary>Human-readable summary of what this entry contains.</summary>
    public string ContentSummary
    {
        get
        {
            var parts = new List<string>();
            if (Mesh != null) parts.Add("Mesh");
            if (Field != null) parts.Add("E-Field");
            if (ShapeParams != null)
                parts.Add(Kind == SceneEntryKind.VoltageSurface ? "V-Surface" : "Volume");
            if (Particles != null) parts.Add($"{Particles.Count} particles");
            if (VoltageResult != null)
                parts.Add($"V≈{VoltageResult.AverageVoltage:E2}V");
            if (TetStats != null)
                parts.Add($"{TetStats.TotalInteriorTets:N0} tets");
            return parts.Count > 0 ? string.Join("+", parts) : "Empty";
        }
    }

    public void NotifyContentChanged() => OnPropertyChanged(nameof(ContentSummary));

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

}