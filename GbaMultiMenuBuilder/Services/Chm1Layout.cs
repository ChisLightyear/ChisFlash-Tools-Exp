using System.Buffers.Binary;

namespace GbaMultiMenuBuilder.Services;

public static class Chm1Layout
{
    public const int BucketBytes = 0x400000;
    public const int SectorBytes = 0x20000;
    public const int ItemListOffset = 0xC0000;
    public const int StatusOffset = 0xE0000;
    public const int SaveAreaOffset = 0x140000;
    public const int MappingOffset = 0x3E0000;
    public const int SaveSlotCount = (MappingOffset - SaveAreaOffset) / SectorBytes;
    public const int MaxGameBuckets = 8;
    public const int MaxCapacity = 0x10000000;
    public const uint LocalSaveFlag = 2;
    private static readonly uint[] CrcTable = CreateCrcTable();

    public static int SaveAddress(int slot)
    {
        if (slot < 0 || slot >= SaveSlotCount) throw new ArgumentOutOfRangeException(nameof(slot));
        return SaveAreaOffset + slot * SectorBytes;
    }

    public static byte AllocateSaveSlot(IEnumerable<int> occupied, int slotCount = SaveSlotCount)
    {
        var used = occupied.ToHashSet();
        for (byte slot = 0; slot < slotCount; slot++)
            if (!used.Contains(slot)) return slot;
        throw new InvalidOperationException($"01G/02G fragment management has {slotCount} local save slots; none are free.");
    }

    public static byte[] PrepareSaveImage(byte[]? source)
    {
        if (source is null) return Enumerable.Repeat((byte)0xFF, SectorBytes).ToArray();
        if (source.Length > SectorBytes) throw new InvalidOperationException("01G/02G fragment management save data exceeds 128 KiB.");
        byte[] result = new byte[SectorBytes];
        source.CopyTo(result, 0);
        return result;
    }

    public static byte[] BuildMappingSector(IReadOnlyList<int[]> records, int capacityBytes)
    {
        if (capacityBytes is <= BucketBytes or > MaxCapacity || capacityBytes % BucketBytes != 0)
            throw new ArgumentOutOfRangeException(nameof(capacityBytes));
        if (records.Count > 512) throw new ArgumentOutOfRangeException(nameof(records));
        int buckets = Math.Max(32, capacityBytes / BucketBytes);
        int recordsOffset = 32 + buckets * 2;
        byte[] data = Enumerable.Repeat((byte)0xFF, SectorBytes).ToArray();
        "WLMI"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 1 | LocalSaveFlag);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 32);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(16), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(18), (ushort)buckets);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20), (uint)recordsOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(24), 32);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(26), 512);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(32), 0xFFFE);
        for (int slot = 0; slot < records.Count; slot++)
        {
            int[] map = records[slot];
            if (map.Length is < 1 or > MaxGameBuckets) throw new InvalidOperationException("01G/02G fragment management games require 1..8 mapped buckets.");
            int offset = recordsOffset + slot * 32;
            data[offset] = (byte)map.Length;
            for (int i = 0; i < map.Length; i++)
            {
                int bucket = map[i];
                if (bucket <= 0 || bucket >= capacityBytes / BucketBytes)
                    throw new InvalidOperationException($"01G/02G fragment management bucket {bucket} is outside the cartridge.");
                Span<byte> owner = data.AsSpan(32 + bucket * 2, 2);
                if (BinaryPrimitives.ReadUInt16LittleEndian(owner) != 0xFFFF)
                    throw new InvalidOperationException($"01G/02G fragment management bucket {bucket} belongs to multiple games.");
                BinaryPrimitives.WriteUInt16LittleEndian(owner, (ushort)slot);
                data[offset + 1 + i] = (byte)bucket;
            }
        }
        data.AsSpan(28, 4).Clear();
        uint crc = uint.MaxValue;
        foreach (byte value in data) crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(28), ~crc);
        return data;
    }

    private static uint[] CreateCrcTable()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint crc = i;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0);
            table[i] = crc;
        }
        return table;
    }
}
