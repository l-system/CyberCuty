namespace CyberCity.Rendering;

/// <summary>
/// Mirrors the shader_type strings from shader_loader.py's get_shader_pair.
/// Note: "digital_rain_fragment_shader.glsl" ships in the Python project's shaders
/// folder but was never wired into get_shader_pair's dictionary there either —
/// that parity gap is preserved here rather than silently "fixed".
/// </summary>
public enum RaymarchShaderType
{
    Default,
    Grid,
    RealData
}

/// <summary>
/// Port of shader_loader.py. Loads GLSL source text and resolves shader-type to
/// vertex/fragment file paths, relative to the running executable's directory
/// (the analog of Python's os.path.dirname(__file__) + shaders/ copy-to-output).
/// </summary>
public static class ShaderLoader
{
    private const string VertexShaderPath = "shaders/default_vertex_shader.glsl";

    private static readonly Dictionary<RaymarchShaderType, string> FragmentShaders = new()
    {
        [RaymarchShaderType.Default] = "shaders/default_fragment_shader.glsl",
        [RaymarchShaderType.Grid] = "shaders/grid_fragment_shader.glsl",
        [RaymarchShaderType.RealData] = "shaders/real_data_fragment_shader.glsl",
    };

    public static string LoadShaderSource(string relativePath)
    {
        string fullPath = Path.Combine(AppContext.BaseDirectory, relativePath);
        try
        {
            return File.ReadAllText(fullPath);
        }
        catch (FileNotFoundException)
        {
            Console.Error.WriteLine($"Error: Shader file not found at {fullPath}");
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error loading shader from {fullPath}: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Returns (vertexShaderPath, fragmentShaderPath) for the given shader type.
    /// Unknown/unmapped types fall back to Default, matching the Python dict.get(...) default.
    /// </summary>
    public static (string VertexPath, string FragmentPath) GetShaderPair(RaymarchShaderType shaderType = RaymarchShaderType.Default)
    {
        string fragmentPath = FragmentShaders.GetValueOrDefault(shaderType, FragmentShaders[RaymarchShaderType.Default]);
        return (VertexShaderPath, fragmentPath);
    }
}
