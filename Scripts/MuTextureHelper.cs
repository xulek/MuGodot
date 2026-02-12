using Client.Data.Texture;
using Godot;

namespace MuGodot;

/// <summary>
/// Converts MU Online texture data (OZJ/OZP/OZT) into Godot ImageTexture objects.
/// </summary>
public static class MuTextureHelper
{
    public readonly struct LoadedTexture
    {
        public LoadedTexture(ImageTexture texture, bool hasSourceAlpha)
        {
            Texture = texture;
            HasSourceAlpha = hasSourceAlpha;
        }

        public ImageTexture Texture { get; }
        public bool HasSourceAlpha { get; }
    }

    private static readonly OZJReader _ozjReader = new();
    private static readonly OZPReader _ozpReader = new();
    private static readonly OZTReader _oztReader = new();

    /// <summary>
    /// Load a texture file and return a Godot ImageTexture.
    /// Supports .ozj (JPEG), .ozp (PNG), .ozt (raw RGBA32) formats.
    /// </summary>
    public static async Task<ImageTexture?> LoadTextureAsync(string path, bool generateMipmaps = true)
    {
        var loaded = await LoadTextureWithInfoAsync(path, generateMipmaps);
        return loaded?.Texture;
    }

    /// <summary>
    /// Load a texture file and return texture plus source-alpha metadata.
    /// Source alpha follows MU logic: Components == 4.
    /// </summary>
    public static async Task<LoadedTexture?> LoadTextureWithInfoAsync(string path, bool generateMipmaps = true)
    {
        if (!System.IO.File.Exists(path))
            return null;

        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();

        TextureData? data = ext switch
        {
            ".ozj" or ".jpg" or ".jpeg" => await _ozjReader.Load(path),
            ".ozp" or ".png" => await _ozpReader.Load(path),
            ".ozt" or ".tga" => await _oztReader.Load(path),
            _ => null
        };

        if (data == null)
            return null;

        return new LoadedTexture(CreateImageTexture(data, generateMipmaps), data.Components == 4);
    }

    /// <summary>
    /// Convert TextureData (from Client.Data readers) to a Godot ImageTexture.
    /// </summary>
    public static ImageTexture CreateImageTexture(TextureData data, bool generateMipmaps = true)
    {
        Image image;

        if (data.Components == 3)
        {
            // RGB -> convert to RGBA for Godot
            var rgba = new byte[data.Width * data.Height * 4];
            for (int i = 0, j = 0; i < data.Data.Length; i += 3, j += 4)
            {
                rgba[j + 0] = data.Data[i + 0]; // R
                rgba[j + 1] = data.Data[i + 1]; // G
                rgba[j + 2] = data.Data[i + 2]; // B
                rgba[j + 3] = 255;               // A
            }
            image = Image.CreateFromData(data.Width, data.Height, false, Image.Format.Rgba8, rgba);
        }
        else
        {
            // Already RGBA
            image = Image.CreateFromData(data.Width, data.Height, false, Image.Format.Rgba8, data.Data);
        }

        if (generateMipmaps)
            image.GenerateMipmaps();

        return ImageTexture.CreateFromImage(image);
    }

    /// <summary>
    /// Create a Godot ImageTexture from OZB height/light map data (System.Drawing.Color array).
    /// </summary>
    public static ImageTexture CreateFromColors(System.Drawing.Color[] colors, int width, int height)
    {
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < colors.Length && i < width * height; i++)
        {
            rgba[i * 4 + 0] = colors[i].R;
            rgba[i * 4 + 1] = colors[i].G;
            rgba[i * 4 + 2] = colors[i].B;
            rgba[i * 4 + 3] = colors[i].A;
        }
        var image = Image.CreateFromData(width, height, false, Image.Format.Rgba8, rgba);
        return ImageTexture.CreateFromImage(image);
    }
}
