using System.Numerics;

namespace EFieldSimulation.Models;

public sealed class ProbeResult
{
    public ProbePointSample[] Samples { get; init; } = Array.Empty<ProbePointSample>();
}

public sealed class ProbePointSample
{
    /// <summary>Parametric position along line (0 for point probes, 0–1 for lines).</summary>
    public float T { get; init; }

    /// <summary>World-space position of this sample.</summary>
    public Vector3 Position { get; init; }

    /// <summary>Distance from probe start point (metres).</summary>
    public float Distance { get; init; }

    /// <summary>Superposed E-field from all sources.</summary>
    public Vector3 TotalField { get; init; }

    public float TotalFieldMagnitude => TotalField.Length();

    /// <summary>Electrostatic potential (V = 0 at infinity for particles, ∫E·dl for grids).</summary>
    public float TotalVoltage { get; init; }

    /// <summary>Per-source breakdown.</summary>
    public ProbeSourceContribution[] Contributions { get; init; } = Array.Empty<ProbeSourceContribution>();

    // ── Grouped aggregates (vector-sum then magnitude — physically correct) ──
    public float StaticFieldMagnitude { get; init; }
    public float ParticleFieldMagnitude { get; init; }
    public float StaticVoltage { get; init; }
    public float ParticleVoltage { get; init; }

    // ── Formatted accessors for DataGrid binding ──
    public string PositionText => $"({Position.X:F4}, {Position.Y:F4}, {Position.Z:F4})";
    public string DistText => $"{Distance:F5}";
    public string TotalEText => $"{TotalFieldMagnitude:E3}";
    public string StaticEText => $"{StaticFieldMagnitude:E3}";
    public string ParticleEText => $"{ParticleFieldMagnitude:E3}";
    public string TotalVText => $"{TotalVoltage:E3}";
    public string StaticVText => $"{StaticVoltage:E3}";
    public string ParticleVText => $"{ParticleVoltage:E3}";
}

public sealed class ProbeSourceContribution
{
    public string SourceName { get; init; } = "";
    public string SourceCategory { get; init; } = ""; // "Static" or "Particles"
    public bool IsCoulombDerived { get; init; }
    public Vector3 FieldVector { get; init; }
    public float FieldMagnitude => FieldVector.Length();
    public float VoltageContribution { get; init; }
}