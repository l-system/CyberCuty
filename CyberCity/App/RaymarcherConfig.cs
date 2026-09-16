namespace CyberCity.App;

/// <summary>Port of raymarcher.py's RaymarcherConfig dataclass.</summary>
public sealed record RaymarcherConfig
{
    public int TextureWidth { get; init; } = 512;
    public int TextureHeight { get; init; } = 2048;
    public int FontSize { get; init; } = 24;
    public int LineHeight { get; init; } = 26;
    public int MaxDirectories { get; init; } = 1000;
    public int FontAtlasTextureUnit { get; init; } = 0;
    public int DirectoryArrayTextureUnit { get; init; } = 1;
}
