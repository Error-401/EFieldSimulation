using System.Numerics;
using EFieldSimulation.Compute;
using EFieldSimulation.Helpers;
using EFieldSimulation.Models;

namespace EFieldSimulation.Services;

public static class MeshTetrahedralizer
{
    private const double Ke = 8.9875517923e9;
    private const double E_CHARGE = 1.602176634e-19;
    private const float FieldPadding = 1.75f;//1.15

    private static readonly string[] InnerKeys = { "inner", "bore", "interior", "cavity" };
    private static readonly string[] OuterKeys = { "outer", "exterior", "shell", "surface" };

    private static readonly int[][] Kuhn = {
        new[]{0,0,0, 1,0,0, 1,1,0, 1,1,1},
        new[]{0,0,0, 1,0,0, 1,0,1, 1,1,1},
        new[]{0,0,0, 0,1,0, 1,1,0, 1,1,1},
        new[]{0,0,0, 0,1,0, 0,1,1, 1,1,1},
        new[]{0,0,0, 0,0,1, 1,0,1, 1,1,1},
        new[]{0,0,0, 0,0,1, 0,1,1, 1,1,1},
    };

    /// <summary>
    /// Tetrahedralizes a mesh and computes the Coulomb E-field.
    /// Returns both the field data and diagnostic statistics.
    /// </summary>
    public static (ElectricFieldData field, TetStatistics stats) Tetrahedralize(
        MeshData mesh, double chargeDensityPerCm3, double maxVolCm3,
        int fieldGrid, bool innerIsPositive, float softeningAlpha = 0.5f,
        IProgress<double>? progress = null,
        IComputeBackend? backend = null)
    {
        backend ??= ComputeBackend.Default;

        double rhoSI = Math.Abs(chargeDensityPerCm3) * E_CHARGE * 1e6;

        double maxVolM3 = maxVolCm3 * 1e-6;
        float hf = (float)Math.Pow(6.0 * maxVolM3, 1.0 / 3.0);
        double tetVol = (double)hf * hf * hf / 6.0;

        var b = mesh.Bounds;
        var bMin = new Vector3((float)b.X, (float)b.Y, (float)b.Z);
        var bMax = new Vector3(
            (float)(b.X + b.SizeX), (float)(b.Y + b.SizeY), (float)(b.Z + b.SizeZ));
        var range = bMax - bMin;
        var meshCenter = (bMin + bMax) * 0.5f;
        float maxDim = MathF.Max(range.X, MathF.Max(range.Y, range.Z));
        if (maxDim < 1e-12f)
            throw new InvalidOperationException("Mesh has zero extent.");

        int nx = Math.Max(1, (int)MathF.Ceiling(range.X / hf));
        int ny = Math.Max(1, (int)MathF.Ceiling(range.Y / hf));
        int nz = Math.Max(1, (int)MathF.Ceiling(range.Z / hf));
        var tetOrigin = new Vector3(
            meshCenter.X - nx * hf * 0.5f,
            meshCenter.Y - ny * hf * 0.5f,
            meshCenter.Z - nz * hf * 0.5f);
        long totalCells = (long)nx * ny * nz;

        Console.WriteLine();
        Console.WriteLine("════════════════════════════════════════");
        Console.WriteLine(" TETRAHEDRALIZATION");
        Console.WriteLine("════════════════════════════════════════");
        Console.WriteLine($"  Mesh            : {mesh.Name}");
        Console.WriteLine($"  Vertices        : {mesh.Vertices.Length:N0}");
        Console.WriteLine($"  Triangles       : {mesh.TriangleIndices.Length / 3:N0}");
        Console.WriteLine($"  Bounds          : ({bMin.X:G5},{bMin.Y:G5},{bMin.Z:G5}) → ({bMax.X:G5},{bMax.Y:G5},{bMax.Z:G5})");
        Console.WriteLine($"  Charge density  : {chargeDensityPerCm3:E3} charges/cm³");
        Console.WriteLine($"  ρ (SI)          : {rhoSI:E3} C/m³");
        Console.WriteLine($"  Inner is        : {(innerIsPositive ? "POSITIVE" : "NEGATIVE")}");
        Console.WriteLine($"  Max tet volume  : {maxVolCm3:E3} cm³");
        Console.WriteLine($"  Cell size       : {hf:E3} m  ({hf * 1000:F4} mm)");
        Console.WriteLine($"  Actual tet vol  : {tetVol * 1e6:E4} cm³");
        Console.WriteLine($"  Grid            : {nx}×{ny}×{nz} = {totalCells:N0} cells");

        var tris = BuildTriangles(mesh);
        var tetGridMax = tetOrigin + new Vector3(nx * hf, ny * hf, nz * hf);
        var bins = new TriBins(tris, hf, tetOrigin, tetGridMax);

        int flatCells = nx * ny * nz;
        var allCentroids = new Vector3[flatCells * 6];
        var inside = new bool[flatCells * 6];

        Parallel.For(0, flatCells, ci =>
        {
            int ix = ci / (ny * nz), rem = ci % (ny * nz);
            int iy = rem / nz, iz = rem % nz;
            float x0 = tetOrigin.X + ix * hf;
            float y0 = tetOrigin.Y + iy * hf;
            float z0 = tetOrigin.Z + iz * hf;
            for (int t = 0; t < 6; t++)
            {
                var k = Kuhn[t];
                var c = new Vector3(
                    x0 + (k[0] + k[3] + k[6] + k[9]) * 0.25f * hf,
                    y0 + (k[1] + k[4] + k[7] + k[10]) * 0.25f * hf,
                    z0 + (k[2] + k[5] + k[8] + k[11]) * 0.25f * hf);
                int fi = ci * 6 + t;
                allCentroids[fi] = c;
                inside[fi] = bins.IsInside(c);
            }
        });

        var tetCentroids = new List<Vector3>();
        for (int i = 0; i < allCentroids.Length; i++)
            if (inside[i]) tetCentroids.Add(allCentroids[i]);

        int nTets = tetCentroids.Count;
        if (nTets == 0)
            throw new InvalidOperationException(
                "No tetrahedra inside mesh. Check mesh is watertight or reduce max tet volume.");

        double totalVol = nTets * tetVol;

        Console.WriteLine();
        Console.WriteLine("── Element statistics ──");
        Console.WriteLine($"  Interior tets   : {nTets:N0}");
        Console.WriteLine($"  Fill ratio      : {(double)nTets / (totalCells * 6) * 100:F1}%");
        Console.WriteLine($"  Element volume  : {tetVol:E4} m³");
        Console.WriteLine($"  Total volume    : {totalVol:E4} m³");

        // ── face classification ──
        float[] chargeSign;
        bool dipoleMode = false;
        var (innerC, outerC, nInner, nOuter, nNeither, matBreakdown) = ClassifyFaces(mesh);

        // Depth field stats (populated only in dipole mode)
        float rawDepthMin = 0, rawDepthMax = 0, volMedianT = 0.5f;
        float csMin = 0, csMax = 0;
        double csMean = 0;
        int nNegTets = 0, nPosTets = 0, nZeroTets = 0;

        if (innerC.Length > 0 && outerC.Length > 0)
        {
            dipoleMode = true;
            Console.WriteLine();
            Console.WriteLine("── Face classification (material-based) ──");
            Console.WriteLine($"  Inner faces     : {nInner:N0}");
            Console.WriteLine($"  Outer faces     : {nOuter:N0}");
            Console.WriteLine($"  Neither         : {nNeither:N0}");

            var (sign, depthStats) = ComputeDepthField(tetCentroids, tetVol, innerC, outerC);
            chargeSign = sign;
            rawDepthMin = depthStats.RawMin;
            rawDepthMax = depthStats.RawMax;
            volMedianT = depthStats.TStar;
            csMin = depthStats.SignMin;
            csMax = depthStats.SignMax;
            csMean = depthStats.SignMean;
            nNegTets = depthStats.NegCount;
            nPosTets = depthStats.PosCount;
            nZeroTets = depthStats.ZeroCount;

            if (innerIsPositive)
                for (int i = 0; i < chargeSign.Length; i++)
                    chargeSign[i] = -chargeSign[i];

            BalanceCharge(chargeSign, tetVol);
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("── Charge mode: UNIFORM ──");
            chargeSign = new float[nTets];
            float uniformSign = innerIsPositive ? 1f : -1f;
            Array.Fill(chargeSign, uniformSign);
            nPosTets = uniformSign > 0 ? nTets : 0;
            nNegTets = uniformSign < 0 ? nTets : 0;
            csMin = csMax = uniformSign;
            csMean = uniformSign;
        }

        // ── Build charge sign histogram (10 bins) ──
        var histogram = BuildChargeSignHistogram(chargeSign, 10);

        // ── compute per-tet charges ──
        var charges = new float[nTets];
        double posQ = 0, negQ = 0;
        for (int i = 0; i < nTets; i++)
        {
            charges[i] = (float)(tetVol * rhoSI * chargeSign[i]);
            if (charges[i] > 0) posQ += charges[i]; else negQ += charges[i];
        }
        double netQ = posQ + negQ;
        double totalAbsQ = Math.Abs(posQ) + Math.Abs(negQ);
        double imbalPct = totalAbsQ > 0 ? Math.Abs(netQ) / totalAbsQ * 100 : 0;

        Console.WriteLine();
        Console.WriteLine("── Charge summary ──");
        Console.WriteLine($"  +Q              : {posQ:E3} C");
        Console.WriteLine($"  −Q              : {negQ:E3} C");
        Console.WriteLine($"  Net charge      : {netQ:E3} C");
        Console.WriteLine($"  Imbalance       : {imbalPct:F6}%");

        // ── compute E-field on grid ──
        int gn = fieldGrid;
        int gTotal = gn * gn * gn;

        var fieldHalf = range * 0.5f * FieldPadding;
        float minHalf = maxDim * 0.5f * FieldPadding;
        fieldHalf = new Vector3(
            MathF.Max(fieldHalf.X, minHalf),
            MathF.Max(fieldHalf.Y, minHalf),
            MathF.Max(fieldHalf.Z, minHalf));
        var gMin = meshCenter - fieldHalf;
        var gMax = meshCenter + fieldHalf;

        var sp = new Vector3(
            gn > 1 ? (gMax.X - gMin.X) / (gn - 1) : 1f,
            gn > 1 ? (gMax.Y - gMin.Y) / (gn - 1) : 1f,
            gn > 1 ? (gMax.Z - gMin.Z) / (gn - 1) : 1f);

        var srcBuf = SourceBuffer.From(tetCentroids, charges);
        var gridSpec = new GridSpec(
            gMin.X, gMin.Y, gMin.Z,
            sp.X, sp.Y, sp.Z,
            gn, gn, gn);
        var fldBuf = new FieldBuffer(gTotal, trackSign: true);

        // ── set softening length ────
        float epsilon = softeningAlpha * hf;
        float prevEps2 = CoulombKernels.Epsilon2;
        CoulombKernels.Epsilon2 = epsilon * epsilon;
        Console.WriteLine($"  Softening ε     : {epsilon:E3} m  (α={softeningAlpha:F2} × h={hf:E3})");
        Console.WriteLine($"  Softening ε²    : {CoulombKernels.Epsilon2:E3}");


        var sw = System.Diagnostics.Stopwatch.StartNew();
        backend.ComputeCoulombField(srcBuf, gridSpec, fldBuf, progress);
        sw.Stop();
        double computeTime = sw.Elapsed.TotalSeconds;

        // ── restore default (zero) softening for other solvers ──
        CoulombKernels.Epsilon2 = prevEps2;

        float eMagMax = 0f, eMagMin = float.MaxValue;
        double eMagSum = 0;
        for (int i = 0; i < gTotal; i++)
        {
            float m = MathF.Sqrt(
                fldBuf.Ex[i] * fldBuf.Ex[i] +
                fldBuf.Ey[i] * fldBuf.Ey[i] +
                fldBuf.Ez[i] * fldBuf.Ez[i]);
            if (m > eMagMax) eMagMax = m;
            if (m < eMagMin) eMagMin = m;
            eMagSum += m;
        }

        Console.WriteLine($"  Time            : {computeTime:F2} s");
        Console.WriteLine($"  |E| min         : {eMagMin:E3} V/m");
        Console.WriteLine($"  |E| max         : {eMagMax:E3} V/m");
        Console.WriteLine($"  |E| mean        : {eMagSum / gTotal:E3} V/m");
        Console.WriteLine("════════════════════════════════════════");

        // ── After polarity flip, recount for stats ──
        int finalNeg = 0, finalPos = 0, finalZero = 0;
        for (int i = 0; i < chargeSign.Length; i++)
        {
            if (chargeSign[i] < -0.001f) finalNeg++;
            else if (chargeSign[i] > 0.001f) finalPos++;
            else finalZero++;
        }

        var stats = new TetStatistics
        {
            MeshName = mesh.Name,
            VertexCount = mesh.Vertices.Length,
            TriangleCount = mesh.TriangleIndices.Length / 3,
            GridNx = nx,
            GridNy = ny,
            GridNz = nz,
            CellSize = hf,
            TotalInteriorTets = nTets,
            FillRatioPercent = (double)nTets / (totalCells * 6) * 100,
            ElementVolume = tetVol,
            TotalVolume = totalVol,
            InnerFaces = nInner,
            OuterFaces = nOuter,
            NeitherFaces = nNeither,
            IsDipoleMode = dipoleMode,
            RawDepthMin = rawDepthMin,
            RawDepthMax = rawDepthMax,
            VolumeMedianTStar = volMedianT,
            ChargeSignMin = csMin,
            ChargeSignMax = csMax,
            ChargeSignMean = csMean,
            NegativeTets = finalNeg,
            PositiveTets = finalPos,
            NearZeroTets = finalZero,
            PositiveChargeC = posQ,
            NegativeChargeC = negQ,
            ImbalancePercent = imbalPct,
            RhoSI = rhoSI,
            FieldGridDensity = gn,
            FieldMagMin = eMagMin,
            FieldMagMax = eMagMax,
            FieldMagMean = eMagSum / gTotal,
            ComputeTimeSeconds = computeTime,
            SofteningEpsilon = epsilon,
            SofteningAlpha = softeningAlpha,
            BackendName = backend.Name,
            MaterialBreakdown = matBreakdown,
            ChargeSignHistogram = histogram,
        };

        var field = new ElectricFieldData
        {
            GridPoints = gridSpec.GenerateGridPoints(),
            FieldVectors = fldBuf.PackFieldVectors(),
            GridBounds = gridSpec.ToBoundsArray(),
            GridShape = gridSpec.ToShapeArray(),
            ChargeDensity = rhoSI,
            Description = $"Tet: {nTets:N0} tets, {chargeDensityPerCm3:E1}/cm³, " +
                          $"{(dipoleMode ? "dipole" : "uniform")}, {gn}³ grid"
        };

        return (field, stats);
    }

    // ═════════════════════════════════════════════════════════
    //  Face classification
    // ═════════════════════════════════════════════════════════

    private static (Vector3[] innerCentroids, Vector3[] outerCentroids,
                     int nInner, int nOuter, int nNeither,
                     IReadOnlyList<MaterialClassification> materialBreakdown)
        ClassifyFaces(MeshData mesh)
    {
        if (mesh.FaceMaterials == null)
            return (Array.Empty<Vector3>(), Array.Empty<Vector3>(), 0, 0, 0,
                    Array.Empty<MaterialClassification>());

        int nTri = mesh.FaceMaterials.Length;
        var innerList = new List<Vector3>();
        var outerList = new List<Vector3>();
        int neither = 0;
        var matCounts = new Dictionary<string, (int i, int o, int n)>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < nTri; i++)
        {
            string mat = (mesh.FaceMaterials[i] ?? "").Trim();
            string low = mat.ToLowerInvariant();
            var fc = FaceCentroid(mesh, i);
            if (!matCounts.ContainsKey(mat)) matCounts[mat] = (0, 0, 0);

            if (InnerKeys.Any(k => low.Contains(k)))
            {
                innerList.Add(fc);
                var c = matCounts[mat]; matCounts[mat] = (c.i + 1, c.o, c.n);
            }
            else if (OuterKeys.Any(k => low.Contains(k)))
            {
                outerList.Add(fc);
                var c = matCounts[mat]; matCounts[mat] = (c.i, c.o + 1, c.n);
            }
            else
            {
                neither++;
                var c = matCounts[mat]; matCounts[mat] = (c.i, c.o, c.n + 1);
            }
        }

        foreach (var (mat, (inn, outr, oth)) in matCounts.OrderBy(kv => kv.Key))
            Console.WriteLine($"    '{mat}' → inner:{inn}  outer:{outr}  neither:{oth}");

        var breakdown = matCounts
            .OrderBy(kv => kv.Key)
            .Select(kv => new MaterialClassification
            {
                MaterialName = kv.Key,
                InnerCount = kv.Value.i,
                OuterCount = kv.Value.o,
                NeitherCount = kv.Value.n,
            })
            .ToList();

        return (innerList.ToArray(), outerList.ToArray(),
                innerList.Count, outerList.Count, neither,
                breakdown);
    }

    private static Vector3 FaceCentroid(MeshData mesh, int triIdx)
    {
        var a = mesh.Vertices[mesh.TriangleIndices[3 * triIdx]];
        var b = mesh.Vertices[mesh.TriangleIndices[3 * triIdx + 1]];
        var c = mesh.Vertices[mesh.TriangleIndices[3 * triIdx + 2]];
        return new Vector3(
            (float)(a.X + b.X + c.X) / 3f,
            (float)(a.Y + b.Y + c.Y) / 3f,
            (float)(a.Z + b.Z + c.Z) / 3f);
    }

    // ═════════════════════════════════════════════════════════
    //  Depth field  (raw convention: inner=−1, outer=+1)
    // ═════════════════════════════════════════════════════════

    private static (float[] chargeSign, DepthFieldStats stats) ComputeDepthField(
        List<Vector3> tetCentroids, double tetVol,
        Vector3[] innerC, Vector3[] outerC)
    {
        int n = tetCentroids.Count;
        var rawDepth = new float[n];

        var innerTree = new KdTree3D(Vec3ToFloat2D(innerC));
        var outerTree = new KdTree3D(Vec3ToFloat2D(outerC));

        Parallel.For(0, n, i =>
        {
            var c = tetCentroids[i];
            float dInner = Vector3.Distance(c, innerC[innerTree.FindNearest(c)]);
            float dOuter = Vector3.Distance(c, outerC[outerTree.FindNearest(c)]);
            float total = dInner + dOuter;
            rawDepth[i] = total < 1e-15f ? 0.5f : dInner / total;
        });

        // Volume-median for centering
        var sorted = rawDepth.OrderBy(d => d).ToArray();
        double cumVol = 0, halfVol = n * tetVol * 0.5;
        float tStar = 0.5f;
        foreach (float d in sorted)
        {
            cumVol += tetVol;
            if (cumVol >= halfVol) { tStar = d; break; }
        }
        tStar = Math.Clamp(tStar, 0.01f, 0.99f);

        // Smooth sigmoid remap
        const float steepness = 6.0f;

        var chargeSign = new float[n];
        for (int i = 0; i < n; i++)
        {
            float arg = steepness * (rawDepth[i] - tStar);
            float sigmoid = 1.0f / (1.0f + MathF.Exp(-arg));
            chargeSign[i] = 2.0f * sigmoid - 1.0f;
        }

        // Collect stats
        int nNeg = 0, nPos = 0, nZero = 0;
        float minSign = float.MaxValue, maxSign = float.MinValue;
        double signSum = 0;
        float rawMin = float.MaxValue, rawMax = float.MinValue;
        for (int i = 0; i < n; i++)
        {
            float s = chargeSign[i];
            if (s < -0.001f) nNeg++; else if (s > 0.001f) nPos++; else nZero++;
            if (s < minSign) minSign = s;
            if (s > maxSign) maxSign = s;
            signSum += s;
            if (rawDepth[i] < rawMin) rawMin = rawDepth[i];
            if (rawDepth[i] > rawMax) rawMax = rawDepth[i];
        }

        Console.WriteLine();
        Console.WriteLine("── Depth field (sigmoid, raw: inner=−, outer=+) ──");
        Console.WriteLine($"  Raw depth       : [{rawMin:F4}, {rawMax:F4}]");
        Console.WriteLine($"  Volume median t*: {tStar:F6}");
        Console.WriteLine($"  Sigmoid steep.  : {steepness:F1}");
        Console.WriteLine($"  Charge sign     : [{minSign:F4}, {maxSign:F4}], mean={signSum / n:F4}");
        Console.WriteLine($"  Negative tets   : {nNeg:N0}  ({100.0 * nNeg / n:F1}%)");
        Console.WriteLine($"  Near-zero tets  : {nZero:N0}  ({100.0 * nZero / n:F1}%)");
        Console.WriteLine($"  Positive tets   : {nPos:N0}  ({100.0 * nPos / n:F1}%)");

        var stats = new DepthFieldStats(rawMin, rawMax, tStar,
            minSign, maxSign, signSum / n,
            nNeg, nPos, nZero);

        return (chargeSign, stats);
    }

    private static float[] ComputeDepthField_tStar(
        List<Vector3> tetCentroids, double tetVol,
        Vector3[] innerC, Vector3[] outerC)
    {
        int n = tetCentroids.Count;
        var rawDepth = new float[n];

        var innerTree = new KdTree3D(Vec3ToFloat2D(innerC));
        var outerTree = new KdTree3D(Vec3ToFloat2D(outerC));

        Parallel.For(0, n, i =>
        {
            var c = tetCentroids[i];
            float dInner = Vector3.Distance(c, innerC[innerTree.FindNearest(c)]);
            float dOuter = Vector3.Distance(c, outerC[outerTree.FindNearest(c)]);
            float total = dInner + dOuter;
            rawDepth[i] = total < 1e-15f ? 0.5f : dInner / total;
        });

        // Volume-balanced remapping
        var sorted = rawDepth.OrderBy(d => d).ToArray();
        double cumVol = 0, halfVol = n * tetVol * 0.5;
        float tStar = 0.5f;
        foreach (float d in sorted) { cumVol += tetVol; if (cumVol >= halfVol) { tStar = d; break; } }
        tStar = Math.Clamp(tStar, 1e-6f, 1f - 1e-6f);

        var chargeSign = new float[n];
        for (int i = 0; i < n; i++)
        {
            float nd = rawDepth[i] <= tStar
                ? 0.5f * rawDepth[i] / tStar
                : 0.5f + 0.5f * (rawDepth[i] - tStar) / (1f - tStar);
            chargeSign[i] = 2f * nd - 1f;
        }

        Console.WriteLine();
        Console.WriteLine("── Depth field (raw: inner=−, outer=+) ──");
        Console.WriteLine($"  Raw depth       : [{rawDepth.Min():F4}, {rawDepth.Max():F4}]");
        Console.WriteLine($"  Volume median t*: {tStar:F6}");
        Console.WriteLine($"  Negative tets   : {chargeSign.Count(s => s < 0):N0}");
        Console.WriteLine($"  Positive tets   : {chargeSign.Count(s => s > 0):N0}");
        return chargeSign;
    }

    // ═════════════════════════════════════════════════════════
    //  Charge balancing
    // ═════════════════════════════════════════════════════════

    private static void BalanceCharge(float[] chargeSign, double tetVol)
    {
        double posSum = 0, negSum = 0;
        for (int i = 0; i < chargeSign.Length; i++)
        {
            double c = tetVol * chargeSign[i];
            if (c > 0) posSum += c; else negSum += c;
        }
        double net = posSum + negSum;
        if (Math.Abs(net) < 1e-30 * Math.Max(posSum, Math.Abs(negSum)))
        { Console.WriteLine("  Balance         : already balanced."); return; }

        double alpha; string side;
        if (net > 0) { alpha = Math.Abs(negSum) / posSum; side = "positive"; }
        else { alpha = posSum / Math.Abs(negSum); side = "negative"; }

        for (int i = 0; i < chargeSign.Length; i++)
            if ((side == "positive" && chargeSign[i] > 0) ||
                (side == "negative" && chargeSign[i] < 0))
                chargeSign[i] *= (float)alpha;

        Console.WriteLine();
        Console.WriteLine("── Charge balancing ──");
        Console.WriteLine($"  Scaled {side} side by α={alpha:F10}");

        double vP = 0, vN = 0;
        for (int i = 0; i < chargeSign.Length; i++)
        { double c = tetVol * chargeSign[i]; if (c > 0) vP += c; else vN += c; }
        double res = (vP + Math.Abs(vN)) > 0 ? Math.Abs(vP + vN) / (vP + Math.Abs(vN)) : 0;
        Console.WriteLine($"  Residual        : {res:E2}");
    }

    // ═════════════════════════════════════════════════════════
    //  Charge sign histogram builder
    // ═════════════════════════════════════════════════════════

    private static IReadOnlyList<HistogramBin> BuildChargeSignHistogram(
        float[] chargeSign, int numBins)
    {
        var bins = new int[numBins];
        float binWidth = 2.0f / numBins; // range [−1, +1]

        foreach (float s in chargeSign)
        {
            int bi = (int)((s + 1.0f) / binWidth);
            bi = Math.Clamp(bi, 0, numBins - 1);
            bins[bi]++;
        }

        var result = new HistogramBin[numBins];
        for (int i = 0; i < numBins; i++)
        {
            result[i] = new HistogramBin
            {
                RangeMin = -1.0f + i * binWidth,
                RangeMax = -1.0f + (i + 1) * binWidth,
                Count = bins[i],
            };
        }
        return result;
    }

    // ═════════════════════════════════════════════════════════
    //  Geometry helpers
    // ═════════════════════════════════════════════════════════

    private static float[,] Vec3ToFloat2D(Vector3[] pts)
    {
        var a = new float[pts.Length, 3];
        for (int i = 0; i < pts.Length; i++)
        { a[i, 0] = pts[i].X; a[i, 1] = pts[i].Y; a[i, 2] = pts[i].Z; }
        return a;
    }

    private static (Vector3 v0, Vector3 v1, Vector3 v2)[] BuildTriangles(MeshData mesh)
    {
        int n = mesh.TriangleIndices.Length / 3;
        var t = new (Vector3, Vector3, Vector3)[n];
        for (int i = 0; i < n; i++)
        {
            var p0 = mesh.Vertices[mesh.TriangleIndices[i * 3]];
            var p1 = mesh.Vertices[mesh.TriangleIndices[i * 3 + 1]];
            var p2 = mesh.Vertices[mesh.TriangleIndices[i * 3 + 2]];
            t[i] = (new Vector3((float)p0.X, (float)p0.Y, (float)p0.Z),
                    new Vector3((float)p1.X, (float)p1.Y, (float)p1.Z),
                    new Vector3((float)p2.X, (float)p2.Y, (float)p2.Z));
        }
        return t;
    }

    private static bool RayZ(Vector3 o, Vector3 v0, Vector3 v1, Vector3 v2)
    {
        var e1 = v1 - v0; var e2 = v2 - v0;
        float hx = -e2.Y, hy = e2.X;
        float a = e1.X * hx + e1.Y * hy;
        if (a > -1e-8f && a < 1e-8f) return false;
        float f = 1f / a; var s = o - v0;
        float u = f * (s.X * hx + s.Y * hy);
        if (u < 0f || u > 1f) return false;
        float qz = s.X * e1.Y - s.Y * e1.X;
        float v = f * qz;
        if (v < 0f || u + v > 1f) return false;
        float qx = s.Y * e1.Z - s.Z * e1.Y;
        float qy = s.Z * e1.X - s.X * e1.Z;
        return f * (e2.X * qx + e2.Y * qy + e2.Z * qz) > 1e-8f;
    }

    private sealed class TriBins
    {
        private readonly List<int>?[,] _bins;
        private readonly (Vector3 v0, Vector3 v1, Vector3 v2)[] _tris;
        private readonly float _bs, _ox, _oy;
        private readonly int _nx, _ny;

        public TriBins((Vector3 v0, Vector3 v1, Vector3 v2)[] tris,
            float binSize, Vector3 bMin, Vector3 bMax)
        {
            _tris = tris; _bs = binSize; _ox = bMin.X; _oy = bMin.Y;
            _nx = Math.Max(1, (int)MathF.Ceiling((bMax.X - bMin.X) / binSize)) + 1;
            _ny = Math.Max(1, (int)MathF.Ceiling((bMax.Y - bMin.Y) / binSize)) + 1;
            _bins = new List<int>?[_nx, _ny];
            for (int i = 0; i < tris.Length; i++)
            {
                var (v0, v1, v2) = tris[i];
                int x0 = Bx(MathF.Min(v0.X, MathF.Min(v1.X, v2.X)));
                int x1 = Bx(MathF.Max(v0.X, MathF.Max(v1.X, v2.X)));
                int y0 = By(MathF.Min(v0.Y, MathF.Min(v1.Y, v2.Y)));
                int y1 = By(MathF.Max(v0.Y, MathF.Max(v1.Y, v2.Y)));
                for (int bx = x0; bx <= x1; bx++)
                    for (int by = y0; by <= y1; by++)
                        (_bins[bx, by] ??= new List<int>()).Add(i);
            }
        }

        private int Bx(float x) => Math.Clamp((int)((x - _ox) / _bs), 0, _nx - 1);
        private int By(float y) => Math.Clamp((int)((y - _oy) / _bs), 0, _ny - 1);

        public bool IsInside(Vector3 p)
        {
            var list = _bins[Bx(p.X), By(p.Y)];
            if (list == null) return false;
            int crossings = 0;
            foreach (int ti in list)
                if (RayZ(p, _tris[ti].v0, _tris[ti].v1, _tris[ti].v2))
                    crossings++;
            return (crossings & 1) == 1;
        }
    }
    private readonly struct DepthFieldStats
    {
        public readonly float RawMin, RawMax, TStar;
        public readonly float SignMin, SignMax;
        public readonly double SignMean;
        public readonly int NegCount, PosCount, ZeroCount;

        public DepthFieldStats(float rawMin, float rawMax, float tStar,
            float signMin, float signMax, double signMean,
            int negCount, int posCount, int zeroCount)
        {
            RawMin = rawMin; RawMax = rawMax; TStar = tStar;
            SignMin = signMin; SignMax = signMax; SignMean = signMean;
            NegCount = negCount; PosCount = posCount; ZeroCount = zeroCount;
        }
    }

}