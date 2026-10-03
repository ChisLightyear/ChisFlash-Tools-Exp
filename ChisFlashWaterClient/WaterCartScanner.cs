using System.Text;
using System.Threading;
using System.Buffers.Binary;
using ChisFlashWaterClient.Services;
using GbaMultiMenuBuilder.Services;

namespace ChisFlashWaterClient;

public static class WaterCartScanner
{
    public const int BucketSizeBytes = 4 * 1024 * 1024;
    private const int WaterLinkMultiNorSizeBytes = 512 * 1024 * 1024;
    private const int WaterLinkMultiNorChipSizeBytes = 128 * 1024 * 1024;
    private const int LegacyBeggarTimeoutMs = 3000;
    private const int GbcProbeTimeoutMs = 3000;

    private const int MenuItemEntrySize = 0x70;
    private const int MenuEntriesPerChunk = 8;
    private const int MenuMaxEntries = 512;
    private const int WaterProbeTimeoutMs = 1500;
    private const int WaterProbeLengthBytes = 192;
    private const int NorCfiDiagnosticWindowAddress = 0x10 << 1;
    private const int NorCfiDiagnosticWindowLengthBytes = 0x50;
    private const int NorCfiReadWindowBytes = 0x100;
    private const int GbcFixedMapReservedBytes = 1 * 1024 * 1024;
    private const int GbcConfigOffsetBytes = 0x020000;
    private const int GbcRomDirectoryOffsetBytes = 0x040000;
    private const int GbcConfigHeaderSize = 64;
    private const int GbcRomDirectoryHeaderSize = 64;
    private const int GbcRomEntrySize = 128;
    private const int GbcRomSramMapOffset = 110;
    private const int GbcMaxGameSramBanks = 8;
    private const int GbcSharedSramBankStart = 60;
    private const int GbcSharedSramBankCount = 4;
    private const int GbcTotalSramBankCount = GbcSharedSramBankStart + GbcSharedSramBankCount;
    private const int WaterLinkItemListOffset = 0x0C0000;
    private const int WaterLinkStatusOffset = 0x0E0000;
    private const int MenuBackgroundRegionLengthBytes = 0x9800;
    private const int WlMenuBackgroundMarkerGapBytes = 0x40;
    private static readonly byte[] MenuBackgroundMarker = [0x52, 0x54, 0x46, 0x4E, 0xFF, 0xFE];
    public const int UniversalImageManifestOffset = 0x0B0000;
    public const int UniversalImageManifestSize = 64;
    private const uint UniversalImageManifestMagic = 0x464D4D55;
    private const ushort UniversalImageManifestVersion = 1;
    private const uint UniversalImageManifestFlags = 3;
    private const uint ExtendedSlotInfoMagicMask = 0xFFFF0000u;
    private const uint ExtendedSlotInfoMagic = 0x534C0000u;
    private const uint ExtendedSlotInfoFlagWaterLinkFixedLayout = 0x8000u;
    private const int ExtendedSlotInfoNorMaskShift = 9;
    private const int ExtendedSlotInfoNorMaskBits = 0x0F;

    private sealed record UniversalImageManifestLayout(
        int ItemListOffset,
        int ItemListSize,
        int StatusOffset,
        int StatusSize,
        int MappingOffset,
        int MappingSize,
        int EntryCount);

    private static readonly uint[] WaterDiagnosticProbeAddresses =
    [
        0x00000000,
        0x02000000,
        0x04000000,
        0x06000000,
    ];

    private static readonly byte[] NintendoLogoGba =
    [
        0x24, 0xFF, 0xAE, 0x51, 0x69, 0x9A, 0xA2, 0x21, 0x3D, 0x84, 0x82, 0x0A, 0x84, 0xE4, 0x09, 0xAD,
        0x11, 0x24, 0x8B, 0x98, 0xC0, 0x81, 0x7F, 0x21, 0xA3, 0x52, 0xBE, 0x19, 0x93, 0x09, 0xCE, 0x20,
        0x10, 0x46, 0x4A, 0x4A, 0xF8, 0x27, 0x31, 0xEC, 0x58, 0xC7, 0xE8, 0x33, 0x82, 0xE3, 0xCE, 0xBF,
        0x85, 0xF4, 0xDF, 0x94, 0xCE, 0x4B, 0x09, 0xC1, 0x94, 0x56, 0x8A, 0xC0, 0x13, 0x72, 0xA7, 0xFC,
        0x9F, 0x84, 0x4D, 0x73, 0xA3, 0xCA, 0x9A, 0x61, 0x58, 0x97, 0xA3, 0x27, 0xFC, 0x03, 0x98, 0x76,
        0x23, 0x1D, 0xC7, 0x61, 0x03, 0x04, 0xAE, 0x56, 0xBF, 0x38, 0x84, 0x00, 0x40, 0xA7, 0x0E, 0xFD,
        0xFF, 0x52, 0xFE, 0x03, 0x6F, 0x95, 0x30, 0xF1, 0x97, 0xFB, 0xC0, 0x85, 0x60, 0xD6, 0x80, 0x25,
        0xA9, 0x63, 0xBE, 0x03, 0x01, 0x4E, 0x38, 0xE2, 0xF9, 0xA2, 0x34, 0xFF, 0xBB, 0x3E, 0x03, 0x44,
        0x78, 0x00, 0x90, 0xCB, 0x88, 0x11, 0x3A, 0x94, 0x65, 0xC0, 0x7C, 0x63, 0x87, 0xF0, 0x3C, 0xAF,
        0xD6, 0x25, 0xE4, 0x8B, 0x38, 0x0A, 0xAC, 0x72, 0x21, 0xD4, 0xF8, 0x07
    ];

    private static readonly byte[] NintendoLogoGb =
    [
        0xCE, 0xED, 0x66, 0x66, 0xCC, 0x0D, 0x00, 0x0B, 0x03, 0x73, 0x00, 0x83, 0x00, 0x0C, 0x00, 0x0D,
        0x00, 0x08, 0x11, 0x1F, 0x88, 0x89, 0x00, 0x0E, 0xDC, 0xCC, 0x6E, 0xE6, 0xDD, 0xDD, 0xD9, 0x99,
        0xBB, 0xBB, 0x67, 0x63, 0x6E, 0x0E, 0xEC, 0xCC, 0xDD, 0xDC, 0x99, 0x9F, 0xBB, 0xB9, 0x33, 0x3E
    ];

    private sealed record BeggarMaxSlotCandidate(int StartAddress, int ReservedSizeBytes, string Label, bool IsMenu, int SaveSlotIndex);

    private sealed class BeggarMaxHeaderInfo
    {
        public string Title { get; init; } = string.Empty;
        public bool IsColor { get; init; }
        public byte RomSizeCode { get; init; }
        public int RomSizeBytes { get; init; }
        public byte RamSizeCode { get; init; }
        public int RamSizeBytes { get; init; }
        public byte CartridgeType { get; init; }
        public int EffectiveRamBankCount { get; init; }
        public bool HasBattery { get; init; }
    }

    private sealed class BeggarMaxDirectoryEntryInfo
    {
        public int Index { get; init; }
        public string Title { get; init; } = string.Empty;
        public string HeaderTitle { get; init; } = string.Empty;
        public int RomOffsetBank { get; init; }
        public int RomBankCount { get; init; }
        public int AllocatedSizeBytes { get; init; }
        public int SaveLengthBytes { get; init; }
        public byte CartridgeType { get; init; }
        public bool HasBattery { get; init; }
        public bool UsesSharedSave { get; init; }
        public byte[] SaveBankMap { get; init; } = [];
    }

    private sealed class NorCfiProbeResult
    {
        public byte[] RawWindow { get; init; } = [];
        public int EnterWordAddress { get; init; }
        public int DeviceSizeBytes { get; init; }
        public int SectorCount { get; init; }
        public int SectorSizeBytes { get; init; }
        public int LargestSectorSizeBytes { get; init; }
        public int BufferWriteBytes { get; init; }
        public int RegionCount { get; init; }
        public ushort PrimaryCommandSetId { get; init; }
        public string CommandSetName { get; init; } = "Unknown";
    }

    public static WaterCartScanResult Scan(
        WaterDeviceProtocol device,
        Action<string>? log = null,
        bool useWaterLinkMultiNor = false)
    {
        if (!device.TryReadLegacyNorId(out byte[] norId, 1500))
        {
            throw new InvalidOperationException(T("error", "read_nor_id_failed", "Failed to read NOR ID."));
        }

        log?.Invoke(T(
            "log",
            "water_legacy_id_first",
            "WaterLink diagnostics: used legacy-compatible NOR ID read first."));
        log?.Invoke(BitConverter.ToString(norId).Replace("-", " "));
        LogNorIdInference(norId, log);
        NorGeometryInfo geometry;
        if (LooksLikeNoCartridgeNorId(norId))
        {
            if(!TryReadNorGeometry(device,WaterProbeTimeoutMs,log,out geometry) || geometry.DeviceSizeBytes<=0)
                return CreateNoCartridgeResult(norId);
        }
        else geometry=ReadNorGeometry(device,log:log);
        NormalizeGeometryForWaterDirectAccess(geometry, norId, log);
        NormalizeWaterDirectBufferWriteBytes(geometry, norId, log);
        geometry.IsS70Gl02GsDualDie = IsS70Gl02GsDualDieNorId(norId);
        WaterRuntimeStatusInfo runtimeStatus = ReadWaterRuntimeStatus(device, log);
        bool detectedWaterLinkMultiNor = ResolveWaterLinkMultiNorMode(
            geometry,
            runtimeStatus,
            useWaterLinkMultiNor,
            log);
        if (detectedWaterLinkMultiNor)
        {
            ApplyWaterLinkMultiNorGeometry(geometry, runtimeStatus, log);
        }
        LogWaterDirectAddressDiagnostics(device, log);
        string? recognizedChipName = GetChipName(norId, geometry.DeviceSizeBytes, geometry.BufferWriteBytes);
        bool chipRecognized = !string.IsNullOrWhiteSpace(recognizedChipName);
        int detectedPhysicalChipCount = GetDetectedPhysicalNorChipCount(geometry, runtimeStatus);
        string chipName = detectedWaterLinkMultiNor
            ? !string.IsNullOrWhiteSpace(recognizedChipName)
                ? $"WL multi-nor ({recognizedChipName} x{detectedPhysicalChipCount})"
                : "WL multi-nor"
            : recognizedChipName ?? T("value", "chip_unknown", "Unknown / Needs confirmation");

        log?.Invoke(chipName);
        log?.Invoke(BuildCapacitySummary(geometry));

        List<DetectedNorGameInfo> games = ScanNorGameHeaders(device, geometry.DeviceSizeBytes, log);
        HashSet<int> knownGameStartBuckets = games
            .Where(game => !game.IsMenu && game.StartBucket > 0)
            .Select(game => game.StartBucket)
            .ToHashSet();

        WaterWlMappingInfo wlMapping = ReadWlMapping(device, log);
        WaterMenuConfigInfo menuConfig = ReadUniversalImageMenuConfig(device, knownGameStartBuckets, log)
            ?? ReadFixedWaterLinkMenuConfig(device, knownGameStartBuckets, log)
            ?? ReadMenuConfig(device, geometry, knownGameStartBuckets, log);
        WaterMagicMetadataSyncInfo magicMetadataSync = WaterMagicUpgradeBuilder.AnalyzeMagicMetadataSync(menuConfig, wlMapping);
        LogMagicMetadataSyncSummary(magicMetadataSync, log);
        List<WaterCartEntry> entries = BuildEntries(games, menuConfig, wlMapping, log);

        return new WaterCartScanResult
        {
            NorId = norId,
            RawNorIdHex = BitConverter.ToString(norId).Replace("-", " "),
            IsChipRecognized = chipRecognized,
            ChipName = chipName,
            CapacitySummary = FormatMiBSize(geometry.DeviceSizeBytes),
            SectorSummary = BuildSectorSummary(geometry),
            Geometry = geometry,
            IsWriteProtected = geometry.DeviceSizeBytes <= 0,
            WriteProtectionReason = geometry.DeviceSizeBytes > 0 ? string.Empty : GetCapacityUnavailableReason(),
            Games = games,
            MenuConfig = menuConfig,
            WlMapping = wlMapping,
            MagicMetadataSync = magicMetadataSync,
            RuntimeStatus = runtimeStatus,
            Entries = entries,
        };
    }

    public static WaterCartScanResult ScanLegacyBeggar(WaterDeviceProtocol device, Action<string>? log = null)
    {
        if (!device.TryReadLegacyNorId(out byte[] norId, LegacyBeggarTimeoutMs))
        {
            throw new InvalidOperationException(T("error", "read_nor_id_failed", "Failed to read NOR ID."));
        }

        log?.Invoke(BitConverter.ToString(norId).Replace("-", " "));


        log?.Invoke(T(
            "log",
            "beggar_scan_path",
            "Detected the Beggar programmer. Using the legacy scan path: read NOR ID, CFI, 4MiB headers, and menu/WL metadata through plain ROM reads."));

        NorGeometryInfo geometry;
        if (!TryReadNorGeometry(device, LegacyBeggarTimeoutMs, log, out geometry))
        {
            if(LooksLikeNoCartridgeNorId(norId))return CreateNoCartridgeResult(norId);
            geometry = CreateFallbackLegacyBeggarGeometry(norId);
            log?.Invoke(T(
                "log",
                "beggar_scan_cfi_fallback",
                "CFI could not be decoded. Trying available NOR-ID geometry data; an unknown capacity remains unavailable."));
        }

        geometry.IsS70Gl02GsDualDie = IsS70Gl02GsDualDieNorId(norId);
        string? recognizedChipName = GetChipName(norId, geometry.DeviceSizeBytes, geometry.BufferWriteBytes);
        bool chipRecognized = !string.IsNullOrWhiteSpace(recognizedChipName);
        string chipName = recognizedChipName ?? T("value", "chip_unknown", "Unknown / Needs confirmation");
        bool writeProtected = geometry.DeviceSizeBytes <= 0;

        log?.Invoke(chipName);
        log?.Invoke(BuildCapacitySummary(geometry));

        List<DetectedNorGameInfo> games = ScanNorGameHeadersLegacyBeggar(device, geometry.DeviceSizeBytes, log);
        HashSet<int> knownGameStartBuckets = games
            .Where(game => !game.IsMenu && game.StartBucket > 0)
            .Select(game => game.StartBucket)
            .ToHashSet();

        if (!TrySwitchLegacyBeggarBucket(device, 0, LegacyBeggarTimeoutMs))
        {
            throw new InvalidOperationException(Tf(
                "error",
                "beggar_switch_bucket_failed",
                "Failed to switch to legacy 4MiB bucket {0:00}.",
                0));
        }

        WaterMenuConfigInfo menuConfig = ReadUniversalImageMenuConfig(
                device,
                knownGameStartBuckets,
                log,
                LegacyBeggarTimeoutMs)
            ?? ReadMenuConfig(device, geometry, knownGameStartBuckets, log, LegacyBeggarTimeoutMs);
        WaterWlMappingInfo wlMapping = ReadWlMapping(device, log, LegacyBeggarTimeoutMs);
        WaterMagicMetadataSyncInfo magicMetadataSync = WaterMagicUpgradeBuilder.AnalyzeMagicMetadataSync(menuConfig, wlMapping);
        List<WaterCartEntry> entries = BuildEntries(games, menuConfig, wlMapping, log);

        return new WaterCartScanResult
        {
            NorId = norId,
            RawNorIdHex = BitConverter.ToString(norId).Replace("-", " "),
            IsChipRecognized = chipRecognized,
            ChipName = chipName,
            CapacitySummary = FormatMiBSize(geometry.DeviceSizeBytes),
            SectorSummary = BuildSectorSummary(geometry),
            Geometry = geometry,
            IsWriteProtected = writeProtected,
            WriteProtectionReason = writeProtected ? GetCapacityUnavailableReason() : string.Empty,
            Games = games,
            MenuConfig = menuConfig,
            WlMapping = wlMapping,
            MagicMetadataSync = magicMetadataSync,
            Entries = entries,
        };
    }

    private static void PopulateOfflineGbaMenuImageState(
        byte[] imageBytes,
        WaterMenuConfigInfo menuConfig,
        bool usesWlLayout,
        Action<string>? log,
        out bool isUniversalGbaMenuDetected,
        out bool isSimplifiedNsGbaMenuDetected,
        out GbaMenuFirstScreenPreviewInfo firstScreenPreview,
        out byte[] backgroundBitmapData,
        out byte[] backgroundPaletteData)
    {
        isUniversalGbaMenuDetected = false;
        isSimplifiedNsGbaMenuDetected = false;
        firstScreenPreview = new GbaMenuFirstScreenPreviewInfo();
        backgroundBitmapData = [];
        backgroundPaletteData = [];

        if (!menuConfig.IsDetected)
        {
            return;
        }

        int menuAreaLength = menuConfig.Status.ItemListAddress;
        if (!TryReadImageRange(imageBytes, 0, menuAreaLength, out byte[] menuArea) || menuAreaLength <= 0)
        {
            return;
        }

        isUniversalGbaMenuDetected = GbaMenuRomService.SupportsCovers(menuArea);
        isSimplifiedNsGbaMenuDetected = GbaMenuRomService.HasSimplifiedNsMenuSignature(menuArea);
        firstScreenPreview = GbaMenuFirstScreenPreviewCodec.Decode(menuArea, menuConfig.Items);

        if (firstScreenPreview.IsDetected ||
            !TryResolveMenuBackgroundRegion(menuArea, usesWlLayout, out int backgroundOffset))
        {
            return;
        }

        const int bitmapLength = BackgroundImageEncoder.Width * BackgroundImageEncoder.Height;
        const int paletteLength = 0x200;
        if (backgroundOffset < 0 ||
            backgroundOffset + bitmapLength + paletteLength > menuArea.Length)
        {
            log?.Invoke(T(
                "log",
                "menu_background_region_invalid",
                "Menu background data is outside the scanned menu area."));
            return;
        }

        backgroundBitmapData = menuArea.AsSpan(backgroundOffset, bitmapLength).ToArray();
        backgroundPaletteData = menuArea.AsSpan(backgroundOffset + bitmapLength, paletteLength).ToArray();
    }

    private static bool TryResolveMenuBackgroundRegion(
        byte[] source,
        bool usesWlLayout,
        out int backgroundOffset)
    {
        backgroundOffset = -1;
        int markerOffset = FindByteSequence(source, MenuBackgroundMarker);
        int markerGapBytes = usesWlLayout && !GbaMenuRomService.SupportsCovers(source)
            ? WlMenuBackgroundMarkerGapBytes
            : 0;
        int requiredPrefixLength = MenuBackgroundRegionLengthBytes + markerGapBytes;
        if (markerOffset < requiredPrefixLength)
        {
            return false;
        }

        backgroundOffset = markerOffset - requiredPrefixLength;
        if (backgroundOffset < 0 ||
            backgroundOffset + MenuBackgroundRegionLengthBytes > source.Length)
        {
            backgroundOffset = -1;
            return false;
        }

        return true;
    }

    private static int FindByteSequence(byte[] source, byte[] pattern)
    {
        if (source.Length == 0 || pattern.Length == 0 || pattern.Length > source.Length)
        {
            return -1;
        }

        for (int index = 0; index <= source.Length - pattern.Length; index++)
        {
            bool matched = true;
            for (int patternIndex = 0; patternIndex < pattern.Length; patternIndex++)
            {
                if (source[index + patternIndex] != pattern[patternIndex])
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return index;
            }
        }

        return -1;
    }

    public static bool TryScanBeggarMaxGbc(WaterDeviceProtocol device, Action<string>? log, out WaterCartScanResult result, bool scanContent = true)
    {
        result = new WaterCartScanResult();

        try
        {
            bool flashIdRead = TryGbcReadFlashId(device, log, out byte[] flashId);
            string rawFlashIdHex = flashIdRead
                ? BitConverter.ToString(flashId).Replace("-", " ")
                : "CFI ONLY";

            if (flashIdRead)
            {
                log?.Invoke(Tf("log", "beggar_max_probe_id", "GBC/MAX probe returned Flash ID: {0}", rawFlashIdHex));
            }

            NorGeometryInfo? geometry = TryReadGbcNorGeometry(device, log);
            bool flashIdRecognized = flashIdRead && LooksLikeSupportedBeggarMaxId(flashId);

            if (!flashIdRead && geometry is null)
            {
                return false;
            }

            NorGeometryInfo resolvedGeometry = geometry ?? CreateFallbackBeggarMaxGeometry(flashId);
            byte[] expandedNorId = flashIdRead
                ? ExpandBeggarMaxIdToLegacyNorId(flashId)
                : [];
            string chipName = flashIdRecognized
                ? GetBeggarMaxChipName(flashId, resolvedGeometry.DeviceSizeBytes)
                    ?? GetChipName(expandedNorId, resolvedGeometry.DeviceSizeBytes, resolvedGeometry.BufferWriteBytes)
                    ?? T("value", "chip_unknown", "Unknown / Needs confirmation")
                : T("value", "chip_unknown", "Unknown / Needs confirmation");
            List<DetectedNorGameInfo> games=[];
            List<WaterCartEntry> entries=[];
            bool isFcContent=false,isMdContent=false;
            bool hasDirectoryHeaders=scanContent && ScanBeggarMaxContent(device,resolvedGeometry.DeviceSizeBytes,log,out games,out entries,out isFcContent,out isMdContent);

            result = new WaterCartScanResult
            {
                IsBeggarMaxGbcMode = true,
                HasBeggarMaxDirectoryHeaders = hasDirectoryHeaders,
                IsFcMbcxContent = isFcContent,
                IsMdMbcxContent = isMdContent,
                IsChipRecognized = flashIdRecognized,
                NorId = expandedNorId,
                RawNorIdHex = rawFlashIdHex,
                ChipName = chipName,
                CapacitySummary = FormatMiBSize(resolvedGeometry.DeviceSizeBytes),
                SectorSummary = BuildSectorSummary(resolvedGeometry),
                Geometry = resolvedGeometry,
                IsWriteProtected = resolvedGeometry.DeviceSizeBytes <= 0,
                WriteProtectionReason = resolvedGeometry.DeviceSizeBytes > 0 ? string.Empty : GetCapacityUnavailableReason(),
                Games = games,
                Entries = entries,
            };

            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke(Tf(
                "log",
                "beggar_max_probe_exception",
                "GBC/MAX probe stopped because of an error: {0}",
                ex.Message));
            return false;
        }
    }

    public static WaterCartScanResult ScanOfflineRomImage(byte[] imageBytes, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);

        if (imageBytes.Length <= 0)
        {
            throw new InvalidOperationException(T("error", "rom_file_empty", "The ROM file is empty."));
        }

        NorGeometryInfo geometry = GuessOfflineGeometry(imageBytes.Length);
        List<DetectedNorGameInfo> games = ScanNorGameHeadersFromImage(imageBytes, log);
        HashSet<int> knownGameStartBuckets = games
            .Where(game => !game.IsMenu && game.StartBucket > 0)
            .Select(game => game.StartBucket)
            .ToHashSet();

        WaterWlMappingInfo wlMapping = ReadWlMappingFromImage(imageBytes, log);
        WaterMenuConfigInfo? universalMenuConfig = ReadUniversalImageMenuConfigFromImage(
            imageBytes,
            knownGameStartBuckets,
            log);
        WaterMenuConfigInfo menuConfig = universalMenuConfig ?? (ShouldUseFixedWaterLinkMenuLayout(wlMapping)
            ? ReadFixedWaterLinkMenuConfigFromImage(imageBytes, knownGameStartBuckets, log)
            : ReadMenuConfigFromImage(imageBytes, knownGameStartBuckets, log));
        if (wlMapping.IsDetected && (wlMapping.Header.Flags & GbaMultiMenuBuilder.Services.Chm1Layout.LocalSaveFlag) != 0)
            menuConfig.Status.ExplicitSaveAreaAddress = GbaMultiMenuBuilder.Services.Chm1Layout.SaveAreaOffset;

        if (menuConfig.IsDetected && menuConfig.Status.SectorSize > 0)
        {
            geometry.SectorSizeBytes = menuConfig.Status.SectorSize;
            geometry.SectorCount = DivCeil(geometry.DeviceSizeBytes, geometry.SectorSizeBytes);
        }
        else if (wlMapping.IsDetected)
        {
            geometry.SectorSizeBytes = WaterWlMappingCodec.SectorSize;
            geometry.SectorCount = DivCeil(geometry.DeviceSizeBytes, geometry.SectorSizeBytes);
        }

        WaterMagicMetadataSyncInfo magicMetadataSync = WaterMagicUpgradeBuilder.AnalyzeMagicMetadataSync(menuConfig, wlMapping);
        LogMagicMetadataSyncSummary(magicMetadataSync, log);
        List<WaterCartEntry> entries = BuildEntries(games, menuConfig, wlMapping, log);

        PopulateOfflineGbaMenuImageState(
            imageBytes,
            menuConfig,
            ShouldUseFixedWaterLinkMenuLayout(wlMapping),
            log,
            out bool isUniversalGbaMenuDetected,
            out bool isSimplifiedNsGbaMenuDetected,
            out GbaMenuFirstScreenPreviewInfo firstScreenPreview,
            out byte[] menuBackgroundBitmapData,
            out byte[] menuBackgroundPaletteData);

        return new WaterCartScanResult
        {
            RawNorIdHex = "OFFLINE",
            IsChipRecognized = true,
            ChipName = T("value", "offline_rom_image", "Offline ROM Image"),
            CapacitySummary = FormatMiBSize(imageBytes.Length),
            SectorSummary = BuildSectorSummary(geometry),
            Geometry = geometry,
            Games = games,
            MenuConfig = menuConfig,
            WlMapping = wlMapping,
            MagicMetadataSync = magicMetadataSync,
            MenuBackgroundBitmapData = menuBackgroundBitmapData,
            MenuBackgroundPaletteData = menuBackgroundPaletteData,
            IsUniversalGbaMenuDetected = isUniversalGbaMenuDetected,
            IsSimplifiedNsGbaMenuDetected = isSimplifiedNsGbaMenuDetected,
            GbaMenuFirstScreenPreview = firstScreenPreview,
            Entries = entries,
        };
    }

    public static WaterCartScanResult RebuildCachedScanResult(
        WaterCartScanResult source,
        WaterWlMappingInfo? wlMappingOverride = null,
        WaterMenuConfigInfo? menuConfigOverride = null,
        IReadOnlyList<DetectedNorGameInfo>? gamesOverride = null,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        List<DetectedNorGameInfo> games = CloneGames(gamesOverride ?? source.Games);

        WaterMenuConfigInfo menuConfig = CloneMenuConfig(menuConfigOverride ?? source.MenuConfig);

        WaterWlMappingInfo wlMapping = wlMappingOverride is not null
            ? CloneWlMapping(wlMappingOverride)
            : CloneWlMapping(source.WlMapping);

        WaterMagicMetadataSyncInfo magicMetadataSync = WaterMagicUpgradeBuilder.AnalyzeMagicMetadataSync(menuConfig, wlMapping);
        List<WaterCartEntry> entries = source.IsBeggarMaxGbcMode
            ? CloneEntries(source.Entries, games)
            : BuildEntries(games, menuConfig, wlMapping, log);

        return new WaterCartScanResult
        {
            IsBeggarMaxGbcMode = source.IsBeggarMaxGbcMode,
            IsFcMbcxContent = source.IsFcMbcxContent,
            IsMdMbcxContent = source.IsMdMbcxContent,
            IsChm1 = source.IsChm1,
            HasBeggarMaxDirectoryHeaders = source.HasBeggarMaxDirectoryHeaders,
            IsChipRecognized = source.IsChipRecognized,
            IsWriteProtected = source.IsWriteProtected,
            WriteProtectionReason = source.WriteProtectionReason,
            NorId = source.NorId.ToArray(),
            RawNorIdHex = source.RawNorIdHex,
            ChipName = source.ChipName,
            CapacitySummary = source.CapacitySummary,
            SectorSummary = source.SectorSummary,
            Geometry = new NorGeometryInfo
            {
                DeviceSizeBytes = source.Geometry.DeviceSizeBytes,
                SectorCount = source.Geometry.SectorCount,
                SectorSizeBytes = source.Geometry.SectorSizeBytes,
                BufferWriteBytes = source.Geometry.BufferWriteBytes,
                LargestSectorSizeBytes = source.Geometry.LargestSectorSizeBytes,
                RegionCount = source.Geometry.RegionCount,
                PrimaryCommandSetId = source.Geometry.PrimaryCommandSetId,
                CommandSetName = source.Geometry.CommandSetName,
                IsS70Gl02GsDualDie = source.Geometry.IsS70Gl02GsDualDie,
                IsWaterLinkMultiNor = source.Geometry.IsWaterLinkMultiNor,
            },
            Games = games,
            MenuConfig = menuConfig,
            WlMapping = wlMapping,
            MagicMetadataSync = magicMetadataSync,
            SaveSlotSummary = new WaterSaveSlotSummaryInfo
            {
                IsAvailable = source.SaveSlotSummary.IsAvailable,
                HasFullAbPreview = source.SaveSlotSummary.HasFullAbPreview,
                Entries = source.SaveSlotSummary.Entries.ToArray(),
            },
            RuntimeStatus = new WaterRuntimeStatusInfo
            {
                IsAvailable = source.RuntimeStatus.IsAvailable,
                BucketId = source.RuntimeStatus.BucketId,
                MapperLocked = source.RuntimeStatus.MapperLocked,
                GameModeActive = source.RuntimeStatus.GameModeActive,
                SupportsWaterLinkFixedLayout = source.RuntimeStatus.SupportsWaterLinkFixedLayout,
                NorChipValidMask = source.RuntimeStatus.NorChipValidMask,
                NorChipCount = source.RuntimeStatus.NorChipCount,
                CurrentGameSlot = source.RuntimeStatus.CurrentGameSlot,
                SaveDelayRaw = source.RuntimeStatus.SaveDelayRaw,
            },
            WaterLinkSaveDataByEntrySlot = source.WaterLinkSaveDataByEntrySlot.ToDictionary(static pair => pair.Key, static pair => pair.Value),
            WaterLinkSaveStatusTextByEntrySlot = source.WaterLinkSaveStatusTextByEntrySlot.ToDictionary(static pair => pair.Key, static pair => pair.Value),
            LegacySaveDataByEntrySlot = source.LegacySaveDataByEntrySlot.ToDictionary(static pair => pair.Key, static pair => pair.Value),
            LegacySaveStatusTextByEntrySlot = source.LegacySaveStatusTextByEntrySlot.ToDictionary(static pair => pair.Key, static pair => pair.Value),
            BatterylessSaveDataByEntrySlot = source.BatterylessSaveDataByEntrySlot.ToDictionary(static pair => pair.Key, static pair => pair.Value),
            BatterylessSaveStatusTextByEntrySlot = source.BatterylessSaveStatusTextByEntrySlot.ToDictionary(static pair => pair.Key, static pair => pair.Value),
            MenuBackgroundBitmapData = source.MenuBackgroundBitmapData.ToArray(),
            MenuBackgroundPaletteData = source.MenuBackgroundPaletteData.ToArray(),
            IsUniversalGbaMenuDetected = source.IsUniversalGbaMenuDetected,
            IsSimplifiedNsGbaMenuDetected = source.IsSimplifiedNsGbaMenuDetected,
            GbaMenuFirstScreenPreview = new GbaMenuFirstScreenPreviewInfo
            {
                IsDetected = source.GbaMenuFirstScreenPreview.IsDetected,
                PackOffset = source.GbaMenuFirstScreenPreview.PackOffset,
                PackSize = source.GbaMenuFirstScreenPreview.PackSize,
                PaletteBase = source.GbaMenuFirstScreenPreview.PaletteBase,
                PaletteData = source.GbaMenuFirstScreenPreview.PaletteData.ToArray(),
                GameCodesByIndex = source.GbaMenuFirstScreenPreview.GameCodesByIndex
                    .ToDictionary(static pair => pair.Key, static pair => pair.Value),
                CoverPixelsByIndex = source.GbaMenuFirstScreenPreview.CoverPixelsByIndex
                    .ToDictionary(static pair => pair.Key, static pair => pair.Value.ToArray()),
            },
            Entries = entries,
        };
    }

    public static NorGeometryInfo ReadNorGeometry(
        WaterDeviceProtocol device,
        int timeoutMs = Timeout.Infinite,
        Action<string>? log = null)
    {
        if (!TryReadNorGeometry(device, timeoutMs, log, out NorGeometryInfo geometry))
        {
            throw new InvalidOperationException(T("error", "read_cfi_failed", "Failed to read CFI data."));
        }

        return geometry;
    }

    public static bool TryReadNorGeometry(
        WaterDeviceProtocol device,
        int timeoutMs,
        Action<string>? log,
        out NorGeometryInfo geometry)
    {
        geometry = new NorGeometryInfo();
        if (!TryProbeNorCfi(device, timeoutMs, log, out NorCfiProbeResult? probe) ||
            probe is null)
        {
            return false;
        }

        geometry = new NorGeometryInfo
        {
            DeviceSizeBytes = probe.DeviceSizeBytes,
            SectorCount = probe.SectorCount,
            SectorSizeBytes = probe.SectorSizeBytes,
            LargestSectorSizeBytes = probe.LargestSectorSizeBytes,
            BufferWriteBytes = probe.BufferWriteBytes,
            RegionCount = probe.RegionCount,
            PrimaryCommandSetId = probe.PrimaryCommandSetId,
            CommandSetName = probe.CommandSetName,
        };

        LogNorCfiDiagnostics(probe, geometry, log);
        return true;
    }

    public static List<DetectedNorGameInfo> ScanNorGameHeaders(WaterDeviceProtocol device, int deviceSizeBytes, Action<string>? log = null)
    {
        int bucketCount = Math.Max(1, deviceSizeBytes / BucketSizeBytes);
        byte[] header = new byte[200];
        byte[] logo = new byte[156];
        List<DetectedNorGameInfo> games = [];

        log?.Invoke(T("log", "start_scan_headers", "Start scanning NOR 4MiB headers."));

        for (int bucket = 0; bucket < bucketCount; bucket++)
        {
            uint bucketAddress = (uint)(bucket * BucketSizeBytes);
            if (!device.TryReadRom(bucketAddress, header))
            {
                throw new InvalidOperationException(
                    Tf("error", "read_nor_header_failed", "Failed to read NOR header at bucket {0:00}.", bucket));
            }

            Array.Copy(header, 4, logo, 0, logo.Length);
            if (!logo.SequenceEqual(NintendoLogoGba))
            {
                continue;
            }

            string title = DecodeGbaHeaderTitle(header);
            bool isMenu = bucket == 0 && LooksLikeMenuTitle(title);

            games.Add(new DetectedNorGameInfo
            {
                StartBucket = bucket,
                EndBucket = bucket,
                Title = title,
                GameCode = DecodeGbaHeaderGameCode(header),
                IsMenu = isMenu,
            });

            log?.Invoke(isMenu
                ? Tf("log", "found_menu_header", "Found menu header: [{0,3} M] {1}", bucket * 4, title)
                : Tf("log", "found_game_header", "Found game header: [{0,3} M] {1}", bucket * 4, title));
        }

        games = games
            .OrderBy(game => game.StartBucket)
            .ToList();

        for (int i = 0; i < games.Count; i++)
        {
            int nextStartBucket = i + 1 < games.Count
                ? games[i + 1].StartBucket
                : bucketCount;

            games[i].EndBucket = Math.Max(games[i].StartBucket, nextStartBucket - 1);
        }

        if (games.Count == 0)
        {
            log?.Invoke(T("log", "no_valid_headers", "No valid GBA ROM headers were found."));
        }
        else
        {
            log?.Invoke(Tf("log", "header_scan_complete", "Header scan complete. Identified {0} NOR record(s).", games.Count));
        }

        return games;
    }

    private static NorCfiProbeResult ProbeNorCfi(
        WaterDeviceProtocol device,
        int timeoutMs,
        Action<string>? log)
    {
        if (!TryProbeNorCfi(device, timeoutMs, log, out NorCfiProbeResult? result) ||
            result is null)
        {
            throw new InvalidOperationException(T("error", "read_cfi_failed", "Failed to read CFI data."));
        }

        return result;
    }

    private static bool TryProbeNorCfi(
        WaterDeviceProtocol device,
        int timeoutMs,
        Action<string>? log,
        out NorCfiProbeResult? result)
    {
        const uint enterWordAddress = 0x55u;
        result = null;
        if (!TryEnterNorCfi(device, enterWordAddress, timeoutMs))
        {
            return false;
        }

        try
        {
            byte[] window = new byte[NorCfiReadWindowBytes];
            if (!device.TryReadRom(0u, window, timeoutMs))
            {
                return false;
            }

            return TryParseNorCfiWindow(window, (int)enterWordAddress, out result) &&
                   result is not null;
        }
        finally
        {
            ExitNorCfi(device, timeoutMs);
        }
    }

    private static bool TryEnterNorCfi(WaterDeviceProtocol device, uint enterWordAddress, int timeoutMs)
    {
        ExitNorCfi(device, timeoutMs);
        return device.TryWriteRom(enterWordAddress, BitConverter.GetBytes((ushort)0x98), timeoutMs);
    }

    private static void ExitNorCfi(WaterDeviceProtocol device, int timeoutMs)
    {
        device.TryWriteRom(0x00, BitConverter.GetBytes((ushort)0xF0), timeoutMs);
    }

    private static bool TryParseNorCfiWindow(
        byte[] window,
        int enterWordAddress,
        out NorCfiProbeResult? result)
    {
        result = null;

        if (window.Length < NorCfiReadWindowBytes)
        {
            return false;
        }

        if (window[0x20] != (byte)'Q' ||
            window[0x22] != (byte)'R' ||
            window[0x24] != (byte)'Y')
        {
            return false;
        }

        int deviceExponent = window[0x4E];
        if (deviceExponent <= 0 || deviceExponent >= 31)
        {
            return false;
        }

        int deviceSizeBytes = 1 << deviceExponent;
        int bufferWriteExponent = ReadCfiLaneU16(window, 0x54);
        int bufferWriteBytes = bufferWriteExponent <= 1 || bufferWriteExponent >= 16
            ? 0
            : 1 << bufferWriteExponent;

        int regionCount = window[0x58];
        if (regionCount <= 0)
        {
            return false;
        }

        int sectorCount = 0;
        int smallestSectorSizeBytes = 0;
        int largestSectorSizeBytes = 0;

        for (int i = 0; i < regionCount; i++)
        {
            int baseOffset = 0x5A + (i * 8);
            if (baseOffset + 6 >= window.Length)
            {
                return false;
            }

            int sectorCountInRegion = ReadCfiLaneU16(window, baseOffset) + 1;
            int sectorSizeInRegion = ReadCfiLaneU16(window, baseOffset + 4) * 256;
            if (sectorCountInRegion <= 0 || sectorSizeInRegion <= 0)
            {
                return false;
            }

            sectorCount += sectorCountInRegion;
            smallestSectorSizeBytes = smallestSectorSizeBytes == 0
                ? sectorSizeInRegion
                : Math.Min(smallestSectorSizeBytes, sectorSizeInRegion);
            largestSectorSizeBytes = Math.Max(largestSectorSizeBytes, sectorSizeInRegion);
        }

        if (sectorCount <= 0 || smallestSectorSizeBytes <= 0)
        {
            return false;
        }

        ushort primaryCommandSetId = (ushort)ReadCfiLaneU16(window, 0x26);
        string commandSetName = primaryCommandSetId switch
        {
            0x0001 or 0x0003 => "Intel",
            0x0002 or 0x0004 => "AMD",
            _ => "Unknown",
        };

        result = new NorCfiProbeResult
        {
            RawWindow = window,
            EnterWordAddress = enterWordAddress,
            DeviceSizeBytes = deviceSizeBytes,
            SectorCount = sectorCount,
            SectorSizeBytes = smallestSectorSizeBytes,
            LargestSectorSizeBytes = largestSectorSizeBytes,
            BufferWriteBytes = bufferWriteBytes,
            RegionCount = regionCount,
            PrimaryCommandSetId = primaryCommandSetId,
            CommandSetName = commandSetName,
        };
        return true;
    }

    private static int ReadCfiLaneU16(byte[] window, int lowByteOffset)
    {
        if (lowByteOffset < 0 || lowByteOffset + 2 >= window.Length)
        {
            return 0;
        }

        return (window[lowByteOffset + 2] << 8) | window[lowByteOffset];
    }

    private static List<DetectedNorGameInfo> ScanNorGameHeadersLegacyBeggar(WaterDeviceProtocol device, int deviceSizeBytes, Action<string>? log)
    {
        int bucketCount = Math.Max(1, deviceSizeBytes / BucketSizeBytes);
        byte[] header = new byte[200];
        byte[] logo = new byte[156];
        List<DetectedNorGameInfo> games = [];

        log?.Invoke(T("log", "start_scan_headers", "Start scanning NOR 4MiB headers."));

        try
        {
            for (int bucket = 0; bucket < bucketCount; bucket++)
            {
                if (!TrySwitchLegacyBeggarBucket(device, bucket, LegacyBeggarTimeoutMs))
                {
                    throw new InvalidOperationException(Tf(
                        "error",
                        "beggar_switch_bucket_failed",
                        "Failed to switch to legacy 4MiB bucket {0:00}.",
                        bucket));
                }

                if (!device.TryReadRom(0, header, LegacyBeggarTimeoutMs))
                {
                    throw new InvalidOperationException(
                        Tf("error", "read_nor_header_failed", "Failed to read NOR header at bucket {0:00}.", bucket));
                }

                Array.Copy(header, 4, logo, 0, logo.Length);
                if (!logo.SequenceEqual(NintendoLogoGba))
                {
                    continue;
                }

                string title = DecodeGbaHeaderTitle(header);
                bool isMenu = bucket == 0 && LooksLikeMenuTitle(title);

                games.Add(new DetectedNorGameInfo
                {
                    StartBucket = bucket,
                    EndBucket = bucket,
                    Title = title,
                    GameCode = DecodeGbaHeaderGameCode(header),
                    IsMenu = isMenu,
                });

                log?.Invoke(isMenu
                    ? Tf("log", "found_menu_header", "Found menu header: [{0,3} M] {1}", bucket * 4, title)
                    : Tf("log", "found_game_header", "Found game header: [{0,3} M] {1}", bucket * 4, title));
            }
        }
        finally
        {
            TrySwitchLegacyBeggarBucket(device, 0, LegacyBeggarTimeoutMs);
        }

        games = games
            .OrderBy(game => game.StartBucket)
            .ToList();

        for (int i = 0; i < games.Count; i++)
        {
            int nextStartBucket = i + 1 < games.Count
                ? games[i + 1].StartBucket
                : bucketCount;

            games[i].EndBucket = Math.Max(games[i].StartBucket, nextStartBucket - 1);
        }

        if (games.Count == 0)
        {
            log?.Invoke(T("log", "no_valid_headers", "No valid GBA ROM headers were found."));
        }
        else
        {
            log?.Invoke(Tf("log", "header_scan_complete", "Header scan complete. Identified {0} NOR record(s).", games.Count));
        }

        return games;
    }

    private static List<DetectedNorGameInfo> ScanNorGameHeadersFromImage(byte[] imageBytes, Action<string>? log)
    {
        int bucketCount = Math.Max(1, DivCeil(imageBytes.Length, BucketSizeBytes));
        byte[] header = new byte[200];
        byte[] logo = new byte[156];
        List<DetectedNorGameInfo> games = [];

        log?.Invoke(T("log", "start_scan_headers", "Start scanning NOR 4MiB headers."));

        for (int bucket = 0; bucket < bucketCount; bucket++)
        {
            Array.Fill(header, (byte)0xFF);
            int bucketOffset = bucket * BucketSizeBytes;
            if (bucketOffset < imageBytes.Length)
            {
                int copyLength = Math.Min(header.Length, imageBytes.Length - bucketOffset);
                Buffer.BlockCopy(imageBytes, bucketOffset, header, 0, copyLength);
            }

            Array.Copy(header, 4, logo, 0, logo.Length);
            if (!logo.SequenceEqual(NintendoLogoGba))
            {
                continue;
            }

            string title = DecodeGbaHeaderTitle(header);
            bool isMenu = bucket == 0 && LooksLikeMenuTitle(title);

            games.Add(new DetectedNorGameInfo
            {
                StartBucket = bucket,
                EndBucket = bucket,
                Title = title,
                GameCode = DecodeGbaHeaderGameCode(header),
                IsMenu = isMenu,
            });

            log?.Invoke(isMenu
                ? Tf("log", "found_menu_header", "Found menu header: [{0,3} M] {1}", bucket * 4, title)
                : Tf("log", "found_game_header", "Found game header: [{0,3} M] {1}", bucket * 4, title));
        }

        games = games
            .OrderBy(game => game.StartBucket)
            .ToList();

        for (int i = 0; i < games.Count; i++)
        {
            int nextStartBucket = i + 1 < games.Count
                ? games[i + 1].StartBucket
                : bucketCount;

            games[i].EndBucket = Math.Max(games[i].StartBucket, nextStartBucket - 1);
        }

        if (games.Count == 0)
        {
            log?.Invoke(T("log", "no_valid_headers", "No valid GBA ROM headers were found."));
        }
        else
        {
            log?.Invoke(Tf("log", "header_scan_complete", "Header scan complete. Identified {0} NOR record(s).", games.Count));
        }

        return games;
    }

    private static bool ScanBeggarMaxContent(
        WaterDeviceProtocol device,
        int deviceSizeBytes,
        Action<string>? log,
        out List<DetectedNorGameInfo> games,
        out List<WaterCartEntry> entries,
        out bool isFcContent, out bool isMdContent)
    {
        if (TryScanBeggarMaxDirectoryContent(device, log, out games, out entries, out isFcContent, out isMdContent))
        {
            return true;
        }

        if (isFcContent || isMdContent) return false; // Recognized NES data is not a GB directory failure.
        games = [];
        entries = [];
        log?.Invoke(T(
            "log",
            "beggar_max_directory_missing_empty",
            "No valid MBCX directory was detected. GBC/MAX content list will stay empty."));
        return false;
    }

    private static void ScanBeggarMaxHeaderSlotContent(
        WaterDeviceProtocol device,
        int deviceSizeBytes,
        Action<string>? log,
        out List<DetectedNorGameInfo> games,
        out List<WaterCartEntry> entries)
    {
        games = [];
        entries = [];
        int visibleIndex = 0;
        int dedicatedSaveBankCursor = 0;

        log?.Invoke(T("log", "beggar_max_header_scan_start", "Start scanning MAX MBC5 header slots."));

        foreach (BeggarMaxSlotCandidate candidate in EnumerateBeggarMaxSlotCandidates(deviceSizeBytes))
        {
            byte[] header = new byte[0x150];
            if (!TryReadBeggarMaxRomRange(device, candidate.StartAddress, header))
            {
                log?.Invoke(Tf(
                    "log",
                    "beggar_max_header_read_failed",
                    "Failed to read MAX slot {0} header at 0x{1:X6}.",
                    candidate.Label,
                    candidate.StartAddress));
                continue;
            }

            if (!TryParseBeggarMaxHeader(header, out BeggarMaxHeaderInfo headerInfo))
            {
                continue;
            }

            DetectedNorGameInfo headerGame = new()
            {
                StartBucket = visibleIndex,
                EndBucket = visibleIndex,
                Title = string.IsNullOrWhiteSpace(headerInfo.Title)
                    ? T("value", "untitled", "UNTITLED")
                    : headerInfo.Title,
                IsMenu = candidate.IsMenu,
            };

            WaterCartEntry entry = new()
            {
                HeaderGame = headerGame,
                AllocatedSizeBytesOverride = candidate.ReservedSizeBytes,
                EntrySlotOverride = candidate.SaveSlotIndex,
                SaveLengthBytesOverride = ResolveBeggarMaxSaveLengthBytes(headerInfo, candidate.SaveSlotIndex),
                SourceTextOverride = T("value", "source_gbc", "GBC"),
                SaveTypeTextOverride = BuildBeggarMaxSaveTypeText(headerInfo),
                RomOffsetBankOverride = candidate.StartAddress / GameBoyRomHeaderService.RomBankSize,
                RomBankCountOverride = ResolveBeggarMaxHeaderRomBankCount(candidate, headerInfo),
                SaveBankMapOverride = BuildDefaultBeggarMaxSaveBankMap(
                    headerInfo.EffectiveRamBankCount,
                    headerInfo.HasBattery && headerInfo.EffectiveRamBankCount > 0,
                    ref dedicatedSaveBankCursor),
                UsesSharedSaveOverride = headerInfo.EffectiveRamBankCount > 0 && !headerInfo.HasBattery,
                MbcTypeOverride = headerInfo.CartridgeType,
                RamBankCountOverride = headerInfo.EffectiveRamBankCount,
                HasBatteryOverride = headerInfo.HasBattery,
                DirectoryTitleOverride = headerInfo.Title,
            };

            games.Add(headerGame);
            entries.Add(entry);
            visibleIndex++;

            log?.Invoke(Tf(
                "log",
                "beggar_max_header_detected",
                "Found MAX slot {0}: {1}",
                candidate.Label,
                headerGame.Title));
        }

        if (games.Count == 0)
        {
            log?.Invoke(T("log", "beggar_max_no_valid_headers", "No valid MAX MBC5 headers were found."));
        }
        else
        {
            log?.Invoke(Tf(
                "log",
                "beggar_max_header_scan_complete",
                "MAX header scan complete. Identified {0} record(s).",
                games.Count));
        }
    }

    private static bool TryScanBeggarMaxDirectoryContent(
        WaterDeviceProtocol device,
        Action<string>? log,
        out List<DetectedNorGameInfo> games,
        out List<WaterCartEntry> entries,
        out bool isFcContent, out bool isMdContent)
    {
        isFcContent = false;isMdContent = false;
        games = [];
        entries = [];

        byte[] configHeader = new byte[GbcConfigHeaderSize];
        byte[] directoryHeader = new byte[GbcRomDirectoryHeaderSize];
        if (!TryReadBeggarMaxRomRange(device, GbcConfigOffsetBytes, configHeader) ||
            !TryReadBeggarMaxRomRange(device, GbcRomDirectoryOffsetBytes, directoryHeader))
        {
            return false;
        }

        if(configHeader.AsSpan(0,4).SequenceEqual("MD02"u8)){
            isMdContent=true;log?.Invoke("检测到 MD02 数据卡；GB 菜单写入已禁用。");return false;
        }

        if (!configHeader.AsSpan(0, 7).SequenceEqual("MBCXCFG"u8) ||
            !directoryHeader.AsSpan(0, 7).SequenceEqual("MBCXROM"u8))
        {
            return false;
        }

        if(GbaMultiMenuBuilder.Fc.FcMbcxCodec.HasFcIdentity(configHeader))
        {
            isFcContent = true;
            log?.Invoke(T("fc","detected","NES data card detected; opening the NES workspace."));
            return false;
        }

        bool useChineseDirectoryNames = configHeader.Length > 11 && configHeader[11] != 0;
        int entryCount = BitConverter.ToUInt16(directoryHeader, 10);
        int entrySize = BitConverter.ToUInt16(directoryHeader, 12);
        uint entryBase = BitConverter.ToUInt32(directoryHeader, 16);
        if (entryCount <= 0)
        {
            log?.Invoke(T("log", "beggar_max_directory_empty", "MBCX directory is present but contains no game entries."));
            return true;
        }

        entrySize = Math.Clamp(entrySize, 14, GbcRomEntrySize);
        if (entryBase < GbcRomDirectoryOffsetBytes)
        {
            entryBase = GbcRomDirectoryOffsetBytes + GbcRomDirectoryHeaderSize;
        }

        log?.Invoke(Tf(
            "log",
            "beggar_max_directory_detected",
            "Detected MBCX directory: {0} entry(s), entry size {1} bytes, base 0x{2:X6}.",
            entryCount,
            entrySize,
            entryBase));

        for (int index = 0; index < entryCount; index++)
        {
            byte[] entryBytes = new byte[entrySize];
            if (!TryReadBeggarMaxRomRange(device, checked((int)entryBase + (index * entrySize)), entryBytes))
            {
                continue;
            }

            if (!TryParseBeggarMaxDirectoryEntry(device, index, entryBytes, useChineseDirectoryNames, out BeggarMaxDirectoryEntryInfo? parsedInfo) ||
                parsedInfo is null)
            {
                continue;
            }

            BeggarMaxDirectoryEntryInfo info = parsedInfo;
            DetectedNorGameInfo headerGame = new()
            {
                StartBucket = index,
                EndBucket = index,
                Title = string.IsNullOrWhiteSpace(info.HeaderTitle) ? info.Title : info.HeaderTitle,
                IsMenu = false,
            };

            WaterCartEntry entry = new()
            {
                HeaderGame = headerGame,
                AllocatedSizeBytesOverride = info.AllocatedSizeBytes,
                EntrySlotOverride = info.Index,
                SaveLengthBytesOverride = info.SaveLengthBytes,
                SourceTextOverride = T("value", "source_gbc", "GBC"),
                SaveTypeTextOverride = BuildBeggarMaxDirectorySaveTypeText(info),
                RomOffsetBankOverride = info.RomOffsetBank,
                RomBankCountOverride = info.RomBankCount,
                SaveBankMapOverride = info.SaveBankMap.ToArray(),
                UsesSharedSaveOverride = info.UsesSharedSave,
                MbcTypeOverride = info.CartridgeType,
                RamBankCountOverride = entryBytes[10],
                HasBatteryOverride = info.HasBattery,
                DirectoryTitleOverride = info.Title,
            };

            games.Add(headerGame);
            entries.Add(entry);
        }

        log?.Invoke(entries.Count == 0
            ? T("log", "beggar_max_directory_no_entries", "Detected MBCX headers, but no valid game entries were parsed.")
            : Tf(
                "log",
                "beggar_max_directory_scan_complete",
                "MBCX directory scan complete. Identified {0} game entry(s).",
                entries.Count));

        return true;
    }

    private static bool TryParseBeggarMaxDirectoryEntry(
        WaterDeviceProtocol device,
        int index,
        byte[] entryBytes,
        bool useChineseDirectoryName,
        out BeggarMaxDirectoryEntryInfo? info)
    {
        info = null;
        if (entryBytes.Length < 14 || (entryBytes[13] & 0x01) == 0)
        {
            return false;
        }

        int romOffsetBank = checked((int)BitConverter.ToUInt32(entryBytes, 4));
        int romBankCount = BitConverter.ToUInt16(entryBytes, 8);
        if (romOffsetBank < (GbcFixedMapReservedBytes / GameBoyRomHeaderService.RomBankSize) || romBankCount <= 0)
        {
            return false;
        }

        string title = DecodeBeggarMaxDirectoryEntryName(entryBytes, useChineseDirectoryName);
        int ramBankCount = entryBytes[10];
        byte cartridgeType = entryBytes[11];
        bool hasBattery = entryBytes[12] != 0;
        byte[] saveMap = Enumerable.Repeat((byte)0xFF, GbcMaxGameSramBanks).ToArray();
        for (int i = 0; i < saveMap.Length && GbcRomSramMapOffset + i < entryBytes.Length; i++)
        {
            saveMap[i] = entryBytes[GbcRomSramMapOffset + i];
        }

        int absoluteAddress = checked(romOffsetBank * GameBoyRomHeaderService.RomBankSize);
        byte[] header = new byte[0x150];
        if (!TryReadBeggarMaxRomRange(device, absoluteAddress, header) ||
            !TryParseBeggarMaxHeader(header, out BeggarMaxHeaderInfo headerInfo))
        {
            return false;
        }

        bool usesSharedSave = ramBankCount > 0 &&
            saveMap[0] >= GbcSharedSramBankStart &&
            saveMap[0] < GbcTotalSramBankCount;
        int saveLengthBytes = ResolveBeggarMaxDirectorySaveLengthBytes(
            headerInfo,
            ramBankCount,
            usesSharedSave,
            hasBattery,
            cartridgeType);

        info = new BeggarMaxDirectoryEntryInfo
        {
            Index = index,
            Title = string.IsNullOrWhiteSpace(title) ? headerInfo.Title : title,
            HeaderTitle = headerInfo.Title,
            RomOffsetBank = romOffsetBank,
            RomBankCount = romBankCount,
            AllocatedSizeBytes = checked(romBankCount * GameBoyRomHeaderService.RomBankSize),
            SaveLengthBytes = saveLengthBytes,
            CartridgeType = cartridgeType,
            HasBattery = hasBattery,
            UsesSharedSave = usesSharedSave,
            SaveBankMap = saveMap,
        };

        return true;
    }

    private static string DecodeBeggarMaxDirectoryEntryName(byte[] entryBytes, bool useChineseDirectoryName)
    {
        if (entryBytes.Length < 14)
        {
            return string.Empty;
        }

        int codeUnitCount = Math.Min(BitConverter.ToUInt16(entryBytes, 2), (entryBytes.Length - 14) / 2);
        if (codeUnitCount <= 0)
        {
            return string.Empty;
        }

        if (!useChineseDirectoryName)
        {
            int asciiLength = Math.Min(codeUnitCount, Math.Min(16, entryBytes.Length - 14));
            int terminatorIndex = Array.IndexOf(entryBytes, (byte)0x00, 14, asciiLength);
            if (terminatorIndex >= 14)
            {
                asciiLength = terminatorIndex - 14;
            }

            return Encoding.ASCII.GetString(entryBytes, 14, asciiLength).Trim();
        }

        List<char> chars = [];
        bool fallbackAscii = false;
        for (int i = 0; i < codeUnitCount; i++)
        {
            ushort value = BitConverter.ToUInt16(entryBytes, 14 + (i * 2));
            if (value == 0)
            {
                if (chars.Count > 0)
                {
                    fallbackAscii = true;
                    break;
                }

                continue;
            }

            chars.Add((char)value);
        }

        string primary = new string(chars.ToArray()).Trim();
        if (!fallbackAscii)
        {
            return primary;
        }

        int separatorIndex = primary.IndexOf('\0');
        return separatorIndex >= 0
            ? primary[..separatorIndex].Trim()
            : primary;
    }

    private static int ResolveBeggarMaxDirectorySaveLengthBytes(
        BeggarMaxHeaderInfo info,
        int ramBankCount,
        bool usesSharedSave,
        bool hasBattery,
        byte cartridgeType)
    {
        if (!hasBattery)
        {
            return 0;
        }

        if (!usesSharedSave)
        {
            if (cartridgeType is 0x05 or 0x06)
            {
                return 0x200;
            }

            if (info.RamSizeBytes > 0)
            {
                return Math.Max(info.RamSizeBytes, GameBoyRomHeaderService.RamBankSize);
            }

            return Math.Max(0, ramBankCount) * GameBoyRomHeaderService.RamBankSize;
        }

        if (cartridgeType is 0x05 or 0x06)
        {
            return 0x200;
        }

        if (info.RamSizeBytes > 0)
        {
            return Math.Min(
                Math.Max(info.RamSizeBytes, GameBoyRomHeaderService.RamBankSize),
                0x8000);
        }

        return ramBankCount > 0
            ? Math.Min(ramBankCount * GameBoyRomHeaderService.RamBankSize, 0x8000)
            : 0;
    }

    private static string BuildBeggarMaxDirectorySaveTypeText(BeggarMaxDirectoryEntryInfo info)
    {
        if (info.SaveLengthBytes <= 0)
        {
            return T("value", "save_type_none", "NONE");
        }

        if (info.CartridgeType is 0x05 or 0x06)
        {
            return "MBC2";
        }

        string baseText = $"RAM {FormatKiBSize(info.SaveLengthBytes)}";
        return info.UsesSharedSave ? $"{baseText} (Shared)" : baseText;
    }

    private static IEnumerable<BeggarMaxSlotCandidate> EnumerateBeggarMaxSlotCandidates(int deviceSizeBytes)
    {
        List<BeggarMaxSlotCandidate> candidates =
        [
            new BeggarMaxSlotCandidate(0x000000, 0x100000, "Menu/GM", true, -1),
            new BeggarMaxSlotCandidate(0x100000, 0x100000, "Game 01", false, 0),
        ];

        for (int i = 1; i < 16; i++)
        {
            candidates.Add(new BeggarMaxSlotCandidate(0x200000 * i, 0x200000, $"Game {(i + 1):00}", false, i));
        }

        return candidates.Where(candidate => candidate.StartAddress < deviceSizeBytes);
    }

    private static bool TryReadBeggarMaxRomRange(WaterDeviceProtocol device, int absoluteAddress, byte[] buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int currentAddress = absoluteAddress + offset;
            int bank = currentAddress >> 14;
            int cartAddress = (bank == 0 ? 0x0000 : 0x4000) + (currentAddress & 0x3FFF);
            int availableInBank = 0x4000 - (currentAddress & 0x3FFF);
            int readLength = Math.Min(buffer.Length - offset, availableInBank);

            if (!TrySwitchBeggarMaxRomBank(device, bank))
            {
                return false;
            }

            byte[] chunk = new byte[readLength];
            if (!device.TryReadGbc((uint)cartAddress, chunk, GbcProbeTimeoutMs))
            {
                return false;
            }

            Buffer.BlockCopy(chunk, 0, buffer, offset, readLength);
            offset += readLength;
        }

        return true;
    }

    private static bool TrySwitchBeggarMaxRomBank(WaterDeviceProtocol device, int bank)
    {
        byte low = (byte)(bank & 0xFF);
        byte high = (byte)((bank >> 8) & 0xFF);

        return device.TryWriteGbc(0x3000, [high], GbcProbeTimeoutMs) &&
               device.TryWriteGbc(0x2000, [low], GbcProbeTimeoutMs);
    }

    private static bool TryParseBeggarMaxHeader(byte[] header, out BeggarMaxHeaderInfo info)
    {
        info = new BeggarMaxHeaderInfo();
        if (header.Length < 0x150)
        {
            return false;
        }

        if (!header.AsSpan(0x104, NintendoLogoGb.Length).SequenceEqual(NintendoLogoGb))
        {
            return false;
        }

        byte expectedChecksum = CalculateBeggarMaxHeaderChecksum(header);
        if (header[0x14D] != expectedChecksum)
        {
            return false;
        }

        bool isColor = header[0x143] is 0x80 or 0xC0;
        bool hasMakerCode = isColor && header.AsSpan(0x13F, 4).ToArray().Any(value => value != 0x00);
        ReadOnlySpan<byte> titleBytes = header.AsSpan(0x134, hasMakerCode ? 0x0B : 0x0F);
        string title = Encoding.ASCII.GetString(titleBytes)
            .TrimEnd('\0', ' ')
            .Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            title = T("value", "untitled", "UNTITLED");
        }

        info = new BeggarMaxHeaderInfo
        {
            Title = title,
            IsColor = isColor,
            RomSizeCode = header[0x148],
            RomSizeBytes = DecodeBeggarMaxRomSize(header[0x148]),
            RamSizeCode = header[0x149],
            RamSizeBytes = DecodeBeggarMaxRamSize(header[0x149]),
            CartridgeType = header[0x147],
            EffectiveRamBankCount = GameBoyRomHeaderService.GetEffectiveRamBankCount(header[0x147], header[0x149]),
            HasBattery = IsBatteryBackedBeggarMaxCartridgeType(header[0x147]),
        };
        return true;
    }

    private static byte CalculateBeggarMaxHeaderChecksum(byte[] header)
    {
        int checksum = 0;
        for (int i = 0x134; i <= 0x14C; i++)
        {
            checksum = checksum - header[i] - 1;
        }

        return (byte)(checksum & 0xFF);
    }

    private static int DecodeBeggarMaxRomSize(byte romSizeCode)
    {
        return romSizeCode switch
        {
            < 8 => (32 * 1024) << romSizeCode,
            0x52 => 72 * 16 * 1024,
            0x53 => 80 * 16 * 1024,
            0x54 => 96 * 16 * 1024,
            _ => 0,
        };
    }

    private static int DecodeBeggarMaxRamSize(byte ramSizeCode)
    {
        return ramSizeCode switch
        {
            // MAX SRAM is allocated and transferred in whole 8 KiB banks.
            0x01 => 8 * 1024,
            0x02 => 8 * 1024,
            0x03 => 32 * 1024,
            0x04 => 128 * 1024,
            0x05 => 64 * 1024,
            _ => 0,
        };
    }

    private static bool IsBatteryBackedBeggarMaxCartridgeType(byte cartridgeType)
    {
        return cartridgeType is
            0x03 or
            0x06 or
            0x09 or
            0x0D or
            0x0F or
            0x10 or
            0x13 or
            0x1B or
            0x1E or
            0x22 or
            0xFF;
    }

    private static int ResolveBeggarMaxHeaderRomBankCount(BeggarMaxSlotCandidate candidate, BeggarMaxHeaderInfo info)
    {
        int reservedBankCount = Math.Max(2, candidate.ReservedSizeBytes / GameBoyRomHeaderService.RomBankSize);
        int headerBankCount = info.RomSizeBytes > 0
            ? Math.Max(2, DivCeil(info.RomSizeBytes, GameBoyRomHeaderService.RomBankSize))
            : reservedBankCount;
        return Math.Clamp(headerBankCount, 2, reservedBankCount);
    }

    private static string BuildBeggarMaxSaveTypeText(BeggarMaxHeaderInfo info)
    {
        if (info.CartridgeType is 0x05 or 0x06)
        {
            return "MBC2";
        }

        if (info.RamSizeBytes <= 0)
        {
            return T("value", "save_type_none", "NONE");
        }

        int saveLengthBytes = Math.Max(info.RamSizeBytes, GameBoyRomHeaderService.RamBankSize);
        return $"RAM {FormatKiBSize(saveLengthBytes)}";
    }

    private static int ResolveBeggarMaxSaveLengthBytes(BeggarMaxHeaderInfo info, int entrySlot)
    {
        if (entrySlot < 0 || !info.HasBattery)
        {
            return 0;
        }

        if (info.CartridgeType is 0x05 or 0x06)
        {
            return 0x200;
        }

        if (info.RamSizeBytes > 0)
        {
            return Math.Min(
                Math.Max(info.RamSizeBytes, GameBoyRomHeaderService.RamBankSize),
                0x8000);
        }

        return entrySlot < 16
            ? 0x8000
            : 0;
    }

    private static byte[] BuildDefaultBeggarMaxSaveBankMap(
        int ramBankCount,
        bool usesDedicatedSaveAllocation,
        ref int dedicatedSaveBankCursor)
    {
        byte[] result = Enumerable.Repeat((byte)0xFF, GbcMaxGameSramBanks).ToArray();
        if (ramBankCount <= 0)
        {
            return result;
        }

        if (!usesDedicatedSaveAllocation)
        {
            for (int i = 0; i < ramBankCount && i < GbcSharedSramBankCount && i < result.Length; i++)
            {
                result[i] = (byte)(GbcSharedSramBankStart + i);
            }

            return result;
        }

        for (int i = 0; i < ramBankCount && i < result.Length; i++)
        {
            int bank = dedicatedSaveBankCursor + i;
            if (bank >= GbcSharedSramBankStart)
            {
                break;
            }

            result[i] = (byte)bank;
        }

        dedicatedSaveBankCursor += ramBankCount;
        return result;
    }

    private static List<WaterCartEntry> CloneEntries(
        IEnumerable<WaterCartEntry> entries,
        IReadOnlyList<DetectedNorGameInfo> games)
    {
        Dictionary<(int StartBucket, bool IsMenu), DetectedNorGameInfo> gameByStartBucket = games
            .GroupBy(game => (game.StartBucket, game.IsMenu))
            .ToDictionary(group => group.Key, group => group.First());

        return entries
            .Select(entry =>
            {
                DetectedNorGameInfo? headerGame = null;
                if (entry.HeaderGame is not null &&
                    gameByStartBucket.TryGetValue((entry.HeaderGame.StartBucket, entry.HeaderGame.IsMenu), out DetectedNorGameInfo? clonedGame))
                {
                    headerGame = clonedGame;
                }

                return new WaterCartEntry
                {
                    HeaderGame = headerGame,
                    MenuItem = entry.MenuItem is null ? null : WaterMenuConfigCodec.Clone(entry.MenuItem),
                    WlRecord = entry.WlRecord is null
                        ? null
                        : new WlRomMapRecordInfo
                        {
                            EntrySlot = entry.WlRecord.EntrySlot,
                            LogicalBucketCount = entry.WlRecord.LogicalBucketCount,
                            PhysicalBuckets = entry.WlRecord.PhysicalBuckets.ToList(),
                        },
                    AllocatedSizeBytesOverride = entry.AllocatedSizeBytesOverride,
                    EntrySlotOverride = entry.EntrySlotOverride,
                    SaveLengthBytesOverride = entry.SaveLengthBytesOverride,
                    SourceTextOverride = entry.SourceTextOverride,
                    SaveTypeTextOverride = entry.SaveTypeTextOverride,
                    RomOffsetBankOverride = entry.RomOffsetBankOverride,
                    RomBankCountOverride = entry.RomBankCountOverride,
                    SaveBankMapOverride = entry.SaveBankMapOverride.ToArray(),
                    UsesSharedSaveOverride = entry.UsesSharedSaveOverride,
                    MbcTypeOverride = entry.MbcTypeOverride,
                    RamBankCountOverride = entry.RamBankCountOverride,
                    HasBatteryOverride = entry.HasBatteryOverride,
                    DirectoryTitleOverride = entry.DirectoryTitleOverride,
                };
            })
            .ToList();
    }

    public static string DecodeGbaHeaderTitle(byte[] header)
    {
        if (header.Length < 172)
        {
            return LocalizedText.T("value", "untitled", "UNTITLED");
        }

        string title = Encoding.ASCII.GetString(header, 160, 12).TrimEnd('\0', ' ');
        return string.IsNullOrWhiteSpace(title)
            ? LocalizedText.T("value", "untitled", "UNTITLED")
            : title;
    }

    public static string DecodeGbaHeaderGameCode(byte[] header)
    {
        if (header.Length < 0xB0)
        {
            return string.Empty;
        }

        string code = System.Text.Encoding.ASCII.GetString(header, 0xAC, 4);
        return code.All(char.IsLetterOrDigit) ? code.ToUpperInvariant() : string.Empty;
    }

    public static string? GetChipName(byte[] id, int deviceSize) =>
        GetChipName(id, deviceSize, 0);

    public static string? GetChipName(byte[] id, int deviceSize, int bufferWriteBytes)
    {
        if (id.Length < 8)
        {
            return null;
        }

        if (id[0] == 0x01 &&
            id[1] == 0x00 &&
            id[2] == 0x7E &&
            id[3] == 0x22 &&
            id[5] == 0x22 &&
            id[6] == 0x01 &&
            id[7] == 0x22)
        {
            return GetSpansionGlChipName(id[4], bufferWriteBytes);
        }

        if (IdMatches(id, 0x89, 0x00, 0x7E, 0x22, 0x22, 0x22, 0x01, 0x22))
        {
            return "JS28F256 / MT28EW256ABA";
        }

        if (id[0] == 0x89 &&
            id[1] == 0x00 &&
            id[2] == 0x7E &&
            id[3] == 0x22 &&
            id[5] == 0x22 &&
            id[6] == 0x01 &&
            id[7] == 0x22)
        {
            return id[4] switch
            {
                0x21 => "Intel/Micron NOR family (128Mbit)",
                0x22 => "JS28F256 / MT28EW256ABA",
                0x28 when deviceSize == 128 * 1024 * 1024 => "MT28EW01G-family (inferred)",
                0x28 => "Intel/Micron NOR family (1Gbit)",
                0x48 => "Intel/Micron NOR family (2Gbit)",
                _ => null,
            };
        }

        if (IdMatches(id, 0xC2, 0x00, 0x7E, 0x22, 0x22, 0x22, 0x01, 0x22))
        {
            return "MX29GL256E/F";
        }

        if (IdMatches(id, 0x20, 0x00, 0x7E, 0x22, 0x22, 0x22, 0x01, 0x22))
        {
            return "M29W256G";
        }

        return null;
    }

    private static string? GetSpansionGlChipName(byte densityCode, int bufferWriteBytes)
    {
        return densityCode switch
        {
            0x21 => bufferWriteBytes switch
            {
                32 => "S29GL128N",
                64 => "S29GL128P",
                512 => "S29GL128S",
                _ => "S29GL128N/P/S",
            },
            0x22 => bufferWriteBytes switch
            {
                32 => "S29GL256N",
                64 => "S29GL256P",
                512 => "S29GL256S",
                _ => "S29GL256N/P/S",
            },
            0x23 => bufferWriteBytes switch
            {
                32 => "S29GL512N",
                64 => "S29GL512P",
                512 => "S29GL512S",
                _ => "S29GL512N/P/S",
            },
            0x28 => bufferWriteBytes switch
            {
                64 => "S29GL01GP",
                512 => "S29GL01GS",
                _ => "S29GL01GP/GS",
            },
            0x48 => "S70GL02GS",
            _ => null,
        };
    }

    public static string? GetBeggarMaxChipName(byte[] id, int deviceSize)
    {
        if (id.Length < 4)
        {
            return null;
        }

        if (id[0] == 0x01 && id[1] == 0x7E)
        {
            return id[2] switch
            {
                0x21 => "S29GL128N/P/S",
                0x22 => "S29GL256N/P/S",
                0x23 => "S29GL512N/P/S",
                0x28 => "S29GL01GP/GS",
                0x48 => "S70GL02GS",
                _ => deviceSize == 128 * 1024 * 1024 ? "S29GL01G-family" : "Spansion/Infineon NOR family",
            };
        }

        if (id[0] == 0x89 && id[1] == 0x7E)
        {
            return id[2] switch
            {
                0x22 => "JS28F256 / MT28EW256ABA",
                0x28 when deviceSize == 128 * 1024 * 1024 => "MT28EW01G-family",
                _ => "Intel/Micron NOR family",
            };
        }

        if (id[0] == 0xC2 && id[1] == 0x7E && id[2] == 0x22)
        {
            return "MX29GL256E/F";
        }

        if (id[0] == 0xC2 && id[1] == 0xCB)
        {
            return "MX29LV640EB";
        }

        if (id[0] == 0xC2 && id[1] == 0xC9)
        {
            return "MX29LV640ET";
        }

        if (id[0] == 0x20 && id[1] == 0x7E && id[2] == 0x22)
        {
            return "M29W256G";
        }

        return null;
    }

    private static void NormalizeGeometryForWaterDirectAccess(
        NorGeometryInfo geometry,
        byte[] norId,
        Action<string>? log)
    {
        if (!TryGetKnownNorDeviceSizeBytes(norId, out int inferredDeviceSize))
        {
            return;
        }

        if (inferredDeviceSize <= 0 || geometry.DeviceSizeBytes >= inferredDeviceSize)
        {
            return;
        }

        int previousDeviceSize = geometry.DeviceSizeBytes;
        geometry.DeviceSizeBytes = inferredDeviceSize;

        if (geometry.SectorSizeBytes > 0)
        {
            geometry.SectorCount = DivCeil(geometry.DeviceSizeBytes, geometry.SectorSizeBytes);
        }

        log?.Invoke(Tf(
            "log",
            "water_geometry_capacity_corrected",
            "WaterLink direct-address scan corrected NOR capacity from {0} to {1} using the NOR ID.",
            FormatMiBSize(previousDeviceSize),
            FormatMiBSize(geometry.DeviceSizeBytes)));
    }

    private static void NormalizeWaterDirectBufferWriteBytes(
        NorGeometryInfo geometry,
        byte[] norId,
        Action<string>? log)
    {
        if (!TryGetKnownWaterDirectBufferWriteBytes(norId, out int knownBufferWriteBytes))
        {
            return;
        }

        if (knownBufferWriteBytes <= 0 || geometry.BufferWriteBytes == knownBufferWriteBytes)
        {
            return;
        }

        int previousBufferWriteBytes = geometry.BufferWriteBytes;
        geometry.BufferWriteBytes = knownBufferWriteBytes;

        log?.Invoke(Tf(
            "log",
            "water_geometry_buffer_corrected",
            "WaterLink direct-address scan corrected NOR buffer write size from {0} to {1}.",
            FormatBufferWriteSize(previousBufferWriteBytes),
            FormatBufferWriteSize(geometry.BufferWriteBytes)));
    }

    private static void ApplyWaterLinkMultiNorGeometry(
        NorGeometryInfo geometry,
        WaterRuntimeStatusInfo runtimeStatus,
        Action<string>? log)
    {
        int previousDeviceSize = geometry.DeviceSizeBytes;
        int detectedChipCount = runtimeStatus.NorChipCount;
        if (detectedChipCount <= 0)
        {
            detectedChipCount = WaterLinkMultiNorSizeBytes / WaterLinkMultiNorChipSizeBytes;
        }
        detectedChipCount = Math.Clamp(detectedChipCount, 1, 4);

        geometry.DeviceSizeBytes = detectedChipCount * WaterLinkMultiNorChipSizeBytes;
        if (geometry.SectorSizeBytes > 0)
        {
            geometry.SectorCount = DivCeil(geometry.DeviceSizeBytes, geometry.SectorSizeBytes);
        }

        geometry.IsWaterLinkMultiNor = true;

        int detectedPhysicalChipCount = GetDetectedPhysicalNorChipCount(geometry, runtimeStatus);
        log?.Invoke(Tf(
            "log",
            "waterlink_multi_nor_profile",
            "WaterLink multi-NOR detected: capacity {0}, physical NOR chips {1}, logical 128 MiB targets {2}/4, physical chip boundary {3}, explicit erase-before-write.",
            FormatMiBSize(geometry.DeviceSizeBytes),
            detectedPhysicalChipCount,
            detectedChipCount,
            FormatMiBSize(WaterLinkMultiNorChipSizeBytes)));
        if (previousDeviceSize > 0 && previousDeviceSize != geometry.DeviceSizeBytes)
        {
            log?.Invoke(Tf(
                "log",
                "waterlink_multi_nor_capacity_corrected",
                "WaterLink multi-NOR profile overrides the scanned single-chip capacity from {0} to {1}.",
                FormatMiBSize(previousDeviceSize),
                FormatMiBSize(geometry.DeviceSizeBytes)));
        }
    }

    private static bool ResolveWaterLinkMultiNorMode(
        NorGeometryInfo geometry,
        WaterRuntimeStatusInfo runtimeStatus,
        bool selectedMultiNorMode,
        Action<string>? log)
    {
        if (!runtimeStatus.IsAvailable || runtimeStatus.NorChipCount <= 0)
        {
            return selectedMultiNorMode;
        }

        int physicalChipCount = GetDetectedPhysicalNorChipCount(geometry, runtimeStatus);
        bool detectedMultiNorMode = physicalChipCount > 1;
        if (detectedMultiNorMode != selectedMultiNorMode)
        {
            log?.Invoke(Tf(
                "log",
                "waterlink_multi_nor_auto_detected",
                "WaterLink NOR layout auto-detection overrides the selected profile: physical NOR chips={0}, logical 128 MiB targets={1}, multi-NOR={2}.",
                physicalChipCount,
                runtimeStatus.NorChipCount,
                detectedMultiNorMode));
        }

        return detectedMultiNorMode;
    }

    private static int GetDetectedPhysicalNorChipCount(
        NorGeometryInfo geometry,
        WaterRuntimeStatusInfo runtimeStatus)
    {
        int logicalTargetCount = runtimeStatus.NorChipCount;
        if (logicalTargetCount <= 0)
        {
            logicalTargetCount = Math.Max(1, geometry.DeviceSizeBytes / WaterLinkMultiNorChipSizeBytes);
        }

        int logicalTargetsPerPhysicalChip = geometry.IsS70Gl02GsDualDie ? 2 : 1;
        return Math.Clamp(
            DivCeil(logicalTargetCount, logicalTargetsPerPhysicalChip),
            1,
            4);
    }

    private static int CountSetBits(int value)
    {
        int count = 0;
        value &= ExtendedSlotInfoNorMaskBits;
        while (value != 0)
        {
            count += value & 1;
            value >>= 1;
        }

        return count;
    }

    private static bool TryGetKnownWaterDirectBufferWriteBytes(byte[] id, out int bufferWriteBytes)
    {
        bufferWriteBytes = 0;

        if (id.Length < 8)
        {
            return false;
        }

        if (IdMatches(id, 0x89, 0x00, 0x7E, 0x22, 0x22, 0x22, 0x01, 0x22))
        {
            bufferWriteBytes = 1024;
            return true;
        }

        if (IdMatches(id, 0x01, 0x00, 0x7E, 0x22, 0x48, 0x22, 0x01, 0x22))
        {
            bufferWriteBytes = 512;
            return true;
        }

        return false;
    }

    private static bool TryGetKnownNorDeviceSizeBytes(byte[] id, out int deviceSizeBytes)
    {
        deviceSizeBytes = 0;

        if (id.Length < 8)
        {
            return false;
        }

        if ((id[0] == 0x01 || id[0] == 0x89) &&
            id[1] == 0x00 &&
            id[2] == 0x7E &&
            id[3] == 0x22 &&
            id[5] == 0x22 &&
            id[6] == 0x01 &&
            id[7] == 0x22)
        {
            return TryGetDensityMappedDeviceSizeBytes(id[4], out deviceSizeBytes);
        }

        if (IdMatches(id, 0xC2, 0x00, 0x7E, 0x22, 0x22, 0x22, 0x01, 0x22) ||
            IdMatches(id, 0x20, 0x00, 0x7E, 0x22, 0x22, 0x22, 0x01, 0x22))
        {
            deviceSizeBytes = 32 * 1024 * 1024;
            return true;
        }

        return false;
    }

    private static bool IsS70Gl02GsDualDieNorId(byte[] norId)
    {
        if (norId.Length < 8)
        {
            return false;
        }

        // S70GL02GS exposes a continuous 256 MiB read space, but program/erase
        // commands must be directed to the correct 128 MiB die.
        return norId[0] == 0x01 &&
               norId[1] == 0x00 &&
               norId[2] == 0x7E &&
               norId[3] == 0x22 &&
               norId[4] == 0x48 &&
               norId[5] == 0x22 &&
               norId[6] == 0x01 &&
               norId[7] == 0x22;
    }

    private static bool IsMtNorFamily(byte[] norId)
    {
        if (norId.Length < 8)
        {
            return false;
        }

        return norId[0] == 0x89 &&
               norId[1] == 0x00 &&
               norId[2] == 0x7E &&
               norId[3] == 0x22 &&
               norId[5] == 0x22 &&
               norId[6] == 0x01 &&
               norId[7] == 0x22;
    }

    private static bool TryGetDensityMappedDeviceSizeBytes(byte densityCode, out int deviceSizeBytes)
    {
        deviceSizeBytes = densityCode switch
        {
            0x21 => 16 * 1024 * 1024,
            0x22 => 32 * 1024 * 1024,
            0x23 => 64 * 1024 * 1024,
            0x28 => 128 * 1024 * 1024,
            0x48 => 256 * 1024 * 1024,
            _ => 0,
        };

        return deviceSizeBytes > 0;
    }

    public static WaterRuntimeStatusInfo ParseExtendedRuntimeStatus(byte[] extendedPayload)
    {
        WaterRuntimeStatusInfo status = new();
        if (extendedPayload.Length < 32)
        {
            return status;
        }

        uint slotInfoWord = BitConverter.ToUInt32(extendedPayload, 20);
        if ((slotInfoWord & ExtendedSlotInfoMagicMask) != ExtendedSlotInfoMagic)
        {
            return status;
        }

        uint currentGameSlot = BitConverter.ToUInt32(extendedPayload, 24);
        uint saveDelayMs = BitConverter.ToUInt32(extendedPayload, 28);
        status.IsAvailable = true;
        status.BucketId = (int)(slotInfoWord & 0x7Fu);
        status.MapperLocked = (slotInfoWord & 0x80u) != 0;
        status.GameModeActive = (slotInfoWord & 0x100u) != 0;
        status.SupportsWaterLinkFixedLayout = (slotInfoWord & ExtendedSlotInfoFlagWaterLinkFixedLayout) != 0;
        status.NorChipValidMask = (int)((slotInfoWord >> ExtendedSlotInfoNorMaskShift) & ExtendedSlotInfoNorMaskBits);
        status.NorChipCount = CountSetBits(status.NorChipValidMask);
        status.CurrentGameSlot = currentGameSlot == 0xFFFFFFFFu
            ? null
            : checked((int)currentGameSlot);
        status.SaveDelayRaw = saveDelayMs;
        return status;
    }

    private static WaterRuntimeStatusInfo ReadWaterRuntimeStatus(WaterDeviceProtocol device, Action<string>? log)
    {
        if (!device.TryReadExtendedId(out byte[] extendedPayload))
        {
            log?.Invoke(T(
                "log",
                "water_diag_extended_id_unavailable",
                "WaterLink diagnostics: extended ID/status (0xA4) did not respond."));
            return new WaterRuntimeStatusInfo();
        }

        WaterRuntimeStatusInfo status = ParseExtendedRuntimeStatus(extendedPayload);
        LogExtendedIdDiagnostics(extendedPayload, status, log);
        return status;
    }

    private static void LogExtendedIdDiagnostics(byte[] extendedPayload, WaterRuntimeStatusInfo runtimeStatus, Action<string>? log)
    {
        if (log is null || extendedPayload.Length < 32)
        {
            return;
        }

        uint spiRdid = BitConverter.ToUInt32(extendedPayload, 8);
        uint spiRems = BitConverter.ToUInt32(extendedPayload, 12);
        uint spiCapacityRaw = BitConverter.ToUInt32(extendedPayload, 16);
        uint slotInfoWord = BitConverter.ToUInt32(extendedPayload, 20);
        uint currentGameSlot = BitConverter.ToUInt32(extendedPayload, 24);
        uint saveDelayMs = BitConverter.ToUInt32(extendedPayload, 28);
        int bucketId = runtimeStatus.IsAvailable ? runtimeStatus.BucketId : (int)(slotInfoWord & 0x7Fu);
        bool mapperLocked = runtimeStatus.IsAvailable && runtimeStatus.MapperLocked;
        bool gameModeActive = runtimeStatus.IsAvailable && runtimeStatus.GameModeActive;
        bool supportsFixedLayout = runtimeStatus.IsAvailable && runtimeStatus.SupportsWaterLinkFixedLayout;
        int norChipValidMask = runtimeStatus.IsAvailable ? runtimeStatus.NorChipValidMask : 0;
        int norChipCount = runtimeStatus.IsAvailable ? runtimeStatus.NorChipCount : 0;

        log.Invoke(T("log", "water_diag_extended_id_header", "WaterLink diagnostics: raw extended ID payload (32 bytes)."));
        log.Invoke(BitConverter.ToString(extendedPayload).Replace("-", " "));
        log.Invoke(Tf(
            "log",
            "water_diag_extended_id_fields",
            "WaterLink diagnostics: SPI RDID=0x{0:X8} SPI REMS=0x{1:X8} SPI capacity raw=0x{2:X8} slotInfo=0x{3:X8} currentSlot=0x{4:X8} saveDelay={5} ms",
            spiRdid,
            spiRems,
            spiCapacityRaw,
            slotInfoWord,
            currentGameSlot,
            saveDelayMs));
        log.Invoke(Tf(
            "log",
            "water_diag_extended_id_flags",
            "WaterLink diagnostics: slotInfo flags -> bucket={0}, mapperLocked={1}, gameModeActive={2}, fixedLayout={3}, logicalTargetMask=0x{4:X1}, logical128MiBTargets={5}",
            bucketId,
            mapperLocked,
            gameModeActive,
            supportsFixedLayout,
            norChipValidMask,
            norChipCount));
    }

    private static void LogNorIdInference(byte[] norId, Action<string>? log)
    {
        if (log is null)
        {
            return;
        }

        if (TryGetKnownNorDeviceSizeBytes(norId, out int inferredDeviceSize))
        {
            log.Invoke(Tf(
                "log",
                "water_diag_nor_id_capacity_hint",
                "WaterLink diagnostics: NOR ID capacity hint = {0}",
                FormatMiBSize(inferredDeviceSize)));
            return;
        }

        log.Invoke(T("log", "water_diag_nor_id_capacity_unknown", "WaterLink diagnostics: NOR ID capacity hint is unavailable for this ID."));
    }

    private static void LogNorCfiDiagnostics(
        NorCfiProbeResult probe,
        NorGeometryInfo geometry,
        Action<string>? log)
    {
        if (log is null)
        {
            return;
        }

        log.Invoke(T("log", "water_diag_cfi_geometry_header", "WaterLink diagnostics: raw CFI geometry bytes @ word 0x27..0x30."));
        log.Invoke(BitConverter.ToString(probe.RawWindow.Skip(0x4E).Take(20).ToArray()).Replace("-", " "));
        log.Invoke(Tf(
            "log",
            "water_diag_cfi_window_header",
            "WaterLink diagnostics: raw CFI window @ byte 0x{0:X2}, length {1} bytes.",
            0,
            probe.RawWindow.Length));
        log.Invoke(BitConverter.ToString(probe.RawWindow).Replace("-", " "));

        string qryText = probe.RawWindow.Length >= 0x25
            ? $"{(char)probe.RawWindow[0x20]}{(char)probe.RawWindow[0x22]}{(char)probe.RawWindow[0x24]}"
            : "???";
        log.Invoke(Tf(
            "log",
            "water_diag_cfi_qry",
            "WaterLink diagnostics: CFI signature bytes -> {0}",
            qryText));

        log.Invoke(Tf(
            "log",
            "water_diag_cfi_decoded",
            "WaterLink diagnostics: decoded CFI -> enter=0x{0:X2}, cmdset={1} (0x{2:X4}), capacity={3}, bufferWrite={4}, sectors={5}, sectorSize={6}, largestSector={7}, regions={8}",
            probe.EnterWordAddress,
            probe.CommandSetName,
            probe.PrimaryCommandSetId,
            FormatMiBSize(geometry.DeviceSizeBytes),
            FormatBufferWriteSize(geometry.BufferWriteBytes),
            geometry.SectorCount,
            FormatKiBSize(geometry.SectorSizeBytes),
            FormatKiBSize(geometry.LargestSectorSizeBytes),
            geometry.RegionCount));
    }

    private static void LogWaterDirectAddressDiagnostics(WaterDeviceProtocol device, Action<string>? log)
    {
        if (log is null)
        {
            return;
        }

        log.Invoke(T("log", "water_diag_probe_header", "WaterLink diagnostics: probing direct ROM reads at 0 / 32 / 64 / 96 MiB bucket starts."));

        byte[]? referenceSample = null;
        foreach (uint address in WaterDiagnosticProbeAddresses)
        {
            byte[] sample = new byte[WaterProbeLengthBytes];
            if (!device.TryReadRom(address, sample, WaterProbeTimeoutMs))
            {
                log.Invoke(Tf(
                    "log",
                    "water_diag_probe_failed",
                    "WaterLink diagnostics: probe read failed at 0x{0:X8}.",
                    address));
                continue;
            }

            bool allFf = sample.All(value => value == 0xFF);
            bool allZero = sample.All(value => value == 0x00);
            bool sameAsReference = referenceSample is not null && sample.SequenceEqual(referenceSample);
            bool hasValidLogo = HasNintendoLogo(sample);
            string title = hasValidLogo ? DecodeGbaHeaderTitle(sample) : T("value", "untitled", "UNTITLED");

            log.Invoke(Tf(
                "log",
                "water_diag_probe_summary",
                "WaterLink diagnostics: probe @ 0x{0:X8} -> allFF={1}, all00={2}, sameAs0={3}, gbaLogo={4}, prefix={5}",
                address,
                allFf,
                allZero,
                sameAsReference,
                hasValidLogo,
                FormatHexBytes(sample, 32)));

            if (hasValidLogo)
            {
                log.Invoke(Tf(
                    "log",
                    "water_diag_probe_title",
                    "WaterLink diagnostics: probe title @ 0x{0:X8} -> {1}",
                    address,
                    title));
            }

            referenceSample ??= sample;
        }
    }

    private static WaterMenuConfigInfo ReadMenuConfig(
        WaterDeviceProtocol device,
        NorGeometryInfo geometry,
        HashSet<int> knownGameStartBuckets,
        Action<string>? log,
        int timeoutMs = Timeout.Infinite)
    {
        List<int> candidateSectorSizes = [];
        if (geometry.SectorSizeBytes > 0)
        {
            candidateSectorSizes.Add(geometry.SectorSizeBytes);
        }

        candidateSectorSizes.Add(0x20000);
        candidateSectorSizes.Add(0x40000);
        candidateSectorSizes = candidateSectorSizes
            .Where(size => size > 0 && size <= BucketSizeBytes)
            .Distinct()
            .OrderBy(size => size)
            .ToList();

        int bestScore = int.MinValue;
        WaterMenuConfigInfo? bestConfig = null;

        log?.Invoke(T("log", "start_parse_menu_config", "Start parsing NOR menu config area."));

        foreach (int sectorSize in candidateSectorSizes)
        {
            int headAreaLimit = Math.Min(BucketSizeBytes, Math.Max(BucketSizeBytes, geometry.DeviceSizeBytes));
            for (int statusAddress = sectorSize; statusAddress < headAreaLimit; statusAddress += sectorSize)
            {
                byte[] statusPayload = new byte[16];
                if (!device.TryReadRom((uint)statusAddress, statusPayload, timeoutMs))
                {
                    continue;
                }

                if (!TryDecodeMenuFlashStatus(statusPayload, statusAddress, sectorSize, out MenuFlashStatusInfo statusInfo))
                {
                    continue;
                }

                int itemListAddress = statusInfo.ItemListAddress;
                if (!TryReadMenuItems(device, itemListAddress, out List<MenuItemConfigInfo> items, timeoutMs))
                {
                    continue;
                }

                int[] startBuckets = items
                    .Select(item => item.StartBucket)
                    .Where(bucket => bucket > 0)
                    .Distinct()
                    .ToArray();

                int matchingBucketCount = 0;
                if (knownGameStartBuckets.Count > 0 && startBuckets.Length > 0)
                {
                    matchingBucketCount = startBuckets.Count(knownGameStartBuckets.Contains);
                }

                int score = 10;
                score += items.Count;
                score += startBuckets.Length * 5;
                score += matchingBucketCount * 200;
                if (statusInfo.BatteryPresent)
                {
                    score += 50;
                }

                if (statusInfo.LastBootMenuIndex != 0xFFFF && statusInfo.LastBootMenuIndex < items.Count)
                {
                    score += 25;
                }

                if (bestConfig is null || score > bestScore)
                {
                    bestScore = score;
                    bestConfig = new WaterMenuConfigInfo
                    {
                        IsDetected = true,
                        Status = statusInfo,
                        Items = items,
                    };
                }
            }
        }

        if (bestConfig is null)
        {
            log?.Invoke(T("log", "menu_config_not_detected", "No NOR menu config area was recognized."));
            return new WaterMenuConfigInfo();
        }

        LogMenuConfigSummary(bestConfig, knownGameStartBuckets, log);
        return bestConfig;
    }

    private static WaterMenuConfigInfo ReadMenuConfigFromImage(
        byte[] imageBytes,
        HashSet<int> knownGameStartBuckets,
        Action<string>? log)
    {
        List<int> candidateSectorSizes =
        [
            0x20000,
            0x40000,
        ];

        int bestScore = int.MinValue;
        WaterMenuConfigInfo? bestConfig = null;

        log?.Invoke(T("log", "start_parse_menu_config", "Start parsing NOR menu config area."));

        int headAreaLimit = Math.Min(BucketSizeBytes, imageBytes.Length);
        foreach (int sectorSize in candidateSectorSizes)
        {
            for (int statusAddress = sectorSize; statusAddress < headAreaLimit; statusAddress += sectorSize)
            {
                if (!TryReadImageRange(imageBytes, statusAddress, 16, out byte[] statusPayload))
                {
                    continue;
                }

                if (!TryDecodeMenuFlashStatus(statusPayload, statusAddress, sectorSize, out MenuFlashStatusInfo statusInfo))
                {
                    continue;
                }

                int itemListAddress = statusInfo.ItemListAddress;
                if (!TryReadMenuItemsFromImage(imageBytes, itemListAddress, out List<MenuItemConfigInfo> items))
                {
                    continue;
                }

                int[] startBuckets = items
                    .Select(item => item.StartBucket)
                    .Where(bucket => bucket > 0)
                    .Distinct()
                    .ToArray();

                int matchingBucketCount = 0;
                if (knownGameStartBuckets.Count > 0 && startBuckets.Length > 0)
                {
                    matchingBucketCount = startBuckets.Count(knownGameStartBuckets.Contains);
                }

                int score = 10;
                score += items.Count;
                score += startBuckets.Length * 5;
                score += matchingBucketCount * 200;
                if (statusInfo.BatteryPresent)
                {
                    score += 50;
                }

                if (statusInfo.LastBootMenuIndex != 0xFFFF && statusInfo.LastBootMenuIndex < items.Count)
                {
                    score += 25;
                }

                if (bestConfig is null || score > bestScore)
                {
                    bestScore = score;
                    bestConfig = new WaterMenuConfigInfo
                    {
                        IsDetected = true,
                        Status = statusInfo,
                        Items = items,
                    };
                }
            }
        }

        if (bestConfig is null)
        {
            log?.Invoke(T("log", "menu_config_not_detected", "No NOR menu config area was recognized."));
            return new WaterMenuConfigInfo();
        }

        LogMenuConfigSummary(bestConfig, knownGameStartBuckets, log);
        return bestConfig;
    }

    private static bool ShouldUseFixedWaterLinkMenuLayout(WaterWlMappingInfo wlMapping)
    {
        return wlMapping.IsDetected ||
               wlMapping.LegacyVersionDetected ||
               wlMapping.Header.Magic == WaterWlMappingCodec.Magic;
    }

    private static WaterMenuConfigInfo? ReadUniversalImageMenuConfig(
        WaterDeviceProtocol device,
        HashSet<int> knownGameStartBuckets,
        Action<string>? log,
        int timeoutMs = Timeout.Infinite)
    {
        byte[] payload = new byte[UniversalImageManifestSize];
        if (!device.TryReadRom((uint)UniversalImageManifestOffset, payload, timeoutMs) ||
            !TryDecodeUniversalImageManifest(payload, out UniversalImageManifestLayout? layout) ||
            layout is null)
        {
            return null;
        }

        byte[] statusPayload = new byte[16];
        if (!device.TryReadRom((uint)layout.StatusOffset, statusPayload, timeoutMs) ||
            !TryDecodeMenuFlashStatus(statusPayload, layout.StatusOffset, layout.StatusSize, out MenuFlashStatusInfo statusInfo))
        {
            log?.Invoke(T("log", "universal_image_status_invalid", "Universal image manifest found, but its menu status is invalid."));
            return new WaterMenuConfigInfo();
        }

        statusInfo.ExplicitItemListAddress = layout.ItemListOffset;
        statusInfo.ExplicitSaveAreaAddress = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(28, 4)));
        if (!TryReadMenuItems(device, layout.ItemListOffset, out List<MenuItemConfigInfo> items, timeoutMs) ||
            items.Count <= 0)
        {
            log?.Invoke(T("log", "universal_image_itemlist_invalid", "Universal image manifest found, but its menu item list is invalid."));
            return new WaterMenuConfigInfo();
        }

        LogUniversalManifestEntryCountMismatch(layout.EntryCount, items.Count, log);

        WaterMenuConfigInfo config = new()
        {
            IsDetected = true,
            Status = statusInfo,
            Items = items,
        };
        log?.Invoke(Tf(
            "log",
            "universal_image_menu_detected",
            "Universal menu image detected: itemlist 0x{0:X6}, status 0x{1:X6}, entries {2}.",
            layout.ItemListOffset,
            layout.StatusOffset,
            items.Count));
        LogMenuConfigSummary(config, knownGameStartBuckets, log);
        return config;
    }

    private static WaterMenuConfigInfo? ReadUniversalImageMenuConfigFromImage(
        byte[] imageBytes,
        HashSet<int> knownGameStartBuckets,
        Action<string>? log)
    {
        if (!TryReadImageRange(imageBytes, UniversalImageManifestOffset, UniversalImageManifestSize, out byte[] payload) ||
            !TryDecodeUniversalImageManifest(payload, out UniversalImageManifestLayout? layout) ||
            layout is null)
        {
            return null;
        }

        if (!TryReadImageRange(imageBytes, layout.StatusOffset, 16, out byte[] statusPayload) ||
            !TryDecodeMenuFlashStatus(statusPayload, layout.StatusOffset, layout.StatusSize, out MenuFlashStatusInfo statusInfo))
        {
            log?.Invoke(T("log", "universal_image_status_invalid", "Universal image manifest found, but its menu status is invalid."));
            return new WaterMenuConfigInfo();
        }

        statusInfo.ExplicitItemListAddress = layout.ItemListOffset;
        statusInfo.ExplicitSaveAreaAddress = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(28, 4)));
        if (!TryReadMenuItemsFromImage(imageBytes, layout.ItemListOffset, out List<MenuItemConfigInfo> items) ||
            items.Count <= 0)
        {
            log?.Invoke(T("log", "universal_image_itemlist_invalid", "Universal image manifest found, but its menu item list is invalid."));
            return new WaterMenuConfigInfo();
        }

        LogUniversalManifestEntryCountMismatch(layout.EntryCount, items.Count, log);

        WaterMenuConfigInfo config = new()
        {
            IsDetected = true,
            Status = statusInfo,
            Items = items,
        };
        log?.Invoke(Tf(
            "log",
            "universal_image_menu_detected",
            "Universal menu image detected: itemlist 0x{0:X6}, status 0x{1:X6}, entries {2}.",
            layout.ItemListOffset,
            layout.StatusOffset,
            items.Count));
        LogMenuConfigSummary(config, knownGameStartBuckets, log);
        return config;
    }

    private static bool TryDecodeUniversalImageManifest(
        ReadOnlySpan<byte> payload,
        out UniversalImageManifestLayout? layout)
    {
        layout = null;
        if (payload.Length < UniversalImageManifestSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(payload) != UniversalImageManifestMagic ||
            BinaryPrimitives.ReadUInt16LittleEndian(payload[4..]) != UniversalImageManifestVersion ||
            BinaryPrimitives.ReadUInt16LittleEndian(payload[6..]) != UniversalImageManifestSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]) != UniversalImageManifestFlags)
        {
            return false;
        }

        int itemListOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(payload[12..]));
        int itemListSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(payload[16..]));
        int statusOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(payload[20..]));
        int statusSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(payload[24..]));
        int mappingOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(payload[40..]));
        int mappingSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(payload[44..]));
        int entryCount = BinaryPrimitives.ReadUInt16LittleEndian(payload[48..]);
        int itemConfigSize = BinaryPrimitives.ReadUInt16LittleEndian(payload[38..]);
        uint expectedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(payload[60..]);
        if (itemConfigSize != MenuItemEntrySize || entryCount <= 0 || entryCount > MenuMaxEntries ||
            itemListSize != checked(entryCount * MenuItemEntrySize) ||
            itemListOffset < UniversalImageManifestOffset ||
            statusOffset <= itemListOffset || statusSize is < 0x1000 or > 0x40000 ||
            mappingOffset != WaterWlMappingCodec.Offset || mappingSize != WaterWlMappingCodec.SectorSize ||
            ComputeUniversalImageManifestChecksum(payload) != expectedChecksum)
        {
            return false;
        }

        layout = new UniversalImageManifestLayout(
            itemListOffset,
            itemListSize,
            statusOffset,
            statusSize,
            mappingOffset,
            mappingSize,
            entryCount);
        return true;
    }

    private static uint ComputeUniversalImageManifestChecksum(ReadOnlySpan<byte> payload)
    {
        uint hash = 2166136261;
        for (int index = 0; index < UniversalImageManifestSize; index++)
        {
            byte value = index >= UniversalImageManifestSize - sizeof(uint) ? (byte)0 : payload[index];
            hash ^= value;
            hash *= 16777619;
        }

        return hash;
    }

    public static bool TrySynchronizeUniversalImageManifestEntryCount(
        Span<byte> payload,
        int entryCount,
        out int previousEntryCount)
    {
        previousEntryCount = 0;
        if (entryCount <= 0 || entryCount > MenuMaxEntries ||
            !TryDecodeUniversalImageManifest(payload, out UniversalImageManifestLayout? layout) ||
            layout is null)
        {
            return false;
        }

        previousEntryCount = layout.EntryCount;
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload[16..],
            checked((uint)(entryCount * MenuItemEntrySize)));
        BinaryPrimitives.WriteUInt16LittleEndian(payload[48..], checked((ushort)entryCount));
        BinaryPrimitives.WriteUInt32LittleEndian(payload[60..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload[60..],
            ComputeUniversalImageManifestChecksum(payload));
        return TryDecodeUniversalImageManifest(payload, out _);
    }

    private static void LogUniversalManifestEntryCountMismatch(
        int declaredEntryCount,
        int actualEntryCount,
        Action<string>? log)
    {
        if (declaredEntryCount == actualEntryCount)
        {
            return;
        }

        log?.Invoke(Tf(
            "log",
            "universal_image_entry_count_stale",
            "Universal image manifest entry count is stale: declared {0}, decoded {1}; using the decoded item list.",
            declaredEntryCount,
            actualEntryCount));
    }

    private static WaterMenuConfigInfo ReadFixedWaterLinkMenuConfig(
        WaterDeviceProtocol device,
        HashSet<int> knownGameStartBuckets,
        Action<string>? log,
        int timeoutMs = Timeout.Infinite)
    {
        log?.Invoke(Tf(
            "log",
            "start_parse_waterlink_fixed_menu_config",
            "Start parsing fixed WaterLink menu config: status 0x{0:X6}, itemlist 0x{1:X6}.",
            WaterLinkStatusOffset,
            WaterLinkItemListOffset));

        byte[] statusPayload = new byte[16];
        if (!device.TryReadRom((uint)WaterLinkStatusOffset, statusPayload, timeoutMs))
        {
            log?.Invoke(T(
                "log",
                "waterlink_fixed_status_read_failed",
                "Fixed WaterLink status sector could not be read. Run Repair Magic to rebuild the menu metadata."));
            return new WaterMenuConfigInfo();
        }

        if (!TryDecodeMenuFlashStatus(statusPayload, WaterLinkStatusOffset, WaterWlMappingCodec.SectorSize, out MenuFlashStatusInfo statusInfo))
        {
            log?.Invoke(T(
                "log",
                "waterlink_fixed_status_invalid",
                "Fixed WaterLink status sector is missing or invalid. Run Repair Magic to rebuild the menu metadata."));
            return new WaterMenuConfigInfo();
        }

        if (statusInfo.ItemListAddress != WaterLinkItemListOffset)
        {
            log?.Invoke(Tf(
                "log",
                "waterlink_fixed_itemlist_mismatch",
                "Fixed WaterLink status points to itemlist 0x{0:X6}, expected 0x{1:X6}. Run Repair Magic to rebuild the menu metadata.",
                statusInfo.ItemListAddress,
                WaterLinkItemListOffset));
            return new WaterMenuConfigInfo();
        }

        if (!TryReadMenuItems(device, WaterLinkItemListOffset, out List<MenuItemConfigInfo> items, timeoutMs))
        {
            log?.Invoke(T(
                "log",
                "waterlink_fixed_itemlist_invalid",
                "Fixed WaterLink itemlist sector is missing or invalid. Run Repair Magic to rebuild the menu metadata."));
            return new WaterMenuConfigInfo();
        }

        WaterMenuConfigInfo config = new()
        {
            IsDetected = true,
            Status = statusInfo,
            Items = items,
        };
        LogMenuConfigSummary(config, knownGameStartBuckets, log);
        return config;
    }

    private static WaterMenuConfigInfo ReadFixedWaterLinkMenuConfigFromImage(
        byte[] imageBytes,
        HashSet<int> knownGameStartBuckets,
        Action<string>? log)
    {
        log?.Invoke(Tf(
            "log",
            "start_parse_waterlink_fixed_menu_config",
            "Start parsing fixed WaterLink menu config: status 0x{0:X6}, itemlist 0x{1:X6}.",
            WaterLinkStatusOffset,
            WaterLinkItemListOffset));

        if (!TryReadImageRange(imageBytes, WaterLinkStatusOffset, 16, out byte[] statusPayload))
        {
            log?.Invoke(T(
                "log",
                "waterlink_fixed_status_read_failed",
                "Fixed WaterLink status sector could not be read. Run Repair Magic to rebuild the menu metadata."));
            return new WaterMenuConfigInfo();
        }

        if (!TryDecodeMenuFlashStatus(statusPayload, WaterLinkStatusOffset, WaterWlMappingCodec.SectorSize, out MenuFlashStatusInfo statusInfo))
        {
            log?.Invoke(T(
                "log",
                "waterlink_fixed_status_invalid",
                "Fixed WaterLink status sector is missing or invalid. Run Repair Magic to rebuild the menu metadata."));
            return new WaterMenuConfigInfo();
        }

        if (statusInfo.ItemListAddress != WaterLinkItemListOffset)
        {
            log?.Invoke(Tf(
                "log",
                "waterlink_fixed_itemlist_mismatch",
                "Fixed WaterLink status points to itemlist 0x{0:X6}, expected 0x{1:X6}. Run Repair Magic to rebuild the menu metadata.",
                statusInfo.ItemListAddress,
                WaterLinkItemListOffset));
            return new WaterMenuConfigInfo();
        }

        if (!TryReadMenuItemsFromImage(imageBytes, WaterLinkItemListOffset, out List<MenuItemConfigInfo> items))
        {
            log?.Invoke(T(
                "log",
                "waterlink_fixed_itemlist_invalid",
                "Fixed WaterLink itemlist sector is missing or invalid. Run Repair Magic to rebuild the menu metadata."));
            return new WaterMenuConfigInfo();
        }

        WaterMenuConfigInfo config = new()
        {
            IsDetected = true,
            Status = statusInfo,
            Items = items,
        };
        LogMenuConfigSummary(config, knownGameStartBuckets, log);
        return config;
    }

    public static WaterWlMappingInfo ReadWlMapping(WaterDeviceProtocol device, Action<string>? log, int timeoutMsPerChunk = 3000)
    {
        log?.Invoke(T("log", "start_parse_wl_mapping", "Start parsing WL mapping sector."));

        byte[] mappingSector = new byte[WaterWlMappingCodec.SectorSize];
        string parseError = string.Empty;
        bool sectorRead = device.TryReadRomRange((uint)WaterWlMappingCodec.Offset, mappingSector, 4096, timeoutMsPerChunk);
        if (sectorRead &&
            WaterWlMappingCodec.TryParse(mappingSector, WaterWlMappingCodec.Offset, false, out WaterWlMappingInfo mapping, out parseError))
        {
            LogWlMappingSummary(mapping, log);
            return mapping;
        }

        if (sectorRead &&
            WaterWlMappingCodec.TryReadHeader(mappingSector, out WlMappingHeaderInfo header) &&
            header.Magic == WaterWlMappingCodec.Magic)
        {
            log?.Invoke(Tf(
                "log",
                "wl_mapping_invalid_detail",
                "WL mapping sector failed validation: {0}",
                string.IsNullOrWhiteSpace(parseError)
                    ? T("error", "wl_sector_format_invalid", "WL mapping sector format is invalid.")
                    : parseError));

            return new WaterWlMappingInfo
            {
                SourceAddress = WaterWlMappingCodec.Offset,
                RawSector = mappingSector.ToArray(),
                LegacyVersionDetected = header.Version == WaterWlMappingCodec.LegacyVersion,
                ValidationError = parseError,
                Header = header,
            };
        }

        log?.Invoke(T("log", "wl_mapping_not_detected", "No valid WL mapping sector was recognized."));
        return new WaterWlMappingInfo();
    }

    private static WaterWlMappingInfo ReadWlMappingFromImage(byte[] imageBytes, Action<string>? log)
    {
        log?.Invoke(T("log", "start_parse_wl_mapping", "Start parsing WL mapping sector."));

        string parseError = string.Empty;
        bool sectorRead = TryReadImageRange(imageBytes, WaterWlMappingCodec.Offset, WaterWlMappingCodec.SectorSize, out byte[] mappingSector);
        if (sectorRead &&
            WaterWlMappingCodec.TryParse(mappingSector, WaterWlMappingCodec.Offset, false, out WaterWlMappingInfo mapping, out parseError))
        {
            LogWlMappingSummary(mapping, log);
            return mapping;
        }

        if (sectorRead &&
            WaterWlMappingCodec.TryReadHeader(mappingSector, out WlMappingHeaderInfo header) &&
            header.Magic == WaterWlMappingCodec.Magic)
        {
            log?.Invoke(Tf(
                "log",
                "wl_mapping_invalid_detail",
                "WL mapping sector failed validation: {0}",
                string.IsNullOrWhiteSpace(parseError)
                    ? T("error", "wl_sector_format_invalid", "WL mapping sector format is invalid.")
                    : parseError));

            return new WaterWlMappingInfo
            {
                SourceAddress = WaterWlMappingCodec.Offset,
                RawSector = mappingSector.ToArray(),
                LegacyVersionDetected = header.Version == WaterWlMappingCodec.LegacyVersion,
                ValidationError = parseError,
                Header = header,
            };
        }

        log?.Invoke(T("log", "wl_mapping_not_detected", "No valid WL mapping sector was recognized."));
        return new WaterWlMappingInfo();
    }

    private static bool TryReadMenuItems(
        WaterDeviceProtocol device,
        int itemListAddress,
        out List<MenuItemConfigInfo> items,
        int timeoutMs = Timeout.Infinite)
    {
        items = [];

        for (int entryBaseIndex = 0; entryBaseIndex < MenuMaxEntries; entryBaseIndex += MenuEntriesPerChunk)
        {
            int chunkEntryCount = Math.Min(MenuEntriesPerChunk, MenuMaxEntries - entryBaseIndex);
            byte[] chunk = new byte[chunkEntryCount * MenuItemEntrySize];

            if (!device.TryReadRom((uint)(itemListAddress + entryBaseIndex * MenuItemEntrySize), chunk, timeoutMs))
            {
                return false;
            }

            for (int i = 0; i < chunkEntryCount; i++)
            {
                if (!TryDecodeMenuItemConfig(
                    chunk,
                    i * MenuItemEntrySize,
                    itemListAddress + entryBaseIndex * MenuItemEntrySize,
                    entryBaseIndex + i,
                    out MenuItemConfigInfo itemInfo))
                {
                    if (items.Count > 0)
                    {
                        return true;
                    }

                    return IsAllBytes(chunk.AsSpan(i * MenuItemEntrySize), 0xFF);
                }

                items.Add(itemInfo);
            }
        }

        return true;
    }

    private static bool TryReadMenuItemsFromImage(
        byte[] imageBytes,
        int itemListAddress,
        out List<MenuItemConfigInfo> items)
    {
        items = [];

        for (int entryBaseIndex = 0; entryBaseIndex < MenuMaxEntries; entryBaseIndex += MenuEntriesPerChunk)
        {
            int chunkEntryCount = Math.Min(MenuEntriesPerChunk, MenuMaxEntries - entryBaseIndex);
            int chunkOffset = itemListAddress + (entryBaseIndex * MenuItemEntrySize);
            int chunkLength = chunkEntryCount * MenuItemEntrySize;

            if (!TryReadImageRange(imageBytes, chunkOffset, chunkLength, out byte[] chunk))
            {
                return false;
            }

            for (int i = 0; i < chunkEntryCount; i++)
            {
                if (!TryDecodeMenuItemConfig(
                    chunk,
                    i * MenuItemEntrySize,
                    itemListAddress + entryBaseIndex * MenuItemEntrySize,
                    entryBaseIndex + i,
                    out MenuItemConfigInfo itemInfo))
                {
                    if (items.Count > 0)
                    {
                        return true;
                    }

                    return IsAllBytes(chunk.AsSpan(i * MenuItemEntrySize), 0xFF);
                }

                items.Add(itemInfo);
            }
        }

        return true;
    }

    private static bool TrySwitchLegacyBeggarBucket(WaterDeviceProtocol device, int bucket, int timeoutMs)
    {
        if (bucket < 0)
        {
            return false;
        }

        int high = ((bucket / 8) & 0x0F) << 4;
        int low = 0x40 | ((bucket % 8) << 3);

        return device.TryWriteRam(2, [(byte)high], timeoutMs) &&
               device.TryWriteRam(3, [(byte)low], timeoutMs);
    }

    private static bool TryDecodeMenuFlashStatus(byte[] payload, int statusAddress, int sectorSize, out MenuFlashStatusInfo statusInfo)
    {
        statusInfo = new MenuFlashStatusInfo();

        if (payload.Length < 16)
        {
            return false;
        }

        if (BitConverter.ToUInt32(payload, 0) != 0x414D554B)
        {
            return false;
        }

        statusInfo.StatusAddress = statusAddress;
        statusInfo.SectorSize = sectorSize;
        statusInfo.Version = payload[4];
        statusInfo.BatteryPresent = payload[5] != 0;
        statusInfo.LastBootMenuIndex = BitConverter.ToUInt16(payload, 6);
        statusInfo.LastBootSaveIndex = payload[8];
        statusInfo.LastBootSaveType = payload[9];
        statusInfo.SramBankType = payload[10];
        return true;
    }

    private static bool TryDecodeMenuItemConfig(byte[] payload, int offset, int baseAddress, int index, out MenuItemConfigInfo itemInfo)
    {
        itemInfo = new MenuItemConfigInfo();

        if (payload.Length < offset + MenuItemEntrySize)
        {
            return false;
        }

        byte titleLength = payload[offset + 1];
        ushort hiddenKeysMask = BitConverter.ToUInt16(payload, offset + 8);

        if (titleLength == 0 || titleLength == 0xFF || titleLength > 0x30)
        {
            return false;
        }

        if ((hiddenKeysMask & ~0x03FF) != 0)
        {
            return false;
        }

        int titleByteCount = Math.Min(titleLength * 2, 0x60);
        string title = Encoding.Unicode.GetString(payload, offset + 0x10, titleByteCount)
            .TrimEnd('\0', '\uFFFF', '\uFFFE');
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        itemInfo.Index = index;
        itemInfo.EntryAddress = baseAddress + offset;
        itemInfo.Font = payload[offset];
        itemInfo.TitleLength = titleLength;
        itemInfo.RomOffsetBlocks = BitConverter.ToUInt16(payload, offset + 2);
        itemInfo.RomSizeBlocks = BitConverter.ToUInt16(payload, offset + 4);
        itemInfo.SaveType = payload[offset + 6];
        itemInfo.SaveIndex = payload[offset + 7];
        itemInfo.HiddenKeysMask = hiddenKeysMask;
        itemInfo.CoverIndex = payload[offset + 10];
        itemInfo.CoverFlags = payload[offset + 11];
        itemInfo.Title = title;
        return true;
    }

    private static List<WaterCartEntry> BuildEntries(
        IReadOnlyList<DetectedNorGameInfo> games,
        WaterMenuConfigInfo menuConfig,
        WaterWlMappingInfo wlMapping,
        Action<string>? log)
    {
        List<WaterCartEntry> entries = wlMapping.IsDetected && menuConfig.IsDetected
            ? BuildEntriesFromWlMapping(games, menuConfig, wlMapping, log)
            : BuildEntriesFromMenuAndHeaders(games, menuConfig, log);

        log?.Invoke(Tf("log", "merge_complete", "Merge complete. Output {0} display record(s).", entries.Count));
        return entries;
    }

    private static List<WaterCartEntry> BuildEntriesFromWlMapping(
        IReadOnlyList<DetectedNorGameInfo> games,
        WaterMenuConfigInfo menuConfig,
        WaterWlMappingInfo wlMapping,
        Action<string>? log)
    {
        Dictionary<int, DetectedNorGameInfo> headerByStartBucket = games
            .GroupBy(game => game.StartBucket)
            .ToDictionary(group => group.Key, group => group.First());

        Dictionary<int, MenuItemConfigInfo> menuItemByIndex = menuConfig.Items
            .GroupBy(item => item.Index)
            .ToDictionary(group => group.Key, group => group.First());
        Dictionary<int, WlRomMapRecordInfo> recordBySlot = wlMapping.Records
            .GroupBy(record => record.EntrySlot)
            .ToDictionary(group => group.Key, group => group.First());

        List<WaterCartEntry> entries = [];
        foreach (MenuItemConfigInfo menuItem in menuConfig.Items.OrderBy(item => item.Index))
        {
            DetectedNorGameInfo? headerGame = null;
            if (menuItemByIndex.TryGetValue(menuItem.Index, out MenuItemConfigInfo? actualMenuItem) &&
                actualMenuItem.StartBucket > 0)
            {
                headerByStartBucket.TryGetValue(actualMenuItem.StartBucket, out headerGame);
            }

            recordBySlot.TryGetValue(menuItem.Index, out WlRomMapRecordInfo? record);
            if (record is not null && !MenuItemMatchesRecord(menuItem, record))
            {
                record = null;
            }

            entries.Add(new WaterCartEntry
            {
                HeaderGame = headerGame,
                MenuItem = menuItem,
                WlRecord = record,
            });
        }

        return entries
            .OrderBy(entry => entry.SortKey)
            .ThenBy(entry => entry.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<WaterCartEntry> BuildEntriesFromMenuAndHeaders(
        IReadOnlyList<DetectedNorGameInfo> games,
        WaterMenuConfigInfo menuConfig,
        Action<string>? log)
    {
        Dictionary<int, DetectedNorGameInfo> headerByStartBucket = games
            .GroupBy(game => game.StartBucket)
            .ToDictionary(group => group.Key, group => group.First());

        List<WaterCartEntry> entries = [];

        if (menuConfig.IsDetected)
        {
            foreach (MenuItemConfigInfo item in menuConfig.Items.OrderBy(item => item.StartBucket).ThenBy(item => item.Index))
            {
                headerByStartBucket.TryGetValue(item.StartBucket, out DetectedNorGameInfo? headerGame);

                entries.Add(new WaterCartEntry
                {
                    HeaderGame = headerGame,
                    MenuItem = item,
                });
            }

            return entries
                .OrderBy(entry => entry.SortKey)
                .ThenBy(entry => entry.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return [];
    }

    private static void LogMenuConfigSummary(WaterMenuConfigInfo menuConfig, HashSet<int> knownGameStartBuckets, Action<string>? log)
    {
        if (log is null || !menuConfig.IsDetected)
        {
            return;
        }

        MenuFlashStatusInfo status = menuConfig.Status;
        log(Tf(
            "log",
            "menu_config_summary",
            "Menu config: status 0x{0:X6} | itemlist 0x{1:X6} | sector {2} KiB | battery mode: {3}",
            status.StatusAddress,
            status.ItemListAddress,
            status.SectorSize / 1024,
            status.BatteryPresent
                ? T("value", "state_on", "On")
                : T("value", "state_off", "Off")));
        log(Tf(
            "log",
            "menu_config_item_count",
            "Menu entries: {0} | start 4MiB buckets: {1}",
            menuConfig.ItemCount,
            FormatBucketList(menuConfig.StartBuckets)));

        foreach (MenuItemConfigInfo item in menuConfig.Items.OrderBy(item => item.StartBucket).ThenBy(item => item.Index))
        {
            string matchText = knownGameStartBuckets.Contains(item.StartBucket)
                ? T("value", "nor_header_hit", "NOR header matched")
                : T("value", "nor_header_miss", "NOR header missing");

            log(Tf(
                "log",
                "menu_config_item",
                "#{0:000} {1} | 4MiB {2} | blocks {3}/{4} | save={5} | {6}",
                item.Index + 1,
                ShortenLogText(item.Title, 36),
                item.BucketRangeText,
                item.RomOffsetBlocks,
                item.RomSizeBlocks,
                item.SaveTypeText,
                matchText));
        }
    }

    private static void LogWlMappingSummary(WaterWlMappingInfo wlMapping, Action<string>? log)
    {
        if (log is null || !wlMapping.IsDetected)
        {
            return;
        }

        log(Tf(
            "log",
            "wl_mapping_summary",
            "WL mapping: 0x{0:X6} | records {1} | v{2} | {3}",
            wlMapping.SourceAddress,
            wlMapping.RecordCount,
            wlMapping.Header.Version,
            wlMapping.HasChecksum
                ? T("value", "wl_checksum_crc32_ok", "CRC32 OK")
                : T("value", "wl_checksum_legacy_none", "legacy/no checksum")));

        foreach (WlRomMapRecordInfo record in wlMapping.Records.Take(24))
        {
            log(Tf(
                "log",
                "wl_mapping_record",
                "WL#{0:000} | logical blocks {1} | physical blocks {2}",
                record.EntrySlot + 1,
                record.LogicalBucketCount,
                record.PhysicalBucketText));
        }

        if (wlMapping.RecordCount > 24)
        {
            log(Tf(
                "log",
                "wl_mapping_more_records",
                "The remaining {0} WL mapping records were omitted.",
                wlMapping.RecordCount - 24));
        }
    }

    private static WaterWlMappingInfo CloneWlMapping(WaterWlMappingInfo source)
    {
        return new WaterWlMappingInfo
        {
            IsDetected = source.IsDetected,
            UsingBackupCopy = source.UsingBackupCopy,
            SourceAddress = source.SourceAddress,
            LegacyVersionDetected = source.LegacyVersionDetected,
            ValidationError = source.ValidationError,
            HasChecksum = source.HasChecksum,
            IsChecksumValid = source.IsChecksumValid,
            ComputedChecksum = source.ComputedChecksum,
            Header = new WlMappingHeaderInfo
            {
                Magic = source.Header.Magic,
                Version = source.Header.Version,
                HeaderSize = source.Header.HeaderSize,
                Flags = source.Header.Flags,
                BucketOwnerOffset = source.Header.BucketOwnerOffset,
                BucketOwnerEntrySize = source.Header.BucketOwnerEntrySize,
                BucketOwnerCount = source.Header.BucketOwnerCount,
                RomMapRecordOffset = source.Header.RomMapRecordOffset,
                RomMapRecordSize = source.Header.RomMapRecordSize,
                RomMapRecordCount = source.Header.RomMapRecordCount,
                MappingChecksum = source.Header.MappingChecksum,
            },
            BucketOwners = source.BucketOwners.ToArray(),
            Records = source.Records
                .Select(record => new WlRomMapRecordInfo
                {
                    EntrySlot = record.EntrySlot,
                    LogicalBucketCount = record.LogicalBucketCount,
                    PhysicalBuckets = record.PhysicalBuckets.ToList(),
                })
                .ToList(),
            RawSector = source.RawSector.ToArray(),
        };
    }

    private static List<DetectedNorGameInfo> CloneGames(IEnumerable<DetectedNorGameInfo> games)
    {
        return games
            .Select(game => new DetectedNorGameInfo
            {
                StartBucket = game.StartBucket,
                EndBucket = game.EndBucket,
                Title = game.Title,
                GameCode = game.GameCode,
                IsMenu = game.IsMenu,
            })
            .ToList();
    }

    private static WaterMenuConfigInfo CloneMenuConfig(WaterMenuConfigInfo source)
    {
        return new WaterMenuConfigInfo
        {
            IsDetected = source.IsDetected,
            Status = new MenuFlashStatusInfo
            {
                StatusAddress = source.Status.StatusAddress,
                SectorSize = source.Status.SectorSize,
                ExplicitItemListAddress = source.Status.ExplicitItemListAddress,
                ExplicitSaveAreaAddress = source.Status.ExplicitSaveAreaAddress,
                Version = source.Status.Version,
                BatteryPresent = source.Status.BatteryPresent,
                LastBootMenuIndex = source.Status.LastBootMenuIndex,
                LastBootSaveIndex = source.Status.LastBootSaveIndex,
                LastBootSaveType = source.Status.LastBootSaveType,
                SramBankType = source.Status.SramBankType,
            },
            Items = source.Items
                .Select(WaterMenuConfigCodec.Clone)
                .ToList(),
        };
    }

    private static void LogMagicMetadataSyncSummary(WaterMagicMetadataSyncInfo syncInfo, Action<string>? log)
    {
        if (log is null || !syncInfo.IsApplicable || !syncInfo.NeedsRepair)
        {
            return;
        }

        log(Tf(
            "log",
            "magic_metadata_sync_warning",
            "Magic metadata is out of sync. itemlist={0}, mapping={1}, itemlist-only slots={2}, mapping-only slots={3}, mismatched slots={4}, non-sequential mapping slots={5}",
            syncInfo.MenuItemCount,
            syncInfo.MappingRecordCount,
            FormatSlotList(syncInfo.MenuOnlySlots),
            FormatSlotList(syncInfo.MappingOnlySlots),
            FormatSlotList(syncInfo.SlotRangeMismatchSlots),
            FormatSlotList(syncInfo.NonSequentialRecordSlots)));
    }

    private static bool IdMatches(byte[] id, params byte[] expected)
    {
        if (id.Length != expected.Length)
        {
            return false;
        }

        for (int i = 0; i < expected.Length; i++)
        {
            if (id[i] != expected[i])
            {
                return false;
            }
        }

        return true;
    }

    private static bool LooksLikeNoCartridgeNorId(byte[] norId)
    {
        return norId.Length > 0 && norId.All(value => value == 0xFF);
    }

    private static WaterCartScanResult CreateNoCartridgeResult(byte[] norId)
    {
        string rawNorIdHex = BitConverter.ToString(norId).Replace("-", " ");
        return new WaterCartScanResult
        {
            NoCartridgeDetected = true,
            IsChipRecognized = false,
            NorId = norId.ToArray(),
            RawNorIdHex = rawNorIdHex,
            ChipName = T("value", "chip_unknown", "Unknown / Needs confirmation"),
        };
    }

    private static string GetCapacityUnavailableReason()
    {
        return T(
            "error",
            "scan_capacity_unavailable",
            "A valid NOR capacity was not obtained. Read the cartridge again before writing.");
    }

    private static bool TryGbcReadFlashId(WaterDeviceProtocol device, Action<string>? log, out byte[] id)
    {
        id = new byte[4];

        if (!TryWriteGbcProbeStep(device, 0x0AAA, [0xAA], "write 0xAA -> 0x0AAA", log) ||
            !TryWriteGbcProbeStep(device, 0x0555, [0x55], "write 0x55 -> 0x0555", log) ||
            !TryWriteGbcProbeStep(device, 0x0AAA, [0x90], "write 0x90 -> 0x0AAA", log))
        {
            return false;
        }

        try
        {
            return TryReadSingleGbcByte(device, 0x0000, out id[0], log) &&
                   TryReadSingleGbcByte(device, 0x0002, out id[1], log) &&
                   TryReadSingleGbcByte(device, 0x001C, out id[2], log) &&
                   TryReadSingleGbcByte(device, 0x001E, out id[3], log);
        }
        finally
        {
            device.TryWriteGbc(0x0000, [0xF0], GbcProbeTimeoutMs);
        }
    }

    private static bool TryReadSingleGbcByte(WaterDeviceProtocol device, uint address, out byte value, Action<string>? log = null)
    {
        byte[] buffer = new byte[1];
        value = 0;
        if (!TryReadGbcProbeStep(device, address, buffer, $"read 0x{address:X4}", log))
        {
            return false;
        }

        value = buffer[0];
        return true;
    }

    private static NorGeometryInfo? TryReadGbcNorGeometry(WaterDeviceProtocol device, Action<string>? log)
    {
        if (!TryWriteGbcProbeStep(device, 0x00AA, [0x98], "write 0x98 -> 0x00AA", log))
        {
            return null;
        }

        try
        {
            byte[] cfiBuffer = new byte[NorCfiReadWindowBytes];
            if (!TryReadGbcProbeStep(device, 0x0000, cfiBuffer, "read CFI 0x0000..0x00FF", log))
            {
                return null;
            }

            if (cfiBuffer[0x20] != (byte)'Q' ||
                cfiBuffer[0x22] != (byte)'R' ||
                cfiBuffer[0x24] != (byte)'Y')
            {
                return null;
            }

            int deviceExponent = cfiBuffer[0x4E];
            if (deviceExponent <= 0 || deviceExponent >= 31)
            {
                return null;
            }

            int deviceSizeBytes = (int)Math.Pow(2, deviceExponent);

            int bufferWriteExponent = (cfiBuffer[0x56] << 8) | cfiBuffer[0x54];
            int bufferWriteBytes = bufferWriteExponent <= 1
                ? 0
                : (int)Math.Pow(2, bufferWriteExponent);

            int sectorRegionCount = cfiBuffer[0x58];
            int sectorCount = 0;
            int sectorSizeBytes = 0;
            int largestSectorSizeBytes = 0;

            for (int i = 0; i < Math.Min(4, sectorRegionCount); i++)
            {
                int sectorCountInRegion = ((cfiBuffer[0x5C + (i * 8)] << 8) | cfiBuffer[0x5A + (i * 8)]) + 1;
                int sectorSizeInRegion = ((cfiBuffer[0x60 + (i * 8)] << 8) | cfiBuffer[0x5E + (i * 8)]) * 256;

                if (sectorCountInRegion <= 0 || sectorSizeInRegion <= 0)
                {
                    continue;
                }

                sectorCount += sectorCountInRegion;
                sectorSizeBytes = sectorSizeBytes == 0
                    ? sectorSizeInRegion
                    : Math.Min(sectorSizeBytes, sectorSizeInRegion);
                largestSectorSizeBytes = Math.Max(largestSectorSizeBytes, sectorSizeInRegion);
            }

            if (sectorSizeBytes <= 0)
            {
                sectorSizeBytes = 128 * 1024;
            }

            if (sectorCount <= 0)
            {
                sectorCount = Math.Max(1, deviceSizeBytes / sectorSizeBytes);
            }

            ushort primaryCommandSetId = (ushort)ReadCfiLaneU16(cfiBuffer, 0x26);
            string commandSetName = primaryCommandSetId switch
            {
                0x0001 or 0x0003 => "Intel",
                0x0002 or 0x0004 => "AMD",
                _ => "Unknown",
            };

            log?.Invoke(Tf(
                "log",
                "beggar_max_probe_cfi",
                "GBC/MAX probe detected CFI QRY. Capacity={0}, BuffWr={1}.",
                FormatMiBSize(deviceSizeBytes),
                bufferWriteBytes <= 0 ? "0 B" : $"{bufferWriteBytes} B"));

            return new NorGeometryInfo
            {
                DeviceSizeBytes = deviceSizeBytes,
                SectorSizeBytes = sectorSizeBytes,
                SectorCount = sectorCount,
                BufferWriteBytes = bufferWriteBytes,
                LargestSectorSizeBytes = largestSectorSizeBytes,
                RegionCount = Math.Min(4, sectorRegionCount),
                PrimaryCommandSetId = primaryCommandSetId,
                CommandSetName = commandSetName,
                IsS70Gl02GsDualDie = false,
            };
        }
        finally
        {
            device.TryWriteGbc(0x0000, [0xF0], GbcProbeTimeoutMs);
        }
    }

    private static bool TryWriteGbcProbeStep(
        WaterDeviceProtocol device,
        uint address,
        byte[] bytes,
        string step,
        Action<string>? log = null)
    {
        bool ok = device.TryWriteGbc(address, bytes, GbcProbeTimeoutMs);
        log?.Invoke(ok
            ? Tf("log", "beggar_max_probe_step", "GBC/MAX {0}: {1}", step, "OK")
            : Tf("log", "beggar_max_probe_step_detail", "GBC/MAX {0}: FAILED ({1})", step, device.DescribeLastAckFailure()));
        return ok;
    }

    private static bool TryReadGbcProbeStep(
        WaterDeviceProtocol device,
        uint address,
        byte[] buffer,
        string step,
        Action<string>? log = null)
    {
        bool ok = device.TryReadGbc(address, buffer, GbcProbeTimeoutMs);
        log?.Invoke(Tf("log", "beggar_max_probe_step", "GBC/MAX {0}: {1}", step, ok ? "OK" : "FAILED"));
        return ok;
    }

    private static NorGeometryInfo CreateFallbackBeggarMaxGeometry(byte[] id)
    {
        int deviceSizeBytes = (id.Length >= 2 && id[0] == 0xC2 && (id[1] == 0xCB || id[1] == 0xC9 || id[1] == 0x7E))
            ? 8 * 1024 * 1024
            : id.Length >= 3
                ? id[2] switch
                {
                    0x21 => 16 * 1024 * 1024,
                    0x22 => 32 * 1024 * 1024,
                    0x23 => 64 * 1024 * 1024,
                    0x28 => 128 * 1024 * 1024,
                    0x48 => 256 * 1024 * 1024,
                    _ => 0,
                }
                : 0;

        const int sectorSizeBytes = 128 * 1024;
        return new NorGeometryInfo
        {
            DeviceSizeBytes = deviceSizeBytes,
            SectorSizeBytes = sectorSizeBytes,
            SectorCount = Math.Max(1, deviceSizeBytes / sectorSizeBytes),
            BufferWriteBytes = 0,
            LargestSectorSizeBytes = sectorSizeBytes,
            RegionCount = 1,
            IsS70Gl02GsDualDie = id.Length >= 3 && id[2] == 0x48,
        };
    }

    private static NorGeometryInfo CreateFallbackLegacyBeggarGeometry(byte[] norId)
    {
        int deviceSizeBytes = TryGetKnownNorDeviceSizeBytes(norId, out int inferredDeviceSize)
            ? inferredDeviceSize
            : 0;
        const int sectorSizeBytes = 128 * 1024;

        return new NorGeometryInfo
        {
            DeviceSizeBytes = deviceSizeBytes,
            SectorSizeBytes = sectorSizeBytes,
            SectorCount = Math.Max(1, DivCeil(deviceSizeBytes, sectorSizeBytes)),
            BufferWriteBytes = TryGetKnownWaterDirectBufferWriteBytes(norId, out int bufferWriteBytes)
                ? bufferWriteBytes
                : 0,
            LargestSectorSizeBytes = sectorSizeBytes,
            RegionCount = 1,
            CommandSetName = "Unknown",
            IsS70Gl02GsDualDie = IsS70Gl02GsDualDieNorId(norId),
        };
    }

    private static bool LooksLikeSupportedBeggarMaxId(byte[] id)
    {
        if (id.Length < 4)
        {
            return false;
        }

        if (id.All(value => value == 0x00 || value == 0xFF))
        {
            return false;
        }

        return (id[0] == 0x01 && (id[1] == 0x7E || id[1] == 0x01)) ||
               (id[0] == 0x89 && id[1] == 0x7E) ||
               (id[0] == 0xC2 && (id[1] == 0xCB || id[1] == 0xC9 || id[1] == 0x7E)) ||
               (id[0] == 0x20 && id[1] == 0x7E);
    }

    private static byte[] ExpandBeggarMaxIdToLegacyNorId(byte[] id)
    {
        if (id.Length < 4)
        {
            return [];
        }

        if (id[0] == 0xC2 && (id[1] == 0xCB || id[1] == 0xC9))
        {
            return [id[0], id[1], id[2], id[3], 0x00, 0x00, 0x00, 0x00];
        }

        return [id[0], 0x00, id[1], 0x22, id[2], 0x22, 0x01, id[3]];
    }

    private static bool LooksLikeMenuTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        string normalized = title.Trim().ToUpperInvariant();
        return normalized.Contains("MULTIMENU") || normalized.Contains("MENU");
    }

    private static bool HasNintendoLogo(byte[] header)
    {
        if (header.Length < 160)
        {
            return false;
        }

        return header
            .Skip(4)
            .Take(NintendoLogoGba.Length)
            .SequenceEqual(NintendoLogoGba);
    }

    internal static bool HasValidGbaHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < 0xC0 ||
            !header.Slice(4, NintendoLogoGba.Length).SequenceEqual(NintendoLogoGba) ||
            header[0xB2] != 0x96)
        {
            return false;
        }

        int checksum = 0;
        for (int i = 0xA0; i < 0xBD; i++)
        {
            checksum -= header[i];
        }

        return header[0xBD] == (byte)((checksum - 0x19) & 0xFF);
    }

    private static string FormatBucketList(IEnumerable<int> values)
    {
        string text = WaterCartEntry.FormatBucketList(values);
        return string.IsNullOrWhiteSpace(text)
            ? T("value", "none", "None")
            : text;
    }

    private static bool MenuItemMatchesRecord(MenuItemConfigInfo item, WlRomMapRecordInfo record)
    {
        if (record.PhysicalBuckets.Count <= 0)
        {
            return false;
        }

        return item.StartBucket == record.PhysicalBuckets[0] &&
               item.CoveredBucketCount == record.LogicalBucketCount;
    }

    private static bool IsAllBytes(ReadOnlySpan<byte> buffer, byte value)
    {
        foreach (byte item in buffer)
        {
            if (item != value)
            {
                return false;
            }
        }

        return true;
    }

    private static string FormatSlotList(IEnumerable<int> slots)
    {
        List<int> slotList = slots
            .Distinct()
            .OrderBy(slot => slot)
            .ToList();

        return slotList.Count == 0
            ? T("value", "none", "None")
            : string.Join(", ", slotList.Select(slot => (slot + 1).ToString("000")));
    }

    private static string ShortenLogText(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text) || maxLength <= 0)
        {
            return string.Empty;
        }

        text = text.Trim();
        if (text.Length <= maxLength)
        {
            return text;
        }

        if (maxLength <= 3)
        {
            return text[..maxLength];
        }

        return text[..(maxLength - 3)] + "...";
    }

    private static string BuildCapacitySummary(NorGeometryInfo geometry)
    {
        return Tf(
            "log",
            "geometry_summary",
            "Capacity:{0} Sectors:{1} SectorSize:{2} BuffWr:{3} CmdSet:{4} Regions:{5}",
            FormatMiBSize(geometry.DeviceSizeBytes),
            geometry.SectorCount,
            FormatKiBSize(geometry.SectorSizeBytes),
            FormatBufferWriteSize(geometry.BufferWriteBytes),
            geometry.CommandSetName,
            geometry.RegionCount);
    }

    private static string BuildSectorSummary(NorGeometryInfo geometry)
    {
        return Tf(
            "ui",
            "sector_summary_value",
            "Sectors {0} x {1} | BuffWr {2} | {3}",
            geometry.SectorCount,
            FormatKiBSize(geometry.SectorSizeBytes),
            FormatBufferWriteSize(geometry.BufferWriteBytes),
            geometry.CommandSetName);
    }

    private static string FormatMiBSize(long bytes)
    {
        return FormatBinarySize(bytes / 1024.0 / 1024.0, "MiB");
    }

    private static string FormatKiBSize(long bytes)
    {
        return FormatBinarySize(bytes / 1024.0, "KiB");
    }

    private static string FormatBufferWriteSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        return FormatBinarySize(bytes / 1024.0, "KiB");
    }

    private static string FormatHexBytes(byte[] bytes, int maxLength)
    {
        if (bytes.Length == 0 || maxLength <= 0)
        {
            return string.Empty;
        }

        int length = Math.Min(bytes.Length, maxLength);
        return BitConverter.ToString(bytes, 0, length).Replace("-", " ");
    }

    private static string FormatBinarySize(double value, string unit)
    {
        if (Math.Abs(value - Math.Round(value)) < 0.0005)
        {
            return $"{Math.Round(value):0} {unit}";
        }

        return $"{value:0.###} {unit}";
    }

    private static string T(string section, string key, string fallback) =>
        LocalizedText.T(section, key, fallback);

    private static string Tf(string section, string key, string fallback, params object[] args) =>
        LocalizedText.Tf(section, key, fallback, args);

    private static NorGeometryInfo GuessOfflineGeometry(int imageLengthBytes)
    {
        int deviceSizeBytes = AlignUp(Math.Max(imageLengthBytes, BucketSizeBytes), BucketSizeBytes);
        int sectorSizeBytes = 0x20000;

        return new NorGeometryInfo
        {
            DeviceSizeBytes = deviceSizeBytes,
            SectorSizeBytes = sectorSizeBytes,
            SectorCount = DivCeil(deviceSizeBytes, sectorSizeBytes),
            BufferWriteBytes = 0,
            LargestSectorSizeBytes = sectorSizeBytes,
            RegionCount = 1,
            IsS70Gl02GsDualDie = false,
        };
    }

    private static bool TryReadImageRange(byte[] imageBytes, int offset, int length, out byte[] data)
    {
        data = [];

        if (offset < 0 || length < 0 || offset > imageBytes.Length || imageBytes.Length - offset < length)
        {
            return false;
        }

        data = new byte[length];
        Buffer.BlockCopy(imageBytes, offset, data, 0, length);
        return true;
    }

    private static int AlignUp(int value, int alignment)
    {
        if (alignment <= 0)
        {
            return value;
        }

        int remainder = value % alignment;
        return remainder == 0
            ? value
            : checked(value + (alignment - remainder));
    }

    private static int DivCeil(int value, int divisor) =>
        divisor <= 0
            ? 0
            : (value / divisor) + ((value % divisor) != 0 ? 1 : 0);
}
