using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using PushPullMe.Core;
using PushPullMe.Editor;
using RsDocx.Core.Stl;
using Silk.NET.OpenGL;

namespace PushPullMe.Desktop;

/// <summary>
/// The model in 3D through OpenGL (on Windows through ANGLE, on Linux GLX/EGL, on macOS OpenGL): the faces lit by a
/// light at the eye (the inside in the back colour), the edges, the ground grid and axes, a face highlighted. The mouse
/// as in the WPF window: middle (or right) button turns the view round the point under it, with Shift it pans, the wheel
/// zooms toward the mouse, a click picks a face. All the sums are the editor layer's (<see cref="ViewFrame"/>,
/// <see cref="CameraPose"/>, <see cref="Picking"/>), so a click lands on the triangle drawn under it.
/// </summary>
public sealed class ModelView : OpenGlControlBase
{
    private static readonly Vector4 FrontColor = Rgb(0xF3, 0xF0, 0xE8), BackColor = Rgb(0x8E, 0x9B, 0xB8), EdgeColor = Rgb(0x26, 0x2B, 0x36),
        HoverColor = Rgb(0xFA, 0xCC, 0x15) with { W = 0.4f }, SelectColor = Rgb(0x25, 0x63, 0xEB) with { W = 0.35f },
        GridColor = Rgb(0xD5, 0xDC, 0xE6), XColor = Rgb(0xE1, 0x1D, 0x48), YColor = Rgb(0x16, 0xA3, 0x4A);

    private static Vector4 Rgb(byte r, byte g, byte b) => new(r / 255f, g / 255f, b / 255f, 1);
    private static Vector4 Argb(uint c) => new((byte)(c >> 16) / 255f, (byte)(c >> 8) / 255f, (byte)c / 255f, 1);

    private SketchDocument? _doc;
    private Scene? _scene;
    private CameraPose _camera = new(new Vec3(-150, -200, 150), new Vec3(150, 200, -150), new Vec3(0, 0, 1));

    /// <summary>The face under the mouse and the one picked.</summary>
    private FaceRef? _hover, _selected;

    /// <summary>What a face is said to be when the mouse is on it or it is picked (null: nothing).</summary>
    public event Action<FaceRef?, FaceRef?>? FacesChanged;

    public SketchDocument? Document
    {
        get => _doc;
        set
        {
            _doc = value; _scene = value == null ? null : Scene.Build(value); _hover = _selected = null;
            _sceneDirty = _highlightDirty = true;
            ZoomAll();
        }
    }

    public FaceRef? Selected => _selected;
    public CameraPose Camera => _camera;

    // ------------------------------------------------------------------ the screen and the camera

    /// <summary>The camera as a frame over this control (sizes in device-independent pixels, as the mouse gives them).</summary>
    public ViewFrame? Frame => ViewFrame.Of(_camera.Position, _camera.Look, _camera.Up, 45, true, Bounds.Width, Bounds.Height);

    private void SetCamera(CameraPose? pose)
    {
        if (pose is not { IsUsable: true } p) return;
        _camera = p;
        RequestNextFrameRendering();
    }

    /// <summary>Everything in view, looking the way the camera looks.</summary>
    public void ZoomAll()
    {
        double w = Bounds.Width > 0 ? Bounds.Width : 1000, h = Bounds.Height > 0 ? Bounds.Height : 700, t = Math.Tan(45 * Math.PI / 360);
        SetCamera(_scene?.FitAll(_camera.IsUsable ? _camera.Look : null, t, t * h / w));
    }

    /// <summary>Turned round the middle of the view as the mouse would (pixels); for the test aid.</summary>
    public void Orbit(double dx, double dy) => SetCamera(_camera.Orbit(_camera.Target, dx, dy));

    /// <summary>The face under a point of the screen and where (millimetres).</summary>
    public (FaceRef Face, Vec3 At)? FaceAt(Point pos) =>
        _scene != null && Frame is { } f ? Picking.FaceAt(_scene.Picks, f.Ray(new ScreenPoint(pos.X, pos.Y))) : null;

    /// <summary>Where a point of the model (millimetres) is on the screen.</summary>
    public Point? ScreenOf(Vec3 p) => Frame is { } f ? new Point(f.Project(p).X, f.Project(p).Y) : null;

    // ------------------------------------------------------------------ the mouse

    private Point? _orbitFrom, _panFrom;
    private Vec3 _orbitPivot;
    private double _panDepth;

    public ModelView()
    {
        Focusable = true;
        // (a size change: drawn anew; the first one also puts the model in view)
        PropertyChanged += (_, e) => { if (e.Property == BoundsProperty) { if (e.OldValue is Rect { Width: 0 }) ZoomAll(); RequestNextFrameRendering(); } };
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        var pos = point.Position;
        var kind = point.Properties.PointerUpdateKind;
        if (kind is PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.RightButtonPressed)
        {
            var grab = FaceAt(pos)?.At ?? _camera.Target;
            if ((e.KeyModifiers & KeyModifiers.Shift) != 0) { _panDepth = Frame?.Depth(grab) ?? _camera.Look.Length; _panFrom = pos; }
            else { _orbitPivot = grab; _orbitFrom = pos; }
            e.Pointer.Capture(this);
            e.Handled = true;
        }
        else if (kind == PointerUpdateKind.LeftButtonPressed)
        {
            Pick(pos);
            e.Handled = true;
        }
    }

    /// <summary>The face under that point of the screen picked (nothing there: none).</summary>
    public void Pick(Point pos)
    {
        _selected = FaceAt(pos)?.Face;
        _highlightDirty = true;
        RequestNextFrameRendering();
        FacesChanged?.Invoke(_hover, _selected);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);
        if (_panFrom is { } pf)
        {
            if (Frame is { } frame) SetCamera(_camera.Pan(frame, _panDepth, pos.X - pf.X, pos.Y - pf.Y));
            _panFrom = pos;
            return;
        }
        if (_orbitFrom is { } of)
        {
            SetCamera(_camera.Orbit(_orbitPivot, pos.X - of.X, pos.Y - of.Y));
            _orbitFrom = pos;
            return;
        }
        var hover = FaceAt(pos)?.Face;
        if (hover == _hover) return;
        _hover = hover;
        _highlightDirty = true;
        RequestNextFrameRendering();
        FacesChanged?.Invoke(_hover, _selected);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_orbitFrom == null && _panFrom == null) return;
        _orbitFrom = _panFrom = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        e.Handled = true;
        var pos = e.GetPosition(this);
        if (Frame is not { } frame) return;
        // toward the model under the mouse, or else a point as far as the middle of the view
        var target = FaceAt(pos)?.At ?? frame.Ray(new ScreenPoint(pos.X, pos.Y)).At(_camera.Look.Length);
        SetCamera(_camera.Zoom(target, e.Delta.Y * 120));
    }

    // ------------------------------------------------------------------ drawing

    private GL? _gl;

    /// <summary>Which OpenGL drew it (for the log and the test aid).</summary>
    public string GlInfo { get; private set; } = "(not yet)";
    private uint _faceProgram, _lineProgram;
    private int _uMvp, _uLight, _uBack, _uUnlit, _uLineMvp;
    private bool _sceneDirty = true, _highlightDirty = true;

    /// <summary>A vertex array with its buffer and how many vertices are in it.</summary>
    private sealed class Batch { public uint Vao, Vbo; public int Count; }
    private Batch? _faces, _lines, _highlight;

    /// <summary>Called after a frame is drawn, with its pixels (for the test aid's snapshot); then cleared.</summary>
    public Action<int, int, byte[]>? AfterFrame { get; set; }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        _gl = GL.GetApi(gl.GetProcAddress);
        GlInfo = $"{GlVersion.Type} {GlVersion.Major}.{GlVersion.Minor}, {_gl.GetStringS(StringName.Renderer)}";
        Editor.SessionLog.Write("OpenGL: " + GlInfo);
        // (OpenGL ES 3 through ANGLE on Windows; desktop OpenGL 3.3 core and up elsewhere)
        string head = GlVersion.Type == GlProfileType.OpenGLES ? "#version 300 es\nprecision highp float;\n" : "#version 330 core\n";
        _faceProgram = Program(head + """
            layout(location = 0) in vec3 aPos;
            layout(location = 1) in vec3 aNormal;
            layout(location = 2) in vec4 aColor;
            uniform mat4 uMvp;
            out vec3 vNormal;
            out vec4 vColor;
            void main() { gl_Position = uMvp * vec4(aPos, 1.0); vNormal = aNormal; vColor = aColor; }
            """, head + """
            in vec3 vNormal;
            in vec4 vColor;
            uniform vec3 uLight;
            uniform vec4 uBack;
            uniform int uUnlit;
            out vec4 fragColor;
            void main()
            {
                if (uUnlit == 1) { fragColor = vColor; return; }
                // the inside (seen through a hole or a cut): the back colour, the normal turned round
                vec3 n = normalize(gl_FrontFacing ? vNormal : -vNormal);
                vec4 c = gl_FrontFacing ? vColor : uBack;
                float light = 0.55 + 0.45 * max(dot(n, -uLight), 0.0);
                fragColor = vec4(c.rgb * light, c.a);
            }
            """);
        _lineProgram = Program(head + """
            layout(location = 0) in vec3 aPos;
            layout(location = 2) in vec4 aColor;
            uniform mat4 uMvp;
            out vec4 vColor;
            void main() { gl_Position = uMvp * vec4(aPos, 1.0); vColor = aColor; }
            """, head + """
            in vec4 vColor;
            out vec4 fragColor;
            void main() { fragColor = vColor; }
            """);
        _uMvp = _gl.GetUniformLocation(_faceProgram, "uMvp");
        _uLight = _gl.GetUniformLocation(_faceProgram, "uLight");
        _uBack = _gl.GetUniformLocation(_faceProgram, "uBack");
        _uUnlit = _gl.GetUniformLocation(_faceProgram, "uUnlit");
        _uLineMvp = _gl.GetUniformLocation(_lineProgram, "uMvp");
        _faces = NewBatch(); _lines = NewBatch(); _highlight = NewBatch();
        _sceneDirty = _highlightDirty = true;
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        if (_gl == null) return;
        foreach (var b in new[] { _faces, _lines, _highlight })
            if (b != null) { _gl.DeleteBuffer(b.Vbo); _gl.DeleteVertexArray(b.Vao); }
        _gl.DeleteProgram(_faceProgram); _gl.DeleteProgram(_lineProgram);
        _faces = _lines = _highlight = null;
        _gl = null;
    }

    private uint Program(string vertex, string fragment)
    {
        var gl = _gl!;
        uint Shader(ShaderType type, string src)
        {
            uint s = gl.CreateShader(type);
            gl.ShaderSource(s, src);
            gl.CompileShader(s);
            gl.GetShader(s, ShaderParameterName.CompileStatus, out int ok);
            if (ok == 0) throw new InvalidOperationException($"{type}: {gl.GetShaderInfoLog(s)}");
            return s;
        }
        uint vs = Shader(ShaderType.VertexShader, vertex), fs = Shader(ShaderType.FragmentShader, fragment);
        uint p = gl.CreateProgram();
        gl.AttachShader(p, vs); gl.AttachShader(p, fs);
        gl.LinkProgram(p);
        gl.GetProgram(p, ProgramPropertyARB.LinkStatus, out int linked);
        if (linked == 0) throw new InvalidOperationException("link: " + gl.GetProgramInfoLog(p));
        gl.DeleteShader(vs); gl.DeleteShader(fs);
        return p;
    }

    /// <summary>A vertex: position, normal, colour (10 floats).</summary>
    private const int Stride = 10;

    private unsafe Batch NewBatch()
    {
        var gl = _gl!;
        var b = new Batch { Vao = gl.GenVertexArray(), Vbo = gl.GenBuffer() };
        gl.BindVertexArray(b.Vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, b.Vbo);
        gl.EnableVertexAttribArray(0); gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, Stride * sizeof(float), (void*)0);
        gl.EnableVertexAttribArray(1); gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, Stride * sizeof(float), (void*)(3 * sizeof(float)));
        gl.EnableVertexAttribArray(2); gl.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, Stride * sizeof(float), (void*)(6 * sizeof(float)));
        gl.BindVertexArray(0);
        return b;
    }

    private unsafe void Fill(Batch b, List<float> data)
    {
        var gl = _gl!;
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, b.Vbo);
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(data);
        fixed (float* p = span) gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(span.Length * sizeof(float)), p, BufferUsageARB.DynamicDraw);
        b.Count = data.Count / Stride;
    }

    private static void Add(List<float> d, Vector3 p, Vector3 n, Vector4 c)
    {
        d.Add(p.X); d.Add(p.Y); d.Add(p.Z); d.Add(n.X); d.Add(n.Y); d.Add(n.Z); d.Add(c.X); d.Add(c.Y); d.Add(c.Z); d.Add(c.W);
    }

    private void UploadScene()
    {
        var faces = new List<float>();
        var lines = new List<float>();
        if (_scene != null)
        {
            foreach (var m in _scene.Meshes)
            {
                var c = m.Color is { } col ? Argb(col) : FrontColor;
                for (int i = 0; i < m.Positions.Length; i++) Add(faces, m.Positions[i], m.Normals[i], c);
            }
            foreach (var p in _scene.Edges) Add(lines, p, default, EdgeColor);
        }
        // the ground: a grid of 10 mm round the model (or 200 mm round the origin), the red and green axes on it
        var box = _scene != null ? Picking.Bounds(_scene.Picks) : null;
        double size = box is { } bx ? Math.Max(200, 1.5 * Math.Max(bx.Max.X - bx.Min.X, bx.Max.Y - bx.Min.Y)) : 200;
        double step = Math.Pow(10, Math.Floor(Math.Log10(size / 4)));
        int n = (int)Math.Ceiling(size / step);
        float lim = (float)(n * step);
        for (int i = -n; i <= n; i++)
        {
            float v = (float)(i * step);
            if (i != 0)
            {
                Add(lines, new(v, -lim, 0), default, GridColor); Add(lines, new(v, lim, 0), default, GridColor);
                Add(lines, new(-lim, v, 0), default, GridColor); Add(lines, new(lim, v, 0), default, GridColor);
            }
        }
        Add(lines, new(-lim, 0, 0), default, XColor); Add(lines, new(lim, 0, 0), default, XColor);
        Add(lines, new(0, -lim, 0), default, YColor); Add(lines, new(0, lim, 0), default, YColor);
        Fill(_faces!, faces);
        Fill(_lines!, lines);
    }

    private void UploadHighlight()
    {
        var d = new List<float>();
        if (_doc != null)
        {
            void Face(FaceRef f, Vector4 c) { foreach (var p in Scene.FaceTriangles(_doc, f)) Add(d, p, default, c); }
            if (_selected is { } s) Face(s, SelectColor);
            if (_hover is { } h && h != _selected) Face(h, HoverColor);
        }
        Fill(_highlight!, d);
    }

    /// <summary>
    /// From the model to the clip space, exactly as <see cref="ViewFrame.Project"/> (x and y over the depth along the
    /// look, scaled by how far the view reaches): what is drawn and what is picked agree. For row vectors (v * M), as
    /// System.Numerics has them; given to GL as is, GLSL reads it as its transpose, which is M for column vectors.
    /// </summary>
    private static Matrix4x4 ClipMatrix(ViewFrame f, double near, double far)
    {
        double a = (far + near) / (far - near), b = -2 * far * near / (far - near);
        Vec3 r = f.Right * (1 / f.Tx), u = f.Up * (1 / f.Ty), fw = f.Forward;
        double er = -r.Dot(f.Eye), eu = -u.Dot(f.Eye), ef = -fw.Dot(f.Eye);
        return new Matrix4x4(
            (float)r.X, (float)u.X, (float)(a * fw.X), (float)fw.X,
            (float)r.Y, (float)u.Y, (float)(a * fw.Y), (float)fw.Y,
            (float)r.Z, (float)u.Z, (float)(a * fw.Z), (float)fw.Z,
            (float)er, (float)eu, (float)(a * ef + b), (float)ef);
    }

    protected override unsafe void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_gl == null || _faces == null || _lines == null || _highlight == null) return;
        var g = _gl;
        if (_sceneDirty) { UploadScene(); _sceneDirty = false; }
        if (_highlightDirty) { UploadHighlight(); _highlightDirty = false; }

        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        int w = Math.Max(1, (int)(Bounds.Width * scale)), h = Math.Max(1, (int)(Bounds.Height * scale));
        g.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
        g.Viewport(0, 0, (uint)w, (uint)h);
        g.ClearColor(0.941f, 0.957f, 0.976f, 1);
        g.ClearDepth(1);
        g.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        if (Frame is not { } frame) return;

        // what can be seen from just in front of the eye to far behind the model, as a fixed ratio (as the WPF window)
        double near = Math.Clamp(_camera.Look.Length / 100, 0.01, 50);
        var mvp = ClipMatrix(frame, near, near * 20000);
        var light = frame.Forward;

        g.Enable(EnableCap.DepthTest);
        g.DepthFunc(DepthFunction.Lequal);
        g.Disable(EnableCap.CullFace);
        g.Disable(EnableCap.Blend);

        // the faces, pushed back a little in the depth test so their edges lie in front of them
        g.UseProgram(_faceProgram);
        g.UniformMatrix4(_uMvp, 1, false, (float*)&mvp);
        g.Uniform3(_uLight, (float)light.X, (float)light.Y, (float)light.Z);
        g.Uniform4(_uBack, BackColor.X, BackColor.Y, BackColor.Z, BackColor.W);
        g.Uniform1(_uUnlit, 0);
        g.Enable(EnableCap.PolygonOffsetFill);
        g.PolygonOffset(1, 1);
        g.BindVertexArray(_faces.Vao);
        g.DrawArrays(PrimitiveType.Triangles, 0, (uint)_faces.Count);
        g.Disable(EnableCap.PolygonOffsetFill);

        // the highlight over them (see-through, unlit)
        if (_highlight.Count > 0)
        {
            g.Enable(EnableCap.Blend);
            g.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            g.DepthMask(false);
            g.Uniform1(_uUnlit, 1);
            g.BindVertexArray(_highlight.Vao);
            g.DrawArrays(PrimitiveType.Triangles, 0, (uint)_highlight.Count);
            g.DepthMask(true);
            g.Disable(EnableCap.Blend);
        }

        // the edges, the grid and the axes
        g.UseProgram(_lineProgram);
        g.UniformMatrix4(_uLineMvp, 1, false, (float*)&mvp);
        g.BindVertexArray(_lines.Vao);
        g.DrawArrays(PrimitiveType.Lines, 0, (uint)_lines.Count);
        g.BindVertexArray(0);

        if (AfterFrame is { } after && _scene != null)
        {
            AfterFrame = null;
            var pixels = new byte[w * h * 4];
            fixed (byte* p = pixels) g.ReadPixels(0, 0, (uint)w, (uint)h, PixelFormat.Rgba, PixelType.UnsignedByte, p);
            after(w, h, pixels);
        }
    }
}
