namespace EFieldSimulation.Models;

public sealed class ArbitraryShapeParams
{
    public string Type { get; set; } = "Cylinder";

    // Center (local to owning SceneEntry; world offset handled by transform)
    public double CenterX { get; set; }
    public double CenterY { get; set; }
    public double CenterZ { get; set; }

    // Cylinder
    public double Radius { get; set; } = 1.0;
    public double Height { get; set; } = 2.0;

    // Torus / BoundedToroid / Helix
    public double MajorRadius { get; set; } = 2.0;
    public double MinorRadius { get; set; } = 0.5;
    public double AngleStartDeg { get; set; } = 0;
    public double AngleSpanDeg { get; set; } = 360;

    // Helix
    public double HelixTurns { get; set; } = 3.0;   // number of windings
    public double HelixPitch { get; set; } = 1.0;   // vertical rise per full turn

    // Tessellation quality
    public int RadialSegments { get; set; } = 64;
    public int TubularSegments { get; set; } = 32;

    // Sphere
    public double SphereRadius { get; set; } = 1.0;

    // Cone
    public double ConeTopRadius { get; set; } = 0.0;
    public double ConeBottomRadius { get; set; } = 1.0;
    public double ConeHeight { get; set; } = 2.0;

    // Per-volume charge parameters
    public double VolChargeDensity { get; set; } = 1.0;
    public bool IsPositive { get; set; } = true;
    public int VolParticleCount { get; set; } = 1000;

    /// <summary>Whether charge is specified as density or surface field strength.</summary>
    public ChargeInputMode InputMode { get; set; } = ChargeInputMode.VolumeDensity;

    /// <summary>Target average surface |E| in V/m. Only used when InputMode == SurfaceFieldStrength.</summary>
    public double SurfaceFieldStrength { get; set; }
}