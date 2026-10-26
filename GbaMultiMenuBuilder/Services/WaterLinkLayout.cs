using System.Buffers.Binary;

namespace GbaMultiMenuBuilder.Services;

public static class WaterLinkLayout
{
    public const int BucketBytes = 0x400000;
    public const int MappingOffset = 0x3E0000;
    public const int SectorBytes = 0x20000;
    public const int SaveAreaOffset = 0x140000;
    public const int MaxBuckets = 160;
    public const uint ChimeraFlag = 4;

    public static byte[] BuildMappingSector(IReadOnlyList<int[]> maps, int capacityBytes)
    {
        int buckets = capacityBytes / BucketBytes;
        if (capacityBytes % BucketBytes != 0 || buckets < 32 || buckets > MaxBuckets || maps.Count > 512)
            throw new InvalidDataException("水线映射容量或目录项数量超限。");
        byte[] sector = Enumerable.Repeat((byte)255, SectorBytes).ToArray();
        int recordOffset = 32 + buckets * 2;
        ushort[] owners = Enumerable.Repeat((ushort)0xFFFF, buckets).ToArray();
        owners[0] = 0xFFFE;
        for (int slot = 0; slot < maps.Count; slot++)
        {
            int[] map = maps[slot];
            if (map.Length < 1 || map.Length > 8 || map.Distinct().Count() != map.Length)
                throw new InvalidDataException("水线单游戏映射须包含1～8个不同的4 MiB块。");
            int record = recordOffset + slot * 32;
            sector[record] = (byte)map.Length;
            for (int index = 0; index < map.Length; index++)
            {
                int bucket = map[index];
                if (bucket <= 0 || bucket >= buckets || owners[bucket] != 0xFFFF)
                    throw new InvalidDataException("水线物理块越界或重复分配。");
                owners[bucket] = (ushort)slot;
                sector[record + 1 + index] = (byte)bucket;
            }
        }
        var header = sector.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x494D4C57);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], 32);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], ChimeraFlag);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], 32);
        BinaryPrimitives.WriteUInt16LittleEndian(header[16..], 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header[18..], (ushort)buckets);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], (uint)recordOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(header[24..], 32);
        BinaryPrimitives.WriteUInt16LittleEndian(header[26..], 512);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], 0);
        for (int bucket = 0; bucket < buckets; bucket++)
            BinaryPrimitives.WriteUInt16LittleEndian(header[(32 + bucket * 2)..], owners[bucket]);
        uint crc = uint.MaxValue;
        foreach (byte value in sector)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], ~crc);
        return sector;
    }
}
