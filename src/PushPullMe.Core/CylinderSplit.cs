using RsDocx.Core.Brep;
using RsDocx.Core.Stl;

namespace PushPullMe.Core;

/// <summary>
/// Cutting a face on a cylinder in two along one of its lines (parallel to the axis). Push/Pull uses it for a rounding
/// that runs on past a quarter (two quarters of one cylinder that became one face, a half-round end): only the part up
/// to where the surface faces across the direction of movement can move along; there it is cut.
/// </summary>
public static class CylinderSplit
{
    /// <summary>
    /// Face <paramref name="g"/> (on a cylinder, one loop of lines, circles and ellipses) cut along the cylinder's line
    /// in radial direction <paramref name="t"/> (across the axis). The part with edge <paramref name="keep"/> keeps the
    /// face's index; the other part is a new face at the end. Null when the line does not cross the face in two points.
    /// </summary>
    public static (BrepBody Body, int NewFace)? Split(BrepBody body, int g, Vec3 t, int keep)
    {
        var G = body.Faces[g];
        if (G.Surface is not BrepCylinder cy || G.Loops.Count != 1) return null;
        var axis = cy.Axis.Normalized();
        t = (t - axis * t.Dot(axis)).Normalized();
        var m = axis.Cross(t);
        // on the cutting line's side of the axis: (p - O)·m = 0 with (p - O)·t > 0
        double F(Vec3 p) => (p - cy.Origin).Dot(m);
        bool Front(Vec3 p) => (p - cy.Origin).Dot(t) > 0;

        var verts = body.Vertices.ToList(); var edges = body.Edges.ToList();
        var loops = body.Faces.Select(f => f.Loops.Select(l => l.ToList()).ToList()).ToList();
        var cut = new List<int>();   // the vertices the line runs through
        var tol = 1e-9;
        foreach (var c in body.Faces[g].Loops[0].ToList())
        {
            var e = edges[c.Edge];
            if (e.Start < 0) return null;
            foreach (int v in new[] { e.Start, e.End })
                if (Math.Abs(F(verts[v])) < tol && Front(verts[v]) && !cut.Contains(v)) cut.Add(v);
            if (e.Curve is not (BrepLine or BrepCircle or BrepEllipse)) continue;
            // a crossing inside the edge: where F changes sign along it
            const int samples = 64;
            var tmp = new BrepBody { Vertices = verts, Edges = edges, Faces = [] };
            double? at = null;
            var fs = Enumerable.Range(0, samples + 1).Select(i => F(FaceGeometry.EdgePoint(tmp, e, (double)i / samples)!.Value)).ToArray();
            for (int i = 1; i < samples && at == null; i++)
                if (Math.Abs(fs[i]) < tol) at = (double)i / samples;   // right on a sample
            for (int i = 0; i < samples && at == null; i++)
            {
                double s0 = (double)i / samples, s1 = (double)(i + 1) / samples;
                double f0 = fs[i], f1 = fs[i + 1];
                if (Math.Abs(f0) < tol || Math.Abs(f1) < tol || Math.Sign(f0) == Math.Sign(f1)) continue;
                for (int k = 0; k < 60; k++)
                {
                    double sm = (s0 + s1) / 2, fm = F(FaceGeometry.EdgePoint(tmp, e, sm)!.Value);
                    if (Math.Sign(fm) == Math.Sign(f0)) { s0 = sm; f0 = fm; } else s1 = sm;
                }
                at = (s0 + s1) / 2;
            }
            if (at is not { } sa) continue;
            var p = FaceGeometry.EdgePoint(tmp, e, sa)!.Value;
            if (!Front(p) || (p - verts[e.Start]).Length < 1e-7 || (p - verts[e.End]).Length < 1e-7) continue;
            // the edge cut in two at p, in every face that runs along it
            int nv = verts.Count; verts.Add(p);
            int e1 = c.Edge, e2 = edges.Count;
            edges[e1] = e with { End = nv };
            edges.Add(e with { Start = nv });
            foreach (var fl in loops)
                for (int li = 0; li < fl.Count; li++)
                    if (fl[li].Any(x => x.Edge == e1))
                        fl[li] = fl[li].SelectMany(x => x.Edge != e1 ? [x]
                            : x.Forward ? new[] { new BrepCoedge(e1, true), new BrepCoedge(e2, true) } : [new BrepCoedge(e2, false), new BrepCoedge(e1, false)]).ToList();
            if (keep == e1 && !loops[g][0].Any(x => x.Edge == e1)) keep = e2;
            cut.Add(nv);
        }
        if (cut.Count != 2) return null;

        var loop = loops[g][0];
        int From(BrepCoedge c) => c.Forward ? edges[c.Edge].Start : edges[c.Edge].End;
        int ia = loop.FindIndex(c => From(c) == cut[0]), ib = loop.FindIndex(c => From(c) == cut[1]);
        if (ia < 0 || ib < 0) return null;
        // (the two points must not be the ends of one edge of the loop: then there is nothing to cut)
        if (loop.Any(c => edges[c.Edge] is var x && ((x.Start == cut[0] && x.End == cut[1]) || (x.Start == cut[1] && x.End == cut[0])))) return null;
        int ne = edges.Count; edges.Add(new BrepEdge(cut[0], cut[1], new BrepLine()));
        List<BrepCoedge> Run(int from, int to) { var r = new List<BrepCoedge>(); for (int i = from; i != to; i = (i + 1) % loop.Count) r.Add(loop[i]); return r; }
        var a = Run(ia, ib); a.Add(new BrepCoedge(ne, false));
        var b = Run(ib, ia); b.Add(new BrepCoedge(ne, true));
        var (mine, other) = a.Any(c => c.Edge == keep) ? (a, b) : (b, a);
        var faces = body.Faces.Select((f, i) => i == g ? f with { Loops = [mine] }
            : f with { Loops = loops[i].Select(l => (IReadOnlyList<BrepCoedge>)l).ToList() }).ToList();
        faces.Add(G with { Loops = [other] });
        return (new BrepBody { Vertices = verts, Edges = edges, Faces = faces }, faces.Count - 1);
    }
}
