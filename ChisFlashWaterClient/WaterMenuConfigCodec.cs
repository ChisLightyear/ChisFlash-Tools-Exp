using System.Buffers.Binary;
using System.Text;
using ChisFlashWaterClient.Services;

namespace ChisFlashWaterClient;

public static class WaterMenuConfigCodec
{
    public const int EntrySize = 0x70;
    public const int MaxEntries = 512;
    public const int MaxItemBytes = EntrySize * MaxEntries;
    public const int TitleCharCapacity = 0x30;
    private const uint StatusMagic = 0x414D554B;

    public static string NormalizeTitle(string? title)
    {
        title = string.IsNullOrWhiteSpace(title)
            ? LocalizedText.T("value", "untitled", "UNTITLED")
            : title.Trim();

        if (title.Length > TitleCharCapacity)
        {
            title = title[..TitleCharCapacity];
        }

        return title;
    }

    public static MenuItemConfigInfo Clone(MenuItemConfigInfo source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new MenuItemConfigInfo
        {
            Index = source.Index,
            EntryAddress = source.EntryAddress,
            Font = source.Font,
            TitleLength = source.TitleLength,
            RomOffsetBlocks = source.RomOffsetBlocks,
            RomSizeBlocks = source.RomSizeBlocks,
            SaveType = source.SaveType,
            SaveIndex = source.SaveIndex,
            HiddenKeysMask = source.HiddenKeysMask,
            CoverIndex = source.CoverIndex,
            CoverFlags = source.CoverFlags,
            Title = source.Title,
        };
    }

    public static List<MenuItemConfigInfo> ReindexItems(IEnumerable<MenuItemConfigInfo> items, int itemListAddress)
    {
        ArgumentNullException.ThrowIfNull(items);

        List<MenuItemConfigInfo> result = [];
        int index = 0;
        foreach (MenuItemConfigInfo item in items)
        {
            MenuItemConfigInfo clone = Clone(item);
            clone.Index = index;
            clone.EntryAddress = itemListAddress + (index * EntrySize);
            clone.Title = NormalizeTitle(clone.Title);
            clone.TitleLength = (byte)Math.Min(clone.Title.Length, TitleCharCapacity);
            result.Add(clone);
            index++;
        }

        return result;
    }

    public static byte[] EncodeMenuItem(MenuItemConfigInfo item)
    {
        ArgumentNullException.ThrowIfNull(item);

        byte[] entry = Enumerable.Repeat((byte)0xFF, EntrySize).ToArray();
        string title = NormalizeTitle(item.Title);
        byte[] titleBytes = Encoding.Unicode.GetBytes(title.PadRight(TitleCharCapacity, '\0'));

        entry[0] = item.Font;
        entry[1] = (byte)Math.Min(title.Length, TitleCharCapacity);
        Array.Copy(BitConverter.GetBytes(item.RomOffsetBlocks), 0, entry, 2, 2);
        Array.Copy(BitConverter.GetBytes(item.RomSizeBlocks), 0, entry, 4, 2);
        entry[6] = item.SaveType;
        entry[7] = item.SaveIndex;
        Array.Copy(BitConverter.GetBytes(item.HiddenKeysMask), 0, entry, 8, 2);
        entry[10] = item.CoverIndex;
        entry[11] = item.CoverFlags;
        Array.Clear(entry, 12, 4);
        Array.Copy(titleBytes, 0, entry, 0x10, Math.Min(titleBytes.Length, 0x60));

        return entry;
    }

    public static byte[] BuildItemListSector(IReadOnlyList<MenuItemConfigInfo> items, int itemListAddress, int sectorSizeBytes)
    {
        ArgumentNullException.ThrowIfNull(items);

        if (sectorSizeBytes < MaxItemBytes)
        {
            throw new InvalidOperationException(
                LocalizedText.Tf(
                    "error",
                    "itemlist_sector_too_small",
                    "The itemlist sector is too small: 0x{0:X}.",
                    sectorSizeBytes));
        }

        if (items.Count > MaxEntries)
        {
            throw new InvalidOperationException(
                LocalizedText.Tf(
                    "error",
                    "itemlist_entry_limit_exceeded",
                    "The itemlist exceeds the {0} entry limit.",
                    MaxEntries));
        }

        byte[] sectorData = Enumerable.Repeat((byte)0xFF, sectorSizeBytes).ToArray();
        List<MenuItemConfigInfo> indexedItems = ReindexItems(items, itemListAddress);

        for (int i = 0; i < indexedItems.Count; i++)
        {
            byte[] encoded = EncodeMenuItem(indexedItems[i]);
            Buffer.BlockCopy(encoded, 0, sectorData, i * EntrySize, encoded.Length);
        }

        return sectorData;
    }

    public static byte[] BuildStatusSector(MenuFlashStatusInfo status)
    {
        ArgumentNullException.ThrowIfNull(status);

        if (status.SectorSize <= 0)
        {
            throw new InvalidOperationException(
                LocalizedText.T("error", "legacy_itemlist_invalid", "Legacy itemlist address is invalid."));
        }

        byte[] sector = Enumerable.Repeat((byte)0xFF, status.SectorSize).ToArray();
        sector.AsSpan(0, 16).Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(0, 4), StatusMagic);
        sector[4] = status.Version;
        sector[5] = status.BatteryPresent ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(6, 2), status.LastBootMenuIndex);
        sector[8] = status.LastBootSaveIndex;
        sector[9] = status.LastBootSaveType;
        sector[10] = status.SramBankType;
        return sector;
    }
}
