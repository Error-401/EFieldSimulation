using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EFieldSimulation.Models;

/// <summary>
/// Diagnostic statistics from a tetrahedralization run.
/// Displayed in the UI Tet Statistics tab.
/// </summary>
public sealed class TetStatistics : INotifyPropertyChanged
{
    // ── Mesh info ──
    public string MeshName { get; init; } = "";
    public int VertexCount { get; init; }
    public int TriangleCount { get; init; }

    // ── Grid info ──
    public int GridNx { get; init; }
    public int GridNy { get; init; }
    public int GridNz { get; init; }
    public long TotalCells => (long)GridNx * GridNy * GridNz;
    public float CellSize { get; init; }

    // ── Tet counts ──
    public int TotalInteriorTets { get; init; }
    public double FillRatioPercent { get; init; }
    public double ElementVolume { get; init; }
    public double TotalVolume { get; init; }

    // ── Face classification ──
    public int InnerFaces { get; init; }
    public int OuterFaces { get; init; }
    public int NeitherFaces { get; init; }
    public bool IsDipoleMode { get; init; }

    // ── Depth field ──
    public float RawDepthMin { get; init; }
    public float RawDepthMax { get; init; }
    public float VolumeMedianTStar { get; init; }
    public float ChargeSignMin { get; init; }
    public float ChargeSignMax { get; init; }
    public double ChargeSignMean { get; init; }
    public int NegativeTets { get; init; }
    public int PositiveTets { get; init; }
    public int NearZeroTets { get; init; }
    public double NegativePercent => TotalInteriorTets > 0 ? 100.0 * NegativeTets / TotalInteriorTets : 0;
    public double PositivePercent => TotalInteriorTets > 0 ? 100.0 * PositiveTets / TotalInteriorTets : 0;
    public double NearZeroPercent => TotalInteriorTets > 0 ? 100.0 * NearZeroTets / TotalInteriorTets : 0;

    // ── Charge balance ──
    public double PositiveChargeC { get; init; }
    public double NegativeChargeC { get; init; }
    public double NetChargeC => PositiveChargeC + NegativeChargeC;
    public double ImbalancePercent { get; init; }
    public double RhoSI { get; init; }

    // ── Field stats ──
    public int FieldGridDensity { get; init; }
    public int FieldGridTotal => FieldGridDensity * FieldGridDensity * FieldGridDensity;
    public float FieldMagMin { get; init; }
    public float FieldMagMax { get; init; }
    public double FieldMagMean { get; init; }
    public double ComputeTimeSeconds { get; init; }
    public string BackendName { get; init; } = "";

    // ── Softening ──
    public float SofteningEpsilon { get; init; }
    public float SofteningAlpha { get; init; }

    // ── Per-material breakdown ──
    public IReadOnlyList<MaterialClassification> MaterialBreakdown { get; init; }
        = Array.Empty<MaterialClassification>();

    // ── Charge sign histogram (10 bins from −1 to +1) ──
    public IReadOnlyList<HistogramBin> ChargeSignHistogram { get; init; }
        = Array.Empty<HistogramBin>();

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public sealed class MaterialClassification
{
    public string MaterialName { get; init; } = "";
    public int InnerCount { get; init; }
    public int OuterCount { get; init; }
    public int NeitherCount { get; init; }
    public int Total => InnerCount + OuterCount + NeitherCount;
}

public sealed class HistogramBin
{
    public float RangeMin { get; init; }
    public float RangeMax { get; init; }
    public int Count { get; init; }
    public string Label => $"[{RangeMin:F1}, {RangeMax:F1})";
}