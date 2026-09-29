using System.Drawing;

namespace GbaMultiMenuBuilder.Services;

public static class BackgroundImageEncoder
{
    public const int Width = 240;
    public const int Height = 160;
    public const int MaxBackgroundColors = 240;

    public static (byte[] BitmapData, byte[] PaletteData) Encode(string imagePath)
    {
        using var bitmap = new Bitmap(imagePath);
        if (bitmap.Width != Width || bitmap.Height != Height)
        {
            throw new InvalidOperationException("Background image must be exactly 240x160.");
        }

        var pixels = new byte[Width * Height];
        var paletteMap = new Dictionary<int, byte>();
        var paletteColors = new List<int>(MaxBackgroundColors);

        var pos = 0;
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var color = bitmap.GetPixel(x, y);
                var argb = color.ToArgb();
                if (!paletteMap.TryGetValue(argb, out var index))
                {
                    if (paletteColors.Count >= MaxBackgroundColors)
                    {
                        throw new InvalidOperationException($"Background image uses more than {MaxBackgroundColors} colors.");
                    }

                    index = (byte)paletteColors.Count;
                    paletteMap[argb] = index;
                    paletteColors.Add(argb);
                }

                pixels[pos++] = index;
            }
        }

        var paletteBytes = new byte[0x200];
        for (var i = 0; i < paletteColors.Count; i++)
        {
            var color = Color.FromArgb(paletteColors[i]);
            var rgb555 = (ushort)(((color.B >> 3) << 10) | ((color.G >> 3) << 5) | (color.R >> 3));
            paletteBytes[i * 2] = (byte)(rgb555 & 0xFF);
            paletteBytes[(i * 2) + 1] = (byte)(rgb555 >> 8);
        }

        return (pixels, paletteBytes);
    }
}
