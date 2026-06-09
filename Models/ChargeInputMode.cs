namespace EFieldSimulation.Models;

/// <summary>
/// How the user specifies charge magnitude for a volume shape.
/// </summary>
public enum ChargeInputMode
{
    /// <summary>User enters volume charge density ρ (C/m³) directly.</summary>
    VolumeDensity,

    /// <summary>User enters desired average |E| at the surface (V/m);
    /// ρ is derived via Gauss's law.</summary>
    SurfaceFieldStrength
}