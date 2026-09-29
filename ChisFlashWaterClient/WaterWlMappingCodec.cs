using System.Buffers.Binary;
using ChisFlashWaterClient.Services;

namespace ChisFlashWaterClient;

public static class WaterWlMappingCodec
{
    public const int Offset = 0x3E0000;
    public const int SectorSize = 0x20000;
    public const uint Magic = 0x494D4C57;
    public const ushort LegacyVersion = 0x0001;
    public const ushort ChecksummedVersion = 0x0002;
    public const ushort Version = ChecksummedVersion;
    public const int MinimumBucketCount = 32;
    public const int BucketCount = 128;
    public const int RomMapRecordCount = 512;
    public const int RomMapRecordSize = 32;
    public const int RomPhysicalBucketCount = BucketCount - 1;
    public const int MaxLogicalBucketCount = RomMapRecordSize - 1;
    public const ushort BucketOwnerFree = 0xFFFF;
    public const ushort BucketOwnerSystem = 0xFFFE;
    public const int HeaderSize = 32;
    public const int MappingChecksumOffset = 0x1C;

    private static readonly uint[] Crc32Table = BuildCrc32Table();

    public static bool TryReadHeader(byte[] sectorData, out WlMappingHeaderInfo header)
    {
        header = new WlMappingHeaderInfo();

        if (sectorData is null || sectorData.Length < HeaderSize)
        {
            return false;
        }

        header.Magic = BinaryPrimitives.ReadUInt32LittleEndian(sectorData.AsSpan(0x00, 4));
        header.Version = BinaryPrimitives.ReadUInt16LittleEndian(sectorData.AsSpan(0x04, 2));
        header.HeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(sectorData.AsSpan(0x06, 2));
        header.Flags = BinaryPrimitives.ReadUInt32LittleEndian(sectorData.AsSpan(0x08, 4));
        header.BucketOwnerOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(sectorData.AsSpan(0x0C, 4));
        header.BucketOwnerEntrySize = BinaryPrimitives.ReadUInt16LittleEndian(sectorData.AsSpan(0x10, 2));
        header.BucketOwnerCount = BinaryPrimitives.ReadUInt16LittleEndian(sectorData.AsSpan(0x12, 2));
        header.RomMapRecordOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(sectorData.AsSpan(0x14, 4));
        header.RomMapRecordSize = BinaryPrimitives.ReadUInt16LittleEndian(sectorData.AsSpan(0x18, 2));
        header.RomMapRecordCount = BinaryPrimitives.ReadUInt16LittleEndian(sectorData.AsSpan(0x1A, 2));
        header.MappingChecksum = BinaryPrimitives.ReadUInt32LittleEndian(sectorData.AsSpan(MappingChecksumOffset, sizeof(uint)));
        return true;
    }

    public static bool TryParse(byte[] sectorData, int sourceAddress, bool usingBackupCopy, out WaterWlMappingInfo info) =>
        TryParse(sectorData, sourceAddress, usingBackupCopy, out info, out _);

    public static bool TryParse(
        byte[] sectorData,
        int sourceAddress,
        bool usingBackupCopy,
        out WaterWlMappingInfo info,
        out string errorMessage)
    {
        info = new WaterWlMappingInfo
        {
            SourceAddress = sourceAddress,
            UsingBackupCopy = usingBackupCopy,
            RawSector = sectorData?.ToArray() ?? [],
        };
        errorMessage = string.Empty;

        if (sectorData is null || sectorData.Length < SectorSize)
        {
            errorMessage = LocalizedText.T("error", "wl_sector_length_invalid", "WL mapping sector data length is invalid.");
            return false;
        }

        if (!TryReadHeader(sectorData, out WlMappingHeaderInfo header))
        {
            errorMessage = LocalizedText.T("error", "wl_sector_length_invalid", "WL mapping sector data length is invalid.");
            return false;
        }

        uint magic = header.Magic;
        ushort version = header.Version;
        ushort headerSize = header.HeaderSize;
        uint flags = header.Flags;
        uint bucketOwnerOffset = (uint)header.BucketOwnerOffset;
        ushort bucketOwnerEntrySize = header.BucketOwnerEntrySize;
        ushort bucketOwnerCount = header.BucketOwnerCount;
        uint romMapRecordOffset = (uint)header.RomMapRecordOffset;
        ushort romMapRecordSize = header.RomMapRecordSize;
        ushort romMapRecordCount = header.RomMapRecordCount;
        uint mappingChecksum = header.MappingChecksum;

        if (magic != Magic)
        {
            errorMessage = LocalizedText.T("error", "wl_sector_format_invalid", "WL mapping sector format is invalid.");
            return false;
        }

        if (version == LegacyVersion)
        {
            errorMessage = LocalizedText.T(
                "error",
                "wl_sector_legacy_requires_repair",
                "Detected an old WL mapping format without checksum. Please run Repair Magic.");
            return false;
        }

        if (version != ChecksummedVersion)
        {
            errorMessage = LocalizedText.Tf(
                "error",
                "wl_sector_version_unsupported",
                "WL mapping sector version 0x{0:X4} is unsupported.",
                version);
            return false;
        }

        if (headerSize < HeaderSize ||
            bucketOwnerEntrySize != sizeof(ushort) ||
            bucketOwnerCount < MinimumBucketCount ||
            romMapRecordSize != RomMapRecordSize ||
            romMapRecordCount < RomMapRecordCount)
        {
            errorMessage = LocalizedText.T("error", "wl_sector_format_invalid", "WL mapping sector format is invalid.");
            return false;
        }

        uint bucketOwnerEnd = bucketOwnerOffset + ((uint)bucketOwnerCount * bucketOwnerEntrySize);
        uint recordTableEnd = romMapRecordOffset + ((uint)romMapRecordCount * romMapRecordSize);
        if (bucketOwnerOffset < headerSize ||
            bucketOwnerEnd > SectorSize ||
            romMapRecordOffset < bucketOwnerOffset ||
            recordTableEnd > SectorSize)
        {
            errorMessage = LocalizedText.T("error", "wl_sector_format_invalid", "WL mapping sector format is invalid.");
            return false;
        }

        uint computedChecksum = ComputeMappingChecksum(sectorData);
        if (mappingChecksum != computedChecksum)
        {
            errorMessage = LocalizedText.Tf(
                "error",
                "wl_sector_checksum_invalid",
                "WL mapping checksum is invalid. stored=0x{0:X8}, computed=0x{1:X8}.",
                mappingChecksum,
                computedChecksum);
            return false;
        }

        ushort[] bucketOwners = new ushort[bucketOwnerCount];
        for (int i = 0; i < bucketOwnerCount; i++)
        {
            int ownerOffset = (int)bucketOwnerOffset + (i * bucketOwnerEntrySize);
            bucketOwners[i] = BinaryPrimitives.ReadUInt16LittleEndian(sectorData.AsSpan(ownerOffset, bucketOwnerEntrySize));
        }

        List<WlRomMapRecordInfo> records = [];
        for (int entrySlot = 0; entrySlot < romMapRecordCount; entrySlot++)
        {
            int recordOffset = (int)romMapRecordOffset + (entrySlot * romMapRecordSize);
            int logicalBucketCount = sectorData[recordOffset];
            if (logicalBucketCount <= 0 || logicalBucketCount > MaxLogicalBucketCount)
            {
                continue;
            }

            List<int> physicalBuckets = [];
            bool validRecord = true;
            for (int i = 0; i < logicalBucketCount; i++)
            {
                int physicalBucket = sectorData[recordOffset + 1 + i];
                if (physicalBucket <= 0 || physicalBucket > RomPhysicalBucketCount)
                {
                    validRecord = false;
                    break;
                }

                physicalBuckets.Add(physicalBucket);
            }

            if (!validRecord)
            {
                continue;
            }

            records.Add(new WlRomMapRecordInfo
            {
                EntrySlot = entrySlot,
                LogicalBucketCount = logicalBucketCount,
                PhysicalBuckets = physicalBuckets,
            });
        }

        info.IsDetected = true;
        info.HasChecksum = true;
        info.IsChecksumValid = true;
        info.ComputedChecksum = computedChecksum;
        info.Header = new WlMappingHeaderInfo
        {
            Magic = magic,
            Version = version,
            HeaderSize = headerSize,
            Flags = flags,
            BucketOwnerOffset = (int)bucketOwnerOffset,
            BucketOwnerEntrySize = bucketOwnerEntrySize,
            BucketOwnerCount = bucketOwnerCount,
            RomMapRecordOffset = (int)romMapRecordOffset,
            RomMapRecordSize = romMapRecordSize,
            RomMapRecordCount = romMapRecordCount,
            MappingChecksum = mappingChecksum,
        };
        info.BucketOwners = bucketOwners;
        info.Records = records;
        return true;
    }

    public static uint ComputeMappingChecksum(ReadOnlySpan<byte> sectorData)
    {
        if (sectorData.Length < SectorSize)
        {
            throw new ArgumentOutOfRangeException(nameof(sectorData), "WL mapping sector data length is invalid.");
        }

        uint crc = 0xFFFFFFFF;
        for (int offset = 0; offset < SectorSize; offset++)
        {
            byte value = offset >= MappingChecksumOffset && offset < MappingChecksumOffset + sizeof(uint)
                ? (byte)0
                : sectorData[offset];
            crc = (crc >> 8) ^ Crc32Table[(int)((crc ^ value) & 0xFF)];
        }

        return ~crc;
    }

    public static void SealSector(byte[] sectorData)
    {
        if (sectorData is null || sectorData.Length < SectorSize)
        {
            throw new InvalidOperationException(LocalizedText.T("error", "wl_sector_length_invalid", "WL mapping sector data length is invalid."));
        }

        BinaryPrimitives.WriteUInt16LittleEndian(sectorData.AsSpan(0x04, sizeof(ushort)), Version);
        BinaryPrimitives.WriteUInt16LittleEndian(sectorData.AsSpan(0x06, sizeof(ushort)), HeaderSize);
        uint checksum = ComputeMappingChecksum(sectorData);
        BinaryPrimitives.WriteUInt32LittleEndian(sectorData.AsSpan(MappingChecksumOffset, sizeof(uint)), checksum);
    }

    public static bool TryExpandBucketOwners(
        byte[] sourceSectorData,
        out byte[] updatedSectorData,
        out string errorMessage)
    {
        updatedSectorData = [];
        errorMessage = string.Empty;

        if (!TryParse(sourceSectorData, 0, false, out WaterWlMappingInfo mapping, out errorMessage))
        {
            return false;
        }

        if (mapping.Header.BucketOwnerCount >= BucketCount)
        {
            updatedSectorData = sourceSectorData.ToArray();
            return true;
        }

        int ownerOffset = mapping.Header.BucketOwnerOffset;
        int ownerEntrySize = mapping.Header.BucketOwnerEntrySize;
        int newOwnerEnd = ownerOffset + (BucketCount * ownerEntrySize);
        int newRecordOffset = AlignUp(newOwnerEnd, sizeof(uint));
        int recordTableBytes = mapping.Header.RomMapRecordCount * mapping.Header.RomMapRecordSize;
        int oldRecordOffset = mapping.Header.RomMapRecordOffset;
        int oldRecordEnd = oldRecordOffset + recordTableBytes;
        int newRecordEnd = newRecordOffset + recordTableBytes;

        if (newRecordEnd > SectorSize)
        {
            errorMessage = LocalizedText.T(
                "error",
                "wl_sector_capacity_invalid",
                "The WL mapping sector does not have enough room for the expanded bucket table.");
            return false;
        }

        byte[] recordTable = sourceSectorData.AsSpan(oldRecordOffset, recordTableBytes).ToArray();
        updatedSectorData = sourceSectorData.ToArray();
        updatedSectorData.AsSpan(oldRecordOffset, oldRecordEnd - oldRecordOffset).Fill(0xFF);
        updatedSectorData.AsSpan(newRecordOffset, newRecordEnd - newRecordOffset).Fill(0xFF);

        for (int bucket = mapping.Header.BucketOwnerCount; bucket < BucketCount; bucket++)
        {
            int bucketOffset = ownerOffset + (bucket * ownerEntrySize);
            BinaryPrimitives.WriteUInt16LittleEndian(
                updatedSectorData.AsSpan(bucketOffset, sizeof(ushort)),
                BucketOwnerFree);
        }

        Buffer.BlockCopy(recordTable, 0, updatedSectorData, newRecordOffset, recordTable.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(
            updatedSectorData.AsSpan(0x12, sizeof(ushort)),
            BucketCount);
        BinaryPrimitives.WriteUInt32LittleEndian(
            updatedSectorData.AsSpan(0x14, sizeof(uint)),
            (uint)newRecordOffset);
        SealSector(updatedSectorData);
        return true;
    }

    public static bool TryClearRecord(byte[] sourceSectorData, int entrySlot, out byte[] updatedSectorData, out WlMappingDeletePlan plan, out string errorMessage)
    {
        updatedSectorData = [];
        plan = new WlMappingDeletePlan
        {
            EntrySlot = entrySlot,
        };
        errorMessage = string.Empty;

        if (sourceSectorData is null || sourceSectorData.Length < SectorSize)
        {
            errorMessage = LocalizedText.T("error", "wl_sector_length_invalid", "WL mapping sector data length is invalid.");
            return false;
        }

        if (!TryParse(sourceSectorData, 0, false, out WaterWlMappingInfo mapping, out errorMessage))
        {
            return false;
        }

        if (entrySlot < 0 || entrySlot >= mapping.Header.RomMapRecordCount)
        {
            errorMessage = LocalizedText.T("error", "wl_entry_slot_out_of_range", "The entry slot is out of WL mapping range.");
            return false;
        }

        WlRomMapRecordInfo? record = mapping.Records.FirstOrDefault(item => item.EntrySlot == entrySlot);
        if (record is null)
        {
            errorMessage = LocalizedText.T("error", "wl_record_missing", "The selected entry does not have a valid WL mapping record.");
            return false;
        }

        updatedSectorData = sourceSectorData.ToArray();
        plan.LogicalBucketCount = record.LogicalBucketCount;
        plan.ClearedBuckets.AddRange(record.PhysicalBuckets);

        foreach (int physicalBucket in record.PhysicalBuckets)
        {
            if (physicalBucket < 0 || physicalBucket >= mapping.Header.BucketOwnerCount)
            {
                continue;
            }

            int ownerOffset = mapping.Header.BucketOwnerOffset + (physicalBucket * mapping.Header.BucketOwnerEntrySize);
            ushort owner = BinaryPrimitives.ReadUInt16LittleEndian(updatedSectorData.AsSpan(ownerOffset, sizeof(ushort)));
            if (owner == entrySlot || owner == BucketOwnerFree)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(updatedSectorData.AsSpan(ownerOffset, sizeof(ushort)), BucketOwnerFree);
            }
            else
            {
                plan.OwnerConflictBuckets.Add(physicalBucket);
            }
        }

        int recordOffsetBytes = mapping.Header.RomMapRecordOffset + (entrySlot * mapping.Header.RomMapRecordSize);
        updatedSectorData.AsSpan(recordOffsetBytes, mapping.Header.RomMapRecordSize).Fill(0xFF);
        SealSector(updatedSectorData);
        return true;
    }

    public static bool TryInsertRecordSlot(byte[] sourceSectorData, int insertSlot, out byte[] updatedSectorData, out string errorMessage)
    {
        updatedSectorData = [];
        errorMessage = string.Empty;

        if (sourceSectorData is null || sourceSectorData.Length < SectorSize)
        {
            errorMessage = LocalizedText.T("error", "wl_sector_length_invalid", "WL mapping sector data length is invalid.");
            return false;
        }

        if (!TryParse(sourceSectorData, 0, false, out WaterWlMappingInfo mapping, out errorMessage))
        {
            return false;
        }

        if (insertSlot < 0 || insertSlot >= mapping.Header.RomMapRecordCount)
        {
            errorMessage = LocalizedText.T("error", "wl_entry_slot_out_of_range", "The entry slot is out of WL mapping range.");
            return false;
        }

        int highestUsedSlot = mapping.Records.Count > 0
            ? mapping.Records.Max(item => item.EntrySlot)
            : -1;

        if (highestUsedSlot >= mapping.Header.RomMapRecordCount - 1 && insertSlot <= highestUsedSlot)
        {
            errorMessage = LocalizedText.T("error", "wl_record_table_full", "The WL mapping table is full and cannot shift to insert a new item.");
            return false;
        }

        updatedSectorData = sourceSectorData.ToArray();

        for (int slot = mapping.Header.RomMapRecordCount - 2; slot >= insertSlot; slot--)
        {
            int sourceOffset = mapping.Header.RomMapRecordOffset + (slot * mapping.Header.RomMapRecordSize);
            int targetOffset = sourceOffset + mapping.Header.RomMapRecordSize;
            byte[] recordBytes = updatedSectorData.AsSpan(sourceOffset, mapping.Header.RomMapRecordSize).ToArray();
            Buffer.BlockCopy(recordBytes, 0, updatedSectorData, targetOffset, recordBytes.Length);
        }

        int insertRecordOffset = mapping.Header.RomMapRecordOffset + (insertSlot * mapping.Header.RomMapRecordSize);
        updatedSectorData.AsSpan(insertRecordOffset, mapping.Header.RomMapRecordSize).Fill(0xFF);

        for (int bucket = 0; bucket < mapping.Header.BucketOwnerCount; bucket++)
        {
            int ownerOffset = mapping.Header.BucketOwnerOffset + (bucket * mapping.Header.BucketOwnerEntrySize);
            ushort owner = BinaryPrimitives.ReadUInt16LittleEndian(updatedSectorData.AsSpan(ownerOffset, sizeof(ushort)));
            if (owner == BucketOwnerFree || owner == BucketOwnerSystem)
            {
                continue;
            }

            if (owner >= insertSlot)
            {
                if (owner >= mapping.Header.RomMapRecordCount - 1)
                {
                    errorMessage = LocalizedText.T("error", "wl_record_table_full", "The WL mapping table is full and cannot shift to insert a new item.");
                    return false;
                }

                BinaryPrimitives.WriteUInt16LittleEndian(updatedSectorData.AsSpan(ownerOffset, sizeof(ushort)), (ushort)(owner + 1));
            }
        }

        SealSector(updatedSectorData);
        return true;
    }

    public static bool TryUpsertRecord(byte[] sourceSectorData, int entrySlot, IReadOnlyList<int> physicalBuckets, out byte[] updatedSectorData, out string errorMessage)
    {
        updatedSectorData = [];
        errorMessage = string.Empty;

        if (sourceSectorData is null || sourceSectorData.Length < SectorSize)
        {
            errorMessage = LocalizedText.T("error", "wl_sector_length_invalid", "WL mapping sector data length is invalid.");
            return false;
        }

        if (!TryParse(sourceSectorData, 0, false, out WaterWlMappingInfo mapping, out errorMessage))
        {
            return false;
        }

        if (entrySlot < 0 || entrySlot >= mapping.Header.RomMapRecordCount)
        {
            errorMessage = LocalizedText.T("error", "wl_entry_slot_out_of_range", "The entry slot is out of WL mapping range.");
            return false;
        }

        List<int> normalizedBuckets = physicalBuckets?
            .Distinct()
            .ToList() ?? [];

        if (normalizedBuckets.Count <= 0 || normalizedBuckets.Count > MaxLogicalBucketCount)
        {
            errorMessage = LocalizedText.T("error", "wl_record_bucket_count_invalid", "The WL mapping bucket count is invalid.");
            return false;
        }

        foreach (int bucket in normalizedBuckets)
        {
            if (bucket <= 0 || bucket > RomPhysicalBucketCount || bucket >= mapping.Header.BucketOwnerCount)
            {
                errorMessage = LocalizedText.Tf("error", "wl_record_bucket_invalid", "WL physical bucket {0} is out of range.", bucket);
                return false;
            }
        }

        updatedSectorData = sourceSectorData.ToArray();

        WlRomMapRecordInfo? existingRecord = mapping.Records.FirstOrDefault(item => item.EntrySlot == entrySlot);
        if (existingRecord is not null)
        {
            foreach (int bucket in existingRecord.PhysicalBuckets)
            {
                if (bucket < 0 || bucket >= mapping.Header.BucketOwnerCount)
                {
                    continue;
                }

                int ownerOffset = mapping.Header.BucketOwnerOffset + (bucket * mapping.Header.BucketOwnerEntrySize);
                ushort owner = BinaryPrimitives.ReadUInt16LittleEndian(updatedSectorData.AsSpan(ownerOffset, sizeof(ushort)));
                if (owner == entrySlot)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(updatedSectorData.AsSpan(ownerOffset, sizeof(ushort)), BucketOwnerFree);
                }
            }
        }

        foreach (int bucket in normalizedBuckets)
        {
            int ownerOffset = mapping.Header.BucketOwnerOffset + (bucket * mapping.Header.BucketOwnerEntrySize);
            ushort owner = BinaryPrimitives.ReadUInt16LittleEndian(updatedSectorData.AsSpan(ownerOffset, sizeof(ushort)));
            if (owner != BucketOwnerFree && owner != entrySlot)
            {
                errorMessage = LocalizedText.Tf(
                    "error",
                    "wl_bucket_owner_conflict",
                    "WL physical bucket {0:00} is already owned by entry #{1:000}.",
                    bucket,
                    owner + 1);
                return false;
            }
        }

        int recordOffset = mapping.Header.RomMapRecordOffset + (entrySlot * mapping.Header.RomMapRecordSize);
        updatedSectorData.AsSpan(recordOffset, mapping.Header.RomMapRecordSize).Fill(0xFF);
        updatedSectorData[recordOffset] = (byte)normalizedBuckets.Count;
        for (int i = 0; i < normalizedBuckets.Count; i++)
        {
            updatedSectorData[recordOffset + 1 + i] = (byte)normalizedBuckets[i];
        }

        foreach (int bucket in normalizedBuckets)
        {
            int ownerOffset = mapping.Header.BucketOwnerOffset + (bucket * mapping.Header.BucketOwnerEntrySize);
            BinaryPrimitives.WriteUInt16LittleEndian(updatedSectorData.AsSpan(ownerOffset, sizeof(ushort)), (ushort)entrySlot);
        }

        SealSector(updatedSectorData);
        return true;
    }

    private static uint[] BuildCrc32Table()
    {
        uint[] table = new uint[256];
        const uint polynomial = 0xEDB88320;

        for (uint i = 0; i < table.Length; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0
                    ? (value >> 1) ^ polynomial
                    : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }

    private static int AlignUp(int value, int alignment) =>
        ((value + alignment - 1) / alignment) * alignment;
}
