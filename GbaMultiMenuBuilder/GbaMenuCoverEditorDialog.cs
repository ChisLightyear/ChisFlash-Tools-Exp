using System.Drawing.Drawing2D;
using System.Text;
using System.ComponentModel;
using GbaMultiMenuBuilder.Models;
using GbaMultiMenuBuilder.Services;

namespace GbaMultiMenuBuilder;

internal sealed class GbaMenuCoverEditorDialog : DpiAwareForm
{
    private const int ButtonWidth = 112;
    private const int CoverThumbnailSize = 104;
    private static readonly Rectangle CoverThumbnailCloseBounds = new(74, 4, 26, 26);
    private readonly IReadOnlyList<GameEntryModel> _games;
    private readonly bool _simplifiedMenu;
    private readonly bool _batteryPresent;
    private readonly Func<string, string, string, string> _text;
    private readonly string? _catalogDirectory;
    private readonly List<CoverEditItem> _items = [];
    private readonly ImageList _thumbnails = new();
    private readonly ListView _coverList = new();
    private readonly PixelPreviewBox _preview = new();
    private readonly Label _previewTitle = new();
    private readonly Label _positionLabel = new();
    private readonly Button _saveButton = new();
    private int _selectedIndex;
    private bool _editingTitle;

    public GbaMenuCoverEditorDialog(
        IReadOnlyList<GameEntryModel> games,
        GbaMenuType menuType,
        bool batteryPresent,
        Func<string, string, string, string> text,
        string? catalogDirectory = null)
    {
        _games = games;
        _simplifiedMenu = menuType == GbaMenuType.SimplifiedNsMenu;
        _batteryPresent = batteryPresent;
        _text = text;
        _catalogDirectory = catalogDirectory;
        LoadStagedItems();
        InitializeDialog();
        PopulateCoverList();
        SelectGame(0, true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var item in _items)
            {
                item.Cover?.Dispose();
            }
            _thumbnails.Dispose();
        }
        base.Dispose(disposing);
    }

    private void LoadStagedItems()
    {
        var catalogDirectory = GbaCoverCatalog.FindCatalogDirectory(_catalogDirectory);
        IReadOnlyDictionary<string, GbaCoverCatalogEntry> catalog;
        try
        {
            catalog = catalogDirectory is null
                ? new Dictionary<string, GbaCoverCatalogEntry>(StringComparer.OrdinalIgnoreCase)
                : GbaCoverCatalog.LoadPrimaryByGameCode(catalogDirectory);
        }
        catch
        {
            catalog = new Dictionary<string, GbaCoverCatalogEntry>(StringComparer.OrdinalIgnoreCase);
        }

        foreach (var game in _games)
        {
            var gameCode = ReadGameCode(game.RomPath);
            string? persistedCoverPath = null;
            string? resolvedCoverPath = null;
            if (!game.CoverCleared)
            {
                if (!string.IsNullOrWhiteSpace(game.CoverPath) && File.Exists(game.CoverPath))
                {
                    persistedCoverPath = Path.GetFullPath(game.CoverPath);
                    resolvedCoverPath = persistedCoverPath;
                }
                else if (GbaCoverCatalog.TryFindByGameCode(catalog, gameCode, out var match) && match is not null)
                {
                    resolvedCoverPath = match.CoverPath;
                }
            }

            _items.Add(new CoverEditItem
            {
                Title = game.Title,
                OriginalTitle = game.Title,
                RomPath = game.RomPath,
                GameCode = gameCode,
                SaveIndex = int.TryParse(game.SaveSlot, out var saveSlot) && saveSlot > 0
                    ? saveSlot - 1
                    : null,
                CoverPath = persistedCoverPath,
                OriginalCoverPath = persistedCoverPath,
                SourcePath = resolvedCoverPath,
                OriginalSourcePath = resolvedCoverPath,
                CoverCleared = game.CoverCleared,
                OriginalCoverCleared = game.CoverCleared,
                Cover = TryLoadCover(resolvedCoverPath),
            });
        }
    }

    private void InitializeDialog()
    {
        var pageBackground = Color.FromArgb(247, 248, 250);
        Text = Tx("menu_cover_browser", "Menu Cover Browser");
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1280, 720);
        MinimumSize = new Size(960, 560);
        BackColor = pageBackground;
        KeyPreview = true;
        ShowInTaskbar = false;

        _previewTitle.Dock = DockStyle.Top;
        _previewTitle.Height = 54;
        _previewTitle.Padding = new Padding(24, 17, 24, 0);
        _previewTitle.Font = new Font(Font.FontFamily, 12F, FontStyle.Bold);
        _previewTitle.ForeColor = Color.FromArgb(24, 26, 30);

        _preview.Dock = DockStyle.Fill;
        _preview.BackColor = pageBackground;
        _preview.Margin = Padding.Empty;

        _positionLabel.AutoSize = true;
        _positionLabel.TextAlign = ContentAlignment.MiddleCenter;
        _positionLabel.ForeColor = Color.FromArgb(91, 98, 108);
        _positionLabel.Margin = new Padding(16, 12, 16, 0);

        var autoMatchButton = CreateButton(Tx("auto_match_covers", "Auto Match"));
        autoMatchButton.Click += (_, _) => AutoMatchCovers();
        _saveButton.Text = Tx("save_cover_changes", "Save");
        ConfigureButton(_saveButton);
        _saveButton.Enabled = false;
        _saveButton.Click += (_, _) => SaveChanges();
        var cancelButton = CreateButton(Tx("cancel_cover_changes", "Cancel"));
        cancelButton.DialogResult = DialogResult.Cancel;

        var navigation = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 58,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(24, 8, 24, 8),
            BackColor = pageBackground,
        };
        navigation.Controls.Add(autoMatchButton);
        navigation.Controls.Add(_saveButton);
        navigation.Controls.Add(cancelButton);
        navigation.Controls.Add(_positionLabel);

        var previewPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = pageBackground,
            Padding = new Padding(24, 0, 24, 12),
        };
        previewPanel.Controls.Add(_preview);
        previewPanel.Controls.Add(navigation);
        previewPanel.Controls.Add(_previewTitle);

        _thumbnails.ColorDepth = ColorDepth.Depth32Bit;
        _thumbnails.ImageSize = new Size(CoverThumbnailSize, CoverThumbnailSize);
        _coverList.Dock = DockStyle.Fill;
        _coverList.View = View.LargeIcon;
        _coverList.LargeImageList = _thumbnails;
        _coverList.MultiSelect = false;
        _coverList.HideSelection = false;
        _coverList.BorderStyle = BorderStyle.None;
        _coverList.BackColor = Color.White;
        _coverList.ForeColor = Color.FromArgb(24, 26, 30);
        _coverList.LabelWrap = true;
        _coverList.LabelEdit = true;
        _coverList.ShowItemToolTips = true;
        _coverList.AllowDrop = true;
        WireCoverListEvents();

        var coverListTitle = new Label
        {
            Dock = DockStyle.Top,
            Height = 54,
            Padding = new Padding(16, 17, 12, 0),
            Text = Tx("game_covers", "Game Covers"),
            Font = new Font(Font.FontFamily, 11F, FontStyle.Bold),
            ForeColor = Color.FromArgb(24, 26, 30),
        };
        var listPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            Padding = new Padding(0, 0, 8, 8),
        };
        listPanel.Controls.Add(_coverList);
        listPanel.Controls.Add(coverListTitle);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = pageBackground,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.Controls.Add(previewPanel, 0, 0);
        layout.Controls.Add(listPanel, 1, 0);
        Controls.Add(layout);

        KeyDown += (_, eventArgs) =>
        {
            if (_editingTitle || _items.Count == 0)
            {
                return;
            }
            if (eventArgs.KeyCode == Keys.Left)
            {
                SelectGame(_selectedIndex - 1, true);
                eventArgs.SuppressKeyPress = true;
            }
            else if (eventArgs.KeyCode == Keys.Right)
            {
                SelectGame(_selectedIndex + 1, true);
                eventArgs.SuppressKeyPress = true;
            }
        };
    }

    private void PopulateCoverList()
    {
        for (var index = 0; index < _items.Count; index++)
        {
            _thumbnails.Images.Add(CreateThumbnail(_items[index].Cover));
            _coverList.Items.Add(new ListViewItem(_items[index].Title, index)
            {
                Tag = index,
                ToolTipText = BuildToolTip(_items[index]),
            });
        }
    }

    private void WireCoverListEvents()
    {
        _coverList.SelectedIndexChanged += (_, _) =>
        {
            if (_coverList.SelectedIndices.Count > 0 && _coverList.SelectedIndices[0] != _selectedIndex)
            {
                SelectGame(_coverList.SelectedIndices[0], false);
            }
        };
        _coverList.BeforeLabelEdit += (_, _) => _editingTitle = true;
        _coverList.AfterLabelEdit += (_, eventArgs) =>
        {
            _editingTitle = false;
            if (eventArgs.Item < 0 || eventArgs.Item >= _items.Count || eventArgs.Label is null)
            {
                return;
            }

            var normalized = eventArgs.Label.Trim();
            if (normalized.Length == 0)
            {
                eventArgs.CancelEdit = true;
                return;
            }
            var index = eventArgs.Item;
            _items[index].Title = normalized;
            eventArgs.CancelEdit = true;
            BeginInvoke(() =>
            {
                _coverList.Items[index].Text = normalized;
                _coverList.Items[index].ToolTipText = BuildToolTip(_items[index]);
                MarkDirty();
                SelectGame(index, true);
            });
        };
        _coverList.MouseDoubleClick += (_, eventArgs) =>
        {
            var hit = _coverList.HitTest(eventArgs.Location);
            if (hit.Item is not null && (hit.Location & ListViewHitTestLocations.Label) != 0)
            {
                hit.Item.BeginEdit();
            }
        };
        _coverList.MouseDown += (_, eventArgs) =>
        {
            if (eventArgs.Button == MouseButtons.Left &&
                TryGetClearCoverIndex(_coverList, eventArgs.Location, out var clearIndex))
            {
                ClearCover(clearIndex);
                return;
            }
            var hitItem = FindCoverListItemAt(_coverList, eventArgs.Location);
            if (eventArgs.Button == MouseButtons.Right && hitItem is not null)
            {
                hitItem.Selected = true;
                hitItem.Focused = true;
            }
            else if (eventArgs.Button == MouseButtons.Right)
            {
                _coverList.SelectedIndices.Clear();
            }
        };

        var exportMenu = new ContextMenuStrip();
        var undoItem = new ToolStripMenuItem(Tx("undo_cover_change", "Undo Cover Change"));
        var deleteItem = new ToolStripMenuItem(Tx("delete_cover", "Delete Cover"));
        var importItem = new ToolStripMenuItem(Tx("import_cover", "Import Cover"));
        var exportItem = new ToolStripMenuItem(Tx("export_current_cover", "Export Cover"));
        exportMenu.Items.AddRange([undoItem, deleteItem, importItem, new ToolStripSeparator(), exportItem]);
        exportMenu.Opening += (_, eventArgs) =>
        {
            eventArgs.Cancel = _coverList.SelectedIndices.Count == 0;
            if (eventArgs.Cancel)
            {
                return;
            }

            var selectedItem = _items[_coverList.SelectedIndices[0]];
            undoItem.Enabled = HasCoverChanged(selectedItem);
            deleteItem.Enabled = selectedItem.Cover is not null;
            exportItem.Enabled = selectedItem.Cover is not null;
        };
        undoItem.Click += (_, _) => RunForSelectedCover(UndoCoverChange);
        deleteItem.Click += (_, _) => RunForSelectedCover(ClearCover);
        importItem.Click += (_, _) => RunForSelectedCover(ImportCover);
        exportItem.Click += (_, _) =>
        {
            RunForSelectedCover(ExportCover);
        };
        _coverList.ContextMenuStrip = exportMenu;

        _coverList.DragEnter += (sender, eventArgs) =>
            eventArgs.Effect = TryGetDropTarget(eventArgs, out var _, out var _)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        _coverList.DragOver += (sender, eventArgs) =>
        {
            if (!TryGetDropTarget(eventArgs, out var targetIndex, out var _))
            {
                eventArgs.Effect = DragDropEffects.None;
                return;
            }
            eventArgs.Effect = DragDropEffects.Copy;
            SelectGame(targetIndex, true);
        };
        _coverList.DragDrop += (_, eventArgs) =>
        {
            if (TryGetDropTarget(eventArgs, out var targetIndex, out var imagePath) && imagePath is not null)
            {
                ReplaceCover(targetIndex, imagePath, persistPath: true, showError: true);
            }
        };
    }

    private void AutoMatchCovers()
    {
        var directory = GbaCoverCatalog.FindCatalogDirectory(_catalogDirectory);
        if (directory is null)
        {
            MessageBox.Show(this, Tx("cover_catalog_not_found", "The local cover catalog could not be found."),
                Tx("auto_match_covers", "Auto Match"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            var catalog = GbaCoverCatalog.LoadPrimaryByGameCode(directory);
            var matched = 0;
            for (var index = 0; index < _items.Count; index++)
            {
                if (!GbaCoverCatalog.TryFindByGameCode(catalog, _items[index].GameCode, out var match) || match is null)
                {
                    continue;
                }
                if (ReplaceCover(index, match.CoverPath, persistPath: false, showError: false))
                {
                    matched++;
                }
            }

            MessageBox.Show(this,
                string.Format(Tx("cover_match_result", "Matched {0} of {1} covers."), matched, _items.Count),
                Tx("auto_match_covers", "Auto Match"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                string.Format(Tx("cover_auto_match_failed", "Automatic cover matching failed: {0}"), ex.Message),
                Tx("auto_match_covers", "Auto Match"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private bool ReplaceCover(int index, string path, bool persistPath, bool showError)
    {
        Bitmap? replacement = null;
        try
        {
            using var source = Image.FromFile(path);
            replacement = GbaMenuCoverPackBuilder.FitCover(source);
            _items[index].Cover?.Dispose();
            _items[index].Cover = replacement;
            replacement = null;
            _items[index].SourcePath = Path.GetFullPath(path);
            _items[index].CoverPath = persistPath ? _items[index].SourcePath : null;
            _items[index].CoverCleared = false;
            RefreshListItem(index);
            MarkDirty();
            SelectGame(index, true);
            return true;
        }
        catch (Exception ex)
        {
            replacement?.Dispose();
            if (showError)
            {
                MessageBox.Show(this,
                    string.Format(Tx("cover_drop_failed", "Unable to replace the cover: {0}"), ex.Message),
                    Tx("game_covers", "Game Covers"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return false;
        }
    }

    private void ClearCover(int index)
    {
        _items[index].Cover?.Dispose();
        _items[index].Cover = null;
        _items[index].CoverPath = null;
        _items[index].SourcePath = null;
        _items[index].CoverCleared = true;
        RefreshListItem(index);
        MarkDirty();
        SelectGame(index, true);
    }

    private void ImportCover(int index)
    {
        using var dialog = new OpenFileDialog
        {
            Title = Tx("import_cover", "Import Cover"),
            Filter = _text("file_dialog", "png_filter", "PNG Image (*.png)|*.png|All files (*.*)|*.*"),
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            ReplaceCover(index, dialog.FileName, persistPath: true, showError: true);
        }
    }

    private void UndoCoverChange(int index)
    {
        var item = _items[index];
        var restoredCover = TryLoadCover(item.OriginalSourcePath);
        item.Cover?.Dispose();
        item.Cover = restoredCover;
        item.CoverPath = item.OriginalCoverPath;
        item.SourcePath = item.OriginalSourcePath;
        item.CoverCleared = item.OriginalCoverCleared;
        RefreshListItem(index);
        RefreshSaveButtonState();
        SelectGame(index, true);
    }

    private void RefreshListItem(int index)
    {
        _thumbnails.Images[index] = CreateThumbnail(_items[index].Cover);
        _coverList.Items[index].ToolTipText = BuildToolTip(_items[index]);
        _coverList.Invalidate(_coverList.Items[index].Bounds);
    }

    private void SelectGame(int index, bool synchronizeList)
    {
        if (_items.Count == 0)
        {
            _previewTitle.Text = Tx("no_games_to_preview", "No games to preview");
            _positionLabel.Text = "000 / 000";
            _preview.PreviewImage = null;
            return;
        }

        _selectedIndex = Math.Clamp(index, 0, _items.Count - 1);
        _previewTitle.Text = _items[_selectedIndex].Title;
        _positionLabel.Text = $"{_selectedIndex + 1:D3} / {_items.Count:D3}";
        _preview.PreviewImage = RenderMenuPreview(_selectedIndex);
        if (synchronizeList)
        {
            _coverList.SelectedIndices.Clear();
            _coverList.Items[_selectedIndex].Selected = true;
            _coverList.Items[_selectedIndex].Focused = true;
            _coverList.EnsureVisible(_selectedIndex);
        }
    }

    private Bitmap RenderMenuPreview(int selectedIndex)
    {
        const int screenWidth = 240;
        const int screenHeight = 160;
        const int cardWidth = 56;
        const int cardHeight = 56;
        const int cardPitch = 57;
        const int cardX = 7;
        const int cardY = 44;

        var bitmap = new Bitmap(screenWidth, screenHeight);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.None;
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        graphics.Clear(Color.FromArgb(247, 248, 250));

        var divider = Color.FromArgb(216, 219, 224);
        var shadow = Color.FromArgb(217, 220, 226);
        var surface = Color.White;
        var selection = Color.FromArgb(24, 154, 211);
        var text = Color.FromArgb(24, 26, 30);
        using var dividerPen = new Pen(divider);
        using var selectionPen = new Pen(selection, 2);
        using var backgroundPen = new Pen(Color.FromArgb(247, 248, 250));
        using var shadowBrush = new SolidBrush(shadow);
        using var surfaceBrush = new SolidBrush(surface);
        using var textBrush = new SolidBrush(text);

        if (_simplifiedMenu)
        {
            graphics.FillRectangle(surfaceBrush, 0, 124, screenWidth, 36);
            graphics.DrawLine(dividerPen, 0, 123, screenWidth - 1, 123);
            DrawPreviewText(graphics, "CHIS", new Rectangle(11, 5, 52, 12), text, 8F, ContentAlignment.TopLeft);
        }
        else
        {
            graphics.DrawLine(dividerPen, 7, 143, 232, 143);
            DrawPreviewHeader(graphics, shadowBrush, surfaceBrush);
        }

        var total = _items.Count;
        selectedIndex = total == 0 ? 0 : Math.Clamp(selectedIndex, 0, total - 1);
        DrawPreviewText(
            graphics,
            total > 0 ? $"{selectedIndex + 1:D3}/{total:D3}" : "000/000",
            new Rectangle(174, 5, 58, 10),
            text,
            7F,
            ContentAlignment.TopRight);

        var firstVisible = total <= 4 ? 0 : Math.Clamp(selectedIndex - 2, 0, total - 4);
        if (total > 0)
        {
            var selectedSlot = selectedIndex - firstVisible;
            DrawPreviewText(
                graphics,
                _items[selectedIndex].Title,
                new Rectangle(cardX + selectedSlot * cardPitch - cardWidth / 2, 21, cardWidth * 2, 13),
                selection,
                8F,
                ContentAlignment.TopCenter);
        }

        Color[] fallbackCardColors =
        [
            Color.FromArgb(231, 76, 91),
            Color.FromArgb(240, 140, 70),
            Color.FromArgb(227, 179, 65),
            Color.FromArgb(66, 169, 107),
            Color.FromArgb(47, 169, 184),
        ];
        var palette = BuildPreviewPalette();
        var lastVisible = Math.Min(total - 1, firstVisible + 3);
        for (var index = lastVisible; index >= firstVisible; index--)
        {
            if (index == selectedIndex)
            {
                continue;
            }

            var slot = index - firstVisible;
            DrawPreviewCard(
                graphics,
                _items[index],
                palette,
                cardX + slot * cardPitch,
                cardY,
                cardWidth,
                cardHeight,
                fallbackCardColors[index % fallbackCardColors.Length],
                false,
                selectionPen,
                backgroundPen,
                shadowBrush);
        }

        if (total > 0)
        {
            var selectedSlot = selectedIndex - firstVisible;
            DrawPreviewCard(
                graphics,
                _items[selectedIndex],
                palette,
                cardX + selectedSlot * cardPitch,
                cardY - 3,
                cardWidth,
                cardHeight,
                fallbackCardColors[selectedIndex % fallbackCardColors.Length],
                true,
                selectionPen,
                backgroundPen,
                shadowBrush);
        }

        if (_simplifiedMenu)
        {
            if (total > 0 && _batteryPresent && _items[selectedIndex].SaveIndex.HasValue)
            {
                DrawPreviewText(
                    graphics,
                    $"SAVE {_items[selectedIndex].SaveIndex!.Value + 1:D2}",
                    new Rectangle(11, 138, 70, 13),
                    text,
                    7F,
                    ContentAlignment.TopLeft);
            }
            using var readyBrush = new SolidBrush(Color.FromArgb(66, 169, 107));
            graphics.FillRectangle(readyBrush, 225, 141, 4, 4);
            DrawPreviewText(graphics, "SYSTEM READY", new Rectangle(144, 138, 73, 13), text, 7F, ContentAlignment.TopRight);
        }
        else
        {
            DrawPreviewToolbar(graphics, divider, shadow, surface, selection, text);
            DrawPreviewFooter(graphics, text, surface);
        }

        return bitmap;
    }

    private static Color[] BuildPreviewPalette()
    {
        var palette = Enumerable.Repeat(Color.White, 256).ToArray();
        var paletteData = GbaMenuCoverPackBuilder.BuildPaletteData();
        var colorCount = Math.Min(
            paletteData.Length / 2,
            palette.Length - GbaMenuCoverPackBuilder.PaletteBase);
        for (var index = 0; index < colorCount; index++)
        {
            var offset = index * 2;
            var rgb555 = (ushort)(paletteData[offset] | (paletteData[offset + 1] << 8));
            palette[GbaMenuCoverPackBuilder.PaletteBase + index] = Color.FromArgb(
                ExpandRgb555Component(rgb555 & 0x1F),
                ExpandRgb555Component((rgb555 >> 5) & 0x1F),
                ExpandRgb555Component((rgb555 >> 10) & 0x1F));
        }

        return palette;
    }

    private static void DrawPreviewCard(
        Graphics graphics,
        CoverEditItem item,
        Color[] palette,
        int x,
        int y,
        int width,
        int height,
        Color fallbackColor,
        bool selected,
        Pen selectionPen,
        Pen backgroundPen,
        Brush shadowBrush)
    {
        var cardX = selected ? x - 1 : x;
        var cardY = selected ? y - 1 : y;
        var cardWidth = selected ? width + 2 : width;
        var cardHeight = selected ? height + 2 : height;
        graphics.FillRectangle(shadowBrush, cardX + 2, cardY + 3, cardWidth, cardHeight);
        if (selected)
        {
            graphics.DrawRectangle(selectionPen, cardX - 3, cardY - 3, cardWidth + 5, cardHeight + 5);
            graphics.DrawRectangle(backgroundPen, cardX - 1, cardY - 1, cardWidth + 1, cardHeight + 1);
        }

        using var fallbackBrush = new SolidBrush(fallbackColor);
        graphics.FillRectangle(fallbackBrush, cardX, cardY, cardWidth, cardHeight);
        if (item.Cover is null)
        {
            DrawPreviewText(
                graphics,
                item.Title,
                new Rectangle(x + 2, y + 10, width - 4, height - 12),
                Color.White,
                7F,
                ContentAlignment.MiddleCenter,
                true);
            return;
        }

        var pixels = GbaMenuCoverPackBuilder.Quantize(item.Cover);
        using var cover = new Bitmap(width, height);
        for (var py = 0; py < height; py++)
        {
            for (var px = 0; px < width; px++)
            {
                cover.SetPixel(px, py, palette[pixels[py * width + px]]);
            }
        }

        graphics.DrawImageUnscaled(cover, x, y);
    }

    private static void DrawPreviewHeader(Graphics graphics, Brush shadow, Brush surface)
    {
        graphics.FillEllipse(shadow, 5, 3, 20, 20);
        graphics.FillEllipse(surface, 6, 4, 18, 18);
        using var edge = new SolidBrush(Color.FromArgb(217, 145, 25));
        using var cheese = new SolidBrush(Color.FromArgb(255, 217, 74));
        using var hole = new SolidBrush(Color.FromArgb(201, 120, 22));
        Point[] wedge = [new(8, 17), new(9, 10), new(19, 6), new(23, 10), new(22, 17), new(15, 19)];
        graphics.FillPolygon(edge, wedge);
        Point[] inner = [new(9, 16), new(10, 11), new(19, 7), new(22, 10), new(21, 16), new(15, 18)];
        graphics.FillPolygon(cheese, inner);
        graphics.FillRectangle(hole, 14, 9, 2, 1);
        graphics.FillRectangle(hole, 11, 12, 2, 2);
        graphics.FillRectangle(hole, 18, 14, 2, 2);
    }

    private static void DrawPreviewToolbar(
        Graphics graphics,
        Color divider,
        Color shadow,
        Color surface,
        Color selection,
        Color text)
    {
        Color[] colors =
        [
            Color.FromArgb(231, 76, 91),
            Color.FromArgb(240, 140, 70),
            selection,
            Color.FromArgb(66, 169, 107),
            text,
            text,
        ];
        for (var index = 0; index < colors.Length; index++)
        {
            var centerX = 69 + index * 21;
            using var shadowBrush = new SolidBrush(shadow);
            using var dividerBrush = new SolidBrush(divider);
            using var surfaceBrush = new SolidBrush(surface);
            graphics.FillEllipse(shadowBrush, centerX - 8, 113, 18, 18);
            graphics.FillEllipse(dividerBrush, centerX - 9, 112, 18, 18);
            graphics.FillEllipse(surfaceBrush, centerX - 8, 113, 16, 16);
            using var iconPen = new Pen(colors[index]);
            using var iconBrush = new SolidBrush(colors[index]);
            if (index == 0)
            {
                graphics.DrawRectangle(iconPen, centerX - 4, 116, 8, 10);
                graphics.DrawLine(iconPen, centerX - 2, 119, centerX + 2, 119);
                graphics.DrawLine(iconPen, centerX - 2, 122, centerX + 2, 122);
            }
            else if (index == 1)
            {
                graphics.DrawRectangle(iconPen, centerX - 5, 116, 10, 10);
                graphics.DrawRectangle(iconPen, centerX - 2, 121, 4, 3);
                graphics.FillRectangle(iconBrush, centerX - 2, 117, 4, 2);
            }
            else if (index == 2)
            {
                graphics.DrawRectangle(iconPen, centerX - 5, 117, 10, 9);
                graphics.FillRectangle(iconBrush, centerX - 2, 119, 2, 2);
                graphics.FillRectangle(iconBrush, centerX + 2, 122, 2, 2);
                graphics.FillRectangle(iconBrush, centerX - 3, 123, 2, 2);
            }
            else if (index == 3)
            {
                graphics.DrawRectangle(iconPen, centerX - 5, 117, 3, 9);
                graphics.DrawRectangle(iconPen, centerX + 2, 117, 3, 9);
                graphics.DrawLine(iconPen, centerX - 4, 121, centerX - 2, 121);
                graphics.DrawLine(iconPen, centerX + 2, 120, centerX + 4, 120);
            }
            else if (index == 4)
            {
                graphics.DrawEllipse(iconPen, centerX - 4, 117, 8, 8);
                graphics.DrawEllipse(iconPen, centerX - 1, 120, 2, 2);
                graphics.DrawLine(iconPen, centerX, 115, centerX, 118);
                graphics.DrawLine(iconPen, centerX, 124, centerX, 127);
            }
            else
            {
                graphics.DrawEllipse(iconPen, centerX - 4, 117, 8, 8);
                graphics.DrawLine(iconPen, centerX, 115, centerX, 121);
            }
        }
    }

    private static void DrawPreviewFooter(Graphics graphics, Color text, Color surface)
    {
        using var pen = new Pen(text);
        graphics.DrawRectangle(pen, 10, 149, 14, 8);
        graphics.DrawRectangle(pen, 7, 149, 3, 8);
        graphics.DrawRectangle(pen, 24, 149, 3, 8);
        using var button = new SolidBrush(text);
        graphics.FillEllipse(button, 181, 149, 8, 8);
        DrawPreviewText(graphics, "A", new Rectangle(181, 148, 8, 9), surface, 6F, ContentAlignment.MiddleCenter);
        DrawPreviewText(graphics, "START", new Rectangle(190, 147, 42, 12), text, 7F, ContentAlignment.MiddleLeft);
    }

    private static void DrawPreviewText(
        Graphics graphics,
        string value,
        Rectangle bounds,
        Color color,
        float size,
        ContentAlignment alignment,
        bool wordBreak = false)
    {
        using var font = new Font("Microsoft YaHei UI", size, FontStyle.Regular, GraphicsUnit.Pixel);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = alignment switch
            {
                ContentAlignment.TopRight or ContentAlignment.MiddleRight => StringAlignment.Far,
                ContentAlignment.TopCenter or ContentAlignment.MiddleCenter => StringAlignment.Center,
                _ => StringAlignment.Near,
            },
            LineAlignment = alignment is ContentAlignment.MiddleLeft or ContentAlignment.MiddleCenter or ContentAlignment.MiddleRight
                ? StringAlignment.Center
                : StringAlignment.Near,
            Trimming = StringTrimming.EllipsisCharacter,
        };
        if (!wordBreak)
        {
            format.FormatFlags |= StringFormatFlags.NoWrap;
        }

        var state = graphics.Save();
        try
        {
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
            using var brush = new SolidBrush(color);
            graphics.DrawString(value, font, brush, bounds, format);
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    private static int ExpandRgb555Component(int value) => (value << 3) | (value >> 2);

    private void SaveChanges()
    {
        for (var index = 0; index < _games.Count; index++)
        {
            _games[index].Title = _items[index].Title;
            _games[index].CoverPath = _items[index].CoverPath;
            _games[index].CoverCleared = _items[index].CoverCleared;
        }
        DialogResult = DialogResult.OK;
        Close();
    }

    private void ExportCover(int index)
    {
        if (_items[index].Cover is not Image cover)
        {
            return;
        }
        using var dialog = new SaveFileDialog
        {
            FileName = SanitizeFileName(_items[index].Title) + ".png",
            Filter = "PNG Image (*.png)|*.png|All files (*.*)|*.*",
            AddExtension = true,
            DefaultExt = "png",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            cover.Save(dialog.FileName, System.Drawing.Imaging.ImageFormat.Png);
        }
    }

    private bool TryGetDropTarget(DragEventArgs eventArgs, out int targetIndex, out string? imagePath)
    {
        targetIndex = -1;
        imagePath = null;
        if (eventArgs.Data?.GetData(DataFormats.FileDrop) is not string[] { Length: 1 } files || !File.Exists(files[0]))
        {
            return false;
        }
        var point = _coverList.PointToClient(new Point(eventArgs.X, eventArgs.Y));
        var target = FindCoverListItemAt(_coverList, point);
        if (target is null)
        {
            return false;
        }
        targetIndex = target.Index;
        imagePath = files[0];
        return true;
    }

    private void RunForSelectedCover(Action<int> action)
    {
        if (_coverList.SelectedIndices.Count > 0)
        {
            action(_coverList.SelectedIndices[0]);
        }
    }

    private void MarkDirty() => RefreshSaveButtonState();

    private void RefreshSaveButtonState() =>
        _saveButton.Enabled = _items.Any(item =>
            !string.Equals(item.Title, item.OriginalTitle, StringComparison.Ordinal) ||
            HasCoverChanged(item));

    private static bool HasCoverChanged(CoverEditItem item) =>
        item.CoverCleared != item.OriginalCoverCleared ||
        !string.Equals(item.CoverPath, item.OriginalCoverPath, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(item.SourcePath, item.OriginalSourcePath, StringComparison.OrdinalIgnoreCase);

    private string Tx(string key, string fallback) => _text("ui", key, fallback);

    private static Button CreateButton(string text)
    {
        var button = new Button { Text = text };
        ConfigureButton(button);
        return button;
    }

    private static void ConfigureButton(Button button)
    {
        button.Width = ButtonWidth;
        button.Height = 34;
        button.Margin = new Padding(0, 4, 8, 4);
        button.UseVisualStyleBackColor = true;
    }

    private static Bitmap? TryLoadCover(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }
        try
        {
            using var source = Image.FromFile(path);
            return GbaMenuCoverPackBuilder.FitCover(source);
        }
        catch
        {
            return null;
        }
    }

    private static Bitmap CreateThumbnail(Image? cover)
    {
        var thumbnail = new Bitmap(CoverThumbnailSize, CoverThumbnailSize);
        using var graphics = Graphics.FromImage(thumbnail);
        graphics.Clear(Color.FromArgb(238, 240, 244));
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        if (cover is not null)
        {
            graphics.DrawImage(cover, new Rectangle(6, 6, 92, 92));
        }
        graphics.FillRectangle(Brushes.White, CoverThumbnailCloseBounds);
        graphics.DrawRectangle(Pens.DimGray, CoverThumbnailCloseBounds);
        using var closeFont = new Font((SystemFonts.MessageBoxFont ?? Control.DefaultFont).FontFamily, 11F, FontStyle.Regular, GraphicsUnit.Pixel);
        TextRenderer.DrawText(graphics, "\u00D7", closeFont, CoverThumbnailCloseBounds, Color.FromArgb(38, 42, 48),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        return thumbnail;
    }

    private static bool TryGetClearCoverIndex(ListView coverList, Point location, out int index)
    {
        foreach (ListViewItem item in coverList.Items)
        {
            var iconBounds = item.GetBounds(ItemBoundsPortion.Icon);
            if (iconBounds.Width <= 0 || iconBounds.Height <= 0)
            {
                continue;
            }

            var scaleX = iconBounds.Width / (float)CoverThumbnailSize;
            var scaleY = iconBounds.Height / (float)CoverThumbnailSize;
            var clearBounds = new Rectangle(
                iconBounds.Left + (int)Math.Round(CoverThumbnailCloseBounds.Left * scaleX),
                iconBounds.Top + (int)Math.Round(CoverThumbnailCloseBounds.Top * scaleY),
                Math.Max(1, (int)Math.Round((CoverThumbnailCloseBounds.Width + 1) * scaleX)),
                Math.Max(1, (int)Math.Round((CoverThumbnailCloseBounds.Height + 1) * scaleY)));
            if (clearBounds.Contains(location))
            {
                index = item.Index;
                return true;
            }
        }

        index = -1;
        return false;
    }

    private static ListViewItem? FindCoverListItemAt(ListView coverList, Point location) =>
        coverList.GetItemAt(location.X, location.Y) ??
        coverList.Items.Cast<ListViewItem>().FirstOrDefault(item => item.Bounds.Contains(location));

    private static string BuildToolTip(CoverEditItem item)
    {
        var detail = item.CoverCleared
            ? "No cover"
            : string.IsNullOrWhiteSpace(item.SourcePath)
                ? "No matched cover"
                : Path.GetFileName(item.SourcePath);
        return $"{item.Title}\r\n{item.GameCode}\r\n{detail}";
    }

    private static string ReadGameCode(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length < 0xB0)
            {
                return string.Empty;
            }
            stream.Position = 0xAC;
            Span<byte> bytes = stackalloc byte[4];
            return stream.Read(bytes) == 4 ? Encoding.ASCII.GetString(bytes).Trim().ToUpperInvariant() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }

    private sealed class CoverEditItem
    {
        public string Title { get; set; } = string.Empty;
        public string OriginalTitle { get; init; } = string.Empty;
        public string RomPath { get; init; } = string.Empty;
        public string GameCode { get; init; } = string.Empty;
        public int? SaveIndex { get; init; }
        public string? CoverPath { get; set; }
        public string? OriginalCoverPath { get; init; }
        public string? SourcePath { get; set; }
        public string? OriginalSourcePath { get; init; }
        public bool CoverCleared { get; set; }
        public bool OriginalCoverCleared { get; init; }
        public Bitmap? Cover { get; set; }
    }

    private sealed class PixelPreviewBox : Control
    {
        private Image? _previewImage;

        public PixelPreviewBox()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Image? PreviewImage
        {
            get => _previewImage;
            set
            {
                var previous = _previewImage;
                _previewImage = value;
                previous?.Dispose();
                Invalidate();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _previewImage?.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs eventArgs)
        {
            base.OnPaint(eventArgs);
            if (_previewImage is null)
            {
                return;
            }
            var scale = Math.Max(1, Math.Min(ClientSize.Width / _previewImage.Width, ClientSize.Height / _previewImage.Height));
            var destination = new Rectangle(
                (ClientSize.Width - _previewImage.Width * scale) / 2,
                (ClientSize.Height - _previewImage.Height * scale) / 2,
                _previewImage.Width * scale,
                _previewImage.Height * scale);
            eventArgs.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            eventArgs.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
            eventArgs.Graphics.DrawImage(_previewImage, destination);
        }
    }
}
