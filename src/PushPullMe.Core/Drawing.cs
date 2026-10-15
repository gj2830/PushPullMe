using RsDocx.Core.Brep;
using RsDocx.Core.Stl;

namespace PushPullMe.Core;

/// <summary>
/// Drawing on a planar face, as in SketchUp: a rectangle inside it becomes a face of its own (with a hole for it in the
/// face around it), a line from edge to edge divides the face in two. Push/Pull then makes a rectangle a block or a
/// pocket, and half a face a step.
/// </summary>
public static class Drawing
{
    /// <summary>What is being drawn (named in the messages).</summary>
    public enum Shape { Shape, Rectangle, Arc, Pie, NewEdge }

    /// <summary>Its name in the language chosen.</summary>
    private static string Name(Shape what) => what switch
    {
        Shape.Rectangle => Lang.T("rechthoek", "rectangle"),
        Shape.Arc => Lang.T("boog", "arc"),
        Shape.Pie => Lang.T("taartpunt", "pie"),
        Shape.NewEdge => Lang.T("nieuwe rand", "new edge"),
        _ => Lang.T("vorm", "shape"),
    };

    /// <summary>Distance (metres) within which a point counts as on an edge or a vertex.</summary>
    public const double SnapTol = 1e-6;

    /// <summary>
    /// The rectangle with corners <paramref name="a"/> and <paramref name="c"/> (opposite; on the face's plane) and its
    /// sides along <paramref name="u"/> and the normal × u, as a face of its own. It must lie inside the face, clear of
    /// its edges. Returns the new body and the index of the new face (the old face keeps its index).
    /// </summary>
    public static (BrepBody Body, int Face) Rectangle(BrepBody body, int face, Vec3 a, Vec3 c, Vec3 u)
    {
        var F = Planar(body, face, out var n);
        u = (u - n * u.Dot(n)).Normalized();
        var v = n.Cross(u);
        var d = c - a;
        double du = d.Dot(u), dv = d.Dot(v);
        if (Math.Abs(du) < SnapTol || Math.Abs(dv) < SnapTol) throw new PushPullException(Lang.T("Een rechthoek moet een breedte en een hoogte hebben.", "A rectangle must have a width and a height."));
        return Polygon(body, face, [a, a + u * du, a + u * du + v * dv, a + v * dv], Shape.Rectangle);
    }

    /// <summary>
    /// A closed polygon of straight sides through <paramref name="corners"/> (on the face's plane, in either order), as
    /// a face of its own; the face around it gets a hole for it. It must lie inside the face, clear of its edges, and not
    /// cross itself. Returns the new body and the index of the new face.
    /// </summary>
    public static (BrepBody Body, int Face) Polygon(BrepBody body, int face, IReadOnlyList<Vec3> corners, Shape what = Shape.Shape)
    {
        var F = Planar(body, face, out var n);
        var pts = corners.ToList();
        if (pts.Count < 3) throw new PushPullException(Lang.T("Een vlak heeft minstens drie hoeken.", "A face has at least three corners."));
        if (Math.Abs(FaceGeometry.Area(pts, n)) < SnapTol * SnapTol) throw new PushPullException(Lang.T($"Een {Name(what)} moet een oppervlakte hebben.", $"A {Name(what)} must have an area."));
        // counter-clockwise around the face's normal
        if (FaceGeometry.Area(pts, n) < 0) pts.Reverse();
        int k = pts.Count;

        var (x, y) = Axes(n);
        var loops = F.Loops.Select(l => FaceGeometry.LoopPoints(body, l).Select(q => (q.Dot(x), q.Dot(y))).ToList()).ToList();
        var poly = pts.Select(q => (q.Dot(x), q.Dot(y))).ToList();
        foreach (var q in poly)
            if (!Inside(loops, q)) throw new PushPullException(Lang.T($"De {Name(what)} moet helemaal binnen het vlak liggen.", $"The {Name(what)} must lie wholly inside the face."));
        foreach (var l in loops)
            for (int i = 0; i < l.Count; i++)
                for (int j = 0; j < k; j++)
                    if (SegmentDistance(l[i], l[(i + 1) % l.Count], poly[j], poly[(j + 1) % k]) < SnapTol)
                        throw new PushPullException(Lang.T($"De {Name(what)} raakt een rand van het vlak; teken hem iets kleiner (dat kan nog niet).", $"The {Name(what)} touches an edge of the face; draw it a little smaller (that is not possible yet)."));
        for (int i = 0; i < k; i++)
            for (int j = i + 2; j < k; j++)
                if (!(i == 0 && j == k - 1) && SegmentDistance(poly[i], poly[(i + 1) % k], poly[j], poly[(j + 1) % k]) < SnapTol)
                    throw new PushPullException(Lang.T($"De {Name(what)} kruist zichzelf.", $"The {Name(what)} crosses itself."));
        // a hole of the face inside the polygon goes to the new face (they do not touch: checked above)
        var inside = Enumerable.Range(1, loops.Count - 1).Where(i => Inside([poly], loops[i][0])).ToHashSet();

        var verts = body.Vertices.ToList(); var edges = body.Edges.ToList();
        int v0 = verts.Count; verts.AddRange(pts);
        int e0 = edges.Count;
        for (int i = 0; i < k; i++) edges.Add(new BrepEdge(v0 + i, v0 + (i + 1) % k, new BrepLine()));
        var outer = Enumerable.Range(0, k).Select(i => new BrepCoedge(e0 + i, true)).ToList();
        var hole = Enumerable.Range(0, k).Reverse().Select(i => new BrepCoedge(e0 + i, false)).ToList();
        var faces = body.Faces.ToList();
        faces[face] = F with { Loops = F.Loops.Where((_, i) => !inside.Contains(i)).Append(hole).ToList() };
        faces.Add(new BrepFace(F.Surface, F.Reversed, new[] { (IReadOnlyList<BrepCoedge>)outer }.Concat(F.Loops.Where((_, i) => inside.Contains(i))).ToList()));
        return (new BrepBody { Vertices = verts, Edges = edges, Faces = faces }, faces.Count - 1);
    }

    /// <summary>
    /// The circle round <paramref name="centre"/> (on the face's plane) with <paramref name="radius"/>, as a face of its
    /// own (one ring edge; the face around it gets a hole for it). It must lie inside the face, clear of its edges.
    /// Returns the new body and the index of the new face.
    /// </summary>
    public static (BrepBody Body, int Face) Circle(BrepBody body, int face, Vec3 centre, double radius)
    {
        var F = Planar(body, face, out var n);
        if (radius < SnapTol) throw new PushPullException(Lang.T("Een cirkel moet een straal hebben.", "A circle must have a radius."));
        var plane = (BrepPlane)F.Surface;
        centre -= n * (centre - plane.Origin).Dot(n);
        var x = plane.XAxis - n * plane.XAxis.Dot(n);
        x = x.Length > 0.5 ? x.Normalized() : Axes(n).X;
        var circle = new BrepCircle(centre, n, x, radius);
        var (ax, ay) = Axes(n);
        var loops = F.Loops.Select(l => FaceGeometry.LoopPoints(body, l).Select(p => (p.Dot(ax), p.Dot(ay))).ToList()).ToList();
        var ring = FaceGeometry.EdgePoints(body, new BrepEdge(-1, -1, circle)).Select(p => (p.Dot(ax), p.Dot(ay))).ToList();
        foreach (var p in ring)
            if (!Inside(loops, p)) throw new PushPullException(Lang.T("De cirkel moet helemaal binnen het vlak liggen.", "The circle must lie wholly inside the face."));
        // clear of every edge of the face (the points of the ring are at most a sagitta inside the true circle)
        var c2 = (centre.Dot(ax), centre.Dot(ay));
        // a hole of the face inside the circle goes to the new face; it must stay clear of the circle
        var within = Enumerable.Range(1, loops.Count - 1).Where(i => Inside([ring], loops[i][0])).ToHashSet();
        foreach (int i in within)
            if (loops[i].Any(q => Math.Sqrt((q.Item1 - c2.Item1) * (q.Item1 - c2.Item1) + (q.Item2 - c2.Item2) * (q.Item2 - c2.Item2)) > radius - SnapTol))
                throw new PushPullException(Lang.T("De cirkel raakt een rand van het vlak; teken hem iets kleiner (dat kan nog niet).", "The circle touches an edge of the face; draw it a little smaller (that is not possible yet)."));
        foreach (var l in loops.Where((_, i) => !within.Contains(i)))
            for (int i = 0; i < l.Count; i++)
            {
                var (s0, s1) = (l[i], l[(i + 1) % l.Count]);
                double dx = s1.Item1 - s0.Item1, dy = s1.Item2 - s0.Item2, len2 = dx * dx + dy * dy;
                double t = len2 == 0 ? 0 : Math.Clamp(((c2.Item1 - s0.Item1) * dx + (c2.Item2 - s0.Item2) * dy) / len2, 0, 1);
                double ex = s0.Item1 + dx * t - c2.Item1, ey = s0.Item2 + dy * t - c2.Item2;
                if (Math.Sqrt(ex * ex + ey * ey) < radius + SnapTol)
                    throw new PushPullException(Lang.T("De cirkel raakt een rand van het vlak; teken hem iets kleiner (dat kan nog niet).", "The circle touches an edge of the face; draw it a little smaller (that is not possible yet)."));
            }
        var edges = body.Edges.ToList();
        int e = edges.Count; edges.Add(new BrepEdge(-1, -1, circle));
        var faces = body.Faces.ToList();
        faces[face] = F with { Loops = F.Loops.Where((_, i) => !within.Contains(i)).Append(new[] { new BrepCoedge(e, false) }).ToList() };
        faces.Add(new BrepFace(F.Surface, F.Reversed, new[] { (IReadOnlyList<BrepCoedge>)new[] { new BrepCoedge(e, true) } }.Concat(F.Loops.Where((_, i) => within.Contains(i))).ToList()));
        return (new BrepBody { Vertices = body.Vertices, Edges = edges, Faces = faces }, faces.Count - 1);
    }

    /// <summary>
    /// The outer edge of a planar face moved inward by <paramref name="distance"/> (as SketchUp's Offset): the corners of
    /// the new outline (when the edge is straight all round), or the smaller circle (a face round one circle). Throws when
    /// it cannot be done (arcs among straight edges, or so far in that the shape turns over).
    /// </summary>
    public static (List<Vec3>? Corners, BrepCircle? Circle) OffsetOutline(BrepBody body, int face, double distance)
    {
        var F = Planar(body, face, out var n);
        if (distance < SnapTol) throw new PushPullException(Lang.T("Offset gaat naar binnen: de afstand moet groter dan 0 zijn.", "Offset goes inwards: the distance must be more than 0."));
        var outer = F.Loops[0];
        if (outer.Count == 1 && body.Edges[outer[0].Edge].Curve is BrepCircle c)
        {
            if (c.Radius - distance < SnapTol) throw new PushPullException(Lang.T("Zo ver naar binnen past niet: de cirkel verdwijnt.", "That far inwards does not fit: the circle disappears."));
            return (null, c with { Radius = c.Radius - distance });
        }
        if (outer.Any(co => body.Edges[co.Edge].Curve is not BrepLine))
            throw new PushPullException(Lang.T("Offset van een vlak met bogen kan nog niet (alleen rechte randen, of één cirkel).", "Offset of a face with arcs is not possible yet (only straight edges, or one circle)."));
        // the corners (not the points where an edge was only divided)
        var all = FaceGeometry.LoopPoints(body, outer);
        var poly = new List<Vec3>();
        for (int i = 0; i < all.Count; i++)
        {
            Vec3 a = all[(i - 1 + all.Count) % all.Count], b = all[i], cc = all[(i + 1) % all.Count];
            if ((b - a).Normalized().Cross((cc - b).Normalized()).Length < 1e-9) continue;
            poly.Add(b);
        }
        int k = poly.Count;
        if (k < 3) throw new PushPullException(Lang.T("Dit vlak heeft geen hoeken om naar binnen te schuiven.", "This face has no corners to move inwards."));
        var result = new List<Vec3>();
        for (int i = 0; i < k; i++)
        {
            Vec3 a = poly[(i - 1 + k) % k], b = poly[i], cc = poly[(i + 1) % k];
            Vec3 d1 = (b - a).Normalized(), d2 = (cc - b).Normalized();
            // inside is to the left of the outer loop (counter-clockwise around the normal): where both edges, moved in, meet
            Vec3 in1 = n.Cross(d1), in2 = n.Cross(d2);
            double den = d1.Cross(d2).Dot(n);
            result.Add(b + in1 * distance + d1 * (((in2 - in1) * distance).Cross(d2).Dot(n) / den));
        }
        for (int i = 0; i < k; i++)
            if ((result[(i + 1) % k] - result[i]).Dot(poly[(i + 1) % k] - poly[i]) <= SnapTol * SnapTol)
                throw new PushPullException(Lang.T("Zo ver naar binnen past de vorm niet meer: hij klapt om.", "That far inwards the shape no longer fits: it folds over."));
        return (result, null);
    }

    /// <summary>
    /// SketchUp's Offset: a new edge inside the face, <paramref name="distance"/> from its outer edge all round; the part
    /// inside becomes a face of its own (holes inside go with it), the strip round it keeps the face's number. Returns the
    /// new body and the inner face.
    /// </summary>
    public static (BrepBody Body, int Face) Offset(BrepBody body, int face, double distance)
    {
        var (corners, circle) = OffsetOutline(body, face, distance);
        return circle is { } c ? Circle(body, face, c.Centre, c.Radius) : Polygon(body, face, corners!, Shape.NewEdge);
    }

    /// <summary>
    /// A line from <paramref name="p"/> to <paramref name="q"/>, both on the outer edge of the face (on a straight edge
    /// or a vertex), dividing it in two. Returns the new body and the index of the new face: the part to the left of p → q
    /// (seen along the face's normal); the other part keeps the face's index.
    /// </summary>
    public static (BrepBody Body, int Face) Line(BrepBody body, int face, Vec3 p, Vec3 q) => Path(body, face, [p, q]);

    /// <summary>
    /// Lines drawn one after the other through <paramref name="pts"/> (as SketchUp's line tool): closed (the last point
    /// back on the first) they are a polygon of their own (<see cref="Polygon"/>); from the outer edge of the face to it
    /// again (on straight edges or vertices, the points between inside the face) they divide the face in two: the part to
    /// the left of the path (seen along the face's normal) is the new face. Returns the new body and that face.
    /// <paramref name="curves"/>: per piece (from point i to the next) a line, or a circle running counter-clockwise round
    /// its normal from the one to the other (an arc); none: all lines.
    /// </summary>
    public static (BrepBody Body, int Face) Path(BrepBody body, int face, IReadOnlyList<Vec3> pts, IReadOnlyList<BrepCurve>? curves = null)
    {
        var F = Planar(body, face, out var n);
        if (pts.Count < 2) throw new PushPullException(Lang.T("Een lijn moet een lengte hebben.", "A line must have a length."));
        for (int i = 0; i + 1 < pts.Count; i++)
            if ((pts[i + 1] - pts[i]).Length < SnapTol) throw new PushPullException(Lang.T("Een lijn moet een lengte hebben.", "A line must have a length."));
        var cv = curves?.ToList() ?? Enumerable.Range(0, pts.Count - 1).Select(_ => (BrepCurve)new BrepLine()).ToList();
        if (cv.Count != pts.Count - 1) throw new ArgumentException("One curve per piece required");
        bool straight = cv.All(c => c is BrepLine);
        if (pts.Count >= (straight ? 4 : 3) && (pts[^1] - pts[0]).Length < SnapTol)
            return straight ? Polygon(body, face, pts.Take(pts.Count - 1).ToList()) : ClosedInside(body, face, pts.Take(pts.Count - 1).ToList(), cv, Shape.Shape);
        // pieces along the outer edge at the start or the end (as SketchUp: they fall on the edge that is there): the
        // path starts where it leaves the edge, and ends where it reaches it
        bool OnOuter(Vec3 q) => NearestOnEdge(body, face, q, 0) is { } ne && (ne.Point - q).Length < SnapTol;
        var trimmed = pts.ToList();
        while (trimmed.Count > 2 && cv[0] is BrepLine && OnOuter(trimmed[1]) && OnOuter((trimmed[0] + trimmed[1]) * 0.5)) { trimmed.RemoveAt(0); cv.RemoveAt(0); }
        while (trimmed.Count > 2 && cv[^1] is BrepLine && OnOuter(trimmed[^2]) && OnOuter((trimmed[^1] + trimmed[^2]) * 0.5)) { trimmed.RemoveAt(trimmed.Count - 1); cv.RemoveAt(cv.Count - 1); }
        pts = trimmed;

        var verts = body.Vertices.ToList(); var edges = body.Edges.ToList();
        var faces = body.Faces.Select(f => f.Loops.Select(l => l.ToList()).ToList()).ToList();
        int vp = OnOuterEdge(verts, edges, faces, face, pts[0]), vq = OnOuterEdge(verts, edges, faces, face, pts[^1]);
        if (vp == vq) throw new PushPullException(Lang.T("Begin en eind van de lijn liggen op hetzelfde punt; zo deelt hij het vlak niet.", "Start and end of the line are the same point; that way it does not divide the face."));
        var outer = faces[face][0];
        int From(BrepCoedge c) => c.Forward ? edges[c.Edge].Start : edges[c.Edge].End;
        int ip = outer.FindIndex(c => From(c) == vp), iq = outer.FindIndex(c => From(c) == vq);
        if (ip < 0 || iq < 0) throw new PushPullException(Lang.T("Begin en eind van de lijn moeten op de buitenrand van het vlak liggen.", "Start and end of the line must lie on the outer edge of the face."));
        if (pts.Count == 2 && cv[0] is BrepLine && outer.Any(c => edges[c.Edge] is { Curve: BrepLine } e && ((e.Start == vp && e.End == vq) || (e.Start == vq && e.End == vp))))
            throw new PushPullException(Lang.T("Die lijn ligt op een rand van het vlak.", "That line lies on an edge of the face."));
        // points between on the edge of a hole: the path touches it there (they share that vertex)
        var touch = new Dictionary<int, int>();
        for (int i = 1; i + 1 < pts.Count; i++)
            for (int li = 1; li < faces[face].Count; li++)
                if (OnLoopEdge(verts, edges, faces, face, li, pts[i]) is int tv) { touch[i] = tv; break; }
        outer = faces[face][0];
        ip = outer.FindIndex(c => From(c) == vp); iq = outer.FindIndex(c => From(c) == vq);

        // the path must run inside the face: the points between inside, every piece's middle inside, nothing crossing
        // (an arc by points along it)
        var (x, y) = Axes(n);
        (double, double) P2(Vec3 pt) => (pt.Dot(x), pt.Dot(y));
        var loops2 = faces[face].Select(l => LoopPoints(verts, edges, l).Select(P2).ToList()).ToList();
        var dense = new List<Vec3> { verts[vp] };
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            var to = i + 2 == pts.Count ? verts[vq] : pts[i + 1];
            if (cv[i] is BrepCircle arc) dense.AddRange(ArcPoints(arc, i == 0 ? verts[vp] : pts[i], to, 24).Skip(1));
            else dense.Add(to);
        }
        var path2 = dense.Select(P2).ToList();
        var touched2 = touch.Keys.Select(i => P2(pts[i])).ToList();
        bool Touched((double, double) q) => touched2.Any(t => Near(t, q));
        for (int i = 1; i + 1 < path2.Count; i++)
            if (!Touched(path2[i]) && !Inside(loops2, path2[i])) throw new PushPullException(Lang.T("De lijn loopt buiten het vlak.", "The line runs outside the face."));
        for (int i = 0; i + 1 < path2.Count; i++)
        {
            var (a2, b2) = (path2[i], path2[i + 1]);
            if (!Inside(loops2, ((a2.Item1 + b2.Item1) / 2, (a2.Item2 + b2.Item2) / 2))) throw new PushPullException(Lang.T("De lijn loopt buiten het vlak om.", "The line runs round outside the face."));
            foreach (var l in loops2)
                for (int j = 0; j < l.Count; j++)
                {
                    var s0 = l[j]; var s1 = l[(j + 1) % l.Count];
                    // (the ends of the path lie on the edge: touching there is fine)
                    bool atEnd = (i == 0 && (Near(s0, a2) || Near(s1, a2))) || (i + 2 == path2.Count && (Near(s0, b2) || Near(s1, b2)))
                        || (Touched(a2) && SegmentDistance(s0, s1, a2, a2) < SnapTol) || (Touched(b2) && SegmentDistance(s0, s1, b2, b2) < SnapTol);
                    if (!atEnd && SegmentDistance(s0, s1, a2, b2) < SnapTol) throw new PushPullException(Lang.T("De lijn raakt of kruist een andere rand van het vlak.", "The line touches or crosses another edge of the face."));
                }
            for (int j = i + 2; j + 1 < path2.Count; j++)
                if (SegmentDistance(a2, b2, path2[j], path2[j + 1]) < SnapTol) throw new PushPullException(Lang.T("De lijn kruist zichzelf.", "The line crosses itself."));
        }

        // the path's vertices and edges, p → … → q
        var chain = new List<int> { vp };
        for (int i = 1; i + 1 < pts.Count; i++)
            if (touch.TryGetValue(i, out int tv)) chain.Add(tv); else { chain.Add(verts.Count); verts.Add(pts[i]); }
        chain.Add(vq);
        var forward = new List<BrepCoedge>();
        for (int i = 0; i + 1 < chain.Count; i++) { forward.Add(new BrepCoedge(edges.Count, true)); edges.Add(new BrepEdge(chain[i], chain[i + 1], cv[i])); }
        var backward = Enumerable.Reverse(forward).Select(c => new BrepCoedge(c.Edge, false)).ToList();

        int m = outer.Count;
        List<BrepCoedge> Run(int from, int to) { var r = new List<BrepCoedge>(); for (int i = from; i != to; i = (i + 1) % m) r.Add(outer[i]); return r; }
        // p → … → q along the outer loop, back over the path: the part to the right of the path; and the other part
        var right = Run(ip, iq); right.AddRange(backward);
        var left = Run(iq, ip); left.AddRange(forward);
        var keep = new List<List<BrepCoedge>> { right };
        var add = new List<List<BrepCoedge>> { left };
        var leftPts = LoopPoints(verts, edges, left).Select(P2).ToList();
        foreach (var (hole, i) in faces[face].Skip(1).Select((h, i) => (h, i + 1)))
        {
            // (by a point of the hole's edge away from where the path touches it)
            var hp = LoopPoints(verts, edges, hole).Select(P2).ToList();
            int k = Enumerable.Range(0, hp.Count).FirstOrDefault(k => !Touched(hp[k]) && !Touched(hp[(k + 1) % hp.Count]));
            var probe = ((hp[k].Item1 + hp[(k + 1) % hp.Count].Item1) / 2, (hp[k].Item2 + hp[(k + 1) % hp.Count].Item2) / 2);
            (Inside([leftPts], probe) ? add : keep).Add(hole);
        }
        // a hole the path touches: taken into the outer loop of its face at that vertex (the loop runs round it there and
        // on), as loops may not share a vertex
        foreach (var loops in new[] { keep, add })
            for (int h = loops.Count - 1; h >= 1; h--)
            {
                int at = loops[h].FindIndex(c => touch.ContainsValue(From(c)));
                if (at < 0) continue;
                int v = From(loops[h][at]);
                var round = loops[h].Skip(at).Concat(loops[h].Take(at)).ToList();
                int To(BrepCoedge c) => c.Forward ? edges[c.Edge].End : edges[c.Edge].Start;
                int into = loops[0].FindIndex(c => To(c) == v);
                loops[0].InsertRange(into + 1, round);
                loops.RemoveAt(h);
            }
        faces[face] = keep;
        faces.Add(add);
        var outFaces = faces.Select((loops, i) => new BrepFace(i < body.Faces.Count ? body.Faces[i].Surface : F.Surface, i < body.Faces.Count ? body.Faces[i].Reversed : F.Reversed,
            loops.Select(l => (IReadOnlyList<BrepCoedge>)l).ToList())).ToList();
        return (new BrepBody { Vertices = verts, Edges = edges, Faces = outFaces }, outFaces.Count - 1);
    }

    /// <summary>
    /// The circle through <paramref name="a"/>, <paramref name="through"/> and <paramref name="b"/> (on a plane with normal
    /// <paramref name="n"/>), as an arc from a to b by way of the third point: its normal is n or −n so that it runs
    /// counter-clockwise from a past the third point to b. Throws when the three lie on one line.
    /// </summary>
    public static BrepCircle ArcThrough(Vec3 a, Vec3 through, Vec3 b, Vec3 n)
    {
        var (x, y) = Axes(n);
        double ax = a.Dot(x), ay = a.Dot(y), mx = through.Dot(x), my = through.Dot(y), bx = b.Dot(x), by = b.Dot(y);
        double d = 2 * (ax * (my - by) + mx * (by - ay) + bx * (ay - my));
        if (Math.Abs(d) < 1e-18) throw new PushPullException(Lang.T("Zo is het geen boog maar een rechte lijn; geef de boog wat bolling.", "That way it is not an arc but a straight line; give the arc some bulge."));
        double a2 = ax * ax + ay * ay, m2 = mx * mx + my * my, b2 = bx * bx + by * by;
        double cx = (a2 * (my - by) + m2 * (by - ay) + b2 * (ay - my)) / d;
        double cy = (a2 * (bx - mx) + m2 * (ax - bx) + b2 * (mx - ax)) / d;
        // (back in space: from a, in the plane)
        var centre = a + x * (cx - ax) + y * (cy - ay);
        double r = (a - centre).Length;
        var ex = (a - centre).Normalized();
        double Angle(Vec3 p, Vec3 normal) { var ey = normal.Cross(ex); var v = p - centre; double t = Math.Atan2(v.Dot(ey), v.Dot(ex)); return t < 0 ? t + 2 * Math.PI : t; }
        var nn = n.Normalized();
        if (Angle(through, nn) > Angle(b, nn)) nn = nn * -1;
        return new BrepCircle(centre, nn, ex, r);
    }

    /// <summary>Points along an arc from a to b (the circle's way round), both ends included.</summary>
    public static List<Vec3> ArcPoints(BrepCircle c, Vec3 a, Vec3 b, int count = 48)
    {
        var ex = (a - c.Centre).Normalized(); var ey = c.Normal.Normalized().Cross(ex);
        var v = b - c.Centre;
        double end = Math.Atan2(v.Dot(ey), v.Dot(ex)); if (end <= 1e-12) end += 2 * Math.PI;
        var pts = Enumerable.Range(0, count + 1).Select(k => c.Centre + (ex * Math.Cos(end * k / count) + ey * Math.Sin(end * k / count)) * c.Radius).ToList();
        pts[0] = a; pts[^1] = b;
        return pts;
    }

    /// <summary>
    /// An arc drawn on a planar face from <paramref name="a"/> to <paramref name="b"/> by way of
    /// <paramref name="through"/> (as SketchUp's arc), one exact circle edge: from edge to edge of the face's outer loop it
    /// divides the face in two (the part to the left of a → b, seen along the normal, is the new face; to round a corner
    /// off); wholly inside the face it closes with the straight line back (its chord) into a face of its own. Returns the
    /// new body and the new face.
    /// </summary>
    public static (BrepBody Body, int Face) Arc(BrepBody body, int face, Vec3 a, Vec3 b, Vec3 through)
    {
        var F = Planar(body, face, out var n);
        var o = ((BrepPlane)F.Surface).Origin;
        Vec3 OnPlane(Vec3 p) => p - n * (p - o).Dot(n);
        a = OnPlane(a); b = OnPlane(b); through = OnPlane(through);
        if ((b - a).Length < SnapTol) throw new PushPullException(Lang.T("Een boog moet een begin en een eind hebben.", "An arc must have a start and an end."));
        var circle = ArcThrough(a, through, b, n);
        var samples = ArcPoints(circle, a, b);
        var (x, y) = Axes(n);
        (double, double) P2(Vec3 p) => (p.Dot(x), p.Dot(y));
        bool OnOuter(Vec3 q) => NearestOnEdge(body, face, q, 0) is { } ne && (ne.Point - q).Length < SnapTol;
        var verts = body.Vertices.ToList(); var edges = body.Edges.ToList();
        var faces = body.Faces.Select(f => f.Loops.Select(l => l.ToList()).ToList()).ToList();
        var loops2 = faces[face].Select(l => LoopPoints(verts, edges, l).Select(P2).ToList()).ToList();
        var s2 = samples.Select(P2).ToList();
        // inside the face, crossing nothing (only its ends may touch the edge)
        for (int i = 1; i + 1 < s2.Count; i++)
            if (!Inside(loops2, s2[i])) throw new PushPullException(Lang.T("De boog loopt buiten het vlak.", "The arc runs outside the face."));
        bool ends = OnOuter(a) && OnOuter(b);
        for (int i = 0; i + 1 < s2.Count; i++)
            foreach (var l in loops2)
                for (int j = 0; j < l.Count; j++)
                {
                    var (q0, q1) = (l[j], l[(j + 1) % l.Count]);
                    bool atEnd = ends && ((i == 0 && (Near(q0, s2[0]) || Near(q1, s2[0]) || SegmentDistance(q0, q1, s2[0], s2[0]) < SnapTol))
                        || (i + 2 == s2.Count && (Near(q0, s2[^1]) || Near(q1, s2[^1]) || SegmentDistance(q0, q1, s2[^1], s2[^1]) < SnapTol)));
                    if (!atEnd && SegmentDistance(q0, q1, s2[i], s2[i + 1]) < SnapTol) throw new PushPullException(Lang.T("De boog raakt of kruist een rand van het vlak.", "The arc touches or crosses an edge of the face."));
                }

        if (ends)
        {
            // from edge to edge: the face divided along the arc
            int vp = OnOuterEdge(verts, edges, faces, face, a), vq = OnOuterEdge(verts, edges, faces, face, b);
            if (vp == vq) throw new PushPullException(Lang.T("Begin en eind van de boog liggen op hetzelfde punt.", "Start and end of the arc are the same point."));
            var outer = faces[face][0];
            int From(BrepCoedge c) => c.Forward ? edges[c.Edge].Start : edges[c.Edge].End;
            int ip = outer.FindIndex(c => From(c) == vp), iq = outer.FindIndex(c => From(c) == vq);
            if (ip < 0 || iq < 0) throw new PushPullException(Lang.T("Begin en eind van de boog moeten op de buitenrand van het vlak liggen.", "Start and end of the arc must lie on the outer edge of the face."));
            int e = edges.Count; edges.Add(new BrepEdge(vp, vq, circle));
            int m = outer.Count;
            List<BrepCoedge> Run(int from, int to) { var r = new List<BrepCoedge>(); for (int i = from; i != to; i = (i + 1) % m) r.Add(outer[i]); return r; }
            var right = Run(ip, iq); right.Add(new BrepCoedge(e, false));
            var left = Run(iq, ip); left.Add(new BrepCoedge(e, true));
            var keep = new List<List<BrepCoedge>> { right };
            var add = new List<List<BrepCoedge>> { left };
            var leftPts = LoopPoints(verts, edges, left).Select(P2).ToList();
            foreach (var (hole, i) in faces[face].Skip(1).Select((h, i) => (h, i + 1)))
                (Inside([leftPts], loops2[i][0]) ? add : keep).Add(hole);
            faces[face] = keep;
            faces.Add(add);
        }
        else
        {
            if (OnOuter(a) || OnOuter(b))
                throw new PushPullException(Lang.T("Een boog loopt van rand tot rand (dan deelt hij het vlak), of ligt helemaal binnen het vlak; niet half.", "An arc runs from edge to edge (then it divides the face), or lies wholly inside the face; not half."));
            // inside: closed with the straight line back (its chord)
            return ClosedInside(body, face, [a, b], [circle, new BrepLine()], Shape.Arc);
        }
        var outFaces = faces.Select((loops, i) => new BrepFace(i < body.Faces.Count ? body.Faces[i].Surface : F.Surface, i < body.Faces.Count ? body.Faces[i].Reversed : F.Reversed,
            loops.Select(l => (IReadOnlyList<BrepCoedge>)l).ToList())).ToList();
        return (new BrepBody { Vertices = verts, Edges = edges, Faces = outFaces }, outFaces.Count - 1);
    }

    /// <summary>
    /// A pie (as SketchUp's): the arc from <paramref name="a"/> to <paramref name="b"/> by way of
    /// <paramref name="through"/> round <paramref name="centre"/>, closed by its two radii, wholly inside the face, as a face
    /// of its own. Returns the new body and the new face.
    /// </summary>
    public static (BrepBody Body, int Face) Pie(BrepBody body, int face, Vec3 centre, Vec3 a, Vec3 b, Vec3 through)
    {
        var F = Planar(body, face, out var n);
        var o = ((BrepPlane)F.Surface).Origin;
        Vec3 OnPlane(Vec3 p) => p - n * (p - o).Dot(n);
        centre = OnPlane(centre); a = OnPlane(a); b = OnPlane(b); through = OnPlane(through);
        var circle = ArcThrough(a, through, b, n);
        if ((circle.Centre - centre).Length > Math.Max(SnapTol, circle.Radius * 1e-6)) throw new PushPullException(Lang.T("De taartpunt heeft zijn middelpunt niet in het midden van de boog.", "The pie does not have its centre in the middle of the arc."));
        return ClosedInside(body, face, [centre, a, b], [new BrepLine(), circle with { Centre = centre }, new BrepLine()], Shape.Pie);
    }

    /// <summary>
    /// A closed shape of straight edges and arcs wholly inside a planar face, as a face of its own (the face around it gets
    /// a hole for it; holes of the face inside it go with it): corners <paramref name="pts"/>, edge i from corner i to the
    /// next with <paramref name="curves"/>[i] (a line, or a circle running from it to the next). It must stay clear of the
    /// face's edges and not cross itself.
    /// </summary>
    private static (BrepBody Body, int Face) ClosedInside(BrepBody body, int face, IReadOnlyList<Vec3> pts, IReadOnlyList<BrepCurve> curves, Shape what)
    {
        var F = Planar(body, face, out var n);
        var (x, y) = Axes(n);
        (double, double) P2(Vec3 p) => (p.Dot(x), p.Dot(y));
        var verts = body.Vertices.ToList(); var edges = body.Edges.ToList();
        var faces = body.Faces.Select(f => f.Loops.Select(l => l.ToList()).ToList()).ToList();
        var loops2 = faces[face].Select(l => LoopPoints(verts, edges, l).Select(P2).ToList()).ToList();
        int k = pts.Count;
        // its outline, the arcs as points
        var outline = new List<Vec3>();
        for (int i = 0; i < k; i++)
        {
            var (p, q) = (pts[i], pts[(i + 1) % k]);
            if (curves[i] is BrepCircle c) outline.AddRange(ArcPoints(c, p, q).Take(48));
            else outline.Add(p);
        }
        var o2 = outline.Select(P2).ToList();
        foreach (var q in o2)
            if (!Inside(loops2, q)) throw new PushPullException(Lang.T($"De {Name(what)} moet helemaal binnen het vlak liggen.", $"The {Name(what)} must lie wholly inside the face."));
        for (int i = 0; i < o2.Count; i++)
        {
            var (s0, s1) = (o2[i], o2[(i + 1) % o2.Count]);
            foreach (var l in loops2)
                for (int j = 0; j < l.Count; j++)
                    if (SegmentDistance(l[j], l[(j + 1) % l.Count], s0, s1) < SnapTol) throw new PushPullException(Lang.T($"De {Name(what)} raakt een rand van het vlak; teken hem iets kleiner (dat kan nog niet).", $"The {Name(what)} touches an edge of the face; draw it a little smaller (that is not possible yet)."));
            for (int j = i + 2; j < o2.Count; j++)
                if (!(i == 0 && j == o2.Count - 1) && SegmentDistance(s0, s1, o2[j], o2[(j + 1) % o2.Count]) < SnapTol) throw new PushPullException(Lang.T($"De {Name(what)} kruist zichzelf.", $"The {Name(what)} crosses itself."));
        }
        int v0 = verts.Count; verts.AddRange(pts);
        int e0 = edges.Count;
        for (int i = 0; i < k; i++) edges.Add(new BrepEdge(v0 + i, v0 + (i + 1) % k, curves[i]));
        // counter-clockwise round the normal for the new face, the other way round as a hole in the old one
        double area = 0;
        for (int i = 0; i < o2.Count; i++) { var (p, q) = (o2[i], o2[(i + 1) % o2.Count]); area += p.Item1 * q.Item2 - q.Item1 * p.Item2; }
        var loop = Enumerable.Range(0, k).Select(i => new BrepCoedge(e0 + i, true)).ToList();
        var rev = Enumerable.Range(0, k).Reverse().Select(i => new BrepCoedge(e0 + i, false)).ToList();
        var (outerLoop, holeLoop) = area > 0 ? (loop, rev) : (rev, loop);
        var within = Enumerable.Range(1, loops2.Count - 1).Where(i => Inside([o2], loops2[i][0])).ToHashSet();
        var add = new List<List<BrepCoedge>> { outerLoop };
        add.AddRange(faces[face].Where((_, i) => within.Contains(i)));
        faces[face] = faces[face].Where((_, i) => !within.Contains(i)).Append(holeLoop).ToList();
        faces.Add(add);
        var outFaces = faces.Select((loops, i) => new BrepFace(i < body.Faces.Count ? body.Faces[i].Surface : F.Surface, i < body.Faces.Count ? body.Faces[i].Reversed : F.Reversed,
            loops.Select(l => (IReadOnlyList<BrepCoedge>)l).ToList())).ToList();
        return (new BrepBody { Vertices = verts, Edges = edges, Faces = outFaces }, outFaces.Count - 1);
    }

    /// <summary>Whether p lies on the edge of a hole of one of the faces.</summary>
    public static bool OnHoleEdge(BrepBody body, IEnumerable<int> faces, Vec3 p) =>
        faces.Any(f => body.Faces[f].Loops.Skip(1).Any(l =>
        {
            var pts = FaceGeometry.LoopPoints(body, l);
            for (int i = 0; i < pts.Count; i++)
            {
                Vec3 a = pts[i], ab = pts[(i + 1) % pts.Count] - a;
                double t = Math.Clamp((p - a).Dot(ab) / Math.Max(ab.Dot(ab), 1e-30), 0, 1);
                if ((a + ab * t - p).Length < SnapTol) return true;
            }
            return false;
        }));

    /// <summary>The planar faces of the body in the same plane as <paramref name="face"/> (facing the same way), itself
    /// first: a line drawn on it may run across all of them.</summary>
    public static List<int> CoplanarFaces(BrepBody body, int face)
    {
        var F = Planar(body, face, out var n);
        var o = ((BrepPlane)F.Surface).Origin;
        var result = new List<int> { face };
        for (int i = 0; i < body.Faces.Count; i++)
            if (i != face && body.Faces[i] is { Surface: BrepPlane pl } G && G.Loops.Count > 0
                && FaceGeometry.Normal(G).Dot(n) > 1 - 1e-9 && Math.Abs((pl.Origin - o).Dot(n)) < SnapTol)
                result.Add(i);
        return result;
    }

    /// <summary>
    /// <see cref="NearestOnEdge"/> over several faces: a vertex or middle within <paramref name="snapToPoint"/> on any of
    /// them first, otherwise the nearest point on any of their edges.
    /// </summary>
    public static (Vec3 Point, Snap Kind)? NearestOnEdges(BrepBody body, IEnumerable<int> faces, Vec3 p, double snapToPoint)
    {
        var all = faces.Select(f => NearestOnEdge(body, f, p, snapToPoint)).Where(x => x != null).Select(x => x!.Value).ToList();
        if (all.Count == 0) return null;
        var points = all.Where(x => x.Kind != Snap.OnEdge).ToList();
        return (points.Count > 0 ? points : all).MinBy(x => (x.Point - p).Length);
    }

    /// <summary>
    /// The first point beyond <paramref name="start"/> where the ray along <paramref name="dir"/> meets a straight edge
    /// of any of these faces' outer loops; or null.
    /// </summary>
    public static Vec3? RayToEdges(BrepBody body, IEnumerable<int> faces, Vec3 start, Vec3 dir) =>
        faces.Select(f => RayToEdge(body, f, start, dir)).Where(q => q != null).Select(q => q!.Value).OrderBy(q => (q - start).Length).Cast<Vec3?>().FirstOrDefault();

    /// <summary>
    /// A line through <paramref name="pts"/> on the plane of <paramref name="face"/>, cut where it meets the edges of the
    /// faces in that plane (<see cref="CoplanarFaces"/>): the pieces from edge to edge (each inside one face, to divide it
    /// with <see cref="Path"/>), without the pieces that run along an edge. A closed line that meets no edge stays whole
    /// (a shape of its own). As in SketchUp, where a line divides every face it crosses.
    /// </summary>
    public static List<List<Vec3>> Runs(BrepBody body, int face, IReadOnlyList<Vec3> pts)
    {
        Planar(body, face, out var n);
        if (pts.Count < 2) throw new PushPullException(Lang.T("Een lijn moet een lengte hebben.", "A line must have a length."));
        var (x, y) = Axes(n);
        (double X, double Y) P2(Vec3 q) => (q.Dot(x), q.Dot(y));
        var segs = CoplanarFaces(body, face).SelectMany(f => body.Faces[f].Loops.SelectMany(l => l)).Select(c => body.Edges[c.Edge])
            .Where(e => e.Curve is BrepLine && e.Start >= 0).Select(e => (A: P2(body.Vertices[e.Start]), B: P2(body.Vertices[e.End]))).ToList();
        bool OnEdge(Vec3 q)
        {
            var q2 = P2(q);
            return segs.Any(sg => SegmentDistance(sg.A, sg.B, q2, q2) < SnapTol);
        }

        bool closed = pts.Count >= 4 && (pts[^1] - pts[0]).Length < SnapTol;
        var line = closed ? pts.Take(pts.Count - 1).ToList() : pts.ToList();
        int segCount = closed ? line.Count : line.Count - 1;
        // every point where the line meets an edge, in order
        var refined = new List<Vec3>();
        for (int i = 0; i < segCount; i++)
        {
            Vec3 a = line[i], b = line[(i + 1) % line.Count], d = b - a;
            double len = d.Length;
            if (len < SnapTol) continue;
            var ts = new List<double> { 0 };
            var a2 = P2(a);
            var d2 = (X: d.Dot(x), Y: d.Dot(y));
            foreach (var (p2, q2) in segs)
            {
                var e2 = (X: q2.X - p2.X, Y: q2.Y - p2.Y);
                // the edge's ends on the line
                foreach (var v in new[] { p2, q2 })
                {
                    double t = ((v.X - a2.X) * d2.X + (v.Y - a2.Y) * d2.Y) / (len * len);
                    if (t > 0 && t < 1 && Math.Abs((v.X - a2.X) * d2.Y - (v.Y - a2.Y) * d2.X) / len < SnapTol) ts.Add(t);
                }
                // the line's crossing with the edge
                double den = d2.X * e2.Y - d2.Y * e2.X;
                double elen = Math.Sqrt(e2.X * e2.X + e2.Y * e2.Y);
                if (Math.Abs(den) < 1e-12 * len * elen) continue;
                double tt = ((p2.X - a2.X) * e2.Y - (p2.Y - a2.Y) * e2.X) / den, ss = ((p2.X - a2.X) * d2.Y - (p2.Y - a2.Y) * d2.X) / den;
                if (tt > 0 && tt < 1 && ss >= -SnapTol / elen && ss <= 1 + SnapTol / elen) ts.Add(tt);
            }
            ts.Sort();
            double lastT = double.NegativeInfinity;
            foreach (var t in ts)
            {
                if ((t - lastT) * len < SnapTol || (1 - t) * len < SnapTol) continue;
                refined.Add(a + d * t); lastT = t;
            }
        }
        if (!closed) refined.Add(line[^1]);
        var on = refined.Select(OnEdge).ToList();
        if (closed)
        {
            int first = on.IndexOf(true);
            if (first < 0) return [pts.ToList()];
            refined = refined.Skip(first).Concat(refined.Take(first)).Append(refined[first]).ToList();
            on = refined.Select(OnEdge).ToList();
        }
        else if (!on[0] || !on[^1])
            throw new PushPullException(Lang.T("Begin en eind van de lijn moeten op een rand liggen (of sluit de lijn op het beginpunt).", "Start and end of the line must lie on an edge (or close the line on its start point)."));
        var runs = new List<List<Vec3>>();
        var run = new List<Vec3> { refined[0] };
        for (int i = 1; i < refined.Count; i++)
        {
            run.Add(refined[i]);
            if (!on[i]) continue;
            if (!(run.Count == 2 && OnEdge((run[0] + run[1]) * 0.5))) runs.Add(run);
            run = [refined[i]];
        }
        // where it only touches a hole of the face it runs over (on both sides the same face, not on its outer edge): one run
        for (int k = 0; k + 1 < runs.Count; k++)
        {
            var q = runs[k][^1];
            if ((runs[k + 1][0] - q).Length > SnapTol || FaceOfRun(body, face, runs[k]) is not int g || FaceOfRun(body, face, runs[k + 1]) != g) continue;
            if (NearestOnEdge(body, g, q, 0) is { } ne && (ne.Point - q).Length < SnapTol) continue;
            runs[k] = runs[k].Concat(runs[k + 1].Skip(1)).ToList();
            runs.RemoveAt(k + 1); k--;
        }
        return runs;
    }

    /// <summary>The face (in the plane of <paramref name="face"/>) a piece of a line from <see cref="Runs"/> lies in: the
    /// one around the middle of its first part; or null (outside them all).</summary>
    public static int? FaceOfRun(BrepBody body, int face, IReadOnlyList<Vec3> run)
    {
        var mid = (run[0] + run[1]) * 0.5;
        foreach (var f in CoplanarFaces(body, face))
            if (Contains(body, f, mid)) return f;
        return null;
    }

    /// <summary>
    /// The nearest point on the face's outer edge to <paramref name="p"/> (on its plane): a vertex, the middle of a
    /// straight edge, or a point on one; with what kind of point it is. Null when the face has no outer edge to snap to.
    /// </summary>
    public static (Vec3 Point, Snap Kind)? NearestOnEdge(BrepBody body, int face, Vec3 p, double snapToPoint)
    {
        var F = body.Faces[face];
        if (F.Surface is not BrepPlane || F.Loops.Count == 0) return null;
        var lines = F.Loops[0].Select(c => body.Edges[c.Edge]).Where(e => e.Curve is BrepLine)
            .Select(e => (A: body.Vertices[e.Start], B: body.Vertices[e.End])).ToList();
        if (lines.Count == 0) return null;
        // a vertex or a middle close by first
        var points = lines.SelectMany(l => new[] { (l.A, Snap.Endpoint), (l.B, Snap.Endpoint), ((l.A + l.B) * 0.5, Snap.Midpoint) })
            .Where(x => (x.Item1 - p).Length < snapToPoint).ToList();
        if (points.Count > 0) return points.MinBy(x => (x.Item1 - p).Length);
        return lines.Select(l =>
        {
            var ab = l.B - l.A;
            double t = Math.Clamp((p - l.A).Dot(ab) / ab.Dot(ab), 0, 1);
            return (l.A + ab * t, Snap.OnEdge);
        }).MinBy(x => (x.Item1 - p).Length);
    }

    /// <summary>Whether a point (on the face's plane) lies inside the face: inside its outer loop, outside its holes.</summary>
    public static bool Contains(BrepBody body, int face, Vec3 p)
    {
        var F = Planar(body, face, out var n);
        var (x, y) = Axes(n);
        var loops = F.Loops.Select(l => FaceGeometry.LoopPoints(body, l).Select(q => (q.Dot(x), q.Dot(y))).ToList()).ToList();
        return Inside(loops, (p.Dot(x), p.Dot(y)));
    }

    /// <summary>
    /// The first point beyond <paramref name="start"/> where the ray along <paramref name="dir"/> (in the face's plane)
    /// meets a straight edge of the face's outer loop; or null.
    /// </summary>
    public static Vec3? RayToEdge(BrepBody body, int face, Vec3 start, Vec3 dir)
    {
        var F = Planar(body, face, out var n);
        double best = double.MaxValue;
        foreach (var c in F.Loops[0])
        {
            var e = body.Edges[c.Edge];
            if (e.Curve is not BrepLine) continue;
            Vec3 a = body.Vertices[e.Start], ab = body.Vertices[e.End] - a;
            double den = dir.Cross(ab).Dot(n);
            if (Math.Abs(den) < 1e-15) continue;
            double t = (a - start).Cross(ab).Dot(n) / den, s = (a - start).Cross(dir).Dot(n) / den;
            if (t > SnapTol && s >= -1e-9 && s <= 1 + 1e-9) best = Math.Min(best, t);
        }
        return best == double.MaxValue ? null : start + dir * best;
    }

    /// <summary>The straight edge of the face's outer loop nearest to p: its end nearest to p (the corner) and its other
    /// end; or null.</summary>
    public static (Vec3 Corner, Vec3 Other)? EdgeAt(BrepBody body, int face, Vec3 p)
    {
        var F = body.Faces[face];
        if (F.Surface is not BrepPlane || F.Loops.Count == 0) return null;
        (Vec3, Vec3)? best = null; double bestD = double.MaxValue;
        foreach (var c in F.Loops[0])
        {
            var e = body.Edges[c.Edge];
            if (e.Curve is not BrepLine) continue;
            Vec3 a = body.Vertices[e.Start], b = body.Vertices[e.End], ab = b - a;
            double t = Math.Clamp((p - a).Dot(ab) / ab.Dot(ab), 0, 1);
            double d = (a + ab * t - p).Length;
            if (d < bestD) { bestD = d; best = t <= 0.5 ? (a, b) : (b, a); }
        }
        return best;
    }

    /// <summary>The corner of the face's outer loop nearest to p.</summary>
    public static Vec3 NearestCorner(BrepBody body, int face, Vec3 p)
    {
        var F = Planar(body, face, out _);
        return F.Loops[0].Select(c => body.Edges[c.Edge]).Where(e => e.Start >= 0).Select(e => body.Vertices[e.Start]).MinBy(v => (v - p).Length);
    }

    private static BrepFace Planar(BrepBody body, int face, out Vec3 n)
    {
        if (face < 0 || face >= body.Faces.Count) throw new ArgumentOutOfRangeException(nameof(face));
        var F = body.Faces[face];
        if (F.Surface is not BrepPlane) throw new PushPullException(Lang.T("Tekenen kan (nog) alleen op platte vlakken.", "Drawing only works on flat faces (for now)."));
        n = FaceGeometry.Normal(F);
        return F;
    }

    /// <summary>Two directions in the plane with normal n.</summary>
    private static (Vec3 X, Vec3 Y) Axes(Vec3 n)
    {
        var x = Math.Abs(n.X) < 0.9 ? new Vec3(1, 0, 0).Cross(n).Normalized() : new Vec3(0, 1, 0).Cross(n).Normalized();
        return (x, n.Cross(x));
    }

    /// <summary>The vertex at p on the outer loop of the face: an existing one, or a new one splitting the straight edge p
    /// lies on (in every face that runs along it).</summary>
    private static int OnOuterEdge(List<Vec3> verts, List<BrepEdge> edges, List<List<List<BrepCoedge>>> faces, int face, Vec3 p) =>
        OnLoopEdge(verts, edges, faces, face, 0, p) ?? throw new PushPullException(Lang.T("Begin en eind van de lijn moeten op een rechte rand van het vlak liggen.", "Start and end of the line must lie on a straight edge of the face."));

    /// <summary>The vertex of loop <paramref name="loop"/> of the face at p: one there, or made by cutting the straight
    /// edge p lies on (in every loop that uses it); null when p is not on that loop.</summary>
    private static int? OnLoopEdge(List<Vec3> verts, List<BrepEdge> edges, List<List<List<BrepCoedge>>> faces, int face, int loop, Vec3 p)
    {
        foreach (var c in faces[face][loop])
        {
            var e = edges[c.Edge];
            if (e.Start >= 0 && (verts[e.Start] - p).Length < SnapTol) return e.Start;
            if (e.End >= 0 && (verts[e.End] - p).Length < SnapTol) return e.End;
        }
        foreach (var c in faces[face][loop])
        {
            int ei = c.Edge; var e = edges[ei];
            if (e.Curve is not BrepLine) continue;
            Vec3 a = verts[e.Start], b = verts[e.End], ab = b - a;
            double t = (p - a).Dot(ab) / ab.Dot(ab);
            if (t <= 0 || t >= 1 || (a + ab * t - p).Length > SnapTol) continue;
            int nv = verts.Count; verts.Add(a + ab * t);
            int e1 = ei, e2 = edges.Count;
            edges[e1] = new BrepEdge(e.Start, nv, new BrepLine());
            edges.Add(new BrepEdge(nv, e.End, new BrepLine()));
            foreach (var loops in faces)
                for (int li = 0; li < loops.Count; li++)
                    if (loops[li].Any(x => x.Edge == ei))
                        loops[li] = loops[li].SelectMany(x => x.Edge != ei ? [x]
                            : x.Forward ? new[] { new BrepCoedge(e1, true), new BrepCoedge(e2, true) } : [new BrepCoedge(e2, false), new BrepCoedge(e1, false)]).ToList();
            return nv;
        }
        return null;
    }

    private static List<Vec3> LoopPoints(List<Vec3> verts, List<BrepEdge> edges, IReadOnlyList<BrepCoedge> loop) =>
        FaceGeometry.LoopPoints(new BrepBody { Vertices = verts, Edges = edges, Faces = [] }, loop);

    private static bool Near((double X, double Y) a, (double X, double Y) b) => Math.Abs(a.X - b.X) < SnapTol && Math.Abs(a.Y - b.Y) < SnapTol;

    /// <summary>Inside the first loop and outside the others (even-odd over all).</summary>
    private static bool Inside(List<List<(double X, double Y)>> loops, (double X, double Y) p)
    {
        bool inside = false;
        foreach (var l in loops)
            for (int i = 0, j = l.Count - 1; i < l.Count; j = i++)
                if ((l[i].Y > p.Y) != (l[j].Y > p.Y) && p.X < (l[j].X - l[i].X) * (p.Y - l[i].Y) / (l[j].Y - l[i].Y) + l[i].X)
                    inside = !inside;
        return inside;
    }

    /// <summary>The shortest distance between two segments (0 when they cross).</summary>
    private static double SegmentDistance((double X, double Y) a, (double X, double Y) b, (double X, double Y) c, (double X, double Y) d)
    {
        static double O((double X, double Y) p, (double X, double Y) q, (double X, double Y) r) => (q.X - p.X) * (r.Y - p.Y) - (q.Y - p.Y) * (r.X - p.X);
        double d1 = O(a, b, c), d2 = O(a, b, d), d3 = O(c, d, a), d4 = O(c, d, b);
        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0))) return 0;
        static double PS((double X, double Y) p, (double X, double Y) s, (double X, double Y) t)
        {
            double dx = t.X - s.X, dy = t.Y - s.Y, l = dx * dx + dy * dy;
            double k = l == 0 ? 0 : Math.Clamp(((p.X - s.X) * dx + (p.Y - s.Y) * dy) / l, 0, 1);
            double ex = s.X + k * dx - p.X, ey = s.Y + k * dy - p.Y;
            return Math.Sqrt(ex * ex + ey * ey);
        }
        return Math.Min(Math.Min(PS(a, c, d), PS(b, c, d)), Math.Min(PS(c, a, b), PS(d, a, b)));
    }
}
