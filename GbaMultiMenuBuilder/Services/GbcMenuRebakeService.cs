using System.Text;
using GbaMultiMenuBuilder.Models;

namespace GbaMultiMenuBuilder.Services;

public static class GbcMenuRebakeService
{
    private const string WorkerArgument = "--rebake-gbc-menu";
    private const int GbcConfigOffsetBytes = 0x020000;
    private const int GbcRomDirectoryOffsetBytes = 0x040000;
    private const int GbcFixedMapReservedBytes = 1 * 1024 * 1024;
    private const int GbcFontOffsetBytes = 0x0C0000;
    private const int GbcConfigHeaderSize = 64;
    private const int GbcRomDirectoryHeaderSize = 64;
    private const int GbcRomEntrySize = 128;
    private const int GbcRomNameCodeCount = 48;
    private const int GbcRomSramMapOffset = 110;
    private const int GbcMaxGames = 99;
    private const int GbcIndependentSramBankCapacity = 60;

    public static bool TryRun(string[] args)
    {
        if (args.Length < 3 || !string.Equals(args[0], WorkerArgument, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            Run(args);
            Environment.ExitCode = 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            Environment.ExitCode = 1;
        }

        return true;
    }

    private static void Run(string[] args)
    {
        var inputPath = Path.GetFullPath(args[1]);
        var outputPath = Path.GetFullPath(args[2]);
        string? runtimePath = null;
        string? requestedFont = null;
        if (args.Length >= 4)
        {
            if (File.Exists(args[3]) &&
                (string.Equals(Path.GetExtension(args[3]), ".gb", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(Path.GetExtension(args[3]), ".gbc", StringComparison.OrdinalIgnoreCase)))
            {
                runtimePath = Path.GetFullPath(args[3]);
                requestedFont = args.Length >= 5 ? args[4] : null;
            }
            else
            {
                requestedFont = args[3];
            }
        }

        var configuredFontName = AppConfigService.LoadSettings(CompilationFamily.Gbc).GbcFontName;
        requestedFont ??= configuredFontName ?? GbcFontService.GetDefaultBuiltInFontFileName();
        var fontPath = GbcFontService.FindFontPath(requestedFont)
            ?? throw new InvalidOperationException($"Unable to resolve GBC font: {requestedFont}");

        if (!File.Exists(inputPath))
        {
            throw new InvalidOperationException($"Input ROM not found: {inputPath}");
        }

        var compilation = File.ReadAllBytes(inputPath);
        if (!string.IsNullOrWhiteSpace(runtimePath))
        {
            WriteMenuRuntime(compilation, runtimePath);
        }

        ValidateFixedMapLayout(compilation);
        var gameInfos = ReadGameInfos(compilation);
        if (gameInfos.Count == 0)
        {
            throw new InvalidOperationException("No valid GBC fixed-map directory entries were found.");
        }

        var layer = GbcMenuLayerBuilder.Build(gameInfos, fontPath);
        WriteLayer(compilation, layer);
        PatchCartridgeHeader(compilation);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllBytes(outputPath, compilation);

        Console.WriteLine($"Rebaked GBC menu layer: {outputPath}");
        if (!string.IsNullOrWhiteSpace(runtimePath))
        {
            Console.WriteLine($"Runtime: {runtimePath}");
        }
        Console.WriteLine($"Games: {gameInfos.Count}");
        Console.WriteLine($"Font: {fontPath}");
    }

    private static void WriteMenuRuntime(byte[] compilation, string runtimePath)
    {
        var runtime = File.ReadAllBytes(runtimePath);
        if (runtime.Length > GbcConfigOffsetBytes)
        {
            throw new InvalidOperationException(
                $"GBC menu runtime exceeds the reserved 128 KiB menu region: {runtimePath}");
        }

        Buffer.BlockCopy(runtime, 0, compilation, 0, runtime.Length);
    }

    private static void PatchCartridgeHeader(byte[] romData)
    {
        if (romData.Length < 0x150)
        {
            return;
        }

        romData[0x147] = 0x19;
        romData[0x148] = GetRomSizeCode(romData.Length);
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

    private static byte GetRomSizeCode(int romBytes)
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
            if (romBytes <= candidate.SizeBytes)
            {
                return candidate.Code;
            }
        }

        return 0x08;
    }

    private static void ValidateFixedMapLayout(byte[] compilation)
    {
        if (compilation.Length < GbcConfigOffsetBytes + GbcFixedMapReservedBytes)
        {
            throw new InvalidOperationException("ROM is too small to contain the full fixed-map reserved region.");
        }

        if (!MatchesAscii(compilation, GbcConfigOffsetBytes, "MBCXCFG"))
        {
            throw new InvalidOperationException("ROM does not contain an MBCXCFG header at 0x020000.");
        }

        if (!MatchesAscii(compilation, GbcRomDirectoryOffsetBytes, "MBCXROM"))
        {
            throw new InvalidOperationException("ROM does not contain an MBCXROM header at 0x040000.");
        }
    }

    private static List<GbcMenuLayerBuilder.GbcMenuGameInfo> ReadGameInfos(byte[] compilation)
    {
        var dirHeaderOffset = GbcRomDirectoryOffsetBytes;
        var entryCount = ReadUInt16(compilation, dirHeaderOffset + 10);
        var entrySize = ReadUInt16(compilation, dirHeaderOffset + 12);
        var entryBase = ReadUInt32(compilation, dirHeaderOffset + 16);

        if (entrySize < 14 || entrySize > GbcRomEntrySize)
        {
            entrySize = GbcRomEntrySize;
        }

        if (entryBase < GbcRomDirectoryOffsetBytes || entryBase >= GbcFontOffsetBytes)
        {
            entryBase = GbcRomDirectoryOffsetBytes + GbcRomDirectoryHeaderSize;
        }

        entryCount = (ushort)Math.Min((int)entryCount, GbcMaxGames);

        var games = new List<GbcMenuLayerBuilder.GbcMenuGameInfo>(entryCount);
        for (var index = 0; index < entryCount; index++)
        {
            var entryOffset = checked((int)entryBase + (index * entrySize));
            if (entryOffset < 0 || entryOffset + entrySize > compilation.Length)
            {
                break;
            }

            if ((compilation[entryOffset + 13] & 0x01) == 0)
            {
                continue;
            }

            var romBanks = ReadUInt16(compilation, entryOffset + 8);
            if (romBanks == 0)
            {
                continue;
            }

            var title = DecodeLocalizedTitle(compilation.AsSpan(entryOffset + 14, GbcRomNameCodeCount * sizeof(ushort)));
            if (string.IsNullOrWhiteSpace(title))
            {
                title = $"GAME{index + 1:D2}";
            }

            games.Add(new GbcMenuLayerBuilder.GbcMenuGameInfo(
                TitleCn: title,
                MbcType: compilation[entryOffset + 11],
                RomBanks: romBanks,
                RamBanks: compilation[entryOffset + 10],
                UsesDedicatedSave: HasDedicatedSramMap(compilation, entryOffset, compilation[entryOffset + 10])));
        }

        return games;
    }

    private static bool HasDedicatedSramMap(byte[] compilation, int entryOffset, int ramBankCount)
    {
        if (ramBankCount <= 0 || entryOffset + GbcRomSramMapOffset >= compilation.Length)
        {
            return false;
        }

        var firstMappedBank = compilation[entryOffset + GbcRomSramMapOffset];
        return firstMappedBank < GbcIndependentSramBankCapacity;
    }

    private static string DecodeLocalizedTitle(ReadOnlySpan<byte> encodedName)
    {
        var strings = new List<string>(2);
        var codeUnits = new List<char>(GbcRomNameCodeCount);

        for (var offset = 0; offset + 1 < encodedName.Length; offset += 2)
        {
            var codeUnit = (ushort)(encodedName[offset] | (encodedName[offset + 1] << 8));
            if (codeUnit == 0xFFFF)
            {
                break;
            }

            if (codeUnit == 0x0000)
            {
                if (codeUnits.Count == 0)
                {
                    break;
                }

                strings.Add(new string(codeUnits.ToArray()));
                codeUnits.Clear();
                continue;
            }

            codeUnits.Add((char)codeUnit);
        }

        if (codeUnits.Count > 0)
        {
            strings.Add(new string(codeUnits.ToArray()));
        }

        return strings.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }

    private static void WriteLayer(byte[] compilation, GbcMenuLayerBuilder.GbcMenuLayerResult layer)
    {
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

        if (maxLayerEnd > GbcFontOffsetBytes)
        {
            throw new InvalidOperationException(
                $"Rebaked menu layer overlaps with MBCXFNT font area. " +
                $"Layer data ends at: 0x{maxLayerEnd:X6}, font area starts at: 0x{GbcFontOffsetBytes:X6}");
        }
    }

    private static bool MatchesAscii(byte[] buffer, int offset, string text)
    {
        if (offset < 0 || offset + text.Length > buffer.Length)
        {
            return false;
        }

        return Encoding.ASCII.GetString(buffer, offset, text.Length) == text;
    }

    private static ushort ReadUInt16(byte[] buffer, int offset) =>
        (ushort)(buffer[offset] | (buffer[offset + 1] << 8));

    private static int ReadUInt32(byte[] buffer, int offset) =>
        buffer[offset] |
        (buffer[offset + 1] << 8) |
        (buffer[offset + 2] << 16) |
        (buffer[offset + 3] << 24);
}
