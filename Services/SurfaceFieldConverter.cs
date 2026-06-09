using System.Numerics;
using EFieldSimulation.Models;

namespace EFieldSimulation.Services;

/// <summary>
/// Converts between average surface field strength and volume charge density
/// using Gauss's law:  E_avg · A ≈ ρ · V / ε₀
/// </summary>
public static class SurfaceFieldConverter
{
    /// <summary>Vacuum permittivity (F/m).</summary>
    public const double Epsilon0 = 8.854187817e-12;

    /// <summary>
    /// ρ = E_target · ε₀ · A / V
    /// </summary>
    public static double FieldStrengthToChargeDensity(
        double targetEVm, double volumeM3, double surfaceAreaM2)
    {
        if (volumeM3 < 1e-30 || surfaceAreaM2 < 1e-30) return 0;
        return targetEVm * Epsilon0 * surfaceAreaM2 / volumeM3;
    }

    /// <summary>
    /// E_avg = ρ · V / (ε₀ · A)
    /// </summary>
    public static double ChargeDensityToFieldStrength(
        double rhoCm3, double volumeM3, double surfaceAreaM2)
    {
        if (surfaceAreaM2 < 1e-30) return 0;
        return rhoCm3 * volumeM3 / (Epsilon0 * surfaceAreaM2);
    }

    /// <summary>
    /// Computes surface area (m²) from the shape's tessellated triangle mesh.
    /// Uses the same ShapeLibrary tessellation that drives the 3D display.
    /// </summary>
    public static double ComputeSurfaceArea(ArbitraryShapeParams p)
    {
        try
        {
            var (verts, indices, _) = ShapeLibrary.Tessellate(p);
            double area = 0;
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                var ab = verts[indices[i + 1]] - verts[indices[i]];
                var ac = verts[indices[i + 2]] - verts[indices[i]];
                area += Vector3.Cross(ab, ac).Length() * 0.5;
            }
            return Math.Max(area, 1e-30);
        }
        catch
        {
            return 1.0; // safe fallback
        }
    }

    // ══════════════════════════════════════════════════════════
    //  Mesh-based geometry (for tetrahedralization pipeline)
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// Surface area from a triangle mesh (MeshData uses Point3D vertices).
    /// </summary>
    public static double ComputeMeshSurfaceArea(MeshData mesh)
    {
        double area = 0;
        var v = mesh.Vertices;
        var idx = mesh.TriangleIndices;
        for (int i = 0; i + 2 < idx.Length; i += 3)
        {
            var a = v[idx[i]];
            var b = v[idx[i + 1]];
            var c = v[idx[i + 2]];
            double abx = b.X - a.X, aby = b.Y - a.Y, abz = b.Z - a.Z;
            double acx = c.X - a.X, acy = c.Y - a.Y, acz = c.Z - a.Z;
            double cx = aby * acz - abz * acy;
            double cy = abz * acx - abx * acz;
            double cz = abx * acy - aby * acx;
            area += Math.Sqrt(cx * cx + cy * cy + cz * cz) * 0.5;
        }
        return Math.Max(area, 1e-30);
    }

    /// <summary>
    /// Volume of a closed (watertight) triangle mesh via the divergence theorem:
    ///   V = (1/6) · |Σ v₀ · (v₁ × v₂)|  over all triangles.
    /// Requires consistent winding; returns absolute value.
    /// </summary>
    public static double ComputeMeshVolume(MeshData mesh)
    {
        double vol = 0;
        var v = mesh.Vertices;
        var idx = mesh.TriangleIndices;
        for (int i = 0; i + 2 < idx.Length; i += 3)
        {
            var a = v[idx[i]];
            var b = v[idx[i + 1]];
            var c = v[idx[i + 2]];
            vol += (a.X * (b.Y * c.Z - b.Z * c.Y)
                  + a.Y * (b.Z * c.X - b.X * c.Z)
                  + a.Z * (b.X * c.Y - b.Y * c.X)) / 6.0;
        }
        return Math.Max(Math.Abs(vol), 1e-30);
    }

    // ══════════════════════════════════════════════════════════
    //  Tet-pipeline unit conversions
    //  MeshTetrahedralizer speaks charges/cm³;
    //  internally:  ρ_SI = |charges/cm³| · e · 1e6
    // ══════════════════════════════════════════════════════════

    private const double E_CHARGE = 1.602176634e-19;

    /// <summary>ρ (C/m³) → elementary charges per cm³.</summary>
    public static double RhoSIToChargesPerCm3(double rhoSI) =>
        rhoSI / (E_CHARGE * 1e6);

    /// <summary>Elementary charges per cm³ → ρ (C/m³).</summary>
    public static double ChargesPerCm3ToRhoSI(double chargesPerCm3) =>
        Math.Abs(chargesPerCm3) * E_CHARGE * 1e6;

    /// <summary>
    /// Target surface |E| (V/m) → charges/cm³ via Gauss's law.
    /// Combines FieldStrengthToChargeDensity + unit conversion.
    /// </summary>
    public static double FieldStrengthToChargesPerCm3(
        double targetEVm, double volumeM3, double surfaceAreaM2) =>
        RhoSIToChargesPerCm3(
            FieldStrengthToChargeDensity(targetEVm, volumeM3, surfaceAreaM2));

    /// <summary>
    /// Charges/cm³ → estimated average surface |E| (V/m).
    /// </summary>
    public static double ChargesPerCm3ToFieldStrength(
        double chargesPerCm3, double volumeM3, double surfaceAreaM2) =>
        ChargeDensityToFieldStrength(
            ChargesPerCm3ToRhoSI(chargesPerCm3), volumeM3, surfaceAreaM2);
}