using GbaMultiMenuBuilder.Services;

namespace ChisFlashWaterClient;

public static class Chm1MenuBuilder
{
    public static bool RequiresConversion(WaterCartScanResult scan, bool magic, bool? battery = null)
    {
        if (!scan.IsChm1) return false;
        if (!scan.MenuConfig.IsDetected || scan.UsesChm1Layout != magic || (!magic && scan.WlMapping.IsDetected) ||
            (battery.HasValue && scan.MenuConfig.Status.BatteryPresent != battery.Value)) return true;
        var status = scan.MenuConfig.Status;
        return status.ItemListAddress != Chm1Layout.ItemListOffset || status.StatusAddress != Chm1Layout.StatusOffset ||
            status.SaveAreaAddress != (magic ? Chm1Layout.SaveAreaOffset : Chm1Layout.StatusOffset + Chm1Layout.SectorBytes);
    }

    public static MenuFlashStatusInfo CreateStatus(MenuFlashStatusInfo source, IReadOnlyList<MenuItemConfigInfo> items,
        bool magic = true, bool battery = true)
    {
        if (magic && battery)
            foreach (var item in items.Where(item => item.SaveType != 0))
                _ = Chm1Layout.SaveAddress(item.SaveIndex);
        return new MenuFlashStatusInfo
        {
            StatusAddress = Chm1Layout.StatusOffset,
            ExplicitItemListAddress = Chm1Layout.ItemListOffset,
            ExplicitSaveAreaAddress = magic ? Chm1Layout.SaveAreaOffset : Chm1Layout.StatusOffset + Chm1Layout.SectorBytes,
            SectorSize = Chm1Layout.SectorBytes,
            Version = source.Version,
            BatteryPresent = battery,
            SramBankType = 1,
            LastBootMenuIndex = source.LastBootMenuIndex,
            LastBootSaveIndex = battery ? source.LastBootSaveIndex : (byte)0xFF,
            LastBootSaveType = battery ? source.LastBootSaveType : (byte)0,
        };
    }

    public static byte[] BuildHead(byte[] previousHead, byte[] menu, WaterCartScanResult scan, bool magic, bool battery = true)
    {
        if (previousHead.Length != Chm1Layout.BucketBytes || menu.Length > Chm1Layout.ItemListOffset ||
            scan.Geometry.SectorSizeBytes != Chm1Layout.SectorBytes || scan.Geometry.DeviceSizeBytes > Chm1Layout.MaxCapacity)
            throw new InvalidOperationException("Unsupported 01G/02G fragment management menu layout or NOR geometry.");
        if (magic && menu.AsSpan().IndexOf("WLMI"u8) < 0)
            throw new InvalidOperationException("01G/02G fragment management requires a WL or NS menu.");
        if (!magic && menu.AsSpan().IndexOf("WLMI"u8) >= 0 && !GbaMenuRomService.SupportsCovers(menu))
            throw new InvalidOperationException("An ordinary layout requires a classic or NS menu; the WL menu requires bucket mapping.");
        if (scan.Games.Any(game => !game.IsMenu && game.StartBucket == 0))
            throw new InvalidOperationException("A game occupies the first 4 MiB; export or relocate it before installing a menu.");
        if (scan.WlMapping.LegacyVersionDetected || (scan.MenuConfig.IsDetected && !scan.WlMapping.IsDetected &&
            (previousHead.AsSpan(Chm1Layout.MappingOffset, 4).SequenceEqual("WLMI"u8) ||
             previousHead.AsSpan(0xB0000, 4).SequenceEqual("UMMF"u8))))
            throw new InvalidOperationException("The existing fragmented map is invalid; restore its backup before converting the menu.");
        if (scan.WlMapping.IsDetected && !scan.MenuConfig.IsDetected)
            throw new InvalidOperationException("The fragmented map has no valid menu directory; restore the directory before converting the menu.");

        var original = scan.MenuConfig.IsDetected
            ? scan.MenuConfig.Items.Select(WaterMenuConfigCodec.Clone).ToList()
            : scan.Games.Where(game => !game.IsMenu && game.StartBucket > 0).Select(game => new MenuItemConfigInfo
            {
                Title = game.Title, RomOffsetBlocks = checked((ushort)(game.StartBucket * 8)),
                RomSizeBlocks = checked((ushort)((game.EndBucket - game.StartBucket + 1) * 8)),
            }).ToList();
        var items = WaterMenuConfigCodec.ReindexItems(original, Chm1Layout.ItemListOffset);
        var maps = items.Select(item => scan.WlMapping.IsDetected
            ? scan.WlMapping.Records.Single(record => record.EntrySlot == item.Index).PhysicalBuckets.ToArray()
            : Enumerable.Range(item.StartBucket, item.CoveredBucketCount).ToArray()).ToArray();
        if (!magic && maps.Any(map => !map.SequenceEqual(Enumerable.Range(map[0], map.Length))))
            throw new InvalidOperationException("Fragmented games cannot switch to an ordinary menu without relocating their ROM data.");
        byte[]? mapping = magic ? Chm1Layout.BuildMappingSector(maps, scan.Geometry.DeviceSizeBytes) : null;
        int saveBase = magic ? Chm1Layout.SaveAreaOffset : Chm1Layout.StatusOffset + Chm1Layout.SectorBytes;
        int saveLimit = magic ? Chm1Layout.MappingOffset : Chm1Layout.BucketBytes;
        int slotCount = (saveLimit - saveBase) / Chm1Layout.SectorBytes;
        int[] oldSlots = battery
            ? original.Where(item => item.SaveType != 0).Select(item => (int)item.SaveIndex).Distinct().ToArray()
            : [];
        if (!battery)
            foreach (var item in items) { item.SaveType = 0; item.SaveIndex = 0; }
        if (oldSlots.Length > slotCount) throw new InvalidOperationException($"The selected layout has only {slotCount} save slots.");
        var slots = oldSlots.Where(slot => slot < slotCount).ToDictionary(slot => slot, slot => slot);
        foreach (int oldSlot in oldSlots.Where(slot => !slots.ContainsKey(slot)))
            slots[oldSlot] = Enumerable.Range(0, slotCount).First(slot => !slots.ContainsValue(slot));

        byte[] head = Enumerable.Repeat((byte)0xFF, Chm1Layout.BucketBytes).ToArray();
        menu.CopyTo(head, 0);
        foreach (int oldSlot in oldSlots)
        {
            int oldAddress = scan.MenuConfig.Status.SaveAreaAddress + oldSlot * scan.MenuConfig.Status.SectorSize;
            if (oldAddress < 0 || oldAddress + Chm1Layout.SectorBytes > previousHead.Length)
                throw new InvalidOperationException("The existing save slot is outside the menu head area.");
            previousHead.AsSpan(oldAddress, Chm1Layout.SectorBytes).CopyTo(head.AsSpan(saveBase + slots[oldSlot] * Chm1Layout.SectorBytes));
        }
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].SaveType != 0) items[i].SaveIndex = (byte)slots[original[i].SaveIndex];
        }
        var previousStatus = scan.MenuConfig.Status;
        var status = CreateStatus(previousStatus, items, magic, battery);
        if (status.LastBootSaveType != 0)
            status.LastBootSaveIndex = (byte)slots[previousStatus.LastBootSaveIndex];
        WaterMenuConfigCodec.BuildItemListSector(items, Chm1Layout.ItemListOffset, Chm1Layout.SectorBytes).CopyTo(head, Chm1Layout.ItemListOffset);
        WaterMenuConfigCodec.BuildStatusSector(status).CopyTo(head, Chm1Layout.StatusOffset);
        mapping?.CopyTo(head, Chm1Layout.MappingOffset);
        return head;
    }
}
