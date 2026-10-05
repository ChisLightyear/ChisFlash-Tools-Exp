using System.Globalization;
using System.Drawing;
using System.Windows.Forms;
using ChisFlashWaterClient.Services;
using GbaMultiMenuBuilder.Models;
using GbaMultiMenuBuilder.Services;
using ClientAppConfigService = ChisFlashWaterClient.Services.AppConfigService;
using ClientLanguageService = ChisFlashWaterClient.Services.LanguageService;
using ClientUiLanguage = ChisFlashWaterClient.Services.UiLanguage;

namespace ChisFlashWaterClient;

public partial class Form1 : DpiAwareForm
{
    private const string TargetUsbVid = "CAFE";
    private const string TargetUsbPid = "4001";
    private const string TargetUsbInterface = "MI_00";
    private const string BeggarUsbVid = "0483";
    private const string BeggarUsbPid = "0721";
    private const string TargetDisplayName = "ChisFlash WaterLink";
    private const string BeggarDisplayName = "ChisFlash Beggar";
    private enum ProgrammerKind
    {
        Unknown,
        WaterLink,
        BeggarLegacy,
    }

    private enum BeggarCartMode
    {
        Gba,
        Gb,
    }

    private enum BurnProfileMode
    {
        BeggarNoBattery,
        BeggarFram,
        BeggarWithBattery,
        BeggarMaxSram,
        FcMbcx,
        WaterLinkSram,
        WaterLinkMultiNor,
        Chm1,
        Chm1NoBattery,
        Mbc5EightMiB,
        GbcUnselected,
        MbcxRtcEightMiB,
        MbcxRtcThirtyTwoMiB,
        GbcSixteenInOne,
    }

    private enum GbcMenuLanguageMode
    {
        Chinese,
        English,
    }

    private enum GbaMenuSelection
    {
        Auto,
        Legacy,
        Universal,
        Simplified,
    }

    private sealed class SerialPortEntry
    {
        public string DisplayName { get; init; } = string.Empty;
        public string PortName { get; init; } = string.Empty;
        public bool IsTargetMcu { get; init; }
        public ProgrammerKind Kind { get; init; }
        public string PnpDeviceId { get; init; } = string.Empty;
    }

    private sealed class PendingRomImportItem
    {
        public GbaMultiMenuBuilder.Md.MdRomCandidate? MdRom { get; init; }
        public string? MdReadError { get; init; }
        public RomPlatform? ImportedPlatform { get; init; }
        public GbaMultiMenuBuilder.Fc.NesRomInfo? FcRom { get; init; }
        public string FilePath { get; init; } = string.Empty;
        public string SourceImportKey { get; init; } = string.Empty;
        public bool ImportedFromMulticart { get; init; }
        public string FileName { get; init; } = string.Empty;
        public bool IsLoadingPlaceholder { get; init; }
        public string DisplayTitle { get; set; } = string.Empty;
        public string HeaderTitle { get; init; } = string.Empty;
        public string GbaGameCode { get; init; } = string.Empty;
        public string? CoverPath { get; set; }
        public bool CoverCleared { get; set; }
        public long FileSizeBytes { get; init; }
        public DetectedSaveTypeInfo SaveTypeInfo { get; init; } = new(DetectedSaveTypeKind.Unknown, null);
        public byte? GameBoyCartridgeType { get; init; }
        public bool GameBoyHasBattery { get; init; }
        public int GameBoyEffectiveRamBankCount { get; init; }
        public bool? GbcDedicatedSaveAllocationOverride { get; set; }
        public long GameBoyEffectiveRomSizeBytes { get; init; }
        public string SramPatchTargetIdentifier { get; init; } = string.Empty;
        public string AppliedSramPatchIdentifier { get; init; } = string.Empty;
        public bool ApplySramPatch { get; init; }
        public bool BatterylessAlreadyPatched { get; init; }
        public bool BatterylessHasPayloadSpace { get; init; }
        public bool BatterylessCanExpand { get; init; }
        public BatterylessPatchInspection? PreparedBatterylessInspection { get; init; }
    }

    private sealed record BurnProfileModeOption(string Name, BurnProfileMode Mode)
    {
        public override string ToString() => Name;
    }

    private sealed record GbcMenuLanguageOption(string Name, GbcMenuLanguageMode Mode)
    {
        public override string ToString() => Name;
    }

    private sealed record GbaMenuSelectionOption(string Name, GbaMenuSelection Selection)
    {
        public override string ToString() => Name;
    }

    private sealed class WorkspaceListRow
    {
        public WaterCartEntry? CartEntry { get; set; }
        public PendingRomImportItem? PendingItem { get; set; }
        public bool IsLoadingPlaceholder { get; set; }
        public string DisplayTitleOverride { get; set; } = string.Empty;
        public bool? GbcDedicatedSaveAllocationOverride { get; set; }
        public bool IsPending => PendingItem is not null;
        public bool IsCartEntry => CartEntry is not null;
    }

    private readonly Dictionary<string, SerialPortEntry> serialPorts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PendingRomImportItem> pendingImportItems = [];
    private readonly List<WorkspaceListRow> workspaceRows = [];
    private readonly BatterylessPatchService batterylessPatchService = new();
    private readonly IReadOnlyList<ClientUiLanguage> availableLanguages;
    private readonly SramPatchService sramPatchService = new();
    private List<BurnProfileModeOption> burnProfileModeOptions = [];
    private ClientUiLanguage language;

    private bool languageSelectionInitialized;
    private bool burnProfileModeSelectionInitialized;
    private bool beggarCartModeSelectionInitialized;
    private bool gbaMenuSelectionInitialized;
    private bool isBusy;
    private bool isAnalyzingQueueImports;
    private CancellationTokenSource? activeBurnCancellation;
    private bool isBurnCancellationRequested;
    private int queueAnalysisFileCount;
    private int activeQueueAnalysisBatchCount;
    private bool contentSplitterInitialized;
    private bool contentSplitterLayoutScheduled;

    private Label labelPortCaption = null!;
    private Label labelBeggarCartModeCaption = null!;
    private Label labelLanguageCaption = null!;
    private Label labelStatus = null!;
    private Label labelNorIdCaption = null!;
    private Label labelChipNameCaption = null!;
    private Label labelCapacityCaption = null!;
    private Label labelScanMethodCaption = null!;
    private Label labelSectorInfoCaption = null!;
    private Label labelRomCountCaption = null!;
    private Label labelMenuConfigCaption = null!;
    private Label labelMenuItemCountCaption = null!;
    private Label labelMenuTitleCaption = null!;
    private Label labelMenuBackgroundPreviewCaption = null!;
    private GroupBox groupBoxInfo = null!;
    private GroupBox groupBoxList = null!;
    private GroupBox groupBoxLog = null!;

    private ComboBox comboBoxPorts = null!;
    private ComboBox comboBoxLanguage = null!;
    private Panel panelProgrammerModeHost = null!;
    private FlowLayoutPanel panelBeggarCartMode = null!;
    private MenuStrip mainMenuStrip = null!;
    private ToolStripMenuItem menuDevice = null!;
    private ToolStripMenuItem menuCartridge = null!;
    private ToolStripMenuItem menuRom = null!;
    private ToolStripMenuItem menuSave = null!;
    private ToolStripMenuItem menuRefreshPorts = null!;
    private ToolStripMenuItem menuScan = null!;
    private ToolStripMenuItem menuExit = null!;
    private ToolStripMenuItem menuFragmentTest = null!;
    private ToolStripMenuItem menuDetectMagic = null!;
    private ToolStripMenuItem menuUpdateMenu = null!;
    private ToolStripMenuItem menuReplaceMenuFile = null!;
    private ToolStripMenuItem menuRepairMenu = null!;
    private ToolStripMenuItem menuUpdateBackground = null!;
    private ToolStripMenuItem menuEraseConfig = null!;
    private ToolStripMenuItem menuEraseEntireNor = null!;
    private ToolStripMenuItem menuUnlockPpb = null!;
    private ToolStripMenuItem menuAddRom = null!;
    private ToolStripMenuItem menuPendingGbaCovers = null!;
    private ToolStripMenuItem menuRemoveSelectedRom = null!;
    private ToolStripMenuItem menuClearRomQueue = null!;
    private ToolStripMenuItem menuReplaceSelectedRom = null!;
    private ToolStripMenuItem menuDeleteSelectedMapping = null!;
    private ToolStripMenuItem menuExportSelectedRom = null!;
    private ToolStripMenuItem menuStartBurn = null!;
    private ToolStripMenuItem menuExportSelectedSave = null!;
    private ToolStripMenuItem menuImportSelectedSave = null!;
    private ToolStripMenuItem menuClearSelectedSave = null!;
    private ToolStripSeparator menuChipSaveSeparator = null!;
    private ToolStripMenuItem menuExportChipSave = null!;
    private ToolStripMenuItem menuImportChipSave = null!;
    private ToolStripMenuItem menuClearChipSave = null!;
    private Button buttonRefreshPorts = null!;
    private Button buttonScan = null!;
    private Button buttonQueueAdd = null!;
    private Button buttonQueueRemove = null!;
    private Button buttonQueueClear = null!;
    private Button buttonQueueBurn = null!;
    private CheckBox checkBoxWaitcntPatch = null!;
    private Label labelBurnProfileModeCaption = null!;
    private Label labelGbaMenuCaption = null!;
    private Label labelGbcMenuLanguageCaption = null!;
    private ComboBox comboBoxGbcMenuLanguage = null!;
    private bool gbcMenuLanguageSelectionInitialized;
    private ComboBox comboBoxBurnProfileMode = null!;
    private ComboBox comboBoxGbaMenu = null!;
    private GbcMenuLanguageMode selectedGbcMenuLanguage;
    private string? selectedGbcFontName;
    private Label labelNorIdValue = null!;
    private Label labelChipNameValue = null!;
    private Label labelCapacityValue = null!;
    private Label labelSectorInfoValue = null!;
    private Label labelRomCountValue = null!;
    private Label labelMenuConfigValue = null!;
    private Label labelMenuItemCountValue = null!;
    private Label labelMenuTitleValue = null!;
    private Label labelScanMethodValue = null!;
    private Label labelMenuBackgroundPreviewPlaceholder = null!;
    private TableLayoutPanel rootLayout = null!;
    private TableLayoutPanel tableLayoutMenuBackgroundPreview = null!;
    private Panel panelMenuBackgroundPreviewHost = null!;
    private SplitContainer splitContainerContent = null!;
    private DataGridView dataGridViewRoms = null!;
    private TextBox textBoxLog = null!;
    private PictureBox pictureBoxMenuBackgroundPreview = null!;
    private bool currentPreviewUsesGbcListMode;
    private bool currentPreviewUsesGbaFirstScreenMode;
    private RadioButton radioButtonBeggarCartModeGba = null!;
    private RadioButton radioButtonBeggarCartModeGb = null!;
    private ContextMenuStrip romContextMenu = null!;
    private ToolStripMenuItem menuItemDeleteMapping = null!;
    private ToolStripMenuItem menuItemReplaceRom = null!;
    private ToolStripSeparator menuItemContextSeparator = null!;
    private ToolStripMenuItem menuItemExportRom = null!;
    private ToolStripMenuItem menuItemExportSave = null!;
    private ToolStripMenuItem menuItemImportSave = null!;
    private ToolStripMenuItem menuItemClearSave = null!;
    private ToolStripSeparator menuItemChipSaveSeparator = null!;
    private ToolStripMenuItem menuItemExportChipSaveContext = null!;
    private ToolStripMenuItem menuItemImportChipSaveContext = null!;
    private ToolStripMenuItem menuItemClearChipSaveContext = null!;

    private WaterCartScanResult? currentScanResult;
    private string lastScannedPortName = string.Empty;
    private BeggarCartMode lastSelectedBeggarCartMode = BeggarCartMode.Gba;
    private int workspaceDragSourceIndex = -1;
    private Point workspaceDragStartPoint = Point.Empty;
    private int workspaceTitleEditRowIndex = -1;
    private string workspaceTitleEditOriginalValue = string.Empty;

    public Form1() : this(true, null) { }

    // Construct the real UI without touching ports or user recovery files in regression checks.
    internal Form1(bool initializeSession, Func<GbaMultiMenuBuilder.Fc.IFcCardBackend>? fcBackend)
    {
        fcBackendOverride = fcBackend;
        availableLanguages = ClientLanguageService.LoadAvailableLanguages();
        language = ClientLanguageService.Load();
        LocalizedText.Initialize(language);
        selectedGbcMenuLanguage = ParseGbcMenuLanguageMode(ClientAppConfigService.LoadGbcMenuLanguage());
        selectedGbcFontName = GbcFontService.NormalizeBuiltInFontName(ClientAppConfigService.LoadGbcFontName())
            ?? GbcFontService.GetDefaultBuiltInFontFileName();

        InitializeComponent();
        if(initializeSession)DeleteLegacyWorkspaceRecoveryFile();
        BuildInterface();
        checkBoxWaitcntPatch.Checked = ClientAppConfigService.LoadWaitcntPatchEnabled();
        RefreshGbaMenuOptions(ClientAppConfigService.LoadGbaMenuSelection());
        RefreshGbcFontSelectorVisibility();
        ApplyLanguage(language, refreshScanResult: false);
        if(initializeSession)RefreshPorts();
        FormClosing += (_,e) => { if (fcWorkspace?.IsBusy == true) { fcWorkspace.Cancel(); e.Cancel=true; } };
    }

    private static void DeleteLegacyWorkspaceRecoveryFile()
    {
        try
        {
            string rootDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ChisFlashWaterClient");
            string filePath = Path.Combine(rootDirectory, "workspace_recovery.json");
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
        }
    }

    private string T(string section, string key, string fallback) =>
        language.Get(section, key, fallback);

    private string Tf(string section, string key, string fallback, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(section, key, fallback), args);

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

    private static string FormatBinarySize(double value, string unit)
    {
        if (Math.Abs(value - Math.Round(value)) < 0.0005)
        {
            return $"{Math.Round(value):0} {unit}";
        }

        return $"{value:0.###} {unit}";
    }

    private void RefreshGbaMenuOptions(string? preferredSelection = null)
    {
        if (comboBoxGbaMenu is null)
        {
            return;
        }

        GbaMenuSelection selected = string.IsNullOrWhiteSpace(preferredSelection)
            ? GetSelectedGbaMenuSelection()
            : ParseGbaMenuSelection(preferredSelection);
        List<GbaMenuSelectionOption> options =
        [
            new(T("ui", "gba_menu_auto", "Auto"), GbaMenuSelection.Auto),
            new(T("ui", "gba_menu_legacy", "Original Menu"), GbaMenuSelection.Legacy),
            new(T("ui", "gba_menu_universal", "New NS Menu"), GbaMenuSelection.Universal),
            new(T("ui", "gba_menu_simplified", "Simplified NS Menu"), GbaMenuSelection.Simplified),
        ];

        gbaMenuSelectionInitialized = false;
        comboBoxGbaMenu.DataSource = null;
        comboBoxGbaMenu.DisplayMember = "Name";
        comboBoxGbaMenu.ValueMember = "Selection";
        comboBoxGbaMenu.DataSource = options;
        comboBoxGbaMenu.SelectedItem = options.First(option => option.Selection == selected);
        gbaMenuSelectionInitialized = true;
    }

    private GbaMenuSelection GetSelectedGbaMenuSelection() =>
        comboBoxGbaMenu?.SelectedItem is GbaMenuSelectionOption option
            ? option.Selection
            : ParseGbaMenuSelection(ClientAppConfigService.LoadGbaMenuSelection());

    private static GbaMenuSelection ParseGbaMenuSelection(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "legacy" or "old" => GbaMenuSelection.Legacy,
            "universal" or "new" => GbaMenuSelection.Universal,
            "simplified" or "simple" => GbaMenuSelection.Simplified,
            _ => GbaMenuSelection.Auto,
        };
    }

    private static string GetGbaMenuSelectionConfigValue(GbaMenuSelection selection) => selection switch
    {
        GbaMenuSelection.Legacy => "legacy",
        GbaMenuSelection.Universal => "universal",
        GbaMenuSelection.Simplified => "simplified",
        _ => "auto",
    };

    private bool HasGbaMenuSelectionChanges(
        WaterCartScanResult? scanResult = null,
        GbaMenuSelection? selectionOverride = null)
    {
        WaterCartScanResult? effectiveScan = scanResult ?? currentScanResult;
        if (effectiveScan?.IsBeggarMaxGbcMode == true ||
            (selectionOverride is null && IsGbcWorkspaceMode()))
        {
            return false;
        }

        GbaMenuSelection selection = selectionOverride ?? GetSelectedGbaMenuSelection();
        if (selection == GbaMenuSelection.Auto)
        {
            return false;
        }

        bool currentUsesUniversalMenu = effectiveScan?.IsUniversalGbaMenuDetected == true;
        bool currentUsesSimplifiedMenu = effectiveScan?.IsSimplifiedNsGbaMenuDetected == true;
        return selection switch
        {
            GbaMenuSelection.Universal => !currentUsesUniversalMenu || currentUsesSimplifiedMenu,
            GbaMenuSelection.Simplified => !currentUsesUniversalMenu || !currentUsesSimplifiedMenu,
            _ => currentUsesUniversalMenu,
        };
    }

    private byte[] LoadSelectedGbaMenuRom(bool usesWlLayout, out string sourcePath)
        => LoadSelectedGbaMenuRom(usesWlLayout, GetSelectedGbaMenuSelection(), out sourcePath);

    private byte[] LoadSelectedGbaMenuRom(
        bool usesWlLayout,
        GbaMenuSelection selection,
        out string sourcePath)
    {
        if (selection == GbaMenuSelection.Universal)
        {
            return GbaMenuRomService.LoadUniversalMenuRom(out sourcePath);
        }

        if (selection == GbaMenuSelection.Simplified)
        {
            return GbaMenuRomService.LoadSimplifiedNsMenuRom(out sourcePath);
        }

        return usesWlLayout
            ? WaterMagicUpgradeBuilder.LoadMagicMenuRom(out sourcePath)
            : WaterLegacyMenuRepairBuilder.LoadLegacyMenuRom(out sourcePath);
    }

    private string? GetSelectedGbcFontName() => selectedGbcFontName;

    private string? ResolveSelectedGbcFontPath() =>
        IsSelectedGbcMenuChinese() ? GbcFontService.FindFontPath(selectedGbcFontName) : null;

    // Current MAX/MBCX menus do not consume the encoded background section.
    private static string? ResolveSelectedGbcBackgroundPath() => null;

    private GbcMenuLanguageMode GetSelectedGbcMenuLanguageMode() => selectedGbcMenuLanguage;

    private void RefreshGbcMenuLanguageOptions()
    {
        gbcMenuLanguageSelectionInitialized = false;
        comboBoxGbcMenuLanguage.Items.Clear();
        comboBoxGbcMenuLanguage.Items.AddRange([
            new GbcMenuLanguageOption(T("ui", "gbc_menu_language_zh", "Chinese"), GbcMenuLanguageMode.Chinese),
            new GbcMenuLanguageOption(T("ui", "gbc_menu_language_en", "English"), GbcMenuLanguageMode.English),
        ]);
        comboBoxGbcMenuLanguage.SelectedIndex = IsSelectedGbcMenuChinese() ? 0 : 1;
        gbcMenuLanguageSelectionInitialized = true;
    }

    private void GbcMenuLanguageChanged()
    {
        if (!gbcMenuLanguageSelectionInitialized ||
            comboBoxGbcMenuLanguage.SelectedItem is not GbcMenuLanguageOption option)
        {
            return;
        }

        selectedGbcMenuLanguage = option.Mode;
        ClientAppConfigService.SaveGbcMenuLanguage(GetGbcMenuLanguageConfigValue(option.Mode));
        int selectedIndex = dataGridViewRoms.SelectedRows.Count > 0 ? dataGridViewRoms.SelectedRows[0].Index : -1;
        RefreshWorkspaceGrid(selectedIndex);
        UpdateMenuBackgroundPreview(currentScanResult);
    }

    private bool IsSelectedGbcMenuChinese() =>
        GetSelectedGbcMenuLanguageMode() == GbcMenuLanguageMode.Chinese;

    private static GbcMenuLanguageMode ParseGbcMenuLanguageMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return GbcMenuLanguageMode.Chinese;
        }

        string normalized = value.Trim();
        return normalized.Equals("en", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("en_US", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("english", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("menu_en", StringComparison.OrdinalIgnoreCase)
                ? GbcMenuLanguageMode.English
                : GbcMenuLanguageMode.Chinese;
    }

    private static string GetGbcMenuLanguageConfigValue(GbcMenuLanguageMode mode) =>
        mode == GbcMenuLanguageMode.English ? "en" : "zh";

    private void RefreshGbcFontSelectorVisibility()
    {
        bool imageProfile = IsGbcRawImageMode() || IsGbcProfileUnselected();
        bool visible = IsGbcWorkspaceMode() && !IsFcWorkspaceActive && !IsMbc5EightMiBMode() && !imageProfile;
        if (gbcMenuOptions is not null) gbcMenuOptions.Visible = visible;
        labelGbcMenuLanguageCaption.Visible = visible;
        comboBoxGbcMenuLanguage.Visible = visible;
        comboBoxGbcMenuLanguage.Enabled = visible && !isBusy && !isAnalyzingQueueImports;
        if (checkBoxWaitcntPatch is not null)
        {
            checkBoxWaitcntPatch.Visible = !visible && !IsFcWorkspaceActive && !IsMbc5EightMiBMode() && !imageProfile;
            checkBoxWaitcntPatch.Enabled = !visible && !IsFcWorkspaceActive && !IsMbc5EightMiBMode() && !imageProfile && !isBusy && !isAnalyzingQueueImports;
        }

        bool gbaVisible = !visible && !IsFcWorkspaceActive && !IsMbc5EightMiBMode() && !imageProfile;
        if (gbaMenuOptions is not null) gbaMenuOptions.Visible = gbaVisible;
        if (labelGbaMenuCaption is not null)
        {
            labelGbaMenuCaption.Visible = gbaVisible;
            labelGbaMenuCaption.Enabled = gbaVisible && !isBusy;
        }

        if (comboBoxGbaMenu is not null)
        {
            comboBoxGbaMenu.Visible = gbaVisible;
            comboBoxGbaMenu.Enabled = gbaVisible && !isBusy && !isAnalyzingQueueImports;
        }
        UpdateAdaptiveLayout();
    }

    private string FormatSectorSummary(NorGeometryInfo geometry)
    {
        if (geometry.SectorCount <= 0 || geometry.SectorSizeBytes <= 0)
        {
            return T("ui", "not_read", "Not Read");
        }

        return Tf(
            "ui",
            "sector_summary_value",
            "Sectors {0} x {1} | BuffWr {2}",
            geometry.SectorCount,
            FormatKiBSize(geometry.SectorSizeBytes),
            FormatBufferWriteSize(geometry.BufferWriteBytes));
    }

    private sealed record WorkspaceUsageSummary(int Used, int Total, int Pending, int Free, int LargestContiguousFree);

    private string BuildCapacityValueText(WaterCartScanResult scanResult)
    {
        if (scanResult.Geometry.DeviceSizeBytes <= 0)
        {
            return T("ui", "not_read", "Not Read");
        }

        string romCapacityText = FormatMiBSize(scanResult.Geometry.DeviceSizeBytes);
        WorkspaceUsageSummary? usage = GetWorkspaceUsageSummary();
        if (usage is not null)
        {
            string freeText = scanResult.IsBeggarMaxGbcMode
                ? FormatWorkspaceUsageBytes(usage.Free)
                : FormatWorkspaceBucketSizeMb(usage.Free);
            string contiguousText = scanResult.IsBeggarMaxGbcMode
                ? FormatWorkspaceUsageBytes(usage.LargestContiguousFree)
                : FormatWorkspaceBucketSizeMb(usage.LargestContiguousFree);

            romCapacityText = Tf(
                "ui",
                "capacity_with_workspace_free",
                "{0} | Free {1} | Max Contiguous {2}",
                romCapacityText,
                freeText,
                contiguousText);
        }

        if (!scanResult.MenuConfig.IsDetected)
        {
            return romCapacityText;
        }

        int saveAreaStart = scanResult.MenuConfig.Status.SaveAreaAddress;
        if (saveAreaStart <= 0 || saveAreaStart >= WaterCartScanner.BucketSizeBytes)
        {
            return romCapacityText;
        }

        int saveAreaLimit = scanResult.WlMapping.IsDetected && scanResult.MenuConfig.Status.BatteryPresent
            ? Chm1Layout.MappingOffset : WaterCartScanner.BucketSizeBytes;
        int saveAreaBytes = saveAreaLimit - saveAreaStart;
        int slotSizeBytes = 0x20000; // show save capacity as 128 KiB slots
        int slotCount = Math.Max(0, saveAreaBytes / slotSizeBytes);
        if (slotCount <= 0)
        {
            return romCapacityText;
        }

        return Tf(
            "ui",
            "capacity_with_save_area",
            "{0} | Save Area {1} ({2} x 128 KiB)",
            romCapacityText,
            FormatMiBSize(saveAreaBytes),
            slotCount);
    }

    private string FormatDetectedSaveTypeDisplay(DetectedSaveTypeInfo info)
    {
        string baseText = info.Kind switch
        {
            DetectedSaveTypeKind.None => T("value", "save_type_none", "NONE"),
            DetectedSaveTypeKind.Sram => "SRAM",
            DetectedSaveTypeKind.Eeprom => "EEPROM",
            DetectedSaveTypeKind.Flash => "FLASH",
            DetectedSaveTypeKind.Flash512 => "FLASH512",
            DetectedSaveTypeKind.Flash1M => "FLASH1M",
            _ => T("value", "save_type_unknown", "Unknown"),
        };

        return string.IsNullOrWhiteSpace(info.Identifier)
            ? baseText
            : $"{baseText} ({info.Identifier})";
    }

    private static bool HasAppliedSramPatch(string appliedPatchIdentifier) =>
        !string.IsNullOrWhiteSpace(appliedPatchIdentifier);

    private static int? ResolveDetectedSaveSizeBytes(PendingRomImportItem item)
    {
        int? identifierSizeBytes = ResolveDetectedSaveSizeBytes(item.AppliedSramPatchIdentifier) ??
                                   ResolveDetectedSaveSizeBytes(item.SramPatchTargetIdentifier) ??
                                   ResolveDetectedSaveSizeBytes(item.SaveTypeInfo.Identifier);
        if (identifierSizeBytes.HasValue)
        {
            return identifierSizeBytes;
        }

        return item.SaveTypeInfo.Kind switch
        {
            DetectedSaveTypeKind.None => 0,
            DetectedSaveTypeKind.Sram => 0x8000,
            DetectedSaveTypeKind.Eeprom => 0x2000,
            DetectedSaveTypeKind.Flash => 0x10000,
            DetectedSaveTypeKind.Flash512 => 0x10000,
            DetectedSaveTypeKind.Flash1M => 0x20000,
            _ => null,
        };
    }

    private static int? ResolveDetectedSaveSizeBytes(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return null;
        }

        if (identifier.StartsWith("FLASH1M", StringComparison.OrdinalIgnoreCase))
        {
            return 0x20000;
        }

        if (identifier.StartsWith("FLASH512", StringComparison.OrdinalIgnoreCase) ||
            identifier.StartsWith("FLASH", StringComparison.OrdinalIgnoreCase))
        {
            return 0x10000;
        }

        if (identifier.StartsWith("SRAM", StringComparison.OrdinalIgnoreCase))
        {
            return 0x8000;
        }

        if (identifier.StartsWith("EEPROM", StringComparison.OrdinalIgnoreCase))
        {
            return 0x2000;
        }

        return null;
    }

    private static byte? ResolveLegacyMenuSaveTypeOverride(PendingRomImportItem item)
    {
        int? saveSizeBytes = ResolveDetectedSaveSizeBytes(item);
        if (!saveSizeBytes.HasValue)
        {
            return null;
        }

        if (saveSizeBytes.Value <= 0)
        {
            return 0;
        }

        if (saveSizeBytes.Value <= 0x8000)
        {
            return 1; // SRAM_256K
        }

        if (saveSizeBytes.Value <= 0x10000)
        {
            return 2; // SRAM_512K
        }

        return 3; // SRAM_1M
    }

    private static bool IsBatterylessPreparedRom(
        string patchTargetIdentifier,
        string appliedPatchIdentifier,
        BatterylessPatchInspection batterylessInspection) =>
        batterylessInspection.AlreadyPatched &&
        string.IsNullOrWhiteSpace(appliedPatchIdentifier) &&
        !string.IsNullOrWhiteSpace(patchTargetIdentifier);

    private static bool HasPreparedSavePatch(
        string patchTargetIdentifier,
        string appliedPatchIdentifier,
        BatterylessPatchInspection batterylessInspection) =>
        HasAppliedSramPatch(appliedPatchIdentifier) ||
        IsBatterylessPreparedRom(patchTargetIdentifier, appliedPatchIdentifier, batterylessInspection);

    private bool ShouldApplySramPatch(
        DetectedSaveTypeInfo info,
        string patchTargetIdentifier,
        string appliedPatchIdentifier,
        BatterylessPatchInspection batterylessInspection)
    {
        if (HasPreparedSavePatch(patchTargetIdentifier, appliedPatchIdentifier, batterylessInspection))
        {
            return false;
        }

        if (info.Kind is DetectedSaveTypeKind.None or DetectedSaveTypeKind.Sram or DetectedSaveTypeKind.Unknown)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(patchTargetIdentifier);
    }

    private string FormatPendingPatchDisplay(PendingRomImportItem item)
    {
        if(item.FcRom is not null)return item.FcRom.CanPack?$"NES · Mapper {item.FcRom.Mapper}，请选择NES模式":"NES · "+item.FcRom.Error;
        if(item.MdRom is not null)return GbaMultiMenuBuilder.Md.MdRomImportService.ImportStatus(item.MdRom,item.MdReadError);
        BurnProfileMode burnProfileMode = GetSelectedBurnProfileMode();
        BatterylessPatchInspection inspection = GetBatterylessInspection(item);
        bool alreadyPrepared = HasPreparedSavePatch(
            item.SramPatchTargetIdentifier,
            item.AppliedSramPatchIdentifier,
            inspection);
        bool appliesSramPatch = item.ApplySramPatch &&
            !alreadyPrepared &&
            !string.IsNullOrWhiteSpace(item.SramPatchTargetIdentifier);
        bool appliesBatterylessPatch = CanApplyBatterylessPatch(item, burnProfileMode);

        if (HasAppliedSramPatch(item.AppliedSramPatchIdentifier) && appliesBatterylessPatch)
        {
            return Tf(
                "value",
                "patch_auto_apply_sram_batteryless",
                "Auto -> SRAM ({0}) + Batteryless",
                item.AppliedSramPatchIdentifier);
        }

        if (HasAppliedSramPatch(item.AppliedSramPatchIdentifier))
        {
            return Tf("value", "patch_already_applied", "Already patched ({0})", item.AppliedSramPatchIdentifier);
        }

        if (IsBatterylessPreparedRom(item.SramPatchTargetIdentifier, item.AppliedSramPatchIdentifier, inspection))
        {
            return Tf(
                "value",
                "patch_already_batteryless_ready",
                "Already batteryless-ready ({0})",
                item.SramPatchTargetIdentifier);
        }

        if (appliesSramPatch && appliesBatterylessPatch)
        {
            return Tf(
                "value",
                "patch_auto_apply_sram_batteryless",
                "Auto -> SRAM ({0}) + Batteryless",
                item.SramPatchTargetIdentifier);
        }

        if (appliesSramPatch)
        {
            return Tf("value", "patch_auto_apply", "Auto -> SRAM ({0})", item.SramPatchTargetIdentifier);
        }

        if (appliesBatterylessPatch)
        {
            return T("value", "patch_auto_apply_batteryless", "Auto -> Batteryless");
        }

        if (RequiresBatterylessPatchButUnavailable(item, burnProfileMode))
        {
            return T("value", "patch_batteryless_unavailable", "Batteryless Unavailable");
        }

        if (item.SaveTypeInfo.Kind is DetectedSaveTypeKind.None or DetectedSaveTypeKind.Sram)
        {
            return T("value", "patch_not_needed", "Not Needed");
        }

        if (string.IsNullOrWhiteSpace(item.SramPatchTargetIdentifier))
        {
            return T("value", "patch_unavailable", "No Patch");
        }

        return T("value", "patch_not_needed", "Not Needed");
    }

    private string FormatWorkspacePatchOrMbcDisplay(WorkspaceListRow row)
    {
        if(row.PendingItem?.FcRom is not null)return FormatPendingPatchDisplay(row.PendingItem);
        if(row.PendingItem?.MdRom is not null)return FormatPendingPatchDisplay(row.PendingItem);
        if (row.IsLoadingPlaceholder)
        {
            return string.Empty;
        }

        if (IsGbcWorkspaceMode())
        {
            if (row.PendingItem?.GameBoyCartridgeType is byte pendingCartridgeType)
            {
                return GameBoyRomHeaderService.GetMbcTypeDisplay(pendingCartridgeType);
            }

            if (row.CartEntry is not null)
            {
                return GameBoyRomHeaderService.GetMbcTypeDisplay(row.CartEntry.MbcType);
            }

            return string.Empty;
        }

        return row.PendingItem is not null
            ? FormatPendingPatchDisplay(row.PendingItem)
            : string.Empty;
    }

    private bool IsGbcDedicatedSaveAllocationSelectable(WorkspaceListRow row)
    {
        if (!IsGbcWorkspaceMode() || row.IsLoadingPlaceholder)
        {
            return false;
        }

        if (row.PendingItem is not null)
        {
            return row.PendingItem.GameBoyHasBattery &&
                row.PendingItem.GameBoyEffectiveRamBankCount > 0;
        }

        return row.CartEntry is not null &&
            !row.CartEntry.IsMenu &&
            row.CartEntry.HasBattery &&
            row.CartEntry.RamBankCount > 0;
    }

    private bool ResolveWorkspaceRowUsesDedicatedSave(WorkspaceListRow row)
    {
        if (row.PendingItem is not null)
        {
            return row.PendingItem.GameBoyHasBattery &&
                row.PendingItem.GameBoyEffectiveRamBankCount > 0 &&
                (row.GbcDedicatedSaveAllocationOverride ??
                 row.PendingItem.GbcDedicatedSaveAllocationOverride ??
                 true);
        }

        return row.CartEntry is not null &&
            !row.CartEntry.IsMenu &&
            row.CartEntry.HasBattery &&
            row.CartEntry.RamBankCount > 0 &&
            (row.GbcDedicatedSaveAllocationOverride ?? !row.CartEntry.UsesSharedSave);
    }

    private void SetWorkspaceRowDedicatedSaveAllocationOverride(WorkspaceListRow row, bool usesDedicatedSave)
    {
        row.GbcDedicatedSaveAllocationOverride = usesDedicatedSave;
        if (row.PendingItem is not null)
        {
            row.PendingItem.GbcDedicatedSaveAllocationOverride = usesDedicatedSave;
        }
    }

    private static bool UsesWlMode(WaterCartScanResult? scanResult) =>
        scanResult?.WlMapping.IsDetected == true;

    private static bool UsesWlManagementMode(WaterCartScanResult? scanResult, BurnProfileMode burnProfileMode) =>
        UsesWlTargetPlanningMode(burnProfileMode) && UsesWlMode(scanResult);

    private static bool UsesWlTargetPlanningMode(BurnProfileMode burnProfileMode) =>
        UsesWaterLinkTargetMode(burnProfileMode) || UsesChm1Mode(burnProfileMode);

    private static bool UsesChm1Mode(BurnProfileMode mode) =>
        mode is BurnProfileMode.Chm1 or BurnProfileMode.Chm1NoBattery;

    private bool UsesEffectiveWlWorkspaceMode(WaterCartScanResult? scanResult)
    {
        return UsesWlManagementMode(scanResult, GetSelectedBurnProfileMode());
    }

    private static bool UsesWaterLinkTargetMode(BurnProfileMode burnProfileMode) =>
        burnProfileMode is BurnProfileMode.WaterLinkSram or BurnProfileMode.WaterLinkMultiNor;

    private static bool IsBeggarMaxMode(BurnProfileMode burnProfileMode) =>
        burnProfileMode == BurnProfileMode.BeggarMaxSram;

    private bool UsesSelectedWaterLinkTargetMode() =>
        UsesWaterLinkTargetMode(GetSelectedBurnProfileMode());

    private bool UsesSelectedMagicTargetMode() => UsesWlTargetPlanningMode(GetSelectedBurnProfileMode());

    private static ushort GetEffectiveBufferWriteBytes(NorGeometryInfo geometry) =>
        (ushort)Math.Clamp(geometry.BufferWriteBytes > 0 ? geometry.BufferWriteBytes : 256, 1, ushort.MaxValue);

    private int GetPendingAllocatedSizeBytes(PendingRomImportItem item)
    {
        return GetPendingAllocatedSizeBytes(item, GetSelectedBurnProfileMode());
    }

    private int GetPendingAllocatedSizeBytes(PendingRomImportItem item, BurnProfileMode burnProfileMode)
    {
        if(item.FcRom is not null)return checked((int)item.FileSizeBytes);
        if(item.MdRom is not null)return checked((int)item.MdRom.RomBytes);
        int plannedLengthBytes = item.IsLoadingPlaceholder
            ? checked((int)Math.Min(int.MaxValue, Math.Max(1L, item.FileSizeBytes)))
            : GetEstimatedPreparedRomLengthBytes(item, item.FileSizeBytes, burnProfileMode);
        return EstimateWorkspaceAllocatedSizeBytes(item, plannedLengthBytes, burnProfileMode);
    }

    private int EstimateWorkspaceAllocatedSizeBytes(PendingRomImportItem item, int plannedLengthBytes, BurnProfileMode burnProfileMode)
    {
        if (IsBeggarMaxMode(burnProfileMode) || currentScanResult?.IsBeggarMaxGbcMode == true)
        {
            int minimumBytes = Math.Max(GameBoyRomHeaderService.RomBankSize * 2, plannedLengthBytes);
            int unit = GameBoyRomHeaderService.RomBankSize;
            return checked(((minimumBytes + unit - 1) / unit) * unit);
        }

        bool usesWlMode = UsesEffectiveWlWorkspaceMode(currentScanResult);
        return WaterRomImportPlanner.EstimateAllocatedSizeBytes(usesWlMode, plannedLengthBytes);
    }

    private static int GetCartEntryAllocatedSizeBytes(WaterCartEntry entry)
    {
        if (entry.AllocatedSizeBytesOverride > 0)
        {
            return entry.AllocatedSizeBytesOverride;
        }

        if (entry.WlRecord is not null && entry.WlRecord.LogicalBucketCount > 0)
        {
            return entry.WlRecord.LogicalBucketCount * WaterCartScanner.BucketSizeBytes;
        }

        if (entry.MenuItem is not null && entry.MenuItem.RomSizeBlocks > 0)
        {
            return entry.MenuItem.RomSizeBlocks * WaterRomImportPlanner.BlockSizeBytes;
        }

        return Math.Max(1, entry.LogicalBucketCount) * WaterCartScanner.BucketSizeBytes;
    }

    private int GetWorkspaceRowAllocatedSizeBytes(WorkspaceListRow row)
    {
        if (row.PendingItem is not null && row.CartEntry is not null)
        {
            return GetCartEntryAllocatedSizeBytes(row.CartEntry);
        }

        if (row.PendingItem is not null)
        {
            return GetPendingAllocatedSizeBytes(row.PendingItem);
        }

        return row.CartEntry is not null
            ? GetCartEntryAllocatedSizeBytes(row.CartEntry)
            : 0;
    }

    private int GetPendingWorkspaceItemCount() =>
        workspaceRows.Count(row => row.PendingItem is not null);

    private bool TryResolveCurrentChipSaveEntry(out WaterCartEntry? entry, out int saveSizeBytes)
    {
        entry = null;
        saveSizeBytes = 0;

        if (GetSelectedProgrammerKind() != ProgrammerKind.BeggarLegacy ||
            currentScanResult is null ||
            currentScanResult.IsChm1 ||
            !currentScanResult.MenuConfig.IsDetected ||
            currentScanResult.IsBeggarMaxGbcMode)
        {
            return false;
        }

        MenuFlashStatusInfo status = currentScanResult.MenuConfig.Status;
        if (!status.BatteryPresent ||
            status.LastBootMenuIndex == 0xFFFF ||
            status.LastBootMenuIndex >= currentScanResult.MenuConfig.ItemCount)
        {
            return false;
        }

        entry = currentScanResult.Entries
            .Where(item => !item.IsMenu && item.MenuItem?.Index == status.LastBootMenuIndex)
            .OrderBy(item => item.SortKey)
            .FirstOrDefault();
        if (entry is null)
        {
            return false;
        }

        if (!HasEntryAccessibleSave(entry, currentScanResult))
        {
            return false;
        }

        saveSizeBytes = status.SramBankType == 1 ? 0x20000 : 0x10000;
        return true;
    }

    private bool IsCurrentChipSaveEntry(WaterCartEntry? entry)
    {
        if (entry is null || !TryResolveCurrentChipSaveEntry(out WaterCartEntry? chipEntry, out _))
        {
            return false;
        }

        if (entry.MenuItem?.Index is int menuIndex && chipEntry?.MenuItem?.Index is int chipMenuIndex)
        {
            return menuIndex == chipMenuIndex;
        }

        return entry.EntrySlot >= 0 &&
               chipEntry is not null &&
               entry.EntrySlot == chipEntry.EntrySlot;
    }

    private int GetPendingBucketCount(PendingRomImportItem item)
    {
        return Math.Max(1, GetPendingAllocatedSizeBytes(item) / WaterCartScanner.BucketSizeBytes);
    }

    private static string FormatWorkspaceBucketSizeMb(int bucketCount)
    {
        int megabytes = Math.Max(0, bucketCount) * 4;
        return $"{megabytes} MB";
    }

    private static string FormatWorkspaceUsageBytes(long bytes)
    {
        long normalizedBytes = Math.Max(0L, bytes);
        return FormatMiBSize(normalizedBytes);
    }

    private (int Used, int Total, int Free)? GetWorkspaceSaveUsageSummary()
    {
        if (GetSelectedBurnProfileMode() == BurnProfileMode.Chm1 && currentScanResult?.MenuConfig.IsDetected == true)
        {
            int used = workspaceRows.Where(row => row.CartEntry?.SaveType is > 0).Select(row => row.CartEntry!.SaveIndex).Distinct().Count()
                + workspaceRows.Count(row => row.CartEntry is null && ResolveWorkspaceRowUsesLegacySaveSlot(row));
            return (used, Chm1Layout.SaveSlotCount, Math.Max(0, Chm1Layout.SaveSlotCount - used));
        }
        if (currentScanResult is null ||
            UsesEffectiveWlWorkspaceMode(currentScanResult) ||
            currentScanResult.IsBeggarMaxGbcMode ||
            !IsBatteryPresentForSelectedMode(GetSelectedBurnProfileMode()) ||
            !currentScanResult.MenuConfig.IsDetected)
        {
            return null;
        }

        int saveAreaStart = currentScanResult.MenuConfig.Status.SaveAreaAddress;
        if (saveAreaStart <= 0 || saveAreaStart >= WaterCartScanner.BucketSizeBytes)
        {
            return null;
        }

        const int SaveSlotSizeBytes = 0x20000; // count save area in 128 KiB slots
        int saveAreaLimit = currentScanResult.WlMapping.IsDetected ? Chm1Layout.MappingOffset : WaterCartScanner.BucketSizeBytes;
        int totalSlots = (saveAreaLimit - saveAreaStart) / SaveSlotSizeBytes;
        if (totalSlots <= 0)
        {
            return null;
        }

        int usedSlots = workspaceRows.Count(row => ResolveWorkspaceRowUsesLegacySaveSlot(row));
        int freeSlots = Math.Max(0, totalSlots - usedSlots);
        return (usedSlots, totalSlots, freeSlots);
    }

    private bool ResolveWorkspaceRowUsesLegacySaveSlot(WorkspaceListRow row)
    {
        if (row.PendingItem is not null)
        {
            byte? overrideSaveType = ResolveLegacyMenuSaveTypeOverride(row.PendingItem);
            byte saveType = overrideSaveType ?? 2;
            return saveType > 0;
        }

        return row.CartEntry?.SaveType is > 0;
    }

    private string FormatWorkspaceSaveSlotText(WaterCartEntry entry)
    {
        BurnProfileMode mode = GetSelectedBurnProfileMode();
        if (UsesWaterLinkTargetMode(mode) &&
            entry.EntrySlot >= 0)
        {
            return (entry.EntrySlot + 1).ToString("000", CultureInfo.CurrentCulture);
        }

        if (currentScanResult?.IsBeggarMaxGbcMode == true)
        {
            return string.Empty;
        }

        if (UsesBatterylessPatch(mode))
        {
            return T("value", "save_slot_batteryless", "Batteryless");
        }

        if (entry.SaveType is > 0 && entry.SaveIndex.HasValue)
        {
            return (entry.SaveIndex.Value + 1).ToString("000", CultureInfo.CurrentCulture);
        }

        return string.Empty;
    }

    private int? ResolveWorkspaceEntrySlot(WaterCartEntry entry)
    {
        return entry.EntrySlot >= 0 ? entry.EntrySlot : null;
    }

    private bool? ResolveWorkspaceSavePresence(WaterCartEntry entry)
    {
        if (currentScanResult is null)
        {
            return null;
        }

        int? entrySlot = ResolveWorkspaceEntrySlot(entry);
        if (!entrySlot.HasValue)
        {
            return null;
        }

        BurnProfileMode mode = GetSelectedBurnProfileMode();
        Dictionary<int, bool> source = UsesWaterLinkTargetMode(mode)
            ? currentScanResult.WaterLinkSaveDataByEntrySlot
            : UsesBatterylessPatch(mode)
                ? currentScanResult.BatterylessSaveDataByEntrySlot
                : currentScanResult.LegacySaveDataByEntrySlot;

        return source.TryGetValue(entrySlot.Value, out bool hasData)
            ? hasData
            : null;
    }

    private string FormatWorkspaceSaveStatusText(WaterCartEntry entry)
    {
        string? detailedText = ResolveWorkspaceSaveStatusDetailText(entry);
        if (!string.IsNullOrWhiteSpace(detailedText))
        {
            return detailedText;
        }

        bool? hasData = ResolveWorkspaceSavePresence(entry);
        if (!hasData.HasValue)
        {
            return string.Empty;
        }

        return T("value", hasData.Value ? "save_status_has_data" : "save_status_empty_slot", hasData.Value ? "Has Save" : "Empty Slot");
    }

    private string? ResolveWorkspaceSaveStatusDetailText(WaterCartEntry entry)
    {
        if (currentScanResult is null)
        {
            return null;
        }

        int? entrySlot = ResolveWorkspaceEntrySlot(entry);
        if (!entrySlot.HasValue)
        {
            return null;
        }

        BurnProfileMode mode = GetSelectedBurnProfileMode();
        Dictionary<int, string> source = UsesWaterLinkTargetMode(mode)
            ? currentScanResult.WaterLinkSaveStatusTextByEntrySlot
            : UsesBatterylessPatch(mode)
                ? currentScanResult.BatterylessSaveStatusTextByEntrySlot
                : currentScanResult.LegacySaveStatusTextByEntrySlot;

        return source.TryGetValue(entrySlot.Value, out string? text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;
    }

    private BurnProfileMode GetDetectedBurnProfileMode(
        WaterCartScanResult? scanResult,
        ProgrammerKind? programmerKind,
        BurnProfileMode fallbackMode)
    {
        if (programmerKind != ProgrammerKind.WaterLink &&
            (GetSelectedBeggarCartMode() == BeggarCartMode.Gb || scanResult?.IsBeggarMaxGbcMode == true))
            return GetGbcProfileModes(scanResult?.Geometry.DeviceSizeBytes > 0
                ? scanResult.Geometry.DeviceSizeBytes : GetGbcProfileCapacity()).Contains(fallbackMode)
                ? fallbackMode : BurnProfileMode.GbcUnselected;
        if (programmerKind == ProgrammerKind.BeggarLegacy && scanResult?.IsChm1 == true &&
            (scanResult.WlMapping.IsDetected || !scanResult.MenuConfig.IsDetected || UsesChm1Mode(fallbackMode)))
            return scanResult.MenuConfig.IsDetected
                ? scanResult.MenuConfig.Status.BatteryPresent ? BurnProfileMode.Chm1 : BurnProfileMode.Chm1NoBattery
                : fallbackMode == BurnProfileMode.Chm1NoBattery ? BurnProfileMode.Chm1NoBattery : BurnProfileMode.Chm1;
        if (programmerKind == ProgrammerKind.WaterLink)
        {
            if (scanResult is not null)
            {
                return scanResult.Geometry.IsWaterLinkMultiNor
                    ? BurnProfileMode.WaterLinkMultiNor
                    : BurnProfileMode.WaterLinkSram;
            }

            return UsesWaterLinkTargetMode(fallbackMode)
                ? fallbackMode
                : BurnProfileMode.WaterLinkSram;
        }

        if (programmerKind == ProgrammerKind.BeggarLegacy &&
            scanResult?.IsBeggarMaxGbcMode == true)
        {
            return BurnProfileMode.BeggarMaxSram;
        }

        if (programmerKind == ProgrammerKind.BeggarLegacy &&
            UsesWaterLinkTargetMode(fallbackMode))
        {
            return fallbackMode;
        }

        if (programmerKind == ProgrammerKind.BeggarLegacy &&
            scanResult?.MenuConfig.IsDetected == true)
        {
            return scanResult.MenuConfig.Status.BatteryPresent
                ? BurnProfileMode.BeggarFram
                : BurnProfileMode.BeggarNoBattery;
        }

        return fallbackMode;
    }

    private static BatterylessPatchInspection GetBatterylessInspection(PendingRomImportItem item) =>
        item.PreparedBatterylessInspection ??
        new(
            item.BatterylessAlreadyPatched,
            item.BatterylessHasPayloadSpace,
            item.BatterylessCanExpand);

    private BurnProfileMode GetSelectedBurnProfileMode()
    {
        if (comboBoxBurnProfileMode?.SelectedItem is BurnProfileModeOption option)
        {
            return option.Mode;
        }

        return GetDefaultBurnProfileMode(GetSelectedProgrammerKind());
    }

    private BeggarCartMode GetSelectedBeggarCartMode() =>
        radioButtonBeggarCartModeGb?.Checked == true
            ? BeggarCartMode.Gb
            : BeggarCartMode.Gba;

    private string GetSelectedBeggarCartModeDisplayName() =>
        GetSelectedBeggarCartMode() == BeggarCartMode.Gb
            ? T("ui", "cart_mode_gb", "GB")
            : T("ui", "cart_mode_gba", "GBA");

    private bool IsGbcWorkspaceMode() =>
        IsMbc5EightMiBMode() ||
        GetSelectedBeggarCartMode() == BeggarCartMode.Gb ||
        currentScanResult?.IsBeggarMaxGbcMode == true ||
        GetSelectedBurnProfileMode() == BurnProfileMode.BeggarMaxSram;

    private string GetRomOpenFileDialogFilter() =>
        IsGbcWorkspaceMode()
            ? T("dialog", "queue_add_rom_filter_gbc", "GB/GBC ROM (*.gb;*.gbc;*.bin)|*.gb;*.gbc;*.bin|All files (*.*)|*.*")
            : T("dialog", "queue_add_rom_filter", "GBA ROM (*.gba;*.agb;*.bin)|*.gba;*.agb;*.bin|All files (*.*)|*.*");

    private BurnProfileMode GetDefaultBurnProfileMode(ProgrammerKind? programmerKind)
    {
        if (programmerKind == ProgrammerKind.BeggarLegacy)
        {
            return GetSelectedBeggarCartMode() == BeggarCartMode.Gb
                ? BurnProfileMode.GbcUnselected
                : BurnProfileMode.BeggarFram;
        }

        return BurnProfileMode.WaterLinkSram;
    }

    private void OpenProgrammerProtocol(WaterDeviceProtocol protocol, ProgrammerKind? programmerKind = null)
    {
        protocol.Open();
    }

    private void UpdateProgrammerModeSelectorUi(bool allowInteraction)
    {
        ProgrammerKind? programmerKind = GetSelectedProgrammerKind();
        bool allowBeggarMode = allowInteraction && programmerKind == ProgrammerKind.BeggarLegacy;

        if (programmerKind == ProgrammerKind.WaterLink)
        {
            bool selectionInitialized = beggarCartModeSelectionInitialized;
            beggarCartModeSelectionInitialized = false;
            try
            {
                radioButtonBeggarCartModeGba.Checked = true;
                radioButtonBeggarCartModeGb.Checked = false;
                lastSelectedBeggarCartMode = BeggarCartMode.Gba;
            }
            finally
            {
                beggarCartModeSelectionInitialized = selectionInitialized;
            }
        }

        labelBeggarCartModeCaption.Visible = true;
        panelProgrammerModeHost.Visible = true;
        panelBeggarCartMode.Visible = true;

        labelBeggarCartModeCaption.Text = T("ui", "cart_mode", "Mode");

        labelBeggarCartModeCaption.Enabled = allowBeggarMode;
        panelBeggarCartMode.Enabled = allowBeggarMode;
    }

    private bool TryGetSelectedKnownProgrammerKind(out ProgrammerKind programmerKind, out string reason)
    {
        programmerKind = ProgrammerKind.Unknown;

        string? portName = GetSelectedPortName();
        if (string.IsNullOrWhiteSpace(portName))
        {
            reason = T("log", "select_port_first", "Please select a compatible programmer port first.");
            return false;
        }

        ProgrammerKind? selectedKind = GetSelectedProgrammerKind();
        if (selectedKind is not ProgrammerKind.WaterLink and not ProgrammerKind.BeggarLegacy)
        {
            reason = Tf(
                "error",
                "unknown_programmer_selected",
                "The selected serial port {0} is not a recognized WaterLink or Beggar device.",
                portName);
            return false;
        }

        programmerKind = selectedKind.Value;
        reason = string.Empty;
        return true;
    }

    private ProgrammerKind GetRequiredSelectedProgrammerKind()
    {
        if (!TryGetSelectedKnownProgrammerKind(out ProgrammerKind programmerKind, out string reason))
        {
            throw new InvalidOperationException(reason);
        }

        return programmerKind;
    }

    private bool IsBatteryPresentForSelectedMode(BurnProfileMode mode) =>
        mode is BurnProfileMode.BeggarFram or BurnProfileMode.BeggarWithBattery or BurnProfileMode.Chm1;

    private static bool UsesBatterylessPatch(BurnProfileMode mode) =>
        mode is BurnProfileMode.BeggarNoBattery or BurnProfileMode.Chm1NoBattery;

    private string GetBurnProfileModeDisplayName(BurnProfileMode mode) =>
        mode switch
        {
            BurnProfileMode.BeggarNoBattery => T("value", "burn_profile_beggar_no_battery", "Beggar No Battery (SRAM + Batteryless)"),
            BurnProfileMode.BeggarFram => T("value", "burn_profile_beggar_fram", "Beggar Battery/FRAM (SRAM)"),
            BurnProfileMode.BeggarWithBattery => T("value", "burn_profile_beggar_fram", "Beggar Battery/FRAM (SRAM)"),
            BurnProfileMode.BeggarMaxSram => T("gbc_profile", "free", "Free multicart 32M · GB/GBC"),
            BurnProfileMode.GbcUnselected => T("gbc_profile", GetGbcProfileCapacity() is null ? "pending" : "choose", GetGbcProfileCapacity() is null ? "Capacity not read · choose cartridge type" : "Choose cartridge type"),
            BurnProfileMode.Mbc5EightMiB => "MBC5 8M · 3in1 / 4in1",
            BurnProfileMode.MbcxRtcEightMiB => "MBCX RTC 8M",
            BurnProfileMode.MbcxRtcThirtyTwoMiB => "MBCX RTC 32M",
            BurnProfileMode.GbcSixteenInOne => "MAX 32M · 16in1",
            BurnProfileMode.FcMbcx => T("fc", "mode", "MIX | NES + MD + SNES (MBCX)"),
            BurnProfileMode.Chm1 => T("value", "burn_profile_chm1", "01G/02G Fragment Management (Battery / FRAM)"),
            BurnProfileMode.Chm1NoBattery => T("value", "burn_profile_chm1_no_battery", "01G/02G Fragment Management (Batteryless)"),
            BurnProfileMode.WaterLinkMultiNor => T("value", "burn_profile_waterlink_multi_nor", "WL multi-nor"),
            _ => T("value", "burn_profile_waterlink_sram", "WaterLink (SRAM)"),
        };

    private bool CanApplyBatterylessPatch(PendingRomImportItem item, BurnProfileMode mode)
    {
        BatterylessPatchInspection inspection = GetBatterylessInspection(item);
        return UsesBatterylessPatch(mode) &&
               item.SaveTypeInfo.Kind != DetectedSaveTypeKind.None &&
               !inspection.AlreadyPatched &&
               inspection.CanInstallPayload;
    }

    private bool RequiresBatterylessPatchButUnavailable(PendingRomImportItem item, BurnProfileMode mode)
    {
        BatterylessPatchInspection inspection = GetBatterylessInspection(item);
        return UsesBatterylessPatch(mode) &&
               item.SaveTypeInfo.Kind != DetectedSaveTypeKind.None &&
               !inspection.AlreadyPatched &&
               !inspection.CanInstallPayload;
    }

    private int GetEstimatedPreparedRomLengthBytes(PendingRomImportItem item, long fileSizeBytes, BurnProfileMode burnProfileMode)
    {
        int estimatedBytes = checked((int)Math.Min(int.MaxValue, Math.Max(1L, fileSizeBytes)));
        if (item.GameBoyEffectiveRomSizeBytes > 0 &&
            (IsBeggarMaxMode(burnProfileMode) ||
             currentScanResult?.IsBeggarMaxGbcMode == true ||
             item.GameBoyCartridgeType.HasValue))
        {
            estimatedBytes = Math.Max(
                estimatedBytes,
                checked((int)Math.Min(int.MaxValue, item.GameBoyEffectiveRomSizeBytes)));
        }

        BatterylessPatchInspection inspection = GetBatterylessInspection(item);

        if (!UsesBatterylessPatch(burnProfileMode))
        {
            return estimatedBytes;
        }

        if (inspection.AlreadyPatched || CanApplyBatterylessPatch(item, burnProfileMode))
        {
            estimatedBytes = Math.Max(
                estimatedBytes,
                BatterylessPatchService.EstimatePreparedRomLength(
                    checked((int)Math.Min(int.MaxValue, Math.Max(1L, fileSizeBytes))),
                    inspection));
        }

        return estimatedBytes;
    }

    private static CartridgeDefinition? ResolveBatterylessCartridge(WaterCartScanResult scanResult)
    {
        string chipName = scanResult.ChipName ?? string.Empty;

        if (chipName.Contains("S29GL01G", StringComparison.OrdinalIgnoreCase) ||
            chipName.Contains("MT28EW01G", StringComparison.OrdinalIgnoreCase))
        {
            return CartridgeDefinition.All.FirstOrDefault(definition => definition.Id == CartridgeDefinition.WaterlineCompatibleId);
        }

        if (chipName.Contains("S70GL02G", StringComparison.OrdinalIgnoreCase))
        {
            return CartridgeDefinition.All.FirstOrDefault(definition => definition.Id == 5);
        }

        if (chipName.Contains("S29GL512", StringComparison.OrdinalIgnoreCase))
        {
            return CartridgeDefinition.All.FirstOrDefault(definition => definition.Id == 6);
        }

        if (chipName.Contains("MSP55LV100", StringComparison.OrdinalIgnoreCase))
        {
            return CartridgeDefinition.All.FirstOrDefault(definition => definition.Id == 1);
        }

        if (chipName.Contains("6600M0U0BE", StringComparison.OrdinalIgnoreCase))
        {
            return CartridgeDefinition.All.FirstOrDefault(definition => definition.Id == 2);
        }

        if (chipName.Contains("MSP54LV100", StringComparison.OrdinalIgnoreCase))
        {
            return CartridgeDefinition.All.FirstOrDefault(definition => definition.Id == 3);
        }

        if (scanResult.Geometry.DeviceSizeBytes == 128 * 1024 * 1024 &&
            scanResult.Geometry.SectorSizeBytes == 0x20000)
        {
            return CartridgeDefinition.All.FirstOrDefault(definition => definition.Id == CartridgeDefinition.WaterlineCompatibleId);
        }

        if (scanResult.Geometry.DeviceSizeBytes == 64 * 1024 * 1024 &&
            scanResult.Geometry.SectorSizeBytes == 0x20000)
        {
            return CartridgeDefinition.All.FirstOrDefault(definition => definition.Id == 6);
        }

        return null;
    }

    private bool HasBurnProfileMetadataChanges()
    {
        if (currentScanResult?.MenuConfig.IsDetected != true)
        {
            return false;
        }

        bool targetBatteryPresent = IsBatteryPresentForSelectedMode(GetSelectedBurnProfileMode());
        return currentScanResult.MenuConfig.Status.BatteryPresent != targetBatteryPresent;
    }

    private string FormatAllocatedSizeText(long bytes)
    {
        return bytes > 0
            ? FormatMiBSize(bytes)
            : string.Empty;
    }

    private string GetWorkspaceRowDisplayTitle(WorkspaceListRow row)
    {
        bool isBeggarMaxGbcMode = currentScanResult?.IsBeggarMaxGbcMode == true;
        return GetWorkspaceRowDisplayTitle(
            row,
            isBeggarMaxGbcMode,
            isBeggarMaxGbcMode ? GetSelectedGbcMenuLanguageMode() : GbcMenuLanguageMode.Chinese);
    }

    private static string GetWorkspaceRowDisplayTitle(
        WorkspaceListRow row,
        bool isBeggarMaxGbcMode,
        GbcMenuLanguageMode gbcMenuLanguage)
    {
        if (!string.IsNullOrWhiteSpace(row.DisplayTitleOverride))
        {
            return row.DisplayTitleOverride;
        }

        if (row.PendingItem is not null)
        {
            return row.PendingItem.DisplayTitle;
        }

        if (isBeggarMaxGbcMode &&
            gbcMenuLanguage == GbcMenuLanguageMode.English &&
            !string.IsNullOrWhiteSpace(row.CartEntry?.HeaderTitle))
        {
            return row.CartEntry.HeaderTitle;
        }

        return row.CartEntry?.Title ?? string.Empty;
    }

    private WorkspaceUsageSummary? GetWorkspaceUsageSummary()
    {
        if (currentScanResult is null ||
            currentScanResult.Geometry.DeviceSizeBytes <= 0)
        {
            return null;
        }

        if (currentScanResult.IsBeggarMaxGbcMode)
        {
            int totalBytes = GetBeggarMaxAvailableRomBytes(currentScanResult);
            int usedBytes = workspaceRows
                .Where(row => row.CartEntry is not null)
                .Sum(row => GetWorkspaceRowAllocatedSizeBytes(row));
            int pendingBytes = workspaceRows
                .Where(row => row.PendingItem is not null && row.CartEntry is null)
                .Sum(row => GetPendingAllocatedSizeBytes(row.PendingItem!));
            int freeBytes = Math.Max(0, totalBytes - usedBytes - pendingBytes);
            int contiguousFreeBytes = GetBeggarMaxLargestContiguousFreeBytes();
            return new WorkspaceUsageSummary(usedBytes, totalBytes, pendingBytes, freeBytes, contiguousFreeBytes);
        }

        int totalBuckets = Math.Max(0, (currentScanResult.Geometry.DeviceSizeBytes / WaterCartScanner.BucketSizeBytes) - 1);
        if (totalBuckets <= 0)
        {
            return null;
        }

        IEnumerable<int> usedBuckets = workspaceRows
            .Where(row => row.CartEntry is not null)
            .SelectMany(row => row.CartEntry!.PhysicalBuckets);

        int used = usedBuckets
            .Distinct()
            .Count(bucket => bucket > 0 && bucket <= totalBuckets);
        int pending = workspaceRows
            .Where(row => row.PendingItem is not null && row.CartEntry is null)
            .Sum(row => GetPendingBucketCount(row.PendingItem!));
        int free = Math.Max(0, totalBuckets - used - pending);
        int largestContiguous = GetLegacyLargestContiguousFreeBuckets();
        return new WorkspaceUsageSummary(used, totalBuckets, pending, free, largestContiguous);
    }

    private static int GetBeggarMaxAvailableRomBytes(WaterCartScanResult scanResult)
    {
        int accessibleBytes = CompilationBuilder.GetGbcAccessibleFlashSize(ResolveBeggarMaxCartridge(scanResult));
        return Math.Max(0, accessibleBytes - BeggarMaxFixedMapReservedBytes);
    }

    private int GetBeggarMaxLargestContiguousFreeBytes()
    {
        if (currentScanResult is null || !currentScanResult.IsBeggarMaxGbcMode)
        {
            return 0;
        }

        List<BeggarMaxWorkspaceRowPlan> rowPlans = TryAnalyzeBeggarMaxWorkspaceRowsForUsage();
        if (rowPlans.Count == 0)
        {
            return GetBeggarMaxAvailableRomBytes(currentScanResult);
        }

        List<(int Start, int End)> freeRanges = BuildBeggarMaxFreeRomRanges(rowPlans);
        int largestBanks = freeRanges.Count == 0
            ? 0
            : freeRanges.Max(range => Math.Max(0, range.End - range.Start + 1));
        return largestBanks * GameBoyRomHeaderService.RomBankSize;
    }

    private List<BeggarMaxWorkspaceRowPlan> TryAnalyzeBeggarMaxWorkspaceRowsForUsage()
    {
        try
        {
            return AnalyzeBeggarMaxWorkspaceRows(
                workspaceRows,
                GetSelectedBurnProfileMode(),
                GetSelectedGbcMenuLanguageMode());
        }
        catch
        {
            return [];
        }
    }

    private int GetLegacyLargestContiguousFreeBuckets()
    {
        if (currentScanResult is null || currentScanResult.IsBeggarMaxGbcMode)
        {
            return 0;
        }

        if (UsesEffectiveWlWorkspaceMode(currentScanResult))
        {
            return 0;
        }

        if (!currentScanResult.MenuConfig.IsDetected)
        {
            return Math.Max(0, (currentScanResult.Geometry.DeviceSizeBytes / WaterCartScanner.BucketSizeBytes) - 1);
        }

        try
        {
            WaterCartScanResult planningScan = BuildWorkspacePlanningScanResult(
                currentScanResult,
                workspaceRows,
                GetSelectedBurnProfileMode());

            return GetLegacyLargestContiguousFreeBuckets(planningScan);
        }
        catch
        {
            return 0;
        }
    }

    private static int GetLegacyLargestContiguousFreeBuckets(WaterCartScanResult scanResult)
    {
        NorGeometryInfo geometry = scanResult.Geometry;
        if (geometry.DeviceSizeBytes <= 0 || geometry.SectorSizeBytes <= 0)
        {
            return 0;
        }

        int sectorCount = geometry.DeviceSizeBytes / geometry.SectorSizeBytes;
        if (sectorCount <= 0)
        {
            return 0;
        }

        bool[] usedSectors = new bool[sectorCount];
        MenuFlashStatusInfo status = scanResult.MenuConfig.Status;
        int statusSector = status.StatusAddress / geometry.SectorSizeBytes;
        int saveAreaStartSector = statusSector + 1;
        int firstGameBucketSector = WaterCartScanner.BucketSizeBytes / geometry.SectorSizeBytes;

        MarkUsedSectorRange(usedSectors, 0, saveAreaStartSector);

        foreach (MenuItemConfigInfo item in scanResult.MenuConfig.Items)
        {
            if (item.SaveType != 0)
            {
                int saveSector = saveAreaStartSector + item.SaveIndex;
                MarkUsedSectorRange(usedSectors, saveSector, 1);
            }

            int itemStartSector = BytesToSector(item.RomOffsetBlocks * WaterRomImportPlanner.BlockSizeBytes, geometry.SectorSizeBytes);
            int itemSectorCount = BytesToSector(item.RomSizeBlocks * WaterRomImportPlanner.BlockSizeBytes, geometry.SectorSizeBytes);
            MarkUsedSectorRange(usedSectors, itemStartSector, itemSectorCount);
        }

        foreach (DetectedNorGameInfo game in scanResult.Games.Where(game => !game.IsMenu))
        {
            bool presentInMenu = scanResult.MenuConfig.Items.Any(item => item.StartBucket == game.StartBucket);
            if (presentInMenu)
            {
                continue;
            }

            int startSector = BytesToSector(game.StartBucket * WaterCartScanner.BucketSizeBytes, geometry.SectorSizeBytes);
            int gameSectorCount = BytesToSector((game.EndBucket - game.StartBucket + 1) * WaterCartScanner.BucketSizeBytes, geometry.SectorSizeBytes);
            MarkUsedSectorRange(usedSectors, startSector, gameSectorCount);
        }

        int largestRunSectors = 0;
        int currentRunSectors = 0;
        for (int sector = Math.Max(0, firstGameBucketSector); sector < usedSectors.Length; sector++)
        {
            if (!usedSectors[sector])
            {
                currentRunSectors++;
                if (currentRunSectors > largestRunSectors)
                {
                    largestRunSectors = currentRunSectors;
                }
            }
            else
            {
                currentRunSectors = 0;
            }
        }

        int bucketSectors = Math.Max(1, WaterCartScanner.BucketSizeBytes / geometry.SectorSizeBytes);
        return largestRunSectors / bucketSectors;
    }

    private static void MarkUsedSectorRange(bool[] usedSectors, int startSector, int sectorCount)
    {
        if (sectorCount <= 0 || usedSectors.Length == 0)
        {
            return;
        }

        int start = Math.Max(0, startSector);
        int endExclusive = Math.Min(usedSectors.Length, startSector + sectorCount);
        for (int sector = start; sector < endExclusive; sector++)
        {
            usedSectors[sector] = true;
        }
    }

    private static int BytesToSector(int byteCount, int sectorSizeBytes) =>
        sectorSizeBytes <= 0
            ? 0
            : (byteCount + sectorSizeBytes - 1) / sectorSizeBytes;
}
