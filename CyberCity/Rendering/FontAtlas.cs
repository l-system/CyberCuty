using System.Numerics;
using System.Text.Json;
using Silk.NET.OpenGL;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace CyberCity.Rendering;

/// <summary>
/// Port of raymarcher.py's _create_font_atlas. Loads a multi-channel signed
/// distance field (MSDF) font atlas — image plus glyph-metrics JSON — and
/// extracts the plane/atlas bounds for the 16 hex-digit glyphs the shader
/// uses to render text (see real_data_fragment_shader.glsl's msdf sampling).
/// </summary>
public sealed class FontAtlas : IDisposable
{
    private const string HexChars = "0123456789ABCDEF";

    private readonly GL _gl;

    public uint TextureId { get; private set; }
    public int AtlasWidth { get; private set; }
    public int AtlasHeight { get; private set; }
    public float DistanceRange { get; private set; }

    /// <summary>Indexed by HexChars.IndexOf(char) — left, bottom, right, top.</summary>
    public Vector4[] CharPlaneBounds { get; private set; } = new Vector4[16];

    /// <summary>Indexed by HexChars.IndexOf(char) — left, bottom, right, top.</summary>
    public Vector4[] CharAtlasBounds { get; private set; } = new Vector4[16];

    private FontAtlas(GL gl) => _gl = gl;

    public static FontAtlas Load(GL gl, string pngPath, string jsonPath)
    {
        if (!File.Exists(pngPath))
        {
            var ex = new FileNotFoundException("Font atlas image not found", pngPath);
            Console.WriteLine($"Error loading font atlas: {ex.Message}");
            throw ex;
        }

        if (!File.Exists(jsonPath))
        {
            var ex = new FileNotFoundException("Font atlas metrics not found", jsonPath);
            Console.WriteLine($"Error loading font atlas: {ex.Message}");
            throw ex;
        }

        var atlas = new FontAtlas(gl);
        atlas.LoadGlyphMetrics(jsonPath);
        atlas.LoadTexture(pngPath);
        return atlas;
    }

    private void LoadGlyphMetrics(string jsonPath)
    {
        using var stream = File.OpenRead(jsonPath);
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;

        var atlasInfo = root.GetProperty("atlas");
        AtlasWidth = atlasInfo.GetProperty("width").GetInt32();
        AtlasHeight = atlasInfo.GetProperty("height").GetInt32();
        DistanceRange = atlasInfo.GetProperty("distanceRange").GetSingle();

        foreach (var glyph in root.GetProperty("glyphs").EnumerateArray())
        {
            // Different msdf-atlas-gen charset modes name this field differently
            // ("unicode" in charset mode, "id"/"index" in identifier modes) —
            // this atlas happens to only populate "index", but the fallback
            // chain matches the Python source's tolerance for either.
            if (!TryGetGlyphCodepoint(glyph, out int codepoint) || codepoint is <= 0 or > 0x10FFFF)
            {
                continue;
            }

            char ch = (char)codepoint;
            int idx = HexChars.IndexOf(ch);
            if (idx < 0)
            {
                continue;
            }

            if (glyph.TryGetProperty("planeBounds", out var planeBounds))
            {
                CharPlaneBounds[idx] = ReadBounds(planeBounds);
            }

            if (glyph.TryGetProperty("atlasBounds", out var atlasBounds))
            {
                CharAtlasBounds[idx] = ReadBounds(atlasBounds);
            }
        }
    }

    private static bool TryGetGlyphCodepoint(JsonElement glyph, out int codepoint)
    {
        foreach (string propertyName in new[] { "unicode", "id", "index" })
        {
            if (glyph.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out codepoint))
            {
                return true;
            }
        }

        codepoint = 0;
        return false;
    }

    private static Vector4 ReadBounds(JsonElement bounds) => new(
        bounds.GetProperty("left").GetSingle(),
        bounds.GetProperty("bottom").GetSingle(),
        bounds.GetProperty("right").GetSingle(),
        bounds.GetProperty("top").GetSingle());

    private void LoadTexture(string pngPath)
    {
        using var image = Image.Load<Rgba32>(pngPath);

        // PNG decodes top-down; OpenGL texture coordinates are bottom-up.
        image.Mutate(ctx => ctx.Flip(FlipMode.Vertical));

        byte[] pixels = new byte[image.Width * image.Height * 4];
        image.CopyPixelDataTo(pixels);

        // The source PNG is 3-channel RGB (no alpha) — the shader's msdf sampling
        // only ever reads .rgb, so decoding through as RGBA and letting alpha go
        // unused is behaviorally identical and keeps a single pixel format across
        // this port instead of also handling ImageSharp's Rgb24 path for one file.
        TextureId = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, TextureId);

        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);

        unsafe
        {
            fixed (byte* pixelPtr = pixels)
            {
                _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)image.Width,
                    (uint)image.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, pixelPtr);
            }
        }

        _gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    public void Dispose() => _gl.DeleteTexture(TextureId);
}
