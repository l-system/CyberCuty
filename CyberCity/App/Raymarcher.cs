using System.Numerics;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using CyberCity.IO;
using CyberCity.Rendering;
using System.Diagnostics;

namespace CyberCity.App;

/// <summary>
/// GPU-based raymarcher with directory visualization and MSDF text rendering.
/// Port of raymarcher.py's Raymarcher class.
///
/// Structural note: the Python original does all of its OpenGL setup eagerly in
/// __init__, because pygame.display.set_mode() creates the GL context synchronously.
/// Silk.NET's IWindow has no current GL context until window.Run() starts pumping
/// its Load event, so that "eager init" body has to move into an OnLoad handler
/// instead — the constructor here only builds the window and wires up event
/// handlers. This is a required consequence of the windowing library's lifecycle,
/// not a stylistic change; the resulting OnLoad reproduces __init__'s init order
/// exactly (camera → directory textures → font atlas → shaders → uniforms → quad).
///
/// Split across two files: this one covers state and initialization; the
/// render/update/input-handling side lives in Raymarcher.Loop.cs.
/// </summary>
public sealed partial class Raymarcher : IDisposable
{
    /// <summary>Port of raymarcher.py's UniformLocations dataclass.</summary>
    private readonly record struct UniformLocations(
        int ITime,
        int IResolution,
        int CameraPosition,
        int CameraTarget,
        int CameraUp,
        int CameraFov,
        int N2,
        int FontAtlas,
        int AtlasWidth,
        int AtlasHeight,
        int DistanceRange,
        int CharPlaneBounds,
        int CharAtlasBounds,
        int DirectoryTexturesArray,
        int BuildingTextureCount);

    private const string WindowTitle = "GPU Raymarcher";

    private int _width;
    private int _height;
    private readonly float _iorN2;
    private readonly string _shaderTypeArg;
    private readonly string _rootScanPath;
    private readonly int _maxDepth;
    private readonly int _maxFiles;
    private readonly RaymarcherConfig _config;

    private readonly IWindow _window;
    private GL _gl = null!;
    private IInputContext _input = null!;
    private IKeyboard? _primaryKeyboard;

    private Camera _camera = null!;

    private GlShaderProgram _shaderProgram = null!;
    private UniformLocations _uniforms;

    private FontAtlas _fontAtlas = null!;
    private uint _directoryTextureArrayId;
    private int _actualBuildingTextureCount;

    private uint _vao;
    private uint _vbo;

    private bool _mouseCaptured = true;
    private Vector2? _lastMousePosition;

    private long _startTimestamp;

    private int _frameCount;
    private double _fpsAccumulatedSeconds;

    public Raymarcher(int width, int height, float iorN2, string shaderType = "default", string rootScanPath = ".",
        int maxDepth = 8, int maxFiles = 10000, int maxDirectories = 1000)
    {
        _width = width;
        _height = height;
        _iorN2 = iorN2;
        _shaderTypeArg = shaderType;
        _rootScanPath = rootScanPath;
        _maxDepth = maxDepth;
        _maxFiles = maxFiles;
        _config = new RaymarcherConfig { MaxDirectories = maxDirectories };

        var options = WindowOptions.Default;
        options.Size = new Vector2D<int>(width, height);
        options.Title = WindowTitle;

        _window = Window.Create(options);
        _window.Load += OnLoad;
        _window.Render += OnRender;
        _window.FramebufferResize += OnFramebufferResize;
        _window.Closing += OnClosing;
    }

    /// <summary>Starts the blocking render loop. Port of raymarcher.py's run().</summary>
    public void Run() => _window.Run();

    private void OnLoad()
    {
        _gl = GL.GetApi(_window);

        InitInput();
        InitCamera();
        InitTextures();
        InitShaders();
        InitUniforms();
        SetupQuad();
        BindStaticUniforms();

        _gl.ClearColor(0f, 0f, 0f, 1f);
        _gl.Viewport(0, 0, (uint)_width, (uint)_height);

        // Mirrors Python's start_time being captured at the top of run(), i.e. right
        // as setup finishes and the render loop is about to begin — not at __init__.
        _startTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Sets every uniform and texture binding that never changes after startup —
    /// font-atlas metrics and glyph-bounds arrays, both texture-unit bindings,
    /// building count, camera FOV, index of refraction, and the initial
    /// resolution — exactly once, instead of every frame.
    ///
    /// The Python original re-sets all of this inside its per-frame _render, which
    /// this port initially matched for fidelity. A GL uniform's value persists on
    /// its program (and a texture binding persists on its unit) until something
    /// else changes it; since this app only ever has one shader program and never
    /// rebinds these two texture units to anything else, setting them once here is
    /// behaviorally identical to re-setting them every frame and removes real,
    /// needless work from the hot path. See OnRender (Raymarcher.Loop.cs) for what
    /// actually does need to run every frame, and OnFramebufferResize for the one
    /// value here (resolution) that can change after startup, just not per-frame.
    /// </summary>
    private void BindStaticUniforms()
    {
        _shaderProgram.Use();

        _gl.Uniform2(_uniforms.IResolution, (float)_width, (float)_height);
        _gl.Uniform1(_uniforms.CameraFov, _camera.Fov);
        _gl.Uniform1(_uniforms.N2, _iorN2);

        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _fontAtlas.TextureId);
        _gl.Uniform1(_uniforms.FontAtlas, 0);
        _gl.Uniform1(_uniforms.AtlasWidth, (float)_fontAtlas.AtlasWidth);
        _gl.Uniform1(_uniforms.AtlasHeight, (float)_fontAtlas.AtlasHeight);
        _gl.Uniform1(_uniforms.DistanceRange, _fontAtlas.DistanceRange);
        UploadVec4Array(_uniforms.CharPlaneBounds, _fontAtlas.CharPlaneBounds);
        UploadVec4Array(_uniforms.CharAtlasBounds, _fontAtlas.CharAtlasBounds);

        _gl.ActiveTexture(TextureUnit.Texture1);
        _gl.BindTexture(TextureTarget.Texture2DArray, _directoryTextureArrayId);
        _gl.Uniform1(_uniforms.DirectoryTexturesArray, 1);
        _gl.Uniform1(_uniforms.BuildingTextureCount, _actualBuildingTextureCount);
    }

    private void InitInput()
    {
        _input = _window.CreateInput();

        _primaryKeyboard = _input.Keyboards.Count > 0 ? _input.Keyboards[0] : null;
        if (_primaryKeyboard is not null)
        {
            _primaryKeyboard.KeyDown += OnKeyDown;
        }

        for (int i = 0; i < _input.Mice.Count; i++)
        {
            _input.Mice[i].Cursor.CursorMode = _mouseCaptured ? CursorMode.Raw : CursorMode.Normal;
            _input.Mice[i].MouseMove += OnMouseMove;
        }
    }

    private void InitCamera()
    {
        _camera = new Camera(
            position: new Vector3(-2.0f, 5.0f, -2.0f),
            yaw: -90.0f,
            pitch: 0.0f);
    }

    private void InitTextures()
    {
        // Local rather than a field: nothing needs the TextGenerator (or the full
        // scanned-file list it holds) after the directory textures are built below.
        var textGenerator = new TextGenerator(rootPath: _rootScanPath, maxDepth: _maxDepth, maxFiles: _maxFiles);

        string fontPath = Path.Combine(AppContext.BaseDirectory, "fonts", "DejaVuSansMono-Bold.ttf");
        (_directoryTextureArrayId, _actualBuildingTextureCount) =
            DirectoryTextureBuilder.Build(_gl, textGenerator, _config, fontPath, _rootScanPath);

        string atlasPngPath = Path.Combine(AppContext.BaseDirectory, "fonts", "font_atlas_msdf.png");
        string atlasJsonPath = Path.Combine(AppContext.BaseDirectory, "fonts", "font_atlas_msdf.json");
        _fontAtlas = FontAtlas.Load(_gl, atlasPngPath, atlasJsonPath);
    }

    private void InitShaders()
    {
        RaymarchShaderType shaderType = ParseShaderType(_shaderTypeArg);
        var (vertexPath, fragmentPath) = ShaderLoader.GetShaderPair(shaderType);

        string vertexSource = ShaderLoader.LoadShaderSource(vertexPath);
        string fragmentSource = ShaderLoader.LoadShaderSource(fragmentPath);

        _shaderProgram = new GlShaderProgram(_gl, vertexSource, fragmentSource);
    }

    /// <summary>
    /// Maps the CLI-style shader-type string onto CyberCity.Rendering.RaymarchShaderType.
    /// Python's version keys a plain dict by these same strings and falls back to
    /// "default" for anything unrecognized — ShaderLoader.GetShaderPair already
    /// does that fallback once we've picked an enum value, so an unrecognized
    /// string here just needs to land on RaymarchShaderType.Default to match.
    /// </summary>
    private static RaymarchShaderType ParseShaderType(string shaderType) => shaderType switch
    {
        "grid" => RaymarchShaderType.Grid,
        "real_data" => RaymarchShaderType.RealData,
        _ => RaymarchShaderType.Default,
    };

    private void InitUniforms()
    {
        _uniforms = new UniformLocations(
            ITime: _shaderProgram.GetUniformLocation("iTime"),
            IResolution: _shaderProgram.GetUniformLocation("iResolution"),
            CameraPosition: _shaderProgram.GetUniformLocation("u_cameraPosition"),
            CameraTarget: _shaderProgram.GetUniformLocation("u_cameraTarget"),
            CameraUp: _shaderProgram.GetUniformLocation("u_cameraUp"),
            CameraFov: _shaderProgram.GetUniformLocation("u_cameraFov"),
            N2: _shaderProgram.GetUniformLocation("N2"),
            FontAtlas: _shaderProgram.GetUniformLocation("u_fontAtlas"),
            AtlasWidth: _shaderProgram.GetUniformLocation("u_atlasWidth"),
            AtlasHeight: _shaderProgram.GetUniformLocation("u_atlasHeight"),
            DistanceRange: _shaderProgram.GetUniformLocation("u_distanceRange"),
            CharPlaneBounds: _shaderProgram.GetUniformLocation("u_charPlaneBounds[0]"),
            CharAtlasBounds: _shaderProgram.GetUniformLocation("u_charAtlasBounds[0]"),
            DirectoryTexturesArray: _shaderProgram.GetUniformLocation("u_directoryTexturesArray"),
            BuildingTextureCount: _shaderProgram.GetUniformLocation("u_buildingTextureCount"));
    }

    private unsafe void SetupQuad()
    {
        float[] vertices =
        {
            -1.0f, -1.0f, 0.0f, 1.0f, -1.0f, 0.0f, 1.0f, 1.0f, 0.0f,
            -1.0f, -1.0f, 0.0f, 1.0f, 1.0f, 0.0f, -1.0f, 1.0f, 0.0f,
        };

        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);

        _vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);

        fixed (float* v = vertices)
        {
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(vertices.Length * sizeof(float)), v, BufferUsageARB.StaticDraw);
        }

        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), null);
        _gl.EnableVertexAttribArray(0);

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
        _gl.BindVertexArray(0);
    }

    public void Dispose() => _window.Dispose();
}