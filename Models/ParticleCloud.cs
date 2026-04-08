using System.Numerics;

namespace EFieldSimulation.Models;

/// <summary>
/// A set of point charges uniformly distributed in a volume.
/// Positions are in the owning SceneEntry's local space.
/// </summary>
public sealed class ParticleCloud
{
    public Vector3[] Positions { get; init; } = Array.Empty<Vector3>();
    public float ChargePerParticle { get; init; }
    public int Count => Positions.Length;
}