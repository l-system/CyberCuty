using Silk.NET.OpenGL;

namespace CyberCity.Rendering;

/// <summary>
/// Compiles and links a GLSL vertex/fragment shader pair into a GL program.
/// Port of raymarcher.py's _compile_shaders, split out into its own type since
/// shader compilation is a distinct responsibility from window/render-loop
/// orchestration (which the monolithic Raymarcher class in the Python source
/// handled all in one place).
/// </summary>
public sealed class GlShaderProgram : IDisposable
{
    private readonly GL _gl;

    public uint Handle { get; }

    public GlShaderProgram(GL gl, string vertexSource, string fragmentSource)
    {
        _gl = gl;

        uint vertexShader = CompileShader(ShaderType.VertexShader, vertexSource);
        uint fragmentShader = CompileShader(ShaderType.FragmentShader, fragmentSource);

        Handle = _gl.CreateProgram();
        _gl.AttachShader(Handle, vertexShader);
        _gl.AttachShader(Handle, fragmentShader);
        _gl.LinkProgram(Handle);

        _gl.GetProgram(Handle, ProgramPropertyARB.LinkStatus, out int linkStatus);
        if (linkStatus != (int)GLEnum.True)
        {
            string error = _gl.GetProgramInfoLog(Handle);
            Console.Error.WriteLine($"Shader linking error: {error}");
            _gl.DeleteShader(vertexShader);
            _gl.DeleteShader(fragmentShader);
            _gl.DeleteProgram(Handle);
            throw new InvalidOperationException("Shader linking failed");
        }

        // The Python original leaves the compiled shader objects attached until
        // glDeleteShader flags them (GL defers actual deletion until detached).
        // Detaching first is the more conventional and equally-correct cleanup
        // order, and doesn't change program behavior.
        _gl.DetachShader(Handle, vertexShader);
        _gl.DetachShader(Handle, fragmentShader);
        _gl.DeleteShader(vertexShader);
        _gl.DeleteShader(fragmentShader);
    }

    private uint CompileShader(ShaderType type, string source)
    {
        uint shader = _gl.CreateShader(type);
        _gl.ShaderSource(shader, source);
        _gl.CompileShader(shader);

        _gl.GetShader(shader, ShaderParameterName.CompileStatus, out int compileStatus);
        if (compileStatus != (int)GLEnum.True)
        {
            string error = _gl.GetShaderInfoLog(shader);
            Console.Error.WriteLine($"Shader compilation error: {error}");
            _gl.DeleteShader(shader);
            throw new InvalidOperationException("Shader compilation failed");
        }

        return shader;
    }

    public void Use() => _gl.UseProgram(Handle);

    public int GetUniformLocation(string name) => _gl.GetUniformLocation(Handle, name);

    public void Dispose() => _gl.DeleteProgram(Handle);
}