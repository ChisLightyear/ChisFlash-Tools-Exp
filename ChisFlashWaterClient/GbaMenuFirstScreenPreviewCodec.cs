using System.Buffers.Binary;

namespace ChisFlashWaterClient;

public static class GbaMenuFirstScreenPreviewCodec
{
    private const int TrailerOffset = 0x0AFFF0;
    private const uint TrailerMagic = 0x54564347; // GCVT
    private const uint PackMagic = 0x564F4347; // GCOV
    private const ushort FormatVersion = 1;
    private const int TrailerSize = 16;
    private const int PackHeaderSize = 32;
    private const int PackEntrySize = 24;
    private const int CoverWidth = 56;
    private const int CoverHeight = 56;
    private const int CoverPixelCount = CoverWidth * CoverHeight;
    private const uint EntryFlagLz77 = 1;
    private const byte ItemCoverPresentFlag = 1;

    private readonly record struct CoverPackLayout(
        int PackOffset,
        int AllocatedSize,
        int UsedSize,
        int CoverCount);

    public static bool TryCopyCoverPack(
        ReadOnlySpan<byte> sourceMenuArea,
        Span<byte> targetHeadArea,
        out int coverCount,
        out int copiedBytes)
    {
        coverCount = 0;
        copiedBytes = 0;
        if (!TryReadCoverPackLayout(sourceMenuArea, out CoverPackLayout sourceLayout) ||
            !TryReadCoverPackLayout(targetHeadArea, out CoverPackLayout targetLayout) ||
            sourceLayout.UsedSize > targetLayout.AllocatedSize)
        {
            return false;
        }

        targetHeadArea.Slice(targetLayout.PackOffset, targetLayout.AllocatedSize).Clear();
        sourceMenuArea
            .Slice(sourceLayout.PackOffset, sourceLayout.UsedSize)
            .CopyTo(targetHeadArea[targetLayout.PackOffset..]);

        coverCount = sourceLayout.CoverCount;
        copiedBytes = sourceLayout.UsedSize;
        return true;
    }

    public static GbaMenuFirstScreenPreviewInfo Decode(
        byte[] menuArea,
        IReadOnlyList<MenuItemConfigInfo> items)
    {
        ArgumentNullException.ThrowIfNull(menuArea);
        return Decode(menuArea.AsSpan(), items);
    }

    public static GbaMenuFirstScreenPreviewInfo Decode(
        ReadOnlySpan<byte> menuArea,
        IReadOnlyList<MenuItemConfigInfo> items)
    {
        var result = new GbaMenuFirstScreenPreviewInfo();
        if (menuArea.Length < TrailerOffset + TrailerSize)
        {
            return result;
        }

        ReadOnlySpan<byte> trailer = menuArea.Slice(TrailerOffset, TrailerSize);
        if (BinaryPrimitives.ReadUInt32LittleEndian(trailer) != TrailerMagic ||
            BinaryPrimitives.ReadUInt16LittleEndian(trailer[4..]) != FormatVersion ||
            BinaryPrimitives.ReadUInt16LittleEndian(trailer[6..]) != TrailerSize)
        {
            return result;
        }

        int packOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(trailer[8..]));
        int packSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(trailer[12..]));
        if (packOffset < 0 || packSize < PackHeaderSize ||
            packOffset > TrailerOffset || packSize != TrailerOffset - packOffset ||
            menuArea.Length - packOffset < packSize)
        {
            return result;
        }

        ReadOnlySpan<byte> pack = menuArea.Slice(packOffset, packSize);
        if (BinaryPrimitives.ReadUInt32LittleEndian(pack) != PackMagic ||
            BinaryPrimitives.ReadUInt16LittleEndian(pack[4..]) != FormatVersion ||
            BinaryPrimitives.ReadUInt16LittleEndian(pack[6..]) != PackHeaderSize ||
            BinaryPrimitives.ReadUInt16LittleEndian(pack[8..]) != CoverWidth ||
            BinaryPrimitives.ReadUInt16LittleEndian(pack[10..]) != CoverHeight)
        {
            return result;
        }

        int paletteBase = BinaryPrimitives.ReadUInt16LittleEndian(pack[12..]);
        int paletteCount = BinaryPrimitives.ReadUInt16LittleEndian(pack[14..]);
        int coverCount = BinaryPrimitives.ReadUInt16LittleEndian(pack[16..]);
        int entrySize = BinaryPrimitives.ReadUInt16LittleEndian(pack[18..]);
        int paletteOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(pack[20..]));
        int entriesOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(pack[24..]));
        int dataOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(pack[28..]));
        int paletteByteCount = checked(paletteCount * 2);
        if (paletteBase < 0 || paletteBase + paletteCount > 256 || paletteCount <= 0 ||
            coverCount <= 0 || entrySize != PackEntrySize ||
            !ContainsRange(pack.Length, paletteOffset, paletteByteCount) ||
            !ContainsRange(pack.Length, entriesOffset, checked(coverCount * entrySize)) ||
            paletteOffset + paletteByteCount > entriesOffset ||
            entriesOffset + coverCount * entrySize > dataOffset || dataOffset > pack.Length)
        {
            return result;
        }

        HashSet<int> referencedCoverIndexes = items
            .Where(item => (item.CoverFlags & ItemCoverPresentFlag) != 0 && item.CoverIndex != 0xFF)
            .Select(item => (int)item.CoverIndex)
            .Where(index => index < coverCount)
            .ToHashSet();

        Dictionary<int, byte[]> covers = [];
        Dictionary<int, string> gameCodes = [];
        foreach (int coverIndex in referencedCoverIndexes)
        {
            int entryOffset = entriesOffset + coverIndex * entrySize;
            ReadOnlySpan<byte> entry = pack.Slice(entryOffset, entrySize);
            string gameCode = System.Text.Encoding.ASCII.GetString(entry[..4]);
            if (gameCode.All(char.IsLetterOrDigit))
            {
                gameCodes[coverIndex] = gameCode;
            }
            int streamOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]));
            int storedSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]));
            int rawSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(entry[16..]));
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(entry[20..]);
            if (rawSize != CoverPixelCount || streamOffset < dataOffset ||
                !ContainsRange(pack.Length, streamOffset, storedSize))
            {
                continue;
            }

            ReadOnlySpan<byte> payload = pack.Slice(streamOffset, storedSize);
            byte[]? pixels = (flags & EntryFlagLz77) != 0
                ? TryDecompressGbaLz77(payload, CoverPixelCount)
                : payload.Length == CoverPixelCount ? payload.ToArray() : null;
            if (pixels is not null)
            {
                covers[coverIndex] = pixels;
            }
        }

        result.IsDetected = true;
        result.PackOffset = packOffset;
        result.PackSize = packSize;
        result.PaletteBase = paletteBase;
        result.PaletteData = pack.Slice(paletteOffset, paletteByteCount).ToArray();
        result.GameCodesByIndex = gameCodes;
        result.CoverPixelsByIndex = covers;
        return result;
    }

    private static byte[]? TryDecompressGbaLz77(ReadOnlySpan<byte> input, int expectedSize)
    {
        if (input.Length < 4 || input[0] != 0x10)
        {
            return null;
        }

        int decodedSize = input[1] | (input[2] << 8) | (input[3] << 16);
        if (decodedSize != expectedSize)
        {
            return null;
        }

        byte[] output = new byte[decodedSize];
        int source = 4;
        int destination = 0;
        while (destination < output.Length)
        {
            if (source >= input.Length)
            {
                return null;
            }

            byte control = input[source++];
            for (int bit = 7; bit >= 0 && destination < output.Length; bit--)
            {
                if ((control & (1 << bit)) == 0)
                {
                    if (source >= input.Length)
                    {
                        return null;
                    }

                    output[destination++] = input[source++];
                    continue;
                }

                if (source + 1 >= input.Length)
                {
                    return null;
                }

                int first = input[source++];
                int second = input[source++];
                int length = (first >> 4) + 3;
                int displacement = ((first & 0x0F) << 8) | second;
                int copySource = destination - displacement - 1;
                if (copySource < 0)
                {
                    return null;
                }

                for (int count = 0; count < length && destination < output.Length; count++)
                {
                    output[destination++] = output[copySource++];
                }
            }
        }

        return output;
    }

    private static bool TryReadCoverPackLayout(
        ReadOnlySpan<byte> menuArea,
        out CoverPackLayout layout)
    {
        layout = default;
        if (menuArea.Length < TrailerOffset + TrailerSize)
        {
            return false;
        }

        ReadOnlySpan<byte> trailer = menuArea.Slice(TrailerOffset, TrailerSize);
        uint rawPackOffset = BinaryPrimitives.ReadUInt32LittleEndian(trailer[8..]);
        uint rawPackSize = BinaryPrimitives.ReadUInt32LittleEndian(trailer[12..]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(trailer) != TrailerMagic ||
            BinaryPrimitives.ReadUInt16LittleEndian(trailer[4..]) != FormatVersion ||
            BinaryPrimitives.ReadUInt16LittleEndian(trailer[6..]) != TrailerSize ||
            rawPackOffset > int.MaxValue || rawPackSize > int.MaxValue)
        {
            return false;
        }

        int packOffset = (int)rawPackOffset;
        int allocatedSize = (int)rawPackSize;
        if (packOffset < 0 || allocatedSize < PackHeaderSize ||
            packOffset > TrailerOffset || allocatedSize != TrailerOffset - packOffset ||
            menuArea.Length - packOffset < allocatedSize)
        {
            return false;
        }

        ReadOnlySpan<byte> pack = menuArea.Slice(packOffset, allocatedSize);
        if (BinaryPrimitives.ReadUInt32LittleEndian(pack) != PackMagic ||
            BinaryPrimitives.ReadUInt16LittleEndian(pack[4..]) != FormatVersion ||
            BinaryPrimitives.ReadUInt16LittleEndian(pack[6..]) != PackHeaderSize ||
            BinaryPrimitives.ReadUInt16LittleEndian(pack[8..]) != CoverWidth ||
            BinaryPrimitives.ReadUInt16LittleEndian(pack[10..]) != CoverHeight)
        {
            return false;
        }

        try
        {
            int paletteCount = BinaryPrimitives.ReadUInt16LittleEndian(pack[14..]);
            int coverCount = BinaryPrimitives.ReadUInt16LittleEndian(pack[16..]);
            int entrySize = BinaryPrimitives.ReadUInt16LittleEndian(pack[18..]);
            int paletteOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(pack[20..]));
            int entriesOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(pack[24..]));
            int dataOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(pack[28..]));
            int paletteByteCount = checked(paletteCount * 2);
            int entriesByteCount = checked(coverCount * entrySize);
            if (paletteCount <= 0 || coverCount <= 0 || entrySize != PackEntrySize ||
                !ContainsRange(pack.Length, paletteOffset, paletteByteCount) ||
                !ContainsRange(pack.Length, entriesOffset, entriesByteCount) ||
                paletteOffset > entriesOffset || paletteByteCount > entriesOffset - paletteOffset ||
                entriesOffset > dataOffset || entriesByteCount > dataOffset - entriesOffset ||
                dataOffset > pack.Length)
            {
                return false;
            }

            int usedSize = dataOffset;
            for (int index = 0; index < coverCount; index++)
            {
                ReadOnlySpan<byte> entry = pack.Slice(entriesOffset + index * entrySize, entrySize);
                int streamOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]));
                int storedSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]));
                int streamEnd = checked(streamOffset + storedSize);
                if (streamOffset < dataOffset || !ContainsRange(pack.Length, streamOffset, storedSize))
                {
                    return false;
                }

                usedSize = Math.Max(usedSize, Align4(streamEnd));
            }

            if (usedSize > allocatedSize)
            {
                return false;
            }

            layout = new CoverPackLayout(packOffset, allocatedSize, usedSize, coverCount);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static int Align4(int value) => checked((value + 3) & ~3);

    private static bool ContainsRange(int totalLength, int offset, int length) =>
        offset >= 0 && length >= 0 && offset <= totalLength && totalLength - offset >= length;
}
