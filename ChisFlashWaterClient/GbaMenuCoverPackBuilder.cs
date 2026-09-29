using System.Buffers.Binary;
using System.Drawing.Drawing2D;

namespace ChisFlashWaterClient;

public sealed record GbaMenuCoverSource(string GameCode, Bitmap Image);

public sealed class GbaMenuCoverPackBuildResult
{
    public byte[] PackData { get; init; } = [];
    public byte[] PaletteData { get; init; } = [];
    public Dictionary<int, byte[]> CoverPixelsByIndex { get; init; } = [];
}

public static class GbaMenuCoverPackBuilder
{
    public const int CoverWidth = 56;
    public const int CoverHeight = 56;
    public const int MaxCoverCount = 63;
    public const int PaletteBase = 32;
    private const int RedLevels = 6;
    private const int GreenLevels = 6;
    private const int BlueLevels = 5;
    private const int PaletteCount = RedLevels * GreenLevels * BlueLevels;
    private const int PackHeaderSize = 32;
    private const int EntrySize = 24;

    public static GbaMenuCoverPackBuildResult Build(IReadOnlyList<GbaMenuCoverSource> sources)
    {
        if (sources.Count == 0 || sources.Count > MaxCoverCount)
        {
            throw new InvalidOperationException($"A cover pack requires between 1 and {MaxCoverCount} covers.");
        }

        byte[] paletteData = BuildPaletteData();
        List<byte[]> pixels = sources.Select(source => Quantize(source.Image)).ToList();
        List<(byte[] Payload, uint Flags)> streams = pixels
            .Select(raw =>
            {
                byte[] compressed = CompressLz77(raw);
                return compressed.Length < raw.Length ? (compressed, 1u) : (raw, 0u);
            })
            .ToList();

        int paletteOffset = PackHeaderSize;
        int entriesOffset = Align4(paletteOffset + paletteData.Length);
        int dataOffset = Align4(entriesOffset + sources.Count * EntrySize);
        int cursor = dataOffset;
        int[] streamOffsets = new int[sources.Count];
        for (int index = sources.Count - 1; index >= 0; index--)
        {
            streamOffsets[index] = cursor;
            cursor = Align4(cursor + streams[index].Payload.Length);
        }

        byte[] pack = new byte[cursor];
        "GCOV"u8.CopyTo(pack);
        BinaryPrimitives.WriteUInt16LittleEndian(pack.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(pack.AsSpan(6), PackHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(pack.AsSpan(8), CoverWidth);
        BinaryPrimitives.WriteUInt16LittleEndian(pack.AsSpan(10), CoverHeight);
        BinaryPrimitives.WriteUInt16LittleEndian(pack.AsSpan(12), PaletteBase);
        BinaryPrimitives.WriteUInt16LittleEndian(pack.AsSpan(14), PaletteCount);
        BinaryPrimitives.WriteUInt16LittleEndian(pack.AsSpan(16), checked((ushort)sources.Count));
        BinaryPrimitives.WriteUInt16LittleEndian(pack.AsSpan(18), EntrySize);
        BinaryPrimitives.WriteUInt32LittleEndian(pack.AsSpan(20), (uint)paletteOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(pack.AsSpan(24), (uint)entriesOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(pack.AsSpan(28), (uint)dataOffset);
        paletteData.CopyTo(pack, paletteOffset);

        for (int index = 0; index < sources.Count; index++)
        {
            int entryOffset = entriesOffset + index * EntrySize;
            string code = NormalizeGameCode(sources[index].GameCode);
            System.Text.Encoding.ASCII.GetBytes(code).CopyTo(pack, entryOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(pack.AsSpan(entryOffset + 4), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(pack.AsSpan(entryOffset + 8), (uint)streamOffsets[index]);
            BinaryPrimitives.WriteUInt32LittleEndian(pack.AsSpan(entryOffset + 12), (uint)streams[index].Payload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(pack.AsSpan(entryOffset + 16), CoverWidth * CoverHeight);
            BinaryPrimitives.WriteUInt32LittleEndian(pack.AsSpan(entryOffset + 20), streams[index].Flags);
            streams[index].Payload.CopyTo(pack, streamOffsets[index]);
        }

        return new GbaMenuCoverPackBuildResult
        {
            PackData = pack,
            PaletteData = paletteData,
            CoverPixelsByIndex = pixels
                .Select((value, index) => (index, value))
                .ToDictionary(pair => pair.index, pair => pair.value),
        };
    }

    public static Bitmap FitCover(Image source)
    {
        Bitmap output = new(CoverWidth, CoverHeight);
        using Graphics graphics = Graphics.FromImage(output);
        graphics.Clear(Color.White);
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        float scale = Math.Min(CoverWidth / (float)source.Width, CoverHeight / (float)source.Height);
        int width = Math.Max(1, (int)Math.Round(source.Width * scale));
        int height = Math.Max(1, (int)Math.Round(source.Height * scale));
        int x = (CoverWidth - width) / 2;
        int y = (CoverHeight - height) / 2;
        graphics.DrawImage(source, new Rectangle(x, y, width, height));
        return output;
    }

    private static byte[] Quantize(Image source)
    {
        using Bitmap fitted = FitCover(source);
        byte[] output = new byte[CoverWidth * CoverHeight];
        for (int y = 0; y < CoverHeight; y++)
        {
            for (int x = 0; x < CoverWidth; x++)
            {
                Color color = fitted.GetPixel(x, y);
                if (color.R >= 250 && color.G >= 250 && color.B >= 250)
                {
                    output[y * CoverWidth + x] = 1;
                    continue;
                }

                int red = QuantizeLevel(color.R, RedLevels);
                int green = QuantizeLevel(color.G, GreenLevels);
                int blue = QuantizeLevel(color.B, BlueLevels);
                output[y * CoverWidth + x] = checked((byte)(PaletteBase + ((red * GreenLevels + green) * BlueLevels + blue)));
            }
        }

        return output;
    }

    private static byte[] BuildPaletteData()
    {
        byte[] output = new byte[PaletteCount * 2];
        int index = 0;
        for (int red = 0; red < RedLevels; red++)
        {
            for (int green = 0; green < GreenLevels; green++)
            {
                for (int blue = 0; blue < BlueLevels; blue++)
                {
                    int r5 = ScaleLevel(red, RedLevels, 31);
                    int g5 = ScaleLevel(green, GreenLevels, 31);
                    int b5 = ScaleLevel(blue, BlueLevels, 31);
                    ushort value = (ushort)(r5 | (g5 << 5) | (b5 << 10));
                    BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(index * 2), value);
                    index++;
                }
            }
        }

        return output;
    }

    private static byte[] CompressLz77(byte[] data)
    {
        List<byte> output = [0x10, (byte)data.Length, (byte)(data.Length >> 8), (byte)(data.Length >> 16)];
        int cursor = 0;
        while (cursor < data.Length)
        {
            int flagPosition = output.Count;
            output.Add(0);
            byte flags = 0;
            for (int token = 0; token < 8 && cursor < data.Length; token++)
            {
                int bestLength = 0;
                int bestDisplacement = 0;
                int searchStart = Math.Max(0, cursor - 0x1000);
                for (int candidate = cursor - 1; candidate >= searchStart; candidate--)
                {
                    int length = 0;
                    while (length < 18 && cursor + length < data.Length && data[candidate + length] == data[cursor + length])
                    {
                        length++;
                    }
                    if (length >= 3 && length > bestLength)
                    {
                        bestLength = length;
                        bestDisplacement = cursor - candidate - 1;
                        if (length == 18)
                        {
                            break;
                        }
                    }
                }

                if (bestLength >= 3)
                {
                    flags |= (byte)(1 << (7 - token));
                    output.Add((byte)(((bestLength - 3) << 4) | (bestDisplacement >> 8)));
                    output.Add((byte)bestDisplacement);
                    cursor += bestLength;
                }
                else
                {
                    output.Add(data[cursor++]);
                }
            }
            output[flagPosition] = flags;
        }
        return output.ToArray();
    }

    private static int QuantizeLevel(int value, int levels) =>
        Math.Clamp((value * levels) / 256, 0, levels - 1);

    private static int ScaleLevel(int value, int levels, int maximum) =>
        (value * maximum + ((levels - 1) / 2)) / (levels - 1);

    private static string NormalizeGameCode(string? value)
    {
        string code = new((value ?? string.Empty).Where(char.IsLetterOrDigit).Take(4).ToArray());
        return code.Length == 4 ? code.ToUpperInvariant() : "UNKN";
    }

    private static int Align4(int value) => (value + 3) & ~3;
}
