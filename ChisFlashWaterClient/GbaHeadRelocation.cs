using GbaMultiMenuBuilder.Services;
using ClientText = ChisFlashWaterClient.Services.LocalizedText;

namespace ChisFlashWaterClient;

public static partial class GbaCardWritePlan
{
    // 编译镜像只提供已准备好的新游戏、菜单和存档区；实际擦写严格按目标块表输出。
    internal static (IReadOnlyList<WaterNorWriteSegment> Segments, WaterCartScanResult Scan) RelocateHead(
        byte[] image, WaterCartScanResult compiled, WaterCartScanResult card, int keptIndex,
        int gameLength, byte[] retainedData, bool fragmented, bool chm1)
    {
        var items = compiled.MenuConfig.Items.Select(WaterMenuConfigCodec.Clone).ToArray();
        int capacity = card.Geometry.DeviceSizeBytes;
        int bucketCount = capacity / HeadBytes;
        int retainedCount = checked((gameLength + HeadBytes - 1) / HeadBytes);
        int copyCount = fragmented ? 1 : retainedCount;
        if (keptIndex < 0 || keptIndex >= items.Length || gameLength <= 0 ||
            retainedData.Length != (fragmented ? Math.Min(HeadBytes, gameLength) : gameLength))
            throw new InvalidDataException(ClientText.T("client", "current_length_required", "Specify a valid read length for the current game."));
        int[] lengths = items.Select(item => item.CoveredBucketCount).ToArray();
        lengths[keptIndex] = retainedCount;
        if (1L + lengths.Sum(n => (long)n) > bucketCount) throw NoRelocationSpace();

        // 先安排搬移，目标不得覆盖旧游戏本身，也不把其他已扫描到的游戏范围当成空闲块。
        bool[] occupied = new bool[bucketCount];
        Array.Fill(occupied, true, 0, retainedCount);
        foreach (var game in card.Games.Where(game => game.StartBucket > 0))
            for (int b = game.StartBucket; b < bucketCount && b <= game.EndBucket; b++) occupied[b] = true;

        int[][]? maps = null;
        for (int start = retainedCount; start <= bucketCount - copyCount; start++)
        {
            if (Array.IndexOf(occupied, true, start, copyCount) >= 0) continue;
            maps = PlaceAroundRetained(lengths, keptIndex, start, bucketCount, fragmented);
            if (maps is not null) break;
        }
        if (maps is null) throw NoRelocationSpace();

        var segments = new List<WaterNorWriteSegment>();
        var headers = new List<DetectedNorGameInfo>();
        void AddGame(int index, ReadOnlySpan<byte> data, int blocksToWrite)
        {
            int[] map = maps[index];
            var item = items[index];
            // 合并同一 ROM 内物理连续的写入，绝不把中间空洞或原位保留块填 FF 擦写。
            for (int first = 0; first < blocksToWrite;)
            {
                int end = first + 1;
                while (end < blocksToWrite && map[end] == map[end - 1] + 1) end++;
                int offset = first * HeadBytes;
                int bytes = Math.Min(data.Length - offset, (end - first) * HeadBytes);
                int sector = card.Geometry.SectorSizeBytes;
                byte[] payload = new byte[checked((bytes + sector - 1) / sector * sector)];
                data.Slice(offset, bytes).CopyTo(payload);
                payload.AsSpan(bytes).Fill(0xFF);
                segments.Add(new() { StartAddress = checked((uint)map[first] * HeadBytes), Data = payload, Label = item.Title });
                first = end;
            }
            var header = data[..Math.Min(0xC0, data.Length)].ToArray();
            headers.Add(new() { StartBucket = map[0], EndBucket = map.Max(), Title = WaterCartScanner.DecodeGbaHeaderTitle(header),
                GameCode = WaterCartScanner.DecodeGbaHeaderGameCode(header) });
            item.RomOffsetBlocks = checked((ushort)(map[0] * 8));
            if (index == keptIndex)
                item.RomSizeBlocks = checked((ushort)((gameLength + WaterRomImportPlanner.BlockSizeBytes - 1) / WaterRomImportPlanner.BlockSizeBytes));
        }
        AddGame(keptIndex, retainedData, copyCount);
        for (int index = 0; index < items.Length; index++)
        {
            if (index == keptIndex) continue;
            var item = items[index];
            int offset = checked(item.RomOffsetBlocks * WaterRomImportPlanner.BlockSizeBytes);
            int size = checked(item.RomSizeBlocks * WaterRomImportPlanner.BlockSizeBytes);
            AddGame(index, image.AsSpan(offset, size), maps[index].Length);
        }
        byte[] head = image.AsSpan(0, HeadBytes).ToArray();
        var config = new WaterMenuConfigInfo { IsDetected = true, Status = compiled.MenuConfig.Status, Items = items.ToList() };
        WaterMenuConfigCodec.BuildItemListSector(items, config.Status.ItemListAddress, config.Status.SectorSize).CopyTo(head, config.Status.ItemListAddress);
        var romCode = CompilationBuilder.ComputeRomCode(head.AsSpan(config.Status.StatusAddress, 16).ToArray(),
            head.AsSpan(config.Status.ItemListAddress, items.Length * WaterMenuConfigCodec.EntrySize).ToArray());
        CompilationBuilder.WriteHeaderMetadata(head, romCode);
        if (fragmented)
        {
            var sector = chm1 ? Chm1Layout.BuildMappingSector(maps, capacity) :
                WaterMagicUpgradeBuilder.BuildMappingSector(maps.Select((map, index) => new WlRomMapRecordInfo
                { EntrySlot = index, LogicalBucketCount = map.Length, PhysicalBuckets = map.ToList() }).ToList(),
                    WaterWlMappingCodec.GetBucketCount(capacity), WaterWlMappingCodec.IsChimeraConfiguration(card));
            sector.CopyTo(head, WaterWlMappingCodec.Offset);
        }
        segments.Add(new() { StartAddress = 0, Data = head, Label = ClientText.T("client", "multicart_menu_directory", "Multicart menu and directory") });
        var cached = WaterCartScanner.RebuildCachedScanResult(card, gamesOverride: headers);
        var refreshed = WaterCartScanner.RebuildCachedGbaMenuResult(cached, config, head);
        refreshed.Geometry = card.Geometry;
        return (segments, refreshed);
    }

    private static InvalidDataException NoRelocationSpace() => new(ClientText.T("client", "head_relocation_no_space",
        "There is not enough free space for the selected placement. The cartridge has not been written."));

    private static int[][]? PlaceAroundRetained(int[] lengths, int kept, int start, int capacity, bool fragmented)
    {
        int[][] maps = new int[lengths.Length][];
        var others = Enumerable.Range(0, lengths.Length).Where(i => i != kept).ToArray();
        int count = lengths[kept];
        if (fragmented)
        {
            maps[kept] = [start, .. Enumerable.Range(1, count - 1)];
            var free = new Queue<int>(Enumerable.Range(count, capacity - count).Where(b => b != start));
            foreach (int index in others)
                maps[index] = Enumerable.Range(0, lengths[index]).Select(_ => free.Dequeue()).ToArray();
            return maps;
        }
        // 旧游戏占据一段连续地址。把其他 ROM 分配到它左右两段，每个 ROM 都必须连续。
        int left = start - 1;
        int[] chosen = Enumerable.Repeat(-1, left + 1).ToArray();
        chosen[0] = kept;
        foreach (int index in others)
            for (int sum = left; sum >= lengths[index]; sum--)
                if (chosen[sum] < 0 && chosen[sum - lengths[index]] >= 0) chosen[sum] = index;
        int used = Array.FindLastIndex(chosen, index => index >= 0);
        if (others.Sum(i => lengths[i]) - used > capacity - start - count) return null;
        var onLeft = new HashSet<int>();
        while (used > 0)
        {
            int index = chosen[used];
            onLeft.Add(index);
            used -= lengths[index];
        }
        maps[kept] = Enumerable.Range(start, count).ToArray();
        int before = 1, after = start + count;
        foreach (int index in others)
        {
            maps[index] = Enumerable.Range(onLeft.Contains(index) ? before : after, lengths[index]).ToArray();
            if (onLeft.Contains(index)) before += lengths[index]; else after += lengths[index];
        }
        return maps;
    }
}
