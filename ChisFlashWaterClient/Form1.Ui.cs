using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using ChisFlashWaterClient.Services;
using GbaMultiMenuBuilder.Models;
using GbaMultiMenuBuilder.Services;
using ClientAppConfigService = ChisFlashWaterClient.Services.AppConfigService;
using ClientUiLanguage = ChisFlashWaterClient.Services.UiLanguage;

namespace ChisFlashWaterClient;

public partial class Form1
{
    private const int HeaderActionButtonWidth = 104;
    private const int ContentSplitPanel1MinSize = 220;
    private const int ContentSplitPanel2MinSize = 120;
    private const int ContentSplitPreferredLogHeight = 148;
    private const int MenuBackgroundPreviewColumnWidth = 144;
    private ToolTip infoValueToolTip = null!;
    private const int MenuBackgroundPreviewCaptionMinHeight = 24;
    private const int MenuBackgroundPreviewDialogDefaultWidth = 960;
    private const int MenuBackgroundPreviewDialogMinWidth = 540;
    private const int GbcPreviewScreenWidth = 160;
    private const int GbcPreviewScreenHeight = 144;
    private const int GbaCoverThumbnailSize = 104;
    private static readonly Rectangle GbaCoverThumbnailCloseBounds = new(74, 4, 26, 26);

    private async Task RunUiActionSafelyAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            RecoverFromUiException(ex, releaseBusyState: true);
        }
    }

    internal void RecoverFromUiException(Exception exception)
    {
        RecoverFromUiException(exception, releaseBusyState: !isBusy);
    }

    private void RecoverFromUiException(Exception exception, bool releaseBusyState)
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        if (InvokeRequired)
        {
            if (IsHandleCreated)
            {
                try
                {
                    BeginInvoke(new Action<Exception, bool>(RecoverFromUiException), exception, releaseBusyState);
                }
                catch (InvalidOperationException)
                {
                    // The form was closed while the worker was reporting its failure.
                }
            }

            return;
        }

        string failureMessage;
        try
        {
            failureMessage = GetFriendlyUiFailureMessage(exception);
        }
        catch
        {
            failureMessage = exception.GetBaseException().Message;
        }

        if (releaseBusyState)
        {
            try
            {
                AbortMeasuredProgress();
            }
            catch
            {
            }
        }

        try
        {
            AppendLog(Tf(
                "log",
                "ui_action_failed",
                "Operation failed: {0}",
                failureMessage));
        }
        catch
        {
        }

        if (releaseBusyState)
        {
            try
            {
                SetBusy(false, T("status", "operation_failed", "Operation failed"));
            }
            catch
            {
                isBusy = false;
                UseWaitCursor = false;
            }
        }
    }

    private void BuildInterface()
    {
        SuspendLayout();

        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        MinimumSize = new Size(1080, 760);
        ClientSize = new Size(1420, 840);
        StartPosition = FormStartPosition.CenterScreen;

        BuildMainMenu();

        rootLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(12),
        };
        rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 132F));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));

        var headerPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 8,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 4),
        };
        headerPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        headerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        headerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        for (int column = 2; column < 8; column++)
            headerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        labelPortCaption = CreateHeaderLabel();
        labelBeggarCartModeCaption = CreateHeaderLabel();
        comboBoxPorts = new ComboBox
        {
            AutoSize = true,
            DropDownStyle = ComboBoxStyle.DropDownList,
            IntegralHeight = false,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = new Padding(4),
        };
        comboBoxPorts.DropDown += (_, _) => AdjustPortDropDownWidth();
        comboBoxPorts.SelectedIndexChanged += (_, _) =>
        {
            RefreshBurnProfileModeOptions(GetSelectedBurnProfileMode());
            InvalidateFcDevice();
            if (dataGridViewRoms is not null && !dataGridViewRoms.IsDisposed && dataGridViewRoms.Columns.Count > 0)
            {
                int selectedIndex = dataGridViewRoms.SelectedRows.Count > 0
                    ? dataGridViewRoms.SelectedRows[0].Index
                    : -1;
                RebuildWorkspaceRows(selectedIndex);
            }
            UpdateActionButtons(isBusy);
        };

        radioButtonBeggarCartModeGba = new RadioButton
        {
            AutoSize = true,
            Anchor = AnchorStyles.None,
            Margin = new Padding(4),
            Checked = true,
        };
        radioButtonBeggarCartModeGb = new RadioButton
        {
            AutoSize = true,
            Anchor = AnchorStyles.None,
            Margin = new Padding(4),
        };
        radioButtonBeggarCartModeGba.CheckedChanged += (_, _) => HandleBeggarCartModeSelectionChanged();
        radioButtonBeggarCartModeGb.CheckedChanged += (_, _) => HandleBeggarCartModeSelectionChanged();

        panelBeggarCartMode = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        panelBeggarCartMode.Controls.Add(radioButtonBeggarCartModeGba);
        panelBeggarCartMode.Controls.Add(radioButtonBeggarCartModeGb);
        beggarCartModeSelectionInitialized = true;

        panelProgrammerModeHost = new Panel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.None,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        panelProgrammerModeHost.Controls.Add(panelBeggarCartMode);

        buttonRefreshPorts = CreateHeaderButton(string.Empty, HeaderActionButtonWidth);
        buttonRefreshPorts.Click += (_, _) => RefreshPorts(resetAnalysisState: true);

        buttonScan = CreateHeaderButton(string.Empty, HeaderActionButtonWidth);
        buttonScan.Click += async (_, _) => await RunUiActionSafelyAsync(ScanSelectedPortAsync);

        labelLanguageCaption = CreateHeaderLabel();

        comboBoxLanguage = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            IntegralHeight = false,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = new Padding(4),
            DataSource = availableLanguages.ToList(),
        };
        comboBoxLanguage.SelectedIndexChanged += comboBoxLanguage_SelectedIndexChanged;

        labelStatus = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(60, 88, 122),
            AutoEllipsis = true,
            Padding = new Padding(4, 0, 4, 0),
        };

        var footerPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 4, 0, 0),
            Padding = new Padding(0),
        };
        footerPanel.Controls.Add(labelStatus);

        headerPanel.Controls.Add(labelPortCaption, 0, 0);
        headerPanel.Controls.Add(comboBoxPorts, 1, 0);
        headerPanel.Controls.Add(buttonRefreshPorts, 2, 0);
        headerPanel.Controls.Add(buttonScan, 3, 0);
        headerPanel.Controls.Add(labelBeggarCartModeCaption, 4, 0);
        headerPanel.Controls.Add(panelProgrammerModeHost, 5, 0);
        headerPanel.Controls.Add(labelLanguageCaption, 6, 0);
        headerPanel.Controls.Add(comboBoxLanguage, 7, 0);

        BuildInfoGroup();
        BuildListGroup();
        BuildLogGroup();

        splitContainerContent = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            FixedPanel = FixedPanel.Panel2,
            Margin = new Padding(0, 3, 0, 0),
            SplitterWidth = 8,
        };
        splitContainerContent.Panel1.Controls.Add(groupBoxList);
        splitContainerContent.Panel2.Controls.Add(groupBoxLog);
        splitContainerContent.SizeChanged += (_, _) => EnsureContentSplitterLayout();

        rootLayout.Controls.Add(headerPanel, 0, 0);
        rootLayout.Controls.Add(groupBoxInfo, 0, 1);
        rootLayout.Controls.Add(splitContainerContent, 0, 2);
        rootLayout.Controls.Add(footerPanel, 0, 3);

        Controls.Add(rootLayout);
        Controls.Add(mainMenuStrip);
        MainMenuStrip = mainMenuStrip;
        ConfigureDragDropTargets();
        InitializeMeasuredProgress();
        UpdateMenuBackgroundPreviewLayout();
        if (!contentSplitterLayoutScheduled)
        {
            contentSplitterLayoutScheduled = true;
            Shown += OnFirstShownEnsureContentSplitterLayout;
        }
        ResumeLayout(false);
        PerformLayout();
    }

    private void BuildMainMenu()
    {
        mainMenuStrip = new MenuStrip
        {
            Dock = DockStyle.Top,
            GripStyle = ToolStripGripStyle.Hidden,
            Padding = new Padding(8, 2, 0, 2),
            ShowItemToolTips = true,
        };

        menuDevice = new ToolStripMenuItem();
        menuCartridge = new ToolStripMenuItem();
        menuRom = new ToolStripMenuItem();
        menuSave = new ToolStripMenuItem();

        menuRefreshPorts = new ToolStripMenuItem();
        menuRefreshPorts.Click += (_, _) => RefreshPorts(resetAnalysisState: true);
        menuScan = new ToolStripMenuItem();
        menuScan.Click += async (_, _) => await RunUiActionSafelyAsync(ScanSelectedPortAsync);
        menuExit = new ToolStripMenuItem();
        menuExit.Click += (_, _) => Close();
        menuDevice.DropDownItems.AddRange(
        [
            menuRefreshPorts,
            menuScan,
            new ToolStripSeparator(),
            menuExit,
        ]);

        menuUpdateMenu = new ToolStripMenuItem();
        menuUpdateMenu.Click += async (_, _) => await RunUiActionSafelyAsync(UpdateMenuAsync);
        menuReplaceMenuFile = new ToolStripMenuItem();
        menuReplaceMenuFile.Click += async (_, _) => await RunUiActionSafelyAsync(ReplaceMenuFromFileAsync);
        menuRepairMenu = new ToolStripMenuItem();
        menuRepairMenu.Click += async (_, _) => await RunUiActionSafelyAsync(RepairMissingMenuAsync);
        menuUpdateBackground = new ToolStripMenuItem();
        menuUpdateBackground.Click += async (_, _) => await RunUiActionSafelyAsync(
            async () => { await UpdateMenuBackgroundAsync(this); });
        menuEraseConfig = new ToolStripMenuItem();
        menuEraseConfig.Click += async (_, _) => await RunUiActionSafelyAsync(EraseHeadAreaAsync);
        menuEraseEntireNor = new ToolStripMenuItem();
        menuEraseEntireNor.Click += async (_, _) => await RunUiActionSafelyAsync(EraseEntireNorAsync);
        menuUnlockPpb = new ToolStripMenuItem();
        menuUnlockPpb.Click += async (_, _) => await RunUiActionSafelyAsync(UnlockPpbAsync);
        menuFragmentTest = new ToolStripMenuItem();
        menuFragmentTest.Click += async (_, _) => await RunUiActionSafelyAsync(RunFragmentedSaveSlotTestAsync);
        menuDetectMagic = new ToolStripMenuItem();
        menuDetectMagic.Click += async (_, _) => await RunUiActionSafelyAsync(DetectMagicSupportAsync);
        menuCartridge.DropDownItems.AddRange(
        [
            menuUpdateMenu,
            menuReplaceMenuFile,
            menuRepairMenu,
            menuUpdateBackground,
            new ToolStripSeparator(),
            menuEraseConfig,
            menuEraseEntireNor,
            menuUnlockPpb,
            menuFragmentTest,
            menuDetectMagic,
        ]);

        menuAddRom = new ToolStripMenuItem();
        menuAddRom.Click += async (_, _) => await RunUiActionSafelyAsync(SelectRomFilesForQueueAsync);
        menuPendingGbaCovers = new ToolStripMenuItem();
        menuPendingGbaCovers.Click += (_, _) => ShowPendingGbaCoverEditor();
        menuRemoveSelectedRom = new ToolStripMenuItem();
        menuRemoveSelectedRom.Click += (_, _) => RemoveSelectedQueuedRom();
        menuClearRomQueue = new ToolStripMenuItem();
        menuClearRomQueue.Click += (_, _) => ClearQueuedRoms();
        menuReplaceSelectedRom = new ToolStripMenuItem();
        menuReplaceSelectedRom.Click += async (_, _) => await RunUiActionSafelyAsync(ReplaceSelectedRomAsync);
        menuDeleteSelectedMapping = new ToolStripMenuItem();
        menuDeleteSelectedMapping.Click += (_, _) => DeleteSelectedMapping();
        menuExportSelectedRom = new ToolStripMenuItem();
        menuExportSelectedRom.Click += async (_, _) => await RunUiActionSafelyAsync(ExportSelectedEntryRomAsync);
        menuStartBurn = new ToolStripMenuItem();
        menuStartBurn.Click += async (_, _) => await RunUiActionSafelyAsync(HandlePrimaryQueueActionAsync);
        menuRom.DropDownItems.AddRange(
        [
            menuAddRom,
            menuPendingGbaCovers,
            menuRemoveSelectedRom,
            menuClearRomQueue,
            new ToolStripSeparator(),
            menuReplaceSelectedRom,
            menuDeleteSelectedMapping,
            menuExportSelectedRom,
            new ToolStripSeparator(),
            menuStartBurn,
        ]);

        menuExportSelectedSave = new ToolStripMenuItem();
        menuExportSelectedSave.Click += async (_, _) => await RunUiActionSafelyAsync(ExportSelectedEntrySaveAsync);
        menuImportSelectedSave = new ToolStripMenuItem();
        menuImportSelectedSave.Click += async (_, _) => await RunUiActionSafelyAsync(ImportSelectedEntrySaveAsync);
        menuClearSelectedSave = new ToolStripMenuItem();
        menuClearSelectedSave.Click += async (_, _) => await RunUiActionSafelyAsync(ClearSelectedEntrySaveAsync);
        menuChipSaveSeparator = new ToolStripSeparator();
        menuExportChipSave = new ToolStripMenuItem();
        menuExportChipSave.Click += async (_, _) => await RunUiActionSafelyAsync(ExportChipSaveAsync);
        menuImportChipSave = new ToolStripMenuItem();
        menuImportChipSave.Click += async (_, _) => await RunUiActionSafelyAsync(ImportChipSaveAsync);
        menuClearChipSave = new ToolStripMenuItem();
        menuClearChipSave.Click += async (_, _) => await RunUiActionSafelyAsync(ClearChipSaveAsync);
        menuSave.DropDownItems.AddRange(
        [
            menuExportSelectedSave,
            menuImportSelectedSave,
            menuClearSelectedSave,
            menuChipSaveSeparator,
            menuExportChipSave,
            menuImportChipSave,
            menuClearChipSave,
        ]);

        menuDevice.DropDownOpening += (_, _) => UpdateActionButtons(isBusy);
        menuCartridge.DropDownOpening += (_, _) => UpdateActionButtons(isBusy);
        menuRom.DropDownOpening += (_, _) => UpdateActionButtons(isBusy);
        menuSave.DropDownOpening += (_, _) => UpdateActionButtons(isBusy);
        mainMenuStrip.Items.AddRange([menuDevice, menuCartridge, menuRom, menuSave]);
        BuildFcMenus();
    }

    private void BuildInfoGroup()
    {
        infoValueToolTip = new ToolTip(components);
        groupBoxInfo = new GroupBox
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(8, 6, 8, 6),
            Margin = new Padding(3, 4, 3, 3),
        };

        var infoRoot = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
        };
        infoRoot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        infoRoot.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, MenuBackgroundPreviewColumnWidth));
        infoRoot.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        var infoTable = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 6,
            RowCount = 3,
            Margin = new Padding(0, 0, 12, 0),
        };
        for (int column = 0; column < 3; column++)
        {
            infoTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            infoTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / 3));
            infoTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100F / 3));
        }

        labelNorIdCaption = CreateInfoLabel(string.Empty);
        labelChipNameCaption = CreateInfoLabel(string.Empty);
        labelCapacityCaption = CreateInfoLabel(string.Empty);
        labelScanMethodCaption = CreateInfoLabel(string.Empty);
        labelSectorInfoCaption = CreateInfoLabel(string.Empty);
        labelRomCountCaption = CreateInfoLabel(string.Empty);
        labelMenuConfigCaption = CreateInfoLabel(string.Empty);
        labelMenuItemCountCaption = CreateInfoLabel(string.Empty);
        labelMenuTitleCaption = CreateInfoLabel(string.Empty);
        labelMenuBackgroundPreviewCaption = CreateInfoLabel(string.Empty);

        labelNorIdValue = CreateInfoValueLabel(string.Empty);
        labelChipNameValue = CreateInfoValueLabel(string.Empty);
        labelCapacityValue = CreateInfoValueLabel(string.Empty);
        labelScanMethodValue = CreateInfoValueLabel(string.Empty);
        labelSectorInfoValue = CreateInfoValueLabel(string.Empty);
        labelRomCountValue = CreateInfoValueLabel("0");
        labelMenuConfigValue = CreateInfoValueLabel(string.Empty);
        labelMenuItemCountValue = CreateInfoValueLabel("0");
        labelMenuTitleValue = CreateInfoValueLabel(string.Empty);

        pictureBoxMenuBackgroundPreview = new NearestNeighborPictureBox
        {
            Dock = DockStyle.Fill,
            BackColor = SystemColors.Control,
            SizeMode = PictureBoxSizeMode.Zoom,
            Cursor = Cursors.Default,
        };
        pictureBoxMenuBackgroundPreview.Click += (_, _) => ShowMenuBackgroundPreviewDialog();

        labelMenuBackgroundPreviewPlaceholder = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(110, 118, 132),
            BackColor = SystemColors.Control,
        };
        labelMenuBackgroundPreviewPlaceholder.Click += (_, _) =>
        {
            if (CanEditPendingGbaCovers())
            {
                ShowPendingGbaCoverEditor();
            }
            else if (IsGbcWorkspaceMode())
            {
                ShowGbcMenuPreviewDialog();
            }
        };

        panelMenuBackgroundPreviewHost = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(1),
            BackColor = SystemColors.ControlLight,
            Margin = new Padding(0),
        };
        panelMenuBackgroundPreviewHost.Controls.Add(pictureBoxMenuBackgroundPreview);
        panelMenuBackgroundPreviewHost.Controls.Add(labelMenuBackgroundPreviewPlaceholder);

        tableLayoutMenuBackgroundPreview = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 1,
            Margin = new Padding(0),
        };
        tableLayoutMenuBackgroundPreview.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        tableLayoutMenuBackgroundPreview.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        tableLayoutMenuBackgroundPreview.Controls.Add(panelMenuBackgroundPreviewHost, 0, 0);

        infoTable.Controls.Add(labelNorIdCaption, 0, 0);
        infoTable.Controls.Add(labelChipNameCaption, 2, 0);
        infoTable.Controls.Add(labelCapacityCaption, 4, 0);
        infoTable.Controls.Add(labelSectorInfoCaption, 0, 1);
        infoTable.Controls.Add(labelScanMethodCaption, 2, 1);
        infoTable.Controls.Add(labelMenuConfigCaption, 4, 1);
        infoTable.Controls.Add(labelRomCountCaption, 0, 2);
        infoTable.Controls.Add(labelMenuItemCountCaption, 2, 2);
        infoTable.Controls.Add(labelMenuTitleCaption, 4, 2);

        infoTable.Controls.Add(labelNorIdValue, 1, 0);
        infoTable.Controls.Add(labelChipNameValue, 3, 0);
        infoTable.Controls.Add(labelCapacityValue, 5, 0);
        infoTable.Controls.Add(labelSectorInfoValue, 1, 1);
        infoTable.Controls.Add(labelScanMethodValue, 3, 1);
        infoTable.Controls.Add(labelMenuConfigValue, 5, 1);
        infoTable.Controls.Add(labelRomCountValue, 1, 2);
        infoTable.Controls.Add(labelMenuItemCountValue, 3, 2);
        infoTable.Controls.Add(labelMenuTitleValue, 5, 2);

        infoRoot.Controls.Add(infoTable, 0, 0);
        infoRoot.Controls.Add(tableLayoutMenuBackgroundPreview, 1, 0);
        groupBoxInfo.Controls.Add(infoRoot);
    }

    private void BuildListGroup()
    {
        groupBoxList = new GroupBox
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 14, 12, 12),
        };

        var listLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
        };
        listLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        listLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        workspaceLayout = listLayout;

        var settingsPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Margin = new Padding(0),
            Padding = new Padding(0),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };

        buttonQueueAdd = CreateHeaderButton(string.Empty, 110);
        buttonQueueAdd.Click += async (_, _) => await RunUiActionSafelyAsync(SelectRomFilesForQueueAsync);

        buttonQueueRemove = CreateHeaderButton(string.Empty, 118);
        buttonQueueRemove.Click += (_, _) => RemoveSelectedQueuedRom();

        buttonQueueClear = CreateHeaderButton(string.Empty, 110);
        buttonQueueClear.Click += (_, _) => ClearQueuedRoms();

        labelBurnProfileModeCaption = CreateToolbarLabel();
        labelGbaMenuCaption = CreateToolbarLabel();
        labelGbcMenuLanguageCaption = CreateToolbarLabel();
        comboBoxGbcMenuLanguage = new ComboBox
        {
            Name = "gbcMenuLanguage",
            DropDownStyle = ComboBoxStyle.DropDownList,
            IntegralHeight = false,
            Width = 104,
            Margin = new Padding(6, 7, 6, 7),
        };
        comboBoxGbcMenuLanguage.SelectedIndexChanged += (_, _) => GbcMenuLanguageChanged();

        comboBoxBurnProfileMode = new GbaMultiMenuBuilder.Fc.ModeAlignedComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            IntegralHeight = false,
            Width = 220,
            Margin = new Padding(6, 7, 6, 7),
        };
        comboBoxBurnProfileMode.SelectedIndexChanged += (_, _) =>
        {
            if (!burnProfileModeSelectionInitialized)
            {
                return;
            }

            if(GetSelectedBurnProfileMode()==BurnProfileMode.FcMbcx){ShowFcWorkspace();return;}
            HideFcWorkspace();
            HandleMbc5ProfileSelection();
            int selectedIndex = dataGridViewRoms.SelectedRows.Count > 0
                ? dataGridViewRoms.SelectedRows[0].Index
                : -1;
            RefreshGbcFontSelectorVisibility();
            RebuildWorkspaceRows(selectedIndex);
        };

        comboBoxGbaMenu = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            IntegralHeight = false,
            Width = 132,
            Margin = new Padding(6, 7, 6, 7),
        };
        comboBoxGbaMenu.SelectedIndexChanged += (_, _) =>
        {
            if (!gbaMenuSelectionInitialized)
            {
                return;
            }

            ClientAppConfigService.SaveGbaMenuSelection(
                GetGbaMenuSelectionConfigValue(GetSelectedGbaMenuSelection()));
            UpdateActionButtons(isBusy);
        };

        buttonQueueBurn = CreateHeaderButton(string.Empty, 118);
        buttonQueueBurn.Click += async (_, _) => await RunUiActionSafelyAsync(HandlePrimaryQueueActionAsync);

        checkBoxWaitcntPatch = new CheckBox
        {
            AutoSize = true,
            Margin = new Padding(8, 11, 4, 0),
        };
        checkBoxWaitcntPatch.CheckedChanged += (_, _) =>
            ClientAppConfigService.SaveWaitcntPatchEnabled(checkBoxWaitcntPatch.Checked);

        settingsPanel.Controls.Add(CreateToolbarPair(labelBurnProfileModeCaption, comboBoxBurnProfileMode));
        settingsPanel.Controls.Add(buttonQueueAdd);
        settingsPanel.Controls.Add(buttonQueueRemove);
        settingsPanel.Controls.Add(buttonQueueClear);
        settingsPanel.Controls.Add(buttonQueueBurn);
        gbcMenuOptions = CreateToolbarPair(labelGbcMenuLanguageCaption, comboBoxGbcMenuLanguage);
        gbaMenuOptions = CreateToolbarPair(labelGbaMenuCaption, comboBoxGbaMenu);
        settingsPanel.Controls.Add(gbcMenuOptions);
        settingsPanel.Controls.Add(gbaMenuOptions);
        checkBoxWaitcntPatch.Anchor = AnchorStyles.None;
        checkBoxWaitcntPatch.Margin = new Padding(6, 4, 4, 4);
        settingsPanel.Controls.Add(checkBoxWaitcntPatch);
        foreach (var selector in new[] { comboBoxBurnProfileMode, comboBoxGbaMenu, comboBoxGbcMenuLanguage })
        {
            selector.DataSourceChanged += (_, _) => UpdateAdaptiveLayout();
            selector.SelectedIndexChanged += (_, _) => UpdateAdaptiveLayout();
        }

        BuildWorkspaceGrid();

        listLayout.Controls.Add(settingsPanel, 0, 0);
        listLayout.Controls.Add(dataGridViewRoms, 0, 1);
        groupBoxList.Controls.Add(listLayout);
    }

    private void BuildWorkspaceGrid()
    {
        dataGridViewRoms = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeColumns = true,
            AllowUserToResizeRows = false,
            ReadOnly = false,
            MultiSelect = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.None,
            AllowDrop = true,
            EditMode = DataGridViewEditMode.EditProgrammatically,
        };
        components.Add(dataGridViewRoms);
        dataGridViewRoms.SizeChanged += (_, _) => ResizeMbc5Rows();
        dataGridViewRoms.SelectionChanged += (_, _) => UpdateActionButtons(isBusy);
        dataGridViewRoms.CellMouseDown += DataGridViewRoms_CellMouseDown;
        dataGridViewRoms.CellContentClick += DataGridViewRoms_CellContentClick;
        dataGridViewRoms.CellDoubleClick += DataGridViewRoms_CellDoubleClick;
        dataGridViewRoms.CellBeginEdit += DataGridViewRoms_CellBeginEdit;
        dataGridViewRoms.CellEndEdit += DataGridViewRoms_CellEndEdit;
        dataGridViewRoms.EditingControlShowing += DataGridViewRoms_EditingControlShowing;
        dataGridViewRoms.MouseDown += DataGridViewRoms_MouseDown;
        dataGridViewRoms.MouseMove += DataGridViewRoms_MouseMove;
        dataGridViewRoms.DragEnter += DataGridViewRoms_DragEnter;
        dataGridViewRoms.DragOver += DataGridViewRoms_DragOver;
        dataGridViewRoms.DragDrop += DataGridViewRoms_DragDrop;

        dataGridViewRoms.Columns.Add(CreateTextColumn("Order", string.Empty, 56, 60F));
        dataGridViewRoms.Columns.Add(CreateTextColumn("State", string.Empty, 72, 80F));
        dataGridViewRoms.Columns.Add(CreateTextColumn("Source", string.Empty, 80, 90F));
        dataGridViewRoms.Columns.Add(CreateTextColumn("Kind", string.Empty, 64, 70F));
        dataGridViewRoms.Columns.Add(CreateTextColumn("Title", string.Empty, 220, 360F));
        dataGridViewRoms.Columns.Add(CreateTextColumn("HeaderTitle", string.Empty, 100, 120F));
        dataGridViewRoms.Columns.Add(CreateTextColumn("AllocSize", string.Empty, 88, 90F));
        dataGridViewRoms.Columns.Add(CreateTextColumn("SaveType", string.Empty, 95, 95F));
        dataGridViewRoms.Columns.Add(CreateCheckBoxColumn("GbcDedicatedSave", string.Empty, 104, 100F));
        dataGridViewRoms.Columns.Add(CreateTextColumn("PatchStatus", string.Empty, 130, 150F));
        dataGridViewRoms.Columns.Add(CreateTextColumn("SpiSlot", string.Empty, 80, 85F));
        dataGridViewRoms.Columns.Add(CreateTextColumn("SaveStatus", string.Empty, 230, 240F));
        foreach (DataGridViewColumn column in dataGridViewRoms.Columns)
        {
            column.ReadOnly = true;
            column.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
        }

        dataGridViewRoms.Columns["Title"]!.ReadOnly = false;
        dataGridViewRoms.Columns["GbcDedicatedSave"]!.ReadOnly = false;
        dataGridViewRoms.Columns["SaveStatus"]!.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        dataGridViewRoms.Columns["SaveStatus"]!.DefaultCellStyle.Font = CreateSaveDistributionFont();

        romContextMenu = new ContextMenuStrip(components);
        romContextMenu.Opening += RomContextMenu_Opening;
        menuItemReplaceRom = new ToolStripMenuItem();
        menuItemReplaceRom.Click += async (_, _) => await RunUiActionSafelyAsync(ReplaceSelectedRomAsync);
        menuItemDeleteMapping = new ToolStripMenuItem();
        menuItemDeleteMapping.Click += (_, _) => DeleteSelectedMapping();
        menuItemContextSeparator = new ToolStripSeparator();
        menuItemExportRom = new ToolStripMenuItem();
        menuItemExportSave = new ToolStripMenuItem();
        menuItemImportSave = new ToolStripMenuItem();
        menuItemClearSave = new ToolStripMenuItem();
        menuItemChipSaveSeparator = new ToolStripSeparator();
        menuItemExportChipSaveContext = new ToolStripMenuItem();
        menuItemImportChipSaveContext = new ToolStripMenuItem();
        menuItemClearChipSaveContext = new ToolStripMenuItem();
        menuItemExportRom.Click += async (_, _) => await RunUiActionSafelyAsync(ExportSelectedEntryRomAsync);
        menuItemExportSave.Click += async (_, _) => await RunUiActionSafelyAsync(ExportSelectedEntrySaveAsync);
        menuItemImportSave.Click += async (_, _) => await RunUiActionSafelyAsync(ImportSelectedEntrySaveAsync);
        menuItemClearSave.Click += async (_, _) => await RunUiActionSafelyAsync(ClearSelectedEntrySaveAsync);
        menuItemExportChipSaveContext.Click += async (_, _) => await RunUiActionSafelyAsync(ExportChipSaveAsync);
        menuItemImportChipSaveContext.Click += async (_, _) => await RunUiActionSafelyAsync(ImportChipSaveAsync);
        menuItemClearChipSaveContext.Click += async (_, _) => await RunUiActionSafelyAsync(ClearChipSaveAsync);
        menuItemExportRom.Enabled = false;
        menuItemExportSave.Enabled = false;
        menuItemImportSave.Enabled = false;
        menuItemClearSave.Enabled = false;
        menuItemExportChipSaveContext.Enabled = false;
        menuItemImportChipSaveContext.Enabled = false;
        menuItemClearChipSaveContext.Enabled = false;
        menuItemChipSaveSeparator.Visible = false;
        menuItemExportChipSaveContext.Visible = false;
        menuItemImportChipSaveContext.Visible = false;
        menuItemClearChipSaveContext.Visible = false;
        romContextMenu.Items.Add(menuItemReplaceRom);
        romContextMenu.Items.Add(menuItemDeleteMapping);
        romContextMenu.Items.Add(menuItemContextSeparator);
        romContextMenu.Items.Add(menuItemExportRom);
        romContextMenu.Items.Add(menuItemExportSave);
        romContextMenu.Items.Add(menuItemImportSave);
        romContextMenu.Items.Add(menuItemClearSave);
        romContextMenu.Items.Add(menuItemChipSaveSeparator);
        romContextMenu.Items.Add(menuItemExportChipSaveContext);
        romContextMenu.Items.Add(menuItemImportChipSaveContext);
        romContextMenu.Items.Add(menuItemClearChipSaveContext);
        dataGridViewRoms.ContextMenuStrip = romContextMenu;
    }

    private void BuildLogGroup()
    {
        groupBoxLog = new GroupBox
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 14, 12, 12),
        };

        var logLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        logLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        logLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
        logLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));

        textBoxLog = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = true,
            BackColor = Color.White,
        };

        progressBarTotal = new ProgressBar
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 8, 0, 0),
            Style = ProgressBarStyle.Continuous,
        };

        statusStripProgress = new StatusStrip
        {
            Dock = DockStyle.Fill,
            SizingGrip = false,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };

        labelProgress = new ToolStripStatusLabel
        {
            Name = "labelProgress",
            Text = string.Empty,
        };

        labelProgressSpring = new ToolStripStatusLabel
        {
            Name = "labelProgressSpring",
            Spring = true,
        };

        labelSpeed = new ToolStripStatusLabel
        {
            Name = "labelSpeed",
            Text = string.Empty,
        };

        statusStripProgress.Items.AddRange(
        [
            labelProgress,
            labelProgressSpring,
            labelSpeed,
        ]);

        logLayout.Controls.Add(textBoxLog, 0, 0);
        logLayout.Controls.Add(progressBarTotal, 0, 1);
        logLayout.Controls.Add(statusStripProgress, 0, 2);

        groupBoxLog.Controls.Add(logLayout);
    }

    private void ApplyLanguage(ClientUiLanguage selectedLanguage, bool refreshScanResult = true)
    {
        language = selectedLanguage;
        fcWorkspace?.RefreshLanguage();
        LocalizedText.SetCurrent(selectedLanguage);

        Text = string.Format(
            CultureInfo.CurrentCulture,
            T("ui", "window_title", AppDisplayInfo.EnglishTitle),
            AppDisplayInfo.Version);
        labelPortCaption.Text = T("ui", "port", "Port");
        labelBeggarCartModeCaption.Text = T("ui", "cart_mode", "Mode");
        radioButtonBeggarCartModeGba.Text = T("ui", "cart_mode_gba", "GBA");
        radioButtonBeggarCartModeGb.Text = T("ui", "cart_mode_gb", "GB");
        labelLanguageCaption.Text = T("ui", "language", "Language");
        buttonRefreshPorts.Text = T("ui", "refresh", "Refresh");
        buttonScan.Text = T("ui", "scan_roms", "Read Cart ROMs");
        buttonQueueAdd.Text = T("ui", "queue_add_rom", "Add ROM");
        buttonQueueRemove.Text = T("ui", "queue_remove_selected", "Remove Selected");
        buttonQueueClear.Text = T("ui", "queue_clear", "Clear Queue");
        buttonQueueBurn.Text = T("ui", "queue_burn", "Start Burn");
        checkBoxWaitcntPatch.Text = T("ui", "waitcnt_patch", "WAITCNT");
        labelGbaMenuCaption.Text = T("ui", "gba_menu", "GBA Menu");
        labelGbcMenuLanguageCaption.Text = T("ui", "gbc_menu_language", "Menu Language");
        RefreshGbcMenuLanguageOptions();
        labelBurnProfileModeCaption.Text = T("ui", "burn_profile_mode", "烧录卡");

        menuDevice.Text = T("ui", "menu_device", "Device");
        menuCartridge.Text = T("ui", "menu_cartridge", "Cartridge");
        menuRom.Text = T("ui", "menu_rom", "ROM");
        menuPendingGbaCovers.Text = T("ui", "pending_gba_covers", "Queued ROM Covers");
        menuSave.Text = T("ui", "menu_save", "Save");
        menuRefreshPorts.Text = T("ui", "refresh", "Refresh");
        menuScan.Text = T("ui", "scan_roms", "Analyze Cart");
        menuExit.Text = T("ui", "exit", "Exit");
        menuFragmentTest.Text = T("ui", "fragment_test", "Fragment Test");
        menuDetectMagic.Text = T("ui", "detect_magic", "Detect Magic Support");
        menuUpdateMenu.Text = T("ui", "update_menu", "Update Menu");
        menuReplaceMenuFile.Text = T("ui", "replace_menu_file", "Rebuild From Menu File");
        menuRepairMenu.Text = T("ui", "repair_menu", "Repair Menu");
        menuUpdateBackground.Text = T("ui", "update_menu_background", "Update Background");
        menuEraseConfig.Text = T("ui", "erase_config", "Erase Config");
        menuEraseEntireNor.Text = T("ui", "erase_entire_nor", "Erase Entire NOR");
        menuUnlockPpb.Text = T("ui", "unlock_ppb", "Unlock PPB");
        menuAddRom.Text = T("ui", "queue_add_rom", "Add ROM");
        menuRemoveSelectedRom.Text = T("ui", "queue_remove_selected", "Remove Selected");
        menuClearRomQueue.Text = T("ui", "queue_clear", "Clear Queue");
        menuReplaceSelectedRom.Text = T("ui", "replace_rom", "Replace ROM");
        menuDeleteSelectedMapping.Text = T("ui", "delete_mapping", "Delete Mapping");
        menuExportSelectedRom.Text = T("ui", "export_rom", "Export ROM");
        menuStartBurn.Text = T("ui", "queue_burn", "Start Burn");
        menuExportSelectedSave.Text = T("ui", "export_save", "Export Save");
        menuImportSelectedSave.Text = T("ui", "import_save", "Import Save");
        menuClearSelectedSave.Text = T("ui", "clear_save", "Clear Save");
        menuExportChipSave.Text = T("ui", "export_chip_save", "Export Chip Save");
        menuImportChipSave.Text = T("ui", "import_chip_save", "Import Chip Save");
        menuClearChipSave.Text = T("ui", "clear_chip_save", "Clear Chip Save");

        groupBoxInfo.Text = T("ui", "info_group", "Cart Info");
        UpdateWorkspaceGroupTitle();
        groupBoxLog.Text = T("ui", "log_group", "Log");
        menuItemReplaceRom.Text = T("ui", "replace_rom", "Replace ROM");
        menuItemDeleteMapping.Text = T("ui", "delete_mapping", "Delete Mapping");
        menuItemExportRom.Text = T("ui", "export_rom", "Export ROM");
        menuItemExportSave.Text = T("ui", "export_save", "Export Save");
        menuItemImportSave.Text = T("ui", "import_save", "Import Save");
        menuItemClearSave.Text = T("ui", "clear_save", "Clear Save");
        menuItemExportChipSaveContext.Text = T("ui", "export_chip_save", "Export Chip Save");
        menuItemImportChipSaveContext.Text = T("ui", "import_chip_save", "Import Chip Save");
        menuItemClearChipSaveContext.Text = T("ui", "clear_chip_save", "Clear Chip Save");

        labelNorIdCaption.Text = T("ui", "nor_id", "NOR ID");
        labelChipNameCaption.Text = T("ui", "chip_name", "Chip");
        labelCapacityCaption.Text = T("ui", "capacity", "Capacity");
        labelScanMethodCaption.Text = T("ui", "scan_method", "Scan Method");
        labelSectorInfoCaption.Text = T("ui", "sector_info", "Sector/BuffWr");
        labelRomCountCaption.Text = T("ui", "display_entries", "Displayed Entries");
        labelMenuConfigCaption.Text = T("ui", "menu_config", "Menu Config");
        labelMenuItemCountCaption.Text = T("ui", "menu_item_count", "Menu Items");
        labelMenuTitleCaption.Text = T("ui", "menu_title", "Menu Title");
        labelMenuBackgroundPreviewCaption.Text = T("ui", "menu_background_preview", "Background Preview");
        UpdateAdaptiveLayout();

        RefreshGridHeaders();
        RefreshBurnProfileModeOptions(GetSelectedBurnProfileMode());
        RefreshGbaMenuOptions(GetGbaMenuSelectionConfigValue(GetSelectedGbaMenuSelection()));
        RefreshGbcFontSelectorVisibility();

        languageSelectionInitialized = false;
        comboBoxLanguage.SelectedItem = availableLanguages
            .FirstOrDefault(item => string.Equals(item.Code, selectedLanguage.Code, StringComparison.OrdinalIgnoreCase));
        languageSelectionInitialized = true;

        if (currentScanResult is not null && refreshScanResult)
        {
            ApplyScanResult(currentScanResult, appendUpgradeHint: false);
        }
        else if (currentScanResult is null)
        {
            ResetInfoValues();
        }

        if (isAnalyzingQueueImports)
        {
            labelStatus.Text = GetQueueAnalysisStatusText();
        }
        else if (!isBusy)
        {
            labelStatus.Text = T("status", "ready", "Ready");
        }

        UpdateActionButtons(isBusy);
    }

    private void HandleBeggarCartModeSelectionChanged()
    {
        if (!beggarCartModeSelectionInitialized)
        {
            return;
        }

        BeggarCartMode selectedMode = GetSelectedBeggarCartMode();
        if (selectedMode == lastSelectedBeggarCartMode)
        {
            return;
        }

        lastSelectedBeggarCartMode = selectedMode;

        RefreshBurnProfileModeOptions(GetSelectedBurnProfileMode());
        InvalidateFcDevice();
        RefreshGbcFontSelectorVisibility();

        if (currentScanResult is not null)
        {
            ResetCurrentCartAnalysisView();
            AppendLog(Tf(
                "log",
                "beggar_cart_mode_changed",
                "Beggar cart mode switched to {0}. Please read the cart again.",
                GetSelectedBeggarCartModeDisplayName()));
        }

        UpdateActionButtons(isBusy);
    }

    private void ResetInfoValues()
    {
        labelNorIdValue.Text = T("ui", "not_read", "Not Read");
        labelChipNameValue.Text = T("ui", "not_read", "Not Read");
        labelCapacityValue.Text = T("ui", "not_read", "Not Read");
        labelSectorInfoValue.Text = T("ui", "not_read", "Not Read");
        labelRomCountValue.Text = "0";
        labelMenuConfigValue.Text = T("ui", "not_detected", "Not Detected");
        labelMenuItemCountValue.Text = "0";
        labelMenuTitleValue.Text = T("ui", "not_detected", "Not Detected");
        labelScanMethodValue.Text = T("ui", "scan_method_nor_only", "NOR 4MiB Header Scan");
        if (IsMbc5EightMiBMode()) RefreshMbc5Preview();
        else SetMenuBackgroundPreviewState(null, false);
    }

    private void SetMenuBackgroundPreviewState(Image? image, bool isVisible)
    {
        if (pictureBoxMenuBackgroundPreview.Image is Image existing)
        {
            pictureBoxMenuBackgroundPreview.Image = null;
            existing.Dispose();
        }

        pictureBoxMenuBackgroundPreview.Image = image;
        pictureBoxMenuBackgroundPreview.Visible = image is not null && isVisible;
        pictureBoxMenuBackgroundPreview.Cursor = image is not null && isVisible
            ? Cursors.Hand
            : Cursors.Default;
        labelMenuBackgroundPreviewPlaceholder.Text = image is null
            ? T("ui", "not_detected", "Not Detected")
            : string.Empty;
        labelMenuBackgroundPreviewPlaceholder.Visible = image is null && isVisible;
        labelMenuBackgroundPreviewPlaceholder.Cursor = IsGbcWorkspaceMode() ? Cursors.Hand : Cursors.Default;
        tableLayoutMenuBackgroundPreview.Visible = isVisible;
    }

    private void UpdateMenuBackgroundPreview(WaterCartScanResult? result)
    {
        if (IsMbc5EightMiBMode()) { RefreshMbc5Preview(); return; }
        if (result?.IsBeggarMaxGbcMode == true || IsGbcWorkspaceMode())
        {
            currentPreviewUsesGbcListMode = true;
            currentPreviewUsesGbaFirstScreenMode = false;
            labelMenuBackgroundPreviewCaption.Text = T("ui", "menu_list_preview", "Menu List Preview");
            UpdateMenuBackgroundPreviewLayout();
            Bitmap? preview = TryBuildGbcWorkspacePreviewImage();
            SetMenuBackgroundPreviewState(preview, true);
            return;
        }

        currentPreviewUsesGbcListMode = false;
        currentPreviewUsesGbaFirstScreenMode = result?.GbaMenuFirstScreenPreview.IsDetected == true;
        if (currentPreviewUsesGbaFirstScreenMode && result is not null)
        {
            labelMenuBackgroundPreviewCaption.Text = T("ui", "menu_first_screen_preview", "Menu First Screen Preview");
            UpdateMenuBackgroundPreviewLayout();
            SetMenuBackgroundPreviewState(RenderGbaMenuFirstScreenPreview(result), true);
            return;
        }

        labelMenuBackgroundPreviewCaption.Text = T("ui", "menu_background_preview", "Background Preview");
        UpdateMenuBackgroundPreviewLayout();
        if (result is null ||
            result.MenuBackgroundBitmapData.Length != BackgroundImageEncoder.Width * BackgroundImageEncoder.Height ||
            result.MenuBackgroundPaletteData.Length != 0x200)
        {
            SetMenuBackgroundPreviewState(null, true);
            return;
        }

        try
        {
            SetMenuBackgroundPreviewState(DecodeMenuBackgroundPreview(
                result.MenuBackgroundBitmapData,
                result.MenuBackgroundPaletteData), true);
        }
        catch
        {
            SetMenuBackgroundPreviewState(null, true);
        }
    }

    private Bitmap? TryBuildGbcWorkspacePreviewImage()
    {
        try
        {
            MenuPreviewModel preview = BuildGbcWorkspacePreviewModel();
            return RenderGbcPreviewImage(preview, 2);
        }
        catch
        {
            return null;
        }
    }

    private MenuPreviewModel BuildGbcWorkspacePreviewModel()
    {
        if (!IsSelectedGbcMenuChinese())
        {
            return BuildEnglishGbcWorkspacePreviewModel();
        }

        string fontPath = ResolveSelectedGbcFontPath()
            ?? GbcFontService.FindFontPath(null)
            ?? throw new InvalidOperationException(T("error", "gbc_layer_font_required", "A 12px Chinese font is required. 8px fonts are not supported."));
        GbcFontGlyphCacheResult glyphResult = GbcFontService.BuildGlyphCacheDetailed(
            CollectGbcPreviewCodepoints(),
            fontPath);
        Dictionary<ushort, byte[]> glyphMap = glyphResult.Glyphs
            .Where(pair => pair.Key is >= 0 and <= ushort.MaxValue)
            .ToDictionary(pair => (ushort)pair.Key, pair => pair.Value);

        List<MenuPreviewEntry> entries = [];
        int previewIndex = 0;
        foreach (WorkspaceListRow row in workspaceRows.Where(row => !row.IsLoadingPlaceholder))
        {
            if (!TryCreateGbcPreviewEntry(row, previewIndex, glyphMap, glyphResult.Format.GlyphSizePx, out MenuPreviewEntry? entry))
            {
                continue;
            }

            if (entry is null)
            {
                continue;
            }

            entries.Add(entry);
            previewIndex++;
        }

        return new MenuPreviewModel(
            "GBC Menu Preview",
            string.Empty,
            CompilationFamily.Gbc,
            "zh_CN",
            glyphResult.Format.GlyphSizePx,
            8,
            0,
            entries);
    }

    private MenuPreviewModel BuildEnglishGbcWorkspacePreviewModel()
    {
        List<MenuPreviewEntry> entries = [];
        int previewIndex = 0;
        foreach (WorkspaceListRow row in workspaceRows.Where(row => !row.IsLoadingPlaceholder))
        {
            if (!TryCreateGbcPreviewEntry(row, previewIndex, new Dictionary<ushort, byte[]>(), 8, out MenuPreviewEntry? entry) || entry is null)
            {
                continue;
            }

            entries.Add(entry);
            previewIndex++;
        }

        return new MenuPreviewModel(
            "GBC Menu Preview",
            string.Empty,
            CompilationFamily.Gbc,
            "en_US",
            8,
            16,
            0,
            entries);
    }

    private static Bitmap RenderGbcPreviewImage(MenuPreviewModel preview, int scale)
    {
        Bitmap bitmap = new(GbcPreviewScreenWidth * scale, GbcPreviewScreenHeight * scale);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;

        Rectangle screenRect = new(0, 0, bitmap.Width, bitmap.Height);
        using SolidBrush backgroundBrush = new(Color.FromArgb(247, 248, 243));
        using SolidBrush textBrush = new(Color.FromArgb(25, 31, 37));
        using SolidBrush mutedBrush = new(Color.FromArgb(108, 118, 128));
        using SolidBrush selectedBrush = new(Color.FromArgb(214, 228, 243));
        using Font asciiFont = new(FontFamily.GenericMonospace, Math.Max(8F, scale * 1.15F), FontStyle.Regular, GraphicsUnit.Pixel);
        using Font asciiFontBold = new(FontFamily.GenericMonospace, Math.Max(8F, scale * 1.15F), FontStyle.Bold, GraphicsUnit.Pixel);

        graphics.FillRectangle(backgroundBrush, screenRect);
        graphics.DrawRectangle(Pens.Black, 0, 0, bitmap.Width - 1, bitmap.Height - 1);

        if (string.Equals(preview.LanguageCode, "en_US", StringComparison.OrdinalIgnoreCase))
        {
            DrawEnglishGbcPreview(graphics, preview, scale, selectedBrush);
            return bitmap;
        }

        int pageCount = preview.Entries.Count == 0 ? 1 : preview.Entries.Max(entry => entry.PageIndex) + 1;
        IReadOnlyDictionary<ushort, byte[]>? previewGlyphs = preview.Entries.FirstOrDefault()?.GlyphBitmaps;
        string versionText = GbcMenuLayerBuilder.PreviewVersionText;
        string pageText = $"01/{pageCount:D2}";
        int versionX = GbcPreviewScreenWidth - 8 - MeasureGbcCompactTextWidth(previewGlyphs, preview.FontSizePx, versionText);
        int pageTextX = GbcPreviewScreenWidth - 8 - MeasureGbcCompactTextWidth(previewGlyphs, preview.FontSizePx, pageText);

        int footerY = 132;
        DrawGbcCompactText(graphics, previewGlyphs, preview.FontSizePx, GbcMenuLayerBuilder.PreviewHeaderText, 8 * scale, 2 * scale, scale, asciiFontBold, textBrush);
        DrawGbcCompactText(graphics, previewGlyphs, preview.FontSizePx, versionText, versionX * scale, 2 * scale, scale, asciiFont, mutedBrush);
        DrawGbcCompactText(graphics, previewGlyphs, preview.FontSizePx, GbcMenuLayerBuilder.PreviewFooterText, 8 * scale, footerY * scale, scale, asciiFont, mutedBrush);
        DrawGbcCompactText(graphics, previewGlyphs, preview.FontSizePx, pageText, pageTextX * scale, footerY * scale, scale, asciiFont, mutedBrush);

        List<MenuPreviewEntry> entries = preview.Entries
            .Where(entry => entry.PageIndex == 0)
            .OrderBy(entry => entry.RowIndex)
            .ToList();
        if (entries.Count == 0)
        {
            DrawGbcCompactText(graphics, previewGlyphs, preview.FontSizePx, "No Game Found!", 52 * scale, 24 * scale, scale, asciiFont, textBrush);
            return bitmap;
        }

        int rowTopStartPx = 24;
        int rowStepPx = 12;
        DrawGbcArrow(graphics, screenRect, 0, rowTopStartPx, rowStepPx, scale, textBrush);

        foreach (MenuPreviewEntry entry in entries)
        {
            int rowTopPx = rowTopStartPx + (entry.RowIndex * rowStepPx);
            if (entry.RowIndex == 0)
            {
                graphics.FillRectangle(selectedBrush, 0, rowTopPx * scale, bitmap.Width, Math.Max(1, rowStepPx * scale));
            }

            string limitedMainTitle = GbcMenuLayerBuilder.LimitPreviewMainMenuTitle(entry.DisplayTitle);
            DrawGbcMenuIndexText(graphics, entry.GlyphBitmaps, entry.GlyphSizePx, entry.Index + 1, 28 * scale, rowTopPx * scale, scale, asciiFont, textBrush);
            DrawGbcCompactText(graphics, entry.GlyphBitmaps, entry.GlyphSizePx, limitedMainTitle, 52 * scale, rowTopPx * scale, scale, asciiFont, textBrush);
        }

        return bitmap;
    }

    private IEnumerable<int> CollectGbcPreviewCodepoints()
    {
        HashSet<int> codepoints = [];
        foreach (string text in GbcMenuLayerBuilder.GetPreviewUiTexts())
        {
            foreach (char ch in text)
            {
                codepoints.Add(ch);
            }
        }

        foreach (char ch in "0123456789/.")
        {
            codepoints.Add(ch);
        }

        foreach (WorkspaceListRow row in workspaceRows.Where(row => !row.IsLoadingPlaceholder))
        {
            string limitedTitle = GbcMenuLayerBuilder.LimitPreviewMainMenuTitle(GetWorkspaceRowDisplayTitle(row));
            foreach (char ch in limitedTitle)
            {
                codepoints.Add(ch);
            }
        }

        return codepoints;
    }

    private bool TryCreateGbcPreviewEntry(
        WorkspaceListRow row,
        int previewIndex,
        IReadOnlyDictionary<ushort, byte[]> glyphMap,
        int glyphSizePx,
        out MenuPreviewEntry? entry)
    {
        string displayTitle = GetWorkspaceRowDisplayTitle(row);
        if (string.IsNullOrWhiteSpace(displayTitle))
        {
            entry = null;
            return false;
        }

        string headerTitle = string.Empty;
        string detailTypeText = string.Empty;
        string detailRomText = string.Empty;
        string detailRamText = string.Empty;
        string detailSaveText = string.Empty;
        bool usesDedicatedSave = false;
        bool hasSaveSupport = false;

        if (row.PendingItem is not null)
        {
            GameBoyRomMetadata metadata = GameBoyRomHeaderService.Read(row.PendingItem.FilePath);
            int ramBanks = metadata.EffectiveRamBankCount;
            bool dedicated = ResolveWorkspaceRowUsesDedicatedSave(row);
            headerTitle = string.IsNullOrWhiteSpace(row.PendingItem.HeaderTitle) ? displayTitle : row.PendingItem.HeaderTitle;
            detailTypeText = GbcMenuLayerBuilder.FormatPreviewDetailTypeText(metadata.CartridgeType);
            detailRomText = GbcMenuLayerBuilder.FormatPreviewDetailRomText(Math.Max(2, metadata.EffectiveRomBankCount));
            detailRamText = GbcMenuLayerBuilder.FormatPreviewDetailRamText(ramBanks);
            detailSaveText = GbcMenuLayerBuilder.FormatPreviewDetailSaveText(metadata.HasBattery, ramBanks);
            usesDedicatedSave = dedicated;
            hasSaveSupport = ramBanks > 0;
        }
        else if (row.CartEntry is not null && !row.CartEntry.IsMenu)
        {
            WaterCartEntry cartEntry = row.CartEntry;
            headerTitle = string.IsNullOrWhiteSpace(cartEntry.HeaderTitle) ? displayTitle : cartEntry.HeaderTitle;
            detailTypeText = GbcMenuLayerBuilder.FormatPreviewDetailTypeText(cartEntry.MbcType);
            detailRomText = GbcMenuLayerBuilder.FormatPreviewDetailRomText(Math.Max(2, cartEntry.RomBankCount));
            detailRamText = GbcMenuLayerBuilder.FormatPreviewDetailRamText(cartEntry.RamBankCount);
            detailSaveText = GbcMenuLayerBuilder.FormatPreviewDetailSaveText(cartEntry.HasBattery, cartEntry.RamBankCount);
            usesDedicatedSave = ResolveWorkspaceRowUsesDedicatedSave(row);
            hasSaveSupport = cartEntry.RamBankCount > 0;
        }
        else
        {
            entry = null;
            return false;
        }

        entry = new MenuPreviewEntry(
            previewIndex,
            displayTitle,
            headerTitle,
            true,
            true,
            previewIndex == 0,
            previewIndex / (IsSelectedGbcMenuChinese() ? 8 : 16),
            previewIndex % (IsSelectedGbcMenuChinese() ? 8 : 16),
            string.Empty,
            usesDedicatedSave,
            hasSaveSupport,
            null,
            glyphMap,
            glyphSizePx,
            detailTypeText,
            detailRomText,
            detailRamText,
            detailSaveText);
        return true;
    }

    private static void DrawGbcArrow(
        Graphics graphics,
        Rectangle screenRect,
        int rowIndex,
        int rowTopStartPx,
        int rowStepPx,
        int scale,
        Brush brush)
    {
        int rowTopPx = rowTopStartPx + (rowIndex * rowStepPx);
        int arrowY = rowTopPx + (rowStepPx / 2);
        Point[] points =
        [
            new Point(screenRect.X + (26 * scale), screenRect.Y + (arrowY * scale)),
            new Point(screenRect.X + (20 * scale), screenRect.Y + ((arrowY - 4) * scale)),
            new Point(screenRect.X + (20 * scale), screenRect.Y + ((arrowY + 4) * scale)),
        ];
        graphics.FillPolygon(brush, points);
    }

    private static void DrawGbcMenuIndexText(
        Graphics graphics,
        IReadOnlyDictionary<ushort, byte[]>? glyphBitmaps,
        int preferredGlyphSizePx,
        int value,
        int x,
        int y,
        int scale,
        Font fallbackFont,
        Brush brush)
    {
        string digits = value.ToString("D2", CultureInfo.InvariantCulture);
        if (glyphBitmaps is null || glyphBitmaps.Count == 0)
        {
            graphics.DrawString($"{digits}.", fallbackFont, brush, x, y);
            return;
        }

        const int targetSizePx = 12;
        int digitStepPx = MeasurePreviewDigitStepPx(glyphBitmaps, preferredGlyphSizePx, targetSizePx);
        for (int i = 0; i < digits.Length; i++)
        {
            if (!glyphBitmaps.TryGetValue(digits[i], out byte[]? digitBitmap))
            {
                graphics.DrawString(digits[i].ToString(), fallbackFont, brush, x + (i * digitStepPx * scale), y);
                continue;
            }

            DrawGlyphBitmapExact(graphics, digitBitmap, x + (i * digitStepPx * scale), y, targetSizePx, scale, preferredGlyphSizePx);
        }

        if (glyphBitmaps.TryGetValue('.', out byte[]? dotBitmap))
        {
            DrawGlyphBitmapExact(graphics, dotBitmap, x + (digits.Length * digitStepPx * scale), y, targetSizePx, scale, preferredGlyphSizePx);
        }
        else
        {
            graphics.DrawString(".", fallbackFont, brush, x + (digits.Length * digitStepPx * scale), y);
        }
    }

    private static int MeasurePreviewDigitStepPx(
        IReadOnlyDictionary<ushort, byte[]> glyphBitmaps,
        int preferredGlyphSizePx,
        int targetSizePx)
    {
        int maxAdvancePx = 0;
        for (char ch = '0'; ch <= '9'; ch++)
        {
            if (!glyphBitmaps.TryGetValue(ch, out byte[]? glyphBitmap))
            {
                continue;
            }

            maxAdvancePx = Math.Max(maxAdvancePx, MeasurePreviewGlyphAdvancePx(glyphBitmap, preferredGlyphSizePx, targetSizePx));
        }

        return Math.Max(1, maxAdvancePx) + 2;
    }

    private static int MeasureGbcCompactTextWidth(
        IReadOnlyDictionary<ushort, byte[]>? glyphBitmaps,
        int preferredGlyphSizePx,
        string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        const int targetSizePx = 12;
        const int asciiGapPx = 2;
        const int cjkGapPx = 1;
        int widthPx = 0;
        for (int i = 0; i < text.Length; i++)
        {
            ushort code = text[i];
            if (glyphBitmaps is not null && glyphBitmaps.TryGetValue(code, out byte[]? glyphBitmap))
            {
                widthPx += code < 0x80
                    ? MeasurePreviewGlyphAdvancePx(glyphBitmap, preferredGlyphSizePx, targetSizePx)
                    : targetSizePx;
            }
            else
            {
                widthPx += code < 0x80 ? Math.Max(6, targetSizePx / 2) : targetSizePx;
            }

            if (i + 1 < text.Length)
            {
                widthPx += code < 0x80 ? asciiGapPx : cjkGapPx;
            }
        }

        return widthPx;
    }

    private static int MeasurePreviewGlyphAdvancePx(IReadOnlyList<byte> glyphBitmap, int preferredGlyphSizePx, int targetSizePx)
    {
        int glyphSizePx = ResolveGlyphBitmapSizePx(glyphBitmap, preferredGlyphSizePx);
        if (glyphSizePx <= 0)
        {
            return Math.Max(1, targetSizePx / 2);
        }

        int right = -1;
        for (int row = 0; row < glyphSizePx; row++)
        {
            int rowBits = ReadGlyphBitmapRow(glyphBitmap, glyphSizePx, row);
            for (int column = 0; column < glyphSizePx; column++)
            {
                if ((rowBits & (1 << (glyphSizePx - 1 - column))) != 0 && column > right)
                {
                    right = column;
                }
            }
        }

        if (right < 0)
        {
            return Math.Max(1, targetSizePx / 2);
        }

        return ((right + 1) * targetSizePx + glyphSizePx - 1) / glyphSizePx;
    }

    private static void DrawGbcCompactText(
        Graphics graphics,
        IReadOnlyDictionary<ushort, byte[]>? glyphBitmaps,
        int preferredGlyphSizePx,
        string text,
        int x,
        int y,
        int scale,
        Font fallbackFont,
        Brush brush)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (glyphBitmaps is null || glyphBitmaps.Count == 0)
        {
            graphics.DrawString(text, fallbackFont, brush, x, y);
            return;
        }

        const int targetSizePx = 12;
        const int asciiGapPx = 2;
        const int cjkGapPx = 1;
        int cursorX = x;
        for (int i = 0; i < text.Length; i++)
        {
            ushort code = text[i];
            if (!glyphBitmaps.TryGetValue(code, out byte[]? glyphBitmap))
            {
                graphics.DrawString(text[i].ToString(), fallbackFont, brush, cursorX, y);
                cursorX += Math.Max(6, scale * 6);
                continue;
            }

            DrawGlyphBitmapExact(graphics, glyphBitmap, cursorX, y, targetSizePx, scale, preferredGlyphSizePx);
            int advancePx = code < 0x80
                ? MeasurePreviewGlyphAdvancePx(glyphBitmap, preferredGlyphSizePx, targetSizePx)
                : targetSizePx;
            cursorX += advancePx * scale;
            if (i + 1 < text.Length)
            {
                cursorX += (code < 0x80 ? asciiGapPx : cjkGapPx) * scale;
            }
        }
    }

    private static int ResolveGlyphBitmapSizePx(IReadOnlyList<byte> glyphBitmap, int preferredSizePx)
    {
        if (preferredSizePx > 0)
        {
            int rowBytes = (preferredSizePx + 7) / 8;
            if (glyphBitmap.Count == preferredSizePx * rowBytes)
            {
                return preferredSizePx;
            }
        }

        if (glyphBitmap.Count <= 0)
        {
            return 0;
        }

        int pixelCount = glyphBitmap.Count * 8;
        int dimension = (int)Math.Sqrt(pixelCount);
        return dimension * dimension == pixelCount ? dimension : 0;
    }

    private static int ReadGlyphBitmapRow(IReadOnlyList<byte> glyphBitmap, int glyphSizePx, int row)
    {
        int rowBytes = Math.Max(1, glyphBitmap.Count / Math.Max(1, glyphSizePx));
        int rowOffset = row * rowBytes;
        int rowBits = 0;
        for (int index = 0; index < rowBytes && rowOffset + index < glyphBitmap.Count; index++)
        {
            rowBits = (rowBits << 8) | glyphBitmap[rowOffset + index];
        }

        int unusedBits = (rowBytes * 8) - glyphSizePx;
        if (unusedBits > 0)
        {
            rowBits >>= unusedBits;
        }

        return rowBits;
    }

    private static void DrawGlyphBitmapExact(Graphics graphics, IReadOnlyList<byte> glyphBitmap, int x, int y, int sizePx, int scale, int preferredGlyphSizePx)
    {
        int glyphSizePx = ResolveGlyphBitmapSizePx(glyphBitmap, preferredGlyphSizePx);
        if (glyphSizePx <= 0)
        {
            return;
        }

        using SolidBrush pixelBrush = new(Color.FromArgb(25, 31, 37));
        for (int sy = 0; sy < glyphSizePx; sy++)
        {
            int rowBits = ReadGlyphBitmapRow(glyphBitmap, glyphSizePx, sy);
            int y0 = (sy * sizePx) / glyphSizePx;
            int y1 = Math.Max(y0 + 1, (((sy + 1) * sizePx) + (glyphSizePx - 1)) / glyphSizePx);
            for (int sx = 0; sx < glyphSizePx; sx++)
            {
                if ((rowBits & (1 << (glyphSizePx - 1 - sx))) == 0)
                {
                    continue;
                }

                int x0 = (sx * sizePx) / glyphSizePx;
                int x1 = Math.Max(x0 + 1, (((sx + 1) * sizePx) + (glyphSizePx - 1)) / glyphSizePx);
                graphics.FillRectangle(pixelBrush, x + (x0 * scale), y + (y0 * scale), Math.Max(1, (x1 - x0) * scale), Math.Max(1, (y1 - y0) * scale));
            }
        }
    }

    private static Bitmap DecodeMenuBackgroundPreview(byte[] bitmapData, byte[] paletteData)
    {
        Bitmap bitmap = new(BackgroundImageEncoder.Width, BackgroundImageEncoder.Height);
        for (int y = 0; y < BackgroundImageEncoder.Height; y++)
        {
            int rowOffset = y * BackgroundImageEncoder.Width;
            for (int x = 0; x < BackgroundImageEncoder.Width; x++)
            {
                int paletteIndex = bitmapData[rowOffset + x];
                int paletteOffset = paletteIndex * 2;
                ushort rgb555 = (ushort)(paletteData[paletteOffset] | (paletteData[paletteOffset + 1] << 8));
                int red = ExpandRgb555Component(rgb555 & 0x1F);
                int green = ExpandRgb555Component((rgb555 >> 5) & 0x1F);
                int blue = ExpandRgb555Component((rgb555 >> 10) & 0x1F);
                bitmap.SetPixel(x, y, Color.FromArgb(red, green, blue));
            }
        }

        return bitmap;
    }

    private static Bitmap RenderGbaMenuFirstScreenPreview(WaterCartScanResult result) =>
        RenderGbaMenuFirstScreenPreview(result, 0);

    private static Bitmap RenderGbaMenuFirstScreenPreview(
        WaterCartScanResult result,
        int selectedIndex,
        GbaMenuFirstScreenPreviewInfo? previewOverride = null,
        IReadOnlyList<MenuItemConfigInfo>? itemsOverride = null)
    {
        const int screenWidth = 240;
        const int screenHeight = 160;
        const int cardWidth = 56;
        const int cardHeight = 56;
        const int cardPitch = 57;
        const int cardX = 7;
        const int cardY = 44;

        Bitmap bitmap = new(screenWidth, screenHeight);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        graphics.Clear(Color.FromArgb(247, 248, 250));

        Color divider = Color.FromArgb(216, 219, 224);
        Color shadow = Color.FromArgb(217, 220, 226);
        Color surface = Color.White;
        Color selection = Color.FromArgb(24, 154, 211);
        Color text = Color.FromArgb(24, 26, 30);
        using Pen dividerPen = new(divider);
        using Pen selectionPen = new(selection, 2);
        using Pen backgroundPen = new(Color.FromArgb(247, 248, 250));
        using SolidBrush shadowBrush = new(shadow);
        using SolidBrush surfaceBrush = new(surface);
        using SolidBrush selectionBrush = new(selection);
        using SolidBrush textBrush = new(text);

        bool simplifiedMenu = result.IsSimplifiedNsGbaMenuDetected;
        if (simplifiedMenu)
        {
            graphics.FillRectangle(surfaceBrush, 0, 124, screenWidth, 36);
            graphics.DrawLine(dividerPen, 0, 123, screenWidth - 1, 123);
            DrawGbaPreviewText(graphics, "CHIS", new Rectangle(11, 5, 52, 12), text, 8F, ContentAlignment.TopLeft);
        }
        else
        {
            graphics.DrawLine(dividerPen, 7, 143, 232, 143);
            DrawGbaPreviewHeader(graphics, shadowBrush, surfaceBrush, textBrush);
        }

        List<MenuItemConfigInfo> visibleItems = itemsOverride?.ToList() ?? GetGbaMenuPreviewItems(result);
        int total = visibleItems.Count;
        selectedIndex = total == 0 ? 0 : Math.Clamp(selectedIndex, 0, total - 1);
        DrawGbaPreviewText(
            graphics,
            total > 0 ? $"{selectedIndex + 1:D3}/{total:D3}" : "000/000",
            new Rectangle(174, 5, 58, 10),
            text,
            7F,
            ContentAlignment.TopRight);

        if (visibleItems.Count > 0)
        {
            string title = visibleItems[selectedIndex].Title;
            int firstVisibleIndex = total <= 4
                ? 0
                : Math.Clamp(selectedIndex - 2, 0, total - 4);
            int selectedSlot = selectedIndex - firstVisibleIndex;
            DrawGbaPreviewText(
                graphics,
                title,
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
        GbaMenuFirstScreenPreviewInfo coverPreview = previewOverride ?? result.GbaMenuFirstScreenPreview;
        Color[] coverPalette = BuildGbaPreviewCoverPalette(coverPreview);
        int firstVisible = visibleItems.Count <= 4
            ? 0
            : Math.Clamp(selectedIndex - 2, 0, visibleItems.Count - 4);
        int lastVisible = Math.Min(visibleItems.Count - 1, firstVisible + 3);
        for (int index = lastVisible; index >= firstVisible; index--)
        {
            if (index == selectedIndex)
            {
                continue;
            }

            int slot = index - firstVisible;
            int x = cardX + slot * cardPitch;
            DrawGbaPreviewCard(graphics, coverPreview, visibleItems[index], coverPalette,
                x, cardY, cardWidth, cardHeight, fallbackCardColors[index % fallbackCardColors.Length], false,
                selectionPen, backgroundPen, shadowBrush);
        }

        if (visibleItems.Count > 0)
        {
            int selectedSlot = selectedIndex - firstVisible;
            DrawGbaPreviewCard(graphics, coverPreview, visibleItems[selectedIndex], coverPalette,
                cardX + selectedSlot * cardPitch, cardY - 3, cardWidth, cardHeight,
                fallbackCardColors[selectedIndex % fallbackCardColors.Length], true,
                selectionPen, backgroundPen, shadowBrush);
        }

        if (simplifiedMenu)
        {
            if (visibleItems.Count > 0 && visibleItems[selectedIndex].SaveType != 0)
            {
                DrawGbaPreviewText(
                    graphics,
                    $"SAVE {visibleItems[selectedIndex].SaveIndex + 1:D2}",
                    new Rectangle(11, 138, 70, 13),
                    text,
                    7F,
                    ContentAlignment.TopLeft);
            }
            using SolidBrush readyBrush = new(Color.FromArgb(66, 169, 107));
            graphics.FillRectangle(readyBrush, 225, 141, 4, 4);
            DrawGbaPreviewText(graphics, "SYSTEM READY", new Rectangle(144, 138, 73, 13), text, 7F, ContentAlignment.TopRight);
        }
        else
        {
            DrawGbaPreviewToolbar(graphics, divider, shadow, surface, selection, text);
            DrawGbaPreviewFooter(graphics, text, surface);
        }
        return bitmap;
    }

    private static List<MenuItemConfigInfo> GetGbaMenuPreviewItems(WaterCartScanResult result) =>
        result.MenuConfig.Items.Count == 0
            ? []
            : result.MenuConfig.Items
                .TakeWhile(item => item.HiddenKeysMask == result.MenuConfig.Items[0].HiddenKeysMask)
                .ToList();

    private static Bitmap? DecodeGbaMenuCover(
        GbaMenuFirstScreenPreviewInfo preview,
        MenuItemConfigInfo item,
        Color[] palette)
    {
        const int coverWidth = 56;
        const int coverHeight = 56;
        if ((item.CoverFlags & 1) == 0 ||
            !preview.CoverPixelsByIndex.TryGetValue(item.CoverIndex, out byte[]? pixels) ||
            pixels.Length != coverWidth * coverHeight)
        {
            return null;
        }

        Bitmap cover = new(coverWidth, coverHeight);
        for (int y = 0; y < coverHeight; y++)
        {
            for (int x = 0; x < coverWidth; x++)
            {
                cover.SetPixel(x, y, palette[pixels[y * coverWidth + x]]);
            }
        }

        return cover;
    }

    private static void DrawGbaPreviewCard(
        Graphics graphics,
        GbaMenuFirstScreenPreviewInfo preview,
        MenuItemConfigInfo item,
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
        int cardX = selected ? x - 1 : x;
        int cardY = selected ? y - 1 : y;
        int cardWidth = selected ? width + 2 : width;
        int cardHeight = selected ? height + 2 : height;
        graphics.FillRectangle(shadowBrush, cardX + 2, cardY + 3, cardWidth, cardHeight);
        if (selected)
        {
            graphics.DrawRectangle(selectionPen, cardX - 3, cardY - 3, cardWidth + 5, cardHeight + 5);
            graphics.DrawRectangle(backgroundPen, cardX - 1, cardY - 1, cardWidth + 1, cardHeight + 1);
        }

        using SolidBrush fallbackBrush = new(fallbackColor);
        graphics.FillRectangle(fallbackBrush, cardX, cardY, cardWidth, cardHeight);
        if ((item.CoverFlags & 1) == 0 ||
            !preview.CoverPixelsByIndex.TryGetValue(item.CoverIndex, out byte[]? pixels) ||
            pixels.Length != width * height)
        {
            DrawGbaPreviewText(graphics, item.Title, new Rectangle(x + 2, y + 10, width - 4, height - 12),
                Color.White, 7F, ContentAlignment.MiddleCenter, true);
            return;
        }

        using Bitmap cover = new(width, height);
        for (int py = 0; py < height; py++)
        {
            for (int px = 0; px < width; px++)
            {
                cover.SetPixel(px, py, palette[pixels[py * width + px]]);
            }
        }

        graphics.DrawImageUnscaled(cover, x, y);
    }

    private static Color[] BuildGbaPreviewCoverPalette(GbaMenuFirstScreenPreviewInfo preview)
    {
        Color[] palette = Enumerable.Repeat(Color.White, 256).ToArray();
        int colorCount = Math.Min(preview.PaletteData.Length / 2, 256 - preview.PaletteBase);
        for (int index = 0; index < colorCount; index++)
        {
            int offset = index * 2;
            ushort rgb555 = (ushort)(preview.PaletteData[offset] | (preview.PaletteData[offset + 1] << 8));
            palette[preview.PaletteBase + index] = Color.FromArgb(
                ExpandRgb555Component(rgb555 & 0x1F),
                ExpandRgb555Component((rgb555 >> 5) & 0x1F),
                ExpandRgb555Component((rgb555 >> 10) & 0x1F));
        }

        return palette;
    }

    private static void DrawGbaPreviewHeader(Graphics graphics, Brush shadow, Brush surface, Brush text)
    {
        graphics.FillEllipse(shadow, 5, 3, 20, 20);
        graphics.FillEllipse(surface, 6, 4, 18, 18);
        using SolidBrush edge = new(Color.FromArgb(217, 145, 25));
        using SolidBrush cheese = new(Color.FromArgb(255, 217, 74));
        using SolidBrush hole = new(Color.FromArgb(201, 120, 22));
        Point[] wedge = [new(8, 17), new(9, 10), new(19, 6), new(23, 10), new(22, 17), new(15, 19)];
        graphics.FillPolygon(edge, wedge);
        Point[] inner = [new(9, 16), new(10, 11), new(19, 7), new(22, 10), new(21, 16), new(15, 18)];
        graphics.FillPolygon(cheese, inner);
        graphics.FillRectangle(hole, 14, 9, 2, 1);
        graphics.FillRectangle(hole, 11, 12, 2, 2);
        graphics.FillRectangle(hole, 18, 14, 2, 2);
    }

    private static void DrawGbaPreviewToolbar(Graphics graphics, Color divider, Color shadow, Color surface, Color selection, Color text)
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
        for (int index = 0; index < 6; index++)
        {
            int centerX = 69 + index * 21;
            using SolidBrush shadowBrush = new(shadow);
            using SolidBrush dividerBrush = new(divider);
            using SolidBrush surfaceBrush = new(surface);
            graphics.FillEllipse(shadowBrush, centerX - 8, 113, 18, 18);
            graphics.FillEllipse(dividerBrush, centerX - 9, 112, 18, 18);
            graphics.FillEllipse(surfaceBrush, centerX - 8, 113, 16, 16);
            using Pen iconPen = new(colors[index]);
            using SolidBrush iconBrush = new(colors[index]);
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

    private static void DrawGbaPreviewFooter(Graphics graphics, Color text, Color surface)
    {
        using Pen pen = new(text);
        graphics.DrawRectangle(pen, 10, 149, 14, 8);
        graphics.DrawRectangle(pen, 7, 149, 3, 8);
        graphics.DrawRectangle(pen, 24, 149, 3, 8);
        using SolidBrush button = new(text);
        graphics.FillEllipse(button, 181, 149, 8, 8);
        DrawGbaPreviewText(graphics, "A", new Rectangle(181, 148, 8, 9), surface, 6F, ContentAlignment.MiddleCenter);
        DrawGbaPreviewText(graphics, "START", new Rectangle(190, 147, 42, 12), text, 7F, ContentAlignment.MiddleLeft);
    }

    private static void DrawGbaPreviewText(
        Graphics graphics,
        string value,
        Rectangle bounds,
        Color color,
        float size,
        ContentAlignment alignment,
        bool wordBreak = false)
    {
        using Font font = new("Microsoft YaHei UI", size, FontStyle.Regular, GraphicsUnit.Pixel);
        using StringFormat format = new(StringFormat.GenericTypographic)
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

        System.Drawing.Drawing2D.GraphicsState state = graphics.Save();
        try
        {
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
            using SolidBrush brush = new(color);
            graphics.DrawString(value, font, brush, bounds, format);
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    private static int ExpandRgb555Component(int value) => (value << 3) | (value >> 2);

    private void UpdateMenuBackgroundPreviewLayout()
    {
        if (rootLayout is null ||
            tableLayoutMenuBackgroundPreview is null ||
            panelMenuBackgroundPreviewHost is null)
        {
            return;
        }

        int previewInnerWidth = Math.Max(1, MenuBackgroundPreviewColumnWidth - panelMenuBackgroundPreviewHost.Padding.Horizontal);
        int previewHeight = (int)Math.Ceiling(
                previewInnerWidth * DeviceDpi / 96d * BackgroundImageEncoder.Height / BackgroundImageEncoder.Width)
            + panelMenuBackgroundPreviewHost.Padding.Vertical;
        float desiredInfoGroupHeight = Math.Max(previewHeight, 3 * (Font.Height + 8))
            + Font.Height
            + groupBoxInfo.Padding.Vertical
            + groupBoxInfo.Margin.Vertical;

        if (rootLayout.RowStyles.Count > 1 && Math.Abs(rootLayout.RowStyles[1].Height - desiredInfoGroupHeight) > 0.5F)
        {
            rootLayout.RowStyles[1].Height = desiredInfoGroupHeight;
        }
    }

    private void ShowMenuBackgroundPreviewDialog()
    {
        if (IsMbc5EightMiBMode()) return;
        if (CanEditPendingGbaCovers())
        {
            ShowPendingGbaCoverEditor();
            return;
        }
        if (currentPreviewUsesGbcListMode || IsGbcWorkspaceMode())
        {
            ShowGbcMenuPreviewDialog();
            return;
        }

        if (currentPreviewUsesGbaFirstScreenMode)
        {
            ShowGbaMenuCoverBrowserDialog();
            return;
        }

        if (pictureBoxMenuBackgroundPreview.Image is not Image sourceImage)
        {
            return;
        }

        using Form dialog = new DpiAwareForm()
        {
            Text = currentPreviewUsesGbaFirstScreenMode
                ? T("ui", "menu_first_screen_preview", "Menu First Screen Preview")
                : T("ui", "menu_background_preview", "Background Preview"),
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(MenuBackgroundPreviewDialogDefaultWidth, 640),
            MinimumSize = new Size(MenuBackgroundPreviewDialogMinWidth, 420),
        };

        Image? dialogImage = (Image)sourceImage.Clone();

        PictureBox pictureBox = new NearestNeighborPictureBox()
        {
            Dock = DockStyle.Fill,
            BackColor = SystemColors.Control,
            SizeMode = PictureBoxSizeMode.Zoom,
            Image = dialogImage,
        };

        void ReplaceDialogImage(Image newImage)
        {
            Image? previousImage = dialogImage;
            dialogImage = newImage;
            pictureBox.Image = dialogImage;
            previousImage?.Dispose();
        }

        ContextMenuStrip? contextMenu = null;
        ToolStripMenuItem? exportItem = null;
        if (!currentPreviewUsesGbcListMode && !currentPreviewUsesGbaFirstScreenMode)
        {
            contextMenu = new ContextMenuStrip();
            exportItem = new ToolStripMenuItem(T("ui", "export_background_image", "Export Background Image"));
            exportItem.Enabled = pictureBox.Image is not null;
            exportItem.Click += (_, _) =>
            {
                if (pictureBox.Image is Image image)
                {
                    ExportMenuBackgroundImage(image, dialog);
                }
            };

            var importItem = new ToolStripMenuItem(T("ui", "import_background_image", "Import Background Image"));
            importItem.Enabled = !isBusy && CanUpdateMenuBackgroundCurrentScan(out _);
            importItem.Click += async (_, _) => await RunUiActionSafelyAsync(async () =>
            {
                bool updated = await UpdateMenuBackgroundAsync(dialog);
                if (updated && pictureBoxMenuBackgroundPreview.Image is Image refreshedImage)
                {
                    ReplaceDialogImage((Image)refreshedImage.Clone());
                    if (exportItem is not null)
                    {
                        exportItem.Enabled = true;
                    }
                }
            });

            contextMenu.Items.Add(importItem);
            contextMenu.Items.Add(exportItem);
            pictureBox.ContextMenuStrip = contextMenu;
            dialog.ContextMenuStrip = contextMenu;
        }

        dialog.FormClosed += (_, _) => dialogImage?.Dispose();

        Panel footerPanel = new()
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 8, 12, 8),
            BackColor = SystemColors.Control,
            Margin = Padding.Empty,
        };

        Label requirementsLabel = new()
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            BackColor = SystemColors.Control,
            TextAlign = ContentAlignment.TopLeft,
            Text = T(
                "ui",
                "background_image_requirements_text",
                "Image requirements:\r\n\r\n1. PNG is recommended.\r\n2. Size must be exactly 240x160.\r\n3. The image must use no more than 256 colors.\r\n4. Importing will overwrite the current menu background on the cart."),
        };
        if (!currentPreviewUsesGbcListMode && !currentPreviewUsesGbaFirstScreenMode)
        {
            footerPanel.Controls.Add(requirementsLabel);
        }

        TableLayoutPanel dialogLayout = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = currentPreviewUsesGbcListMode || currentPreviewUsesGbaFirstScreenMode ? 1 : 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        dialogLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        dialogLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        if (!currentPreviewUsesGbcListMode && !currentPreviewUsesGbaFirstScreenMode)
        {
            dialogLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 1F));
        }
        dialogLayout.Controls.Add(pictureBox, 0, 0);
        if (!currentPreviewUsesGbcListMode && !currentPreviewUsesGbaFirstScreenMode)
        {
            dialogLayout.Controls.Add(footerPanel, 0, 1);
        }

        dialog.Controls.Add(dialogLayout);
        if (!currentPreviewUsesGbcListMode && !currentPreviewUsesGbaFirstScreenMode)
        {
            dialog.Shown += (_, _) => EnsureMenuBackgroundPreviewDialogLayout(dialog, dialogLayout, requirementsLabel);
            dialog.SizeChanged += (_, _) => EnsureMenuBackgroundPreviewDialogLayout(dialog, dialogLayout, requirementsLabel);
        }
        dialog.ShowDialog(this);
    }

    private void ShowGbaMenuCoverBrowserDialog()
    {
        WaterCartScanResult? result = currentScanResult;
        if (result?.GbaMenuFirstScreenPreview.IsDetected != true)
        {
            return;
        }

        List<MenuItemConfigInfo> items = GetGbaMenuPreviewItems(result)
            .Select(WaterMenuConfigCodec.Clone)
            .ToList();
        if (items.Count == 0)
        {
            return;
        }

        Color pageBackground = Color.FromArgb(247, 248, 250);
        Color mutedText = Color.FromArgb(91, 98, 108);
        using Form dialog = new DpiAwareForm()
        {
            Text = T("ui", "menu_cover_browser", "Menu Cover Browser"),
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(1280, 720),
            MinimumSize = new Size(960, 560),
            BackColor = pageBackground,
            KeyPreview = true,
        };

        Label previewTitle = new()
        {
            Dock = DockStyle.Top,
            Height = 54,
            Padding = new Padding(24, 17, 24, 0),
            Font = new Font(Font.FontFamily, 12F, FontStyle.Bold),
            ForeColor = Color.FromArgb(24, 26, 30),
        };
        PictureBox menuPreview = new NearestNeighborPictureBox()
        {
            Dock = DockStyle.Fill,
            BackColor = pageBackground,
            SizeMode = PictureBoxSizeMode.Zoom,
            Margin = Padding.Empty,
        };
        Label positionLabel = new()
        {
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = mutedText,
            Margin = new Padding(16, 9, 16, 0),
        };
        Button autoMatchButton = new()
        {
            Text = T("ui", "auto_match_covers", "Auto Match"),
            AutoSize = true,
            MinimumSize = new Size(120, 34),
            Margin = new Padding(0, 4, 8, 4),
        };
        Button saveButton = new()
        {
            Text = T("ui", "save_cover_changes", "Save"),
            AutoSize = true,
            MinimumSize = new Size(90, 34),
            Enabled = false,
            Margin = new Padding(0, 4, 8, 4),
        };
        Button cancelButton = new()
        {
            Text = T("ui", "cancel_cover_changes", "Cancel"),
            AutoSize = true,
            MinimumSize = new Size(90, 34),
            DialogResult = DialogResult.Cancel,
            Margin = new Padding(0, 4, 0, 4),
        };
        FlowLayoutPanel navigation = new()
        {
            Dock = DockStyle.Bottom,
            Height = 58,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(24, 8, 24, 8),
            BackColor = pageBackground,
        };
        navigation.Controls.Add(autoMatchButton);
        navigation.Controls.Add(saveButton);
        navigation.Controls.Add(cancelButton);
        navigation.Controls.Add(positionLabel);

        Panel previewPanel = new()
        {
            Dock = DockStyle.Fill,
            BackColor = pageBackground,
            Padding = new Padding(24, 0, 24, 12),
        };
        previewPanel.Controls.Add(menuPreview);
        previewPanel.Controls.Add(navigation);
        previewPanel.Controls.Add(previewTitle);

        Label coverListTitle = new()
        {
            Dock = DockStyle.Top,
            Height = 54,
            Padding = new Padding(16, 17, 12, 0),
            Text = T("ui", "game_covers", "Game Covers"),
            Font = new Font(Font.FontFamily, 11F, FontStyle.Bold),
            ForeColor = Color.FromArgb(24, 26, 30),
        };
        ImageList coverImages = new()
        {
            ColorDepth = ColorDepth.Depth32Bit,
            ImageSize = new Size(GbaCoverThumbnailSize, GbaCoverThumbnailSize),
        };
        ListView coverList = new()
        {
            Dock = DockStyle.Fill,
            View = View.LargeIcon,
            LargeImageList = coverImages,
            MultiSelect = false,
            HideSelection = false,
            BorderStyle = BorderStyle.None,
            BackColor = Color.White,
            ForeColor = Color.FromArgb(24, 26, 30),
            LabelWrap = true,
            LabelEdit = true,
            ShowItemToolTips = true,
            AllowDrop = true,
        };

        GbaMenuFirstScreenPreviewInfo stagedPreview = new()
        {
            IsDetected = true,
            PackOffset = result.GbaMenuFirstScreenPreview.PackOffset,
            PackSize = result.GbaMenuFirstScreenPreview.PackSize,
            PaletteBase = result.GbaMenuFirstScreenPreview.PaletteBase,
            PaletteData = result.GbaMenuFirstScreenPreview.PaletteData.ToArray(),
            GameCodesByIndex = result.GbaMenuFirstScreenPreview.GameCodesByIndex
                .ToDictionary(pair => pair.Key, pair => pair.Value),
            CoverPixelsByIndex = result.GbaMenuFirstScreenPreview.CoverPixelsByIndex
                .ToDictionary(pair => pair.Key, pair => pair.Value.ToArray()),
        };
        Color[] palette = BuildGbaPreviewCoverPalette(stagedPreview);
        List<Bitmap?> stagedCovers = [];
        List<Bitmap?> originalCovers = [];
        string[] originalTitles = items.Select(item => item.Title).ToArray();
        bool[] coverChanged = new bool[items.Count];
        bool[] titleChanged = new bool[items.Count];
        for (int index = 0; index < items.Count; index++)
        {
            Bitmap? decodedCover = DecodeGbaMenuCover(stagedPreview, items[index], palette);
            stagedCovers.Add(decodedCover);
            originalCovers.Add(decodedCover is null ? null : new Bitmap(decodedCover));
            coverImages.Images.Add(CreateGbaCoverGridThumbnail(decodedCover));
            ListViewItem listItem = new(items[index].Title, index)
            {
                Tag = index,
                ToolTipText = items[index].Title,
            };
            coverList.Items.Add(listItem);
        }

        ContextMenuStrip coverMenu = new();
        ToolStripMenuItem undoCoverItem = new(T("ui", "undo_cover_change", "Undo Cover Change"));
        ToolStripMenuItem deleteCoverItem = new(T("ui", "delete_cover", "Delete Cover"));
        ToolStripMenuItem importCoverItem = new(T("ui", "import_cover", "Import Cover"));
        ToolStripMenuItem exportCoverItem = new(T("ui", "export_current_cover", "Export Cover"));
        coverMenu.Items.AddRange(
            [undoCoverItem, deleteCoverItem, importCoverItem, new ToolStripSeparator(), exportCoverItem]);
        coverList.ContextMenuStrip = coverMenu;

        Panel listPanel = new()
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            Padding = new Padding(0, 0, 8, 8),
        };
        listPanel.Controls.Add(coverList);
        listPanel.Controls.Add(coverListTitle);

        TableLayoutPanel layout = new()
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
        dialog.Controls.Add(layout);

        int selectedIndex = 0;
        Bitmap? previewImage = null;
        bool isEditingTitle = false;

        void SelectGame(int index, bool synchronizeList)
        {
            selectedIndex = Math.Clamp(index, 0, items.Count - 1);
            Bitmap nextPreview = RenderGbaMenuFirstScreenPreview(result, selectedIndex, stagedPreview, items);
            Bitmap? previousPreview = previewImage;
            previewImage = nextPreview;
            menuPreview.Image = previewImage;
            previousPreview?.Dispose();
            previewTitle.Text = items[selectedIndex].Title;
            positionLabel.Text = $"{selectedIndex + 1:D3} / {items.Count:D3}";
            if (synchronizeList && coverList.Items.Count > selectedIndex)
            {
                coverList.SelectedIndices.Clear();
                coverList.Items[selectedIndex].Selected = true;
                coverList.Items[selectedIndex].Focused = true;
                coverList.EnsureVisible(selectedIndex);
            }
        }

        coverList.SelectedIndexChanged += (_, _) =>
        {
            if (coverList.SelectedIndices.Count > 0 && coverList.SelectedIndices[0] != selectedIndex)
            {
                SelectGame(coverList.SelectedIndices[0], false);
            }
        };
        coverList.BeforeLabelEdit += (_, _) => isEditingTitle = true;
        coverList.AfterLabelEdit += (_, eventArgs) =>
        {
            isEditingTitle = false;
            if (eventArgs.Item < 0 || eventArgs.Item >= items.Count || eventArgs.Label is null)
            {
                return;
            }

            string normalizedTitle = WaterMenuConfigCodec.NormalizeTitle(eventArgs.Label);
            int editedIndex = eventArgs.Item;
            items[editedIndex].Title = normalizedTitle;
            items[editedIndex].TitleLength = checked((byte)normalizedTitle.Length);
            titleChanged[editedIndex] = !string.Equals(
                normalizedTitle,
                originalTitles[editedIndex],
                StringComparison.Ordinal);
            RefreshSaveButtonState();
            eventArgs.CancelEdit = true;
            BeginInvoke(() =>
            {
                coverList.Items[editedIndex].Text = normalizedTitle;
                coverList.Items[editedIndex].ToolTipText = normalizedTitle;
                SelectGame(editedIndex, true);
            });
        };
        coverList.MouseDoubleClick += (_, eventArgs) =>
        {
            ListViewHitTestInfo hit = coverList.HitTest(eventArgs.Location);
            if (hit.Item is not null && (hit.Location & ListViewHitTestLocations.Label) != 0)
            {
                hit.Item.Selected = true;
                hit.Item.Focused = true;
                hit.Item.BeginEdit();
            }
        };

        void ExportCover(int index)
        {
            if (index < 0 || index >= stagedCovers.Count || stagedCovers[index] is not Bitmap cover)
            {
                return;
            }

            using SaveFileDialog saveDialog = new()
            {
                Title = T("dialog", "export_menu_cover_title", "Export Menu Cover"),
                FileName = $"{SanitizeSaveFileName(items[index].Title)}.png",
                Filter = T("dialog", "export_menu_cover_filter", "PNG Image (*.png)|*.png|All files (*.*)|*.*"),
                AddExtension = true,
                DefaultExt = "png",
            };
            if (saveDialog.ShowDialog(dialog) == DialogResult.OK)
            {
                cover.Save(saveDialog.FileName, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        void RefreshSaveButtonState() =>
            saveButton.Enabled = coverChanged.Any(changed => changed) || titleChanged.Any(changed => changed);

        Dictionary<int, string> BuildGameCodesByBucket()
        {
            Dictionary<int, string> gameCodesByBucket = result.Games
                .Where(game => !game.IsMenu && !string.IsNullOrWhiteSpace(game.GameCode))
                .GroupBy(game => game.StartBucket)
                .ToDictionary(group => group.Key, group => group.First().GameCode);
            foreach (MenuItemConfigInfo item in items)
            {
                if (!gameCodesByBucket.ContainsKey(item.StartBucket) &&
                    stagedPreview.GameCodesByIndex.TryGetValue(item.CoverIndex, out string? packedCode))
                {
                    gameCodesByBucket[item.StartBucket] = packedCode;
                }
            }

            return gameCodesByBucket;
        }

        async Task RebuildStagedCoverPreviewAsync()
        {
            Dictionary<int, string> gameCodesByBucket = BuildGameCodesByBucket();
            GbaMenuCoverPackBuildResult stagedPack = await Task.Run(() =>
                BuildStagedGbaCoverPack(items, stagedCovers, gameCodesByBucket));
            stagedPreview.PaletteBase = GbaMenuCoverPackBuilder.PaletteBase;
            stagedPreview.PaletteData = stagedPack.PaletteData;
            AssignPackedCoverIndexes(items, stagedCovers);
            stagedPreview.GameCodesByIndex = BuildPackedGameCodes(items, stagedCovers, gameCodesByBucket);
            stagedPreview.CoverPixelsByIndex = stagedPack.CoverPixelsByIndex;
        }

        async Task<bool> ReplaceStagedCoverAsync(
            int index,
            Bitmap replacement,
            string toolTipText,
            bool changed)
        {
            Bitmap? previousCover = stagedCovers[index];
            bool previousChanged = coverChanged[index];
            stagedCovers[index] = replacement;
            try
            {
                autoMatchButton.Enabled = false;
                saveButton.Enabled = false;
                await RebuildStagedCoverPreviewAsync();

                previousCover?.Dispose();
                coverImages.Images[index] = CreateGbaCoverGridThumbnail(replacement);
                coverList.Items[index].ToolTipText = toolTipText;
                coverChanged[index] = changed;
                RefreshSaveButtonState();
                SelectGame(index, true);
                coverList.Invalidate();
                return true;
            }
            catch (Exception ex)
            {
                stagedCovers[index] = previousCover;
                replacement.Dispose();
                coverChanged[index] = previousChanged;
                RefreshSaveButtonState();
                MessageBox.Show(
                    dialog,
                    Tf("error", "cover_drop_failed", "Unable to replace the cover: {0}", ex.Message),
                    T("ui", "game_covers", "Game Covers"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return false;
            }
            finally
            {
                autoMatchButton.Enabled = true;
            }
        }

        void DeleteStagedCover(int index)
        {
            if (index < 0 || index >= stagedCovers.Count || stagedCovers[index] is null)
            {
                return;
            }

            Bitmap? previousCover = stagedCovers[index];
            stagedCovers[index] = null;
            previousCover?.Dispose();
            items[index].CoverIndex = 0xFF;
            items[index].CoverFlags = 0;
            coverImages.Images[index] = CreateGbaCoverGridThumbnail(null);
            coverList.Items[index].ToolTipText = items[index].Title;
            coverChanged[index] = originalCovers[index] is not null;
            RefreshSaveButtonState();
            SelectGame(index, true);
            coverList.Invalidate();
        }

        async Task ImportCoverAsync(int index)
        {
            using OpenFileDialog openDialog = new()
            {
                Title = T("ui", "import_cover", "Import Cover"),
                Filter = T("dialog", "export_menu_cover_filter", "PNG Image (*.png)|*.png|All files (*.*)|*.*"),
                CheckFileExists = true,
                Multiselect = false,
            };
            if (openDialog.ShowDialog(dialog) != DialogResult.OK)
            {
                return;
            }

            Bitmap replacement;
            try
            {
                using Image source = Image.FromFile(openDialog.FileName);
                replacement = GbaMenuCoverPackBuilder.FitCover(source);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    dialog,
                    Tf("error", "cover_drop_failed", "Unable to replace the cover: {0}", ex.Message),
                    T("ui", "game_covers", "Game Covers"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            await ReplaceStagedCoverAsync(
                index,
                replacement,
                $"{items[index].Title}\r\n{Path.GetFileName(openDialog.FileName)}",
                changed: true);
        }

        async Task UndoCoverChangeAsync(int index)
        {
            if (!coverChanged[index])
            {
                return;
            }

            if (originalCovers[index] is not Bitmap originalCover)
            {
                DeleteStagedCover(index);
                coverChanged[index] = false;
                RefreshSaveButtonState();
                return;
            }

            Bitmap replacement = new(originalCover);
            await ReplaceStagedCoverAsync(
                index,
                replacement,
                items[index].Title,
                changed: false);
        }

        ListViewItem? FindCoverItemAt(Point location) =>
            coverList.GetItemAt(location.X, location.Y) ??
            coverList.Items.Cast<ListViewItem>().FirstOrDefault(item => item.Bounds.Contains(location));

        coverList.MouseDown += (_, eventArgs) =>
        {
            if (eventArgs.Button == MouseButtons.Left)
            {
                foreach (ListViewItem item in coverList.Items)
                {
                    Rectangle iconBounds = item.GetBounds(ItemBoundsPortion.Icon);
                    if (iconBounds.Width <= 0 || iconBounds.Height <= 0)
                    {
                        continue;
                    }

                    float scaleX = iconBounds.Width / (float)GbaCoverThumbnailSize;
                    float scaleY = iconBounds.Height / (float)GbaCoverThumbnailSize;
                    Rectangle clearBounds = new(
                        iconBounds.Left + (int)Math.Round(GbaCoverThumbnailCloseBounds.Left * scaleX),
                        iconBounds.Top + (int)Math.Round(GbaCoverThumbnailCloseBounds.Top * scaleY),
                        Math.Max(1, (int)Math.Round((GbaCoverThumbnailCloseBounds.Width + 1) * scaleX)),
                        Math.Max(1, (int)Math.Round((GbaCoverThumbnailCloseBounds.Height + 1) * scaleY)));
                    if (!clearBounds.Contains(eventArgs.Location))
                    {
                        continue;
                    }

                    DeleteStagedCover(item.Index);
                    return;
                }
            }

            if (eventArgs.Button == MouseButtons.Right && FindCoverItemAt(eventArgs.Location) is ListViewItem hit)
            {
                hit.Selected = true;
                hit.Focused = true;
            }
            else if (eventArgs.Button == MouseButtons.Right)
            {
                coverList.SelectedIndices.Clear();
            }
        };
        coverMenu.Opening += (_, eventArgs) =>
        {
            eventArgs.Cancel = coverList.SelectedIndices.Count == 0;
            if (eventArgs.Cancel)
            {
                return;
            }

            int index = coverList.SelectedIndices[0];
            undoCoverItem.Enabled = coverChanged[index];
            deleteCoverItem.Enabled = stagedCovers[index] is not null;
            exportCoverItem.Enabled = stagedCovers[index] is not null;
        };
        undoCoverItem.Click += async (_, _) =>
        {
            if (coverList.SelectedIndices.Count > 0)
            {
                await UndoCoverChangeAsync(coverList.SelectedIndices[0]);
            }
        };
        deleteCoverItem.Click += (_, _) =>
        {
            if (coverList.SelectedIndices.Count > 0)
            {
                DeleteStagedCover(coverList.SelectedIndices[0]);
            }
        };
        importCoverItem.Click += async (_, _) =>
        {
            if (coverList.SelectedIndices.Count > 0)
            {
                await ImportCoverAsync(coverList.SelectedIndices[0]);
            }
        };
        exportCoverItem.Click += (_, _) =>
        {
            if (coverList.SelectedIndices.Count > 0)
            {
                ExportCover(coverList.SelectedIndices[0]);
            }
        };

        bool TryGetCoverDropTarget(DragEventArgs eventArgs, out int targetIndex, out string? imagePath)
        {
            targetIndex = -1;
            imagePath = null;
            if (eventArgs.Data?.GetData(DataFormats.FileDrop) is not string[] files || files.Length != 1 || !File.Exists(files[0]))
            {
                return false;
            }

            Point clientPoint = coverList.PointToClient(new Point(eventArgs.X, eventArgs.Y));
            ListViewItem? target = FindCoverItemAt(clientPoint);
            if (target is null)
            {
                return false;
            }

            targetIndex = target.Index;
            imagePath = files[0];
            return true;
        }

        coverList.DragEnter += (sender, eventArgs) =>
        {
            eventArgs.Effect = TryGetCoverDropTarget(eventArgs, out _, out _)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        };
        coverList.DragOver += (sender, eventArgs) =>
        {
            if (!TryGetCoverDropTarget(eventArgs, out int targetIndex, out _))
            {
                eventArgs.Effect = DragDropEffects.None;
                return;
            }

            eventArgs.Effect = DragDropEffects.Copy;
            if (!coverList.Items[targetIndex].Selected)
            {
                coverList.SelectedIndices.Clear();
                coverList.Items[targetIndex].Selected = true;
                coverList.Items[targetIndex].Focused = true;
            }
        };
        coverList.DragDrop += async (_, eventArgs) =>
        {
            if (!TryGetCoverDropTarget(eventArgs, out int targetIndex, out string? imagePath) || imagePath is null)
            {
                return;
            }

            Bitmap replacement;
            try
            {
                using Image source = Image.FromFile(imagePath);
                replacement = GbaMenuCoverPackBuilder.FitCover(source);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    dialog,
                    Tf("error", "cover_drop_failed", "Unable to replace the cover: {0}", ex.Message),
                    T("ui", "game_covers", "Game Covers"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            await ReplaceStagedCoverAsync(
                targetIndex,
                replacement,
                $"{items[targetIndex].Title}\r\n{Path.GetFileName(imagePath)}",
                changed: true);
        };

        autoMatchButton.Click += async (_, _) =>
        {
            autoMatchButton.Enabled = false;
            try
            {
                string? catalogDirectory = GbaCoverCatalog.FindCatalogDirectory();
                if (catalogDirectory is null)
                {
                    MessageBox.Show(
                        dialog,
                        T("error", "cover_catalog_not_found", "The local cover catalog could not be found."),
                        T("ui", "auto_match_covers", "Auto Match"),
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }

                Dictionary<string, GbaCoverCatalogEntry> catalog =
                    GbaCoverCatalog.LoadPrimaryByGameCode(catalogDirectory);

                Dictionary<int, string> gameCodesByBucket = result.Games
                    .Where(game => !game.IsMenu && !string.IsNullOrWhiteSpace(game.GameCode))
                    .GroupBy(game => game.StartBucket)
                    .ToDictionary(group => group.Key, group => group.First().GameCode);
                foreach (MenuItemConfigInfo item in items)
                {
                    if (!gameCodesByBucket.ContainsKey(item.StartBucket) &&
                        stagedPreview.GameCodesByIndex.TryGetValue(item.CoverIndex, out string? packedCode))
                    {
                        gameCodesByBucket[item.StartBucket] = packedCode;
                    }
                }
                int matched = 0;
                for (int index = 0; index < items.Count; index++)
                {
                    if (!gameCodesByBucket.TryGetValue(items[index].StartBucket, out string? gameCode) ||
                        !GbaCoverCatalog.TryFindByGameCode(
                            catalog,
                            gameCode,
                            out GbaCoverCatalogEntry? entry,
                            out string matchedGameCode) ||
                        entry is null)
                    {
                        continue;
                    }

                    Bitmap? replacement = null;
                    try
                    {
                        using Image source = Image.FromFile(entry.CoverPath);
                        replacement = GbaMenuCoverPackBuilder.FitCover(source);
                        Bitmap thumbnail = CreateGbaCoverGridThumbnail(replacement);
                        coverImages.Images[index] = thumbnail;
                        Bitmap? previousCover = stagedCovers[index];
                        stagedCovers[index] = replacement;
                        replacement = null;
                        previousCover?.Dispose();
                        string matchDetail = string.Equals(gameCode, matchedGameCode, StringComparison.OrdinalIgnoreCase)
                            ? gameCode
                            : $"{gameCode} -> {matchedGameCode}";
                        coverList.Items[index].ToolTipText = $"{items[index].Title}\r\n{entry.Title}\r\n{matchDetail}";
                        coverChanged[index] = true;
                        matched++;
                    }
                    catch
                    {
                        replacement?.Dispose();
                    }
                }

                coverList.Invalidate();
                if (matched == 0)
                {
                    MessageBox.Show(
                        dialog,
                        Tf("ui", "cover_match_result", "Matched {0} of {1} covers.", matched, items.Count),
                        T("ui", "auto_match_covers", "Auto Match"),
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }

                GbaMenuCoverPackBuildResult stagedPack = await Task.Run(() =>
                    BuildStagedGbaCoverPack(items, stagedCovers, gameCodesByBucket));
                stagedPreview.PaletteBase = GbaMenuCoverPackBuilder.PaletteBase;
                stagedPreview.PaletteData = stagedPack.PaletteData;
                AssignPackedCoverIndexes(items, stagedCovers);
                stagedPreview.GameCodesByIndex = BuildPackedGameCodes(items, stagedCovers, gameCodesByBucket);
                stagedPreview.CoverPixelsByIndex = stagedPack.CoverPixelsByIndex;
                RefreshSaveButtonState();
                SelectGame(selectedIndex, false);

                MessageBox.Show(
                    dialog,
                    Tf("ui", "cover_match_result", "Matched {0} of {1} covers.", matched, items.Count),
                    T("ui", "auto_match_covers", "Auto Match"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                RefreshSaveButtonState();
                MessageBox.Show(
                    dialog,
                    Tf("error", "cover_auto_match_failed", "Automatic cover matching failed: {0}", ex.Message),
                    T("ui", "auto_match_covers", "Auto Match"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                autoMatchButton.Enabled = true;
            }
        };

        saveButton.Click += async (_, _) =>
        {
            DialogResult confirmation = MessageBox.Show(
                dialog,
                T(
                    "dialog",
                    "save_menu_covers_confirm_message",
                    "Write the pending cover and game-name changes to the cart now? The menu cover area and item list will be updated immediately."),
                T("dialog", "save_menu_covers_confirm_title", "Save Menu Covers"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (confirmation != DialogResult.Yes)
            {
                return;
            }

            Dictionary<int, string> gameCodesByBucket = result.Games
                .Where(game => !game.IsMenu && !string.IsNullOrWhiteSpace(game.GameCode))
                .GroupBy(game => game.StartBucket)
                .ToDictionary(group => group.Key, group => group.First().GameCode);
            foreach (MenuItemConfigInfo item in items)
            {
                if (!gameCodesByBucket.ContainsKey(item.StartBucket) &&
                    stagedPreview.GameCodesByIndex.TryGetValue(item.CoverIndex, out string? packedCode))
                {
                    gameCodesByBucket[item.StartBucket] = packedCode;
                }
            }
            try
            {
                saveButton.Enabled = false;
                autoMatchButton.Enabled = false;
                bool saved = await UpdateGbaMenuCoversAsync(result, items, stagedCovers, gameCodesByBucket);
                if (saved)
                {
                    dialog.DialogResult = DialogResult.OK;
                    dialog.Close();
                }
                else
                {
                    saveButton.Enabled = true;
                    autoMatchButton.Enabled = true;
                }
            }
            catch (Exception ex)
            {
                RecoverFromUiException(ex);
                saveButton.Enabled = true;
                autoMatchButton.Enabled = true;
            }
        };
        dialog.KeyDown += (_, eventArgs) =>
        {
            if (isEditingTitle)
            {
                return;
            }

            if (eventArgs.KeyCode == Keys.Left)
            {
                SelectGame(selectedIndex - 1, true);
                eventArgs.Handled = true;
                eventArgs.SuppressKeyPress = true;
            }
            else if (eventArgs.KeyCode == Keys.Right)
            {
                SelectGame(selectedIndex + 1, true);
                eventArgs.Handled = true;
                eventArgs.SuppressKeyPress = true;
            }
        };
        dialog.FormClosed += (_, _) =>
        {
            previewImage?.Dispose();
            foreach (Bitmap? cover in stagedCovers)
            {
                cover?.Dispose();
            }
            foreach (Bitmap? cover in originalCovers)
            {
                cover?.Dispose();
            }
            coverMenu.Dispose();
        };
        SelectGame(0, true);
        dialog.ShowDialog(this);
        coverImages.Dispose();
    }

    private static Bitmap CreateGbaCoverGridThumbnail(Image? cover)
    {
        Bitmap thumbnail = new(GbaCoverThumbnailSize, GbaCoverThumbnailSize);
        using Graphics graphics = Graphics.FromImage(thumbnail);
        graphics.Clear(Color.FromArgb(238, 240, 244));
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        if (cover is not null)
        {
            graphics.DrawImage(cover, new Rectangle(6, 6, 92, 92));
        }

        using SolidBrush closeBackground = new(Color.White);
        using Pen closeBorder = new(Color.FromArgb(105, 111, 120));
        Font baseFont = SystemFonts.MessageBoxFont ?? Control.DefaultFont;
        using Font closeFont = new(baseFont.FontFamily, 11F, FontStyle.Regular, GraphicsUnit.Pixel);
        graphics.FillRectangle(closeBackground, GbaCoverThumbnailCloseBounds);
        graphics.DrawRectangle(closeBorder, GbaCoverThumbnailCloseBounds);
        TextRenderer.DrawText(
            graphics,
            "\u00D7",
            closeFont,
            GbaCoverThumbnailCloseBounds,
            Color.FromArgb(38, 42, 48),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        return thumbnail;
    }

    private static GbaMenuCoverPackBuildResult BuildStagedGbaCoverPack(
        IReadOnlyList<MenuItemConfigInfo> items,
        IReadOnlyList<Bitmap?> covers,
        IReadOnlyDictionary<int, string> gameCodesByBucket)
    {
        List<GbaMenuCoverSource> sources = [];
        for (int index = 0; index < items.Count; index++)
        {
            if (covers[index] is not Bitmap cover)
            {
                continue;
            }
            if (sources.Count >= GbaMenuCoverPackBuilder.MaxCoverCount)
            {
                break;
            }
            string gameCode = gameCodesByBucket.TryGetValue(items[index].StartBucket, out string? code)
                ? code
                : "UNKN";
            sources.Add(new GbaMenuCoverSource(gameCode, cover));
        }

        if (sources.Count == 0)
        {
            Bitmap placeholder = new(GbaMenuCoverPackBuilder.CoverWidth, GbaMenuCoverPackBuilder.CoverHeight);
            using (Graphics graphics = Graphics.FromImage(placeholder))
            {
                graphics.Clear(Color.White);
            }
            sources.Add(new GbaMenuCoverSource("UNKN", placeholder));
            try
            {
                return GbaMenuCoverPackBuilder.Build(sources);
            }
            finally
            {
                placeholder.Dispose();
            }
        }
        return GbaMenuCoverPackBuilder.Build(sources);
    }

    private static void AssignPackedCoverIndexes(
        IReadOnlyList<MenuItemConfigInfo> items,
        IReadOnlyList<Bitmap?> covers)
    {
        byte packedIndex = 0;
        for (int index = 0; index < items.Count; index++)
        {
            if (covers[index] is null || packedIndex >= GbaMenuCoverPackBuilder.MaxCoverCount)
            {
                items[index].CoverIndex = 0xFF;
                items[index].CoverFlags = 0;
                continue;
            }

            items[index].CoverIndex = packedIndex++;
            items[index].CoverFlags = 1;
        }
    }

    private static Dictionary<int, string> BuildPackedGameCodes(
        IReadOnlyList<MenuItemConfigInfo> items,
        IReadOnlyList<Bitmap?> covers,
        IReadOnlyDictionary<int, string> gameCodesByBucket)
    {
        Dictionary<int, string> result = [];
        int packedIndex = 0;
        for (int index = 0; index < items.Count; index++)
        {
            if (covers[index] is null || packedIndex >= GbaMenuCoverPackBuilder.MaxCoverCount)
            {
                continue;
            }

            result[packedIndex++] = gameCodesByBucket.TryGetValue(items[index].StartBucket, out string? code)
                ? code
                : "UNKN";
        }
        return result;
    }

    private void EnsureMenuBackgroundPreviewDialogLayout(Form dialog, TableLayoutPanel dialogLayout, Label requirementsLabel)
    {
        if (dialogLayout.RowStyles.Count < 2 || dialogLayout.GetControlFromPosition(0, 1) is not Panel footerPanel)
        {
            return;
        }

        int textWidth = Math.Max(200, dialog.ClientSize.Width - footerPanel.Padding.Horizontal);
        int textHeight = TextRenderer.MeasureText(
            requirementsLabel.Text,
            requirementsLabel.Font,
            new Size(textWidth, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height;
        int footerHeight = textHeight + footerPanel.Padding.Vertical;
        if (Math.Abs(dialogLayout.RowStyles[1].Height - footerHeight) > 0.5F)
        {
            dialogLayout.RowStyles[1].Height = footerHeight;
        }

        int desiredClientHeight = (int)Math.Ceiling(dialog.ClientSize.Width * BackgroundImageEncoder.Height / (double)BackgroundImageEncoder.Width)
            + footerHeight;
        if (dialog.ClientSize.Height < desiredClientHeight)
        {
            dialog.ClientSize = new Size(dialog.ClientSize.Width, desiredClientHeight);
        }
    }

    private void RefreshGridHeaders()
    {
        dataGridViewRoms.Columns["Order"]!.HeaderText = T("grid", "order", "Order");
        dataGridViewRoms.Columns["State"]!.HeaderText = T("grid", "state", "State");
        dataGridViewRoms.Columns["Kind"]!.HeaderText = T("grid", "kind", "Kind");
        dataGridViewRoms.Columns["Source"]!.HeaderText = T("grid", "source", "Source");
        dataGridViewRoms.Columns["Title"]!.HeaderText = T("grid", "title", "Display Title");
        dataGridViewRoms.Columns["HeaderTitle"]!.HeaderText = T("grid", "header_title", "Header Title");
        dataGridViewRoms.Columns["AllocSize"]!.HeaderText = T("grid", "alloc_size", "Alloc Size");
        dataGridViewRoms.Columns["SaveType"]!.HeaderText = T("grid", "save_type", "Save Type");
        dataGridViewRoms.Columns["GbcDedicatedSave"]!.HeaderText = T("grid", "save_allocation", "Dedicated");
        dataGridViewRoms.Columns["PatchStatus"]!.HeaderText = IsGbcWorkspaceMode()
            ? T("grid", "mbc_type", "MBC Type")
            : T("grid", "patch_status", "Patch");
        dataGridViewRoms.Columns["SpiSlot"]!.HeaderText = T("grid", "save_slot", "Save Slot");
        dataGridViewRoms.Columns["SaveStatus"]!.HeaderText = T("grid", "save_status", "Save Status");
    }

    private void UpdateWorkspaceGroupTitle()
    {
        if (IsMbc5EightMiBMode()) { groupBoxList.Text = "MBC5 8M · " + Mbc5LayoutDescription(); return; }
        var usage = GetWorkspaceUsageSummary();
        var saveUsage = GetWorkspaceSaveUsageSummary();
        int pendingCount = GetPendingWorkspaceItemCount();
        if (usage is not null)
        {
            bool isBeggarMaxGbcMode = currentScanResult?.IsBeggarMaxGbcMode == true;
            string usedText = isBeggarMaxGbcMode
                ? FormatWorkspaceUsageBytes(usage.Used)
                : FormatWorkspaceBucketSizeMb(usage.Used);
            string totalText = isBeggarMaxGbcMode
                ? FormatWorkspaceUsageBytes(usage.Total)
                : FormatWorkspaceBucketSizeMb(usage.Total);
            string pendingText = isBeggarMaxGbcMode
                ? FormatWorkspaceUsageBytes(usage.Pending)
                : FormatWorkspaceBucketSizeMb(usage.Pending);
            string freeText = isBeggarMaxGbcMode
                ? FormatWorkspaceUsageBytes(usage.Free)
                : FormatWorkspaceBucketSizeMb(usage.Free);
            string contiguousText = isBeggarMaxGbcMode
                ? FormatWorkspaceUsageBytes(usage.LargestContiguousFree)
                : FormatWorkspaceBucketSizeMb(usage.LargestContiguousFree);
            string saveUsageText = saveUsage.HasValue
                ? Tf(
                    "ui",
                    "rom_workspace_save_usage",
                    "Save {0}/{1} | free {2}",
                    saveUsage.Value.Used,
                    saveUsage.Value.Total,
                    saveUsage.Value.Free)
                : string.Empty;
            groupBoxList.Text = pendingCount > 0
                ? Tf(
                    "ui",
                    "rom_workspace_with_usage_pending",
                    string.IsNullOrWhiteSpace(saveUsageText)
                        ? "ROM Workspace (used {0}/{1} | pending {2} | free {3} | max contiguous {4})"
                        : "ROM Workspace (used {0}/{1} | pending {2} | free {3} | max contiguous {4} | {5})",
                    usedText,
                    totalText,
                    pendingText,
                    freeText,
                    contiguousText,
                    saveUsageText)
                : Tf(
                    "ui",
                    "rom_workspace_with_usage",
                    string.IsNullOrWhiteSpace(saveUsageText)
                        ? "ROM Workspace (used {0}/{1} | free {2} | max contiguous {3})"
                        : "ROM Workspace (used {0}/{1} | free {2} | max contiguous {3} | {4})",
                    usedText,
                    totalText,
                    freeText,
                    contiguousText,
                    saveUsageText);
            return;
        }

        groupBoxList.Text = pendingCount > 0
            ? Tf("ui", "rom_workspace_with_pending", "ROM Workspace ({0} pending)", pendingCount)
            : T("ui", "rom_workspace", "ROM Workspace");
    }

    private static Label CreateHeaderLabel()
    {
        return new SingleLineLabel
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(4),
            TextAlign = ContentAlignment.MiddleLeft,
        };
    }

    private static Label CreateInfoLabel(string text)
    {
        return new SingleLineLabel
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Text = text,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(70, 70, 70),
        };
    }

    private Label CreateInfoValueLabel(string text)
    {
        var label = new SingleLineLabel
        {
            Dock = DockStyle.Fill,
            Text = text,
            UseMnemonic = false,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(20, 20, 20),
        };
        label.TextChanged += (_, _) => infoValueToolTip.SetToolTip(label, label.Text);
        return label;
    }

    private static DataGridViewTextBoxColumn CreateTextColumn(string name, string header, int minimumWidth, float fillWeight)
    {
        return new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = header,
            MinimumWidth = minimumWidth,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = fillWeight,
            Resizable = DataGridViewTriState.True,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        };
    }

    private static DataGridViewCheckBoxColumn CreateCheckBoxColumn(string name, string header, int minimumWidth, float fillWeight)
    {
        return new DataGridViewCheckBoxColumn
        {
            Name = name,
            HeaderText = header,
            MinimumWidth = minimumWidth,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = fillWeight,
            Resizable = DataGridViewTriState.True,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            ThreeState = false,
        };
    }

    private static Font CreateSaveDistributionFont()
    {
        string[] preferredFamilies =
        [
            "NSimSun",
            "MS Gothic",
            "Consolas",
                "Segoe UI Symbol",
        ];

        foreach (string familyName in preferredFamilies)
        {
            if (FontFamily.Families.Any(family => string.Equals(family.Name, familyName, StringComparison.OrdinalIgnoreCase)))
            {
                return new Font(familyName, 4.5F, FontStyle.Regular, GraphicsUnit.Point);
            }
        }

        return new Font(Control.DefaultFont.FontFamily, 4.5F, FontStyle.Regular, GraphicsUnit.Point);
    }

    private static Button CreateHeaderButton(string text, int width)
    {
        return new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Size = new Size(width, 38),
            MinimumSize = new Size(width, 38),
            Anchor = AnchorStyles.None,
            Margin = new Padding(4),
            Padding = new Padding(4, 0, 4, 0),
            UseVisualStyleBackColor = true,
            TextAlign = ContentAlignment.MiddleCenter,
        };
    }

    private static Label CreateToolbarLabel()
    {
        return new SingleLineLabel
        {
            AutoSize = true,
            Anchor = AnchorStyles.None,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(4),
        };
    }

    private void EnsureContentSplitterLayout()
    {
        if (splitContainerContent is null || splitContainerContent.IsDisposed)
        {
            return;
        }

        int availableHeight = splitContainerContent.ClientSize.Height;
        if (availableHeight <= splitContainerContent.SplitterWidth)
        {
            return;
        }

        int minDistance = ContentSplitPanel1MinSize;
        int maxDistance = availableHeight - splitContainerContent.SplitterWidth - ContentSplitPanel2MinSize;
        if (maxDistance < minDistance)
        {
            return;
        }

        if (!contentSplitterInitialized)
        {
            splitContainerContent.Panel1MinSize = 0;
            splitContainerContent.Panel2MinSize = 0;
        }

        int currentDistance = contentSplitterInitialized
            ? splitContainerContent.SplitterDistance
            : availableHeight - splitContainerContent.SplitterWidth - ContentSplitPreferredLogHeight;
        int targetDistance = currentDistance <= 0
            ? maxDistance
            : currentDistance;
        targetDistance = Math.Clamp(targetDistance, minDistance, maxDistance);

        if (splitContainerContent.SplitterDistance != targetDistance)
        {
            splitContainerContent.SplitterDistance = targetDistance;
        }

        if (!contentSplitterInitialized)
        {
            splitContainerContent.Panel1MinSize = ContentSplitPanel1MinSize;
            splitContainerContent.Panel2MinSize = ContentSplitPanel2MinSize;
            contentSplitterInitialized = true;
        }
    }

    private void OnFirstShownEnsureContentSplitterLayout(object? sender, EventArgs e)
    {
        Shown -= OnFirstShownEnsureContentSplitterLayout;
        EnsureContentSplitterLayout();
    }
}
