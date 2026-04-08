using System.Numerics;
using EFieldSimulation.Models;

namespace EFieldSimulation.Services;

public sealed class FieldSuperposition
{
    /// <summary>
    /// Creates a SliceGeometry that enables direct field sampling during Newton refinement.
    /// </summary>
    public static ZeroFieldFinder.SliceGeometry CreateSliceGeometry(
        SliceResult slice,
        IReadOnlyList<SceneEntry> fieldEntries)
    {
        // Capture the entry list for the closure
        var entries = fieldEntries.ToArray();

        return new ZeroFieldFinder.SliceGeometry
        {
            SliceAxis = slice.SliceAxis,
            SlicePosition = slice.SlicePosition,
            Axis0Min = slice.Axis0Min,
            Axis0Max = slice.Axis0Max,
            Axis1Min = slice.Axis1Min,
            Axis1Max = slice.Axis1Max,
            Resolution = slice.Resolution,
            Sampler = worldPos =>
            {
                Vector3 total = Vector3.Zero;
                foreach (var e in entries)
                    total += SampleEntry(e, worldPos);
                return total;
            }
        };
    }

    public SliceResult ComputeSlice(
     int sliceAxis, float slicePosition,
     int resolution, IReadOnlyList<SceneEntry> fieldEntries)
    {
        if (fieldEntries.Count == 0)
            return EmptyResult(sliceAxis, slicePosition, resolution);

        // Parallel bounds computation
        var entryBounds = new (Vector3 min, Vector3 max)[fieldEntries.Count];
        Parallel.For(0, fieldEntries.Count, i =>
        {
            entryBounds[i] = GetEffectiveBounds(fieldEntries[i].Field!, fieldEntries[i].Transform);
        });

        Vector3 unionMin = new(float.MaxValue), unionMax = new(float.MinValue);
        for (int i = 0; i < entryBounds.Length; i++)
        {
            unionMin = Vector3.Min(unionMin, entryBounds[i].min);
            unionMax = Vector3.Max(unionMax, entryBounds[i].max);
        }

        if (unionMin.X >= unionMax.X)
            return EmptyResult(sliceAxis, slicePosition, resolution);

        int ax0 = (sliceAxis + 1) % 3;
        int ax1 = (sliceAxis + 2) % 3;
        float u0Min = Comp(unionMin, ax0), u0Max = Comp(unionMax, ax0);
        float u1Min = Comp(unionMin, ax1), u1Max = Comp(unionMax, ax1);

        var result = new SliceResult
        {
            Resolution = resolution,
            SliceAxis = sliceAxis,
            SlicePosition = slicePosition,
            Axis0Min = u0Min,
            Axis0Max = u0Max,
            Axis1Min = u1Min,
            Axis1Max = u1Max,
            FieldValues = new Vector3[resolution, resolution],
            Magnitudes = new float[resolution, resolution]
        };

        float du0 = (u0Max - u0Min) / (resolution - 1);
        float du1 = (u1Max - u1Min) / (resolution - 1);

        // Parallel field sampling
        Parallel.For(0, resolution, i =>
        {
            for (int j = 0; j < resolution; j++)
            {
                Vector3 wp = BuildWorldPoint(sliceAxis, slicePosition,
                    ax0, u0Min + i * du0, ax1, u1Min + j * du1);
                Vector3 total = Vector3.Zero;
                foreach (var e in fieldEntries)
                    total += SampleEntry(e, wp);

                result.FieldValues[i, j] = total;
                result.Magnitudes[i, j] = total.Length();
            }
        });

        // Parallel max-magnitude reduction
        float maxMag = 0;
        object lockObj = new();
        Parallel.For(0, resolution, () => 0f, (i, _, localMax) =>
        {
            for (int j = 0; j < resolution; j++)
                if (result.Magnitudes[i, j] > localMax)
                    localMax = result.Magnitudes[i, j];
            return localMax;
        },
        localMax =>
        {
            lock (lockObj)
                if (localMax > maxMag) maxMag = localMax;
        });

        result.MaxMagnitude = maxMag;
        return result;
    }
    public static Vector3 SampleEntry(SceneEntry entry, Vector3 worldPos)
    {
        Vector3 localPos = entry.Transform.WorldToLocal(worldPos);
        Vector3 localField = entry.FieldAccessor != null
            ? entry.FieldAccessor.Interpolate(localPos)
            : NearestLookup(entry.Field!, localPos);
        return entry.Transform.LocalVectorToWorld(localField);
    }

    public static (Vector3 field, float chargeSign) SampleEntryWithChargeSign(SceneEntry entry, Vector3 worldPos)
    {
        Vector3 localPos = entry.Transform.WorldToLocal(worldPos);
        Vector3 localField;

        if (entry.FieldAccessor != null)
        {
            localField = entry.FieldAccessor.Interpolate(localPos);
        }
        else if (entry.Field != null)
        {
            localField = NearestLookup(entry.Field, localPos);
        }
        else
        {
            return (Vector3.Zero, 0f);
        }

        return (entry.Transform.LocalVectorToWorld(localField), 0f);
    }
    private static Vector3 NearestLookup(ElectricFieldData field, Vector3 pos)
    {
        float best = float.MaxValue; int idx = 0;
        for (int k = 0; k < field.PointCount; k++)
        {
            float d = Vector3.DistanceSquared(pos, field.GetGridPoint(k));
            if (d < best) { best = d; idx = k; }
        }
        return field.GetFieldVector(idx);
    }

    private static (Vector3, Vector3) GetEffectiveBounds(
        ElectricFieldData field, TransformState transform)
    {
        var (min, max) = field.GetBounds();
        var mat = transform.ToMatrix4x4();
        Vector3[] corners =
        {
            new(min.X,min.Y,min.Z), new(max.X,min.Y,min.Z),
            new(min.X,max.Y,min.Z), new(max.X,max.Y,min.Z),
            new(min.X,min.Y,max.Z), new(max.X,min.Y,max.Z),
            new(min.X,max.Y,max.Z), new(max.X,max.Y,max.Z),
        };
        Vector3 tMin = new(float.MaxValue), tMax = new(float.MinValue);
        foreach (var c in corners)
        { var tc = Vector3.Transform(c, mat); tMin = Vector3.Min(tMin, tc); tMax = Vector3.Max(tMax, tc); }
        return (tMin, tMax);
    }

    private static SliceResult EmptyResult(int ax, float pos, int res) => new()
    {
        Resolution = res,
        SliceAxis = ax,
        SlicePosition = pos,
        FieldValues = new Vector3[res, res],
        Magnitudes = new float[res, res]
    };

    private static float Comp(Vector3 v, int a) => a switch { 0 => v.X, 1 => v.Y, _ => v.Z };

    private static Vector3 BuildWorldPoint(int sa, float sp, int a0, float u0, int a1, float u1)
    {
        float x = 0, y = 0, z = 0;
        Set(ref x, ref y, ref z, sa, sp);
        Set(ref x, ref y, ref z, a0, u0);
        Set(ref x, ref y, ref z, a1, u1);
        return new(x, y, z);
    }

    private static void Set(ref float x, ref float y, ref float z, int a, float v)
    { switch (a) { case 0: x = v; break; case 1: y = v; break; case 2: z = v; break; } }
}

public sealed class SliceResult
{
    public int Resolution { get; set; }
    public int SliceAxis { get; set; }
    public float SlicePosition { get; set; }
    public float Axis0Min { get; set; }
    public float Axis0Max { get; set; }
    public float Axis1Min { get; set; }
    public float Axis1Max { get; set; }
    public Vector3[,] FieldValues { get; set; } = null!;
    public float[,] Magnitudes { get; set; } = null!;
    public float MaxMagnitude { get; set; }
}