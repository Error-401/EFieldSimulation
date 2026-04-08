using System.IO;
using System.Numerics;
using System.Text.Json;

namespace EFieldSimulation.Models;

/// <summary>
/// Loads parametric shape definitions from a JSON file and tessellates them.
/// A shape = one or more (u,v) surface patches whose x/y/z coordinates are
/// math expressions over u, v, and the properties of ArbitraryShapeParams.
/// </summary>
public static class ShapeLibrary
{
    private static Dictionary<string, ShapeDef> _shapes = new();
    private static readonly object _lock = new();

    public static bool IsLoaded { get; private set; }

    public static IReadOnlyList<string> ShapeNames
    {
        get { lock (_lock) return _shapes.Keys.ToList(); }
    }

    /// <summary>Load + compile shape definitions. Call once at startup.</summary>
    public static void Load(string jsonPath)
    {
        lock (_lock)
        {
            var json = File.ReadAllText(jsonPath);
            var doc = JsonSerializer.Deserialize<LibraryFile>(json, _jsonOpts)
                ?? throw new InvalidDataException("Shape library file is empty.");

            var dict = new Dictionary<string, ShapeDef>(StringComparer.Ordinal);
            foreach (var s in doc.Shapes)
            {
                if (string.IsNullOrWhiteSpace(s.Name))
                    throw new InvalidDataException("Shape with empty name.");
                if (s.Patches.Count == 0)
                    throw new InvalidDataException($"Shape '{s.Name}' has no patches.");
                dict[s.Name] = new ShapeDef
                {
                    Name = s.Name,
                    Volume = Compile(s.Volume),
                    Patches = s.Patches.Select(CompilePatch).ToArray()
                };
            }
            _shapes = dict;
            IsLoaded = true;
        }
    }

    public static ShapeDef? Get(string name)
    {
        lock (_lock) return _shapes.TryGetValue(name, out var s) ? s : null;
    }

    /// <summary>Evaluate the shape's volume expression. Returns 1.0 if undefined.</summary>
    public static double EvaluateVolume(ArbitraryShapeParams p)
    {
        var def = Get(p.Type);
        if (def?.Volume == null) return 1.0;
        var ctx = new ParamContext(p);
        return def.Volume.Eval(ctx.Resolve);
    }

    /// <summary>
    /// Tessellate into vertices + triangle indices in shape-local space
    /// (includes CenterX/Y/Z offset, excludes RotationX/Y/Z and entry transform).
    /// Normals are computed per-vertex from triangle adjacency.
    /// </summary>
    public static (Vector3[] verts, int[] indices, Vector3[] normals)
        Tessellate(ArbitraryShapeParams p)
    {
        var def = Get(p.Type)
            ?? throw new InvalidOperationException(
                $"Shape '{p.Type}' not found in library. " +
                $"Loaded: [{string.Join(", ", ShapeNames)}]");

        var ctx = new ParamContext(p);
        var resolve = (Func<string, double>)ctx.Resolve;

        var verts = new List<Vector3>();
        var indices = new List<int>();

        foreach (var patch in def.Patches)
            TessellatePatch(patch, ctx, resolve, verts, indices);

        var normals = ComputeVertexNormals(verts, indices);
        return (verts.ToArray(), indices.ToArray(), normals);
    }

    // ── Patch → quad grid ───────────────────────────────────
    private static void TessellatePatch(
        PatchDef patch, ParamContext ctx, Func<string, double> resolve,
        List<Vector3> verts, List<int> indices)
    {
        double uMin = patch.UMin.Eval(resolve);
        double uMax = patch.UMax.Eval(resolve);
        double vMin = patch.VMin.Eval(resolve);
        double vMax = patch.VMax.Eval(resolve);

        int nu = Math.Max(1, (int)Math.Round(patch.USeg.Eval(resolve)));
        int nv = Math.Max(1, (int)Math.Round(patch.VSeg.Eval(resolve)));

        int baseIdx = verts.Count;

        for (int i = 0; i <= nu; i++)
        {
            ctx.U = uMin + (uMax - uMin) * i / nu;
            for (int j = 0; j <= nv; j++)
            {
                ctx.V = vMin + (vMax - vMin) * j / nv;
                verts.Add(new Vector3(
                    (float)patch.X.Eval(resolve),
                    (float)patch.Y.Eval(resolve),
                    (float)patch.Z.Eval(resolve)));
            }
        }

        int stride = nv + 1;
        for (int i = 0; i < nu; i++)
            for (int j = 0; j < nv; j++)
            {
                int a = baseIdx + i * stride + j;       // (i,   j  )
                int b = baseIdx + (i + 1) * stride + j;       // (i+1, j  )
                int c = baseIdx + (i + 1) * stride + j + 1;   // (i+1, j+1)
                int d = baseIdx + i * stride + j + 1;   // (i,   j+1)

                // Default winding: normal ≈ ∂r/∂v × ∂r/∂u (outward for the
                // radial surfaces defined in shapes.json).
                if (patch.Flip)
                {
                    indices.Add(a); indices.Add(b); indices.Add(d);
                    indices.Add(b); indices.Add(c); indices.Add(d);
                }
                else
                {
                    indices.Add(a); indices.Add(d); indices.Add(b);
                    indices.Add(d); indices.Add(c); indices.Add(b);
                }
            }
    }

    private static Vector3[] ComputeVertexNormals(List<Vector3> verts, List<int> idx)
    {
        var n = new Vector3[verts.Count];
        for (int t = 0; t + 2 < idx.Count; t += 3)
        {
            int a = idx[t], b = idx[t + 1], c = idx[t + 2];
            var fn = Vector3.Cross(verts[b] - verts[a], verts[c] - verts[a]); // area-weighted
            n[a] += fn; n[b] += fn; n[c] += fn;
        }
        for (int i = 0; i < n.Length; i++)
        {
            float l = n[i].Length();
            n[i] = l > 1e-20f ? n[i] / l : Vector3.UnitY;
        }
        return n;
    }

    // ── JSON compilation ────────────────────────────────────
    private static MathExpr? Compile(string? src) =>
        string.IsNullOrWhiteSpace(src) ? null : MathExpr.Compile(src);

    private static PatchDef CompilePatch(PatchJson p)
    {
        if (p.UDomain.Length != 2 || p.VDomain.Length != 2)
            throw new InvalidDataException("Patch domain must be [min, max].");
        return new PatchDef
        {
            UMin = MathExpr.Compile(p.UDomain[0]),
            UMax = MathExpr.Compile(p.UDomain[1]),
            VMin = MathExpr.Compile(p.VDomain[0]),
            VMax = MathExpr.Compile(p.VDomain[1]),
            USeg = MathExpr.Compile(p.USegments),
            VSeg = MathExpr.Compile(p.VSegments),
            X = MathExpr.Compile(p.X),
            Y = MathExpr.Compile(p.Y),
            Z = MathExpr.Compile(p.Z),
            Flip = p.FlipWinding
        };
    }

    // ── Variable resolver: u, v, constants, ArbitraryShapeParams props ──
    private sealed class ParamContext
    {
        private readonly ArbitraryShapeParams _p;
        public double U, V;
        public ParamContext(ArbitraryShapeParams p) => _p = p;

        public double Resolve(string name) => name switch
        {
            "u" => U,
            "v" => V,
            "pi" => Math.PI,
            "e" => Math.E,

            "CenterX" => _p.CenterX,
            "CenterY" => _p.CenterY,
            "CenterZ" => _p.CenterZ,
            "Radius" => _p.Radius,
            "Height" => _p.Height,
            "MajorRadius" => _p.MajorRadius,
            "MinorRadius" => _p.MinorRadius,
            "AngleStartDeg" => _p.AngleStartDeg,
            "AngleSpanDeg" => _p.AngleSpanDeg,
            "SphereRadius" => _p.SphereRadius,
            "ConeTopRadius" => _p.ConeTopRadius,
            "ConeBottomRadius" => _p.ConeBottomRadius,
            "ConeHeight" => _p.ConeHeight,
            "RadialSegments" => _p.RadialSegments,
            "TubularSegments" => _p.TubularSegments,
            "HelixTurns" => _p.HelixTurns,
            "HelixPitch" => _p.HelixPitch,
            "WireRadius" => _p.Radius,

            // ── HelicalToroid Frenet frame intermediates ─────────────────
            // Centreline: C(u) = ((R + r·cos(n·u))·cos(u),  r·sin(n·u),  (R + r·cos(n·u))·sin(n·u))
            // where R = MajorRadius, r = MinorRadius, n = HelixTurns

            "_ht_R" => _p.MajorRadius,
            "_ht_r" => _p.MinorRadius,
            "_ht_n" => _p.HelixTurns,

            // Tangent dC/du (unnormalised)
            "_Tx" => -((_p.MajorRadius + _p.MinorRadius * Math.Cos(_p.HelixTurns * U)) * Math.Sin(U))
                     - _p.MinorRadius * _p.HelixTurns * Math.Sin(_p.HelixTurns * U) * Math.Cos(U),
            "_Ty" => _p.MinorRadius * _p.HelixTurns * Math.Cos(_p.HelixTurns * U),
            "_Tz" => (_p.MajorRadius + _p.MinorRadius * Math.Cos(_p.HelixTurns * U)) * Math.Cos(U)
                     - _p.MinorRadius * _p.HelixTurns * Math.Sin(_p.HelixTurns * U) * Math.Sin(U),

            // |T| and safe normal via T × (0,1,0) = (Tz, 0, -Tx)
            "_Tl" => Math.Sqrt(
                         Math.Pow(-((_p.MajorRadius + _p.MinorRadius * Math.Cos(_p.HelixTurns * U)) * Math.Sin(U))
                                  - _p.MinorRadius * _p.HelixTurns * Math.Sin(_p.HelixTurns * U) * Math.Cos(U), 2) +
                         Math.Pow(_p.MinorRadius * _p.HelixTurns * Math.Cos(_p.HelixTurns * U), 2) +
                         Math.Pow((_p.MajorRadius + _p.MinorRadius * Math.Cos(_p.HelixTurns * U)) * Math.Cos(U)
                                  - _p.MinorRadius * _p.HelixTurns * Math.Sin(_p.HelixTurns * U) * Math.Sin(U), 2)),

            // N = normalise(Tz, 0, -Tx),  |N_unnorm| = sqrt(Tx^2 + Tz^2)
            "_Tn" => Math.Sqrt(
                         Math.Pow(-((_p.MajorRadius + _p.MinorRadius * Math.Cos(_p.HelixTurns * U)) * Math.Sin(U))
                                  - _p.MinorRadius * _p.HelixTurns * Math.Sin(_p.HelixTurns * U) * Math.Cos(U), 2) +
                         Math.Pow((_p.MajorRadius + _p.MinorRadius * Math.Cos(_p.HelixTurns * U)) * Math.Cos(U)
                                  - _p.MinorRadius * _p.HelixTurns * Math.Sin(_p.HelixTurns * U) * Math.Sin(U), 2)),

            _ => throw new FormatException($"Unknown variable '{name}' in shape expression.")
        };
    }

    // ── JSON DTOs ───────────────────────────────────────────
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed class LibraryFile { public List<ShapeJson> Shapes { get; set; } = new(); }

    private sealed class ShapeJson
    {
        public string Name { get; set; } = "";
        public List<PatchJson> Patches { get; set; } = new();
        public string? Volume { get; set; }
    }

    private sealed class PatchJson
    {
        public string[] UDomain { get; set; } = { "0", "1" };
        public string[] VDomain { get; set; } = { "0", "1" };
        public string USegments { get; set; } = "8";
        public string VSegments { get; set; } = "8";
        public string X { get; set; } = "0";
        public string Y { get; set; } = "0";
        public string Z { get; set; } = "0";
        public bool FlipWinding { get; set; }
    }
}

// ═════════════════════════════════════════════════════════════
//  Compiled definitions
// ═════════════════════════════════════════════════════════════

public sealed class ShapeDef
{
    public string Name { get; init; } = "";
    public PatchDef[] Patches { get; init; } = Array.Empty<PatchDef>();
    public MathExpr? Volume { get; init; }
}

public sealed class PatchDef
{
    public MathExpr UMin { get; init; } = null!;
    public MathExpr UMax { get; init; } = null!;
    public MathExpr VMin { get; init; } = null!;
    public MathExpr VMax { get; init; } = null!;
    public MathExpr USeg { get; init; } = null!;
    public MathExpr VSeg { get; init; } = null!;
    public MathExpr X { get; init; } = null!;
    public MathExpr Y { get; init; } = null!;
    public MathExpr Z { get; init; } = null!;
    public bool Flip { get; init; }
}

/// <summary>
///   Grammar:
///    expr   := term   (('+'|'-') term)*
///    term   := factor (('*'|'/'|'%') factor)*
///    factor := ('+'|'-') factor | power
///    power  := primary ('^' factor)?          // right-assoc; 2^-3 OK
///    primary:= NUMBER | IDENT '(' args ')' | IDENT | '(' expr ')'
/// </summary>
public sealed class MathExpr
{
    private readonly Node _root;
    private MathExpr(Node r) => _root = r;

    public double Eval(Func<string, double> vars) => _root.Eval(vars);

    public static MathExpr Compile(string src)
    {
        var p = new Parser(src ?? throw new ArgumentNullException(nameof(src)));
        var n = p.ParseExpr();
        if (!p.AtEnd) throw new FormatException($"Unexpected trailing input in '{src}'.");
        return new MathExpr(n);
    }

    private abstract class Node { public abstract double Eval(Func<string, double> r); }

    private sealed class Num : Node
    {
        public readonly double V; public Num(double v) => V = v;
        public override double Eval(Func<string, double> _) => V;
    }

    private sealed class Var : Node
    {
        public readonly string N; public Var(string n) => N = n;
        public override double Eval(Func<string, double> r) => r(N);
    }

    private sealed class Neg : Node
    {
        public readonly Node A; public Neg(Node a) => A = a;
        public override double Eval(Func<string, double> r) => -A.Eval(r);
    }

    private sealed class Bin : Node
    {
        public readonly char Op; public readonly Node L, R;
        public Bin(char o, Node l, Node r) { Op = o; L = l; R = r; }
        public override double Eval(Func<string, double> rv)
        {
            double a = L.Eval(rv), b = R.Eval(rv);
            return Op switch
            {
                '+' => a + b,
                '-' => a - b,
                '*' => a * b,
                '/' => a / b,
                '%' => a % b,
                '^' => Math.Pow(a, b),
                _ => throw new InvalidOperationException($"Bad op {Op}")
            };
        }
    }

    private sealed class Call : Node
    {
        public readonly string Fn; public readonly Node[] Args;
        public Call(string f, Node[] a) { Fn = f; Args = a; }
        public override double Eval(Func<string, double> r)
        {
            double A(int i) => Args[i].Eval(r);
            return Fn switch
            {
                "sin" => Math.Sin(A(0)),
                "cos" => Math.Cos(A(0)),
                "tan" => Math.Tan(A(0)),
                "asin" => Math.Asin(A(0)),
                "acos" => Math.Acos(A(0)),
                "atan" => Math.Atan(A(0)),
                "atan2" => Math.Atan2(A(0), A(1)),
                "sqrt" => Math.Sqrt(A(0)),
                "abs" => Math.Abs(A(0)),
                "floor" => Math.Floor(A(0)),
                "ceil" => Math.Ceiling(A(0)),
                "round" => Math.Round(A(0)),
                "sign" => Math.Sign(A(0)),
                "exp" => Math.Exp(A(0)),
                "log" => Math.Log(A(0)),
                "min" => Math.Min(A(0), A(1)),
                "max" => Math.Max(A(0), A(1)),
                "pow" => Math.Pow(A(0), A(1)),
                "rad" => A(0) * Math.PI / 180.0,
                "deg" => A(0) * 180.0 / Math.PI,
                _ => throw new FormatException($"Unknown function '{Fn}'.")
            };
        }
    }

    private sealed class Parser
    {
        private readonly string _s; private int _i;
        public Parser(string s) { _s = s; _i = 0; }
        public bool AtEnd { get { Skip(); return _i >= _s.Length; } }

        private void Skip() { while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++; }
        private char Peek() { Skip(); return _i < _s.Length ? _s[_i] : '\0'; }
        private bool Eat(char c) { if (Peek() == c) { _i++; return true; } return false; }

        public Node ParseExpr()
        {
            Node l = ParseTerm();
            for (; ; )
            {
                char c = Peek();
                if (c == '+' || c == '-') { _i++; l = new Bin(c, l, ParseTerm()); }
                else return l;
            }
        }
        private Node ParseTerm()
        {
            Node l = ParseFactor();
            for (; ; )
            {
                char c = Peek();
                if (c == '*' || c == '/' || c == '%') { _i++; l = new Bin(c, l, ParseFactor()); }
                else return l;
            }
        }
        private Node ParseFactor()
        {
            if (Peek() == '-') { _i++; return new Neg(ParseFactor()); }
            if (Peek() == '+') { _i++; return ParseFactor(); }
            return ParsePower();
        }
        private Node ParsePower()
        {
            Node l = ParsePrimary();
            if (Peek() == '^') { _i++; return new Bin('^', l, ParseFactor()); } // right-assoc
            return l;
        }
        private Node ParsePrimary()
        {
            Skip();
            if (Eat('('))
            {
                var e = ParseExpr();
                if (!Eat(')')) throw new FormatException("Expected ')'.");
                return e;
            }
            if (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.'))
            {
                int s = _i;
                while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.'
                    || _s[_i] == 'e' || _s[_i] == 'E'
                    || ((_s[_i] == '+' || _s[_i] == '-') && _i > s
                        && (_s[_i - 1] == 'e' || _s[_i - 1] == 'E'))))
                    _i++;
                return new Num(double.Parse(_s.AsSpan(s, _i - s),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture));
            }
            if (_i < _s.Length && (char.IsLetter(_s[_i]) || _s[_i] == '_'))
            {
                int s = _i;
                while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_')) _i++;
                string id = _s.Substring(s, _i - s);
                if (Peek() == '(')
                {
                    _i++;
                    var args = new List<Node>();
                    if (Peek() != ')')
                    { args.Add(ParseExpr()); while (Eat(',')) args.Add(ParseExpr()); }
                    if (!Eat(')')) throw new FormatException($"Expected ')' after '{id}(…'.");
                    return new Call(id, args.ToArray());
                }
                return new Var(id);
            }
            throw new FormatException(
                $"Unexpected character '{Peek()}' at position {_i} in '{_s}'.");
        }
    }
}