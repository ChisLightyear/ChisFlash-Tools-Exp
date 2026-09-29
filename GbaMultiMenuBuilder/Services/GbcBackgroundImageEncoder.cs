using System.Drawing;

namespace GbaMultiMenuBuilder.Services;

internal static class GbcBackgroundImageEncoder
{
    internal const int Width = 160;
    internal const int Height = 144;
    internal const int MaxTiles = 190;
    internal const int PaletteCount = 7;

    internal sealed record Result(
        byte[] TileData,
        byte[] Tilemap,
        byte[] Attributes,
        byte[] PaletteData,
        int TileCount);

    private readonly record struct Rgb5(byte R, byte G, byte B);

    internal static Result Encode(string imagePath)
    {
        using var bitmap = new Bitmap(imagePath);
        if (bitmap.Width != Width || bitmap.Height != Height)
        {
            throw new InvalidOperationException("GBC background image must be exactly 160x144.");
        }

        var tiles = ReadTiles(bitmap);
        var palettes = BuildPalettes(tiles);
        var assignments = AssignTiles(tiles, palettes);
        var patterns = new byte[tiles.Length][];
        for (var i = 0; i < tiles.Length; i++)
        {
            patterns[i] = EncodePattern(tiles[i], palettes[assignments[i]]);
        }

        var (uniquePatterns, tilemap) = ReducePatterns(patterns);
        var tileData = new byte[uniquePatterns.Count * 16];
        for (var i = 0; i < uniquePatterns.Count; i++)
        {
            WriteTile(uniquePatterns[i], tileData, i * 16);
        }

        var attributes = assignments.Select(static value => (byte)value).ToArray();
        var paletteData = new byte[PaletteCount * 8];
        for (var paletteIndex = 0; paletteIndex < PaletteCount; paletteIndex++)
        {
            for (var colorIndex = 0; colorIndex < 3; colorIndex++)
            {
                WriteColor(paletteData, (paletteIndex * 8) + (colorIndex * 2), palettes[paletteIndex][colorIndex]);
            }
            WriteColor(paletteData, (paletteIndex * 8) + 6, new Rgb5(31, 31, 31));
        }

        return new Result(tileData, tilemap, attributes, paletteData, uniquePatterns.Count);
    }

    private static Rgb5[][] ReadTiles(Bitmap bitmap)
    {
        var tiles = new Rgb5[(Width / 8) * (Height / 8)][];
        for (var tileY = 0; tileY < Height / 8; tileY++)
        {
            for (var tileX = 0; tileX < Width / 8; tileX++)
            {
                var pixels = new Rgb5[64];
                for (var y = 0; y < 8; y++)
                {
                    for (var x = 0; x < 8; x++)
                    {
                        var color = bitmap.GetPixel((tileX * 8) + x, (tileY * 8) + y);
                        var alpha = color.A;
                        pixels[(y * 8) + x] = new Rgb5(
                            (byte)(((color.R * alpha / 255) >> 3) & 31),
                            (byte)(((color.G * alpha / 255) >> 3) & 31),
                            (byte)(((color.B * alpha / 255) >> 3) & 31));
                    }
                }
                tiles[(tileY * (Width / 8)) + tileX] = pixels;
            }
        }
        return tiles;
    }

    private static Rgb5[][] BuildPalettes(Rgb5[][] tiles)
    {
        var averages = tiles.Select(Average).ToArray();
        var seedIndexes = new List<int> { 0 };
        while (seedIndexes.Count < PaletteCount)
        {
            var bestIndex = 0;
            var bestDistance = -1;
            for (var i = 0; i < averages.Length; i++)
            {
                var distance = seedIndexes.Min(seed => Distance(averages[i], averages[seed]));
                if (distance > bestDistance)
                {
                    bestDistance = distance;
                    bestIndex = i;
                }
            }
            seedIndexes.Add(bestIndex);
        }

        var palettes = seedIndexes.Select(index => Quantize(tiles[index])).ToArray();
        for (var iteration = 0; iteration < 8; iteration++)
        {
            var assignments = AssignTiles(tiles, palettes);
            for (var paletteIndex = 0; paletteIndex < PaletteCount; paletteIndex++)
            {
                var pixels = new List<Rgb5>();
                for (var tileIndex = 0; tileIndex < tiles.Length; tileIndex++)
                {
                    if (assignments[tileIndex] == paletteIndex) pixels.AddRange(tiles[tileIndex]);
                }
                if (pixels.Count != 0) palettes[paletteIndex] = Quantize(pixels);
            }
        }
        return palettes;
    }

    private static int[] AssignTiles(Rgb5[][] tiles, Rgb5[][] palettes)
    {
        var assignments = new int[tiles.Length];
        for (var tileIndex = 0; tileIndex < tiles.Length; tileIndex++)
        {
            var bestPalette = 0;
            var bestError = long.MaxValue;
            for (var paletteIndex = 0; paletteIndex < palettes.Length; paletteIndex++)
            {
                long error = 0;
                foreach (var pixel in tiles[tileIndex])
                {
                    error += palettes[paletteIndex].Min(color => Distance(pixel, color));
                }
                if (error < bestError)
                {
                    bestError = error;
                    bestPalette = paletteIndex;
                }
            }
            assignments[tileIndex] = bestPalette;
        }
        return assignments;
    }

    private static Rgb5[] Quantize(IEnumerable<Rgb5> pixels)
    {
        var histogram = pixels
            .GroupBy(static color => color)
            .Select(static group => (Color: group.Key, Weight: group.Count()))
            .OrderByDescending(static item => item.Weight)
            .ToArray();
        var centers = new List<Rgb5> { histogram[0].Color };
        while (centers.Count < 3)
        {
            var candidate = histogram
                .OrderByDescending(item => item.Weight * centers.Min(center => Distance(item.Color, center)))
                .First().Color;
            centers.Add(candidate);
        }

        for (var iteration = 0; iteration < 8; iteration++)
        {
            var sumR = new long[3];
            var sumG = new long[3];
            var sumB = new long[3];
            var weights = new long[3];
            foreach (var item in histogram)
            {
                var index = Enumerable.Range(0, 3).MinBy(i => Distance(item.Color, centers[i]));
                sumR[index] += item.Color.R * (long)item.Weight;
                sumG[index] += item.Color.G * (long)item.Weight;
                sumB[index] += item.Color.B * (long)item.Weight;
                weights[index] += item.Weight;
            }
            for (var i = 0; i < 3; i++)
            {
                if (weights[i] == 0) continue;
                centers[i] = new Rgb5(
                    (byte)(sumR[i] / weights[i]),
                    (byte)(sumG[i] / weights[i]),
                    (byte)(sumB[i] / weights[i]));
            }
        }

        return centers.OrderBy(static color => (color.R * 3) + (color.G * 6) + color.B).ToArray();
    }

    private static byte[] EncodePattern(Rgb5[] pixels, Rgb5[] palette)
    {
        var pattern = new byte[64];
        for (var i = 0; i < pixels.Length; i++)
        {
            pattern[i] = (byte)Enumerable.Range(0, 3).MinBy(index => Distance(pixels[i], palette[index]));
        }
        return pattern;
    }

    private static (List<byte[]> Patterns, byte[] Tilemap) ReducePatterns(byte[][] patterns)
    {
        var unique = new List<byte[]>();
        var exactIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        var sourceUniqueIndex = new int[patterns.Length];
        for (var i = 0; i < patterns.Length; i++)
        {
            var key = Convert.ToHexString(patterns[i]);
            if (!exactIndexes.TryGetValue(key, out var index))
            {
                index = unique.Count;
                exactIndexes.Add(key, index);
                unique.Add(patterns[i]);
            }
            sourceUniqueIndex[i] = index;
        }

        if (unique.Count <= MaxTiles)
        {
            return (unique, sourceUniqueIndex.Select(static index => (byte)index).ToArray());
        }

        var representatives = new List<byte[]>(MaxTiles);
        for (var i = 0; i < MaxTiles; i++)
        {
            representatives.Add(unique[i * (unique.Count - 1) / (MaxTiles - 1)]);
        }

        var reducedIndex = new int[unique.Count];
        for (var i = 0; i < unique.Count; i++)
        {
            reducedIndex[i] = Enumerable.Range(0, representatives.Count)
                .MinBy(index => PatternDistance(unique[i], representatives[index]));
        }
        return (representatives, sourceUniqueIndex.Select(index => (byte)reducedIndex[index]).ToArray());
    }

    private static int PatternDistance(byte[] left, byte[] right)
    {
        var distance = 0;
        for (var i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i]) distance++;
        }
        return distance;
    }

    private static void WriteTile(byte[] pattern, byte[] output, int offset)
    {
        for (var y = 0; y < 8; y++)
        {
            byte low = 0;
            byte high = 0;
            for (var x = 0; x < 8; x++)
            {
                var value = pattern[(y * 8) + x];
                var bit = 7 - x;
                low |= (byte)((value & 1) << bit);
                high |= (byte)(((value >> 1) & 1) << bit);
            }
            output[offset + (y * 2)] = low;
            output[offset + (y * 2) + 1] = high;
        }
    }

    private static void WriteColor(byte[] output, int offset, Rgb5 color)
    {
        var value = (ushort)(color.R | (color.G << 5) | (color.B << 10));
        output[offset] = (byte)value;
        output[offset + 1] = (byte)(value >> 8);
    }

    private static Rgb5 Average(Rgb5[] pixels) => new(
        (byte)pixels.Average(static color => color.R),
        (byte)pixels.Average(static color => color.G),
        (byte)pixels.Average(static color => color.B));

    private static int Distance(Rgb5 left, Rgb5 right)
    {
        var dr = left.R - right.R;
        var dg = left.G - right.G;
        var db = left.B - right.B;
        return (dr * dr) + (dg * dg) + (db * db);
    }
}
