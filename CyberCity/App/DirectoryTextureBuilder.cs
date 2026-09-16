using Silk.NET.OpenGL;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using CyberCity.IO;

namespace CyberCity.App;

/// <summary>
/// Builds the GL_TEXTURE_2D_ARRAY of per-building label textures: one layer per
/// top-level scanned directory, each a rasterized text block (directory name in
/// yellow, file listing in black — see RenderTextToImage). Port of raymarcher.py's
/// _generate_directory_textures and _render_text_to_surface, using
/// SixLabors.ImageSharp/.Drawing/.Fonts in place of pygame.font + pygame.Surface.
/// </summary>
public static class DirectoryTextureBuilder
{
    public static (uint TextureArrayId, int ActualBuildingTextureCount) Build(
        GL gl, TextGenerator textGenerator, RaymarcherConfig config, string fontPath, string rootScanPath)
    {
        Console.WriteLine($"Scanning directory: {rootScanPath}");
        var folderDataWithFiles = textGenerator.GetFolderDataWithFiles();
        Console.WriteLine($"Found {folderDataWithFiles.Count} top-level directories.");

        int maxLines = config.TextureHeight / config.LineHeight;
        int maxLineWidthChars = (int)(config.TextureWidth / (config.FontSize * 0.6));

        Font font = LoadDirectoryFont(fontPath, config.FontSize);

        uint textureArrayId = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2DArray, textureArrayId);

        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);

        // The Python original uses the immutable-storage glTexStorage3D here. We use the
        // mutable glTexImage3D (allocate with null data) + glTexSubImage3D per layer instead:
        // same end result for a texture array that's never resized, and its enum-parameter
        // shape is already verified elsewhere in this port (see FontAtlas's TexImage2D call),
        // whereas TexStorage3D's "sized internal format" parameter type isn't something we
        // could confidently verify without a compiler on hand. Swap to TexStorage3D once you
        // can build-test if you want the immutable-format guarantee back.
        unsafe
        {
            gl.TexImage3D(TextureTarget.Texture2DArray, 0, InternalFormat.Rgba8,
                (uint)config.TextureWidth, (uint)config.TextureHeight, (uint)config.MaxDirectories,
                0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
        }

        int actualBuildingTextureCount = 0;

        for (int i = 0; i < folderDataWithFiles.Count; i++)
        {
            if (i >= config.MaxDirectories)
            {
                Console.WriteLine($"Warning: Exceeded max directories ({config.MaxDirectories}). Skipping {folderDataWithFiles[i].DisplayName}.");
                break;
            }

            var (folderName, _, files) = folderDataWithFiles[i];

            string textContent = textGenerator.GenerateText(
                folderName: folderName,
                directoryFiles: files,
                showSizes: true,
                maxItems: maxLines - 2,
                maxLineWidth: maxLineWidthChars);

            using Image<Rgba32> textImage = RenderTextToImage(textContent, font, config);

            byte[] pixels = new byte[textImage.Width * textImage.Height * 4];
            textImage.CopyPixelDataTo(pixels);

            unsafe
            {
                fixed (byte* pixelPtr = pixels)
                {
                    gl.TexSubImage3D(TextureTarget.Texture2DArray, 0, 0, 0, i,
                        (uint)textImage.Width, (uint)textImage.Height, 1,
                        PixelFormat.Rgba, PixelType.UnsignedByte, pixelPtr);
                }
            }

            actualBuildingTextureCount++;
        }

        gl.BindTexture(TextureTarget.Texture2DArray, 0);

        return (textureArrayId, actualBuildingTextureCount);
    }

    /// <summary>
    /// Loads the bundled DejaVu Sans Mono Bold font. The Python original tries to
    /// look up "dejavusansmono" among the host's installed system fonts, falling
    /// back to pygame's bundled default font if that lookup fails. SixLabors.Fonts
    /// has no equivalent of "search installed system fonts" or "built-in default
    /// font", so this loads the exact same font family directly from the file
    /// shipped in this project's fonts folder — deterministic across machines,
    /// where the Python version's font choice depended on what was installed.
    /// </summary>
    private static Font LoadDirectoryFont(string fontPath, int fontSize)
    {
        var collection = new FontCollection();
        FontFamily family = collection.Add(fontPath);
        return family.CreateFont(fontSize, FontStyle.Regular);
    }

    /// <summary>Port of _render_text_to_surface.</summary>
    private static Image<Rgba32> RenderTextToImage(string textContent, Font font, RaymarcherConfig config)
    {
        var image = new Image<Rgba32>(config.TextureWidth, config.TextureHeight);
        // New images are fully transparent (0,0,0,0) by default — matches the
        // Python surface's explicit surface.fill((0, 0, 0, 0)).

        string[] lines = textContent.Split('\n');
        int yOffset = 0;

        image.Mutate(ctx =>
        {
            foreach (string line in lines)
            {
                bool isHeader = line.StartsWith("**") && line.EndsWith("**");
                Color color = isHeader ? Color.Yellow : Color.Black;
                string text = isHeader ? StripBoldMarkers(line) : line;

                ctx.DrawText(text, font, color, new PointF(5, yOffset));

                yOffset += config.LineHeight;
                if (yOffset + config.LineHeight > config.TextureHeight)
                {
                    return;
                }
            }
        });

        // PNGs/pygame surfaces decode/render top-down; OpenGL texture coordinates
        // are bottom-up (matches pygame.image.tostring(surface, "RGBA", True)'s flip).
        image.Mutate(ctx => ctx.Flip(FlipMode.Vertical));

        return image;
    }

    /// <summary>
    /// Mirrors Python's line[2:-2] slice, which returns "" for any string of
    /// length &lt;= 4 rather than raising — C# substring ranges don't clamp the
    /// same way, so this replicates that behavior explicitly.
    /// </summary>
    private static string StripBoldMarkers(string line) => line.Length <= 4 ? string.Empty : line[2..^2];
}