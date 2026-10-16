using System.Numerics;
using System.Windows;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf.SharpDX;

namespace PushPullMe;

/// <summary>
/// Lines between pairs of points (each two points one line, as HelixToolkit.Wpf's LinesVisual3D took them), drawn by the
/// graphics card at a fixed thickness in pixels; not picked by clicks (those are for the faces).
/// </summary>
public sealed class LinesVisual3D : LineGeometryModel3D
{
    private Point3DCollection _points = new();

    public LinesVisual3D()
    {
        IsHitTestVisible = false;
        // (a little in front in the depth test: an edge lies exactly on a round face, whose triangles bulge in front of
        // it here and there and hid it)
        DepthBias = -60;
        SlopeScaledDepthBias = -1.5;
    }

    /// <summary>How thick, as the window gave it for WPF 3D (whose lines came out thinner than DirectX draws the same
    /// number: scaled to look as before).</summary>
    public new double Thickness
    {
        get => base.Thickness / ThicknessScale;
        set => base.Thickness = value * ThicknessScale;
    }

    private const double ThicknessScale = 0.45;

    public Point3DCollection Points
    {
        get => _points;
        set
        {
            _points = value;
            var pos = new HelixToolkit.Vector3Collection(value.Count);
            foreach (var p in value) pos.Add(new Vector3((float)p.X, (float)p.Y, (float)p.Z));
            var idx = new HelixToolkit.IntCollection(pos.Count);
            for (int i = 0; i + 1 < pos.Count; i += 2) { idx.Add(i); idx.Add(i + 1); }
            Geometry = new HelixToolkit.SharpDX.LineGeometry3D { Positions = pos, Indices = idx };
        }
    }
}

/// <summary>A ray in the view: where it starts and which way it goes.</summary>
public sealed record Ray3D(Point3D Origin, Vector3D Direction);

/// <summary>Between the screen and the model (as HelixToolkit.Wpf's Viewport3DHelper did it for WPF 3D).</summary>
public static class Viewport3DHelper
{
    /// <summary>
    /// The camera as a frame, in double precision: the eye, its right, up and forward directions, and how far the view
    /// reaches sideways and up per unit forward. (The viewer's own matrices are single precision: a click lay up to half
    /// a millimetre beside where the point was drawn, enough to miss a face just inside its edge.)
    /// </summary>
    private static (Point3D Eye, Vector3D R, Vector3D U, Vector3D F, double Tx, double Ty, double W, double H)? Frame(Viewport3DX view)
    {
        if (view.Camera is not HelixToolkit.Wpf.SharpDX.PerspectiveCamera cam || view.ActualWidth < 1 || view.ActualHeight < 1) return null;
        var f = cam.LookDirection; f.Normalize();
        var r = Vector3D.CrossProduct(f, cam.UpDirection);
        if (r.Length < 1e-12) return null;
        r.Normalize();
        var u = Vector3D.CrossProduct(r, f);
        double w = view.ActualWidth, h = view.ActualHeight, t = Math.Tan(cam.FieldOfView * Math.PI / 360);
        // (the angle of view across or up the view: whichever the viewer draws with, found once)
        _horizontal ??= Horizontal(view, cam.Position, r, u, f, t, w, h);
        return _horizontal == true ? (cam.Position, r, u, f, t, t * h / w, w, h) : (cam.Position, r, u, f, t * w / h, t, w, h);
    }

    private static bool? _horizontal;

    /// <summary>How far the view reaches sideways and up per unit forward (null without a perspective camera).</summary>
    public static (double Tx, double Ty)? Reach(Viewport3DX view) => Frame(view) is { } c ? (c.Tx, c.Ty) : null;

    private static bool Horizontal(Viewport3DX view, Point3D eye, Vector3D r, Vector3D u, Vector3D f, double t, double w, double h)
    {
        var p = eye + f * 100 + r * 20 + u * 10;
        var drawn = view.Project(p);
        Point At(double tx, double ty) => new(w / 2 * (1 + 20 / (100 * tx)), h / 2 * (1 - 10 / (100 * ty)));
        return (At(t, t * h / w) - drawn).Length <= (At(t * w / h, t) - drawn).Length;
    }

    /// <summary>Where a point of the model is on the screen (pixels in the view).</summary>
    public static Point Point3DtoPoint2D(Viewport3DX view, Point3D p)
    {
        if (Frame(view) is not { } c) return view.Project(p);
        var v = p - c.Eye; double z = Vector3D.DotProduct(v, c.F);
        if (Math.Abs(z) < 1e-12) z = 1e-12;
        return new Point(c.W / 2 * (1 + Vector3D.DotProduct(v, c.R) / (z * c.Tx)), c.H / 2 * (1 - Vector3D.DotProduct(v, c.U) / (z * c.Ty)));
    }

    /// <summary>The ray from the eye through a point of the screen (exactly the way back of <see cref="Point3DtoPoint2D"/>).</summary>
    public static Ray3D? GetRay(Viewport3DX view, Point pos)
    {
        if (Frame(view) is not { } c)
        {
            var r = view.UnProject(pos);
            var rd = new Vector3D(r.Direction.X, r.Direction.Y, r.Direction.Z);
            return rd.Length < 1e-12 ? null : new Ray3D(new Point3D(r.Position.X, r.Position.Y, r.Position.Z), rd);
        }
        var d = c.F + c.R * ((2 * pos.X / c.W - 1) * c.Tx) + c.U * ((1 - 2 * pos.Y / c.H) * c.Ty);
        d.Normalize();
        return new Ray3D(c.Eye, d);
    }

    /// <summary>From the model to the screen in one matrix (the perspective divide done by Transform).</summary>
    public static Matrix3D GetTotalTransform(Viewport3DX view) => view.GetScreenViewProjectionMatrix3D();
}

/// <summary>The names the window used on HelixToolkit.Wpf's viewport, on the DirectX 11 one.</summary>
public static class ViewportMembers
{
    extension(Viewport3DX view)
    {
        /// <summary>The view itself (WPF 3D had a separate Viewport3D inside the control).</summary>
        public Viewport3DX Viewport => view;

        /// <summary>What is shown in it.</summary>
        public ObservableElement3DCollection Children => view.Items;
    }
}
