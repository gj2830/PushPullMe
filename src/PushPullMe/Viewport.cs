using System.Numerics;
using System.Windows;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf.SharpDX;
using PushPullMe.Editor;
using RsDocx.Core.Stl;

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

/// <summary>Between the screen and the model (as HelixToolkit.Wpf's Viewport3DHelper did it for WPF 3D): the sums are the
/// editor's (<see cref="ViewFrame"/>), the camera and the view's size are this viewer's.</summary>
public static class Viewport3DHelper
{
    /// <summary>The camera as a frame (null without a perspective camera or a size).</summary>
    public static ViewFrame? Frame(Viewport3DX view)
    {
        if (view.Camera is not HelixToolkit.Wpf.SharpDX.PerspectiveCamera cam || view.ActualWidth < 1 || view.ActualHeight < 1) return null;
        var eye = V(cam.Position); var look = V(cam.LookDirection); var up = V(cam.UpDirection);
        // (the angle of view across or up the view: whichever the viewer draws with, found once)
        _horizontal ??= Horizontal(view, eye, look, up, cam.FieldOfView);
        return ViewFrame.Of(eye, look, up, cam.FieldOfView, _horizontal == true, view.ActualWidth, view.ActualHeight);
    }

    private static bool? _horizontal;

    /// <summary>Whether the viewer's angle of view is across the view (else up it); null before it can be told.</summary>
    public static bool? FieldOfViewAcross(Viewport3DX view) { Frame(view); return _horizontal; }

    private static Vec3 V(Point3D p) => new(p.X, p.Y, p.Z);
    private static Vec3 V(Vector3D v) => new(v.X, v.Y, v.Z);

    /// <summary>The camera as it is now.</summary>
    public static CameraPose? Pose(Viewport3DX view) =>
        view.Camera is HelixToolkit.Wpf.SharpDX.ProjectionCamera c ? new CameraPose(V(c.Position), V(c.LookDirection), V(c.UpDirection)) : null;

    /// <summary>How far the view reaches sideways and up per unit forward (null without a perspective camera).</summary>
    public static (double Tx, double Ty)? Reach(Viewport3DX view) => Frame(view) is { } c ? (c.Tx, c.Ty) : null;

    private static bool? Horizontal(Viewport3DX view, Vec3 eye, Vec3 look, Vec3 up, double fov)
    {
        double w = view.ActualWidth, h = view.ActualHeight;
        if (ViewFrame.Of(eye, look, up, fov, true, w, h) is not { } across || ViewFrame.Of(eye, look, up, fov, false, w, h) is not { } upward) return null;
        var p = eye + across.Forward * 100 + across.Right * 20 + across.Up * 10;
        var drawn = view.Project(new Point3D(p.X, p.Y, p.Z));
        double Off(ScreenPoint s) => (new Point(s.X, s.Y) - drawn).Length;
        return Off(across.Project(p)) <= Off(upward.Project(p));
    }

    /// <summary>Where a point of the model is on the screen (pixels in the view).</summary>
    public static Point Point3DtoPoint2D(Viewport3DX view, Point3D p)
    {
        if (Frame(view) is not { } c) return view.Project(p);
        var s = c.Project(V(p));
        return new Point(s.X, s.Y);
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
        var ray = c.Ray(new ScreenPoint(pos.X, pos.Y));
        return new Ray3D(new Point3D(ray.Origin.X, ray.Origin.Y, ray.Origin.Z), new Vector3D(ray.Direction.X, ray.Direction.Y, ray.Direction.Z));
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
