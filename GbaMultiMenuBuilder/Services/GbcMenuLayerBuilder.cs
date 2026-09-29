using System.Globalization;
using System.Text;

namespace GbaMultiMenuBuilder.Services;

internal static class GbcMenuLayerBuilder
{
    private const int FixedMapConfigOffset = 0x020000;
    private const int FixedMapRomDirectoryOffset = 0x040000;
    private const int FixedMapHeaderSize = 64;
    private const int FixedMapRomEntrySize = 128;
    internal const int MenuLayerHeaderOffset = 0x00000040;
    internal const int MenuLayerHeaderSize = 80;
    internal const int MenuListStringsOffset = 0x00000100;
    internal const int MenuUiTileDataOffset = 0x00010000;
    internal const int MenuTopBarMapOffset = 0x00001400;
    internal const int MenuBottomBarStaticMapOffset = 0x00001440;
    internal const int MenuDetailTopBarMapOffset = 0x000014C0;
    internal const int MenuDetailBottomBarStaticMapOffset = 0x00001500;
    internal const int MenuCompactGlyphDataOffset = 0x00001540;

    private const int MenuScreenTilesW = 20;
    private const int MenuScreenTilesH = 18;
    private const int MenuTopBarTilesH = 2;
    private const int MenuListRegionTilesX = 3;
    private const int MenuListRegionY = 3;
    private const int MenuListRegionTilesW = MenuScreenTilesW - MenuListRegionTilesX;
    private const int MenuListRegionTilesH = 12;
    private const int MenuBottomBarTilesH = 2;

    private const int MenuTopBarTilemapSize = MenuScreenTilesW * MenuTopBarTilesH;
    private const int MenuBottomBarTilemapSize = MenuScreenTilesW * MenuBottomBarTilesH;
    private const int MenuListTilemapSize = MenuScreenTilesW * MenuScreenTilesH;
    private const int MenuDetailTilemapSize = MenuScreenTilesW * MenuScreenTilesH;

    private const int MenuTileBytes = 16;
    // Current 12px layered UI needs up to 83 unique static tiles across 1..99 entries.
    private const int MenuUiMaxTiles = 84;
    private const int MenuDynamicMaxTiles = 204;
    private const int MenuListEntryWords = 24;
    private const int MenuTitleScrollTableEntrySize = 8;

    private const int ScreenW = 160;
    private const int ScreenH = 144;
    private const int UiSideMarginPx = 8;
    private const int PageLabelRightNudgePx = 0;
    private const int CompactUiGapPx = 2;
    private const int MenuPunctuationVerticalNudgePx = 4;

    private const int GamesPerPage = 8;
    private const int ChineseMenuFontPx = 12;
    private const int ChineseMenuCharGapPx = 1;
    private const int TitleAsciiGapPx = 1;
    private const int TitleSpaceAdvancePx = 4;
    private const int ChineseMenuTitleWidthPx = 112;
    private const int MainMenuTitleUnitBudget = 16;
    private const int MainMenuTitleClipWidthPx = 84;
    private const int MainMenuAsciiCellPx = 6;
    private const int ChineseMenuNameX = 52;
    private const int MenuIndexX = 28;
    private const int RowTopStartPx = 24;
    private const int RowStepPx = 12;

    private const int DetailContentShiftPx = (ChineseMenuFontPx * 3) / 2;
    private const int DetailLabelX = UiSideMarginPx + DetailContentShiftPx;
    private const int DetailValueX = 46 + DetailContentShiftPx;
    private const int DetailRowTopY = 24;
    private const int DetailRowStepY = 16;
    private const int DetailNameWidthPx = 104;
    private const string FullWidthColon = "\uFF1A";

    private const string CompactGlyphChars = "0123456789/.MBC";

    private const string MenuHeaderText = "游戏列表";
    private const string MenuFooterText = "起司菜单";
    private const string MenuVersionText = "ver1.00";
    private const string MenuDetailTitleText = "游戏详情";
    private const string MenuDetailActionText = "A启动 B返回";
    private const string MenuDetailIndexLabel = "序号";
    private const string MenuDetailNameLabel = "名称";
    private const string MenuDetailTypeLabel = "类型";
    private const string MenuDetailRomLabel = "ROM";
    private const string MenuDetailRamLabel = "RAM";
    private const string MenuDetailSaveLabel = "存档";
    private const string MenuDetailSaveOnText = "支持";
    private const string MenuDetailSaveOffText = "无";
    private const string MenuDetailNoneText = "无";

    private const string MenuDetailSaveDedicatedText = "\u72EC\u7ACB";
    private const string MenuDetailSaveSharedText = "\u5171\u4EAB";

    internal static string PreviewHeaderText => MenuHeaderText;
    internal static string PreviewFooterText => MenuFooterText;
    internal static string PreviewVersionText => MenuVersionText;
    internal static string PreviewDetailTitleText => MenuDetailTitleText;
    internal static string PreviewDetailActionText => MenuDetailActionText;
    internal static string PreviewDetailIndexLabelText => MenuDetailIndexLabel + FullWidthColon;
    internal static string PreviewDetailNameLabelText => MenuDetailNameLabel + FullWidthColon;
    internal static string PreviewDetailTypeLabelText => MenuDetailTypeLabel + FullWidthColon;
    internal static string PreviewDetailRomLabelText => MenuDetailRomLabel + ":";
    internal static string PreviewDetailRamLabelText => MenuDetailRamLabel + ":";
    internal static string PreviewDetailSaveLabelText => MenuDetailSaveLabel + FullWidthColon;
    internal static int PreviewScreenWidthPx => ScreenW;
    internal static int PreviewUiSideMarginPx => UiSideMarginPx;
    internal static int PreviewTopBarTextY => 2;
    internal static int PreviewBottomBarTextY => 132;
    internal static int PreviewDetailLabelX => DetailLabelX;
    internal static int PreviewDetailValueX => DetailValueX;
    internal static int PreviewDetailRowTopY => DetailRowTopY;
    internal static int PreviewDetailRowStepY => DetailRowStepY;
    internal static string LimitPreviewMainMenuTitle(string text) => LimitTitleToMainMenuUnits(text, MainMenuTitleUnitBudget);
    internal static string LimitPreviewDetailTitle(string text)
    {
        var visibleNameChars = Math.Max(1, DetailNameWidthPx / (ChineseMenuFontPx + ChineseMenuCharGapPx));
        return LimitTextByVisibleChars(text, visibleNameChars);
    }
    internal static string FormatPreviewDetailTypeText(byte mbcType) => DetailMbcText(mbcType);
    internal static string FormatPreviewDetailRomText(int romBanks) => DetailRomText(romBanks);
    internal static string FormatPreviewDetailRamText(int ramBanks) => DetailRamText(ramBanks);
    internal static string FormatPreviewDetailSaveText(bool usesDedicatedSave, int ramBanks) => DetailSaveText(usesDedicatedSave, ramBanks);

    internal static IReadOnlyList<string> GetPreviewUiTexts() =>
    [
        MenuHeaderText,
        MenuFooterText,
        MenuVersionText,
        MenuDetailTitleText,
        MenuDetailActionText,
        MenuDetailIndexLabel,
        MenuDetailNameLabel,
        MenuDetailTypeLabel,
        MenuDetailRomLabel,
        MenuDetailRamLabel,
        MenuDetailSaveLabel,
        MenuDetailSaveDedicatedText,
        MenuDetailSaveSharedText,
        MenuDetailNoneText,
        FullWidthColon,
        ":",
    ];

    public sealed record GbcMenuLayerResult(
        byte[] LayerHeader,
        byte[] ListStrings,
        byte[] UiTileData,
        byte[] CompactGlyphData,
        byte[] TopBarTilemap,
        byte[] BottomBarStaticMap,
        byte[] DetailTopBarTilemap,
        byte[] DetailBottomBarStaticMap,
        byte[] ListTileCounts,
        byte[] ListTileData,
        byte[] ListTilemaps,
        byte[] DetailTileCounts,
        byte[] DetailTileData,
        byte[] DetailTilemaps,
        byte[] TitleScrollTable,
        byte[] TitleScrollData,
        int UiTileCount,
        int UiTileDataBytesWritten,
        int CompactGlyphDataBytesWritten,
        int TopBarMapBytesWritten,
        int BottomBarStaticMapBytesWritten,
        int DetailTopBarMapBytesWritten,
        int DetailBottomBarStaticMapBytesWritten,
        int ListTileCountsBytesWritten,
        int ListTileDataBytesWritten,
        int ListTilemapsBytesWritten,
        int DetailTileCountsBytesWritten,
        int DetailTileDataBytesWritten,
        int DetailTilemapsBytesWritten,
        int TitleScrollTableBytesWritten,
        int TitleScrollDataBytesWritten,
        int PageCount,
        Dictionary<string, int> DynamicOffsets,
        int TotalLayerBytes);

    public static GbcMenuLayerResult Build(IReadOnlyList<GbcMenuGameInfo> games, string bdfFontPath)
    {
        if (games.Count == 0)
        {
            throw new InvalidOperationException("At least one game is required for menu layer generation.");
        }

        var pageCount = Math.Max(1, (games.Count + GamesPerPage - 1) / GamesPerPage);
        var dynamicBaseOffset = GetDynamicBaseOffset(games.Count);
        var glyphCache = BuildGlyphCache(games, bdfFontPath);
        var compactGlyphData = BuildCompactGlyphData(glyphCache.Glyphs, glyphCache.Format);

        var displayGames = LimitTitlesForMainMenu(games, glyphCache.Glyphs, glyphCache.Format);
        return BuildInternal(games, displayGames, pageCount, glyphCache, compactGlyphData, dynamicBaseOffset);
    }

    private static GbcMenuLayerResult BuildInternal(
        IReadOnlyList<GbcMenuGameInfo> fullGames,
        IReadOnlyList<GbcMenuGameInfo> displayGames,
        int pageCount,
        GbcFontGlyphCacheResult glyphCache,
        byte[] compactGlyphData,
        int dynamicBaseOffset)
    {
        var glyphs = glyphCache.Glyphs;
        var glyphFormat = glyphCache.Format;
        var pageCanvases = new byte[pageCount][,];
        for (var page = 0; page < pageCount; page++)
        {
            pageCanvases[page] = CreateFullPageCanvas(page, pageCount, displayGames, glyphs, glyphFormat);
        }

        var detailCanvases = new byte[fullGames.Count][,];
        for (var i = 0; i < fullGames.Count; i++)
        {
            detailCanvases[i] = CreateDetailPageCanvas(i, fullGames, glyphs, glyphFormat);
        }

        var (uiTileData, topBarTilemap, bottomBarStaticMap, detailTopBarTilemap, detailBottomBarStaticMap, uiTileCount) =
            ExtractStaticUiRegions(pageCanvases[0], detailCanvases[0]);

        var (listTileCounts, listTileData, listTilemaps) = ExtractDynamicRegions(
            pageCanvases, 0, 0, MenuScreenTilesW, MenuScreenTilesH);

        var (detailTileCounts, detailTileData, detailTilemaps) = ExtractDynamicRegions(
            detailCanvases, 0, 0, MenuScreenTilesW, MenuScreenTilesH);

        var dynamicOffsets = MakeDynamicOffsets(pageCount, displayGames.Count, MenuDynamicMaxTiles, dynamicBaseOffset, 0, 0);

        var listStrings = CreateGameListStrings(displayGames);
        var layerHeader = CreateLayerHeader(pageCount, uiTileCount, displayGames.Count, dynamicOffsets, glyphFormat);

        var totalLayerBytes = checked(
            MenuLayerHeaderSize +
            listStrings.Length +
            uiTileData.Length +
            compactGlyphData.Length +
            topBarTilemap.Length +
            bottomBarStaticMap.Length +
            detailTopBarTilemap.Length +
            detailBottomBarStaticMap.Length +
            listTileCounts.Length +
            listTileData.Length +
            listTilemaps.Length +
            detailTileCounts.Length +
            detailTileData.Length +
            detailTilemaps.Length);

        return new GbcMenuLayerResult(
            layerHeader,
            listStrings,
            uiTileData,
            compactGlyphData,
            topBarTilemap,
            bottomBarStaticMap,
            detailTopBarTilemap,
            detailBottomBarStaticMap,
            listTileCounts,
            listTileData,
            listTilemaps,
            detailTileCounts,
            detailTileData,
            detailTilemaps,
            Array.Empty<byte>(),
            Array.Empty<byte>(),
            uiTileCount,
            uiTileData.Length,
            compactGlyphData.Length,
            topBarTilemap.Length,
            bottomBarStaticMap.Length,
            detailTopBarTilemap.Length,
            detailBottomBarStaticMap.Length,
            listTileCounts.Length,
            listTileData.Length,
            listTilemaps.Length,
            detailTileCounts.Length,
            detailTileData.Length,
            detailTilemaps.Length,
            0,
            0,
            pageCount,
            dynamicOffsets,
            totalLayerBytes);
    }

    public sealed record GbcMenuGameInfo(
        string TitleCn,
        byte MbcType,
        int RomBanks,
        int RamBanks,
        bool UsesDedicatedSave);

    private static GbcFontGlyphCacheResult BuildGlyphCache(IReadOnlyList<GbcMenuGameInfo> games, string fontPath)
    {
        var texts = CollectMenuTexts(games);
        var codes = new HashSet<int>();
        foreach (var text in texts)
        {
            foreach (var ch in text)
            {
                var code = (int)ch;
                if (code >= 0x80 || (code >= 0x20 && code <= 0x7E))
                {
                    codes.Add(code);
                }
            }
        }

        foreach (var ch in CompactGlyphChars)
        {
            codes.Add(ch);
        }

        var glyphCache = GbcFontService.BuildGlyphCacheDetailed(codes, fontPath);
        ApplyMenuPunctuationOverrides(glyphCache.Glyphs, glyphCache.Format);
        return glyphCache;
    }

    internal static void ApplyMenuPunctuationOverrides(Dictionary<int, byte[]> glyphs, GbcFontBitmapFormat format)
    {
        if (format.GlyphSizePx <= GbcFontService.CanvasWidth)
        {
            return;
        }

        glyphs['.'] = CreateSolidPeriodGlyph(format);
        glyphs[':'] = CreateSolidColonGlyph(format);
        glyphs['\uFF1A'] = CreateSolidColonGlyph(format);
    }

    private static byte[] CreateSolidPeriodGlyph(GbcFontBitmapFormat format)
    {
        var glyph = new byte[format.GlyphBytes];
        const int dotSize = 2;
        var dotY = ClampGlyphY(format, (format.GlyphSizePx - 4) + MenuPunctuationVerticalNudgePx, dotSize);
        FillGlyphRect(glyph, format, 3, dotY, dotSize, dotSize);
        return glyph;
    }

    private static byte[] CreateSolidColonGlyph(GbcFontBitmapFormat format)
    {
        var glyph = new byte[format.GlyphBytes];
        const int dotSize = 2;
        var left = Math.Max(0, (format.GlyphSizePx - dotSize) / 2);
        var topY = ClampGlyphY(format, 3 + MenuPunctuationVerticalNudgePx, dotSize);
        var bottomY = ClampGlyphY(format, (format.GlyphSizePx - 5) + MenuPunctuationVerticalNudgePx, dotSize);
        if (bottomY <= topY)
        {
            bottomY = ClampGlyphY(format, topY + dotSize + 1, dotSize);
        }

        FillGlyphRect(glyph, format, left, topY, dotSize, dotSize);
        FillGlyphRect(glyph, format, left, bottomY, dotSize, dotSize);
        return glyph;
    }

    private static int ClampGlyphY(GbcFontBitmapFormat format, int y, int height) =>
        Math.Max(0, Math.Min(format.GlyphSizePx - Math.Max(1, height), y));

    private static void FillGlyphRect(byte[] glyph, GbcFontBitmapFormat format, int x, int y, int width, int height)
    {
        for (var py = 0; py < height; py++)
        {
            for (var px = 0; px < width; px++)
            {
                SetGlyphPixel(glyph, format, x + px, y + py);
            }
        }
    }

    private static void SetGlyphPixel(byte[] glyph, GbcFontBitmapFormat format, int x, int y)
    {
        if (x < 0 || y < 0 || x >= format.GlyphSizePx || y >= format.GlyphSizePx)
        {
            return;
        }

        var offset = (y * format.RowBytes) + (x >> 3);
        glyph[offset] |= (byte)(0x80 >> (x & 0x07));
    }

    private static List<string> CollectMenuTexts(IReadOnlyList<GbcMenuGameInfo> games)
    {
        var texts = new List<string>
        {
            MenuHeaderText,
            MenuFooterText,
            MenuDetailTitleText,
            MenuDetailActionText,
            MenuDetailIndexLabel,
            MenuDetailNameLabel,
            MenuDetailTypeLabel,
            MenuDetailRomLabel,
            MenuDetailRamLabel,
            MenuDetailSaveLabel,
            MenuDetailSaveDedicatedText,
            MenuDetailSaveSharedText,
            MenuDetailNoneText,
            MenuVersionText,
            FullWidthColon,
            ":",
        };

        var pageCount = Math.Max(1, (games.Count + GamesPerPage - 1) / GamesPerPage);
        for (var i = 0; i < games.Count; i++)
        {
            texts.Add($"{i + 1:D2}.");
        }

        for (var page = 0; page < pageCount; page++)
        {
            texts.Add($"{page + 1:D2}/{pageCount:D2}");
        }

        for (var i = 0; i < games.Count; i++)
        {
            texts.Add($"{i + 1:D2}/{games.Count:D2}");
        }

        foreach (var game in games)
        {
            texts.Add(DetailMbcText(game.MbcType));
            texts.Add(DetailRomText(game.RomBanks));
            texts.Add(DetailRamText(game.RamBanks));
            texts.Add(DetailSaveText(game.UsesDedicatedSave, game.RamBanks));
            texts.Add(game.TitleCn);
        }

        return texts;
    }

    private static byte[] BuildCompactGlyphData(Dictionary<int, byte[]> glyphs, GbcFontBitmapFormat format)
    {
        var data = new byte[CompactGlyphChars.Length * format.GlyphBytes];
        for (var i = 0; i < CompactGlyphChars.Length; i++)
        {
            if (!glyphs.TryGetValue(CompactGlyphChars[i], out var bitmap))
            {
                throw new InvalidOperationException($"Missing compact glyph for '{CompactGlyphChars[i]}'");
            }

            Array.Copy(bitmap, 0, data, i * format.GlyphBytes, format.GlyphBytes);
        }

        return data;
    }

    private static byte[,] MakeCanvas()
    {
        return new byte[ScreenH, ScreenW];
    }

    private static int ReadGlyphRowBits(byte[] bitmap, GbcFontBitmapFormat format, int row)
    {
        var rowBits = 0;
        var rowOffset = row * format.RowBytes;
        for (var i = 0; i < format.RowBytes && rowOffset + i < bitmap.Length; i++)
        {
            rowBits = (rowBits << 8) | bitmap[rowOffset + i];
        }

        var unusedBits = (format.RowBytes * 8) - format.GlyphSizePx;
        if (unusedBits > 0)
        {
            rowBits >>= unusedBits;
        }

        return rowBits;
    }

    private static void BlitScaled(byte[,] canvas, byte[] bitmap, GbcFontBitmapFormat format, int x, int y, int sizePx)
    {
        for (var sy = 0; sy < format.GlyphSizePx; sy++)
        {
            var rowBits = ReadGlyphRowBits(bitmap, format, sy);
            var y0 = (sy * sizePx) / format.GlyphSizePx;
            var y1 = ((sy + 1) * sizePx + format.GlyphSizePx - 1) / format.GlyphSizePx;
            if (y1 <= y0) y1 = y0 + 1;

            for (var sx = 0; sx < format.GlyphSizePx; sx++)
            {
                if ((rowBits & (1 << (format.GlyphSizePx - 1 - sx))) == 0) continue;

                var x0 = (sx * sizePx) / format.GlyphSizePx;
                var x1 = ((sx + 1) * sizePx + format.GlyphSizePx - 1) / format.GlyphSizePx;
                if (x1 <= x0) x1 = x0 + 1;

                for (var py = y0; py < y1; py++)
                {
                    var canvasY = y + py;
                    if (canvasY < 0 || canvasY >= ScreenH) continue;
                    for (var px = x0; px < x1; px++)
                    {
                        var canvasX = x + px;
                        if (canvasX >= 0 && canvasX < ScreenW)
                        {
                            canvas[canvasY, canvasX] = 1;
                        }
                    }
                }
            }
        }
    }

    private static void DrawText(
        byte[,] canvas,
        Dictionary<int, byte[]> glyphs,
        GbcFontBitmapFormat format,
        string text,
        int x,
        int y,
        int sizePx,
        int gapPx)
    {
        var cursorX = x;
        foreach (var ch in text)
        {
            if (!glyphs.TryGetValue(ch, out var bitmap))
            {
                throw new InvalidOperationException($"Missing glyph for '{ch}'");
            }

            BlitScaled(canvas, bitmap, format, cursorX, y, sizePx);
            cursorX += sizePx + gapPx;
        }
    }

    private static void DrawTitleText(
        byte[,] canvas,
        Dictionary<int, byte[]> glyphs,
        GbcFontBitmapFormat format,
        string text,
        int x,
        int y,
        int? clipWidthPx = null)
    {
        var cursorX = x;
        var clipRight = clipWidthPx.HasValue ? x + clipWidthPx.Value : int.MaxValue;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (!glyphs.TryGetValue(ch, out var bitmap))
            {
                throw new InvalidOperationException($"Missing glyph for '{ch}'");
            }

            if (cursorX < clipRight)
            {
                BlitScaledClipped(canvas, bitmap, format, cursorX, y, ChineseMenuFontPx, clipRight);
            }

            if (ch == ' ')
            {
                cursorX += TitleSpaceAdvancePx;
            }
            else if (ch < 128)
            {
                cursorX += CompactGlyphAdvancePx(bitmap, format, ChineseMenuFontPx);
                if (i + 1 < text.Length)
                {
                    cursorX += TitleAsciiGapPx;
                }
            }
            else
            {
                cursorX += ChineseMenuFontPx + ChineseMenuCharGapPx;
            }
        }
    }

    private static void DrawMainMenuTitleText(
        byte[,] canvas,
        Dictionary<int, byte[]> glyphs,
        GbcFontBitmapFormat format,
        string text,
        int x,
        int y)
    {
        var cursorX = x;
        var clipRight = x + MainMenuTitleClipWidthPx;

        foreach (var ch in text)
        {
            if (!glyphs.TryGetValue(ch, out var bitmap))
            {
                throw new InvalidOperationException($"Missing glyph for '{ch}'");
            }

            if (cursorX < clipRight)
            {
                BlitScaledClipped(canvas, bitmap, format, cursorX, y, ChineseMenuFontPx, clipRight);
            }

            if (ch < 128)
            {
                cursorX += MainMenuAsciiCellPx;
            }
            else
            {
                if ((cursorX - x) % ChineseMenuFontPx != 0)
                {
                    cursorX += MainMenuAsciiCellPx;
                }

                cursorX += ChineseMenuFontPx;
            }
        }
    }

    private static void BlitScaledClipped(
        byte[,] canvas,
        byte[] bitmap,
        GbcFontBitmapFormat format,
        int x,
        int y,
        int sizePx,
        int clipRight)
    {
        for (var sy = 0; sy < format.GlyphSizePx; sy++)
        {
            var rowBits = ReadGlyphRowBits(bitmap, format, sy);
            var y0 = y + (sy * sizePx) / format.GlyphSizePx;
            var y1 = y + ((sy + 1) * sizePx + format.GlyphSizePx - 1) / format.GlyphSizePx;
            if (y1 <= y0) y1 = y0 + 1;

            for (var sx = 0; sx < format.GlyphSizePx; sx++)
            {
                if ((rowBits & (1 << (format.GlyphSizePx - 1 - sx))) == 0) continue;

                var x0 = x + (sx * sizePx) / format.GlyphSizePx;
                var x1 = x + ((sx + 1) * sizePx + format.GlyphSizePx - 1) / format.GlyphSizePx;
                if (x1 <= x0) x1 = x0 + 1;
                if (x0 >= clipRight) continue;
                if (x1 > clipRight) x1 = clipRight;

                for (var py = y0; py < y1; py++)
                {
                    for (var px = x0; px < x1; px++)
                    {
                        if (px >= 0 && px < ScreenW && py >= 0 && py < ScreenH)
                        {
                            canvas[py, px] = 1;
                        }
                    }
                }
            }
        }
    }

    private static int MeasureTitleTextWidth(
        string text,
        Dictionary<int, byte[]> glyphs,
        GbcFontBitmapFormat format)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var width = 0;
        char? previousChar = null;
        foreach (var ch in text)
        {
            if (previousChar.HasValue && previousChar.Value < 128 && previousChar.Value != ' ')
            {
                width += TitleAsciiGapPx;
            }

            width += MeasureTitleCharWidth(glyphs, format, ch);
            previousChar = ch;
        }

        return width;
    }

    private static int MeasureTitleCharWidth(
        Dictionary<int, byte[]> glyphs,
        GbcFontBitmapFormat format,
        char ch)
    {
        if (!glyphs.TryGetValue(ch, out var bitmap))
        {
            throw new InvalidOperationException($"Missing glyph for '{ch}'");
        }

        if (ch == ' ')
        {
            return TitleSpaceAdvancePx;
        }

        return ch < 128
            ? CompactGlyphAdvancePx(bitmap, format, ChineseMenuFontPx)
            : ChineseMenuFontPx + ChineseMenuCharGapPx;
    }

    private static int MainMenuTitleUnitsForChar(char ch)
    {
        return ch < 128 ? 1 : 2;
    }

    private static string LimitTitleToMainMenuUnits(string text, int unitBudget)
    {
        if (unitBudget <= 0 || string.IsNullOrEmpty(text))
        {
            return text;
        }

        var usedUnits = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var nextUnits = MainMenuTitleUnitsForChar(text[i]);
            if (usedUnits + nextUnits > unitBudget)
            {
                return i == 0 ? text[..1] : text[..i];
            }

            usedUnits += nextUnits;
        }

        return text;
    }

    private static string LimitTextByVisibleChars(string text, int maxChars)
    {
        if (maxChars <= 0 || string.IsNullOrEmpty(text) || text.Length <= maxChars)
        {
            return text;
        }

        return text[..maxChars];
    }

    private static List<GbcMenuGameInfo> LimitTitlesForMainMenu(
        IReadOnlyList<GbcMenuGameInfo> games,
        Dictionary<int, byte[]> glyphs,
        GbcFontBitmapFormat format)
    {
        var result = new List<GbcMenuGameInfo>(games.Count);
        foreach (var game in games)
        {
            result.Add(game with
            {
                TitleCn = LimitTitleToMainMenuUnits(game.TitleCn, MainMenuTitleUnitBudget)
            });
        }

        return result;
    }

    private static int CompactGlyphAdvancePx(byte[] bitmap, GbcFontBitmapFormat format, int sizePx)
    {
        var right = -1;
        for (var sy = 0; sy < format.GlyphSizePx; sy++)
        {
            var rowBits = ReadGlyphRowBits(bitmap, format, sy);
            for (var sx = 0; sx < format.GlyphSizePx; sx++)
            {
                if ((rowBits & (1 << (format.GlyphSizePx - 1 - sx))) != 0 && sx > right)
                {
                    right = sx;
                }
            }
        }

        if (right < 0) return Math.Max(1, sizePx / 2);
        return ((right + 1) * sizePx + format.GlyphSizePx - 1) / format.GlyphSizePx;
    }

    private static void DrawCompactMarkerText(
        byte[,] canvas,
        Dictionary<int, byte[]> glyphs,
        GbcFontBitmapFormat format,
        string text,
        int x,
        int y)
    {
        var cursorX = x;
        for (var i = 0; i < text.Length; i++)
        {
            if (!glyphs.TryGetValue(text[i], out var bitmap))
            {
                throw new InvalidOperationException($"Missing glyph for '{text[i]}'");
            }

            BlitScaled(canvas, bitmap, format, cursorX, y, 12);
            cursorX += CompactGlyphAdvancePx(bitmap, format, 12);
            if (i + 1 < text.Length)
            {
                cursorX += CompactUiGapPx;
            }
        }
    }

    private static void DrawCompactMarkerGlyph(
        byte[,] canvas,
        Dictionary<int, byte[]> glyphs,
        GbcFontBitmapFormat format,
        char ch,
        int x,
        int y)
    {
        if (!glyphs.TryGetValue(ch, out var bitmap))
        {
            throw new InvalidOperationException($"Missing glyph for '{ch}'");
        }

        BlitScaled(canvas, bitmap, format, x, y, 12);
    }

    private static int MeasureMenuIndexDigitStep(
        Dictionary<int, byte[]> glyphs,
        GbcFontBitmapFormat format)
    {
        var maxDigitAdvance = 0;
        for (var ch = '0'; ch <= '9'; ch++)
        {
            if (!glyphs.TryGetValue(ch, out var bitmap))
            {
                throw new InvalidOperationException($"Missing glyph for '{ch}'");
            }

            maxDigitAdvance = Math.Max(maxDigitAdvance, CompactGlyphAdvancePx(bitmap, format, 12));
        }

        return maxDigitAdvance + CompactUiGapPx;
    }

    private static int MeasureCompactMarkerText(
        Dictionary<int, byte[]> glyphs,
        GbcFontBitmapFormat format,
        string text)
    {
        var width = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (!glyphs.TryGetValue(text[i], out var bitmap))
            {
                throw new InvalidOperationException($"Missing glyph for '{text[i]}'");
            }

            width += CompactGlyphAdvancePx(bitmap, format, 12);
            if (i + 1 < text.Length)
            {
                width += CompactUiGapPx;
            }
        }

        return width;
    }

    private static void DrawMenuIndex(
        byte[,] canvas,
        Dictionary<int, byte[]> glyphs,
        GbcFontBitmapFormat format,
        int value,
        int x,
        int y)
    {
        var digits = value.ToString("D2", CultureInfo.InvariantCulture);
        var digitStep = MeasureMenuIndexDigitStep(glyphs, format);
        for (var i = 0; i < digits.Length; i++)
        {
            DrawCompactMarkerGlyph(canvas, glyphs, format, digits[i], x + (i * digitStep), y);
        }

        DrawCompactMarkerGlyph(canvas, glyphs, format, '.', x + (digits.Length * digitStep), y);
    }

    private static bool IsAsciiCompact(string text)
    {
        return text.Length > 0 && text.All(ch => ch < 128);
    }

    private static void DrawValueText(
        byte[,] canvas,
        Dictionary<int, byte[]> glyphs,
        GbcFontBitmapFormat format,
        string text,
        int x,
        int y)
    {
        if (IsAsciiCompact(text))
        {
            DrawCompactMarkerText(canvas, glyphs, format, text, x, y);
        }
        else
        {
            DrawText(canvas, glyphs, format, text, x, y, 12, 1);
        }
    }

    private static void DrawLabelText(
        byte[,] canvas,
        Dictionary<int, byte[]> glyphs,
        GbcFontBitmapFormat format,
        string text,
        int x,
        int y)
    {
        if (IsAsciiCompact(text))
        {
            DrawCompactMarkerText(canvas, glyphs, format, text, x, y);
        }
        else
        {
            DrawText(canvas, glyphs, format, text, x, y, 12, 1);
        }
    }

    private static string DetailMbcText(byte mbcType)
    {
        return mbcType switch
        {
            0x00 or 0x08 or 0x09 => "ROM",
            0x01 or 0x02 or 0x03 => "MBC1",
            0x05 or 0x06 => "MBC2",
            0x0F or 0x10 or 0x11 or 0x12 or 0x13 => "MBC3",
            0x19 or 0x1A or 0x1B or 0x1C or 0x1D or 0x1E => "MBC5",
            _ => "MBC?",
        };
    }

    private static string FormatSizeKb(int sizeKb)
    {
        if (sizeKb >= 1024)
        {
            if (sizeKb % 1024 == 0) return $"{sizeKb / 1024}MB";
            var wholeMb = sizeKb / 1024;
            var tenthMb = ((sizeKb % 1024) * 10) / 1024;
            return $"{wholeMb}.{tenthMb}MB";
        }

        return $"{sizeKb}KB";
    }

    private static string DetailRomText(int romBanks)
    {
        return FormatSizeKb(romBanks * 16);
    }

    private static string DetailRamText(int ramBanks)
    {
        if (ramBanks == 0) return MenuDetailNoneText;
        return FormatSizeKb(ramBanks * 8);
    }

    private static string DetailSaveText(bool usesDedicatedSave, int ramBanks)
    {
        if (ramBanks == 0) return MenuDetailNoneText;
        return usesDedicatedSave ? MenuDetailSaveDedicatedText : MenuDetailSaveSharedText;
    }

    private static byte[,] CreateFullPageCanvas(
        int pageIndex,
        int pageCount,
        IReadOnlyList<GbcMenuGameInfo> games,
        Dictionary<int, byte[]> glyphs,
        GbcFontBitmapFormat format)
    {
        var canvas = MakeCanvas();
        var startIndex = pageIndex * GamesPerPage;
        var endIndex = Math.Min(startIndex + GamesPerPage, games.Count);

        var pageLabel = $"{pageIndex + 1:D2}/{pageCount:D2}";
        var pageLabelWidth = MeasureCompactMarkerText(glyphs, format, pageLabel);
        var pageLabelX = ScreenW - UiSideMarginPx - pageLabelWidth + PageLabelRightNudgePx;
        var versionWidth = MeasureCompactMarkerText(glyphs, format, MenuVersionText);
        var versionLabelX = ScreenW - UiSideMarginPx - versionWidth + PageLabelRightNudgePx;

        DrawText(canvas, glyphs, format, MenuHeaderText, UiSideMarginPx, 2, 12, 1);
        DrawCompactMarkerText(canvas, glyphs, format, MenuVersionText, versionLabelX, 2);
        DrawCompactMarkerText(canvas, glyphs, format, pageLabel, pageLabelX, 132);
        DrawText(canvas, glyphs, format, MenuFooterText, UiSideMarginPx, 132, 12, 1);

        for (var i = startIndex; i < endIndex; i++)
        {
            var row = i - startIndex;
            var rowY = RowTopStartPx + row * RowStepPx;
            DrawMenuIndex(canvas, glyphs, format, i + 1, MenuIndexX, rowY);
            DrawMainMenuTitleText(canvas, glyphs, format, games[i].TitleCn, ChineseMenuNameX, rowY);
        }

        return canvas;
    }

    private static byte[,] CreateDetailPageCanvas(
        int gameIndex,
        IReadOnlyList<GbcMenuGameInfo> games,
        Dictionary<int, byte[]> glyphs,
        GbcFontBitmapFormat format)
    {
        var canvas = MakeCanvas();
        var game = games[gameIndex];
        var pageLabel = $"{gameIndex + 1:D2}/{games.Count:D2}";
        var pageLabelWidth = MeasureCompactMarkerText(glyphs, format, pageLabel);
        var pageLabelX = ScreenW - UiSideMarginPx - pageLabelWidth + PageLabelRightNudgePx;
        var versionWidth = MeasureCompactMarkerText(glyphs, format, MenuVersionText);
        var versionLabelX = ScreenW - UiSideMarginPx - versionWidth + PageLabelRightNudgePx;
        var visibleNameChars = Math.Max(1, DetailNameWidthPx / (ChineseMenuFontPx + ChineseMenuCharGapPx));

        DrawText(canvas, glyphs, format, MenuDetailTitleText, UiSideMarginPx, 2, 12, 1);
        DrawCompactMarkerText(canvas, glyphs, format, MenuVersionText, versionLabelX, 2);

        var labels = new[]
        {
            MenuDetailIndexLabel + FullWidthColon,
            MenuDetailNameLabel + FullWidthColon,
            MenuDetailTypeLabel + FullWidthColon,
            MenuDetailRomLabel + ":",
            MenuDetailRamLabel + ":",
            MenuDetailSaveLabel + FullWidthColon,
        };

        var values = new[]
        {
            $"{gameIndex + 1:D2}",
            LimitTextByVisibleChars(game.TitleCn, visibleNameChars),
            DetailMbcText(game.MbcType),
            DetailRomText(game.RomBanks),
            DetailRamText(game.RamBanks),
            DetailSaveText(game.UsesDedicatedSave, game.RamBanks),
        };

        for (var row = 0; row < labels.Length; row++)
        {
            var rowY = DetailRowTopY + row * DetailRowStepY;
            DrawLabelText(canvas, glyphs, format, labels[row], DetailLabelX, rowY);
            if (row == 1)
            {
                DrawTitleText(canvas, glyphs, format, values[row], DetailValueX, rowY);
            }
            else
            {
                DrawValueText(canvas, glyphs, format, values[row], DetailValueX, rowY);
            }
        }

        DrawText(canvas, glyphs, format, MenuDetailActionText, UiSideMarginPx, 132, 12, 1);
        DrawCompactMarkerText(canvas, glyphs, format, pageLabel, pageLabelX, 132);
        return canvas;
    }

    private static ulong[] RegionTileKeys(byte[,] canvas, int tileXStart, int tileYStart, int tileW, int tileH)
    {
        var keys = new ulong[tileW * tileH];
        var index = 0;
        for (var tileY = tileYStart; tileY < tileYStart + tileH; tileY++)
        {
            for (var tileX = tileXStart; tileX < tileXStart + tileW; tileX++)
            {
                ulong key = 0;
                for (var py = 0; py < 8; py++)
                {
                    byte rowBits = 0;
                    for (var px = 0; px < 8; px++)
                    {
                        if (canvas[tileY * 8 + py, tileX * 8 + px] != 0)
                        {
                            rowBits |= (byte)(0x80 >> px);
                        }
                    }

                    key = (key << 8) | rowBits;
                }

                keys[index++] = key;
            }
        }

        return keys;
    }

    private static (byte[] TileData, byte[] Tilemap, int TileCount) CanvasRegionToTiles(
        byte[,] canvas, int tileXStart, int tileYStart, int tileW, int tileH, int maxTiles)
    {
        var uniqueTiles = new Dictionary<ulong, int>();
        var tileData = new byte[maxTiles * MenuTileBytes];
        var tilemap = new byte[tileW * tileH];

        var keys = RegionTileKeys(canvas, tileXStart, tileYStart, tileW, tileH);
        for (var i = 0; i < keys.Length; i++)
        {
            var key = keys[i];
            if (!uniqueTiles.TryGetValue(key, out var tileIndex))
            {
                tileIndex = uniqueTiles.Count;
                if (tileIndex >= maxTiles)
                {
                    throw new InvalidOperationException(
                        $"Region needs {tileIndex + 1} unique tiles, exceeds limit {maxTiles}");
                }

                uniqueTiles[key] = tileIndex;
                var baseOffset = tileIndex * MenuTileBytes;
                for (var row = 0; row < 8; row++)
                {
                    var rowBits = (byte)((key >> ((7 - row) * 8)) & 0xFF);
                    tileData[baseOffset + row * 2] = rowBits;
                    tileData[baseOffset + row * 2 + 1] = rowBits;
                }
            }

            tilemap[i] = (byte)tileIndex;
        }

        return (tileData, tilemap, uniqueTiles.Count);
    }

    private static (byte[] UiTileData, byte[] TopBarTilemap, byte[] BottomBarStaticMap,
        byte[] DetailTopBarTilemap, byte[] DetailBottomBarStaticMap, int UiTileCount)
        ExtractStaticUiRegions(byte[,] firstPageCanvas, byte[,] firstDetailCanvas)
    {
        var uniqueUiTiles = new Dictionary<ulong, int>();
        var uiTileData = new byte[MenuUiMaxTiles * MenuTileBytes];
        var topBarTilemap = new byte[MenuTopBarTilemapSize];
        var bottomBarStaticMap = new byte[MenuBottomBarTilemapSize];
        var detailTopBarTilemap = new byte[MenuTopBarTilemapSize];
        var detailBottomBarStaticMap = new byte[MenuBottomBarTilemapSize];

        AppendUiRegion(uniqueUiTiles, uiTileData, topBarTilemap,
            RegionTileKeys(firstPageCanvas, 0, 0, MenuScreenTilesW, MenuTopBarTilesH));

        AppendUiRegion(uniqueUiTiles, uiTileData, bottomBarStaticMap,
            RegionTileKeys(firstPageCanvas, 0, MenuScreenTilesH - MenuBottomBarTilesH, MenuScreenTilesW, MenuBottomBarTilesH));

        AppendUiRegion(uniqueUiTiles, uiTileData, detailTopBarTilemap,
            RegionTileKeys(firstDetailCanvas, 0, 0, MenuScreenTilesW, MenuTopBarTilesH));

        AppendUiRegion(uniqueUiTiles, uiTileData, detailBottomBarStaticMap,
            RegionTileKeys(firstDetailCanvas, 0, MenuScreenTilesH - MenuBottomBarTilesH, MenuScreenTilesW, MenuBottomBarTilesH));

        var uiTileCount = uniqueUiTiles.Count;
        if (uiTileCount == 0 || uiTileCount > MenuUiMaxTiles)
        {
            throw new InvalidOperationException($"Static UI tile count {uiTileCount} out of range [1, {MenuUiMaxTiles}]");
        }

        var trimmedUiTileData = new byte[uiTileCount * MenuTileBytes];
        Array.Copy(uiTileData, 0, trimmedUiTileData, 0, trimmedUiTileData.Length);

        return (trimmedUiTileData, topBarTilemap, bottomBarStaticMap,
            detailTopBarTilemap, detailBottomBarStaticMap, uiTileCount);
    }

    private static void AppendUiRegion(
        Dictionary<ulong, int> uniqueUiTiles,
        byte[] uiTileData,
        byte[] targetMap,
        ulong[] tileKeys)
    {
        for (var i = 0; i < tileKeys.Length; i++)
        {
            var key = tileKeys[i];
            if (!uniqueUiTiles.TryGetValue(key, out var tileIndex))
            {
                tileIndex = uniqueUiTiles.Count;
                if (tileIndex >= MenuUiMaxTiles)
                {
                    throw new InvalidOperationException(
                        $"UI region needs {tileIndex + 1} unique tiles, exceeds limit {MenuUiMaxTiles}");
                }

                uniqueUiTiles[key] = tileIndex;
                var baseOffset = tileIndex * MenuTileBytes;
                for (var row = 0; row < 8; row++)
                {
                    var rowBits = (byte)((key >> ((7 - row) * 8)) & 0xFF);
                    uiTileData[baseOffset + row * 2] = rowBits;
                    uiTileData[baseOffset + row * 2 + 1] = rowBits;
                }
            }

            targetMap[i] = (byte)tileIndex;
        }
    }

    private static (byte[] TileCounts, byte[] TileData, byte[] Tilemaps) ExtractDynamicRegions(
        byte[][,] canvases, int tileXStart, int tileYStart, int tileW, int tileH)
    {
        var count = canvases.Length;
        var tileCounts = new byte[count];
        var allTileData = new byte[count * MenuDynamicMaxTiles * MenuTileBytes];
        var allTilemaps = new byte[count * tileW * tileH];

        for (var i = 0; i < count; i++)
        {
            var (tileData, tilemap, tileCount) = CanvasRegionToTiles(
                canvases[i], tileXStart, tileYStart, tileW, tileH, MenuDynamicMaxTiles);
            tileCounts[i] = (byte)tileCount;
            Array.Copy(tileData, 0, allTileData, i * MenuDynamicMaxTiles * MenuTileBytes, tileData.Length);
            Array.Copy(tilemap, 0, allTilemaps, i * tileW * tileH, tilemap.Length);
        }

        return (tileCounts, allTileData, allTilemaps);
    }

    private static Dictionary<string, int> MakeDynamicOffsets(int pageCount, int detailCount, int dynamicTileCap)
    {
        return MakeDynamicOffsets(pageCount, detailCount, dynamicTileCap, GetDynamicBaseOffset(detailCount), 0, 0);
    }

    private static Dictionary<string, int> MakeDynamicOffsets(
        int pageCount,
        int detailCount,
        int dynamicTileCap,
        int offset,
        int titleScrollTableLength,
        int titleScrollDataLength)
    {
        var offsets = new Dictionary<string, int>();

        offsets["list_tile_counts_offset"] = offset;
        offset += pageCount;
        offset = (offset + 0xFF) & ~0xFF;

        offsets["list_tile_data_offset"] = offset;
        offset += pageCount * dynamicTileCap * MenuTileBytes;
        offset = (offset + 0xFF) & ~0xFF;

        offsets["list_tilemap_offset"] = offset;
        offset += pageCount * MenuListTilemapSize;
        offset = (offset + 0xFF) & ~0xFF;

        offsets["detail_tile_counts_offset"] = offset;
        offset += detailCount;
        offset = (offset + 0xFF) & ~0xFF;

        offsets["detail_tile_data_offset"] = offset;
        offset += detailCount * dynamicTileCap * MenuTileBytes;
        offset = (offset + 0xFF) & ~0xFF;

        offsets["detail_tilemap_offset"] = offset;
        offset += detailCount * MenuDetailTilemapSize;
        offset = (offset + 0xFF) & ~0xFF;

        offsets["title_scroll_table_offset"] = titleScrollTableLength > 0 ? offset : 0;
        offset += titleScrollTableLength;
        offset = (offset + 0xFF) & ~0xFF;

        offsets["title_scroll_data_offset"] = titleScrollDataLength > 0 ? offset : 0;
        return offsets;
    }

    private static int GetDynamicBaseOffset(int gameCount)
    {
        var absoluteOffset = AlignUp(
            FixedMapRomDirectoryOffset + FixedMapHeaderSize + (gameCount * FixedMapRomEntrySize),
            0x100);
        return absoluteOffset - FixedMapConfigOffset;
    }

    private static int AlignUp(int value, int alignment) =>
        ((value + alignment - 1) / alignment) * alignment;

    private static byte[] CreateLayerHeader(
        int pageCount,
        int uiTileCount,
        int gameCount,
        Dictionary<string, int> offsets,
        GbcFontBitmapFormat compactGlyphFormat)
    {
        var header = new byte[MenuLayerHeaderSize];
        Encoding.ASCII.GetBytes("MBCXLYR1").CopyTo(header, 0);
        header[8] = 0x05;
        header[9] = (byte)(uiTileCount & 0xFF);
        header[10] = (byte)(MenuDynamicMaxTiles & 0xFF);
        header[11] = (byte)(pageCount & 0xFF);
        header[12] = (byte)(GamesPerPage & 0xFF);
        header[13] = (byte)compactGlyphFormat.GlyphSizePx;
        header[14] = (byte)compactGlyphFormat.GlyphBytes;
        header[15] = (byte)compactGlyphFormat.RowBytes;
        WriteU16Le(header, 16, (ushort)(gameCount & 0xFFFF));
        WriteU16Le(header, 18, (ushort)(MenuListEntryWords & 0xFFFF));

        WriteU32Le(header, 20, (uint)MenuListStringsOffset);
        WriteU32Le(header, 24, (uint)MenuUiTileDataOffset);
        WriteU32Le(header, 28, (uint)MenuTopBarMapOffset);
        WriteU32Le(header, 32, (uint)MenuBottomBarStaticMapOffset);
        WriteU32Le(header, 36, (uint)MenuCompactGlyphDataOffset);
        WriteU32Le(header, 40, (uint)offsets["list_tile_counts_offset"]);
        WriteU32Le(header, 44, (uint)offsets["list_tile_data_offset"]);
        WriteU32Le(header, 48, (uint)offsets["list_tilemap_offset"]);
        WriteU32Le(header, 52, (uint)offsets["detail_tile_counts_offset"]);
        WriteU32Le(header, 56, (uint)offsets["detail_tile_data_offset"]);
        WriteU32Le(header, 60, (uint)offsets["detail_tilemap_offset"]);
        WriteU32Le(header, 64, (uint)MenuDetailTopBarMapOffset);
        WriteU32Le(header, 68, (uint)MenuDetailBottomBarStaticMapOffset);
        WriteU32Le(header, 72, (uint)offsets.GetValueOrDefault("title_scroll_table_offset"));
        WriteU32Le(header, 76, (uint)offsets.GetValueOrDefault("title_scroll_data_offset"));

        return header;
    }

    private static byte[] CreateGameListStrings(IReadOnlyList<GbcMenuGameInfo> games)
    {
        var encoded = new List<byte>();
        for (var i = 0; i < games.Count; i++)
        {
            var text = $"{i + 1:D2}.{games[i].TitleCn}";
            var utf16Bytes = Encoding.Unicode.GetBytes(text);
            if (utf16Bytes.Length / 2 >= MenuListEntryWords)
            {
                throw new InvalidOperationException($"Game label too long for entry budget: {text}");
            }

            encoded.AddRange(utf16Bytes);
            encoded.AddRange(new byte[] { 0x00, 0x00 });
            var padWords = MenuListEntryWords - (utf16Bytes.Length / 2) - 1;
            encoded.AddRange(new byte[padWords * 2]);
        }

        return encoded.ToArray();
    }

    private static void WriteU16Le(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    private static void WriteU32Le(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
    }
}
