using System.Collections.Specialized;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Rendering;
using PushPullMe.Editor;
using PushPullMe.Editor.Session;
using Silk.NET.OpenGL;
using Color = PushPullMe.Editor.Session.Color;
using EditorButton = PushPullMe.Editor.Session.MouseButton;
using EditorMods = PushPullMe.Editor.Session.ModifierKeys;
using Point = Avalonia.Point;
using Rect = Avalonia.Rect;

namespace PushPullMe;

/// <summary>
/// The model in 3D through OpenGL (on Windows through ANGLE, on Linux GLX/EGL, on macOS OpenGL), as the editor describes
/// it: the faces lit by a light at the eye (the inside in the back colour; faded outside the open group), the edges, and
/// what the tools show beside it (lines of any thickness, see-through highlights, the work plane's handle on top). The
/// clip matrix comes straight from the editor's <see cref="ViewFrame"/>, so what is drawn and what is picked agree. The
/// mouse goes on to the editor; the right button turns the view as the middle one does (for a Mac without one).
/// </summary>
public sealed class ModelView : OpenGlControlBase, ICustomHitTest
{
    private EditorSession? _session;

    /// <summary>The editor this view shows and passes the mouse on to.</summary>
    public EditorSession? Session
    {
        get => _session;
        set
        {
            _session = value;
            if (value == null) return;
            value.SceneChanged += () => { _modelDirty = true; RequestNextFrameRendering(); };
            value.View.Children.CollectionChanged += (_, _) => { _overlayDirty = true; RequestNextFrameRendering(); };
            value.View.Camera.Changed += RequestNextFrameRendering;
            value.View.MouseCaptureChanged += on => _captured = on;
            SizeToEditor();
        }
    }

    /// <summary>The mouse reaches the whole view (it draws no background of its own, so would otherwise be seen as
    /// empty and passed through).</summary>
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    public ModelView()
    {
        Focusable = true;
        PropertyChanged += (_, e) => { if (e.Property == BoundsProperty) { SizeToEditor(); RequestNextFrameRendering(); } };
    }

    private void SizeToEditor()
    {
        if (_session == null || Bounds.Width < 1) return;
        _session.View.ActualWidth = Bounds.Width; _session.View.ActualHeight = Bounds.Height;
    }

    // ------------------------------------------------------------------ the mouse, on to the editor

    private bool _captured;

    private static EditorMods Mods(KeyModifiers m) =>
        ((m & KeyModifiers.Shift) != 0 ? EditorMods.Shift : 0) | ((m & KeyModifiers.Control) != 0 ? EditorMods.Control : 0)
        | ((m & KeyModifiers.Alt) != 0 ? EditorMods.Alt : 0) | ((m & KeyModifiers.Meta) != 0 ? EditorMods.Control : 0);

    private static PushPullMe.Editor.Session.Point P(Point p) => new(p.X, p.Y);

    private static EditorButton? Button(PointerUpdateKind k) => k switch
    {
        PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => EditorButton.Left,
        // (the right button turns the view too: a Mac mouse or trackpad has no middle one)
        PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => EditorButton.Middle,
        PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => EditorButton.Middle,
        _ => null,
    };

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_session == null) return;
        var point = e.GetCurrentPoint(this);
        if (Button(point.Properties.PointerUpdateKind) is not { } b) return;
        Focus();
        if (_session.MouseDown(b, P(point.Position), Mods(e.KeyModifiers), e.ClickCount)) e.Handled = true;
        if (_captured) e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _session?.MouseMove(P(e.GetPosition(this)), Mods(e.KeyModifiers));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_session == null || Button(e.GetCurrentPoint(this).Properties.PointerUpdateKind) is not { } b) return;
        if (_session.MouseUp(b, P(e.GetPosition(this)), Mods(e.KeyModifiers))) e.Handled = true;
        if (!_captured) e.Pointer.Capture(null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _session?.MouseLeave();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        e.Handled = true;
        _session?.MouseWheel(P(e.GetPosition(this)), e.Delta.Y * 120);
    }

    // ------------------------------------------------------------------ drawing

    private GL? _gl;
    private uint _faceProgram, _lineProgram, _edgeProgram;
    private int _uMvp, _uLight, _uBack, _uUnlit, _uLineMvp, _uHalf, _uBias, _uEdgeMvp, _uEdgeHalf, _uEdgeColor, _uEdgeWidth;
    private bool _modelDirty = true, _overlayDirty = true;

    /// <summary>Which OpenGL drew it (for the log and the test aid).</summary>
    public string GlInfo { get; private set; } = "(not yet)";

    /// <summary>A vertex array with its buffer and how many vertices are in it (for the model's edges: its buffer of
    /// segments, how many, and the buffer of the six corners every one of them is drawn with).</summary>
    private sealed class Batch { public uint Vao, Vbo, Corners; public int Count; }

    // faces: position, normal, colour (10 floats); lines: position, the other end, side, colour, width (12 floats)
    private const int FaceStride = 10, LineStride = 12;
    private Batch? _faces, _faded, _edges, _highlights, _lines, _linesBehind, _linesOnTop;

    /// <summary>A frame asked for: its pixels (width, height, RGBA rows top down) once drawn.</summary>
    private TaskCompletionSource<(int W, int H, byte[] Rgba)>? _capture;

    /// <summary>The next frame's pixels.</summary>
    public Task<(int W, int H, byte[] Rgba)> CaptureAsync()
    {
        var tcs = _capture ??= new TaskCompletionSource<(int, int, byte[])>(TaskCreationOptions.RunContinuationsAsynchronously);
        RequestNextFrameRendering();
        return tcs.Task;
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        _gl = GL.GetApi(gl.GetProcAddress);
        GlInfo = $"{GlVersion.Type} {GlVersion.Major}.{GlVersion.Minor}, {_gl.GetStringS(StringName.Renderer)}";
        SessionLog.Write("OpenGL: " + GlInfo);
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
                vec4 c = gl_FrontFacing ? vColor : vec4(uBack.rgb, vColor.a);
                float light = 0.55 + 0.45 * max(dot(n, -uLight), 0.0);
                fragColor = vec4(c.rgb * light, c.a);
            }
            """);
        // lines of any width: each segment a quad, widened on the screen (thick GL lines are not there everywhere)
        _lineProgram = Program(head + """
            layout(location = 0) in vec3 aPos;
            layout(location = 1) in vec3 aOther;
            layout(location = 2) in float aSide;
            layout(location = 3) in vec4 aColor;
            layout(location = 4) in float aWidth;
            uniform mat4 uMvp;
            uniform vec2 uHalf;
            uniform float uBias;
            out vec4 vColor;
            void main()
            {
                vec4 p = uMvp * vec4(aPos, 1.0);
                vec4 o = uMvp * vec4(aOther, 1.0);
                vec2 ps = p.xy / max(p.w, 1e-6) * uHalf;
                vec2 os = o.xy / max(o.w, 1e-6) * uHalf;
                vec2 d = os - ps;
                d = length(d) < 1e-6 ? vec2(1.0, 0.0) : normalize(d);
                vec2 n = vec2(-d.y, d.x);
                p.xy += n * aSide * (aWidth * 0.5) / uHalf * p.w;
                p.z += uBias * p.w;
                gl_Position = p;
                vColor = aColor;
            }
            """, head + """
            in vec4 vColor;
            out vec4 fragColor;
            void main() { fragColor = vColor; }
            """);
        // the model's edges: one instance per segment, its two ends; the six corners of its quad shared by all, the colour
        // and width the same for all (a model of millions of edges takes 24 bytes for each, not 288)
        _edgeProgram = Program(head + """
            layout(location = 0) in float aEnd;
            layout(location = 1) in float aSide;
            layout(location = 2) in vec3 aA;
            layout(location = 3) in vec3 aB;
            uniform mat4 uMvp;
            uniform vec2 uHalf;
            uniform vec4 uColor;
            uniform float uWidth;
            out vec4 vColor;
            void main()
            {
                vec3 at = aEnd < 0.5 ? aA : aB;
                vec3 other = aEnd < 0.5 ? aB : aA;
                vec4 p = uMvp * vec4(at, 1.0);
                vec4 o = uMvp * vec4(other, 1.0);
                vec2 ps = p.xy / max(p.w, 1e-6) * uHalf;
                vec2 os = o.xy / max(o.w, 1e-6) * uHalf;
                vec2 d = os - ps;
                d = length(d) < 1e-6 ? vec2(1.0, 0.0) : normalize(d);
                vec2 n = vec2(-d.y, d.x);
                p.xy += n * aSide * (uWidth * 0.5) / uHalf * p.w;
                gl_Position = p;
                vColor = uColor;
            }
            """, head + """
            in vec4 vColor;
            out vec4 fragColor;
            void main() { fragColor = vColor; }
            """);
        _uEdgeMvp = _gl.GetUniformLocation(_edgeProgram, "uMvp");
        _uEdgeHalf = _gl.GetUniformLocation(_edgeProgram, "uHalf");
        _uEdgeColor = _gl.GetUniformLocation(_edgeProgram, "uColor");
        _uEdgeWidth = _gl.GetUniformLocation(_edgeProgram, "uWidth");
        _uMvp = _gl.GetUniformLocation(_faceProgram, "uMvp");
        _uLight = _gl.GetUniformLocation(_faceProgram, "uLight");
        _uBack = _gl.GetUniformLocation(_faceProgram, "uBack");
        _uUnlit = _gl.GetUniformLocation(_faceProgram, "uUnlit");
        _uLineMvp = _gl.GetUniformLocation(_lineProgram, "uMvp");
        _uHalf = _gl.GetUniformLocation(_lineProgram, "uHalf");
        _uBias = _gl.GetUniformLocation(_lineProgram, "uBias");
        _faces = FaceBatch(); _faded = FaceBatch(); _highlights = FaceBatch();
        _edges = EdgeBatch(); _lines = LineBatch(); _linesBehind = LineBatch(); _linesOnTop = LineBatch();
        _modelDirty = _overlayDirty = true;
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        if (_gl == null) return;
        foreach (var b in new[] { _faces, _faded, _highlights, _edges, _lines, _linesBehind, _linesOnTop })
            if (b != null) { _gl.DeleteBuffer(b.Vbo); if (b.Corners != 0) _gl.DeleteBuffer(b.Corners); _gl.DeleteVertexArray(b.Vao); }
        _gl.DeleteProgram(_faceProgram); _gl.DeleteProgram(_lineProgram); _gl.DeleteProgram(_edgeProgram);
        _faces = _faded = _highlights = _edges = _lines = _linesBehind = _linesOnTop = null;
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

    private unsafe Batch NewBatch(int stride, (uint Index, int Size, int Offset)[] attributes)
    {
        var gl = _gl!;
        var b = new Batch { Vao = gl.GenVertexArray(), Vbo = gl.GenBuffer() };
        gl.BindVertexArray(b.Vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, b.Vbo);
        foreach (var (index, size, offset) in attributes)
        {
            gl.EnableVertexAttribArray(index);
            gl.VertexAttribPointer(index, size, VertexAttribPointerType.Float, false, (uint)(stride * sizeof(float)), (void*)(offset * sizeof(float)));
        }
        gl.BindVertexArray(0);
        return b;
    }

    private Batch FaceBatch() => NewBatch(FaceStride, [(0, 3, 0), (1, 3, 3), (2, 4, 6)]);
    private Batch LineBatch() => NewBatch(LineStride, [(0, 3, 0), (1, 3, 3), (2, 1, 6), (3, 4, 7), (4, 1, 11)]);

    /// <summary>The model's edges: the six corners of a segment's quad (which end, which side; as
    /// <see cref="Segment"/> makes them), and per segment (one instance) its two ends.</summary>
    private unsafe Batch EdgeBatch()
    {
        var gl = _gl!;
        var b = new Batch { Vao = gl.GenVertexArray(), Vbo = gl.GenBuffer(), Corners = gl.GenBuffer() };
        gl.BindVertexArray(b.Vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, b.Corners);
        float[] corners = [0, 1, 0, -1, 1, -1, 0, -1, 1, 1, 1, -1];
        fixed (float* p = corners) gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(corners.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
        gl.EnableVertexAttribArray(0); gl.VertexAttribPointer(0, 1, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);
        gl.EnableVertexAttribArray(1); gl.VertexAttribPointer(1, 1, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)sizeof(float));
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, b.Vbo);
        gl.EnableVertexAttribArray(2); gl.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), (void*)0);
        gl.EnableVertexAttribArray(3); gl.VertexAttribPointer(3, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), (void*)(3 * sizeof(float)));
        gl.VertexAttribDivisor(2, 1); gl.VertexAttribDivisor(3, 1);
        gl.BindVertexArray(0);
        return b;
    }

    /// <summary>A buffer filled with the first <paramref name="length"/> numbers of <paramref name="data"/>.</summary>
    private unsafe void Fill(Batch b, float[] data, int length, int stride)
    {
        var gl = _gl!;
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, b.Vbo);
        fixed (float* p = data) gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(length * sizeof(float)), p, BufferUsageARB.StaticDraw);
        b.Count = length / stride;
    }

    private unsafe void Fill(Batch b, List<float> data, int stride)
    {
        var gl = _gl!;
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, b.Vbo);
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(data);
        fixed (float* p = span) gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(span.Length * sizeof(float)), p, BufferUsageARB.DynamicDraw);
        b.Count = data.Count / stride;
    }

    private static Vector4 V4(Color c, double opacity = 1) => new(c.R / 255f, c.G / 255f, c.B / 255f, (float)(c.A / 255.0 * opacity));

    private static void Face(List<float> d, Vector3 p, Vector3 n, Vector4 c)
    {
        d.Add(p.X); d.Add(p.Y); d.Add(p.Z); d.Add(n.X); d.Add(n.Y); d.Add(n.Z); d.Add(c.X); d.Add(c.Y); d.Add(c.Z); d.Add(c.W);
    }

    /// <summary>A segment as two triangles, widened in the vertex shader (<paramref name="width"/> in pixels).</summary>
    private static void Segment(List<float> d, Vector3 a, Vector3 b, Vector4 c, float width)
    {
        void V(Vector3 p, Vector3 o, float side) { d.Add(p.X); d.Add(p.Y); d.Add(p.Z); d.Add(o.X); d.Add(o.Y); d.Add(o.Z); d.Add(side); d.Add(c.X); d.Add(c.Y); d.Add(c.Z); d.Add(c.W); d.Add(width); }
        // (at b the other end is a: its side turned round, to stay on the same side of the line)
        V(a, b, 1); V(a, b, -1); V(b, a, -1);
        V(a, b, -1); V(b, a, 1); V(b, a, -1);
    }

    private static Vector3 F(PushPullMe.Editor.Session.Point3D p) => new((float)p.X, (float)p.Y, (float)p.Z);

    /// <summary>How wide a line of the editor's thickness is drawn (pixels, as the WPF window drew it).</summary>
    private float Width(double thickness) => (float)(Math.Max(1, thickness * 0.9) * (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1));

    private void Lines(List<float> d, IReadOnlyList<PushPullMe.Editor.Session.Point3D> pts, Color color, double thickness)
    {
        var c = V4(color); float w = Width(thickness);
        for (int i = 0; i + 1 < pts.Count; i += 2) Segment(d, F(pts[i]), F(pts[i + 1]), c, w);
    }

    /// <summary>The model to the graphics card: its faces (each vertex its position, normal and colour) and its edges
    /// (each segment its two ends), written straight into arrays of the size they need, let go once handed over.</summary>
    private void UploadModel()
    {
        var model = _session?.Shown;
        int solid = 0, seeThrough = 0;
        // (see-through: faded outside the open group, or of a see-through colour (glass); drawn after the rest)
        static bool SeeThrough(ShownMesh m) => m.Faded || m.Color is { A: < 255 };
        foreach (var m in model?.Meshes ?? []) if (SeeThrough(m)) seeThrough += m.Positions.Length; else solid += m.Positions.Length;
        var faces = new float[solid * FaceStride]; var faded = new float[seeThrough * FaceStride];
        int fi = 0, di = 0;
        foreach (var m in model?.Meshes ?? [])
        {
            var c = m.Faded ? V4(EditorSession.FrontColor, 0x38 / 255.0) : V4(m.Color ?? EditorSession.FrontColor);
            bool see = SeeThrough(m);
            var into = see ? faded : faces;
            ref int at = ref (see ? ref di : ref fi);
            for (int i = 0; i < m.Positions.Length; i++)
            {
                var (p, n) = (m.Positions[i], m.Normals[i]);
                into[at++] = p.X; into[at++] = p.Y; into[at++] = p.Z; into[at++] = n.X; into[at++] = n.Y; into[at++] = n.Z;
                into[at++] = c.X; into[at++] = c.Y; into[at++] = c.Z; into[at++] = c.W;
            }
        }
        var pts = model?.Edges;
        int segments = (pts?.Count ?? 0) / 2;
        var edges = new float[segments * 6];
        for (int i = 0; i < segments; i++)
        {
            var (a, b) = (pts![2 * i], pts[2 * i + 1]);
            edges[6 * i] = (float)a.X; edges[6 * i + 1] = (float)a.Y; edges[6 * i + 2] = (float)a.Z;
            edges[6 * i + 3] = (float)b.X; edges[6 * i + 4] = (float)b.Y; edges[6 * i + 5] = (float)b.Z;
        }
        Fill(_faces!, faces, faces.Length, FaceStride); Fill(_faded!, faded, faded.Length, FaceStride); Fill(_edges!, edges, edges.Length, 6);
    }

    private void UploadOverlays()
    {
        var highlights = new List<float>(); var lines = new List<float>(); var behind = new List<float>(); var onTop = new List<float>();
        foreach (var item in _session?.View.Children ?? [])
            switch (item)
            {
                case LineSet ls: Lines(ls.OnTop ? onTop : ls.Behind ? behind : lines, ls.Points, ls.Color, ls.Thickness); break;
                case FaceSet fs:
                {
                    var c = V4(fs.Color, fs.Opacity);
                    foreach (var p in fs.Triangles) Face(highlights, F(p), default, c);
                    break;
                }
            }
        Fill(_highlights!, highlights, FaceStride); Fill(_lines!, lines, LineStride); Fill(_linesBehind!, behind, LineStride); Fill(_linesOnTop!, onTop, LineStride);
    }

    /// <summary>
    /// From the model to the clip space, exactly as <see cref="ViewFrame.Project"/> (x and y over the depth along the
    /// look, scaled by how far the view reaches): what is drawn and what is picked agree. For row vectors (v * M), as
    /// System.Numerics has them; given to GL as is, GLSL reads it as its transpose, which is M for column vectors.
    /// </summary>
    private static Matrix4x4 ClipMatrix(ViewFrame f, double near, double far)
    {
        double a = (far + near) / (far - near), b = -2 * far * near / (far - near);
        var r = f.Right * (1 / f.Tx); var u = f.Up * (1 / f.Ty); var fw = f.Forward;
        double er = -r.Dot(f.Eye), eu = -u.Dot(f.Eye), ef = -fw.Dot(f.Eye);
        return new Matrix4x4(
            (float)r.X, (float)u.X, (float)(a * fw.X), (float)fw.X,
            (float)r.Y, (float)u.Y, (float)(a * fw.Y), (float)fw.Y,
            (float)r.Z, (float)u.Z, (float)(a * fw.Z), (float)fw.Z,
            (float)er, (float)eu, (float)(a * ef + b), (float)ef);
    }

    protected override unsafe void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_gl == null || _faces == null || _session == null) return;
        var g = _gl;
        if (_modelDirty) { UploadModel(); _modelDirty = false; }
        if (_overlayDirty) { UploadOverlays(); _overlayDirty = false; }

        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        int w = Math.Max(1, (int)(Bounds.Width * scale)), h = Math.Max(1, (int)(Bounds.Height * scale));
        g.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
        g.Viewport(0, 0, (uint)w, (uint)h);
        g.ClearColor(0xF2 / 255f, 0xF7 / 255f, 0xFD / 255f, 1);
        g.ClearDepth(1);
        g.DepthMask(true);
        g.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        if (_session.View.Frame is { } frame)
        {
            var cam = _session.View.Camera;
            var mvp = ClipMatrix(frame, cam.NearPlaneDistance, cam.FarPlaneDistance);
            g.Enable(EnableCap.DepthTest);
            g.DepthFunc(DepthFunction.Lequal);
            g.Disable(EnableCap.CullFace);
            g.Disable(EnableCap.Blend);

            // the faces, pushed back a little in the depth test so their edges lie in front of them
            g.UseProgram(_faceProgram);
            g.UniformMatrix4(_uMvp, 1, false, (float*)&mvp);
            g.Uniform3(_uLight, (float)frame.Forward.X, (float)frame.Forward.Y, (float)frame.Forward.Z);
            var back = V4(EditorSession.BackColor);
            g.Uniform4(_uBack, back.X, back.Y, back.Z, back.W);
            g.Uniform1(_uUnlit, 0);
            g.Enable(EnableCap.PolygonOffsetFill);
            g.PolygonOffset(1, 1);
            Draw(_faces);
            g.Disable(EnableCap.PolygonOffsetFill);

            // the edges and the lines beside the model (the ground a little behind what lies on it)
            g.UseProgram(_lineProgram);
            g.UniformMatrix4(_uLineMvp, 1, false, (float*)&mvp);
            g.Uniform2(_uHalf, w / 2f, h / 2f);
            g.Uniform1(_uBias, 2.4e-5f);
            Draw(_linesBehind!);
            g.Uniform1(_uBias, 0f);
            Draw(_lines!);
            if (_edges!.Count > 0)
            {
                g.UseProgram(_edgeProgram);
                g.UniformMatrix4(_uEdgeMvp, 1, false, (float*)&mvp);
                g.Uniform2(_uEdgeHalf, w / 2f, h / 2f);
                var ec = V4(EditorSession.EdgeColor);
                g.Uniform4(_uEdgeColor, ec.X, ec.Y, ec.Z, ec.W);
                g.Uniform1(_uEdgeWidth, Width(1.3));
                g.BindVertexArray(_edges.Vao);
                g.DrawArraysInstanced(PrimitiveType.Triangles, 0, 6, (uint)_edges.Count);
            }

            // see-through: the faded faces, then the highlights (unlit)
            g.Enable(EnableCap.Blend);
            // (the picture itself stays opaque: what is see-through only mixes the colours)
            g.BlendFuncSeparate(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha, BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
            g.DepthMask(false);
            g.UseProgram(_faceProgram);
            Draw(_faded!);
            g.Uniform1(_uUnlit, 1);
            Draw(_highlights!);
            g.DepthMask(true);
            g.Disable(EnableCap.Blend);

            // the work plane's handle over everything
            g.Disable(EnableCap.DepthTest);
            g.UseProgram(_lineProgram);
            Draw(_linesOnTop!);
            g.BindVertexArray(0);
        }

        if (_capture is { } capture)
        {
            _capture = null;
            var pixels = new byte[w * h * 4];
            fixed (byte* p = pixels) g.ReadPixels(0, 0, (uint)w, (uint)h, PixelFormat.Rgba, PixelType.UnsignedByte, p);
            // (OpenGL gives the rows bottom up)
            var rows = new byte[pixels.Length];
            for (int y = 0; y < h; y++) Array.Copy(pixels, (h - 1 - y) * w * 4, rows, y * w * 4, w * 4);
            capture.SetResult((w, h, rows));
        }

        void Draw(Batch b)
        {
            if (b.Count == 0) return;
            g.BindVertexArray(b.Vao);
            g.DrawArrays(PrimitiveType.Triangles, 0, (uint)b.Count);
        }
    }
}
