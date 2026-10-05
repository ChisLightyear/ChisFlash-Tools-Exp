using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using GbaMultiMenuBuilder.Models;
using GbaMultiMenuBuilder.Fc;

namespace GbaMultiMenuBuilder.Services;

public sealed class CompilationBuilder
{
    private const int MaxConcurrentBuildPreparationWorkerCount = 8;
    private const int FixedSystemReservedBytes = 4 * 1024 * 1024;
    private const int GbaCoverTrailerOffset = 0x0AFFF0;
    private const int GbaMinimalMenuPackageSize = 0x0B0000;
    private const int GbaMenuMaxEntries = 512;
    private const int GbcFixedMapReservedBytes = 1 * 1024 * 1024;
    private const int GbcMenuRegionBytes = 128 * 1024;
    private const int GbcConfigOffsetBytes = 0x020000;
    private const int GbcBackgroundHeaderOffsetBytes = 0x020090;
    private const int GbcBackgroundHeaderSize = 32;
    private const int GbcRomDirectoryOffsetBytes = 0x040000;
    private const int GbcBackgroundTileDataOffsetBytes = 0x0BF000;
    private const int GbcBackgroundTilemapOffsetBytes = 0x0BFC00;
    private const int GbcBackgroundAttributesOffsetBytes = 0x0BFD80;
    private const int GbcBackgroundPalettesOffsetBytes = 0x0BFF00;
    private const int GbcFontOffsetBytes = 0x0C0000;
    private const int GbcConfigHeaderSize = 64;
    private const int GbcRomDirectoryHeaderSize = 64;
    private const int GbcRomEntrySize = 128;
    private const int GbcRomNameOffset = 14;
    private const int GbcRomNameCodeCount = 48;
    private const int GbcRomNameAsciiLength = 16;
    private const int GbcRomSramMapOffset = 110;
    private const int GbcFontHeaderSize = 64;
    private const byte GbcMenuFontSizePx = 12;
    private const int GbcChineseMenuGamesPerPage = 8;
    private const int GbcEnglishMenuGamesPerPage = 16;
    private const string GbcFontSignature = "MBCXFNT";
    private const string GbcBackgroundSignature = "MBCXBG1";
    private const int GbcMaxGames = 99;
    private const int GbcMaxGameSramBanks = 8;
    private const int GbcFixedSharedSramBanks = 4;
    private const int GbcSharedSramBankStart = 60;
    private const int GbcIndependentSramBankCapacity = 60;
    private const int GbcAddressableBankCount = 0x0800;
    private const int GbcAddressableTotalBytes = GbcAddressableBankCount * GameBoyRomHeaderService.RomBankSize;
    private static readonly byte[] BootLogoSha1 = Convert.FromHexString("17DAA0FEC02FC33C0F6ABB549A8B80B6613B48EE");
    private readonly SramPatchService _sramPatchService = new();
    private readonly BatterylessPatchService _batterylessPatchService = new();

    public BuildLayoutEstimate AnalyzeLayoutQuick(
        BuildRequest request,
        Func<string, string, string, string>? textResolver = null)
    {
        if(request.Family==CompilationFamily.Fc || request.FlashCartMode==FlashCartMode.FcMbcx)
        {
            var plan=FcMbcxCodec.Plan(PrepareFcGames(request,CancellationToken.None),request.Cartridge.FlashSize,false);
            int capacity=Math.Min(request.Cartridge.FlashSize,FcMbcxCodec.CapacityBytes);
            return new(plan.EndBytes,capacity-plan.EndBytes,capacity,plan.Entries.Count,0,FcMbcxCodec.PrefixBytes,plan.EndBytes-FcMbcxCodec.PrefixBytes,capacity-FcMbcxCodec.PrefixBytes);
        }
        if (request.MinimalMenuPackage)
        {
            ValidateMinimalMenuPackageRequest(request, textResolver);
            return new BuildLayoutEstimate(
                GbaMinimalMenuPackageSize,
                Math.Max(0, request.Cartridge.FlashSize - GbaMinimalMenuPackageSize),
                request.Cartridge.FlashSize,
                request.Games.Count(static game => game.Enabled),
                0,
                GbaMinimalMenuPackageSize,
                0,
                Math.Max(0, request.Cartridge.FlashSize - GbaMinimalMenuPackageSize));
        }

        if (IsGbcMaxBuild(request))
        {
            return AnalyzeGbcLayout(request, textResolver);
        }

        if (string.IsNullOrWhiteSpace(request.BaseMenuPath) || !File.Exists(request.BaseMenuPath))
        {
            throw new InvalidOperationException(T(textResolver, "error", "base_menu_not_found", "Base menu ROM was not found."));
        }

        ValidateFixedSaveSlots(request, textResolver);
        var menuRom = PrepareMenuRom(request.BaseMenuPath);
        var cart = request.Cartridge;
        var preparedGames = new List<PreparedGame>();
        var inputGames = request.Games.Where(static game => game.Enabled).ToList();

        foreach (var game in inputGames)
        {
            var prepared = PrepareGameQuick(
                game,
                preparedGames.Count,
                request.MinRomSize,
                cart.SectorSize,
                request.FlashCartMode,
                request.BatteryPresent,
                textResolver);

            preparedGames.Add(prepared);
        }

        return AnalyzePreparedGamesLayout(request, menuRom.Length, preparedGames);
    }

    public long EstimateGamePayloadBytesQuick(
        BuildGameEntry game,
        BuildRequest request,
        Func<string, string, string, string>? textResolver = null)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(request);

        if(request.Family==CompilationFamily.Fc || request.FlashCartMode==FlashCartMode.FcMbcx)
            return FcMbcxCodec.Align(checked((int)new FileInfo(game.RomPath).Length),FcMbcxCodec.RomBankBytes);
        if (request.MinimalMenuPackage)
        {
            return 0;
        }

        if (IsGbcMaxBuild(request))
        {
            return EstimateGbcGamePayloadBytes(game, request, textResolver);
        }

        var prepared = PrepareGameQuick(
            game,
            0,
            request.MinRomSize,
            request.Cartridge.SectorSize,
            request.FlashCartMode,
            request.BatteryPresent,
            textResolver);
        return prepared.Size;
    }

    private byte[] BuildGbcFixedMapRegion(
        BuildRequest request,
        IReadOnlyList<PreparedGbcGame> includedGames,
        int usedRomBytes,
        Func<string, string, string, string>? textResolver = null)
    {
        if (!IsGbcMaxBuild(request))
        {
            throw new InvalidOperationException(
                T(textResolver, "error", "gbc_font_export_only_for_gbc", "Font package export is only available for GBC MAX/MBCX builds."));
        }

        if (string.IsNullOrWhiteSpace(request.BaseMenuPath) || !File.Exists(request.BaseMenuPath))
        {
            throw new InvalidOperationException(T(textResolver, "error", "base_menu_not_found", "Base menu ROM was not found."));
        }

        if (includedGames is null)
        {
            throw new ArgumentNullException(nameof(includedGames));
        }

        var useChineseMenu = IsGbcChineseMenuBuild(request);
        if (useChineseMenu && includedGames.Count > 0 && string.IsNullOrWhiteSpace(GbcFontService.FindFontPath(request.GbcFontPath)))
        {
            throw new InvalidOperationException(
                T(
                    textResolver,
                    "error",
                    "gbc_layer_font_required",
                    "A valid 12px GBC font is required for layered MBCX menu generation. 8px fonts are not supported."));
        }
        if (usedRomBytes <= 0)
        {
            throw new InvalidOperationException("Used ROM size must be positive for GBC fixed-map generation.");
        }

        var menuRom = File.ReadAllBytes(request.BaseMenuPath);
        if (menuRom.Length > GbcMenuRegionBytes)
        {
            throw new InvalidOperationException(
                Tf(
                    textResolver,
                    "error",
                    "gbc_menu_too_large",
                    "The MAX/MBCX base menu ROM exceeds the reserved 128 KiB region: {0}",
                    Path.GetFileName(request.BaseMenuPath)));
        }

        var fixedRegion = GC.AllocateUninitializedArray<byte>(GbcFixedMapReservedBytes);
        Array.Fill(fixedRegion, (byte)0xFF);
        Buffer.BlockCopy(menuRom, 0, fixedRegion, 0, menuRom.Length);

        var configHeader = BuildGbcConfigHeader(request, includedGames.Count);
        var directoryHeader = BuildGbcDirectoryHeader(includedGames.Count);
        var fontPackage = useChineseMenu && includedGames.Count > 0
            ? BuildGbcFontPackage(includedGames, request.GbcFontPath)
            : null;
        var entryOffset = GbcRomDirectoryOffsetBytes + GbcRomDirectoryHeaderSize;
        Buffer.BlockCopy(configHeader, 0, fixedRegion, GbcConfigOffsetBytes, configHeader.Length);
        Buffer.BlockCopy(directoryHeader, 0, fixedRegion, GbcRomDirectoryOffsetBytes, directoryHeader.Length);

        for (var i = 0; i < includedGames.Count; i++)
        {
            var entryBytes = BuildGbcDirectoryEntry(includedGames[i], useChineseMenu);
            Buffer.BlockCopy(entryBytes, 0, fixedRegion, entryOffset + (i * GbcRomEntrySize), entryBytes.Length);
        }

        if (fontPackage is not null && fontPackage.Bytes.Length > GbcFixedMapReservedBytes - GbcFontOffsetBytes)
        {
            throw new InvalidOperationException("Generated GBC font package exceeded the reserved font area.");
        }

        if (fontPackage is not null)
        {
            Buffer.BlockCopy(fontPackage.Bytes, 0, fixedRegion, GbcFontOffsetBytes, fontPackage.Bytes.Length);
        }

        if (useChineseMenu && includedGames.Count > 0)
        {
            var layerFontPath = ResolveRequiredGbcMenuLayerFontPath(request.GbcFontPath);
            WriteGbcMenuLayer(fixedRegion, includedGames, layerFontPath);
            EnsureGbcMenuLayerHeader(fixedRegion);
        }
        WriteGbcBackground(fixedRegion, request.BackgroundPath, textResolver);
        PatchGbcCartridgeHeader(fixedRegion, usedRomBytes);

        return fixedRegion;
    }

    private byte[] UpdateExistingGbcFixedMapRegion(
        byte[] existingFixedRegion,
        BuildRequest request,
        IReadOnlyList<PreparedGbcGame> includedGames,
        int usedRomBytes,
        bool preserveMenuRuntime,
        Func<string, string, string, string>? textResolver = null)
    {
        if (!IsGbcMaxBuild(request))
        {
            throw new InvalidOperationException(
                T(textResolver, "error", "gbc_font_export_only_for_gbc", "Font package export is only available for GBC MAX/MBCX builds."));
        }

        if (includedGames is null)
        {
            throw new ArgumentNullException(nameof(includedGames));
        }

        if (usedRomBytes <= 0)
        {
            throw new InvalidOperationException("Used ROM size must be positive for GBC fixed-map generation.");
        }

        if (existingFixedRegion.Length < GbcFixedMapReservedBytes)
        {
            throw new InvalidOperationException("The existing GBC fixed-map region is incomplete.");
        }

        var fixedRegion = GC.AllocateUninitializedArray<byte>(GbcFixedMapReservedBytes);
        Buffer.BlockCopy(existingFixedRegion, 0, fixedRegion, 0, GbcFixedMapReservedBytes);

        if (!preserveMenuRuntime)
        {
            if (string.IsNullOrWhiteSpace(request.BaseMenuPath) || !File.Exists(request.BaseMenuPath))
            {
                throw new InvalidOperationException(T(textResolver, "error", "base_menu_not_found", "Base menu ROM was not found."));
            }

            var menuRom = File.ReadAllBytes(request.BaseMenuPath);
            if (menuRom.Length > GbcMenuRegionBytes)
            {
                throw new InvalidOperationException(
                    Tf(
                        textResolver,
                        "error",
                        "gbc_menu_too_large",
                        "The MAX/MBCX base menu ROM exceeds the reserved 128 KiB region: {0}",
                        Path.GetFileName(request.BaseMenuPath)));
            }

            fixedRegion.AsSpan(0, GbcMenuRegionBytes).Fill(0xFF);
            Buffer.BlockCopy(menuRom, 0, fixedRegion, 0, menuRom.Length);
        }

        fixedRegion.AsSpan(GbcConfigOffsetBytes, GbcFixedMapReservedBytes - GbcConfigOffsetBytes).Fill(0xFF);

        var useChineseMenu = IsGbcChineseMenuBuild(request);
        var configHeader = BuildGbcConfigHeader(request, includedGames.Count);
        var directoryHeader = BuildGbcDirectoryHeader(includedGames.Count);
        var fontPackage = useChineseMenu && includedGames.Count > 0
            ? BuildGbcFontPackage(includedGames, request.GbcFontPath)
            : null;
        var entryOffset = GbcRomDirectoryOffsetBytes + GbcRomDirectoryHeaderSize;
        Buffer.BlockCopy(configHeader, 0, fixedRegion, GbcConfigOffsetBytes, configHeader.Length);
        Buffer.BlockCopy(directoryHeader, 0, fixedRegion, GbcRomDirectoryOffsetBytes, directoryHeader.Length);

        for (var i = 0; i < includedGames.Count; i++)
        {
            var entryBytes = BuildGbcDirectoryEntry(includedGames[i], useChineseMenu);
            Buffer.BlockCopy(entryBytes, 0, fixedRegion, entryOffset + (i * GbcRomEntrySize), entryBytes.Length);
        }

        if (fontPackage is not null && fontPackage.Bytes.Length > GbcFixedMapReservedBytes - GbcFontOffsetBytes)
        {
            throw new InvalidOperationException("Generated GBC font package exceeded the reserved font area.");
        }

        if (fontPackage is not null)
        {
            Buffer.BlockCopy(fontPackage.Bytes, 0, fixedRegion, GbcFontOffsetBytes, fontPackage.Bytes.Length);
        }

        if (useChineseMenu && includedGames.Count > 0)
        {
            var layerFontPath = ResolveRequiredGbcMenuLayerFontPath(request.GbcFontPath);
            WriteGbcMenuLayer(fixedRegion, includedGames, layerFontPath);
            EnsureGbcMenuLayerHeader(fixedRegion);
        }
        WriteGbcBackground(fixedRegion, request.BackgroundPath, textResolver);
        PatchGbcCartridgeHeader(fixedRegion, usedRomBytes);

        return fixedRegion;
    }

    public sealed record GbcPreparedEntry(
        int Index,
        int RomIndex,
        string Title,
        string HeaderTitle,
        string RomPath,
        long FileLength,
        long AllocatedBytes,
        int AllocatedRomBanks,
        int RamBankCount,
        byte MbcType,
        bool HasBattery,
        int RomOffsetBank,
        byte[] SramBankMap);

    public static GbcPreparedEntry CreateGbcPreparedEntry(
        int index,
        int romIndex,
        string title,
        string headerTitle,
        string romPath,
        long fileLength,
        long allocatedBytes,
        int allocatedRomBanks,
        int ramBankCount,
        byte mbcType,
        bool hasBattery,
        int romOffsetBank,
        byte[] sramBankMap)
    {
        ArgumentNullException.ThrowIfNull(romPath);
        ArgumentNullException.ThrowIfNull(sramBankMap);

        return new GbcPreparedEntry(
            index,
            romIndex,
            title,
            headerTitle,
            romPath,
            fileLength,
            allocatedBytes,
            allocatedRomBanks,
            ramBankCount,
            mbcType,
            hasBattery,
            romOffsetBank,
            sramBankMap.ToArray());
    }

    public byte[] BuildGbcFixedMapRegion(
        BuildRequest request,
        IReadOnlyList<GbcPreparedEntry> includedGames,
        int usedRomBytes,
        Func<string, string, string, string>? textResolver = null)
    {
        ArgumentNullException.ThrowIfNull(includedGames);

        List<PreparedGbcGame> normalizedGames = includedGames
            .Select(game => new PreparedGbcGame(
                game.Index,
                game.RomIndex,
                game.Title,
                game.HeaderTitle,
                game.RomPath,
                game.FileLength,
                game.AllocatedBytes,
                game.AllocatedRomBanks,
                game.RamBankCount,
                game.MbcType,
                game.HasBattery,
                InferGbcUsesDedicatedSaveAllocation(game.SramBankMap, game.RamBankCount),
                game.RomOffsetBank,
                game.SramBankMap.ToArray()))
            .ToList();

        return BuildGbcFixedMapRegion(request, normalizedGames, usedRomBytes, textResolver);
    }

    public byte[] UpdateExistingGbcFixedMapRegion(
        byte[] existingFixedRegion,
        BuildRequest request,
        IReadOnlyList<GbcPreparedEntry> includedGames,
        int usedRomBytes,
        bool preserveMenuRuntime = true,
        Func<string, string, string, string>? textResolver = null)
    {
        ArgumentNullException.ThrowIfNull(existingFixedRegion);

        var normalizedGames = includedGames
            .Select(game => new PreparedGbcGame(
                game.Index,
                game.RomIndex,
                game.Title,
                game.HeaderTitle,
                game.RomPath,
                game.FileLength,
                game.AllocatedBytes,
                game.AllocatedRomBanks,
                game.RamBankCount,
                game.MbcType,
                game.HasBattery,
                InferGbcUsesDedicatedSaveAllocation(game.SramBankMap, game.RamBankCount),
                game.RomOffsetBank,
                game.SramBankMap.ToArray()))
            .ToList();

        return UpdateExistingGbcFixedMapRegion(
            existingFixedRegion,
            request,
            normalizedGames,
            usedRomBytes,
            preserveMenuRuntime,
            textResolver);
    }

    public BuildLayoutEstimate AnalyzeLayout(
        BuildRequest request,
        Func<string, string, string, string>? textResolver = null)
    {
        if (IsGbcMaxBuild(request))
        {
            return AnalyzeGbcLayout(request, textResolver);
        }

        if (string.IsNullOrWhiteSpace(request.BaseMenuPath) || !File.Exists(request.BaseMenuPath))
        {
            throw new InvalidOperationException(T(textResolver, "error", "base_menu_not_found", "Base menu ROM was not found."));
        }

        ValidateFixedSaveSlots(request, textResolver);
        var menuRom = PrepareMenuRom(request.BaseMenuPath);
        var cart = request.Cartridge;
        var preparedGames = new List<PreparedGame>();
        var inputGames = request.Games.Where(static game => game.Enabled).ToList();

        foreach (var game in inputGames)
        {
            var prepared = PrepareGame(
                game,
                preparedGames.Count,
                request.Cartridge,
                request.MinRomSize,
                cart.SectorSize,
                request.FlashCartMode,
                request.BatteryPresent,
                ignoreSramPatchFailure: true,
                textResolver);

            preparedGames.Add(prepared);
        }

        return AnalyzePreparedGamesLayout(request, menuRom.Length, preparedGames);
    }

    public ConfigPreviewReport CreateConfigurationPreview(
        BuildRequest request,
        Func<string, string, string, string>? textResolver = null) =>
        IsGbcMaxBuild(request)
            ? CreateGbcConfigurationPreview(request, textResolver)
            : CreateGbaConfigurationPreview(request, textResolver);

    public ConfigPreviewSection ExportGbcFontPackage(
        BuildRequest request,
        Func<string, string, string, string>? textResolver = null)
    {
        if (!IsGbcChineseMenuBuild(request))
        {
            throw new InvalidOperationException(
                T(textResolver, "error", "gbc_font_export_chinese_only", "Font package export is only needed for Chinese GBC menu builds."));
        }

        if (!IsGbcMaxBuild(request))
        {
            throw new InvalidOperationException(
                T(textResolver, "error", "gbc_font_export_only_for_gbc", "Font package export is only available for GBC MAX/MBCX builds."));
        }

        ValidateGbcRequest(request, textResolver);
        var inputGames = request.Games.Where(static game => game.Enabled).ToList();
        var preparedGames = PrepareGbcGames(inputGames, request.MinRomSize, textResolver);
        var includedGames = new List<PreparedGbcGame>(preparedGames.Length);
        long currentOffset = GbcFixedMapReservedBytes;
        var accessibleFlashSize = GetGbcAccessibleFlashSize(request.Cartridge);

        for (var i = 0; i < preparedGames.Length; i++)
        {
            var preparedGame = preparedGames[i];
            if (currentOffset + preparedGame.AllocatedBytes > accessibleFlashSize)
            {
                continue;
            }

            includedGames.Add(preparedGame with
            {
                RomIndex = includedGames.Count,
                RomOffsetBank = (int)(currentOffset / GameBoyRomHeaderService.RomBankSize),
            });

            currentOffset += preparedGame.AllocatedBytes;
        }

        var fontPackage = BuildGbcFontPackage(includedGames, request.GbcFontPath);
        return new ConfigPreviewSection(
            "MBCXFNT Package",
            GbcFontOffsetBytes,
            fontPackage.Bytes,
            BuildGbcFontPackageDetails(fontPackage));
    }

    public string ExportGbcFontCharacterList(
        BuildRequest request,
        Func<string, string, string, string>? textResolver = null)
    {
        if (!IsGbcChineseMenuBuild(request))
        {
            throw new InvalidOperationException(
                T(textResolver, "error", "gbc_font_export_chinese_only", "Font package export is only needed for Chinese GBC menu builds."));
        }

        if (!IsGbcMaxBuild(request))
        {
            throw new InvalidOperationException(
                T(textResolver, "error", "gbc_font_export_only_for_gbc", "Font package export is only available for GBC MAX/MBCX builds."));
        }

        ValidateGbcRequest(request, textResolver);
        var inputGames = request.Games.Where(static game => game.Enabled).ToList();
        var preparedGames = PrepareGbcGames(inputGames, request.MinRomSize, textResolver);
        var includedGames = new List<PreparedGbcGame>(preparedGames.Length);
        long currentOffset = GbcFixedMapReservedBytes;
        var accessibleFlashSize = GetGbcAccessibleFlashSize(request.Cartridge);

        for (var i = 0; i < preparedGames.Length; i++)
        {
            var preparedGame = preparedGames[i];
            if (currentOffset + preparedGame.AllocatedBytes > accessibleFlashSize)
            {
                continue;
            }

            includedGames.Add(preparedGame with
            {
                RomIndex = includedGames.Count,
                RomOffsetBank = (int)(currentOffset / GameBoyRomHeaderService.RomBankSize),
            });

            currentOffset += preparedGame.AllocatedBytes;
        }

        var fontPackage = BuildGbcFontPackage(includedGames, request.GbcFontPath);
        return BuildGbcFontCharacterList(fontPackage);
    }

    public MenuPreviewModel CreateMenuPreview(
        BuildRequest request,
        Func<string, string, string, string>? textResolver = null) =>
        IsGbcMaxBuild(request)
            ? CreateGbcMenuPreview(request, textResolver)
            : CreateGbaMenuPreview(request, textResolver);

    private static BuildLayoutEstimate AnalyzePreparedGamesLayout(
        BuildRequest request,
        int menuRomLength,
        IReadOnlyList<PreparedGame> preparedGames)
    {
        var cart = request.Cartridge;
        var sectorCount = cart.FlashSize / cart.SectorSize;
        var sectorMap = Enumerable.Repeat('.', sectorCount).ToList();

        var itemListOffsetBytes = AlignToNextBlock(menuRomLength, 0x40000);
        var itemListOffset = DivCeil(itemListOffsetBytes, cart.SectorSize);
        var statusOffset = itemListOffset + 1;
        var saveDataSectorOffset = request.FlashCartMode.IsChm1Mode() ? Chm1Layout.SaveAreaOffset / cart.SectorSize : GetSaveDataStartSectorOffset(statusOffset);
        var systemReservedSectorCount = GetSystemReservedSectorCount(cart, statusOffset + 1);
        UpdateSectorMap(sectorMap, 0, systemReservedSectorCount, 'm');
        UpdateSectorMap(sectorMap, itemListOffset, 1, 'l');
        UpdateSectorMap(sectorMap, statusOffset, 1, 'c');

        if (request.BatteryPresent)
        {
            foreach (var prepared in preparedGames)
            {
                if (!prepared.SaveSlotIndex.HasValue)
                {
                    continue;
                }

                var offset = saveDataSectorOffset + prepared.SaveSlotIndex.Value;
                UpdateSectorMap(sectorMap, offset, 1, 's');
            }
        }

        var placementOrder = preparedGames.OrderByDescending(static game => game.Size).ToList();
        foreach (var game in placementOrder)
        {
            ReserveGamePlacementForEstimate(game, systemReservedSectorCount, sectorMap, cart);
        }

        var usedBytes = GetOutputRomSize(sectorMap, cart.SectorSize);
        var reservedSystemBytes = systemReservedSectorCount * cart.SectorSize;
        var payloadCapacityBytes = Math.Max(0, cart.FlashSize - reservedSystemBytes);
        var usedPayloadBytes = Math.Max(0, usedBytes - reservedSystemBytes);
        return new BuildLayoutEstimate(
            usedBytes,
            Math.Max(0, cart.FlashSize - usedBytes),
            cart.FlashSize,
            preparedGames.Count,
            0,
            reservedSystemBytes,
            usedPayloadBytes,
            payloadCapacityBytes);
    }

    private MenuPreviewModel CreateGbaMenuPreview(
        BuildRequest request,
        Func<string, string, string, string>? textResolver)
    {
        ValidateRequest(request, textResolver);
        (byte[] BitmapData, byte[] PaletteData)? backgroundData = null;
        if (!IsCoverMenu(request.GbaMenuType))
        {
            var menuRom = File.ReadAllBytes(request.BaseMenuPath);
            backgroundData = ReadGbaMenuBackground(
                menuRom,
                request.FlashCartMode is FlashCartMode.WaterlineVirtualIron or FlashCartMode.Chm1 or FlashCartMode.Chm1NoBattery,
                textResolver);
            if (!string.IsNullOrWhiteSpace(request.BackgroundPath))
            {
                try
                {
                    backgroundData = BackgroundImageEncoder.Encode(request.BackgroundPath!);
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("240x160", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(T(
                        textResolver,
                        "error",
                        "background_invalid_size",
                        "Background image must be exactly 240x160."));
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("240 colors", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(T(
                        textResolver,
                        "error",
                        "background_too_many_colors",
                        "Background image uses more than 240 colors."));
                }
            }
        }

        var inputGames = request.Games.Where(static game => game.Enabled).ToList();
        var preparedGames = PrepareGamesForBuild(inputGames, request, null, CancellationToken.None, textResolver);
        var keyOrder = new List<ushort> { 0 };
        foreach (var prepared in preparedGames)
        {
            if (prepared.HiddenKeysMask != 0 && !keyOrder.Contains(prepared.HiddenKeysMask))
            {
                keyOrder.Add(prepared.HiddenKeysMask);
            }
        }

        var orderedGames = new List<PreparedGame>(preparedGames.Length);
        foreach (var key in keyOrder)
        {
            orderedGames.AddRange(preparedGames.Where(game => game.HiddenKeysMask == key));
        }

        var entries = new List<MenuPreviewEntry>(orderedGames.Count);
        for (var i = 0; i < orderedGames.Count; i++)
        {
            var game = orderedGames[i];
            entries.Add(new MenuPreviewEntry(
                game.Index,
                PrepareMenuTitle(game.Title),
                PrepareMenuTitle(game.Title),
                true,
                true,
                i == 0,
                i / 16,
                i % 16,
                BuildGbaPreviewSecondaryInfo(game),
                game.SaveSlotIndex.HasValue,
                false,
                null,
                null,
                8,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty));
        }

        var summary = new StringBuilder();
        summary.AppendLine("Menu Style: GBA");
        summary.AppendLine($"Entries: {entries.Count}");
        summary.AppendLine($"Selected: {(entries.Count == 0 ? "none" : entries[0].DisplayTitle)}");
        summary.AppendLine("Preview simulates current title order and save-slot metadata.");

        return new MenuPreviewModel(
            "GBA Menu Preview",
            summary.ToString().TrimEnd(),
            CompilationFamily.Gba,
            AppConfigService.LoadLanguageCode() ?? LanguageService.DefaultLanguageCode,
            8,
            16,
            0,
            entries,
            backgroundData?.BitmapData,
            backgroundData?.PaletteData);
    }

    private static (byte[] BitmapData, byte[] PaletteData) ReadGbaMenuBackground(
        byte[] menuRom,
        bool usesWlLayout,
        Func<string, string, string, string>? textResolver)
    {
        const int bitmapLength = GbaMenuBackgroundLocator.BitmapBytes;
        const int paletteLength = GbaMenuBackgroundLocator.PaletteBytes;

        if (!GbaMenuBackgroundLocator.TryFind(menuRom, out int backgroundOffset))
        {
            throw new InvalidOperationException(T(
                textResolver,
                "error",
                "background_marker_not_found",
                "Could not locate background data inside the base menu ROM."));
        }

        return (
            menuRom.AsSpan(backgroundOffset, bitmapLength).ToArray(),
            menuRom.AsSpan(backgroundOffset + bitmapLength, paletteLength).ToArray());
    }

    private MenuPreviewModel CreateGbcMenuPreview(
        BuildRequest request,
        Func<string, string, string, string>? textResolver)
    {
        ValidateGbcRequest(request, textResolver);
        var languageCode = GetGbcBuildLanguageCode(request);
        var useChineseMenu = IsGbcChineseMenuBuild(request);
        var gamesPerPage = GetGbcMenuGamesPerPage(languageCode);
        var inputGames = request.Games.Where(static game => game.Enabled).ToList();
        var preparedGames = PrepareGbcGames(inputGames, request.MinRomSize, textResolver);
        var accessibleFlashSize = GetGbcAccessibleFlashSize(request.Cartridge);
        var includedGames = new List<PreparedGbcGame>(preparedGames.Length);
        long currentOffset = GbcFixedMapReservedBytes;
        var currentBank = GbcFixedMapReservedBytes / GameBoyRomHeaderService.RomBankSize;
        for (var i = 0; i < preparedGames.Length; i++)
        {
            var preparedGame = preparedGames[i];
            if (currentOffset + preparedGame.AllocatedBytes > accessibleFlashSize)
            {
                continue;
            }

            includedGames.Add(preparedGame with
            {
                RomIndex = includedGames.Count,
                RomOffsetBank = currentBank,
            });

            currentOffset += preparedGame.AllocatedBytes;
            currentBank += preparedGame.AllocatedRomBanks;
        }

        GbcFontPackageInfo? fontPackage = null;
        IReadOnlyDictionary<ushort, byte[]>? glyphMap = useChineseMenu
            ? null
            : GbcFontService.GetGbdkDefaultAsciiGlyphs();
        var fontSizePx = useChineseMenu ? GbcMenuFontSizePx : (byte)8;
        if (useChineseMenu)
        {
            fontPackage = BuildGbcFontPackage(includedGames, request.GbcFontPath);
            glyphMap = BuildGbcPreviewGlyphMap(fontPackage, request.GbcFontPath);
            fontSizePx = (byte)fontPackage.Format.GlyphSizePx;
        }

        var entries = new List<MenuPreviewEntry>(includedGames.Count);
        for (var i = 0; i < includedGames.Count; i++)
        {
            var game = includedGames[i];
            var encodedNameCodes = useChineseMenu ? EncodeGbcEntryName(game.Title, game.HeaderTitle) : null;
            entries.Add(new MenuPreviewEntry(
                game.Index,
                ResolveGbcPreviewDisplayTitle(game, languageCode),
                game.HeaderTitle,
                true,
                true,
                i == 0,
                i / gamesPerPage,
                i % gamesPerPage,
                BuildGbcPreviewSecondaryInfo(game),
                game.UsesDedicatedSaveAllocation && game.RamBankCount > 0,
                game.RamBankCount > 0,
                encodedNameCodes,
                glyphMap,
                fontSizePx,
                GbcMenuLayerBuilder.FormatPreviewDetailTypeText(game.MbcType),
                GbcMenuLayerBuilder.FormatPreviewDetailRomText(game.AllocatedRomBanks),
                GbcMenuLayerBuilder.FormatPreviewDetailRamText(game.RamBankCount),
                GbcMenuLayerBuilder.FormatPreviewDetailSaveText(game.UsesDedicatedSaveAllocation, game.RamBankCount),
                game.RomOffsetBank.ToString(CultureInfo.InvariantCulture)));
        }

        var summary = new StringBuilder();
        summary.AppendLine("Menu Style: GBC (MAX/MBCX)");
        summary.AppendLine($"Language: {languageCode}");
        summary.AppendLine($"Font Size: {fontSizePx}px");
        summary.AppendLine($"Games Per Page: {gamesPerPage}");
        summary.AppendLine($"Entries: {entries.Count}");
        if (fontPackage is not null)
        {
            summary.AppendLine($"Font Glyphs: {fontPackage.OrderedCodepoints.Length}");
            summary.AppendLine($"Font Source: {fontPackage.SourcePath}");
        }
        else
        {
            summary.AppendLine("Font Package: not used by the English menu");
        }

        return new MenuPreviewModel(
            "GBC Menu Preview",
            summary.ToString().TrimEnd(),
            CompilationFamily.Gbc,
            languageCode,
            fontSizePx,
            gamesPerPage,
            0,
            entries);
    }

    private ConfigPreviewReport CreateGbaConfigurationPreview(
        BuildRequest request,
        Func<string, string, string, string>? textResolver)
    {
        ValidateRequest(request, textResolver);
        var menuRom = PrepareMenuRom(request.BaseMenuPath);
        var cart = request.Cartridge;
        var sectorCount = cart.FlashSize / cart.SectorSize;
        var sectorMap = Enumerable.Repeat('.', sectorCount).ToArray();
        var itemListOffsetBytes = AlignToNextBlock(menuRom.Length, 0x40000);
        var itemListOffset = DivCeil(itemListOffsetBytes, cart.SectorSize);
        var statusOffset = itemListOffset + 1;
        var saveDataSectorOffset = request.FlashCartMode.IsChm1Mode() ? Chm1Layout.SaveAreaOffset / cart.SectorSize : GetSaveDataStartSectorOffset(statusOffset);
        var systemReservedSectorCount = GetSystemReservedSectorCount(cart, statusOffset + 1);
        UpdateSectorMap(sectorMap, 0, systemReservedSectorCount, 'm');
        UpdateSectorMap(sectorMap, itemListOffset, 1, 'l');
        UpdateSectorMap(sectorMap, statusOffset, 1, 'c');

        var status = new byte[16];
        status[0] = (byte)'K';
        status[1] = (byte)'U';
        status[2] = (byte)'M';
        status[3] = (byte)'A';
        if (request.BatteryPresent)
        {
            status[5] = 0x01;
        }

        var keyOrder = new List<ushort> { 0 };
        var inputGames = request.Games.Where(static game => game.Enabled).ToList();
        var preparedGames = PrepareGamesForBuild(inputGames, request, null, CancellationToken.None, textResolver);

        foreach (var prepared in preparedGames)
        {
            if (prepared.HiddenKeysMask != 0 && !keyOrder.Contains(prepared.HiddenKeysMask))
            {
                keyOrder.Add(prepared.HiddenKeysMask);
            }

            if (request.BatteryPresent && prepared.SaveSlotIndex.HasValue)
            {
                var offset = saveDataSectorOffset + prepared.SaveSlotIndex.Value;
                UpdateSectorMap(sectorMap, offset, 1, 's');
            }
        }

        var skippedTitles = new List<string>();
        var placementOrder = preparedGames.OrderByDescending(static game => game.Size).ToList();
        foreach (var game in placementOrder)
        {
            if (!TryReserveGamePlacement(game, systemReservedSectorCount, sectorMap, cart))
            {
                skippedTitles.Add(game.Title);
            }
        }

        var includedGames = preparedGames
            .Where(static game => game.SectorOffset >= 0)
            .OrderBy(static game => game.Index)
            .ToList();
        var itemList = BuildItemList(includedGames, keyOrder);
        var romCode = ComputeRomCode(status, itemList);
        var outputSize = GetOutputRomSize(sectorMap, cart.SectorSize);

        var summary = new StringBuilder();
        summary.AppendLine("Family: GBA");
        summary.AppendLine($"Base Menu: {request.BaseMenuPath}");
        summary.AppendLine($"Base Menu Size (prepared): 0x{menuRom.Length:X} ({menuRom.Length} bytes)");
        summary.AppendLine($"Cartridge: {cart.UiDisplayName} (flash=0x{cart.FlashSize:X}, sector=0x{cart.SectorSize:X}, block=0x{cart.BlockSize:X})");
        summary.AppendLine($"Battery Present: {request.BatteryPresent}");
        summary.AppendLine($"Enabled ROMs: {inputGames.Count}");
        summary.AppendLine($"Included ROMs: {includedGames.Count}");
        summary.AppendLine($"Skipped ROMs: {skippedTitles.Count}");
        summary.AppendLine($"ROM Code: {romCode}");
        summary.AppendLine($"Item List Offset: sector {itemListOffset}, absolute 0x{itemListOffset * cart.SectorSize:X8}");
        summary.AppendLine($"Status Offset: sector {statusOffset}, absolute 0x{statusOffset * cart.SectorSize:X8}");
        summary.AppendLine($"Save Data Start: sector {saveDataSectorOffset}, absolute 0x{saveDataSectorOffset * cart.SectorSize:X8}");
        summary.AppendLine($"System Reserved Sectors: {systemReservedSectorCount}");
        summary.AppendLine($"Projected Output Size: 0x{outputSize:X} ({outputSize} bytes)");
        if (skippedTitles.Count > 0)
        {
            summary.AppendLine($"Skipped Titles: {string.Join(", ", skippedTitles)}");
        }

        summary.AppendLine("Sector Map:");
        summary.AppendLine(FormatWrappedMap(new string(sectorMap), 64));

        return new ConfigPreviewReport(
            "GBA Config Area",
            summary.ToString().TrimEnd(),
            [
                new ConfigPreviewSection(
                    "Status",
                    statusOffset * cart.SectorSize,
                    status,
                    BuildGbaStatusDetails(statusOffset, cart, request.BatteryPresent)),
                new ConfigPreviewSection(
                    "Item List",
                    itemListOffset * cart.SectorSize,
                    itemList,
                    BuildGbaItemListDetails(includedGames, keyOrder, itemListOffset * cart.SectorSize)),
            ]);
    }

    private ConfigPreviewReport CreateGbcConfigurationPreview(
        BuildRequest request,
        Func<string, string, string, string>? textResolver)
    {
        ValidateGbcRequest(request, textResolver);
        var menuRomLength = new FileInfo(request.BaseMenuPath).Length;
        var accessibleFlashSize = GetGbcAccessibleFlashSize(request.Cartridge);
        var inputGames = request.Games.Where(static game => game.Enabled).ToList();
        var preparedGames = PrepareGbcGames(inputGames, request.MinRomSize, textResolver);

        var includedGames = new List<PreparedGbcGame>(preparedGames.Length);
        var skippedTitles = new List<string>();
        long currentOffset = GbcFixedMapReservedBytes;
        var currentBank = GbcFixedMapReservedBytes / GameBoyRomHeaderService.RomBankSize;

        for (var i = 0; i < preparedGames.Length; i++)
        {
            var preparedGame = preparedGames[i];
            if (currentOffset + preparedGame.AllocatedBytes > accessibleFlashSize)
            {
                skippedTitles.Add(preparedGame.Title);
                continue;
            }

            includedGames.Add(preparedGame with
            {
                RomIndex = includedGames.Count,
                RomOffsetBank = currentBank,
            });

            currentOffset += preparedGame.AllocatedBytes;
            currentBank += preparedGame.AllocatedRomBanks;
        }

        var configHeader = BuildGbcConfigHeader(request, includedGames.Count);
        var directoryHeader = BuildGbcDirectoryHeader(includedGames.Count);
        var useChineseMenu = IsGbcChineseMenuBuild(request);
        var fontPackage = useChineseMenu
            ? BuildGbcFontPackage(includedGames, request.GbcFontPath)
            : null;
        var layeredMenu = useChineseMenu
            ? BuildGbcMenuLayerForPreview(includedGames, request.GbcFontPath)
            : null;
        var entryBytes = GC.AllocateUninitializedArray<byte>(includedGames.Count * GbcRomEntrySize);
        for (var i = 0; i < includedGames.Count; i++)
        {
            var buffer = BuildGbcDirectoryEntry(includedGames[i], useChineseMenu);
            Buffer.BlockCopy(buffer, 0, entryBytes, i * GbcRomEntrySize, buffer.Length);
        }

        var romCode = ComputeGbcRomCode(includedGames);
        var summary = new StringBuilder();
        summary.AppendLine("Family: GBC (MAX/MBCX)");
        summary.AppendLine($"Base Menu: {request.BaseMenuPath}");
        summary.AppendLine($"Base Menu Size: 0x{menuRomLength:X} ({menuRomLength} bytes)");
        summary.AppendLine($"Cartridge: {request.Cartridge.UiDisplayName} (flash=0x{request.Cartridge.FlashSize:X}, accessible=0x{accessibleFlashSize:X})");
        summary.AppendLine($"Battery Present: {request.BatteryPresent}");
        summary.AppendLine($"Enabled ROMs: {inputGames.Count}");
        summary.AppendLine($"Included ROMs: {includedGames.Count}");
        summary.AppendLine($"Skipped ROMs: {skippedTitles.Count}");
        summary.AppendLine($"ROM Code: {romCode}");
        summary.AppendLine($"Config Header: 0x{GbcConfigOffsetBytes:X6} ({GbcConfigHeaderSize} bytes)");
        summary.AppendLine($"Directory Header: 0x{GbcRomDirectoryOffsetBytes:X6} ({GbcRomDirectoryHeaderSize} bytes)");
        summary.AppendLine($"Directory Entries: 0x{GbcRomDirectoryOffsetBytes + GbcRomDirectoryHeaderSize:X6} ({includedGames.Count} x {GbcRomEntrySize} bytes)");
        summary.AppendLine($"Game Payload Start: 0x{GbcFixedMapReservedBytes:X6}");
        if (fontPackage is not null && layeredMenu is not null)
        {
            summary.AppendLine($"Font Offset: 0x{GbcFontOffsetBytes:X6}");
            summary.AppendLine($"Font Package Source: {fontPackage.SourcePath}");
            summary.AppendLine($"Font Package Size: 0x{fontPackage.Bytes.Length:X} ({fontPackage.Bytes.Length} bytes)");
            summary.AppendLine($"Font Glyph Count: {fontPackage.OrderedCodepoints.Length}");
            summary.AppendLine($"Layered Menu Header: 0x{GbcConfigOffsetBytes + GbcMenuLayerBuilder.MenuLayerHeaderOffset:X6}");
            summary.AppendLine($"Layered List Strings: 0x{GbcConfigOffsetBytes + GbcMenuLayerBuilder.MenuListStringsOffset:X6} ({layeredMenu.ListStrings.Length} bytes)");
            summary.AppendLine($"Layered Dynamic Base: 0x{GbcConfigOffsetBytes + layeredMenu.DynamicOffsets["list_tile_counts_offset"]:X6}");
            summary.AppendLine($"Layered Dynamic End: 0x{GbcConfigOffsetBytes + layeredMenu.DynamicOffsets["title_scroll_data_offset"] + layeredMenu.TitleScrollData.Length:X6}");
            summary.AppendLine($"Layered Page Count: {layeredMenu.PageCount}");
            if (fontPackage.MissingCodepoints.Length > 0)
            {
                summary.AppendLine($"Missing Font Glyphs: {FormatCodepointList(fontPackage.MissingCodepoints)}");
            }
        }
        else
        {
            summary.AppendLine($"English Menu Entries Per Page: {GbcEnglishMenuGamesPerPage}");
            summary.AppendLine("Font Package: not generated for English GBC menu builds");
            summary.AppendLine("Layered Menu: not generated for English GBC menu builds");
        }
        summary.AppendLine($"Independent Save Capacity: {GbcIndependentSramBankCapacity} banks ({GetGbcIndependentSaveCapacityBytes()} bytes)");
        if (includedGames.Count == 0 && inputGames.Count > 0)
        {
            summary.AppendLine("Note: no ROM entries fit in the accessible flash region, so a real build would fail.");
        }

        if (skippedTitles.Count > 0)
        {
            summary.AppendLine($"Skipped Titles: {string.Join(", ", skippedTitles)}");
        }

        summary.AppendLine("Bank Map:");
        summary.AppendLine(FormatWrappedMap(BuildGbcBankMap(includedGames, accessibleFlashSize), 64));

        var sections = new List<ConfigPreviewSection>
        {
            new(
                "MBCXCFG Header",
                GbcConfigOffsetBytes,
                configHeader,
                BuildGbcConfigHeaderDetails(request, includedGames.Count)),
            new(
                "MBCXROM Header",
                GbcRomDirectoryOffsetBytes,
                directoryHeader,
                BuildGbcDirectoryHeaderDetails(includedGames.Count)),
            new(
                "MBCXROM Entries",
                GbcRomDirectoryOffsetBytes + GbcRomDirectoryHeaderSize,
                entryBytes,
                BuildGbcEntryDetails(includedGames, GbcRomDirectoryOffsetBytes + GbcRomDirectoryHeaderSize, useChineseMenu)),
        };

        if (fontPackage is not null)
        {
            sections.Add(new ConfigPreviewSection(
                "MBCXFNT Package",
                GbcFontOffsetBytes,
                fontPackage.Bytes,
                BuildGbcFontPackageDetails(fontPackage)));
        }

        return new ConfigPreviewReport(
            "GBC Config Area",
            summary.ToString().TrimEnd(),
            sections);
    }

    private static List<FcGame> PrepareFcGames(BuildRequest request,CancellationToken cancellationToken)
    {
        List<FcGame> result=[];
        foreach(var game in request.Games.Where(g=>g.Enabled))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(new FileInfo(game.RomPath).Length>FcMbcxCodec.CapacityBytes)throw new InvalidDataException("NES ROM exceeds card capacity.");
            byte[] data=File.ReadAllBytes(game.RomPath);
            result.Add(new FcGame{Name=game.Title,Data=data,Info=NesRomHeaderService.Parse(data)});
        }
        return result;
    }

    public BuildResult Build(
        BuildRequest request,
        IProgress<BuildProgressInfo>? progress,
        CancellationToken cancellationToken,
        Func<string, string, string, string>? textResolver = null)
    {
        if(request.Family==CompilationFamily.Fc || request.FlashCartMode==FlashCartMode.FcMbcx)
        {
            var games=PrepareFcGames(request,cancellationToken);
            var layout=FcMbcxCodec.Plan(games,request.Cartridge.FlashSize,false);
            FcMbcxCodec.Export(request.OutputPath,games,cancellationToken,new Progress<FcProgress>(p=>progress?.Report(new(p.Percent,p.Message))));
            return new(request.OutputPath,"FC02",layout.EndBytes,[],"NES/MBCX: 16 KiB ROM banks, 8 KiB SRAM banks");
        }
        if (IsGbcMaxBuild(request))
        {
            return BuildGbcCompilation(request, progress, cancellationToken, textResolver);
        }

        if (request.MinimalMenuPackage)
        {
            ValidateMinimalMenuPackageRequest(request, textResolver);
        }
        else
        {
            ValidateRequest(request, textResolver);
        }
        progress?.Report(new BuildProgressInfo(1, T(textResolver, "build", "loading_base_menu", "Loading base menu ROM.")));
        cancellationToken.ThrowIfCancellationRequested();

        var menuRom = PrepareMenuRom(request.BaseMenuPath);
        if (request.MinimalMenuPackage)
        {
            return BuildMinimalMenuPackage(request, menuRom, progress, cancellationToken, textResolver);
        }
        if (!IsCoverMenu(request.GbaMenuType) && !string.IsNullOrWhiteSpace(request.BackgroundPath))
        {
            progress?.Report(new BuildProgressInfo(5, T(textResolver, "build", "encoding_background", "Encoding background image.")));
            cancellationToken.ThrowIfCancellationRequested();
            ApplyBackgroundImage(
                menuRom,
                request.BackgroundPath!,
                request.FlashCartMode is FlashCartMode.WaterlineVirtualIron or FlashCartMode.Chm1 or FlashCartMode.Chm1NoBattery,
                textResolver);
        }

        var cart = request.Cartridge;
        var sectorCount = cart.FlashSize / cart.SectorSize;
        var compilation = GC.AllocateUninitializedArray<byte>(cart.FlashSize);
        Array.Fill(compilation, (byte)0xFF);
        var sectorMap = Enumerable.Repeat('.', sectorCount).ToArray();
        Buffer.BlockCopy(menuRom, 0, compilation, 0, menuRom.Length);

        var itemListOffsetBytes = AlignToNextBlock(menuRom.Length, 0x40000);
        var itemListOffset = DivCeil(itemListOffsetBytes, cart.SectorSize);
        var statusOffset = itemListOffset + 1;
        var saveDataSectorOffset = request.FlashCartMode.IsChm1Mode() ? Chm1Layout.SaveAreaOffset / cart.SectorSize : GetSaveDataStartSectorOffset(statusOffset);
        var systemReservedSectorCount = GetSystemReservedSectorCount(cart, statusOffset + 1);
        UpdateSectorMap(sectorMap, 0, systemReservedSectorCount, 'm');
        UpdateSectorMap(sectorMap, itemListOffset, 1, 'l');
        UpdateSectorMap(sectorMap, statusOffset, 1, 'c');

        var status = new byte[16];
        status[0] = (byte)'K';
        status[1] = (byte)'U';
        status[2] = (byte)'M';
        status[3] = (byte)'A';
        if (request.BatteryPresent)
        {
            status[5] = 0x01;
        }

        Buffer.BlockCopy(status, 0, compilation, statusOffset * cart.SectorSize, status.Length);
        var bootLogoFound = HasValidBootLogo(compilation);
        var keyOrder = new List<ushort> { 0 };
        var saveSlotsWritten = new HashSet<int>();
        var inputGames = request.Games.Where(static game => game.Enabled).ToList();

        progress?.Report(new BuildProgressInfo(10, Tf(textResolver, "build", "preparing_rom_entries", "Preparing {0} ROM entries.", inputGames.Count)));
        var preparedGames = PrepareGamesForBuild(
            inputGames,
            request,
            progress,
            cancellationToken,
            textResolver);

        foreach (var prepared in preparedGames)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (prepared.HiddenKeysMask != 0 && !keyOrder.Contains(prepared.HiddenKeysMask))
            {
                keyOrder.Add(prepared.HiddenKeysMask);
            }

            if (request.BatteryPresent && prepared.SaveSlotIndex.HasValue)
            {
                var slotIndex = prepared.SaveSlotIndex.Value;
                var offset = saveDataSectorOffset + slotIndex;
                UpdateSectorMap(sectorMap, offset, 1, 's');
                if (saveSlotsWritten.Add(slotIndex))
                {
                    var savePath = Path.ChangeExtension(prepared.RomPath, ".sav");
                    if (File.Exists(savePath))
                    {
                        var saveData = File.ReadAllBytes(savePath);
                        if (request.FlashCartMode.IsChm1Mode()) saveData = Chm1Layout.PrepareSaveImage(saveData);
                        if (saveData.Length < cart.SectorSize)
                        {
                            Array.Resize(ref saveData, cart.SectorSize);
                        }
                        else if (saveData.Length > cart.SectorSize)
                        {
                            Array.Resize(ref saveData, cart.SectorSize);
                        }

                        Buffer.BlockCopy(saveData, 0, compilation, offset * cart.SectorSize, saveData.Length);
                    }
                }
            }
        }

        if (preparedGames.Length == 0)
        {
            throw new InvalidOperationException(T(textResolver, "error", "no_enabled_roms", "No enabled ROMs were provided."));
        }

        var skippedTitles = new List<string>();
        var placementOrder = preparedGames.OrderByDescending(static game => game.Size).ToList();

        progress?.Report(new BuildProgressInfo(35, T(textResolver, "build", "placing_roms", "Placing ROMs into flash layout.")));
        for (var i = 0; i < placementOrder.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var game = placementOrder[i];
            if (TryPlaceGame(game, systemReservedSectorCount, sectorMap, compilation, cart, ref bootLogoFound))
            {
                progress?.Report(new BuildProgressInfo(
                    35 + ((i + 1) * 35 / Math.Max(1, placementOrder.Count)),
                    Tf(textResolver, "build", "placed_rom", "Placed: {0}", game.Title)));
            }
            else
            {
                skippedTitles.Add(game.Title);
                progress?.Report(new BuildProgressInfo(
                    35 + ((i + 1) * 35 / Math.Max(1, placementOrder.Count)),
                    Tf(textResolver, "build", "skipped_rom", "Skipped (not enough space): {0}", game.Title)));
            }
        }

        var includedGames = preparedGames
            .Where(static game => game.SectorOffset >= 0)
            .OrderBy(static game => game.Index)
            .ToList();

        IReadOnlyDictionary<PreparedGame, byte>? coverIndexes = null;
        if (IsCoverMenu(request.GbaMenuType))
        {
            var orderedGames = OrderGamesByHiddenKeys(includedGames, keyOrder);
            progress?.Report(new BuildProgressInfo(72, T(textResolver, "build", "encoding_ns_covers", "Matching and encoding NS menu covers.")));
            coverIndexes = BuildGbaCoverPack(compilation, menuRom.Length, orderedGames, request.BackgroundPath);
        }

        progress?.Report(new BuildProgressInfo(75, T(textResolver, "build", "writing_item_list", "Writing item list.")));
        var itemList = BuildItemList(includedGames, keyOrder, coverIndexes);
        Buffer.BlockCopy(itemList, 0, compilation, itemListOffset * cart.SectorSize, itemList.Length);

        if (request.FlashCartMode.IsChm1Mode())
        {
            var ordered = OrderGamesByHiddenKeys(includedGames, keyOrder);
            var maps = ordered.Select(game => Enumerable.Range(
                game.SectorOffset * cart.SectorSize / Chm1Layout.BucketBytes,
                game.Size / Chm1Layout.BucketBytes).ToArray()).ToArray();
            Chm1Layout.BuildMappingSector(maps, cart.FlashSize).CopyTo(compilation, Chm1Layout.MappingOffset);
            compilation[statusOffset * cart.SectorSize + 10] = 1;
        }

        var romCode = ComputeRomCode(status, itemList);
        WriteHeaderMetadata(compilation, romCode);
        var romSize = GetOutputRomSize(sectorMap, cart.SectorSize);
        var outputPath = request.OutputPath.Replace("<CODE>", romCode, StringComparison.OrdinalIgnoreCase);

        progress?.Report(new BuildProgressInfo(85, T(textResolver, "build", "writing_output", "Writing output file.")));
        WriteOutput(outputPath, request.SplitOutput, compilation, romSize, cart.FlashSize, cancellationToken);
        progress?.Report(new BuildProgressInfo(100, T(textResolver, "build", "build_completed", "Build completed.")));

        return new BuildResult(outputPath, romCode, romSize, skippedTitles, new string(sectorMap));
    }

    private static BuildResult BuildMinimalMenuPackage(
        BuildRequest request,
        byte[] menuRom,
        IProgress<BuildProgressInfo>? progress,
        CancellationToken cancellationToken,
        Func<string, string, string, string>? textResolver)
    {
        if (menuRom.Length > GbaMinimalMenuPackageSize)
        {
            throw new InvalidOperationException(
                $"The NS menu is too large for the 0x{GbaMinimalMenuPackageSize:X} minimal package area.");
        }

        var package = GC.AllocateUninitializedArray<byte>(GbaMinimalMenuPackageSize);
        Array.Fill(package, (byte)0xFF);
        Buffer.BlockCopy(menuRom, 0, package, 0, menuRom.Length);

        var enabledGames = request.Games.Where(static game => game.Enabled).ToList();
        var keyOrder = new List<ushort> { 0 };
        foreach (var game in enabledGames)
        {
            if (game.HiddenKeysMask != 0 && !keyOrder.Contains(game.HiddenKeysMask))
            {
                keyOrder.Add(game.HiddenKeysMask);
            }
        }

        var orderedGames = new List<BuildGameEntry>(enabledGames.Count);
        foreach (var key in keyOrder)
        {
            orderedGames.AddRange(enabledGames.Where(game => game.HiddenKeysMask == key));
        }

        progress?.Report(new BuildProgressInfo(
            45,
            T(textResolver, "build", "encoding_ns_covers", "Matching and encoding NS menu covers.")));
        cancellationToken.ThrowIfCancellationRequested();
        BuildGbaSlotCoverPack(package, menuRom.Length, orderedGames, request.BackgroundPath);

        var romCode = package.Length >= 0xB0
            ? Encoding.ASCII.GetString(package, 0xAC, 4)
            : "MENU";
        var outputPath = request.OutputPath.Replace("<CODE>", romCode, StringComparison.OrdinalIgnoreCase);
        progress?.Report(new BuildProgressInfo(85, T(textResolver, "build", "writing_output", "Writing output file.")));
        WriteOutput(
            outputPath,
            splitOutput: false,
            package,
            GbaMinimalMenuPackageSize,
            GbaMinimalMenuPackageSize,
            cancellationToken);
        progress?.Report(new BuildProgressInfo(100, T(textResolver, "build", "build_completed", "Build completed.")));

        var sectorCount = DivCeil(GbaMinimalMenuPackageSize, request.Cartridge.SectorSize);
        return new BuildResult(
            outputPath,
            romCode,
            GbaMinimalMenuPackageSize,
            [],
            new string('m', sectorCount));
    }

    private static Dictionary<PreparedGame, byte> BuildGbaCoverPack(
        byte[] compilation,
        int menuLength,
        IReadOnlyList<PreparedGame> orderedGames,
        string? coverDirectory)
    {
        var result = new Dictionary<PreparedGame, byte>();
        var directory = GbaCoverCatalog.FindCatalogDirectory(coverDirectory);
        IReadOnlyDictionary<string, GbaCoverCatalogEntry> catalog = directory is null
            ? new Dictionary<string, GbaCoverCatalogEntry>(StringComparer.OrdinalIgnoreCase)
            : GbaCoverCatalog.LoadPrimaryByGameCode(directory);
        var sources = new List<GbaMenuCoverSource>();
        var indexesBySource = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var game in orderedGames)
            {
                if (game.CoverCleared)
                {
                    continue;
                }

                var gameCode = ReadGbaGameCode(game.RomPath);
                var coverPath = ResolveGbaCoverPath(game.CoverPath, game.CoverCleared, gameCode, catalog);
                if (coverPath is null)
                {
                    continue;
                }

                var sourceKey = $"{gameCode}\0{coverPath}";
                if (indexesBySource.TryGetValue(sourceKey, out var existingIndex))
                {
                    result[game] = existingIndex;
                    continue;
                }
                if (sources.Count >= GbaMenuCoverPackBuilder.MaxCoverCount)
                {
                    continue;
                }
                using var loaded = Image.FromFile(coverPath);
                var bitmap = new Bitmap(loaded);
                var index = checked((byte)sources.Count);
                sources.Add(new GbaMenuCoverSource(gameCode, bitmap));
                indexesBySource[sourceKey] = index;
                result[game] = index;
            }

            if (sources.Count == 0)
            {
                Bitmap placeholder = new(GbaMenuCoverPackBuilder.CoverWidth, GbaMenuCoverPackBuilder.CoverHeight);
                using (Graphics graphics = Graphics.FromImage(placeholder))
                {
                    graphics.Clear(Color.White);
                }
                sources.Add(new GbaMenuCoverSource("UNKN", placeholder));
            }

            WriteGbaCoverPack(
                compilation,
                menuLength,
                sources,
                GbaMenuCoverPackBuilder.IndexedFormatVersion);
            return result;
        }
        finally
        {
            foreach (var source in sources) source.Image?.Dispose();
        }
    }

    private static void BuildGbaSlotCoverPack(
        byte[] package,
        int menuLength,
        IReadOnlyList<BuildGameEntry> orderedGames,
        string? coverDirectory)
    {
        var directory = GbaCoverCatalog.FindCatalogDirectory(coverDirectory);
        IReadOnlyDictionary<string, GbaCoverCatalogEntry> catalog = directory is null
            ? new Dictionary<string, GbaCoverCatalogEntry>(StringComparer.OrdinalIgnoreCase)
            : GbaCoverCatalog.LoadPrimaryByGameCode(directory);
        var sourceCount = Math.Min(orderedGames.Count, GbaMenuCoverPackBuilder.MaxCoverCount);
        var sources = new List<GbaMenuCoverSource>(Math.Max(1, sourceCount));
        try
        {
            for (var index = 0; index < sourceCount; index++)
            {
                var game = orderedGames[index];
                var gameCode = ReadGbaGameCode(game.RomPath);
                var coverPath = ResolveGbaCoverPath(game.CoverPath, game.CoverCleared, gameCode, catalog);
                if (coverPath is null)
                {
                    sources.Add(new GbaMenuCoverSource(gameCode, null));
                    continue;
                }

                using var loaded = Image.FromFile(coverPath);
                sources.Add(new GbaMenuCoverSource(gameCode, new Bitmap(loaded)));
            }

            if (sources.Count == 0)
            {
                sources.Add(new GbaMenuCoverSource("UNKN", null));
            }

            WriteGbaCoverPack(
                package,
                menuLength,
                sources,
                GbaMenuCoverPackBuilder.EntrySlotFormatVersion);
        }
        finally
        {
            foreach (var source in sources) source.Image?.Dispose();
        }
    }

    private static string? ResolveGbaCoverPath(
        string? configuredCoverPath,
        bool coverCleared,
        string gameCode,
        IReadOnlyDictionary<string, GbaCoverCatalogEntry> catalog)
    {
        if (coverCleared)
        {
            return null;
        }
        if (!string.IsNullOrWhiteSpace(configuredCoverPath) && File.Exists(configuredCoverPath))
        {
            return Path.GetFullPath(configuredCoverPath);
        }
        return GbaCoverCatalog.TryFindByGameCode(catalog, gameCode, out var cover) && cover is not null
            ? cover.CoverPath
            : null;
    }

    private static void WriteGbaCoverPack(
        byte[] destination,
        int menuLength,
        IReadOnlyList<GbaMenuCoverSource> sources,
        ushort formatVersion)
    {
        var pack = GbaMenuCoverPackBuilder.Build(sources, formatVersion).PackData;
        var packOffset = GbaCoverTrailerOffset - pack.Length;
        if (packOffset < menuLength)
        {
            throw new InvalidOperationException($"Matched cover pack is too large for the NS Menu head area (needs 0x{pack.Length:X} bytes, available 0x{GbaCoverTrailerOffset - menuLength:X}).");
        }
        Buffer.BlockCopy(pack, 0, destination, packOffset, pack.Length);
        Span<byte> trailer = stackalloc byte[16];
        "GCVT"u8.CopyTo(trailer);
        BinaryPrimitives.WriteUInt16LittleEndian(trailer[4..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(trailer[6..], 16);
        BinaryPrimitives.WriteUInt32LittleEndian(trailer[8..], (uint)packOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(trailer[12..], (uint)pack.Length);
        trailer.CopyTo(destination.AsSpan(GbaCoverTrailerOffset));
    }

    private static string ReadGbaGameCode(string romPath)
    {
        using var stream = File.OpenRead(romPath);
        if (stream.Length < 0xB0) return string.Empty;
        stream.Position = 0xAC;
        Span<byte> code = stackalloc byte[4];
        return stream.Read(code) == 4 ? Encoding.ASCII.GetString(code).Trim().ToUpperInvariant() : string.Empty;
    }

    private PreparedGame[] PrepareGamesForBuild(
        IReadOnlyList<BuildGameEntry> inputGames,
        BuildRequest request,
        IProgress<BuildProgressInfo>? progress,
        CancellationToken cancellationToken,
        Func<string, string, string, string>? textResolver)
    {
        if (inputGames.Count == 0)
        {
            return [];
        }

        var preparedGames = new PreparedGame?[inputGames.Count];
        var completedCount = 0;

        try
        {
            Parallel.For(
                0,
                inputGames.Count,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = MaxConcurrentBuildPreparationWorkerCount,
                },
                i =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var prepared = PrepareGame(
                        inputGames[i],
                        i,
                        request.Cartridge,
                        request.MinRomSize,
                        request.Cartridge.SectorSize,
                        request.FlashCartMode,
                        request.BatteryPresent,
                        ignoreSramPatchFailure: false,
                        textResolver);

                    preparedGames[i] = prepared;
                    var preparedCount = Interlocked.Increment(ref completedCount);
                    progress?.Report(new BuildProgressInfo(
                        10 + (preparedCount * 20 / inputGames.Count),
                        FormatPreparedRomMessage(prepared, textResolver)));
                });
        }
        catch (AggregateException ex)
        {
            RethrowParallelPreparationException(ex);
            throw;
        }

        return preparedGames
            .Select(static game => game ?? throw new InvalidOperationException("Internal error: ROM preparation did not produce a result."))
            .ToArray();
    }

    private static bool IsGbcMaxBuild(BuildRequest request) =>
        request.Family == CompilationFamily.Gbc &&
        request.FlashCartMode == FlashCartMode.GbcMaxMbcx512K;

    private static string GetGbcBuildLanguageCode(BuildRequest request) =>
        IsGbcChineseMenuBuild(request) ? LanguageService.DefaultLanguageCode : "en_US";

    private static bool IsGbcChineseMenuBuild(BuildRequest request)
    {
        string fileName = Path.GetFileNameWithoutExtension(request.BaseMenuPath);
        if (fileName.Contains("menu_en", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith("_en", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (fileName.Contains("menu_zh", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith("_zh", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IsGbcChineseLanguage(AppConfigService.LoadLanguageCode() ?? LanguageService.DefaultLanguageCode);
    }

    private static bool IsGbcChineseLanguage(string? languageCode) =>
        string.Equals(
            string.IsNullOrWhiteSpace(languageCode) ? LanguageService.DefaultLanguageCode : languageCode.Trim(),
            LanguageService.DefaultLanguageCode,
            StringComparison.OrdinalIgnoreCase);

    private static int GetGbcMenuGamesPerPage(string? languageCode) =>
        IsGbcChineseLanguage(languageCode) ? GbcChineseMenuGamesPerPage : GbcEnglishMenuGamesPerPage;

    private static BuildLayoutEstimate AnalyzeGbcLayout(
        BuildRequest request,
        Func<string, string, string, string>? textResolver)
    {
        ValidateGbcRequest(request, textResolver);
        var inputGames = request.Games.Where(static game => game.Enabled).ToList();
        var preparedGames = PrepareGbcGames(inputGames, request.MinRomSize, textResolver);
        var accessibleFlashSize = GetGbcAccessibleFlashSize(request.Cartridge);
        var usedPayloadBytes = checked((int)preparedGames.Sum(static game => game.AllocatedBytes));
        var usedBytes = checked(GbcFixedMapReservedBytes + usedPayloadBytes);
        var payloadCapacityBytes = Math.Max(0, accessibleFlashSize - GbcFixedMapReservedBytes);
        return new BuildLayoutEstimate(
            usedBytes,
            Math.Max(0, accessibleFlashSize - usedBytes),
            accessibleFlashSize,
            preparedGames.Length,
            0,
            GbcFixedMapReservedBytes,
            usedPayloadBytes,
            payloadCapacityBytes);
    }

    private static long EstimateGbcGamePayloadBytes(
        BuildGameEntry game,
        BuildRequest request,
        Func<string, string, string, string>? textResolver)
    {
        if (string.IsNullOrWhiteSpace(game.RomPath) || !File.Exists(game.RomPath))
        {
            throw new InvalidOperationException(Tf(textResolver, "error", "rom_not_found", "ROM not found: {0}", game.RomPath));
        }

        var metadata = GameBoyRomHeaderService.Read(game.RomPath);
        return ComputeGbcAllocatedRomBytes(
            new FileInfo(game.RomPath).Length,
            metadata.EffectiveRomSizeBytes,
            request.MinRomSize);
    }

    private BuildResult BuildGbcCompilation(
        BuildRequest request,
        IProgress<BuildProgressInfo>? progress,
        CancellationToken cancellationToken,
        Func<string, string, string, string>? textResolver)
    {
        ValidateGbcRequest(request, textResolver);
        var useChineseMenu = IsGbcChineseMenuBuild(request);
        progress?.Report(new BuildProgressInfo(1, T(textResolver, "build", "loading_base_menu", "Loading base menu ROM.")));
        cancellationToken.ThrowIfCancellationRequested();

        var menuRom = File.ReadAllBytes(request.BaseMenuPath);
        if (menuRom.Length > GbcMenuRegionBytes)
        {
            throw new InvalidOperationException(
                Tf(
                    textResolver,
                    "error",
                    "gbc_menu_too_large",
                    "The MAX/MBCX base menu ROM exceeds the reserved 128 KiB region: {0}",
                    Path.GetFileName(request.BaseMenuPath)));
        }

        var accessibleFlashSize = GetGbcAccessibleFlashSize(request.Cartridge);
        var compilation = GC.AllocateUninitializedArray<byte>(accessibleFlashSize);
        Array.Fill(compilation, (byte)0xFF);
        Buffer.BlockCopy(menuRom, 0, compilation, 0, menuRom.Length);

        var inputGames = request.Games.Where(static game => game.Enabled).ToList();
        progress?.Report(new BuildProgressInfo(10, Tf(textResolver, "build", "preparing_rom_entries", "Preparing {0} ROM entries.", inputGames.Count)));
        var preparedGames = PrepareGbcGames(inputGames, request.MinRomSize, textResolver);

        var includedGames = new List<PreparedGbcGame>(preparedGames.Length);
        var skippedTitles = new List<string>();
        long currentOffset = GbcFixedMapReservedBytes;
        var currentBank = GbcFixedMapReservedBytes / GameBoyRomHeaderService.RomBankSize;

        progress?.Report(new BuildProgressInfo(35, T(textResolver, "build", "placing_roms", "Placing ROMs into flash layout.")));
        for (var i = 0; i < preparedGames.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var preparedGame = preparedGames[i];
            if (currentOffset + preparedGame.AllocatedBytes > compilation.Length)
            {
                skippedTitles.Add(preparedGame.Title);
                progress?.Report(new BuildProgressInfo(
                    35 + ((i + 1) * 35 / Math.Max(1, preparedGames.Length)),
                    Tf(textResolver, "build", "skipped_rom", "Skipped (not enough space): {0}", preparedGame.Title)));
                continue;
            }

            var gameData = File.ReadAllBytes(preparedGame.RomPath);
            Buffer.BlockCopy(gameData, 0, compilation, checked((int)currentOffset), gameData.Length);
            includedGames.Add(preparedGame with
            {
                RomIndex = includedGames.Count,
                RomOffsetBank = currentBank,
            });

            currentOffset += preparedGame.AllocatedBytes;
            currentBank += preparedGame.AllocatedRomBanks;
            progress?.Report(new BuildProgressInfo(
                35 + ((i + 1) * 35 / Math.Max(1, preparedGames.Length)),
                Tf(textResolver, "build", "placed_rom", "Placed: {0}", preparedGame.Title)));
        }

        if (includedGames.Count == 0)
        {
            throw new InvalidOperationException(T(textResolver, "error", "no_enabled_roms", "No enabled ROMs were provided."));
        }

        progress?.Report(new BuildProgressInfo(75, T(textResolver, "build", "writing_item_list", "Writing item list.")));
        var configHeader = BuildGbcConfigHeader(request, includedGames.Count);
        var directoryHeader = BuildGbcDirectoryHeader(includedGames.Count);
        var fontPackage = useChineseMenu
            ? BuildGbcFontPackage(includedGames, request.GbcFontPath)
            : null;
        var entryOffset = GbcRomDirectoryOffsetBytes + GbcRomDirectoryHeaderSize;
        Buffer.BlockCopy(configHeader, 0, compilation, GbcConfigOffsetBytes, configHeader.Length);
        Buffer.BlockCopy(directoryHeader, 0, compilation, GbcRomDirectoryOffsetBytes, directoryHeader.Length);

        for (var i = 0; i < includedGames.Count; i++)
        {
            var entryBytes = BuildGbcDirectoryEntry(includedGames[i], useChineseMenu);
            Buffer.BlockCopy(entryBytes, 0, compilation, entryOffset + (i * GbcRomEntrySize), entryBytes.Length);
        }

        if (fontPackage is not null && fontPackage.Bytes.Length > GbcFixedMapReservedBytes - GbcFontOffsetBytes)
        {
            throw new InvalidOperationException("Generated GBC font package exceeded the reserved font area.");
        }

        if (fontPackage is not null)
        {
            Buffer.BlockCopy(fontPackage.Bytes, 0, compilation, GbcFontOffsetBytes, fontPackage.Bytes.Length);
        }

        if (useChineseMenu)
        {
            var layerFontPath = ResolveRequiredGbcMenuLayerFontPath(request.GbcFontPath);
            WriteGbcMenuLayer(compilation, includedGames, layerFontPath);
            EnsureGbcMenuLayerHeader(compilation);
        }
        WriteGbcBackground(compilation, request.BackgroundPath, textResolver);

        var romCode = ComputeGbcRomCode(includedGames);
        var outputPath = request.OutputPath.Replace("<CODE>", romCode, StringComparison.OrdinalIgnoreCase);
        var romSize = checked((int)currentOffset);
        PatchGbcCartridgeHeader(compilation, romSize);

        progress?.Report(new BuildProgressInfo(85, T(textResolver, "build", "writing_output", "Writing output file.")));
        WriteOutput(outputPath, request.SplitOutput, compilation, romSize, compilation.Length, cancellationToken);
        progress?.Report(new BuildProgressInfo(100, T(textResolver, "build", "build_completed", "Build completed.")));

        return new BuildResult(outputPath, romCode, romSize, skippedTitles, BuildGbcBankMap(includedGames, compilation.Length));
    }

    private static void ValidateGbcRequest(
        BuildRequest request,
        Func<string, string, string, string>? textResolver)
    {
        var useChineseMenu = IsGbcChineseMenuBuild(request);

        if (string.IsNullOrWhiteSpace(request.BaseMenuPath) || !File.Exists(request.BaseMenuPath))
        {
            throw new InvalidOperationException(T(textResolver, "error", "base_menu_not_found", "Base menu ROM was not found."));
        }

        if (string.IsNullOrWhiteSpace(request.OutputPath))
        {
            throw new InvalidOperationException(T(textResolver, "error", "output_path_required", "Output path is required."));
        }

        if (request.Games.Count == 0)
        {
            throw new InvalidOperationException(T(textResolver, "error", "add_one_rom", "Add at least one ROM before building."));
        }

        var enabledGames = request.Games.Where(static game => game.Enabled).ToList();
        if (enabledGames.Count == 0)
        {
            throw new InvalidOperationException(T(textResolver, "error", "no_enabled_roms", "No enabled ROMs were provided."));
        }

        if (enabledGames.Count > GbcMaxGames)
        {
            throw new InvalidOperationException(
                Tf(
                    textResolver,
                    "error",
                    "gbc_too_many_games",
                    "The current MAX/MBCX menu supports at most {0} ROM entries.",
                    GbcMaxGames));
        }

        if (new FileInfo(request.BaseMenuPath).Length > GbcMenuRegionBytes)
        {
            throw new InvalidOperationException(
                Tf(
                    textResolver,
                    "error",
                    "gbc_menu_too_large",
                    "The MAX/MBCX base menu ROM exceeds the reserved 128 KiB region: {0}",
                    Path.GetFileName(request.BaseMenuPath)));
        }

        if (useChineseMenu && string.IsNullOrWhiteSpace(GbcFontService.FindFontPath(request.GbcFontPath)))
        {
            throw new InvalidOperationException(
                T(
                    textResolver,
                    "error",
                    "gbc_layer_font_required",
                    "A valid built-in 8px/12px GBC font is required for layered MBCX menu generation."));
        }

        if (!string.IsNullOrWhiteSpace(request.BackgroundPath) && !File.Exists(request.BackgroundPath))
        {
            throw new InvalidOperationException(
                Tf(
                    textResolver,
                    "error",
                    "gbc_background_not_found",
                    "GBC background image was not found: {0}",
                    request.BackgroundPath));
        }

        foreach (var game in enabledGames)
        {
            if (string.IsNullOrWhiteSpace(game.RomPath) || !File.Exists(game.RomPath))
            {
                throw new InvalidOperationException(Tf(textResolver, "error", "rom_not_found", "ROM not found: {0}", game.RomPath));
            }
        }
    }

    private static PreparedGbcGame[] PrepareGbcGames(
        IReadOnlyList<BuildGameEntry> inputGames,
        int minRomSize,
        Func<string, string, string, string>? textResolver)
    {
        var preparedGames = new PreparedGbcGame[inputGames.Count];
        var nextIndependentRamBank = 0;

        for (var i = 0; i < inputGames.Count; i++)
        {
            var game = inputGames[i];
            var romPath = Path.GetFullPath(game.RomPath);
            if (!File.Exists(romPath))
            {
                throw new InvalidOperationException(Tf(textResolver, "error", "rom_not_found", "ROM not found: {0}", game.RomPath));
            }

            var metadata = GameBoyRomHeaderService.Read(romPath);
            var fileLength = new FileInfo(romPath).Length;
            var allocatedBytes = ComputeGbcAllocatedRomBytes(fileLength, metadata.EffectiveRomSizeBytes, minRomSize);
            var allocatedRomBanks = checked((int)(allocatedBytes / GameBoyRomHeaderService.RomBankSize));
            var ramBankCount = metadata.EffectiveRamBankCount;
            var usesDedicatedSaveAllocation = game.GbcDedicatedSaveAllocation && metadata.HasBattery && ramBankCount > 0;
            var title = string.IsNullOrWhiteSpace(game.Title)
                ? Path.GetFileNameWithoutExtension(romPath)
                : game.Title.Trim();
            var headerTitle = string.IsNullOrWhiteSpace(metadata.HeaderTitle)
                ? title
                : metadata.HeaderTitle.Trim();

            if (usesDedicatedSaveAllocation && ramBankCount > GbcMaxGameSramBanks)
            {
                throw new InvalidOperationException(
                    Tf(
                        textResolver,
                        "error",
                        "gbc_ram_too_large_battery",
                        "ROM requires {0} SRAM banks, but the current MAX/MBCX runtime supports at most {1} independently mapped SRAM banks per game: {2}",
                        ramBankCount,
                        GbcMaxGameSramBanks,
                        title));
            }

            if (!usesDedicatedSaveAllocation && ramBankCount > GbcFixedSharedSramBanks)
            {
                throw new InvalidOperationException(
                    Tf(
                        textResolver,
                        "error",
                        "gbc_ram_too_large_shared",
                        "ROM requires {0} SRAM banks, but the shared MAX/MBCX save area currently provides at most {1} SRAM banks: {2}",
                        ramBankCount,
                        GbcFixedSharedSramBanks,
                        title));
            }

            var sramBankMap = Enumerable.Repeat((byte)0xFF, GbcMaxGameSramBanks).ToArray();
            if (usesDedicatedSaveAllocation)
            {
                if (nextIndependentRamBank + ramBankCount > GbcIndependentSramBankCapacity)
                {
                    throw new InvalidOperationException(
                        Tf(
                            textResolver,
                            "error",
                            "gbc_sram_capacity_exceeded",
                            "Battery-backed SRAM allocation exceeds the MAX/MBCX independent save capacity ({0} banks).",
                            GbcIndependentSramBankCapacity));
                }

                for (var bankIndex = 0; bankIndex < ramBankCount; bankIndex++)
                {
                    sramBankMap[bankIndex] = (byte)(nextIndependentRamBank + bankIndex);
                }

                nextIndependentRamBank += ramBankCount;
            }
            else
            {
                for (var bankIndex = 0; bankIndex < ramBankCount; bankIndex++)
                {
                    sramBankMap[bankIndex] = (byte)(GbcSharedSramBankStart + bankIndex);
                }
            }

            preparedGames[i] = new PreparedGbcGame(
                i,
                0,
                title,
                headerTitle,
                romPath,
                fileLength,
                allocatedBytes,
                allocatedRomBanks,
                ramBankCount,
                metadata.CartridgeType,
                metadata.HasBattery,
                usesDedicatedSaveAllocation,
                0,
                sramBankMap);
        }

        return preparedGames;
    }

    private static long ComputeGbcAllocatedRomBytes(long fileLength, long effectiveRomSizeBytes, int minRomSize)
    {
        var allocationUnit = Math.Max(GameBoyRomHeaderService.RomBankSize, minRomSize);
        var minimumBytes = Math.Max(2L * GameBoyRomHeaderService.RomBankSize, effectiveRomSizeBytes);
        var targetBytes = Math.Max(Math.Max(fileLength, minimumBytes), allocationUnit);
        return RoundUpLong(targetBytes, allocationUnit);
    }

    private static void PatchGbcCartridgeHeader(byte[] romData, int usedRomBytes)
    {
        if (romData.Length < 0x150)
        {
            throw new InvalidOperationException("Generated GBC compilation is too small to contain a valid cartridge header.");
        }

        var romSizeCode = GetGbcRomSizeCode(usedRomBytes);
        romData[0x147] = 0x19;
        romData[0x148] = romSizeCode;
        romData[0x149] = 0x00;

        byte headerChecksum = 0;
        for (var i = 0x134; i <= 0x14C; i++)
        {
            headerChecksum = unchecked((byte)(headerChecksum - romData[i] - 1));
        }

        romData[0x14D] = headerChecksum;

        romData[0x14E] = 0x00;
        romData[0x14F] = 0x00;
        uint globalChecksum = 0;
        for (var i = 0; i < romData.Length; i++)
        {
            if (i == 0x14E || i == 0x14F)
            {
                continue;
            }

            globalChecksum += romData[i];
        }

        romData[0x14E] = (byte)((globalChecksum >> 8) & 0xFF);
        romData[0x14F] = (byte)(globalChecksum & 0xFF);
    }

    private static byte GetGbcRomSizeCode(int usedRomBytes)
    {
        foreach (var candidate in new (byte Code, int SizeBytes)[]
        {
            (0x00, 0x008000),
            (0x01, 0x010000),
            (0x02, 0x020000),
            (0x03, 0x040000),
            (0x04, 0x080000),
            (0x05, 0x100000),
            (0x06, 0x200000),
            (0x07, 0x400000),
            (0x08, 0x800000),
        })
        {
            if (usedRomBytes <= candidate.SizeBytes)
            {
                return candidate.Code;
            }
        }

        return 0x08;
    }

    public static int GetGbcAccessibleFlashSize(CartridgeDefinition cartridge) =>
        Math.Min(cartridge.FlashSize, GbcAddressableTotalBytes);

    internal static int GetGbcIndependentSaveCapacityBytes() =>
        GbcIndependentSramBankCapacity * GameBoyRomHeaderService.RamBankSize;

    public static byte[] BuildGbcConfigHeader(BuildRequest request, int romCount)
    {
        var languageCode = GetGbcBuildLanguageCode(request);
        var buffer = GC.AllocateUninitializedArray<byte>(GbcConfigHeaderSize);
        Array.Fill(buffer, (byte)0xFF);
        WriteAscii(buffer, 0, "MBCXCFG");
        WriteUInt16(buffer, 8, 0x0001);
        buffer[10] = request.BatteryPresent ? (byte)0 : (byte)1;
        buffer[11] = IsGbcChineseLanguage(languageCode)
            ? (byte)1
            : (byte)0;
        WriteUInt16(buffer, 12, 0x0000);
        WriteUInt16(buffer, 14, checked((ushort)romCount));
        WriteUInt32(buffer, 16, GbcRomDirectoryOffsetBytes);
        WriteUInt32(buffer, 20, GbcFontOffsetBytes - GbcRomDirectoryOffsetBytes);
        WriteUInt32(buffer, 24, GbcFontOffsetBytes);
        WriteUInt32(buffer, 28, GbcFixedMapReservedBytes - GbcFontOffsetBytes);
        return buffer;
    }

    public static byte[] BuildGbcDirectoryHeader(int romCount)
    {
        var buffer = GC.AllocateUninitializedArray<byte>(GbcRomDirectoryHeaderSize);
        Array.Fill(buffer, (byte)0xFF);
        WriteAscii(buffer, 0, "MBCXROM");
        WriteUInt16(buffer, 8, 0x0001);
        WriteUInt16(buffer, 10, checked((ushort)romCount));
        WriteUInt16(buffer, 12, GbcRomEntrySize);
        WriteUInt16(buffer, 14, 0x0001);
        WriteUInt32(buffer, 16, GbcRomDirectoryOffsetBytes + GbcRomDirectoryHeaderSize);
        WriteUInt32(buffer, 20, GbcFontOffsetBytes - GbcRomDirectoryOffsetBytes - GbcRomDirectoryHeaderSize);
        return buffer;
    }

    private static byte[] BuildGbcDirectoryEntry(PreparedGbcGame game, bool useChineseMenu)
    {
        var buffer = GC.AllocateUninitializedArray<byte>(GbcRomEntrySize);
        Array.Fill(buffer, (byte)0xFF);
        WriteUInt16(buffer, 0, checked((ushort)game.RomIndex));
        var encodedName = useChineseMenu
            ? EncodeGbcEntryName(game.Title, game.HeaderTitle)
            : null;
        var asciiName = useChineseMenu ? string.Empty : EncodeGbcEntryAsciiName(game);
        WriteUInt16(buffer, 2, useChineseMenu
            ? checked((ushort)encodedName!.Length)
            : checked((ushort)asciiName.Length));
        WriteUInt32(buffer, 4, checked((uint)game.RomOffsetBank));
        WriteUInt16(buffer, 8, checked((ushort)game.AllocatedRomBanks));
        buffer[10] = checked((byte)game.RamBankCount);
        buffer[11] = game.MbcType;
        buffer[12] = game.HasBattery ? (byte)1 : (byte)0;
        buffer[13] = 0x01;

        if (useChineseMenu)
        {
            for (var i = 0; i < encodedName!.Length; i++)
            {
                WriteUInt16(buffer, GbcRomNameOffset + (i * sizeof(ushort)), encodedName[i]);
            }
        }
        else
        {
            Encoding.ASCII.GetBytes(asciiName).CopyTo(buffer, GbcRomNameOffset);
            if (asciiName.Length < GbcRomNameAsciiLength)
            {
                buffer[GbcRomNameOffset + asciiName.Length] = 0x00;
            }
        }

        for (var i = 0; i < game.SramBankMap.Length; i++)
        {
            buffer[GbcRomSramMapOffset + i] = game.SramBankMap[i];
        }

        return buffer;
    }

    private static string EncodeGbcEntryAsciiName(PreparedGbcGame game)
    {
        var source = game.Title;
        if (string.IsNullOrWhiteSpace(source))
        {
            source = game.HeaderTitle;
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            source = "UNKNOWN";
        }

        var normalized = new StringBuilder(GbcRomNameAsciiLength);
        foreach (var ch in source.Trim())
        {
            if (normalized.Length >= GbcRomNameAsciiLength)
            {
                break;
            }

            normalized.Append(ch is >= ' ' and <= '~' ? ch : '?');
        }

        return normalized.Length == 0 ? "UNKNOWN" : normalized.ToString().TrimEnd();
    }

    private static ushort[] EncodeGbcEntryName(string localizedTitle, string headerTitle)
    {
        var normalizedLocalizedTitle = string.IsNullOrWhiteSpace(localizedTitle) ? "UNKNOWN" : localizedTitle.Trim();
        var normalizedHeaderTitle = string.IsNullOrWhiteSpace(headerTitle) ? normalizedLocalizedTitle : headerTitle.Trim();
        var localizedCodes = normalizedLocalizedTitle
            .Where(static character => !char.IsSurrogate(character))
            .Select(static character => (ushort)character)
            .ToList();
        var headerCodes = normalizedHeaderTitle
            .Where(static character => !char.IsSurrogate(character))
            .Select(static character => character is >= ' ' and <= '~' ? (ushort)character : (ushort)'?')
            .Take(15)
            .ToList();

        if (localizedCodes.Count == 0)
        {
            localizedCodes.Add((ushort)'U');
        }

        if (headerCodes.Count == 0)
        {
            headerCodes.Add((ushort)'U');
        }

        var encoded = new List<ushort>(GbcRomNameCodeCount);
        var localizedBudget = Math.Max(1, GbcRomNameCodeCount - headerCodes.Count - 2);
        foreach (var code in localizedCodes.Take(localizedBudget))
        {
            encoded.Add(code);
        }

        encoded.Add(0);
        foreach (var code in headerCodes)
        {
            if (encoded.Count >= GbcRomNameCodeCount - 1)
            {
                break;
            }

            encoded.Add(code);
        }

        if (encoded.Count < GbcRomNameCodeCount)
        {
            encoded.Add(0);
        }

        return encoded.ToArray();
    }

    private static GbcFontPackageInfo BuildGbcFontPackage(IReadOnlyList<PreparedGbcGame> games, string? explicitFontBdfPath)
    {
        var requiredCodepoints = CollectGbcFontCodepoints(games);
        var orderedCodepoints = requiredCodepoints.OrderBy(static codepoint => codepoint).ToArray();
        var glyphLoadResult = orderedCodepoints.Length == 0
            ? new GbcGlyphLoadResult([], [], string.Empty, GbcFontService.DetectBitmapFormat(explicitFontBdfPath ?? string.Empty))
            : LoadGbcGlyphBitmaps(orderedCodepoints, explicitFontBdfPath);
        var glyphBitmaps = glyphLoadResult.GlyphBitmaps;
        var glyphFormat = glyphLoadResult.Format;
        var buffer = GC.AllocateUninitializedArray<byte>(
            GbcFontHeaderSize + (orderedCodepoints.Length * sizeof(ushort) * 2) + (glyphBitmaps.Length * glyphFormat.GlyphBytes));
        Array.Fill(buffer, (byte)0xFF);
        WriteAscii(buffer, 0, GbcFontSignature);
        WriteUInt16(buffer, 8, 0x0001);
        WriteUInt16(buffer, 10, checked((ushort)orderedCodepoints.Length));
        WriteUInt16(buffer, 12, checked((ushort)glyphFormat.GlyphSizePx));
        WriteUInt16(buffer, 14, checked((ushort)glyphFormat.GlyphBytes));

        var tableBase = GbcFontOffsetBytes + GbcFontHeaderSize;
        var bitmapBase = tableBase + (orderedCodepoints.Length * 4);
        WriteUInt32(buffer, 16, checked((uint)tableBase));
        WriteUInt32(buffer, 20, checked((uint)bitmapBase));
        WriteUInt32(buffer, 24, checked((uint)(orderedCodepoints.Length * 4)));
        WriteUInt32(buffer, 28, checked((uint)(glyphBitmaps.Length * glyphFormat.GlyphBytes)));

        for (var i = 0; i < orderedCodepoints.Length; i++)
        {
            var tableOffset = GbcFontHeaderSize + (i * 4);
            var bitmapOffset = GbcFontHeaderSize + (orderedCodepoints.Length * 4) + (i * glyphFormat.GlyphBytes);
            WriteUInt16(buffer, tableOffset, orderedCodepoints[i]);
            WriteUInt16(buffer, tableOffset + 2, checked((ushort)i));
            Buffer.BlockCopy(glyphBitmaps[i], 0, buffer, bitmapOffset, glyphFormat.GlyphBytes);
        }

        return new GbcFontPackageInfo(
            buffer,
            orderedCodepoints,
            glyphLoadResult.MissingCodepoints,
            glyphLoadResult.SourcePath,
            glyphFormat,
            tableBase,
            bitmapBase);
    }

    private static ushort[] CollectGbcFontCodepoints(IReadOnlyList<PreparedGbcGame> games) =>
        games
            .SelectMany(static game => game.Title)
            .Where(static character => !char.IsSurrogate(character) && character >= 0x80)
            .Select(static character => (ushort)character)
            .Distinct()
            .ToArray();

    private static GbcGlyphLoadResult LoadGbcGlyphBitmaps(IReadOnlyList<ushort> orderedCodepoints, string? explicitFontPath)
    {
        var fontPath = GbcFontService.FindFontPath(explicitFontPath);
        if (string.IsNullOrWhiteSpace(fontPath) || !File.Exists(fontPath))
        {
            throw new InvalidOperationException(
                $"Required GBC font source was not found. Expected a 12px BDF/TTF/OTF font for {orderedCodepoints.Count} glyphs.");
        }

        var glyphCache = GbcFontService.BuildGlyphCacheDetailed(orderedCodepoints.Select(static codepoint => (int)codepoint), fontPath);
        var glyphBitmaps = new byte[orderedCodepoints.Count][];
        var missingCodepoints = new List<ushort>();
        for (var i = 0; i < orderedCodepoints.Count; i++)
        {
            var codepoint = orderedCodepoints[i];
            if (glyphCache.Glyphs.TryGetValue(codepoint, out var glyphBitmap))
            {
                glyphBitmaps[i] = glyphBitmap;
            }
            else
            {
                missingCodepoints.Add(codepoint);
                glyphBitmaps[i] = GbcFontService.CreateFallbackGbcGlyphBitmap(glyphCache.Format.GlyphSizePx);
            }
        }

        missingCodepoints.AddRange(glyphCache.MissingCodepoints.Where(codepoint => !missingCodepoints.Contains(codepoint)));

        if (missingCodepoints.Count > 0)
        {
            throw new InvalidOperationException(
                $"The configured GBC pixel font is missing required glyphs from {fontPath}: {FormatCodepointList(missingCodepoints)}");
        }

        return new GbcGlyphLoadResult(glyphBitmaps, missingCodepoints.ToArray(), fontPath, glyphCache.Format);
    }

    private static string ComputeGbcRomCode(IReadOnlyList<PreparedGbcGame> games)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        foreach (var game in games)
        {
            writer.Write(game.RomOffsetBank);
            writer.Write(game.AllocatedRomBanks);
            writer.Write(game.MbcType);
            writer.Write(game.HasBattery);
            writer.Write(game.UsesDedicatedSaveAllocation);
            writer.Write(game.SramBankMap);
            writer.Write(Encoding.UTF8.GetBytes(game.Title));
        }

        writer.Flush();
        var hash = SHA1.HashData(stream.ToArray());
        return $"MX{Convert.ToHexString(hash)[..3]}";
    }

    private static string BuildGbcBankMap(IReadOnlyList<PreparedGbcGame> games, int flashSizeBytes)
    {
        var bankCount = flashSizeBytes / GameBoyRomHeaderService.RomBankSize;
        var bankMap = Enumerable.Repeat('.', bankCount).ToArray();
        var reservedBanks = GbcFixedMapReservedBytes / GameBoyRomHeaderService.RomBankSize;
        UpdateBankMap(bankMap, 0, reservedBanks, 'm');
        foreach (var game in games)
        {
            UpdateBankMap(bankMap, game.RomOffsetBank, game.AllocatedRomBanks, 'r');
        }

        return new string(bankMap);
    }

    private static IReadOnlyDictionary<ushort, byte[]> BuildGbcPreviewGlyphMap(GbcFontPackageInfo fontPackage, string? explicitFontPath)
    {
        var glyphMap = new Dictionary<ushort, byte[]>(fontPackage.OrderedCodepoints.Length + 96);
        for (var i = 0; i < fontPackage.OrderedCodepoints.Length; i++)
        {
            var codepoint = fontPackage.OrderedCodepoints[i];
            var bitmapOffset = GbcFontHeaderSize + (fontPackage.OrderedCodepoints.Length * 4) + (i * fontPackage.Format.GlyphBytes);
            var glyphBytes = GC.AllocateUninitializedArray<byte>(fontPackage.Format.GlyphBytes);
            Buffer.BlockCopy(fontPackage.Bytes, bitmapOffset, glyphBytes, 0, fontPackage.Format.GlyphBytes);
            glyphMap[codepoint] = glyphBytes;
        }

        var fontPath = GbcFontService.FindFontPath(explicitFontPath);
        if (string.IsNullOrWhiteSpace(fontPath))
        {
            fontPath = fontPackage.SourcePath;
        }

        if (!string.IsNullOrWhiteSpace(fontPath) && File.Exists(fontPath))
        {
            var extraCodepoints = new HashSet<int>(Enumerable.Range(0x20, 0x7F - 0x20));
            foreach (var text in GbcMenuLayerBuilder.GetPreviewUiTexts())
            {
                foreach (var ch in text)
                {
                    if (!char.IsSurrogate(ch))
                    {
                        extraCodepoints.Add(ch);
                    }
                }
            }

            var asciiCache = GbcFontService.BuildGlyphCacheDetailed(extraCodepoints, fontPath);
            GbcMenuLayerBuilder.ApplyMenuPunctuationOverrides(asciiCache.Glyphs, asciiCache.Format);
            foreach (var (codepoint, glyphBytes) in asciiCache.Glyphs)
            {
                if (codepoint >= 0x20)
                {
                    glyphMap[(ushort)codepoint] = glyphBytes;
                }
            }
        }

        return glyphMap;
    }

    private static GbcMenuLayerBuilder.GbcMenuLayerResult BuildGbcMenuLayerForPreview(
        IReadOnlyList<PreparedGbcGame> includedGames,
        string? explicitFontPath)
    {
        var layerFontPath = ResolveRequiredGbcMenuLayerFontPath(explicitFontPath);
        return GbcMenuLayerBuilder.Build(
            includedGames.Select(g => new GbcMenuLayerBuilder.GbcMenuGameInfo(
                TitleCn: g.Title,
                MbcType: g.MbcType,
                RomBanks: g.AllocatedRomBanks,
                RamBanks: g.RamBankCount,
                UsesDedicatedSave: g.UsesDedicatedSaveAllocation)).ToList(),
            layerFontPath);
    }

    private static string ResolveGbcPreviewDisplayTitle(PreparedGbcGame game, string languageCode) =>
        IsGbcChineseLanguage(languageCode)
            ? string.IsNullOrWhiteSpace(game.Title)
                ? "UNKNOWN"
                : game.Title.Trim()
            : EncodeGbcEntryAsciiName(game);

    private static bool InferGbcUsesDedicatedSaveAllocation(IReadOnlyList<byte> sramBankMap, int ramBankCount)
    {
        if (ramBankCount <= 0 || sramBankMap.Count == 0)
        {
            return false;
        }

        var firstMappedBank = sramBankMap[0];
        return firstMappedBank < GbcIndependentSramBankCapacity;
    }

    private static string BuildGbcPreviewSecondaryInfo(PreparedGbcGame game)
    {
        var saveText = game.RamBankCount <= 0
            ? "No save"
            : game.UsesDedicatedSaveAllocation
                ? "Dedicated save"
                : "Shared save";
        return $"MBC 0x{game.MbcType:X2}  RAM {game.RamBankCount} bank(s)  {saveText}";
    }

    private static string BuildGbaPreviewSecondaryInfo(PreparedGame game)
    {
        var saveSlotText = game.SaveSlotIndex.HasValue ? $"槽位 {game.SaveSlotIndex.Value + 1}" : "无独立槽位";
        return $"Font {game.TitleFont}  Block 0x{game.BlockCount:X}  {saveSlotText}";
    }

    private static void UpdateBankMap(IList<char> bankMap, int startBank, int bankCount, char marker)
    {
        if (bankCount <= 0 || startBank < 0 || startBank >= bankMap.Count)
        {
            return;
        }

        var endBankExclusive = Math.Min(bankMap.Count, startBank + bankCount);
        bankMap[startBank] = char.ToUpperInvariant(marker);
        for (var index = startBank + 1; index < endBankExclusive; index++)
        {
            bankMap[index] = marker;
        }
    }

    private static long RoundUpLong(long value, int alignment)
    {
        if (alignment <= 0)
        {
            return value;
        }

        var remainder = value % alignment;
        return remainder == 0 ? value : value + (alignment - remainder);
    }

    public static void WriteAscii(Span<byte> destination, int offset, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        var count = Math.Min(bytes.Length, destination.Length - offset);
        bytes.AsSpan(0, count).CopyTo(destination.Slice(offset, count));
    }

    public static void WriteUInt16(Span<byte> destination, int offset, ushort value)
    {
        destination[offset] = (byte)(value & 0xFF);
        destination[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    public static void WriteUInt32(Span<byte> destination, int offset, uint value)
    {
        destination[offset] = (byte)(value & 0xFF);
        destination[offset + 1] = (byte)((value >> 8) & 0xFF);
        destination[offset + 2] = (byte)((value >> 16) & 0xFF);
        destination[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    private static void ValidateRequest(BuildRequest request, Func<string, string, string, string>? textResolver)
    {
        if (string.IsNullOrWhiteSpace(request.BaseMenuPath) || !File.Exists(request.BaseMenuPath))
        {
            throw new InvalidOperationException(T(textResolver, "error", "base_menu_not_found", "Base menu ROM was not found."));
        }

        if (string.IsNullOrWhiteSpace(request.OutputPath))
        {
            throw new InvalidOperationException(T(textResolver, "error", "output_path_required", "Output path is required."));
        }

        if (request.Games.Count == 0)
        {
            throw new InvalidOperationException(T(textResolver, "error", "add_one_rom", "Add at least one ROM before building."));
        }

        var enabledGames = request.Games.Count(static game => game.Enabled);
        if (enabledGames > GbaMenuMaxEntries)
        {
            throw new InvalidOperationException(
                Tf(textResolver, "error", "gba_too_many_games", "The GBA menu supports at most {0} ROM entries.", GbaMenuMaxEntries));
        }

        if (IsCoverMenu(request.GbaMenuType))
        {
            var menuBytes = File.ReadAllBytes(request.BaseMenuPath);
            if (!HasNsMenuSignature(menuBytes, request.GbaMenuType))
            {
                throw new InvalidOperationException(
                    T(textResolver, "error", "ns_menu_signature_missing", "The selected base menu ROM is not an NS menu build."));
            }
        }

        ValidateFixedSaveSlots(request, textResolver);
        foreach (var game in request.Games.Where(static game => game.Enabled))
        {
            if (string.IsNullOrWhiteSpace(game.RomPath) || !File.Exists(game.RomPath))
            {
                throw new InvalidOperationException(Tf(textResolver, "error", "rom_not_found", "ROM not found: {0}", game.RomPath));
            }
        }
    }

    private static void ValidateMinimalMenuPackageRequest(
        BuildRequest request,
        Func<string, string, string, string>? textResolver)
    {
        if (request.Family != CompilationFamily.Gba || !IsCoverMenu(request.GbaMenuType))
        {
            throw new InvalidOperationException(
                T(textResolver, "error", "minimal_menu_ns_only", "Minimal menu packages are only available for NS menus."));
        }
        if (string.IsNullOrWhiteSpace(request.BaseMenuPath) || !File.Exists(request.BaseMenuPath))
        {
            throw new InvalidOperationException(T(textResolver, "error", "base_menu_not_found", "Base menu ROM was not found."));
        }
        if (string.IsNullOrWhiteSpace(request.OutputPath))
        {
            throw new InvalidOperationException(T(textResolver, "error", "output_path_required", "Output path is required."));
        }

        var enabledGames = request.Games.Where(static game => game.Enabled).ToList();
        if (enabledGames.Count == 0)
        {
            throw new InvalidOperationException(T(textResolver, "error", "no_enabled_roms", "No enabled ROMs were provided."));
        }
        if (enabledGames.Count > GbaMenuMaxEntries)
        {
            throw new InvalidOperationException(
                Tf(textResolver, "error", "gba_too_many_games", "The GBA menu supports at most {0} ROM entries.", GbaMenuMaxEntries));
        }

        var menuBytes = File.ReadAllBytes(request.BaseMenuPath);
        if (!HasNsMenuSignature(menuBytes, request.GbaMenuType))
        {
            throw new InvalidOperationException(
                T(textResolver, "error", "ns_menu_signature_missing", "The selected base menu ROM is not an NS menu build."));
        }
        foreach (var game in enabledGames)
        {
            if (string.IsNullOrWhiteSpace(game.RomPath) || !File.Exists(game.RomPath))
            {
                throw new InvalidOperationException(Tf(textResolver, "error", "rom_not_found", "ROM not found: {0}", game.RomPath));
            }
        }
    }

    private static bool HasNsMenuSignature(byte[] menuRom, GbaMenuType menuType)
    {
        var signature = menuType == GbaMenuType.SimplifiedNsMenu
            ? "SNSF"u8.ToArray()
            : "UMMF"u8.ToArray();
        return FindSequence(menuRom, signature) >= 0;
    }

    private static byte[] PrepareMenuRom(string baseMenuPath)
    {
        var menuRom = File.ReadAllBytes(baseMenuPath);
        var alignedLength = menuRom.Length + ((0x10 - (menuRom.Length % 0x10)) % 0x10);
        Array.Resize(ref menuRom, alignedLength + 0x20);
        for (var i = alignedLength; i < menuRom.Length; i++)
        {
            menuRom[i] = 0xFF;
        }

        var timestampBytes = Encoding.ASCII.GetBytes(DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"));
        var timestampOffset = menuRom.Length - 0x20;
        Buffer.BlockCopy(timestampBytes, 0, menuRom, timestampOffset, Math.Min(timestampBytes.Length, 0x20));
        return menuRom;
    }

    private static void ApplyBackgroundImage(
        byte[] menuRom,
        string backgroundPath,
        bool usesWlLayout,
        Func<string, string, string, string>? textResolver)
    {
        byte[] bitmapData;
        byte[] paletteData;
        try
        {
            (bitmapData, paletteData) = BackgroundImageEncoder.Encode(backgroundPath);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("240x160", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(T(
                textResolver,
                "error",
                "background_invalid_size",
                "Background image must be exactly 240x160."));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("240 colors", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(T(
                textResolver,
                "error",
                "background_too_many_colors",
                "Background image uses more than 240 colors."));
        }

        if (!GbaMenuBackgroundLocator.TryFind(menuRom, out int backgroundOffset))
        {
            throw new InvalidOperationException(T(
                textResolver,
                "error",
                "background_marker_not_found",
                "Could not locate background data inside the base menu ROM."));
        }

        Buffer.BlockCopy(bitmapData, 0, menuRom, backgroundOffset, bitmapData.Length);
        Buffer.BlockCopy(paletteData, 0, menuRom, backgroundOffset + 0x9600, paletteData.Length);
    }

    private PreparedGame PrepareGame(
        BuildGameEntry game,
        int index,
        CartridgeDefinition cartridge,
        int minRomSize,
        int sectorSize,
        FlashCartMode flashCartMode,
        bool batteryPresent,
        bool ignoreSramPatchFailure,
        Func<string, string, string, string>? textResolver)
    {
        // NS menu values were persisted by older releases as flash-cart modes.
        // They are menu selections now and must never change GBA preparation rules.
        flashCartMode = NormalizeGbaFlashCartMode(flashCartMode);
        var romPath = Path.GetFullPath(game.RomPath);
        var romInfo = new FileInfo(romPath);
        var size = checked((int)romInfo.Length);
        byte[]? patchedRomData = null;
        string? sramPatchIdentifier = null;
        var batterylessPatched = false;
        BatterylessPatchService.BatterylessPatchDebugInfo? batterylessDebugInfo = null;
        var shouldAutoApplyBatterylessPatch = ShouldAutoApplyBatterylessPatch(game, flashCartMode, batteryPresent);
        var shouldReserveBatterylessRomSize = ShouldReserveBatterylessRomSize(game, flashCartMode, batteryPresent);

        if (game.ApplySramPatch || shouldReserveBatterylessRomSize || size < BatterylessPatchService.MinimumPatchedRomSize)
        {
            var romData = File.ReadAllBytes(romPath);
            if (game.ApplySramPatch)
            {
                var appliedPatchIdentifier = _sramPatchService.DetectAppliedPatchTarget(romData);
                if (!string.IsNullOrWhiteSpace(appliedPatchIdentifier))
                {
                    sramPatchIdentifier = appliedPatchIdentifier;
                }
                else
                {
                    try
                    {
                        var patchResult = _sramPatchService.TryApply(romData);
                        if (patchResult.Patched)
                        {
                            romData = patchResult.RomData;
                            patchedRomData = romData;
                            sramPatchIdentifier = patchResult.PatchSetName;
                        }
                    }
                    catch (InvalidOperationException ex)
                    {
                        appliedPatchIdentifier = _sramPatchService.DetectAppliedPatchTarget(romData);
                        if (!string.IsNullOrWhiteSpace(appliedPatchIdentifier))
                        {
                            sramPatchIdentifier = appliedPatchIdentifier;
                        }
                        else if (ignoreSramPatchFailure)
                        {
                            romData = File.ReadAllBytes(romPath);
                        }
                        else
                        {
                            throw new InvalidOperationException(
                                Tf(
                                    textResolver,
                                    "error",
                                    "sram_patch_failed",
                                    "SRAM patch failed for {0}: {1}",
                                    Path.GetFileName(romPath),
                                    ex.Message),
                                ex);
                        }
                    }
                }
            }

            if (shouldAutoApplyBatterylessPatch)
            {
                try
                {
                    var batterylessResult = _batterylessPatchService.TryApply(romData, cartridge);
                    if (batterylessResult.Patched)
                    {
                        romData = batterylessResult.RomData;
                        patchedRomData = romData;
                        batterylessPatched = true;
                        batterylessDebugInfo = batterylessResult.DebugInfo;
                    }
                    else if (batterylessResult.AlreadyPatched)
                    {
                        shouldReserveBatterylessRomSize = true;
                    }
                }
                catch (InvalidOperationException ex)
                {
                    if (!ignoreSramPatchFailure)
                    {
                        throw new InvalidOperationException(
                            Tf(
                                textResolver,
                                "error",
                                "batteryless_patch_failed",
                                "Batteryless patch failed for {0}: {1}",
                                Path.GetFileName(romPath),
                                ex.Message),
                            ex);
                    }
                }
            }

            var effectiveMinRomSize = shouldReserveBatterylessRomSize || _batterylessPatchService.HasKnownMarker(romData)
                ? Math.Max(minRomSize, BatterylessPatchService.MinimumPatchedRomSize)
                : minRomSize;
            size = Math.Max(romData.Length, effectiveMinRomSize);
        }

        size = AlignUp(size, minRomSize);

        var saveSlotIndex = batteryPresent && game.SaveSlot.HasValue ? game.SaveSlot.Value - 1 : (int?)null;
        return new PreparedGame
        {
            Index = index,
            Title = game.Title,
            RomPath = romPath,
            Size = size,
            SectorCount = size / sectorSize,
            PlacementAlignmentSectorCount = game.Map256m
                ? DivCeil(32 * 1024 * 1024, sectorSize)
                : Math.Max(1, DivCeil(minRomSize, sectorSize)),
            HiddenKeysMask = game.HiddenKeysMask,
            Map256m = game.Map256m,
            TitleFont = Math.Clamp(game.TitleFont, 1, 255),
            CoverPath = game.CoverPath,
            CoverCleared = game.CoverCleared,
            SaveSlotIndex = saveSlotIndex,
            SaveType = batteryPresent && saveSlotIndex.HasValue ? 2 : 0,
            PatchedRomData = patchedRomData,
            SramPatchIdentifier = sramPatchIdentifier,
            BatterylessPatched = batterylessPatched,
            BatterylessDebugInfo = batterylessDebugInfo,
        };
    }

    private static PreparedGame PrepareGameQuick(
        BuildGameEntry game,
        int index,
        int minRomSize,
        int sectorSize,
        FlashCartMode flashCartMode,
        bool batteryPresent,
        Func<string, string, string, string>? textResolver)
    {
        flashCartMode = NormalizeGbaFlashCartMode(flashCartMode);
        var romPath = Path.GetFullPath(game.RomPath);
        if (!File.Exists(romPath))
        {
            throw new InvalidOperationException(Tf(textResolver, "error", "rom_not_found", "ROM not found: {0}", game.RomPath));
        }

        var size = checked((int)new FileInfo(romPath).Length);
        var shouldAutoApplyBatterylessPatch = ShouldAutoApplyBatterylessPatch(game, flashCartMode, batteryPresent);
        var shouldReserveBatterylessRomSize = ShouldReserveBatterylessRomSize(game, flashCartMode, batteryPresent);
        var effectiveMinRomSize = shouldReserveBatterylessRomSize
            ? Math.Max(minRomSize, BatterylessPatchService.MinimumPatchedRomSize)
            : minRomSize;
        if (shouldAutoApplyBatterylessPatch && game.BatterylessRequiresExpansion)
        {
            size = checked(size + BatterylessPatchService.ExpansionSize);
        }

        size = AlignUp(Math.Max(size, effectiveMinRomSize), minRomSize);

        var saveSlotIndex = batteryPresent && game.SaveSlot.HasValue ? game.SaveSlot.Value - 1 : (int?)null;
        return new PreparedGame
        {
            Index = index,
            Title = game.Title,
            RomPath = romPath,
            Size = size,
            SectorCount = size / sectorSize,
            PlacementAlignmentSectorCount = game.Map256m
                ? DivCeil(32 * 1024 * 1024, sectorSize)
                : Math.Max(1, DivCeil(minRomSize, sectorSize)),
            HiddenKeysMask = game.HiddenKeysMask,
            Map256m = game.Map256m,
            TitleFont = Math.Clamp(game.TitleFont, 1, 255),
            CoverPath = game.CoverPath,
            CoverCleared = game.CoverCleared,
            SaveSlotIndex = saveSlotIndex,
            SaveType = batteryPresent && saveSlotIndex.HasValue ? 2 : 0,
        };
    }

    private static string FormatPreparedRomMessage(
        PreparedGame prepared,
        Func<string, string, string, string>? textResolver)
    {
        var batterylessLogSuffix = prepared.BatterylessDebugInfo is null
            ? string.Empty
            : FormatBatterylessLogSuffix(prepared.BatterylessDebugInfo, textResolver);

        if (!string.IsNullOrWhiteSpace(prepared.SramPatchIdentifier) && prepared.BatterylessPatched)
        {
            return Tf(
                textResolver,
                "build",
                "prepared_rom_sram_batteryless_patched",
                "Prepared: {0} (SRAM patched: {1}, batteryless patched)",
                prepared.Title,
                prepared.SramPatchIdentifier) + batterylessLogSuffix;
        }

        if (!string.IsNullOrWhiteSpace(prepared.SramPatchIdentifier))
        {
            return Tf(
                textResolver,
                "build",
                "prepared_rom_sram_patched",
                "Prepared: {0} (SRAM patched: {1})",
                prepared.Title,
                prepared.SramPatchIdentifier);
        }

        if (prepared.BatterylessPatched)
        {
            return Tf(
                textResolver,
                "build",
                "prepared_rom_batteryless_patched",
                "Prepared: {0} (batteryless patched)",
                prepared.Title) + batterylessLogSuffix;
        }

        return Tf(textResolver, "build", "prepared_rom", "Prepared: {0}", prepared.Title);
    }

    private static string FormatBatterylessLogSuffix(
        BatterylessPatchService.BatterylessPatchDebugInfo debugInfo,
        Func<string, string, string, string>? textResolver) =>
        Tf(
            textResolver,
            "build",
            "batteryless_log_suffix",
            " [batteryless: flash={0}({1}), save=0x{2:X}, payload=0x{3:X8}, variant={4}]",
            debugInfo.FlashProfileName,
            debugInfo.FlashProfile,
            debugInfo.SaveSize,
            debugInfo.PayloadBase,
            debugInfo.PayloadVariantName);

    private static bool ShouldAutoApplyBatterylessPatch(
        BuildGameEntry game,
        FlashCartMode flashCartMode,
        bool batteryPresent)
    {
        if (!ShouldIncludeBatterylessPatchCapacity(flashCartMode, batteryPresent) ||
            !game.ApplyBatterylessPatch ||
            !game.SramReadyForBatteryless)
        {
            return false;
        }

        return game.SaveTypeKind != DetectedSaveTypeKind.None;
    }

    private static bool ShouldReserveBatterylessRomSize(
        BuildGameEntry game,
        FlashCartMode flashCartMode,
        bool batteryPresent)
    {
        if (!ShouldIncludeBatterylessPatchCapacity(flashCartMode, batteryPresent))
        {
            return false;
        }

        if (game.SaveTypeKind == DetectedSaveTypeKind.None)
        {
            return false;
        }

        return game.BatterylessAlreadyPatched || (game.SramReadyForBatteryless && game.ApplyBatterylessPatch);
    }

    private static bool ShouldIncludeBatterylessPatchCapacity(
        FlashCartMode flashCartMode,
        bool batteryPresent) =>
        !batteryPresent &&
        (flashCartMode.IsBatterylessMode() || flashCartMode.IsChm1Mode());

    private static bool TryPlaceGame(
        PreparedGame game,
        int placementStartSectorOffset,
        char[] sectorMap,
        byte[] compilation,
        CartridgeDefinition cart,
        ref bool bootLogoFound)
    {
        if (!TryReserveGamePlacement(game, placementStartSectorOffset, sectorMap, cart))
        {
            return false;
        }

        var romData = game.PatchedRomData ?? File.ReadAllBytes(game.RomPath);
        Buffer.BlockCopy(romData, 0, compilation, game.SectorOffset * cart.SectorSize, romData.Length);

        if (!bootLogoFound && HasValidBootLogo(romData))
        {
            Buffer.BlockCopy(romData, 0x04, compilation, 0x04, 0x9C);
            bootLogoFound = true;
        }

        return true;
    }

    private static bool TryReserveGamePlacement(
        PreparedGame game,
        int placementStartSectorOffset,
        IList<char> sectorMap,
        CartridgeDefinition cart)
    {
        var alignmentSectorCount = Math.Max(1, game.PlacementAlignmentSectorCount);
        var firstSector = DivCeil(placementStartSectorOffset, alignmentSectorCount) * alignmentSectorCount;
        for (var sector = firstSector; sector < sectorMap.Count; sector += alignmentSectorCount)
        {
            if (!IsFree(sectorMap, sector, game.SectorCount))
            {
                continue;
            }

            UpdateSectorMap(sectorMap, sector, game.SectorCount, 'r');
            game.SectorOffset = sector;
            game.BlockOffset = (sector * cart.SectorSize) / cart.BlockSize;
            game.BlockCount = ((game.Map256m ? alignmentSectorCount : game.SectorCount) * cart.SectorSize) / cart.BlockSize;
            return true;
        }

        return false;
    }

    private static void ReserveGamePlacementForEstimate(
        PreparedGame game,
        int placementStartSectorOffset,
        List<char> sectorMap,
        CartridgeDefinition cart)
    {
        var alignmentSectorCount = Math.Max(1, game.PlacementAlignmentSectorCount);
        var firstSector = DivCeil(placementStartSectorOffset, alignmentSectorCount) * alignmentSectorCount;
        for (var sector = firstSector; ; sector += alignmentSectorCount)
        {
            EnsureSectorMapLength(sectorMap, sector + game.SectorCount);
            if (!IsFree(sectorMap, sector, game.SectorCount))
            {
                continue;
            }

            UpdateSectorMap(sectorMap, sector, game.SectorCount, 'r');
            game.SectorOffset = sector;
            game.BlockOffset = (sector * cart.SectorSize) / cart.BlockSize;
            game.BlockCount = ((game.Map256m ? alignmentSectorCount : game.SectorCount) * cart.SectorSize) / cart.BlockSize;
            return;
        }
    }

    private static byte[] BuildItemList(
        IReadOnlyList<PreparedGame> games,
        IReadOnlyList<ushort> keyOrder,
        IReadOnlyDictionary<PreparedGame, byte>? coverIndexes = null)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        foreach (var key in keyOrder)
        {
            foreach (var game in games.Where(game => game.HiddenKeysMask == key))
            {
                var title = PrepareMenuTitle(game.Title);
                var titleBuffer = new byte[0x60];
                var titleBytes = Encoding.Unicode.GetBytes(title.PadRight(0x30, '\0'));
                Buffer.BlockCopy(titleBytes, 0, titleBuffer, 0, Math.Min(titleBytes.Length, titleBuffer.Length));

                writer.Write((byte)(game.TitleFont - 1));
                writer.Write((byte)title.Length);
                writer.Write((ushort)game.BlockOffset);
                writer.Write((ushort)game.BlockCount);
                writer.Write((byte)game.SaveType);
                writer.Write((byte)(game.SaveSlotIndex.GetValueOrDefault()));
                writer.Write(game.HiddenKeysMask);
                if (coverIndexes is not null && coverIndexes.TryGetValue(game, out var coverIndex))
                {
                    writer.Write(coverIndex);
                    writer.Write((byte)1);
                    writer.Write(new byte[4]);
                }
                else
                {
                    writer.Write((byte)0xFF);
                    writer.Write((byte)0);
                    writer.Write(new byte[4]);
                }
                writer.Write(titleBuffer);
            }
        }

        writer.Flush();
        return stream.ToArray();
    }

    private static List<PreparedGame> OrderGamesByHiddenKeys(
        IReadOnlyList<PreparedGame> games,
        IReadOnlyList<ushort> keyOrder)
    {
        var orderedGames = new List<PreparedGame>(games.Count);
        foreach (var key in keyOrder)
        {
            orderedGames.AddRange(games.Where(game => game.HiddenKeysMask == key));
        }

        return orderedGames;
    }

    private static string BuildGbaStatusDetails(
        int statusSectorOffset,
        CartridgeDefinition cart,
        bool batteryPresent)
    {
        var details = new StringBuilder();
        details.AppendLine($"Absolute Offset: 0x{statusSectorOffset * cart.SectorSize:X8}");
        details.AppendLine($"Sector Offset: {statusSectorOffset}");
        details.AppendLine("Magic [0x00..0x03]: KUMA");
        details.AppendLine($"Battery Flag [0x05]: 0x{(batteryPresent ? 0x01 : 0x00):X2}");
        details.AppendLine("Remaining bytes are currently zero-filled.");
        return details.ToString().TrimEnd();
    }

    private static string BuildGbaItemListDetails(
        IReadOnlyList<PreparedGame> includedGames,
        IReadOnlyList<ushort> keyOrder,
        int itemListOffsetBytes)
    {
        const int itemListEntrySize = 0x70;
        var orderedGames = new List<PreparedGame>(includedGames.Count);
        foreach (var key in keyOrder)
        {
            orderedGames.AddRange(includedGames.Where(game => game.HiddenKeysMask == key));
        }

        var details = new StringBuilder();
        details.AppendLine($"Entry Size: 0x{itemListEntrySize:X2} ({itemListEntrySize} bytes)");
        details.AppendLine($"Entry Count: {orderedGames.Count}");
        details.AppendLine($"Key Order: {string.Join(", ", keyOrder.Select(key => $"0x{key:X4} ({FormatHiddenKeysMask(key)})"))}");
        if (orderedGames.Count == 0)
        {
            details.AppendLine("No item list entries.");
            return details.ToString().TrimEnd();
        }

        for (var index = 0; index < orderedGames.Count; index++)
        {
            var game = orderedGames[index];
            var title = PrepareMenuTitle(game.Title);
            var rawSaveSlot = game.SaveSlotIndex.GetValueOrDefault();
            details.AppendLine($"[{index:D2}] 0x{itemListOffsetBytes + (index * itemListEntrySize):X8}  Title=\"{title}\"");
            details.AppendLine($"     Title Font Byte: 0x{game.TitleFont - 1:X2}");
            details.AppendLine($"     Title Length Byte: 0x{title.Length:X2} ({title.Length})");
            details.AppendLine($"     Block Offset: 0x{game.BlockOffset:X4}");
            details.AppendLine($"     Block Count: 0x{game.BlockCount:X4}");
            details.AppendLine($"     Save Type Byte: 0x{game.SaveType:X2}");
            details.AppendLine($"     Save Slot Byte: 0x{rawSaveSlot:X2} ({(game.SaveSlotIndex.HasValue ? $"menu slot {game.SaveSlotIndex.Value + 1}" : "none")})");
            details.AppendLine($"     Hidden Keys: 0x{game.HiddenKeysMask:X4} ({FormatHiddenKeysMask(game.HiddenKeysMask)})");
        }

        return details.ToString().TrimEnd();
    }

    private static string BuildGbcConfigHeaderDetails(BuildRequest request, int romCount)
    {
        var languageCode = GetGbcBuildLanguageCode(request);
        var languageFlag = IsGbcChineseLanguage(languageCode) ? 1 : 0;
        var details = new StringBuilder();
        details.AppendLine("Signature [0x00..0x07]: MBCXCFG");
        details.AppendLine("Version [0x08..0x09]: 0x0001");
        details.AppendLine($"Batteryless Mode Flag [0x0A]: 0x{(request.BatteryPresent ? 0x00 : 0x01):X2}");
        details.AppendLine($"Language Flag [0x0B]: 0x{languageFlag:X2} ({languageCode})");
        details.AppendLine("Reserved [0x0C..0x0D]: 0x0000");
        details.AppendLine($"ROM Count [0x0E..0x0F]: {romCount}");
        details.AppendLine($"Directory Offset [0x10..0x13]: 0x{GbcRomDirectoryOffsetBytes:X8}");
        details.AppendLine($"Directory Span [0x14..0x17]: 0x{GbcFontOffsetBytes - GbcRomDirectoryOffsetBytes:X8}");
        details.AppendLine($"Font Offset [0x18..0x1B]: 0x{GbcFontOffsetBytes:X8}");
        details.AppendLine($"Font Span [0x1C..0x1F]: 0x{GbcFixedMapReservedBytes - GbcFontOffsetBytes:X8}");
        return details.ToString().TrimEnd();
    }

    private static string BuildGbcDirectoryHeaderDetails(int romCount)
    {
        var details = new StringBuilder();
        details.AppendLine("Signature [0x00..0x07]: MBCXROM");
        details.AppendLine("Version [0x08..0x09]: 0x0001");
        details.AppendLine($"ROM Count [0x0A..0x0B]: {romCount}");
        details.AppendLine($"Entry Size [0x0C..0x0D]: 0x{GbcRomEntrySize:X4}");
        details.AppendLine("Entry Version [0x0E..0x0F]: 0x0001");
        details.AppendLine($"Entries Offset [0x10..0x13]: 0x{GbcRomDirectoryOffsetBytes + GbcRomDirectoryHeaderSize:X8}");
        details.AppendLine($"Entries Span [0x14..0x17]: 0x{GbcFontOffsetBytes - GbcRomDirectoryOffsetBytes - GbcRomDirectoryHeaderSize:X8}");
        return details.ToString().TrimEnd();
    }

    private static string BuildGbcEntryDetails(
        IReadOnlyList<PreparedGbcGame> includedGames,
        int entriesOffsetBytes,
        bool useChineseMenu)
    {
        var details = new StringBuilder();
        details.AppendLine($"Entry Size: 0x{GbcRomEntrySize:X2} ({GbcRomEntrySize} bytes)");
        details.AppendLine($"Entry Count: {includedGames.Count}");
        if (includedGames.Count == 0)
        {
            details.AppendLine("No directory entries.");
            return details.ToString().TrimEnd();
        }

        for (var index = 0; index < includedGames.Count; index++)
        {
            var game = includedGames[index];
            details.AppendLine($"[{index:D2}] 0x{entriesOffsetBytes + (index * GbcRomEntrySize):X8}  Title=\"{game.Title}\"");
            details.AppendLine($"     Header Title: \"{game.HeaderTitle}\"");
            if (useChineseMenu)
            {
                var encodedNameCodes = EncodeGbcEntryName(game.Title, game.HeaderTitle);
                var encodedTitle = string.Join(" ", encodedNameCodes.Select(static code => $"0x{code:X4}"));
                details.AppendLine($"     Encoded Name Codes: {encodedTitle}");
            }
            else
            {
                details.AppendLine($"     Encoded ASCII Name: \"{EncodeGbcEntryAsciiName(game)}\"");
            }

            details.AppendLine($"     File Length: 0x{game.FileLength:X} ({game.FileLength} bytes)");
            details.AppendLine($"     Allocated Bytes: 0x{game.AllocatedBytes:X} ({game.AllocatedBytes} bytes)");
            details.AppendLine($"     ROM Offset Bank: 0x{game.RomOffsetBank:X4} (absolute 0x{game.RomOffsetBank * GameBoyRomHeaderService.RomBankSize:X8})");
            details.AppendLine($"     ROM Banks: {game.AllocatedRomBanks}");
            details.AppendLine($"     RAM Banks: {game.RamBankCount}");
            details.AppendLine($"     MBC Type Byte: 0x{game.MbcType:X2}");
            details.AppendLine($"     Has Battery Flag: {(game.HasBattery ? 1 : 0)}");
            details.AppendLine($"     Dedicated Save Allocation: {(game.UsesDedicatedSaveAllocation ? 1 : 0)}");
            details.AppendLine($"     SRAM Map: {FormatSramBankMap(game.SramBankMap)}");
        }

        return details.ToString().TrimEnd();
    }

    private static string BuildGbcFontPackageDetails(GbcFontPackageInfo fontPackage)
    {
        var details = new StringBuilder();
        details.AppendLine("Signature [0x00..0x07]: MBCXFNT");
        details.AppendLine("Version [0x08..0x09]: 0x0001");
        details.AppendLine($"Glyph Count [0x0A..0x0B]: {fontPackage.OrderedCodepoints.Length}");
        details.AppendLine($"Glyph Size Px [0x0C..0x0D]: 0x{fontPackage.Format.GlyphSizePx:X4} ({fontPackage.Format.GlyphSizePx})");
        details.AppendLine($"Glyph Bytes [0x0E..0x0F]: 0x{fontPackage.Format.GlyphBytes:X4} ({fontPackage.Format.GlyphBytes})");
        details.AppendLine($"Table Base [0x10..0x13]: 0x{fontPackage.TableBase:X8}");
        details.AppendLine($"Bitmap Base [0x14..0x17]: 0x{fontPackage.BitmapBase:X8}");
        details.AppendLine($"Table Span [0x18..0x1B]: 0x{fontPackage.OrderedCodepoints.Length * 4:X8}");
        details.AppendLine($"Bitmap Span [0x1C..0x1F]: 0x{fontPackage.OrderedCodepoints.Length * fontPackage.Format.GlyphBytes:X8}");
        details.AppendLine($"Source Path: {fontPackage.SourcePath}");
        details.AppendLine($"Missing Glyph Count: {fontPackage.MissingCodepoints.Length}");
        if (fontPackage.MissingCodepoints.Length > 0)
        {
            details.AppendLine($"Missing Glyphs: {FormatCodepointList(fontPackage.MissingCodepoints)}");
        }

        if (fontPackage.OrderedCodepoints.Length == 0)
        {
            details.AppendLine("Glyph Table: (none)");
            return details.ToString().TrimEnd();
        }

        details.AppendLine("Glyph Table:");
        for (var i = 0; i < fontPackage.OrderedCodepoints.Length; i++)
        {
            var codepoint = fontPackage.OrderedCodepoints[i];
            var entryOffset = GbcFontOffsetBytes + GbcFontHeaderSize + (i * 4);
            var bitmapOffset = fontPackage.BitmapBase + (i * fontPackage.Format.GlyphBytes);
            details.AppendLine(
                $"[{i:D3}] 0x{entryOffset:X8}  U+{codepoint:X4} '{FormatCodepointPreview(codepoint)}' -> glyph {i}, bitmap 0x{bitmapOffset:X8}");
        }

        return details.ToString().TrimEnd();
    }

    private static string FormatCodepointList(IReadOnlyList<ushort> codepoints) =>
        string.Join(", ", codepoints.Select(static codepoint => $"U+{codepoint:X4} '{FormatCodepointPreview(codepoint)}'"));

    private static string BuildGbcFontCharacterList(GbcFontPackageInfo fontPackage)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Glyph Count: {fontPackage.OrderedCodepoints.Length}");
        builder.AppendLine($"Source Path: {fontPackage.SourcePath}");
        builder.AppendLine();
        builder.AppendLine("Characters:");

        for (var i = 0; i < fontPackage.OrderedCodepoints.Length; i++)
        {
            var codepoint = fontPackage.OrderedCodepoints[i];
            builder.AppendLine($"[{i:D3}] U+{codepoint:X4} '{FormatCodepointPreview(codepoint)}'");
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatCodepointPreview(ushort codepoint)
    {
        if (codepoint is >= 0x20 and <= 0x7E)
        {
            return ((char)codepoint).ToString();
        }

        return char.ConvertFromUtf32(codepoint);
    }

    private static string FormatHiddenKeysMask(ushort mask)
    {
        if (mask == 0)
        {
            return "None";
        }

        var names = new List<string>(10);
        if ((mask & (1 << 0)) != 0) names.Add("A");
        if ((mask & (1 << 1)) != 0) names.Add("B");
        if ((mask & (1 << 2)) != 0) names.Add("SELECT");
        if ((mask & (1 << 3)) != 0) names.Add("START");
        if ((mask & (1 << 4)) != 0) names.Add("RIGHT");
        if ((mask & (1 << 5)) != 0) names.Add("LEFT");
        if ((mask & (1 << 6)) != 0) names.Add("UP");
        if ((mask & (1 << 7)) != 0) names.Add("DOWN");
        if ((mask & (1 << 8)) != 0) names.Add("R");
        if ((mask & (1 << 9)) != 0) names.Add("L");
        return names.Count == 0 ? $"0x{mask:X4}" : string.Join("+", names);
    }

    private static string FormatSramBankMap(IReadOnlyList<byte> sramBankMap)
    {
        var mappings = sramBankMap
            .Select((mappedBank, localBank) => new { mappedBank, localBank })
            .Where(static entry => entry.mappedBank != 0xFF)
            .Select(entry => $"{entry.localBank}->{entry.mappedBank}");
        return mappings.Any()
            ? string.Join(", ", mappings)
            : "shared / unused";
    }

    private static string FormatWrappedMap(string map, int lineLength)
    {
        if (string.IsNullOrEmpty(map))
        {
            return string.Empty;
        }

        var wrapped = new StringBuilder();
        for (var offset = 0; offset < map.Length; offset += lineLength)
        {
            wrapped.AppendLine(map.Substring(offset, Math.Min(lineLength, map.Length - offset)));
        }

        return wrapped.ToString().TrimEnd();
    }

    private static string PrepareMenuTitle(string title)
    {
        title = string.IsNullOrWhiteSpace(title) ? "Untitled" : title.Trim();
        if (title.Length > 0x30)
        {
            title = title[..0x2F] + "\u2026";
        }

        return title;
    }

    private static string ComputeRomCode(byte[] status, byte[] itemList)
    {
        var data = new byte[status.Length + itemList.Length];
        Buffer.BlockCopy(status, 0, data, 0, status.Length);
        Buffer.BlockCopy(itemList, 0, data, status.Length, itemList.Length);
        var hash = SHA1.HashData(data);
        var suffix = Convert.ToHexString(hash)[..3];
        return $"L{suffix}";
    }

    private static void WriteHeaderMetadata(byte[] compilation, string romCode)
    {
        var romCodeBytes = Encoding.ASCII.GetBytes(romCode);
        Buffer.BlockCopy(romCodeBytes, 0, compilation, 0xAC, romCodeBytes.Length);

        var checksum = 0;
        for (var i = 0xA0; i < 0xBD; i++)
        {
            checksum -= compilation[i];
        }

        checksum = (checksum - 0x19) & 0xFF;
        compilation[0xBD] = (byte)checksum;
    }

    private static int GetOutputRomSize(IReadOnlyList<char> sectorMap, int sectorSize)
    {
        for (var i = sectorMap.Count - 1; i >= 0; i--)
        {
            if (sectorMap[i] != '.')
            {
                return (i + 1) * sectorSize;
            }
        }

        return 0;
    }

    private static void WriteOutput(
        string outputPath,
        bool splitOutput,
        byte[] compilation,
        int romSize,
        int flashSize,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullOutputPath = Path.GetFullPath(outputPath);
        var outputDirectory = Path.GetDirectoryName(fullOutputPath)!;
        Directory.CreateDirectory(outputDirectory);

        const int splitSize = 0x2000000;
        const int writeChunkSize = 0x100000;
        var pendingFiles = new List<(string TemporaryPath, string OutputPath)>();
        try
        {
            var totalParts = splitOutput ? DivCeil(flashSize, splitSize) : 1;
            for (var part = 0; part < totalParts; part++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pos = splitOutput ? part * splitSize : 0;
                if (pos >= romSize)
                {
                    break;
                }

                var size = splitOutput ? Math.Min(splitSize, romSize - pos) : romSize;
                var partPath = splitOutput
                    ? Path.Combine(outputDirectory,
                        $"{Path.GetFileNameWithoutExtension(fullOutputPath)}_part{part}{Path.GetExtension(fullOutputPath)}")
                    : fullOutputPath;
                var temporaryPath = Path.Combine(outputDirectory, $".chis-build-{Guid.NewGuid():N}.tmp");
                pendingFiles.Add((temporaryPath, partPath));

                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    for (var written = 0; written < size; written += writeChunkSize)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        stream.Write(compilation, pos + written, Math.Min(writeChunkSize, size - written));
                    }
                }
            }

            // Finish all cancellable writes before publishing any output, so cancellation
            // leaves neither partial ROMs nor completed parts from an abandoned build.
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var file in pendingFiles)
            {
                File.Move(file.TemporaryPath, file.OutputPath, overwrite: true);
            }

            if (splitOutput)
            {
                RemoveStaleSplitOutputParts(fullOutputPath, pendingFiles.Count);
            }
        }
        finally
        {
            foreach (var file in pendingFiles)
            {
                File.Delete(file.TemporaryPath);
            }
        }
    }

    private static void RemoveStaleSplitOutputParts(string fullOutputPath, int partCount)
    {
        var directory = Path.GetDirectoryName(fullOutputPath)!;
        var prefix = Path.GetFileNameWithoutExtension(fullOutputPath) + "_part";
        var extension = Path.GetExtension(fullOutputPath);
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            var fileName = Path.GetFileName(path);
            if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var suffixLength = fileName.Length - prefix.Length - extension.Length;
            if (suffixLength <= 0 ||
                !int.TryParse(fileName.AsSpan(prefix.Length, suffixLength), NumberStyles.None, CultureInfo.InvariantCulture, out var part) ||
                part < partCount ||
                !string.Equals(fileName, $"{prefix}{part}{extension}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            File.Delete(path);
        }
    }

    private static bool HasValidBootLogo(byte[] data)
    {
        if (data.Length < 0xA0)
        {
            return false;
        }

        var header = data.AsSpan(0x04, 0x9C);
        var hash = SHA1.HashData(header);
        return hash.SequenceEqual(BootLogoSha1);
    }

    private static int FindSequence(byte[] data, byte[] needle)
    {
        for (var i = 0; i <= data.Length - needle.Length; i++)
        {
            var found = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (data[i + j] == needle[j])
                {
                    continue;
                }

                found = false;
                break;
            }

            if (found)
            {
                return i;
            }
        }

        return -1;
    }

    private static void RethrowParallelPreparationException(AggregateException aggregateException)
    {
        var flattened = aggregateException.Flatten();
        var firstMeaningfulException = flattened.InnerExceptions
            .FirstOrDefault(static ex => ex is not OperationCanceledException)
            ?? flattened.InnerExceptions.FirstOrDefault();

        if (firstMeaningfulException is not null)
        {
            ExceptionDispatchInfo.Capture(firstMeaningfulException).Throw();
        }
    }

    private static int AlignUp(int value, int alignment) => checked(DivCeil(value, alignment) * alignment);

    private static bool IsFree(IList<char> sectorMap, int start, int length)
    {
        if (start + length > sectorMap.Count)
        {
            return false;
        }

        for (var i = start; i < start + length; i++)
        {
            if (sectorMap[i] != '.')
            {
                return false;
            }
        }

        return true;
    }

    private static void UpdateSectorMap(IList<char> sectorMap, int start, int length, char marker)
    {
        sectorMap[start] = char.ToUpperInvariant(marker);
        for (var i = start + 1; i < start + length; i++)
        {
            sectorMap[i] = marker;
        }
    }

    private static void EnsureSectorMapLength(List<char> sectorMap, int requiredLength)
    {
        while (sectorMap.Count < requiredLength)
        {
            sectorMap.Add('.');
        }
    }

    private static int DivCeil(int value, int divisor) => (value / divisor) + ((value % divisor) != 0 ? 1 : 0);

    private static FlashCartMode NormalizeGbaFlashCartMode(FlashCartMode mode) =>
        mode is FlashCartMode.NsMenu or FlashCartMode.SimplifiedNsMenu
            ? FlashCartMode.NoBattery
            : mode;

    private static bool IsCoverMenu(GbaMenuType menuType) =>
        menuType is GbaMenuType.NsMenu or GbaMenuType.SimplifiedNsMenu;

    private static int AlignToNextBlock(int value, int blockSize) => value + (blockSize - (value % blockSize));

    internal static int GetMaxBatterySaveSlotCount(CartridgeDefinition cart, FlashCartMode mode = FlashCartMode.WithBattery)
    {
        if (mode.IsChm1Mode()) return Chm1Layout.SaveSlotCount;
        // The UI does not yet have a selected base ROM at this point. Use the
        // canonical GBA menu head size, which is also the NS prepared head size.
        var itemListOffset = DivCeil(AlignToNextBlock(0x0B0000, 0x40000), cart.SectorSize);
        return GetMaxBatterySaveSlotCount(cart, itemListOffset + 1);
    }

    private static int GetMaxBatterySaveSlotCount(CartridgeDefinition cart, int statusOffset)
    {
        var saveDataSectorOffset = GetSaveDataStartSectorOffset(statusOffset);
        var systemReservedSectorCount = GetSystemReservedSectorCount(cart, saveDataSectorOffset);
        return Math.Max(0, systemReservedSectorCount - saveDataSectorOffset);
    }

    private static int GetSaveDataStartSectorOffset(int statusOffset) =>
        checked(statusOffset + 1);

    private static int GetSystemReservedSectorCount(
        CartridgeDefinition cart,
        int minimumReservedSectorCount)
    {
        var reservedBytes = Math.Max(minimumReservedSectorCount * cart.SectorSize, FixedSystemReservedBytes);
        return DivCeil(reservedBytes, cart.SectorSize);
    }

    private static void WriteGbcMenuLayer(byte[] compilation, IReadOnlyList<PreparedGbcGame> includedGames, string fontPath)
    {
        var gameInfos = includedGames.Select(g => new GbcMenuLayerBuilder.GbcMenuGameInfo(
            TitleCn: g.Title,
            MbcType: g.MbcType,
            RomBanks: g.AllocatedRomBanks,
            RamBanks: g.RamBankCount,
            UsesDedicatedSave: g.UsesDedicatedSaveAllocation
        )).ToList();

        var layer = GbcMenuLayerBuilder.Build(gameInfos, fontPath);

        var layerHeaderOffset = GbcConfigOffsetBytes + GbcMenuLayerBuilder.MenuLayerHeaderOffset;
        Buffer.BlockCopy(layer.LayerHeader, 0, compilation, layerHeaderOffset, layer.LayerHeader.Length);

        var listStringsOffset = GbcConfigOffsetBytes + GbcMenuLayerBuilder.MenuListStringsOffset;
        Buffer.BlockCopy(layer.ListStrings, 0, compilation, listStringsOffset, layer.ListStrings.Length);

        var uiTileDataOffset = GbcConfigOffsetBytes + GbcMenuLayerBuilder.MenuUiTileDataOffset;
        Buffer.BlockCopy(layer.UiTileData, 0, compilation, uiTileDataOffset, layer.UiTileData.Length);

        var topBarMapOffset = GbcConfigOffsetBytes + GbcMenuLayerBuilder.MenuTopBarMapOffset;
        Buffer.BlockCopy(layer.TopBarTilemap, 0, compilation, topBarMapOffset, layer.TopBarTilemap.Length);

        var bottomBarMapOffset = GbcConfigOffsetBytes + GbcMenuLayerBuilder.MenuBottomBarStaticMapOffset;
        Buffer.BlockCopy(layer.BottomBarStaticMap, 0, compilation, bottomBarMapOffset, layer.BottomBarStaticMap.Length);

        var detailTopBarMapOffset = GbcConfigOffsetBytes + GbcMenuLayerBuilder.MenuDetailTopBarMapOffset;
        Buffer.BlockCopy(layer.DetailTopBarTilemap, 0, compilation, detailTopBarMapOffset, layer.DetailTopBarTilemap.Length);

        var detailBottomBarMapOffset = GbcConfigOffsetBytes + GbcMenuLayerBuilder.MenuDetailBottomBarStaticMapOffset;
        Buffer.BlockCopy(layer.DetailBottomBarStaticMap, 0, compilation, detailBottomBarMapOffset, layer.DetailBottomBarStaticMap.Length);

        var compactGlyphOffset = GbcConfigOffsetBytes + GbcMenuLayerBuilder.MenuCompactGlyphDataOffset;
        Buffer.BlockCopy(layer.CompactGlyphData, 0, compilation, compactGlyphOffset, layer.CompactGlyphData.Length);

        var listTileCountsOffset = GbcConfigOffsetBytes + layer.DynamicOffsets["list_tile_counts_offset"];
        Buffer.BlockCopy(layer.ListTileCounts, 0, compilation, listTileCountsOffset, layer.ListTileCounts.Length);

        var listTileDataOffset = GbcConfigOffsetBytes + layer.DynamicOffsets["list_tile_data_offset"];
        Buffer.BlockCopy(layer.ListTileData, 0, compilation, listTileDataOffset, layer.ListTileData.Length);

        var listTilemapOffset = GbcConfigOffsetBytes + layer.DynamicOffsets["list_tilemap_offset"];
        Buffer.BlockCopy(layer.ListTilemaps, 0, compilation, listTilemapOffset, layer.ListTilemaps.Length);

        var detailTileCountsOffset = GbcConfigOffsetBytes + layer.DynamicOffsets["detail_tile_counts_offset"];
        Buffer.BlockCopy(layer.DetailTileCounts, 0, compilation, detailTileCountsOffset, layer.DetailTileCounts.Length);

        var detailTileDataOffset = GbcConfigOffsetBytes + layer.DynamicOffsets["detail_tile_data_offset"];
        Buffer.BlockCopy(layer.DetailTileData, 0, compilation, detailTileDataOffset, layer.DetailTileData.Length);

        var detailTilemapOffset = GbcConfigOffsetBytes + layer.DynamicOffsets["detail_tilemap_offset"];
        Buffer.BlockCopy(layer.DetailTilemaps, 0, compilation, detailTilemapOffset, layer.DetailTilemaps.Length);

        var titleScrollTableOffset = GbcConfigOffsetBytes + layer.DynamicOffsets["title_scroll_table_offset"];
        Buffer.BlockCopy(layer.TitleScrollTable, 0, compilation, titleScrollTableOffset, layer.TitleScrollTable.Length);

        var titleScrollDataOffset = GbcConfigOffsetBytes + layer.DynamicOffsets["title_scroll_data_offset"];
        Buffer.BlockCopy(layer.TitleScrollData, 0, compilation, titleScrollDataOffset, layer.TitleScrollData.Length);

        var maxLayerEnd = new[]
        {
            uiTileDataOffset + layer.UiTileData.Length,
            compactGlyphOffset + layer.CompactGlyphData.Length,
            topBarMapOffset + layer.TopBarTilemap.Length,
            bottomBarMapOffset + layer.BottomBarStaticMap.Length,
            detailTopBarMapOffset + layer.DetailTopBarTilemap.Length,
            detailBottomBarMapOffset + layer.DetailBottomBarStaticMap.Length,
            listTileCountsOffset + layer.ListTileCounts.Length,
            listTileDataOffset + layer.ListTileData.Length,
            listTilemapOffset + layer.ListTilemaps.Length,
            detailTileCountsOffset + layer.DetailTileCounts.Length,
            detailTileDataOffset + layer.DetailTileData.Length,
            detailTilemapOffset + layer.DetailTilemaps.Length,
            titleScrollTableOffset + layer.TitleScrollTable.Length,
            titleScrollDataOffset + layer.TitleScrollData.Length,
        }.Max();

        if (maxLayerEnd > GbcBackgroundTileDataOffsetBytes)
        {
            throw new InvalidOperationException(
                $"MBCXLYR1 layered data overlaps with the reserved MBCXBG1 background area. " +
                $"Layer data ends at: 0x{maxLayerEnd:X6}, background area starts at: 0x{GbcBackgroundTileDataOffsetBytes:X6}");
        }
    }

    private static void WriteGbcBackground(
        byte[] compilation,
        string? backgroundPath,
        Func<string, string, string, string>? textResolver)
    {
        if (string.IsNullOrWhiteSpace(backgroundPath))
        {
            return;
        }

        if (!File.Exists(backgroundPath))
        {
            throw new InvalidOperationException(
                Tf(
                    textResolver,
                    "error",
                    "gbc_background_not_found",
                    "GBC background image was not found: {0}",
                    backgroundPath));
        }

        var background = GbcBackgroundImageEncoder.Encode(backgroundPath);
        if (background.TileCount <= 0 || background.TileCount > GbcBackgroundImageEncoder.MaxTiles ||
            background.TileData.Length != background.TileCount * 16 ||
            background.Tilemap.Length != (GbcBackgroundImageEncoder.Width / 8) * (GbcBackgroundImageEncoder.Height / 8) ||
            background.Attributes.Length != background.Tilemap.Length ||
            background.PaletteData.Length != GbcBackgroundImageEncoder.PaletteCount * 8)
        {
            throw new InvalidOperationException("The generated MBCXBG1 background resource is invalid.");
        }

        if (GbcBackgroundTileDataOffsetBytes + background.TileData.Length > GbcBackgroundTilemapOffsetBytes ||
            GbcBackgroundTilemapOffsetBytes + background.Tilemap.Length > GbcBackgroundAttributesOffsetBytes ||
            GbcBackgroundAttributesOffsetBytes + background.Attributes.Length > GbcBackgroundPalettesOffsetBytes ||
            GbcBackgroundPalettesOffsetBytes + background.PaletteData.Length > GbcFontOffsetBytes)
        {
            throw new InvalidOperationException("The generated MBCXBG1 background resource exceeded its reserved area.");
        }

        Buffer.BlockCopy(background.TileData, 0, compilation, GbcBackgroundTileDataOffsetBytes, background.TileData.Length);
        Buffer.BlockCopy(background.Tilemap, 0, compilation, GbcBackgroundTilemapOffsetBytes, background.Tilemap.Length);
        Buffer.BlockCopy(background.Attributes, 0, compilation, GbcBackgroundAttributesOffsetBytes, background.Attributes.Length);
        Buffer.BlockCopy(background.PaletteData, 0, compilation, GbcBackgroundPalettesOffsetBytes, background.PaletteData.Length);

        var header = GC.AllocateUninitializedArray<byte>(GbcBackgroundHeaderSize);
        Array.Fill(header, (byte)0xFF);
        WriteAscii(header, 0, GbcBackgroundSignature);
        header[7] = 0;
        header[8] = 1;
        header[9] = checked((byte)background.TileCount);
        header[10] = GbcBackgroundImageEncoder.PaletteCount;
        header[11] = 0;
        header[12] = GbcBackgroundImageEncoder.Width / 8;
        header[13] = GbcBackgroundImageEncoder.Height / 8;
        WriteUInt16(header, 14, 0);
        WriteUInt32(header, 16, GbcBackgroundTileDataOffsetBytes - GbcConfigOffsetBytes);
        WriteUInt32(header, 20, GbcBackgroundTilemapOffsetBytes - GbcConfigOffsetBytes);
        WriteUInt32(header, 24, GbcBackgroundAttributesOffsetBytes - GbcConfigOffsetBytes);
        WriteUInt32(header, 28, GbcBackgroundPalettesOffsetBytes - GbcConfigOffsetBytes);
        Buffer.BlockCopy(header, 0, compilation, GbcBackgroundHeaderOffsetBytes, header.Length);
    }

    public static string ResolveRequiredGbcMenuLayerFontPath(string? explicitFontPath)
    {
        var fontPath = GbcFontService.FindFontPath(explicitFontPath);
        if (string.IsNullOrWhiteSpace(fontPath) || !File.Exists(fontPath))
        {
            throw new InvalidOperationException("Unable to resolve the required 12px GBC font for layered MBCX menu generation.");
        }

        return fontPath;
    }

    public static void EnsureGbcMenuLayerHeader(byte[] compilation)
    {
        if (!MatchesAscii(compilation, GbcConfigOffsetBytes + GbcMenuLayerBuilder.MenuLayerHeaderOffset, "MBCXLYR1"))
        {
            throw new InvalidOperationException("The GBC layered menu header MBCXLYR1 was not written to the output ROM.");
        }
    }

    private static bool MatchesAscii(byte[] buffer, int offset, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        if (offset < 0 || offset + bytes.Length > buffer.Length)
        {
            return false;
        }

        for (var i = 0; i < bytes.Length; i++)
        {
            if (buffer[offset + i] != bytes[i])
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidateFixedSaveSlots(
        BuildRequest request,
        Func<string, string, string, string>? textResolver)
    {
        if (request.FlashCartMode.IsChm1Mode() &&
            (request.MinRomSize != Chm1Layout.BucketBytes || request.Cartridge.SectorSize != Chm1Layout.SectorBytes ||
             request.Cartridge.FlashSize > Chm1Layout.MaxCapacity))
            throw new InvalidOperationException("01G/02G fragment management requires 4 MiB alignment, 128 KiB sectors, and at most 256 MiB NOR.");
        var menuRom = PrepareMenuRom(request.BaseMenuPath);
        if (request.FlashCartMode.IsChm1Mode() &&
            menuRom.AsSpan().IndexOf("WLMI"u8) < 0)
            throw new InvalidOperationException("01G/02G fragment management requires a WL or NS menu with bucket mapping support.");
        if (!request.BatteryPresent)
        {
            return;
        }
        var itemListOffset = DivCeil(AlignToNextBlock(menuRom.Length, 0x40000), request.Cartridge.SectorSize);
        var maxSaveSlotCount = request.FlashCartMode.IsChm1Mode() ? Chm1Layout.SaveSlotCount : GetMaxBatterySaveSlotCount(request.Cartridge, itemListOffset + 1);
        for (var i = 0; i < request.Games.Count; i++)
        {
            var game = request.Games[i];
            if (!game.Enabled || !game.SaveSlot.HasValue)
            {
                continue;
            }

            if (game.SaveSlot.Value <= 0 || game.SaveSlot.Value > maxSaveSlotCount)
            {
                throw new InvalidOperationException(
                    Tf(textResolver, "error", "row_invalid_save_slot", "Row {0} has an invalid save slot.", i + 1));
            }
        }
    }

    private sealed record PreparedGbcGame(
        int Index,
        int RomIndex,
        string Title,
        string HeaderTitle,
        string RomPath,
        long FileLength,
        long AllocatedBytes,
        int AllocatedRomBanks,
        int RamBankCount,
        byte MbcType,
        bool HasBattery,
        bool UsesDedicatedSaveAllocation,
        int RomOffsetBank,
        byte[] SramBankMap);

    private sealed record GbcFontPackageInfo(
        byte[] Bytes,
        ushort[] OrderedCodepoints,
        ushort[] MissingCodepoints,
        string SourcePath,
        GbcFontBitmapFormat Format,
        int TableBase,
        int BitmapBase);

    private sealed record GbcGlyphLoadResult(
        byte[][] GlyphBitmaps,
        ushort[] MissingCodepoints,
        string SourcePath,
        GbcFontBitmapFormat Format);

    private sealed class PreparedGame
    {
        public int Index { get; init; }

        public string Title { get; init; } = string.Empty;

        public string RomPath { get; init; } = string.Empty;

        public int Size { get; init; }

        public int SectorCount { get; init; }

        public int PlacementAlignmentSectorCount { get; init; }

        public ushort HiddenKeysMask { get; init; }

        public bool Map256m { get; init; }

        public int TitleFont { get; init; }

        public string? CoverPath { get; init; }

        public bool CoverCleared { get; init; }

        public int SaveType { get; init; }

        public int? SaveSlotIndex { get; init; }

        public byte[]? PatchedRomData { get; init; }

        public string? SramPatchIdentifier { get; init; }

        public bool BatterylessPatched { get; init; }

        public BatterylessPatchService.BatterylessPatchDebugInfo? BatterylessDebugInfo { get; init; }

        public int SectorOffset { get; set; } = -1;

        public int BlockOffset { get; set; }

        public int BlockCount { get; set; }
    }

    private static string T(
        Func<string, string, string, string>? textResolver,
        string section,
        string key,
        string fallback) =>
        textResolver?.Invoke(section, key, fallback) ?? fallback;

    private static string Tf(
        Func<string, string, string, string>? textResolver,
        string section,
        string key,
        string fallback,
        params object[] args) =>
        string.Format(T(textResolver, section, key, fallback), args);
}
