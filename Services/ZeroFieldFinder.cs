using System;
using System.Collections.Generic;
using System.Numerics;

namespace EFieldSimulation.Services;

/// <summary>
/// Locates electric-field zero points and near-zero magnitude contours
/// on a 2D field slice. Works entirely in grid-index space; callers
/// convert to world or pixel coordinates as needed.
///
/// When a DirectFieldSampler is provided, Newton–Raphson refinement
/// evaluates the field by sampling source entries directly (single
/// trilinear interpolation) instead of bilinear-interpolating the
/// already-interpolated slice data. This eliminates one layer of
/// interpolation error and dramatically improves cancellation accuracy
/// for superposed fields from independent grids.
/// </summary>
public static class ZeroFieldFinder
{
    // ── Result types ─────────────────────────────────────────

    public sealed class ZeroFieldResult
    {
        public List<ZeroPoint> Points { get; } = new();
        public List<ContourSegment> Contour { get; } = new();

        /// <summary>Absolute |E| threshold used (V/m).</summary>
        public float ThresholdUsed { get; init; }
        public int SliceAxis { get; init; }

        /// <summary>
        /// Reference magnitude used to compute threshold (may differ from
        /// MaxMagnitude when robust percentile mode is active).
        /// </summary>
        public float ReferenceMagnitude { get; init; }
    }

    /// <summary>
    /// A single E≈0 location on the slice, in fractional grid coordinates.
    /// </summary>
    public readonly record struct ZeroPoint(
        float GridX,        // fractional index along slice axis-0
        float GridY,        // fractional index along slice axis-1
        float Magnitude,    // |E| at the refined position
        bool IsFullZero);   // true when out-of-plane component is also ≈0

    /// <summary>
    /// One segment of the near-zero iso-contour, in fractional grid coords.
    /// </summary>
    public readonly record struct ContourSegment(
        float X0, float Y0,
        float X1, float Y1);

    /// <summary>
    /// Delegate that evaluates the full 3-component superposed field at
    /// an arbitrary world-space position by sampling each source entry
    /// directly. This bypasses the pre-computed slice grid entirely.
    /// </summary>
    /// <param name="worldPos">World-space sample position.</param>
    /// <returns>Superposed E-field vector at that position.</returns>
    public delegate Vector3 DirectFieldSampler(Vector3 worldPos);

    /// <summary>
    /// Everything the Newton refiner needs to convert between grid coords
    /// and world coords, plus sample the field directly.
    /// </summary>
    public sealed class SliceGeometry
    {
        public required int SliceAxis { get; init; }
        public required float SlicePosition { get; init; }
        public required float Axis0Min { get; init; }
        public required float Axis0Max { get; init; }
        public required float Axis1Min { get; init; }
        public required float Axis1Max { get; init; }
        public required int Resolution { get; init; }
        public required DirectFieldSampler Sampler { get; init; }

        public int Ax0 => (SliceAxis + 1) % 3;
        public int Ax1 => (SliceAxis + 2) % 3;

        public float Du0 => Resolution > 1
            ? (Axis0Max - Axis0Min) / (Resolution - 1) : 1f;
        public float Du1 => Resolution > 1
            ? (Axis1Max - Axis1Min) / (Resolution - 1) : 1f;

        /// <summary>
        /// Convert fractional grid coordinates to a world-space point on the slice.
        /// </summary>
        public Vector3 GridToWorld(float gx, float gy)
        {
            float u0 = Axis0Min + gx * Du0;
            float u1 = Axis1Min + gy * Du1;
            float x = 0, y = 0, z = 0;
            SetComp(ref x, ref y, ref z, SliceAxis, SlicePosition);
            SetComp(ref x, ref y, ref z, Ax0, u0);
            SetComp(ref x, ref y, ref z, Ax1, u1);
            return new Vector3(x, y, z);
        }

        /// <summary>
        /// Sample the full superposed field at fractional grid coordinates
        /// by going through world space and calling each source entry directly.
        /// </summary>
        public Vector3 SampleAtGrid(float gx, float gy)
        {
            return Sampler(GridToWorld(gx, gy));
        }

        private static void SetComp(ref float x, ref float y, ref float z, int a, float v)
        {
            switch (a) { case 0: x = v; break; case 1: y = v; break; case 2: z = v; break; }
        }
    }

    // ── Public entry point ───────────────────────────────────

    /// <param name="slice">Computed slice with populated FieldValues and Magnitudes.</param>
    /// <param name="thresholdFraction">
    /// Fraction of reference magnitude below which the field is considered "near zero."
    /// The marching-squares contour is drawn at this level.
    /// </param>
    /// <param name="newtonIterations">Max Newton–Raphson steps per candidate.</param>
    /// <param name="geometry">
    /// If non-null, Newton refinement samples the field directly from source entries
    /// instead of bilinear-interpolating the slice grid. This eliminates one layer of
    /// interpolation error and is strongly recommended when superposing fields from
    /// independent grids (e.g. tet surface + Coulomb particles).
    /// </param>
    /// <param name="useRobustPercentile">
    /// If true, uses the 99th percentile of slice magnitudes as the reference for
    /// threshold computation instead of the raw maximum. This prevents hot pixels
    /// near discrete charges from inflating the reference magnitude.
    /// </param>
    public static ZeroFieldResult Find(
        SliceResult slice,
        float thresholdFraction = 0.02f,
        int newtonIterations = 12,
        SliceGeometry? geometry = null,
        bool useRobustPercentile = true)
    {
        int res = slice.Resolution;
        float maxMag = slice.MaxMagnitude;

        // Degenerate or empty slice — nothing to find
        if (res < 3 || maxMag < 1e-30f ||
            slice.FieldValues is null || slice.Magnitudes is null)
        {
            return new ZeroFieldResult
            {
                ThresholdUsed = 0f,
                SliceAxis = slice.SliceAxis,
                ReferenceMagnitude = 0f
            };
        }

        // Compute reference magnitude
        float refMag;
        if (useRobustPercentile)
        {
            refMag = ComputePercentileMagnitude(slice.Magnitudes, res, 0.99f);
            if (refMag < 1e-30f) refMag = maxMag; // fallback
        }
        else
        {
            refMag = maxMag;
        }

        float threshold = refMag * Math.Clamp(thresholdFraction, 1e-6f, 1f);

        var result = new ZeroFieldResult
        {
            ThresholdUsed = threshold,
            SliceAxis = slice.SliceAxis,
            ReferenceMagnitude = refMag
        };

        // Phase 1: isolated zero-point candidates via local-minima scan + Newton
        FindCandidateZeros(slice, threshold, newtonIterations, result, geometry);

        // Phase 2: marching-squares contour at the threshold level
        BuildThresholdContour(slice.Magnitudes, res, threshold, result.Contour);

        return result;
    }

    // ── Robust percentile computation ────────────────────────

    /// <summary>
    /// Compute a percentile of the magnitude field without allocating a full
    /// sorted copy for very large slices. For res ≤ 512 we sort directly;
    /// for larger slices we use a histogram approximation.
    /// </summary>
    private static float ComputePercentileMagnitude(float[,] magnitudes, int res, float percentile)
    {
        int n = res * res;
        var flat = new float[n];
        int k = 0;
        for (int i = 0; i < res; i++)
            for (int j = 0; j < res; j++)
                flat[k++] = magnitudes[i, j];

        if (n <= 512 * 512)
        {
            Array.Sort(flat);
            int idx = Math.Clamp((int)(percentile * (n - 1)), 0, n - 1);
            return flat[idx];
        }

        // Histogram approximation for large slices
        float min = flat[0], max = flat[0];
        for (int i = 1; i < n; i++)
        {
            if (flat[i] < min) min = flat[i];
            if (flat[i] > max) max = flat[i];
        }
        if (max - min < 1e-30f) return max;

        const int nBins = 10000;
        var bins = new int[nBins];
        float scale = (nBins - 1) / (max - min);
        for (int i = 0; i < n; i++)
        {
            int b = Math.Clamp((int)((flat[i] - min) * scale), 0, nBins - 1);
            bins[b]++;
        }

        int target = (int)(percentile * n);
        int cumulative = 0;
        for (int b = 0; b < nBins; b++)
        {
            cumulative += bins[b];
            if (cumulative >= target)
                return min + b / scale;
        }
        return max;
    }

    // ── Phase 1: zero-point detection ────────────────────────

    private static void FindCandidateZeros(
        SliceResult slice, float threshold, int maxNewton,
        ZeroFieldResult result, SliceGeometry? geometry)
    {
        int res = slice.Resolution;
        var mag = slice.Magnitudes;
        int ax0 = (slice.SliceAxis + 1) % 3;
        int ax1 = (slice.SliceAxis + 2) % 3;

        // Adaptive candidate threshold: scan with a wider net, let Newton narrow
        // When using direct sampling, Newton can find much better zeros than
        // the slice grid suggests, so we widen the initial search window.
        float candidateThreshold = geometry != null
            ? threshold * 5f  // wider search when we have direct sampling
            : threshold;

        // Scan interior pixels (border pixels lack full 8-neighbourhood)
        for (int i = 1; i < res - 1; i++)
            for (int j = 1; j < res - 1; j++)
            {
                float m = mag[i, j];
                if (m > candidateThreshold) continue;

                // Strict 8-connected local-minimum test:
                // reject if *any* neighbour has a strictly lower magnitude.
                bool isLocalMin = true;
                for (int di = -1; di <= 1 && isLocalMin; di++)
                    for (int dj = -1; dj <= 1 && isLocalMin; dj++)
                    {
                        if (di == 0 && dj == 0) continue;
                        if (mag[i + di, j + dj] < m)
                            isLocalMin = false;
                    }

                if (!isLocalMin) continue;

                // Refine with Newton–Raphson on the two in-plane field components
                float gx = i, gy = j;

                if (geometry != null)
                {
                    // Direct-sampling Newton: bypasses slice grid interpolation entirely
                    RefineNewtonDirect(geometry, ax0, ax1, ref gx, ref gy, maxNewton, res);
                }
                else
                {
                    // Legacy: Newton on bilinear-interpolated slice data
                    RefineNewton(slice, ax0, ax1, ref gx, ref gy, maxNewton, res);
                }

                // Bounds guard: reject if refinement walked off grid
                const float margin = 0.5f;
                if (gx < margin || gx > res - 1 - margin ||
                    gy < margin || gy > res - 1 - margin)
                    continue;

                // Evaluate the full 3-component field at the refined point
                Vector3 field;
                if (geometry != null)
                {
                    field = geometry.SampleAtGrid(gx, gy);
                }
                else
                {
                    field = BilinearField(slice.FieldValues, res, gx, gy);
                }

                float refinedMag = field.Length();

                // Accept points within a generous window of the threshold
                if (refinedMag > threshold * 6f) continue;

                // Classify: is the out-of-plane component also near zero?
                float outOfPlane = MathF.Abs(Comp(field, slice.SliceAxis));
                bool fullZero = refinedMag < threshold * 3f &&
                                outOfPlane < threshold;

                result.Points.Add(new ZeroPoint(gx, gy, refinedMag, fullZero));
            }

        Deduplicate(result.Points, minSeparation: 2.5f);
    }

    /// <summary>
    /// Newton–Raphson on the two in-plane field components F_u(x,y)=0, F_v(x,y)=0.
    /// Uses direct field sampling through SliceGeometry — no slice grid interpolation.
    /// </summary>
    private static void RefineNewtonDirect(
        SliceGeometry geometry, int ax0, int ax1,
        ref float gx, ref float gy,
        int maxIter, int res)
    {
        // Use a smaller central-difference step since we're sampling the real field
        const float h = 0.25f;
        const float maxStep = 3f;
        const float convTol = 0.001f;  // tighter convergence since field is more accurate
        float border = h + 0.5f;

        for (int iter = 0; iter < maxIter; iter++)
        {
            if (gx < border || gx > res - 1 - border ||
                gy < border || gy > res - 1 - border)
                return;

            var f = geometry.SampleAtGrid(gx, gy);
            float fu = Comp(f, ax0);
            float fv = Comp(f, ax1);

            if (fu * fu + fv * fv < 1e-30f) return; // already at zero

            // Central-difference Jacobian using direct field samples
            var fxp = geometry.SampleAtGrid(gx + h, gy);
            var fxm = geometry.SampleAtGrid(gx - h, gy);
            var fyp = geometry.SampleAtGrid(gx, gy + h);
            var fym = geometry.SampleAtGrid(gx, gy - h);

            float J00 = (Comp(fxp, ax0) - Comp(fxm, ax0)) / (2f * h);
            float J01 = (Comp(fyp, ax0) - Comp(fym, ax0)) / (2f * h);
            float J10 = (Comp(fxp, ax1) - Comp(fxm, ax1)) / (2f * h);
            float J11 = (Comp(fyp, ax1) - Comp(fym, ax1)) / (2f * h);

            float det = J00 * J11 - J01 * J10;
            if (MathF.Abs(det) < 1e-20f) return; // singular Jacobian

            float invDet = 1f / det;
            float dx = -(J11 * fu - J01 * fv) * invDet;
            float dy = -(-J10 * fu + J00 * fv) * invDet;

            // Damped step
            float stepLen = MathF.Sqrt(dx * dx + dy * dy);
            if (stepLen > maxStep)
            {
                float s = maxStep / stepLen;
                dx *= s;
                dy *= s;
            }

            gx += dx;
            gy += dy;

            if (stepLen < convTol) return; // converged
        }
    }

    /// <summary>
    /// Legacy Newton–Raphson on bilinear-interpolated slice data.
    /// Kept for backward compatibility when no SliceGeometry is provided.
    /// </summary>
    private static void RefineNewton(
        SliceResult slice, int ax0, int ax1,
        ref float gx, ref float gy,
        int maxIter, int res)
    {
        const float h = 0.5f;
        const float maxStep = 3f;
        const float convTol = 0.005f;
        float border = h + 0.5f;

        for (int iter = 0; iter < maxIter; iter++)
        {
            if (gx < border || gx > res - 1 - border ||
                gy < border || gy > res - 1 - border)
                return;

            var f = BilinearField(slice.FieldValues, res, gx, gy);
            float fu = Comp(f, ax0);
            float fv = Comp(f, ax1);

            if (fu * fu + fv * fv < 1e-30f) return;

            var fxp = BilinearField(slice.FieldValues, res, gx + h, gy);
            var fxm = BilinearField(slice.FieldValues, res, gx - h, gy);
            var fyp = BilinearField(slice.FieldValues, res, gx, gy + h);
            var fym = BilinearField(slice.FieldValues, res, gx, gy - h);

            float J00 = (Comp(fxp, ax0) - Comp(fxm, ax0)) / (2f * h);
            float J01 = (Comp(fyp, ax0) - Comp(fym, ax0)) / (2f * h);
            float J10 = (Comp(fxp, ax1) - Comp(fxm, ax1)) / (2f * h);
            float J11 = (Comp(fyp, ax1) - Comp(fym, ax1)) / (2f * h);

            float det = J00 * J11 - J01 * J10;
            if (MathF.Abs(det) < 1e-20f) return;

            float invDet = 1f / det;
            float dx = -(J11 * fu - J01 * fv) * invDet;
            float dy = -(-J10 * fu + J00 * fv) * invDet;

            float stepLen = MathF.Sqrt(dx * dx + dy * dy);
            if (stepLen > maxStep)
            {
                float s = maxStep / stepLen;
                dx *= s;
                dy *= s;
            }

            gx += dx;
            gy += dy;

            if (stepLen < convTol) return;
        }
    }

    /// <summary>
    /// Remove duplicate detections within <paramref name="minSeparation"/>
    /// grid units, keeping the point with the smaller magnitude.
    /// </summary>
    private static void Deduplicate(List<ZeroPoint> pts, float minSeparation)
    {
        float minSep2 = minSeparation * minSeparation;
        for (int i = pts.Count - 1; i >= 0; i--)
        {
            for (int j = 0; j < i; j++)
            {
                float dx = pts[i].GridX - pts[j].GridX;
                float dy = pts[i].GridY - pts[j].GridY;
                if (dx * dx + dy * dy < minSep2)
                {
                    if (pts[i].Magnitude < pts[j].Magnitude)
                        pts[j] = pts[i];
                    pts.RemoveAt(i);
                    break;
                }
            }
        }
    }

    // ── Phase 2: marching-squares contour ────────────────────

    private static readonly int[][] SegmentTable =
    {
        /*  0 */ Array.Empty<int>(),
        /*  1 */ new[] { 3, 0 },
        /*  2 */ new[] { 0, 1 },
        /*  3 */ new[] { 3, 1 },
        /*  4 */ new[] { 1, 2 },
        /*  5 */ new[] { 3, 0, 1, 2 },
        /*  6 */ new[] { 0, 2 },
        /*  7 */ new[] { 3, 2 },
        /*  8 */ new[] { 2, 3 },
        /*  9 */ new[] { 2, 0 },
        /* 10 */ new[] { 0, 1, 2, 3 },
        /* 11 */ new[] { 2, 1 },
        /* 12 */ new[] { 1, 3 },
        /* 13 */ new[] { 1, 0 },
        /* 14 */ new[] { 0, 3 },
        /* 15 */ Array.Empty<int>(),
    };

    private static readonly int[] Saddle5Alt = { 0, 1, 2, 3 };
    private static readonly int[] Saddle10Alt = { 3, 0, 1, 2 };

    private static void BuildThresholdContour(
        float[,] magnitudes, int res, float threshold,
        List<ContourSegment> contour)
    {
        for (int i = 0; i < res - 1; i++)
            for (int j = 0; j < res - 1; j++)
            {
                float v0 = magnitudes[i, j];
                float v1 = magnitudes[i + 1, j];
                float v2 = magnitudes[i + 1, j + 1];
                float v3 = magnitudes[i, j + 1];

                int cfg = (v0 < threshold ? 1 : 0)
                        | (v1 < threshold ? 2 : 0)
                        | (v2 < threshold ? 4 : 0)
                        | (v3 < threshold ? 8 : 0);

                if (cfg == 0 || cfg == 15) continue;

                int[] segs;
                if (cfg == 5)
                {
                    float centre = (v0 + v1 + v2 + v3) * 0.25f;
                    segs = centre < threshold ? Saddle5Alt : SegmentTable[5];
                }
                else if (cfg == 10)
                {
                    float centre = (v0 + v1 + v2 + v3) * 0.25f;
                    segs = centre < threshold ? Saddle10Alt : SegmentTable[10];
                }
                else
                {
                    segs = SegmentTable[cfg];
                }

                EmitSegments(contour, i, j, segs, v0, v1, v2, v3, threshold);
            }
    }

    private static void EmitSegments(
        List<ContourSegment> dst,
        int ci, int cj, int[] edges,
        float v0, float v1, float v2, float v3,
        float iso)
    {
        for (int k = 0; k < edges.Length; k += 2)
        {
            var (x0, y0) = EdgeInterp(ci, cj, edges[k], v0, v1, v2, v3, iso);
            var (x1, y1) = EdgeInterp(ci, cj, edges[k + 1], v0, v1, v2, v3, iso);
            dst.Add(new ContourSegment(x0, y0, x1, y1));
        }
    }

    private static (float x, float y) EdgeInterp(
        int ci, int cj, int edge,
        float v0, float v1, float v2, float v3,
        float iso) => edge switch
        {
            0 => (ci + Frac(v0, v1, iso), cj),
            1 => (ci + 1, cj + Frac(v1, v2, iso)),
            2 => (ci + Frac(v3, v2, iso), cj + 1),
            3 => (ci, cj + Frac(v0, v3, iso)),
            _ => (ci + 0.5f, cj + 0.5f)
        };

    private static float Frac(float va, float vb, float iso)
    {
        float d = va - vb;
        if (MathF.Abs(d) < 1e-30f) return 0.5f;
        return Math.Clamp((va - iso) / d, 0f, 1f);
    }

    // ── Shared helpers ───────────────────────────────────────

    private static Vector3 BilinearField(Vector3[,] field, int res, float gx, float gy)
    {
        int ix = Math.Clamp((int)gx, 0, res - 2);
        int iy = Math.Clamp((int)gy, 0, res - 2);
        float tx = Math.Clamp(gx - ix, 0f, 1f);
        float ty = Math.Clamp(gy - iy, 0f, 1f);

        return Vector3.Lerp(
            Vector3.Lerp(field[ix, iy], field[ix + 1, iy], tx),
            Vector3.Lerp(field[ix, iy + 1], field[ix + 1, iy + 1], tx),
            ty);
    }

    private static float Comp(Vector3 v, int axis) => axis switch
    {
        0 => v.X,
        1 => v.Y,
        _ => v.Z
    };

    // ── Bitmap overlay rendering ─────────────────────────────

    /// <summary>
    /// Draws the contour lines and zero-point markers onto an existing
    /// BGRA32 pixel buffer.
    /// </summary>
    public static void OverlayOnPixels(
        byte[] pixels, int res,
        ZeroFieldResult zeroResult,
        byte contourR = 0, byte contourG = 255, byte contourB = 255,
        byte pointR = 255, byte pointG = 255, byte pointB = 0,
        byte fullZeroR = 0, byte fullZeroG = 255, byte fullZeroB = 80,
        int pointRadius = 4)
    {
        if (zeroResult is null) return;

        foreach (var seg in zeroResult.Contour)
        {
            float px0 = seg.X0, py0 = res - 1 - seg.Y0;
            float px1 = seg.X1, py1 = res - 1 - seg.Y1;

            DrawLineThick(pixels, res, px0, py0, px1, py1,
                          contourR, contourG, contourB, 220, thickness: 1);
        }

        foreach (var pt in zeroResult.Points)
        {
            float px = pt.GridX;
            float py = res - 1 - pt.GridY;

            byte r = pt.IsFullZero ? fullZeroR : pointR;
            byte g = pt.IsFullZero ? fullZeroG : pointG;
            byte b = pt.IsFullZero ? fullZeroB : pointB;

            DrawCircleFilled(pixels, res, px, py, pointRadius + 1, 0, 0, 0, 180);
            DrawCircleFilled(pixels, res, px, py, pointRadius, r, g, b, 255);
            DrawCrosshair(pixels, res, (int)(px + 0.5f), (int)(py + 0.5f),
                          pointRadius + 3, 255, 255, 255, 200);
        }
    }

    // ── Pixel drawing primitives ─────────────────────────────

    private static void SetPixel(
        byte[] px, int res, int x, int y,
        byte r, byte g, byte b, byte a)
    {
        if ((uint)x >= (uint)res || (uint)y >= (uint)res) return;
        int idx = (y * res + x) * 4;
        if (a == 255)
        {
            px[idx] = b; px[idx + 1] = g; px[idx + 2] = r; px[idx + 3] = 255;
        }
        else
        {
            float sa = a / 255f, da = 1f - sa;
            px[idx] = (byte)(b * sa + px[idx] * da);
            px[idx + 1] = (byte)(g * sa + px[idx + 1] * da);
            px[idx + 2] = (byte)(r * sa + px[idx + 2] * da);
            px[idx + 3] = (byte)Math.Min(255, a + px[idx + 3] * da);
        }
    }

    private static void DrawLineThick(
        byte[] px, int res,
        float x0, float y0, float x1, float y1,
        byte r, byte g, byte b, byte a,
        int thickness)
    {
        float dx = x1 - x0, dy = y1 - y0;
        int steps = Math.Max(1, (int)(MathF.Sqrt(dx * dx + dy * dy) * 2f));
        float sx = dx / steps, sy = dy / steps;

        int half = thickness / 2;
        for (int s = 0; s <= steps; s++)
        {
            int cx = (int)(x0 + sx * s + 0.5f);
            int cy = (int)(y0 + sy * s + 0.5f);
            for (int oy = -half; oy <= half; oy++)
                for (int ox = -half; ox <= half; ox++)
                    SetPixel(px, res, cx + ox, cy + oy, r, g, b, a);
        }
    }

    private static void DrawCircleFilled(
        byte[] px, int res,
        float cx, float cy, int radius,
        byte r, byte g, byte b, byte a)
    {
        int icx = (int)(cx + 0.5f), icy = (int)(cy + 0.5f);
        int r2 = radius * radius;
        for (int dy = -radius; dy <= radius; dy++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                if (dx * dx + dy * dy <= r2)
                    SetPixel(px, res, icx + dx, icy + dy, r, g, b, a);
            }
    }

    private static void DrawCrosshair(
        byte[] px, int res,
        int cx, int cy, int arm,
        byte r, byte g, byte b, byte a)
    {
        for (int d = -arm; d <= arm; d++)
        {
            SetPixel(px, res, cx + d, cy, r, g, b, a);
            SetPixel(px, res, cx, cy + d, r, g, b, a);
        }
    }
}