using System.Buffers.Binary;
using GbaMultiMenuBuilder.Models;
using GbaMultiMenuBuilder.Services;

namespace ChisFlashWaterClient;

public partial class Form1
{
    private bool CanEditPendingGbaCovers()
    {
        if (isBusy || isAnalyzingQueueImports || IsGbcWorkspaceMode() ||
            !workspaceRows.Any(row => row.PendingItem is { IsLoadingPlaceholder: false, ImportedPlatform: RomPlatform.Gba }))
        {
            return false;
        }

        return GetSelectedGbaMenuSelection() switch
        {
            GbaMenuSelection.Universal or GbaMenuSelection.Simplified => true,
            GbaMenuSelection.Auto => currentScanResult?.IsUniversalGbaMenuDetected == true ||
                currentScanResult?.IsSimplifiedNsGbaMenuDetected == true,
            _ => false,
        };
    }

    private void MatchPendingGbaCovers(IReadOnlyList<PendingRomImportItem> items)
    {
        List<PendingRomImportItem> gbaItems = items
            .Where(item => !item.IsLoadingPlaceholder && item.ImportedPlatform == RomPlatform.Gba)
            .ToList();
        if (gbaItems.Count == 0) return;

        try
        {
            string? directory = GbaCoverCatalog.FindCatalogDirectory();
            if (directory is null)
            {
                AppendLog(T("error", "cover_catalog_not_found", "The local cover catalog could not be found."));
                return;
            }

            Dictionary<string, GbaCoverCatalogEntry> catalog = GbaCoverCatalog.LoadPrimaryByGameCode(directory);
            int matched = 0;
            foreach (PendingRomImportItem item in gbaItems)
            {
                if (!item.CoverCleared && string.IsNullOrWhiteSpace(item.CoverPath) &&
                    GbaCoverCatalog.TryFindByGameCode(catalog, item.GbaGameCode, out GbaCoverCatalogEntry? entry, out _) &&
                    entry is not null)
                {
                    item.CoverPath = entry.CoverPath;
                    matched++;
                }
            }

            AppendLog(Tf("log", "pending_cover_match_result",
                "Automatically matched {0}/{1} queued GBA covers. Preview them before burning.", matched, gbaItems.Count));
        }
        catch (Exception ex)
        {
            AppendLog(Tf("error", "cover_auto_match_failed", "Automatic cover matching failed: {0}", ex.Message));
        }
    }

    private void ShowPendingGbaCoverEditor()
    {
        if (!CanEditPendingGbaCovers()) return;

        List<WorkspaceListRow> rows = workspaceRows
            .Where(row => row.PendingItem is { IsLoadingPlaceholder: false, ImportedPlatform: RomPlatform.Gba })
            .ToList();
        List<GameEntryModel> games = rows.Select(row => new GameEntryModel
        {
            Title = GetWorkspaceRowDisplayTitle(row),
            RomPath = row.PendingItem!.FilePath,
            CoverPath = row.PendingItem.CoverPath,
            CoverCleared = row.PendingItem.CoverCleared,
        }).ToList();
        GbaMenuType menuType = GetSelectedGbaMenuSelection() == GbaMenuSelection.Simplified ||
            (GetSelectedGbaMenuSelection() == GbaMenuSelection.Auto && currentScanResult?.IsSimplifiedNsGbaMenuDetected == true)
            ? GbaMenuType.SimplifiedNsMenu : GbaMenuType.NsMenu;
        using var dialog = new GbaMultiMenuBuilder.GbaMenuCoverEditorDialog(
            games, menuType, IsBatteryPresentForSelectedMode(GetSelectedBurnProfileMode()), TranslatePendingCoverText);
        dialog.Text = T("ui", "pending_gba_cover_title", "Queued ROM Covers — Save To Queue, Not To Cart");
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        for (int index = 0; index < rows.Count; index++)
        {
            PendingRomImportItem pending = rows[index].PendingItem!;
            pending.DisplayTitle = WaterMenuConfigCodec.NormalizeTitle(games[index].Title);
            pending.CoverPath = games[index].CoverPath;
            pending.CoverCleared = games[index].CoverCleared;
            rows[index].DisplayTitleOverride = pending.DisplayTitle;
        }
        int selectedIndex = dataGridViewRoms.SelectedRows.Count > 0 ? dataGridViewRoms.SelectedRows[0].Index : -1;
        RefreshWorkspaceGrid(selectedIndex);
    }

    private string TranslatePendingCoverText(string section, string key, string fallback)
    {
        if (key is "cover_catalog_not_found" or "cover_auto_match_failed" or "cover_drop_failed")
            return T("error", key, fallback);
        return T(section, key, fallback);
    }

    private void PrepareWorkspaceGbaCovers(
        WaterDeviceProtocol protocol,
        ProgrammerKind programmerKind,
        WaterCartScanResult initialScan,
        IReadOnlyList<WorkspaceListRow> workspaceSnapshot,
        WorkspaceMetadataPlan metadataPlan,
        WorkspaceHeadAreaRewritePlan? headPlan)
    {
        if (!workspaceSnapshot.Any(row => row.PendingItem?.ImportedPlatform == RomPlatform.Gba)) return;
        if (headPlan is not null && !GbaMenuRomService.SupportsCovers(headPlan.HeadAreaImage)) return;
        if (headPlan is null && !initialScan.GbaMenuFirstScreenPreview.IsDetected) return;

        byte[] menuArea = headPlan?.HeadAreaImage ?? ReadRomRangeFromDevice(
            protocol, programmerKind, 0, metadataPlan.MenuConfig.Status.ItemListAddress);
        PrepareWorkspaceGbaCoverImage(initialScan, workspaceSnapshot, metadataPlan, headPlan, menuArea);
    }

    private void PrepareWorkspaceGbaCoverImage(
        WaterCartScanResult initialScan,
        IReadOnlyList<WorkspaceListRow> workspaceSnapshot,
        WorkspaceMetadataPlan metadataPlan,
        WorkspaceHeadAreaRewritePlan? headPlan,
        byte[] menuArea)
    {
        const int trailerOffset = 0x0AFFF0;
        GbaMenuFirstScreenPreviewInfo layout = GbaMenuFirstScreenPreviewCodec.Decode(menuArea, metadataPlan.MenuConfig.Items);
        if (!layout.IsDetected)
            throw new InvalidOperationException(T("error", "cover_pack_layout_invalid", "The current cover pack layout is invalid."));

        List<WorkspaceListRow> rows = workspaceSnapshot
            .Where(row => row.PendingItem is not null || row.CartEntry is not null).ToList();
        if (rows.Count != metadataPlan.MenuConfig.Items.Count)
            throw new InvalidOperationException(T("error", "pending_cover_order_invalid",
                "The queued cover order does not match the final menu directory."));

        string? directory = GbaCoverCatalog.FindCatalogDirectory();
        IReadOnlyDictionary<string, GbaCoverCatalogEntry> catalog = directory is null
            ? new Dictionary<string, GbaCoverCatalogEntry>() : GbaCoverCatalog.LoadPrimaryByGameCode(directory);
        Color[] palette = BuildGbaPreviewCoverPalette(initialScan.GbaMenuFirstScreenPreview);
        List<Bitmap?> covers = [];
        Dictionary<int, string> gameCodes = [];
        try
        {
            for (int index = 0; index < rows.Count; index++)
            {
                WorkspaceListRow row = rows[index];
                Bitmap? cover = null;
                string gameCode = string.Empty;
                if (row.PendingItem is not null)
                {
                    PendingRomImportItem pending = row.PendingItem;
                    gameCode = pending.GbaGameCode;
                    if (!pending.CoverCleared)
                    {
                        string? path = pending.CoverPath;
                        if (string.IsNullOrWhiteSpace(path) &&
                            GbaCoverCatalog.TryFindByGameCode(catalog, gameCode, out GbaCoverCatalogEntry? entry, out _) && entry is not null)
                            path = entry.CoverPath;
                        if (!string.IsNullOrWhiteSpace(path))
                        {
                            using Image source = Image.FromFile(path);
                            cover = GbaMenuCoverPackBuilder.FitCover(source);
                        }
                    }
                }
                else if (row.CartEntry is not null)
                {
                    gameCode = row.CartEntry.HeaderGame?.GameCode ?? string.Empty;
                    if (row.CartEntry.MenuItem is not null)
                    {
                        cover = DecodeGbaMenuCover(initialScan.GbaMenuFirstScreenPreview, row.CartEntry.MenuItem, palette);
                        if (initialScan.GbaMenuFirstScreenPreview.IsDetected &&
                            (row.CartEntry.MenuItem.CoverFlags & 1) != 0 && cover is null)
                            throw new InvalidOperationException(T("error", "preserve_menu_covers_failed",
                                "The current NS menu cover pack could not be preserved. Burn preparation was stopped."));
                        if (string.IsNullOrWhiteSpace(gameCode) &&
                            initialScan.GbaMenuFirstScreenPreview.GameCodesByIndex.TryGetValue(row.CartEntry.MenuItem.CoverIndex, out string? packedCode))
                            gameCode = packedCode ?? string.Empty;
                    }
                }
                covers.Add(cover);
                gameCodes[metadataPlan.MenuConfig.Items[index].StartBucket] = gameCode ?? "UNKN";
            }

            GbaMenuCoverPackBuildResult pack = BuildStagedGbaCoverPack(metadataPlan.MenuConfig.Items, covers, gameCodes);
            int newPackOffset = ResolveCoverPackOffset(menuArea, layout.PackOffset, pack.PackData.Length, trailerOffset);
            int clearStart = Math.Min(layout.PackOffset, newPackOffset);
            Array.Fill(menuArea, (byte)0, clearStart, trailerOffset - clearStart);
            pack.PackData.CopyTo(menuArea, newPackOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(menuArea.AsSpan(trailerOffset + 8), (uint)newPackOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(menuArea.AsSpan(trailerOffset + 12), (uint)(trailerOffset - newPackOffset));
            AssignPackedCoverIndexes(metadataPlan.MenuConfig.Items, covers);
            metadataPlan.ItemListSector = WaterMenuConfigCodec.BuildItemListSector(
                metadataPlan.MenuConfig.Items, metadataPlan.MenuConfig.Status.ItemListAddress, metadataPlan.MenuConfig.Status.SectorSize);
            metadataPlan.CoverMenuArea = menuArea;

            if (headPlan is not null)
            {
                metadataPlan.ItemListSector.CopyTo(menuArea, metadataPlan.MenuConfig.Status.ItemListAddress);
            }
            else
            {
                int sectorSize = initialScan.Geometry.SectorSizeBytes;
                int rewriteStart = clearStart / sectorSize * sectorSize;
                int rewriteEnd = AlignUpToBoundary(trailerOffset + 16, sectorSize);
                metadataPlan.CoverSectors = BuildNorSectorWriteBatch((uint)rewriteStart,
                    menuArea.AsSpan(rewriteStart, rewriteEnd - rewriteStart).ToArray(), sectorSize,
                    T("value", "menu_cover_pack_label", "menu cover pack"));
            }

            AppendLog(Tf("log", "pending_cover_pack_ready", "Prepared {0} menu covers for this burn.",
                metadataPlan.MenuConfig.Items.Count(item => (item.CoverFlags & 1) != 0)));
            if (covers.Count(cover => cover is not null) > GbaMenuCoverPackBuilder.MaxCoverCount)
                AppendLog(Tf("log", "pending_cover_limit", "The menu supports at most {0} covers; later entries will have no cover.",
                    GbaMenuCoverPackBuilder.MaxCoverCount));
        }
        finally
        {
            foreach (Bitmap? cover in covers) cover?.Dispose();
        }
    }
}
