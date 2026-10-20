using RsDocx.Core.Brep;
using RsDocx.Core.Stl;
using RsDocx.Core.Xt;

namespace PushPullMe.Core;

/// <summary>
/// SketchUp's Follow Me along a path: a flat profile swept along a path (the outer edge of a flat face, or a run of loose
/// lines and arcs, open or closed), the profile standing square to the path where it touches it (at a corner of it, or
/// round an end: a pipe). With straight edges only, every straight piece of the path sweeps a prism of the profile, cut
/// at the corners by the plane halfway between the two pieces (a mitre), so the prisms fit; an open end is cut square.
/// With an arc in the profile or the path, every piece is swept on its own (a straight one pulled, an arc turned round
/// its centre), which needs the path to run on smoothly at its corners. A whole circle as the path: one whole turn.
/// </summary>
public static class Sweep
{
    private const double Tol = 1e-7;

    /// <summary>A piece of a path: from A to B, straight (<see cref="Arc"/> null) or along a circle counter-clockwise
    /// round its normal (a whole circle: A and B the same point).</summary>
    public sealed record PathPiece(Vec3 A, Vec3 B, BrepCircle? Arc = null)
    {
        public bool Whole => Arc != null && (A - B).Length < Tol;

        /// <summary>The way it runs at a point of it.</summary>
        private Vec3 TangentAt(Vec3 q) => Arc is { } c ? c.Normal.Cross(q - c.Centre).Normalized() : (B - A).Normalized();
        public Vec3 StartTangent => TangentAt(A);
        public Vec3 EndTangent => TangentAt(B);

        /// <summary>How far round it goes (radians, up to a whole turn); 0 for a straight piece.</summary>
        public double Angle
        {
            get
            {
                if (Arc is not { } c) return 0;
                if (Whole) return 2 * Math.PI;
                var a = A - c.Centre; var b = B - c.Centre;
                double t = Math.Atan2(a.Cross(b).Dot(c.Normal.Normalized()), a.Dot(b));
                return t <= 0 ? t + 2 * Math.PI : t;
            }
        }

        public double Length => Arc is { } c ? c.Radius * Angle : (B - A).Length;

        /// <summary>The same way back.</summary>
        public PathPiece Reversed() => new(B, A, Arc is { } c ? c with { Normal = c.Normal * -1, XAxis = (B - c.Centre).Normalized() } : null);
    }

    /// <summary>A path: its pieces end to end, whether it closes, and the normal of the plane it lies in (null: a single
    /// straight line, in no plane of its own).</summary>
    public sealed record SweepPath(IReadOnlyList<PathPiece> Pieces, bool Closed, Vec3? Normal);

    /// <summary>The outer edge of a flat face as a path, run counter-clockwise round the face's normal; straight pieces in
    /// line with each other one piece.</summary>
    public static SweepPath OfFace(BrepBody body, int face)
    {
        var f = body.Faces[face];
        if (f.Surface is not BrepPlane) throw new PushPullException(Lang.T("Het pad moet de rand van een plat vlak zijn.", "The path must be the edge of a flat face."));
        var pieces = new List<PathPiece>();
        foreach (var c in f.Loops[0])
        {
            var e = body.Edges[c.Edge];
            switch (e.Curve)
            {
                case BrepLine:
                    pieces.Add(new PathPiece(body.Vertices[c.Forward ? e.Start : e.End], body.Vertices[c.Forward ? e.End : e.Start]));
                    break;
                case BrepCircle ci when e.Start < 0:
                {
                    var p = ci.Centre + ci.XAxis.Normalized() * ci.Radius;
                    pieces.Add(new PathPiece(p, p, c.Forward ? ci : ci with { Normal = ci.Normal * -1 }));
                    break;
                }
                case BrepCircle ci:
                {
                    var piece = new PathPiece(body.Vertices[e.Start], body.Vertices[e.End], ci with { XAxis = (body.Vertices[e.Start] - ci.Centre).Normalized() });
                    pieces.Add(c.Forward ? piece : piece.Reversed());
                    break;
                }
                default:
                    throw new PushPullException(Lang.T("Langs een pad kan (nog) alleen met rechte randen en bogen.", "Along a path only works (for now) with straight edges and arcs."));
            }
        }
        return new SweepPath(Merged(pieces, closed: true), true, FaceGeometry.Normal(f));
    }

    /// <summary>
    /// The run of loose lines and arcs that <paramref name="picked"/> is part of (each meeting the next end to end, where
    /// no third one meets them), as a path; closed when it comes back to where it started. It must lie in one plane.
    /// </summary>
    public static SweepPath OfLines(IReadOnlyList<SketchLine> lines, int picked)
    {
        static bool Same(Vec3 a, Vec3 b) => (a - b).Length < Tol;
        var loose = Enumerable.Range(0, lines.Count).Where(i => !lines[i].Construction).ToList();
        PathPiece Piece(int i, bool forward)
        {
            var l = lines[i];
            var p = new PathPiece(l.Start, l.End, l.Circle);
            return forward ? p : p.Reversed();
        }
        // the lines meeting at a point (but the one we came along)
        List<(int Line, bool Forward)> At(Vec3 q, int but) => loose.Where(i => i != but)
            .SelectMany(i => new[] { (Line: i, Forward: true, From: lines[i].Start), (Line: i, Forward: false, From: lines[i].End) })
            .Where(x => Same(x.From, q)).Select(x => (x.Line, x.Forward)).ToList();
        var ids = new List<int> { picked };
        var run = new List<PathPiece> { Piece(picked, true) };
        bool closed = false;
        // on from its end, as long as exactly one line goes on there
        while (At(run[^1].B, ids[^1]) is [var next])
        {
            if (next.Line == picked) { closed = next.Forward; break; }
            if (ids.Contains(next.Line)) break;
            ids.Add(next.Line); run.Add(Piece(next.Line, next.Forward));
        }
        // and back from its start
        if (!closed)
            while (At(run[0].A, ids[0]) is [var prev] && !ids.Contains(prev.Line))
            {
                ids.Insert(0, prev.Line); run.Insert(0, Piece(prev.Line, !prev.Forward));
            }
        // the plane: of an arc, or of two pieces not in line
        Vec3? n = run.Select(p => p.Arc?.Normal.Normalized()).FirstOrDefault(x => x != null);
        if (n == null)
            for (int i = 1; i < run.Count && n == null; i++)
                if (run[i - 1].EndTangent.Cross(run[i].StartTangent) is var x && x.Length > 1e-9) n = x.Normalized();
        if (n is { } pn && run.Any(p => Math.Abs((p.A - run[0].A).Dot(pn)) > Tol || Math.Abs((p.B - run[0].A).Dot(pn)) > Tol || p.Arc is { } c && c.Normal.Normalized().Cross(pn).Length > 1e-6))
            throw new PushPullException(Lang.T("Het pad moet in één plat vlak liggen.", "The path must lie in one flat plane."));
        return new SweepPath(Merged(run, closed), closed, n);
    }

    /// <summary>Straight pieces in line with each other (and meeting) as one.</summary>
    private static List<PathPiece> Merged(List<PathPiece> pieces, bool closed)
    {
        bool InLine(PathPiece a, PathPiece b) => a.Arc == null && b.Arc == null && (a.B - b.A).Length < Tol && a.EndTangent.Cross(b.StartTangent).Length < 1e-9 && a.EndTangent.Dot(b.StartTangent) > 0;
        var r = new List<PathPiece>();
        foreach (var p in pieces)
            if (r.Count > 0 && InLine(r[^1], p)) r[^1] = new PathPiece(r[^1].A, p.B); else r.Add(p);
        if (closed && r.Count > 1 && InLine(r[^1], r[0])) { r[0] = new PathPiece(r[^1].A, r[0].B); r.RemoveAt(r.Count - 1); }
        return r;
    }

    /// <summary>The prisms the profile face <paramref name="profile"/> sweeps along the outer edge of face
    /// <paramref name="path"/> (both of <paramref name="body"/>).</summary>
    public static List<BrepBody> Prisms(BrepBody body, int profile, int path) => Pieces(body, profile, OfFace(body, path)).Pieces;

    /// <summary>As <see cref="Prisms(BrepBody, int, int)"/>, the profile a face of one body and the path the outer edge of
    /// a face of another.</summary>
    public static List<BrepBody> Prisms(BrepBody profileBody, int profile, BrepBody pathBody, int path) => Pieces(profileBody, profile, OfFace(pathBody, path)).Pieces;

    /// <summary>What a sweep makes: its pieces, and a point just inside the first (in a body or not: whether the sweep
    /// cuts it away or adds to it).</summary>
    public sealed record Swept(List<BrepBody> Pieces, Vec3 Probe);

    /// <summary>The solids the profile face sweeps along the path, piece by piece (they meet face to face: their union is
    /// the swept solid).</summary>
    public static Swept Pieces(BrepBody profileBody, int profile, SweepPath path)
    {
        var P = profileBody.Faces[profile];
        if (P.Surface is not BrepPlane pl || P.Loops.Count != 1)
            throw new PushPullException(Lang.T("Het profiel moet een plat vlak zonder gaten zijn.", "The profile must be a flat face without holes."));
        if (P.Loops[0].Any(c => profileBody.Edges[c.Edge].Curve is not (BrepLine or BrepCircle)))
            throw new PushPullException(Lang.T("Het profiel kan (nog) alleen rechte randen en bogen hebben.", "The profile can only have straight edges and arcs (for now)."));
        var nP = FaceGeometry.Normal(P);
        bool InPlane(Vec3 q) => Math.Abs((q - pl.Origin).Dot(nP)) < 1e-6;
        var tris = FaceGeometry.PlanarTriangles(profileBody, P);
        // (a point of the profile: the middle of its largest triangle; just off it along the way it goes, in the sweep)
        var big = tris.MaxBy(t => (t.B - t.A).Cross(t.C - t.A).Length);
        var mid = (big.A + big.B + big.C) * (1.0 / 3);
        Vec3 Probe(Vec3 along) => mid + along * 1e-6;
        // on the profile's edge or in it
        bool Touches(Vec3 q) => InPlane(q) && (Drawing.NearestOnEdges(profileBody, [profile], q, 0) is { } on && (on.Point - q).Length < 1e-6 || tris.Any(t => InTriangle(q, t, nP)));
        var pieces = path.Pieces;

        // a whole circle: one whole turn of the profile round its centre (its axis in the profile's plane)
        if (pieces is [{ Whole: true, Arc: { } ring }])
        {
            if (!InPlane(ring.Centre) || Math.Abs(ring.Normal.Normalized().Dot(nP)) > 1e-6 || !Touches(pieces[0].A) && !Enumerable.Range(0, 64).Any(k => Touches(new BrepTransform.Turn(ring.Centre, ring.Normal, 2 * Math.PI * k / 64).Point(pieces[0].A))))
                throw new PushPullException(Lang.T("Het profiel moet de cirkel raken, haaks erop.", "The profile must touch the circle, square to it."));
            var whole = SketchDocument.FromBodies([ProfileSheet(profileBody, profile, nP)]).Revolve(new FaceRef(0, 0), ring.Centre, ring.Normal, 2 * Math.PI, checkCrossing: false).Doc;
            return new Swept([whole.Bodies[0]], Probe(ring.Normal.Cross(mid - ring.Centre).Normalized()));
        }

        // where the profile touches the path: a corner of a closed path, an end of an open one; and the way that leaves it
        // square to the profile
        int m = pieces.Count;
        List<PathPiece>? run = null;
        for (int k = 0; k < (path.Closed ? m : m + 1) && run == null; k++)
        {
            var q = k < m ? pieces[k].A : pieces[m - 1].B;
            if (!Touches(q)) continue;
            if (k < m && (path.Closed || k == 0) && Math.Abs(pieces[k].StartTangent.Dot(nP)) > 0.999)
                run = Enumerable.Range(0, m).Select(i => pieces[(k + i) % m]).ToList();
            else if ((path.Closed || k == m) && Math.Abs(pieces[(k - 1 + m) % m].EndTangent.Dot(nP)) > 0.999)
                run = Enumerable.Range(0, m).Select(i => pieces[((k - 1 - i) % m + m) % m].Reversed()).ToList();
        }
        if (run == null)
        {
            bool touches = pieces.Any(p => Touches(p.A) || Touches(p.B));
            throw new PushPullException(touches ? Lang.T("Het profiel moet haaks op het pad staan.", "The profile must stand square to the path.")
                : path.Closed ? Lang.T("Het profiel moet het pad raken, bij een hoek van het pad.", "The profile must touch the path, at a corner of the path.")
                : Lang.T("Het profiel moet het pad raken, bij een eind van het pad.", "The profile must touch the path, at an end of the path."));
        }

        // the plane the path lies in (a single straight line: any way square to it)
        var t0 = run[0].StartTangent;
        var u = path.Normal is { } pn ? pn.Normalized() : Square(t0);
        bool straight = run.All(p => p.Arc == null) && P.Loops[0].All(c => profileBody.Edges[c.Edge].Curve is BrepLine);
        if (straight) return new Swept(Mitred(profileBody, P, run, path.Closed, u), Probe(t0));

        // with arcs: piece by piece, so the corners must run on smoothly
        for (int i = path.Closed ? 0 : 1; i < m; i++)
            if ((run[(i - 1 + m) % m].EndTangent - run[i].StartTangent).Length > 1e-6)
                throw new PushPullException(Lang.T("Met een boog in het profiel of het pad moet het pad (nog) vloeiend doorlopen, zonder scherpe hoeken.", "With an arc in the profile or the path, the path must run on smoothly, without sharp corners (for now)."));
        // (one body, made on as the profile goes: its end face pulled straight on, or turned round the arc's centre; no
        // booleans between pieces that meet with a round face)
        if (path.Closed)
            throw new PushPullException(Lang.T("Een gesloten pad met bogen kan (nog) niet, behalve een hele cirkel.", "A closed path with arcs is not possible yet, except a whole circle."));
        var doc = SketchDocument.FromBodies([Sheets.Twin(ProfileSheet(profileBody, profile, t0), 0)]);
        var cap = new FaceRef(0, 0);
        for (int i = 0; i < m; i++)
        {
            var p = run[i];
            if (p.Arc is { } c) (doc, cap) = doc.Revolve(cap, c.Centre, c.Normal, p.Angle, checkCrossing: false);
            else
            {
                var (d, r) = doc.PushPull(cap, p.Length, checkCrossing: false, join: false, straight: true);
                doc = d; cap = new FaceRef(0, r.FaceMap[cap.Face]);
            }
        }
        var result = new List<BrepBody> { doc.Bodies[0] };
        return new Swept(result, Probe(t0));
    }

    /// <summary>The profile with straight edges swept along straight pieces: a prism per piece, mitred where two meet,
    /// square at an open end.</summary>
    private static List<BrepBody> Mitred(BrepBody profileBody, BrepFace P, List<PathPiece> run, bool closed, Vec3 u)
    {
        var prof = Corners(FaceGeometry.LoopPoints(profileBody, P.Loops[0]));
        int m = run.Count;
        var c = run.Select(p => p.A).ToList();
        var t = run.Select(p => p.StartTangent).ToList();
        var end = closed ? c[0] : run[^1].B;
        // the profile in the path's frame at the start: sideways and up
        var s0 = t[0].Cross(u);
        var local = prof.Select(p => ((p - c[0]).Dot(s0), (p - c[0]).Dot(u))).ToList();
        // the cut at the start of each piece (and at the end of the last): halfway between the pieces, square at an open end
        var mitre = Enumerable.Range(0, m + 1).Select(i =>
        {
            if (!closed && (i == 0 || i == m)) return t[Math.Min(i, m - 1)];
            var b = t[(i - 1 + m) % m] + t[i % m];
            return b.Length > 1e-9 ? b.Normalized() : throw new PushPullException(Lang.T("Het pad keert op zichzelf terug.", "The path turns back on itself."));
        }).ToList();
        var prisms = new List<BrepBody>();
        for (int i = 0; i < m; i++)
        {
            var s = t[i].Cross(u);
            var a = c[i]; var b = i + 1 < m ? c[i + 1] : end; var ma = mitre[i]; var mb = mitre[i + 1];
            var startPts = local.Select(q => { var off = s * q.Item1 + u * q.Item2; return a + off + t[i] * (-off.Dot(ma) / t[i].Dot(ma)); }).ToList();
            var endPts = local.Select(q => { var off = s * q.Item1 + u * q.Item2; return a + off + t[i] * ((b - a - off).Dot(mb) / t[i].Dot(mb)); }).ToList();
            prisms.Add(Prism(startPts, endPts));
        }
        return prisms;
    }

    /// <summary>The profile face alone, as a face without thickness facing along <paramref name="along"/> (its own way,
    /// or turned round).</summary>
    private static BrepBody ProfileSheet(BrepBody body, int face, Vec3 along)
    {
        var twin = Sheets.Twin(body, face);
        var f = FaceGeometry.Normal(twin.Faces[0]).Dot(along) >= 0 ? twin.Faces[0] : twin.Faces[1];
        return new BrepBody { Vertices = twin.Vertices, Edges = twin.Edges, Faces = [f], Sheet = true };
    }

    /// <summary>A direction square to <paramref name="v"/>.</summary>
    private static Vec3 Square(Vec3 v) => (Math.Abs(v.Z) < 0.9 ? new Vec3(0, 0, 1) : new Vec3(1, 0, 0)).Cross(v).Cross(v).Normalized();

    private static bool InTriangle(Vec3 q, (Vec3 A, Vec3 B, Vec3 C) t, Vec3 n)
    {
        double Side(Vec3 a, Vec3 b) => (b - a).Cross(q - a).Dot(n);
        double s1 = Side(t.A, t.B), s2 = Side(t.B, t.C), s3 = Side(t.C, t.A);
        return s1 >= -1e-12 && s2 >= -1e-12 && s3 >= -1e-12 || s1 <= 1e-12 && s2 <= 1e-12 && s3 <= 1e-12;
    }

    /// <summary>A prism between two outlines with the same number of corners (start and end cap), as a body.</summary>
    private static BrepBody Prism(List<Vec3> a, List<Vec3> b)
    {
        int k = a.Count;
        var verts = a.Concat(b).Select(v => (v.X, v.Y, v.Z)).ToList();
        var faces = new List<int[]> { Enumerable.Range(0, k).Reverse().ToArray(), Enumerable.Range(k, k).ToArray() };
        for (int i = 0; i < k; i++) faces.Add([i, (i + 1) % k, k + (i + 1) % k, k + i]);
        // (the right way out: a positive volume)
        double vol = 0;
        foreach (var f in faces)
            for (int i = 1; i + 1 < f.Length; i++)
            {
                var p = verts[f[0]]; var q = verts[f[i]]; var r = verts[f[i + 1]];
                vol += new Vec3(p.X, p.Y, p.Z).Dot(new Vec3(q.X, q.Y, q.Z).Cross(new Vec3(r.X, r.Y, r.Z)));
            }
        if (vol < 0) faces = faces.Select(f => f.Reverse().ToArray()).ToList();
        return BrepShapes.FromPolyhedron(new Polyhedron { Vertices = verts, Faces = faces });
    }

    /// <summary>A loop's corners (points where it only runs straight on left out).</summary>
    private static List<Vec3> Corners(List<Vec3> pts)
    {
        var r = new List<Vec3>();
        foreach (var p in pts) if (r.Count == 0 || (r[^1] - p).Length > Tol) r.Add(p);
        if (r.Count > 1 && (r[0] - r[^1]).Length <= Tol) r.RemoveAt(r.Count - 1);
        var result = new List<Vec3>();
        for (int i = 0; i < r.Count; i++)
        {
            Vec3 a = r[(i - 1 + r.Count) % r.Count], b = r[i], c = r[(i + 1) % r.Count];
            if ((b - a).Normalized().Cross((c - b).Normalized()).Length > 1e-9) result.Add(b);
        }
        return result;
    }
}
