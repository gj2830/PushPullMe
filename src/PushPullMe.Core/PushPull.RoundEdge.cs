using RsDocx.Core.Brep;
using RsDocx.Core.Stl;

namespace PushPullMe.Core;

public static partial class PushPull
{
    /// <summary>One sharp edge rounded (see <see cref="RoundEdges"/>); the new face.</summary>
    public static (BrepBody Body, int Face) RoundEdge(BrepBody body, int edge, double radius)
    {
        var (b, faces) = RoundEdges(body, [edge], radius);
        return (b, faces[0]);
    }

    /// <summary>A rounded edge: its two flat faces, their normals, its direction (start → end), whether it is an outside
    /// edge (S = 1; an inside one S = −1), and in a cross-section through a point of the edge: the cylinder's centre, and
    /// where it touches A and B.</summary>
    private sealed record Rounded(int Edge, int A, int B, Vec3 NA, Vec3 NB, Vec3 Dir, double S, double R)
    {
        public double C => NA.Dot(NB);
        public Vec3 Centre(Vec3 e) => e - (NA + NB) * (S * R / (1 + C));
        public Vec3 TA(Vec3 e) => Centre(e) + NA * (S * R);
        public Vec3 TB(Vec3 e) => Centre(e) + NB * (S * R);
        public Vec3 Normal(int face) => face == A ? NA : NB;
    }

    /// <summary>
    /// Sharp straight edges between flat faces rounded with <paramref name="radius"/> (as DesignSpark's round; SketchUp has
    /// no such tool): each becomes a cylinder face, its flat faces end where it touches them. At each end of an edge:
    /// <list type="bullet">
    /// <item>a plain corner where only this edge is rounded: the face there (square to it) gets an arc;</item>
    /// <item>a corner where all three edges are rounded: a ball corner (a piece of sphere, as a rolling ball);</item>
    /// <item>a corner where two are rounded and the third stays sharp (the top edges of a block): the two roundings meet
    /// in a mitre (an ellipse), the sharp edge gets shorter.</item>
    /// </list>
    /// Outside edges lose material, inside ones gain it. Returns the new body and the new faces (roundings first, in the
    /// order of the edges, then the ball corners); the others keep their numbers.
    /// </summary>
    public static (BrepBody Body, List<int> Faces) RoundEdges(BrepBody body, IReadOnlyCollection<int> edgeSet, double radius) =>
        RoundEdges(body, edgeSet.Distinct().Select(e => (e, radius)).ToList());

    /// <summary>As <see cref="RoundEdges(BrepBody, IReadOnlyCollection{int}, double)"/>, each edge with its own radius. Where
    /// two roundings of different radius meet in a mitre, the larger runs on (an arc in the face beside it) and they
    /// meet in a free curve (a B-spline through points on both); a ball corner needs one radius.</summary>
    public static (BrepBody Body, List<int> Faces) RoundEdges(BrepBody body, IReadOnlyList<(int Edge, double Radius)> edgeRadii)
    {
        if (edgeRadii.Count == 0) throw new PushPullException(Lang.T("Kies eerst een rand om af te ronden.", "Choose an edge to round first."));
        if (edgeRadii.Any(x => x.Radius < 1e-5)) throw new PushPullException(Lang.T("Een ronding moet een straal hebben.", "A rounding must have a radius."));
        var radiusOf = new Dictionary<int, double>();
        foreach (var (e, r) in edgeRadii) radiusOf[e] = r;
        var set = radiusOf.Keys.ToList();
        var inSet = set.ToHashSet();
        var verts = body.Vertices.ToList();
        var edges = body.Edges.ToList();
        var faces = body.Faces.Select(f => f.Loops.Select(l => l.ToList()).ToList()).ToList();
        int From(BrepCoedge x) => x.Forward ? edges[x.Edge].Start : edges[x.Edge].End;
        int To(BrepCoedge x) => x.Forward ? edges[x.Edge].End : edges[x.Edge].Start;
        var facesOf = new Dictionary<int, List<int>>();
        for (int f = 0; f < body.Faces.Count; f++)
            foreach (var c in body.Faces[f].Loops.SelectMany(l => l))
                (facesOf.TryGetValue(c.Edge, out var l) ? l : facesOf[c.Edge] = []).Add(f);
        var around = new Dictionary<int, List<int>>();
        for (int e = 0; e < edges.Count; e++)
            if (edges[e].Start >= 0)
                foreach (int v in new[] { edges[e].Start, edges[e].End })
                    (around.TryGetValue(v, out var l) ? l : around[v] = []).Add(e);
        int OtherEnd(int e, int v) => edges[e].Start == v ? edges[e].End : edges[e].Start;

        // the edges and their faces
        var info = new Dictionary<int, Rounded>();
        foreach (int e in set)
        {
            if (e < 0 || e >= edges.Count) throw new ArgumentOutOfRangeException(nameof(edgeRadii));
            var E = edges[e];
            if (E.Curve is not BrepLine || E.Start < 0) throw new PushPullException(Lang.T("Alleen rechte randen kunnen (nog) afgerond worden.", "Only straight edges can be rounded (for now)."));
            var users = facesOf.GetValueOrDefault(e, []);
            if (users.Count != 2 || users[0] == users[1]) throw new PushPullException(Lang.T("Deze rand ligt niet tussen twee vlakken.", "This edge does not lie between two faces."));
            int fa = users[0], fb = users[1];
            if (body.Faces[fa].Surface is not BrepPlane || body.Faces[fb].Surface is not BrepPlane)
                throw new PushPullException(Lang.T("Alleen een rand tussen twee platte vlakken kan (nog) afgerond worden.", "Only an edge between two flat faces can be rounded (for now)."));
            Vec3 nA = FaceGeometry.Normal(body.Faces[fa]), nB = FaceGeometry.Normal(body.Faces[fb]);
            double c = nA.Dot(nB);
            if (c < -1 + 1e-6 || c > 1 - 1e-6) throw new PushPullException(Lang.T("Deze rand is niet scherp (de vlakken liggen in elkaars verlengde).", "This edge is not sharp (the faces lie in line with each other)."));
            Vec3 P = verts[E.Start], a = (verts[E.End] - P).Normalized();
            // outside (convex) when A, going away from the edge, lies behind B (from the geometry, not the loop's direction)
            // (both ends must agree: where A runs on differently at its two ends, it is no plain edge)
            var sides = new List<double>();
            foreach (int v in new[] { E.Start, E.End })
                foreach (int o in around[v].Where(o => o != e && facesOf.GetValueOrDefault(o, []).Contains(fa)))
                {
                    var d = verts[OtherEnd(o, v)] - verts[v];
                    d -= a * d.Dot(a);
                    if (d.Length > 1e-9) sides.Add(d.Dot(nB));
                }
            if (sides.Count == 0 || sides.Any(x => Math.Abs(x) < 1e-12)) throw new PushPullException(Lang.T("Bij deze rand is niet te zien of het een buiten- of binnenhoek is.", "At this edge it cannot be told whether it is an outside or an inside corner."));
            if (sides.Any(x => x > 0) && sides.Any(x => x < 0))
                throw new PushPullException(Lang.T("Het vlak naast deze rand loopt aan de twee uiteinden verschillend door; dat kan nog niet.", "The face next to this edge goes on differently at its two ends; that is not possible yet."));
            double side = sides[0];
            info[e] = new Rounded(e, fa, fb, nA, nB, a, side < 0 ? 1 : -1, radiusOf[e]);
        }

        // per corner: the new vertex on each face, and the curve that closes each rounding there
        var nv = new Dictionary<(int V, int Face), int>();
        int Add(Vec3 p) { verts.Add(p); return verts.Count - 1; }
        var endCurve = new Dictionary<(int Edge, int V), List<int>>();
        void EndCurve(int e, int v, int curve) => (endCurve.TryGetValue((e, v), out var l) ? l : endCurve[(e, v)] = []).Add(curve);
        var capArcs = new List<(int Cap, int Arc)>();
        // the corner pieces (a ball, or a torus where the radii differ): their surface and edges
        var patches = new List<(BrepSurface Surface, bool Reversed, List<int> Edges)>();
        // (a rounding's own end on a face, where it differs from the corner's: a torus corner's two on T)
        var nvEdge = new Dictionary<(int Edge, int V, int Face), int>();
        int NV(int e, int v, int face) => nvEdge.TryGetValue((e, v, face), out var x) ? x : nv[(v, face)];
        var movedEnds = new List<(int Edge, int V, int To)>();
        var corners = set.SelectMany(e => new[] { edges[e].Start, edges[e].End }).Distinct().ToList();
        foreach (int v in corners)
        {
            var at = verts[v];
            var inc = around[v];
            if (inc.Count != 3) throw new PushPullException(Lang.T("Bij een hoek van deze rand komen meer dan drie randen samen; dat kan nog niet.", "More than three edges meet at a corner of this edge; that is not possible yet."));
            var rounded = inc.Where(inSet.Contains).ToList();
            var sharp = inc.Where(x => !inSet.Contains(x)).ToList();
            if (sharp.Any(x => edges[x].Curve is not BrepLine))
                throw new PushPullException(Lang.T("Bij een hoek van deze rand zit een gebogen rand; dat kan nog niet.", "There is a curved edge at a corner of this edge; that is not possible yet."));
            if (rounded.Count == 1)
            {
                // a plain corner: the end face, square to the edge, gets an arc
                var r = info[rounded[0]];
                int eA = sharp.SingleOrDefault(x => facesOf[x].Contains(r.A), -1), eB = sharp.SingleOrDefault(x => facesOf[x].Contains(r.B), -1);
                if (eA < 0 || eB < 0 || eA == eB) throw new PushPullException(Lang.T("Aan het eind van deze rand zit geen gewone hoek; dat kan nog niet.", "There is no ordinary corner at the end of this edge; that is not possible yet."));
                int cap = facesOf[eA].Intersect(facesOf[eB]).Where(f => f != r.A && f != r.B).DefaultIfEmpty(-1).First();
                if (cap < 0 || body.Faces[cap].Surface is not BrepPlane cp || Math.Abs(Math.Abs(cp.Normal.Normalized().Dot(r.Dir)) - 1) > 1e-9)
                    throw new PushPullException(Lang.T("Het vlak aan het eind van deze rand staat er niet haaks op; dat kan nog niet (rond de randen daar ook af).", "The face at the end of this edge is not square to it; that is not possible yet (round the edges there too)."));
                Vec3 ta = r.TA(at), tb = r.TB(at);
                // they must lie on the edges along A and B, between the corner and their other end
                foreach (var (e2, t) in new[] { (eA, ta), (eB, tb) })
                {
                    Vec3 far = verts[OtherEnd(e2, v)];
                    double u = (t - at).Dot(far - at) / (far - at).Dot(far - at);
                    if (((at + (far - at) * u) - t).Length > DistanceTol || u <= 1e-9)
                        throw new PushPullException(Lang.T("Het vlak naast deze rand loopt aan het eind niet recht door (bijvoorbeeld een wand die erop staat); dat kan nog niet.", "The face next to this edge does not go straight on at the end (for example a wall standing on it); that is not possible yet."));
                    if (u >= 1 - 1e-9) throw new PushPullException(Lang.T("Zo groot past de ronding niet: hij komt voorbij de rand van een vlak ernaast.", "A rounding that big does not fit: it reaches past the edge of a face next to it."));
                }
                int ia = Add(ta), ib = Add(tb);
                nv[(v, r.A)] = ia; nv[(v, r.B)] = ib;
                movedEnds.Add((eA, v, ia)); movedEnds.Add((eB, v, ib));
                var centre = r.Centre(at);
                var normal = (ta - centre).Cross(tb - centre).Dot(r.Dir) > 0 ? r.Dir : r.Dir * -1;
                edges.Add(new BrepEdge(ia, ib, new BrepCircle(centre, normal, (ta - centre).Normalized(), r.R)));
                EndCurve(r.Edge, v, edges.Count - 1);
                capArcs.Add((cap, edges.Count - 1));
            }
            else if (rounded.Count == 3)
            {
                // a ball corner: a sphere R from all three faces
                var rs = rounded.Select(x => info[x]).ToList();
                if (rs.Any(x => x.S != rs[0].S)) throw new PushPullException(Lang.T("In deze hoek komen een buiten- en een binnenronding samen; dat kan nog niet.", "An outside and an inside rounding meet in this corner; that is not possible yet."));
                if (rs.Any(x => Math.Abs(x.R - rs[0].R) > DistanceTol)) { TorusCorner(v, at, rs); continue; }
                double radius = rs[0].R;
                var fs = rs.SelectMany(x => new[] { x.A, x.B }).Distinct().ToList();
                if (fs.Count != 3) throw new PushPullException(Lang.T("Deze hoek heeft geen drie vlakken; dat kan nog niet.", "This corner does not have three faces; that is not possible yet."));
                var ns = fs.Select(f => rs.First(x => x.A == f || x.B == f).Normal(f)).ToList();
                double s = rs[0].S;
                double det = ns[0].Dot(ns[1].Cross(ns[2]));
                if (Math.Abs(det) < 1e-9) throw new PushPullException(Lang.T("Deze hoek is te plat voor een bolhoek; dat kan nog niet.", "This corner is too flat for a ball corner; that is not possible yet."));
                var d = ns.Select(n => n.Dot(at) - s * radius).ToList();
                var ball = (ns[1].Cross(ns[2]) * d[0] + ns[2].Cross(ns[0]) * d[1] + ns[0].Cross(ns[1]) * d[2]) * (1 / det);
                for (int i = 0; i < 3; i++) nv[(v, fs[i])] = Add(ball + ns[i] * (s * radius));
                var arcs = new List<int>();
                foreach (var r in rs)
                {
                    int ia = nv[(v, r.A)], ib = nv[(v, r.B)];
                    var normal = (verts[ia] - ball).Cross(verts[ib] - ball).Dot(r.Dir) > 0 ? r.Dir : r.Dir * -1;
                    edges.Add(new BrepEdge(ia, ib, new BrepCircle(ball, normal, (verts[ia] - ball).Normalized(), radius)));
                    EndCurve(r.Edge, v, edges.Count - 1);
                    arcs.Add(edges.Count - 1);
                }
                var n0 = (verts[edges[arcs[0]].Start] - ball).Normalized();
                var sAxis = Math.Abs(n0.Z) < 0.9 ? new Vec3(0, 0, 1) : new Vec3(1, 0, 0);
                patches.Add((new BrepSphere(ball, radius, sAxis, (n0 - sAxis * n0.Dot(sAxis)).Normalized()), s < 0, arcs));
            }
            else
            {
                // two rounded on one face T, the third edge sharp: the roundings meet in a mitre (an ellipse)
                var (r1, r2) = (info[rounded[0]], info[rounded[1]]);
                if (r1.S != r2.S) throw new PushPullException(Lang.T("In deze hoek komen een buiten- en een binnenronding samen; dat kan nog niet.", "An outside and an inside rounding meet in this corner; that is not possible yet."));
                var shared = new[] { r1.A, r1.B }.Intersect(new[] { r2.A, r2.B }).ToList();
                if (shared.Count != 1) throw new PushPullException(Lang.T("Deze twee rondingen delen geen vlak; dat kan nog niet.", "These two roundings share no face; that is not possible yet."));
                int T = shared[0], F = r1.A == T ? r1.B : r1.A, R = r2.A == T ? r2.B : r2.A;
                int e3 = sharp[0];
                if (!facesOf[e3].Contains(F) || !facesOf[e3].Contains(R)) throw new PushPullException(Lang.T("Deze hoek is geen gewone hoek; dat kan nog niet.", "This corner is not an ordinary corner; that is not possible yet."));
                double s = r1.S;
                Vec3 nT = r1.Normal(T);
                if (Math.Abs(r1.R - r2.R) > DistanceTol)
                {
                    MixedMitre(v, at, r1, r2, T, F, R, sharp[0]);
                    continue;
                }
                double radius = r1.R;
                // where the two centre lines meet
                Vec3 c1 = r1.Centre(at), c2 = r2.Centre(at), a1 = r1.Dir, a2 = r2.Dir;
                var w = c2 - c1;
                double a1a2 = a1.Dot(a2), den = 1 - a1a2 * a1a2;
                if (den < 1e-12) throw new PushPullException(Lang.T("Deze twee rondingen lopen evenwijdig; dat kan nog niet.", "These two roundings run parallel; that is not possible yet."));
                double t1 = (w.Dot(a1) - w.Dot(a2) * a1a2) / den;
                var x0 = c1 + a1 * t1;
                // where each rounding leaves its other face: on the sharp edge, the same point for both
                var far = verts[OtherEnd(e3, v)];
                var d3 = far - at;
                Vec3 Meet(Rounded r, int face)
                {
                    var p = face == r.A ? r.TA(at) : r.TB(at);
                    // p + Dir·t on the line at + d3·u
                    var n = d3.Cross(r.Dir);
                    double t = (at - p).Cross(d3).Dot(n) / n.Dot(n);
                    return p + r.Dir * t;
                }
                Vec3 q1 = Meet(r1, F), q2 = Meet(r2, R);
                if ((q1 - q2).Length > DistanceTol)
                    throw new PushPullException(Lang.T("De vlakken in deze hoek staan zo dat de twee rondingen de scherpe rand niet op één punt raken; rond die rand ook af.", "The faces in this corner stand so that the two roundings do not touch the sharp edge in one point; round that edge too."));
                var q = q1;
                double u = (q - at).Dot(d3) / d3.Dot(d3);
                if (u <= 1e-9 || u >= 1 - 1e-9) throw new PushPullException(Lang.T("Zo groot past de ronding niet in deze hoek.", "A rounding that big does not fit in this corner."));
                int pT = Add(x0 + nT * (s * radius));
                int iq = Add(q);
                nv[(v, T)] = pT; nv[(v, F)] = iq; nv[(v, R)] = iq;
                movedEnds.Add((e3, v, iq));
                // the mitre: a quarter ellipse from q (end of its long axis) to the point on T (end of its short axis)
                Vec3 ux = q - x0, uy = verts[pT] - x0;
                if (Math.Abs(ux.Normalized().Dot(uy.Normalized())) > 1e-6) throw new PushPullException(Lang.T("Deze hoek kan (nog) niet afgerond worden.", "This corner cannot be rounded (yet)."));
                edges.Add(new BrepEdge(iq, pT, new BrepEllipse(x0, ux.Cross(uy).Normalized(), ux.Normalized(), ux.Length, uy.Length)));
                EndCurve(r1.Edge, v, edges.Count - 1);
                EndCurve(r2.Edge, v, edges.Count - 1);
            }
        }

        // a corner of three roundings of other radii (three different, or the third smaller), as DesignSpark makes it
        // (kubus_bewerkt_bewerkt.rsdocx, a corner of r 1, 2 and 3): no corner piece, the three cylinders run on until they
        // cut each other: each two along a curve from where their lines meet on the face they share to the one point on
        // all three
        void PatchCorner(int v, Vec3 at, List<Rounded> rs)
        {
            Vec3 Tangent(Rounded x, int face, Vec3 e) => face == x.A ? x.TA(e) : x.TB(e);
            int Shared(Rounded x, Rounded y) => new[] { x.A, x.B }.Intersect(new[] { y.A, y.B }).Single();
            foreach (var (x, y) in new[] { (rs[0], rs[1]), (rs[0], rs[2]), (rs[1], rs[2]) })
                if (new[] { x.A, x.B }.Intersect(new[] { y.A, y.B }).Count() != 1) throw new PushPullException(Lang.T("Deze hoek kan (nog) niet afgerond worden.", "This corner cannot be rounded (yet)."));
            // where two meet on the face they share: where the lines along which they touch it cross
            Vec3 MeetPoint(Rounded x, Rounded y)
            {
                int f = Shared(x, y);
                Vec3 px = Tangent(x, f, at), py = Tangent(y, f, at);
                var nn = x.Dir.Cross(y.Dir);
                if (nn.Length < 1e-9) throw new PushPullException(Lang.T("Twee rondingen in deze hoek lopen evenwijdig; dat kan nog niet.", "Two roundings in this corner run parallel; that is not possible yet."));
                return px + x.Dir * ((py - px).Cross(y.Dir).Dot(nn) / nn.Dot(nn));
            }
            var (a, b, c) = (rs[0], rs[1], rs[2]);
            Cyl C(Rounded x) => new(x.Centre(at), x.Dir, x.R);
            Vec3 pab = MeetPoint(a, b), pac = MeetPoint(a, c), pbc = MeetPoint(b, c);
            // (the one nearest the corner; started from the meeting points, their middle, the corner and points inward)
            var inward = (a.NA + a.NB + b.NA + b.NB + c.NA + c.NB) * (-a.S);
            double rMax = Math.Max(a.R, Math.Max(b.R, c.R));
            var guesses = new List<Vec3> { (pab + pac + pbc) * (1.0 / 3), pab, pac, pbc, at };
            for (int k = 1; k <= 4; k++) guesses.Add(at + inward.Normalized() * (rMax * k / 4));
            var triple = TriplePoint(C(a), C(b), C(c), at, guesses)
                ?? throw new PushPullException(Lang.T("De drie rondingen in deze hoek komen niet in één punt samen; dat kan nog niet.", "The three roundings in this corner do not meet in one point; that is not possible yet."));
            int Meet(Rounded x, Rounded y, Vec3 p)
            {
                int f = Shared(x, y), i = Add(p);
                nvEdge[(x.Edge, v, f)] = i; nvEdge[(y.Edge, v, f)] = i;
                return i;
            }
            int iab = Meet(a, b, pab), iac = Meet(a, c, pac), ibc = Meet(b, c, pbc), ip = Add(triple);
            int Cut(Rounded x, Rounded y, int from)
            {
                edges.Add(new BrepEdge(from, ip, CylinderCut(C(x), C(y), verts[from], triple)));
                int e = edges.Count - 1;
                EndCurve(x.Edge, v, e); EndCurve(y.Edge, v, e);
                return e;
            }
            Cut(a, b, iab); Cut(a, c, iac); Cut(b, c, ibc);
        }

        // a corner of three roundings where two (on face T) have the same radius r and the third, square to T, a larger R:
        // a piece of torus (as the rolling ball makes it: a rounded box with rounded top edges); centre on the larger's
        // axis r below T, tube r round a circle of R − r
        void TorusCorner(int v, Vec3 at, List<Rounded> rs)
        {
            Rounded? p = null, q = null, t = null;
            for (int i = 0; i < 3 && t == null; i++)
            {
                var o = rs.Where((_, j) => j != i).ToList();
                if (Math.Abs(o[0].R - o[1].R) < DistanceTol && rs[i].R > o[0].R + DistanceTol) (t, p, q) = (rs[i], o[0], o[1]);
            }
            if (t == null || p == null || q == null) { PatchCorner(v, at, rs); return; }
            var shared = new[] { p.A, p.B }.Intersect(new[] { q.A, q.B }).ToList();
            if (shared.Count != 1) throw new PushPullException(Lang.T("Deze hoek kan (nog) niet afgerond worden.", "This corner cannot be rounded (yet)."));
            int T = shared[0], fp = p.A == T ? p.B : p.A, fq = q.A == T ? q.B : q.A;
            if (!new[] { t.A, t.B }.Order().SequenceEqual(new[] { fp, fq }.Order())) throw new PushPullException(Lang.T("Deze hoek kan (nog) niet afgerond worden.", "This corner cannot be rounded (yet)."));
            var nT = p.Normal(T);
            if (Math.Abs(Math.Abs(t.Dir.Dot(nT)) - 1) > 1e-9 || Math.Abs(t.Normal(fp).Dot(nT)) > 1e-9 || Math.Abs(t.Normal(fq).Dot(nT)) > 1e-9)
                throw new PushPullException(Lang.T("Rondingen met verschillende straal in een hoek die niet haaks is; dat kan nog niet.", "Roundings with different radii in a corner that is not square; that is not possible yet."));
            double r = p.R, big = t.R, s = p.S;
            var a0 = t.Centre(at);
            var aT = a0 + nT * (nT.Dot(at) - nT.Dot(a0));
            var ct = aT - nT * (s * r);
            Vec3 Tangent(Rounded x, int face, Vec3 e) => face == x.A ? x.TA(e) : x.TB(e);
            Vec3 Level(Rounded x) => at + x.Dir * (ct - at).Dot(x.Dir);
            Vec3 ep = Level(p), eq = Level(q), et = Level(t);
            Vec3 cp = Tangent(p, fp, ep), cq = Tangent(q, fq, eq);
            if ((cp - Tangent(t, fp, et)).Length > DistanceTol || (cq - Tangent(t, fq, et)).Length > DistanceTol)
                throw new PushPullException(Lang.T("Deze hoek kan (nog) niet afgerond worden.", "This corner cannot be rounded (yet)."));
            int iA = Add(Tangent(p, T, ep)), iB = Add(Tangent(q, T, eq)), iCp = Add(cp), iCq = Add(cq);
            nvEdge[(p.Edge, v, T)] = iA; nvEdge[(q.Edge, v, T)] = iB;
            nv[(v, fp)] = iCp; nv[(v, fq)] = iCq;
            int Arc(int s0, int s1, Vec3 centre, Vec3 dir, double rad)
            {
                var n = (verts[s0] - centre).Cross(verts[s1] - centre).Dot(dir) > 0 ? dir : dir * -1;
                edges.Add(new BrepEdge(s0, s1, new BrepCircle(centre, n, (verts[s0] - centre).Normalized(), rad)));
                return edges.Count - 1;
            }
            int top = Arc(iA, iB, aT, nT, big - r);
            int arcP = Arc(iA, iCp, p.Centre(ep), p.Dir, r);
            int arcQ = Arc(iB, iCq, q.Centre(eq), q.Dir, r);
            int arcT = Arc(iCp, iCq, ct, t.Dir, big);
            capArcs.Add((T, top));
            EndCurve(p.Edge, v, arcP); EndCurve(q.Edge, v, arcQ); EndCurve(t.Edge, v, arcT);
            var x0 = verts[iA] - aT; x0 -= nT * x0.Dot(nT);
            double rho = big - r;
            if (rho > r + DistanceTol)
            {
                patches.Add((new BrepTorus(ct, nT, x0.Normalized(), rho, r), s < 0, [top, arcP, arcQ, arcT]));
                return;
            }
            // (a torus whose ring is not larger than its tube — R ≤ 2r, the usual case — is not written as a torus: the
            // same piece as an exact rational NURBS patch, a quarter arc turned a quarter round)
            Vec3 u1 = x0.Normalized(), u2 = verts[iB] - aT; u2 = (u2 - nT * u2.Dot(nT)).Normalized();
            if (Math.Abs(u1.Dot(u2)) > 1e-9) throw new PushPullException(Lang.T("Rondingen met verschillende straal in een hoek die niet haaks is; dat kan nog niet.", "Roundings with different radii in a corner that is not square; that is not possible yet."));
            double h = Math.Sqrt(0.5);
            var around = new[] { u1, u1 + u2, u2 };
            var radii = new[] { rho, rho + r, rho + r };
            var heights = new[] { s * r, s * r, 0.0 };
            var wv = new[] { 1, h, 1 };
            var pts = new List<Vec3>(); var wts = new List<double>();
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++) { pts.Add(ct + nT * heights[j] + around[i] * radii[j]); wts.Add(wv[i] * wv[j]); }
            var ns = new BrepNurbsSurface(2, 2, 3, 3, pts, wts, [0.0, 1.0], [3, 3], [0.0, 1.0], [3, 3]);
            // its natural normal dS/du × dS/dv against the way the face must face (outside: away from the tube's ring)
            Vec3 m = ns.At(0.5, 0.5), du = ns.At(0.501, 0.5) - ns.At(0.499, 0.5), dv = ns.At(0.5, 0.501) - ns.At(0.5, 0.499);
            var w = m - ct; w -= nT * w.Dot(nT);
            var ring = ct + w.Normalized() * rho;
            bool reversed = du.Cross(dv).Dot((m - ring) * s) < 0;
            patches.Add((ns, reversed, [top, arcP, arcQ, arcT]));
        }

        // a mitre of two roundings of different radius (on face T; the larger, on T and Fb, runs on past the smaller's
        // face Rs, square to it, as an arc there; the two meet in a curve from that face to T)
        void MixedMitre(int v, Vec3 at, Rounded r1, Rounded r2, int T, int F, int R, int e3)
        {
            var (big, small) = r1.R > r2.R ? (r1, r2) : (r2, r1);
            int fb = big == r1 ? F : R, rsFace = big == r1 ? R : F;
            if (body.Faces[rsFace].Surface is not BrepPlane rp || Math.Abs(Math.Abs(rp.Normal.Normalized().Dot(big.Dir)) - 1) > 1e-9)
                throw new PushPullException(Lang.T("Twee rondingen met verschillende straal in een hoek die niet haaks is; dat kan nog niet.", "Two roundings with different radii in a corner that is not square; that is not possible yet."));
            Vec3 Tangent(Rounded r, int face) => face == r.A ? r.TA(at) : r.TB(at);
            // on T: where the two lines where they touch T meet
            Vec3 pb = Tangent(big, T), ps = Tangent(small, T);
            var nn = big.Dir.Cross(small.Dir);
            if (nn.Length < 1e-9) throw new PushPullException(Lang.T("Deze twee rondingen lopen evenwijdig; dat kan nog niet.", "These two roundings run parallel; that is not possible yet."));
            double tb = (ps - pb).Cross(small.Dir).Dot(nn) / nn.Dot(nn);
            var pT = pb + big.Dir * tb;
            // the larger leaves Fb on the sharp edge
            var d3 = verts[OtherEnd(e3, v)] - at;
            var n3 = d3.Cross(big.Dir);
            var pf = Tangent(big, fb);
            var qBig = pf + big.Dir * ((at - pf).Cross(d3).Dot(n3) / n3.Dot(n3));
            double u = (qBig - at).Dot(d3) / d3.Dot(d3);
            if (u <= 1e-9 || u >= 1 - 1e-9) throw new PushPullException(Lang.T("Zo groot past de ronding niet in deze hoek.", "A rounding that big does not fit in this corner."));
            // the smaller leaves Rs where its line there meets the larger (on Rs, the larger's arc)
            var bc = big.Centre(at);
            Vec3 Perp(Vec3 w) => w - big.Dir * w.Dot(big.Dir);
            double OnBig(Vec3 p0, Vec3 dir, double near)
            {
                Vec3 a0 = Perp(p0 - bc), av = Perp(dir);
                double qa = av.Dot(av), qb = 2 * a0.Dot(av), qc = a0.Dot(a0) - big.R * big.R;
                double disc = qb * qb - 4 * qa * qc;
                if (qa < 1e-18 || disc < 0) throw new PushPullException(Lang.T("Deze twee rondingen raken elkaar niet zoals verwacht; dat kan nog niet.", "These two roundings do not touch as expected; that is not possible yet."));
                double t1 = (-qb - Math.Sqrt(disc)) / (2 * qa), t2 = (-qb + Math.Sqrt(disc)) / (2 * qa);
                return Math.Abs(t1 - near) < Math.Abs(t2 - near) ? t1 : t2;
            }
            var pr = Tangent(small, rsFace);
            var qs = pr + small.Dir * OnBig(pr, small.Dir, (at - pr).Dot(small.Dir));
            int iqBig = Add(qBig), iqs = Add(qs), ipT = Add(pT);
            nv[(v, T)] = ipT; nv[(v, fb)] = iqBig; nv[(v, rsFace)] = iqs;
            // (the smaller's own vertex on its face Rs, the larger's on Rs is the arc's start on the sharp edge)
            movedEnds.Add((e3, v, iqBig));
            // the larger's arc on Rs: from the sharp edge to where the smaller leaves Rs
            var centreRs = bc + big.Dir * (qBig - bc).Dot(big.Dir);
            var arcNormal = (qBig - centreRs).Cross(qs - centreRs).Dot(big.Dir) > 0 ? big.Dir : big.Dir * -1;
            edges.Add(new BrepEdge(iqBig, iqs, new BrepCircle(centreRs, arcNormal, (qBig - centreRs).Normalized(), big.R)));
            int arc = edges.Count - 1;
            capArcs.Add((rsFace, arc));
            EndCurve(big.Edge, v, arc);
            // the curve where they meet: around the smaller from qs to pT, each point moved along it onto the larger
            var sc = small.Centre(at);
            var x0 = qs - sc; x0 -= small.Dir * x0.Dot(small.Dir);
            var y0 = pT - sc; y0 -= small.Dir * y0.Dot(small.Dir);
            var ex = x0.Normalized(); var ey = small.Dir.Cross(ex);
            double end = Math.Atan2(y0.Dot(ey), y0.Dot(ex));
            var pts = new List<Vec3> { qs };
            double tPrev = (qs - sc).Dot(small.Dir);
            const int n = 96;
            for (int i = 1; i < n; i++)
            {
                double th = end * i / n;
                var p0 = sc + (ex * Math.Cos(th) + ey * Math.Sin(th)) * small.R;
                double t = OnBig(p0, small.Dir, tPrev);
                pts.Add(p0 + small.Dir * t); tPrev = t;
            }
            pts.Add(pT);
            edges.Add(new BrepEdge(iqs, ipT, BSpline.Interpolate(pts, false)));
            EndCurve(big.Edge, v, edges.Count - 1);
            EndCurve(small.Edge, v, edges.Count - 1);
        }

        // the sharp edges that end at a corner: their end moves (along them)
        foreach (var (e, v, to) in movedEnds)
            edges[e] = edges[e].Start == v ? edges[e] with { Start = to } : edges[e] with { End = to };
        foreach (var (e, _, _) in movedEnds)
        {
            var o = body.Edges[e]; var n = edges[e];
            if ((verts[n.End] - verts[n.Start]).Dot(verts[o.End] - verts[o.Start]) <= 0)
                throw new PushPullException(Lang.T("Zo groot past de ronding niet: hij komt voorbij de rand van een vlak ernaast.", "A rounding that big does not fit: it reaches past the edge of a face next to it."));
        }
        // the rounded edges become the lines where the roundings touch their faces
        var lineB = new Dictionary<int, int>();
        foreach (int e in set)
        {
            var r = info[e]; var E = body.Edges[e];
            edges[e] = new BrepEdge(NV(e, E.Start, r.A), NV(e, E.End, r.A), new BrepLine());
            edges.Add(new BrepEdge(NV(e, E.Start, r.B), NV(e, E.End, r.B), new BrepLine()));
            lineB[e] = edges.Count - 1;
            foreach (var loop in faces[r.B])
                for (int i = 0; i < loop.Count; i++)
                    if (loop[i].Edge == e) loop[i] = new BrepCoedge(lineB[e], loop[i].Forward);
        }
        // the arcs into the end faces, between the two edges that now end at them
        foreach (var (cap, arc) in capArcs)
        {
            int s0 = edges[arc].Start, s1 = edges[arc].End;
            bool done = false;
            foreach (var loop in faces[cap])
            {
                for (int k = 0; k < loop.Count && !done; k++)
                {
                    int a = To(loop[k]), b = From(loop[(k + 1) % loop.Count]);
                    if (a == b || !((a == s0 && b == s1) || (a == s1 && b == s0))) continue;
                    loop.Insert(k + 1, new BrepCoedge(arc, a == s0));
                    done = true;
                }
                if (done) break;
            }
            if (!done) throw new PushPullException(Lang.T("De ronding sluit niet aan op het vlak aan het eind; dat kan nog niet.", "The rounding does not join the face at the end; that is not possible yet."));
        }
        // how each edge is used so far (the roundings use it the other way round)
        var used = new Dictionary<int, bool>();
        foreach (var c in faces.SelectMany(f => f.SelectMany(l => l))) used[c.Edge] = c.Forward;
        var newFaces = new List<BrepFace>();
        foreach (int e in set)
        {
            var r = info[e]; var E = body.Edges[e];
            var parts = new List<BrepCoedge> { new(e, !used[e]), new(lineB[e], !used[lineB[e]]) };
            var open = endCurve[(e, E.Start)].Concat(endCurve[(e, E.End)]).ToList();
            var loop = new List<BrepCoedge> { parts[0] };
            parts.RemoveAt(0);
            while (parts.Count + open.Count > 0)
            {
                int at = To(loop[^1]);
                int pi = parts.FindIndex(x => From(x) == at);
                if (pi >= 0) { loop.Add(parts[pi]); parts.RemoveAt(pi); continue; }
                int oi = open.FindIndex(x => edges[x].Start == at || edges[x].End == at);
                if (oi < 0) throw new PushPullException(Lang.T("De ronding sluit niet; dat kan nog niet.", "The rounding does not close; that is not possible yet."));
                int ce = open[oi]; open.RemoveAt(oi);
                bool fwd = edges[ce].Start == at;
                // (an arc in an end face, or a mitre the other rounding used already: the other way round here)
                if (used.TryGetValue(ce, out var u) && u == fwd) throw new PushPullException(Lang.T("De ronding sluit niet; dat kan nog niet.", "The rounding does not close; that is not possible yet."));
                loop.Add(new BrepCoedge(ce, fwd));
                used[ce] = fwd;
            }
            if (To(loop[^1]) != From(loop[0])) throw new PushPullException(Lang.T("De ronding sluit niet; dat kan nog niet.", "The rounding does not close; that is not possible yet."));
            var start = verts[E.Start];
            var centre = r.Centre(start);
            newFaces.Add(new BrepFace(new BrepCylinder(centre, r.Dir, (r.TA(start) - centre).Normalized(), r.R), r.S < 0, [loop]));
        }
        // the corner pieces: their arcs, the other way round from the roundings (and T)
        foreach (var (surface, reversed, arcs) in patches)
        {
            var parts = arcs.Select(x => new BrepCoedge(x, !used[x])).ToList();
            var loop = new List<BrepCoedge> { parts[0] };
            parts.RemoveAt(0);
            while (parts.Count > 0)
            {
                int pi = parts.FindIndex(x => From(x) == To(loop[^1]));
                if (pi < 0) throw new PushPullException(Lang.T("De bolhoek sluit niet; dat kan nog niet.", "The ball corner does not close; that is not possible yet."));
                loop.Add(parts[pi]); parts.RemoveAt(pi);
            }
            newFaces.Add(new BrepFace(surface, reversed, [loop]));
        }

        var outFaces = faces.Select((loops, i) => body.Faces[i] with { Loops = loops.Select(l => (IReadOnlyList<BrepCoedge>)l).ToList() }).Concat(newFaces).ToList();
        // (the old corners are no longer used: out, the others renumbered)
        var usedV = edges.Where(x => x.Start >= 0).SelectMany(x => new[] { x.Start, x.End }).Distinct().Order().ToList();
        var renumber = usedV.Select((v, i) => (v, i)).ToDictionary(x => x.v, x => x.i);
        var result = new BrepBody
        {
            Vertices = usedV.Select(v => verts[v]).ToList(),
            Edges = edges.Select(x => x.Start < 0 ? x : x with { Start = renumber[x.Start], End = renumber[x.End] }).ToList(),
            Faces = outFaces,
        };
        var newIdx = Enumerable.Range(body.Faces.Count, newFaces.Count).ToList();
        var changed = set.SelectMany(e => new[] { info[e].A, info[e].B }).Concat(capArcs.Select(x => x.Cap))
            .Concat(movedEnds.SelectMany(m => facesOf[m.Edge])).Concat(newIdx).Distinct().ToList();
        Check(result, changed);
        if (SignedVolume(result) is double vol && vol <= 0) throw new PushPullException(Lang.T("Zo klopt de vorm niet meer (binnenstebuiten).", "That way the shape is no longer right (inside out)."));
        return (result, newIdx);
    }
}
