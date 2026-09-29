using System.Buffers.Binary;
using System.Drawing.Drawing2D;

namespace GbaMultiMenuBuilder.Services;

public sealed record GbaMenuCoverSource(string GameCode, Bitmap? Image);

public sealed record GbaMenuCoverPackBuildResult(byte[] PackData);

public static class GbaMenuCoverPackBuilder
{
    public const int CoverWidth = 56;
    public const int CoverHeight = 56;
    public const int MaxCoverCount = 63;
    public const int PaletteBase = 32;
    public const ushort IndexedFormatVersion = 1;
    public const ushort EntrySlotFormatVersion = 2;
    private const int RedLevels = 6;
    private const int GreenLevels = 6;
    private const int BlueLevels = 5;
    private const int PaletteCount = RedLevels * GreenLevels * BlueLevels;
    private const int PackHeaderSize = 32;
    private const int EntrySize = 24;

    public static GbaMenuCoverPackBuildResult Build(
        IReadOnlyList<GbaMenuCoverSource> sources,
        ushort formatVersion = IndexedFormatVersion)
    {
        if (sources.Count is < 1 or > MaxCoverCount)
        {
            throw new InvalidOperationException($"A cover pack requires between 1 and {MaxCoverCount} covers.");
        }

        var palette = BuildPaletteData();
        if (formatVersion is not (IndexedFormatVersion or EntrySlotFormatVersion))
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion));
        }

        var streams = sources.Select(source =>
        {
            if (source.Image is null)
            {
                return (Payload: Array.Empty<byte>(), Flags: 0u, RawSize: 0);
            }
            var raw = Quantize(source.Image);
            var compressed = CompressLz77(raw);
            return compressed.Length < raw.Length
                ? (Payload: compressed, Flags: 1u, RawSize: raw.Length)
                : (Payload: raw, Flags: 0u, RawSize: raw.Length);
        }).ToList();

        var paletteOffset = PackHeaderSize;
        var entriesOffset = Align4(paletteOffset + palette.Length);
        var dataOffset = Align4(entriesOffset + sources.Count * EntrySize);
        var cursor = dataOffset;
        var streamOffsets = new int[sources.Count];
        for (var index = sources.Count - 1; index >= 0; index--)
        {
            streamOffsets[index] = cursor;
            cursor = Align4(cursor + streams[index].Payload.Length);
        }

        var pack = new byte[cursor];
        "GCOV"u8.CopyTo(pack);
        BinaryPrimitives.WriteUInt16LittleEndian(pack.AsSpan(4), formatVersion);
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
        palette.CopyTo(pack, paletteOffset);

        for (var index = 0; index < sources.Count; index++)
        {
            var entryOffset = entriesOffset + index * EntrySize;
            System.Text.Encoding.ASCII.GetBytes(NormalizeGameCode(sources[index].GameCode)).CopyTo(pack, entryOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(pack.AsSpan(entryOffset + 8), (uint)streamOffsets[index]);
            BinaryPrimitives.WriteUInt32LittleEndian(pack.AsSpan(entryOffset + 12), (uint)streams[index].Payload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(pack.AsSpan(entryOffset + 16), (uint)streams[index].RawSize);
            BinaryPrimitives.WriteUInt32LittleEndian(pack.AsSpan(entryOffset + 20), streams[index].Flags);
            streams[index].Payload.CopyTo(pack, streamOffsets[index]);
        }
        return new GbaMenuCoverPackBuildResult(pack);
    }

    public static byte[] Quantize(Image source)
    {
        using var fitted = FitCover(source);
        var output = new byte[CoverWidth * CoverHeight];
        for (var y = 0; y < CoverHeight; y++)
        for (var x = 0; x < CoverWidth; x++)
        {
            var color = fitted.GetPixel(x, y);
            if (color.R >= 250 && color.G >= 250 && color.B >= 250)
            {
                output[y * CoverWidth + x] = 1;
                continue;
            }
            var red = QuantizeLevel(color.R, RedLevels);
            var green = QuantizeLevel(color.G, GreenLevels);
            var blue = QuantizeLevel(color.B, BlueLevels);
            output[y * CoverWidth + x] = checked((byte)(PaletteBase + ((red * GreenLevels + green) * BlueLevels + blue)));
        }
        return output;
    }

    public static Bitmap FitCover(Image source)
    {
        var output = new Bitmap(CoverWidth, CoverHeight);
        using var graphics = Graphics.FromImage(output);
        graphics.Clear(Color.White);
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var scale = Math.Min(CoverWidth / (float)source.Width, CoverHeight / (float)source.Height);
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        graphics.DrawImage(source, new Rectangle((CoverWidth - width) / 2, (CoverHeight - height) / 2, width, height));
        return output;
    }

    public static byte[] BuildPaletteData()
    {
        var output = new byte[PaletteCount * 2];
        var index = 0;
        for (var red = 0; red < RedLevels; red++)
        for (var green = 0; green < GreenLevels; green++)
        for (var blue = 0; blue < BlueLevels; blue++)
        {
            var value = (ushort)(ScaleLevel(red, RedLevels, 31) |
                (ScaleLevel(green, GreenLevels, 31) << 5) |
                (ScaleLevel(blue, BlueLevels, 31) << 10));
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(index++ * 2), value);
        }
        return output;
    }

    private static byte[] CompressLz77(byte[] data)
    {
        var output = new List<byte> { 0x10, (byte)data.Length, (byte)(data.Length >> 8), (byte)(data.Length >> 16) };
        var cursor = 0;
        while (cursor < data.Length)
        {
            var flagPosition = output.Count;
            output.Add(0);
            byte flags = 0;
            for (var token = 0; token < 8 && cursor < data.Length; token++)
            {
                var bestLength = 0;
                var bestDisplacement = 0;
                for (var candidate = cursor - 1; candidate >= Math.Max(0, cursor - 0x1000); candidate--)
                {
                    var length = 0;
                    while (length < 18 && cursor + length < data.Length && data[candidate + length] == data[cursor + length]) length++;
                    if (length < 3 || length <= bestLength) continue;
                    bestLength = length;
                    bestDisplacement = cursor - candidate - 1;
                    if (length == 18) break;
                }
                if (bestLength >= 3)
                {
                    flags |= (byte)(1 << (7 - token));
                    output.Add((byte)(((bestLength - 3) << 4) | (bestDisplacement >> 8)));
                    output.Add((byte)bestDisplacement);
                    cursor += bestLength;
                }
                else output.Add(data[cursor++]);
            }
            output[flagPosition] = flags;
        }
        return output.ToArray();
    }

    private static int QuantizeLevel(int value, int levels) => Math.Clamp((value * levels) / 256, 0, levels - 1);
    private static int ScaleLevel(int value, int levels, int maximum) => (value * maximum + ((levels - 1) / 2)) / (levels - 1);
    private static string NormalizeGameCode(string? value)
    {
        var code = new string((value ?? string.Empty).Where(char.IsLetterOrDigit).Take(4).ToArray());
        return code.Length == 4 ? code.ToUpperInvariant() : "UNKN";
    }
    private static int Align4(int value) => (value + 3) & ~3;
}
