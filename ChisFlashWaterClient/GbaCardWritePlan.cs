using ClientText = ChisFlashWaterClient.Services.LocalizedText;
namespace ChisFlashWaterClient;

public static class GbaCardWritePlan
{
    public const int HeadBytes = 0x400000;

    public static byte[] CompleteMapping(byte[] image, bool waterLink, int capacityBytes = 0, bool chimeraLayout = false)
    {
        if (!waterLink) return image;
        var scan = WaterCartScanner.ScanOfflineRomImage(image);
        if (!scan.MenuConfig.IsDetected || image.Length < HeadBytes)
            throw new InvalidDataException(ClientText.T("client", "water_directory_missing", "The WaterLink multicart image has no valid directory."));
        byte[] result = image.ToArray();
        int bucketCount = capacityBytes > 0 ? WaterWlMappingCodec.GetBucketCount(capacityBytes)
            : scan.WlMapping.IsDetected ? scan.WlMapping.Header.BucketOwnerCount : WaterWlMappingCodec.DefaultBucketCount;
        WaterMagicUpgradeBuilder.BuildMappingSector(scan.MenuConfig, bucketCount,
            chimeraLayout || WaterWlMappingCodec.IsChimeraConfiguration(scan)).CopyTo(result, WaterWlMappingCodec.Offset);
        var status = WaterMagicUpgradeBuilder.NormalizeWlStatus(scan.MenuConfig.Status);
        WaterMenuConfigCodec.BuildStatusSector(status).CopyTo(result, status.StatusAddress);
        return result;
    }

    public static IReadOnlyList<WaterNorWriteSegment> Multicart(byte[] image, int capacity, int sectorBytes)
    {
        if (sectorBytes <= 0 || HeadBytes % sectorBytes != 0 || image.Length < HeadBytes || image.Length > capacity)
            throw new InvalidDataException(ClientText.T("client", "image_sector_mismatch", "The multicart image or NOR sector size is incompatible."));
        int aligned = checked((image.Length + sectorBytes - 1) / sectorBytes * sectorBytes);
        if (aligned > capacity) throw new InvalidDataException(ClientText.T("client", "image_exceeds_nor", "The multicart image exceeds the detected NOR capacity."));
        byte[] data = Enumerable.Repeat((byte)255, aligned).ToArray();
        image.CopyTo(data, 0);
        var segments = new List<WaterNorWriteSegment>();
        if (aligned > HeadBytes)
            segments.Add(new() { StartAddress = HeadBytes, Data = data.AsSpan(HeadBytes).ToArray(), Label = ClientText.T("client", "multicart_game_area", "Multicart game area") });
        // 游戏和存档准备完成后才提交菜单与目录。
        segments.Add(new() { StartAddress = 0, Data = data.AsSpan(0, HeadBytes).ToArray(), Label = ClientText.T("client", "multicart_menu_directory", "Multicart menu and directory") });
        return segments;
    }
}
