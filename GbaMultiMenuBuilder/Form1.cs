using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using GbaMultiMenuBuilder.Models;
using GbaMultiMenuBuilder.Services;

namespace GbaMultiMenuBuilder;

public partial class Form1 : DpiAwareForm
{
    private const string SequenceColumnName = "sequence";
    private const string DefaultOutputFileName = "LK_MULTIMENU_OUTPUT.gba";
    private const string AppVersion = "v0.91.2 MIX";
    private const string AppAuthor = "chisflash@mori";
    private const long BytesPerMiB = 1024L * 1024L;
    private static readonly Size DefaultWindowSize = new(1184, 761);
    private static readonly Size MinimumWindowSize = new(1200, 760);
    private const int GamesLoadingProgressUnitsPerFile = 100;
    private const int GamesLoadingProgressPreviewUnits = 80;
    private const int GamesLoadingCompletionDelayMs = 200;
    private const int GamesLoadingUiBatchSize = 24;
    private const int GamesLoadingUiTimeBudgetMs = 8;
    private const int MaxConcurrentMetadataWorkerCount = 8;
    private const int SettingsRowHeight = 36;
    private const int SettingsInputVerticalMargin = 5;
    private const int SettingsButtonColumnWidth = 128;
    private const int ActionButtonWidth = 112;
    private readonly BindingList<GameEntryModel> _games = [];
    private readonly CompilationBuilder _builder = new();
    private readonly ToolTip _buildSettingsToolTip = new();
    private readonly ToolTip _gameGridHeaderToolTip = new();
    private readonly HashSet<Button> _fixedWidthButtons = [];
    private readonly System.Windows.Forms.Timer _gameGridHeaderToolTipTimer = new();
    private readonly System.Windows.Forms.Timer _gamesLoadingProgressTimer = new();
    private readonly ConcurrentQueue<GameLoadProgress> _pendingGameLoadProgress = new();
    private UiLanguage _language;
    private UiLanguage? _pendingLanguageRefresh;
    private readonly IReadOnlyList<UiLanguage> _availableLanguages;
    private List<MinRomSizeOption> _minRomSizeOptions;
    private List<FlashCartModeOption> _flashCartModeOptions;
    private List<GbaMenuTypeOption> _gbaMenuTypeOptions;
    private CompilationFamily _currentCompilationFamily;

    private CancellationTokenSource? _buildCancellation;
    private bool _languageSelectionInitialized;
    private bool _flashCartModeSelectionInitialized;
    private bool _suppressFlashCartSync;
    private bool _settingsPersistenceEnabled;
    private bool _gameListPersistenceEnabled;
    private bool _suppressGameGridColumnWidthPersistence;
    private bool _gamesLoading;
    private bool _startupGameListRestorePending;
    private bool _flashCartBatteryPresent;
    private bool _buildBlockedByCapacity;
    private string? _capacitySummaryErrorMessage;
    private DataGridViewHitTestType _hoveredGameGridHitType = DataGridViewHitTestType.None;
    private int _hoveredGameGridColumnIndex = -1;
    private int _hoveredGameGridRowIndex = -1;
    private int _gamesLoadingProgressMaximum = GamesLoadingProgressUnitsPerFile;
    private int _gamesLoadingProgressValue;
    private int _gamesLoadingProgressTarget;
    private int _gamesLoadingCurrentOrdinal;
    private int _gamesLoadingTotalCount;
    private string _gamesLoadingCurrentFileName = string.Empty;
    private string _gamesLoadingStatusMessage = string.Empty;
    private IReadOnlyDictionary<Guid, GameEntryModel> _loadingPlaceholderMap = new Dictionary<Guid, GameEntryModel>();
    private Guid _activeLoadingPlaceholderId = Guid.Empty;

    private TextBox _textBoxBaseMenuPath = null!;
    private TextBox _textBoxOutputPath = null!;
    private TextBox _textBoxBackgroundPath = null!;
    private ComboBox _comboBoxGbcFont = null!;
    private Label _labelBackgroundOrFont = null!;
    private Label _labelGbcFont = null!;
    private Button _buttonBrowseBackgroundOrFont = null!;
    private Button _buttonOpenGbcFontFolder = null!;
    private ComboBox _comboBoxCartridgeType = null!;
    private ComboBox _comboBoxMinRomSize = null!;
    private ComboBox _comboBoxFlashCartMode = null!;
    private ComboBox _comboBoxGbaMenuType = null!;
    private Label _labelGbaMenuType = null!;
    private CheckBox _checkBoxMinimalMenuPackage = null!;
    private ComboBox _comboBoxLanguage = null!;
    private CheckBox _checkBoxSplitOutput = null!;
    private DataGridView _dataGridViewGames = null!;
    private TextBox _textBoxLog = null!;
    private ProgressBar _progressBarBuild = null!;
    private Label _labelStatus = null!;
    private Label _labelCapacitySummary = null!;
    private Label _labelSaveCapacitySummary = null!;
    private Label _labelGamesLoading = null!;
    private Button? _buttonPreviewMenu;
    private Button _buttonBuild = null!;
    private Button _buttonCancel = null!;
    private GroupBox _groupBoxBuildSettings = null!;
    private GroupBox _groupBoxGames = null!;
    private FlowLayoutPanel _panelGameActions = null!;
    private Panel _panelGamesLoading = null!;

    public Form1()
    {
        _availableLanguages = LanguageService.LoadAvailableLanguages();
        _language = LanguageService.Load();
        _currentCompilationFamily = CompilationFamily.Gba;
        _minRomSizeOptions = BuildMinRomSizeOptions(_currentCompilationFamily);
        _flashCartModeOptions = [];
        _gbaMenuTypeOptions = [];
        _buildSettingsToolTip.ShowAlways = true;
        _buildSettingsToolTip.AutoPopDelay = 10000;
        _buildSettingsToolTip.InitialDelay = 1000;
        _buildSettingsToolTip.ReshowDelay = 1000;
        _gameGridHeaderToolTip.ShowAlways = true;
        _gameGridHeaderToolTip.AutoPopDelay = 10000;
        _gameGridHeaderToolTip.InitialDelay = 1000;
        _gameGridHeaderToolTip.ReshowDelay = 1000;
        _gameGridHeaderToolTipTimer.Interval = 1000;
        _gameGridHeaderToolTipTimer.Tick += gameGridHeaderToolTipTimer_Tick;
        _gamesLoadingProgressTimer.Interval = 50;
        _gamesLoadingProgressTimer.Tick += gamesLoadingProgressTimer_Tick;
        InitializeComponent();
        InitializeRuntimeState();
    }

    private void InitializeRuntimeState()
    {
        _currentCompilationFamily = AppConfigService.LoadCompilationFamily() ?? CompilationFamily.Gba;
        _minRomSizeOptions = BuildMinRomSizeOptions(_currentCompilationFamily);
        var settings = AppConfigService.LoadSettings(_currentCompilationFamily);
        _suppressFlashCartSync = true;
        BindUiDataSources();
        _settingsPersistenceEnabled = false;
        _gameListPersistenceEnabled = false;
        RefreshCartridgeTypeOptions(settings.CartridgeId);

        RefreshMinRomSizeOptions(settings.MinRomSize);

        var initialMenuType = settings.GbaMenuType ?? settings.FlashCartMode switch
        {
            FlashCartMode.NsMenu => GbaMenuType.NsMenu,
            FlashCartMode.SimplifiedNsMenu => GbaMenuType.SimplifiedNsMenu,
            _ => GbaMenuType.Classic,
        };
        RestoreGbaMenuTypeSelection(initialMenuType);
        var initialFlashCartMode = settings.FlashCartMode is null or FlashCartMode.NsMenu or FlashCartMode.SimplifiedNsMenu
            ? GetDefaultFlashCartMode(_currentCompilationFamily)
            : settings.FlashCartMode.Value;
        _flashCartBatteryPresent = ResolveBatteryPresent(initialFlashCartMode, settings.BatteryPresent ?? false);
        RefreshFlashCartModeOptions(initialFlashCartMode);
        _flashCartModeSelectionInitialized = true;
        if (_comboBoxFlashCartMode.SelectedItem is FlashCartModeOption flashCartMode)
        {
            ApplyFlashCartPreset(flashCartMode.Mode);
        }
        _checkBoxSplitOutput.Checked = settings.SplitOutput ?? false;
        _checkBoxMinimalMenuPackage.Checked = settings.MinimalMenuPackage ?? false;
        _comboBoxLanguage.SelectedItem = _availableLanguages
            .FirstOrDefault(language => string.Equals(language.Code, _language.Code, StringComparison.OrdinalIgnoreCase));
        _languageSelectionInitialized = true;

        _textBoxBaseMenuPath.Text = ResolveEffectiveBaseMenuPath(
            settings.BaseMenuPath,
            _currentCompilationFamily,
            GetSelectedFlashCartMode(),
            GetSelectedGbaMenuType());
        _textBoxOutputPath.Text = BuildManagedOutputPath(settings.OutputPath);
        _textBoxBackgroundPath.Text = string.IsNullOrWhiteSpace(settings.BackgroundPath)
            ? string.Empty
            : settings.BackgroundPath.Trim();
        RefreshGbcFontOptions(settings.GbcFontName);
        RestoreGameGridColumnWidths(settings.GameGridColumnWidths);
        RestoreConfiguredWindowGeometry(settings);
        _startupGameListRestorePending = true;
        RefreshGameGridColumnVisibility();
        RefreshBackgroundOrFontUi();

        _labelStatus.Text = T("status", "ready", "Ready");
        _suppressFlashCartSync = false;
        _settingsPersistenceEnabled = true;
        _gameListPersistenceEnabled = true;
        SyncManagedOutputPathWithCurrentContext(force: true);
        SaveCurrentAppSettings();
        RefreshBuildCapacitySummary();
    }

    private string T(string section, string key, string fallback) =>
        _language.Get(section, key, fallback);

    private string Tf(string section, string key, string fallback, params object[] args) =>
        string.Format(T(section, key, fallback), args);

    private List<MinRomSizeOption> BuildMinRomSizeOptions(CompilationFamily family) =>
        family == CompilationFamily.Gbc
            ?
            [
                new("32 KiB", 32 * 1024),
            ]
            :
            [
                new(T("ui", "min_rom_size_4m", "4 MiB"), 4 * 1024 * 1024),
            ];

    private static FlashCartMode GetDefaultFlashCartMode(CompilationFamily family) =>
        family == CompilationFamily.Gbc
            ? FlashCartMode.GbcMaxMbcx512K
            : FlashCartMode.WaterlineVirtualIron;

    private static CompilationFamily GetCompilationFamilyForMode(FlashCartMode mode) =>
        mode == FlashCartMode.GbcMaxMbcx512K
            ? CompilationFamily.Gbc
            : CompilationFamily.Gba;

    private bool SupportsPatchWorkflow() => _currentCompilationFamily == CompilationFamily.Gba;

    private static int GetDefaultCartridgeIdForFamily(CompilationFamily family) =>
        family == CompilationFamily.Gbc
            ? CartridgeDefinition.Gbc32MiBId
            : CartridgeDefinition.WaterlineCompatibleId;

    private int GetGameListCount() =>
        _games.Count(game => !game.IsLoadingPlaceholder);

    private static string GetDefaultOutputFileName(CompilationFamily family, int gameCount) =>
        family == CompilationFamily.Gbc
            ? $"MoriMenu-{Math.Max(0, gameCount)}in1.gbc"
            : DefaultOutputFileName;

    private string GetDefaultOutputFileName() =>
        GetDefaultOutputFileName(_currentCompilationFamily, GetGameListCount());

    private static bool IsKnownDefaultOutputFileName(string fileName)
    {
        if (string.Equals(fileName, DefaultOutputFileName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!fileName.StartsWith("MoriMenu-", StringComparison.OrdinalIgnoreCase) ||
            !fileName.EndsWith("in1.gbc", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var numberPart = fileName["MoriMenu-".Length..^"in1.gbc".Length];
        return !string.IsNullOrWhiteSpace(numberPart) && numberPart.All(char.IsDigit);
    }

    private static bool ShouldUseDefaultOutputPath(string? outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return true;
        }

        return IsKnownDefaultOutputFileName(Path.GetFileName(outputPath.Trim()));
    }

    private IReadOnlyList<CartridgeDefinition> GetUiSelectableCartridges(CompilationFamily? family = null) =>
        CartridgeDefinition.GetUiSelectable(family ?? _currentCompilationFamily);

    private void RefreshCartridgeTypeOptions(int? preferredCartridgeId = null)
    {
        if (_comboBoxCartridgeType is null)
        {
            return;
        }

        var options = GetUiSelectableCartridges().ToList();
        var selectedCartridgeId = preferredCartridgeId ??
            (_comboBoxCartridgeType.SelectedItem is CartridgeDefinition cartridge ? cartridge.Id : GetDefaultCartridgeIdForFamily(_currentCompilationFamily));

        _comboBoxCartridgeType.DisplayMember = nameof(CartridgeDefinition.UiDisplayName);
        _comboBoxCartridgeType.ValueMember = nameof(CartridgeDefinition.Id);
        _comboBoxCartridgeType.DataSource = null;
        _comboBoxCartridgeType.DataSource = options;

        if (options.Count == 0)
        {
            return;
        }

        _comboBoxCartridgeType.SelectedItem = options.FirstOrDefault(cartridge => cartridge.Id == selectedCartridgeId)
            ?? options.FirstOrDefault(cartridge => cartridge.Id == GetDefaultCartridgeIdForFamily(_currentCompilationFamily))
            ?? options[0];
    }

    private List<FlashCartModeOption> BuildFlashCartModeOptions(bool includeWaterline, bool includeCustom) =>
[
    new FlashCartModeOption(T("ui", "flashcart_no_battery", "GBA | No Battery"), FlashCartMode.NoBattery),
    new FlashCartModeOption(T("ui", "flashcart_with_battery", "GBA | With Battery / FRAM"), FlashCartMode.WithBattery),
    new FlashCartModeOption(T("ui", "flashcart_chm1", "GBA | 01G/02G Fragment Management (Battery / FRAM)"), FlashCartMode.Chm1),
    new FlashCartModeOption(T("ui", "flashcart_chm1_no_battery", "GBA | 01G/02G Fragment Management (Batteryless)"), FlashCartMode.Chm1NoBattery),
    ..(includeWaterline
        ? [new FlashCartModeOption(T("ui", "flashcart_waterline_virtual_iron", "GBA | Waterline (Virtual Iron)"), FlashCartMode.WaterlineVirtualIron)]
        : Array.Empty<FlashCartModeOption>()),
    new FlashCartModeOption(T("ui", "flashcart_gbc_max_mbcx_512k", "GBC | MAX/MBCX (512K)"), FlashCartMode.GbcMaxMbcx512K),
    new FlashCartModeOption(T("fc", "mode", "MIX | NES + MD + SNES (MBCX)"), FlashCartMode.FcMbcx),
    ..(includeCustom
        ? [new FlashCartModeOption(T("ui", "flashcart_custom", "Custom"), FlashCartMode.Custom)]
        : Array.Empty<FlashCartModeOption>()),
];

    private List<GbaMenuTypeOption> BuildGbaMenuTypeOptions() =>
    [
        new(T("ui", "gba_menu_classic", "Classic Menu"), GbaMenuType.Classic),
        new(T("ui", "gba_menu_ns", "NS Menu"), GbaMenuType.NsMenu),
        new(T("ui", "gba_menu_ns_simplified", "Simplified NS Menu"), GbaMenuType.SimplifiedNsMenu),
    ];

    private GbaMenuType GetSelectedGbaMenuType() =>
        _comboBoxGbaMenuType.SelectedItem is GbaMenuTypeOption option
            ? option.Type
            : GbaMenuType.Classic;

    private void RestoreGbaMenuTypeSelection(GbaMenuType menuType)
    {
        if (_comboBoxGbaMenuType is null) return;
        _comboBoxGbaMenuType.SelectedItem = _gbaMenuTypeOptions.FirstOrDefault(option => option.Type == menuType)
            ?? _gbaMenuTypeOptions.FirstOrDefault();
    }

private void BindUiDataSources()
{
    _dataGridViewGames.DataSource = _games;
    _gbaMenuTypeOptions = BuildGbaMenuTypeOptions();
    _comboBoxGbaMenuType.DataSource = _gbaMenuTypeOptions.ToList();
    RefreshCartridgeTypeOptions();
    _comboBoxMinRomSize.DataSource = _minRomSizeOptions.ToList();
    _comboBoxLanguage.DataSource = _availableLanguages.ToList();
}

    private void RefreshMinRomSizeOptions(int? preferredValue = null)
    {
        _minRomSizeOptions = BuildMinRomSizeOptions(_currentCompilationFamily);
        if (_comboBoxMinRomSize is null)
        {
            return;
        }

        var targetValue = preferredValue ?? _minRomSizeOptions.First().Value;
        _comboBoxMinRomSize.DataSource = null;
        _comboBoxMinRomSize.DataSource = _minRomSizeOptions.ToList();
        var minRomIndex = _minRomSizeOptions.FindIndex(option => option.Value == targetValue);
        _comboBoxMinRomSize.SelectedIndex = minRomIndex >= 0 ? minRomIndex : 0;
    }

    private static bool IsFourMiBRomSize(int romSize) => romSize == 4 * 1024 * 1024;

    private static bool ResolveBatteryPresent(FlashCartMode mode, bool fallbackForCustom) =>
        mode switch
        {
            FlashCartMode.WithBattery => true,
            FlashCartMode.Chm1 => true,
            FlashCartMode.Ferroelectric => true,
            FlashCartMode.NoBattery or FlashCartMode.Chm1NoBattery => false,
            FlashCartMode.WaterlineVirtualIron => false,
            FlashCartMode.NsMenu => false,
            FlashCartMode.SimplifiedNsMenu => false,
            FlashCartMode.GbcMaxMbcx512K => fallbackForCustom,
            _ => fallbackForCustom,
        };

    private bool GetEffectiveBatteryPresent() => ResolveBatteryPresent(GetSelectedFlashCartMode(), _flashCartBatteryPresent);

    private static FlashCartMode NormalizeSelectableFlashCartMode(FlashCartMode mode) =>
        mode == FlashCartMode.Ferroelectric
            ? FlashCartMode.WithBattery
            : mode;

    private bool IsWaterlineAllowed()
    {
        if (_comboBoxMinRomSize.SelectedItem is not MinRomSizeOption minRomSize)
        {
            return false;
        }

        return IsFourMiBRomSize(minRomSize.Value);
    }

    private void RefreshFlashCartModeOptions(FlashCartMode preferredMode)
    {
        var includeWaterline = IsWaterlineAllowed() || _currentCompilationFamily == CompilationFamily.Gbc;
        var resolvedMode = NormalizeSelectableFlashCartMode(preferredMode);
        var includeCustom = resolvedMode == FlashCartMode.Custom;

        if (resolvedMode == FlashCartMode.WaterlineVirtualIron && !includeWaterline)
        {
            resolvedMode = FlashCartMode.Custom;
            includeCustom = true;
        }

        _flashCartModeOptions = BuildFlashCartModeOptions(includeWaterline, includeCustom);

        _suppressFlashCartSync = true;
        _comboBoxFlashCartMode.DataSource = null;
        _comboBoxFlashCartMode.DataSource = _flashCartModeOptions.ToList();
        _comboBoxFlashCartMode.SelectedItem = _flashCartModeOptions
            .FirstOrDefault(option => option.Mode == resolvedMode)
            ?? _flashCartModeOptions.FirstOrDefault(option => option.Mode == FlashCartMode.NoBattery)
            ?? _flashCartModeOptions.FirstOrDefault();
        _suppressFlashCartSync = false;
        SyncCartridgeTypeAvailability();
    }

    private FlashCartMode GetSelectedFlashCartMode()
    {
        if (_comboBoxFlashCartMode.SelectedItem is FlashCartModeOption flashCartMode)
        {
            return flashCartMode.Mode;
        }

        return FlashCartMode.Custom;
    }

    private FlashCartMode GetCurrentPresetMatch()
    {
        if (_currentCompilationFamily == CompilationFamily.Gbc)
        {
            return FlashCartMode.GbcMaxMbcx512K;
        }

        if (GetEffectiveBatteryPresent())
        {
            return FlashCartMode.WithBattery;
        }

        return FlashCartMode.NoBattery;
    }

    private void SyncFlashCartModeWithCurrentSettings()
    {
        if (_suppressFlashCartSync)
        {
            return;
        }

        if (_currentCompilationFamily == CompilationFamily.Gbc)
        {
            RefreshFlashCartModeOptions(FlashCartMode.GbcMaxMbcx512K);
            return;
        }

        var currentMode = GetSelectedFlashCartMode();
        if (currentMode.IsChm1Mode())
        {
            RefreshFlashCartModeOptions(currentMode);
            return;
        }
        var waterlineMatches = IsWaterlineAllowed() &&
            !GetEffectiveBatteryPresent();
        var matchedPreset = GetCurrentPresetMatch();

        if (currentMode == FlashCartMode.WaterlineVirtualIron && waterlineMatches)
        {
            RefreshFlashCartModeOptions(FlashCartMode.WaterlineVirtualIron);
            return;
        }

        if (matchedPreset != FlashCartMode.Custom)
        {
            RefreshFlashCartModeOptions(matchedPreset);
            return;
        }

        RefreshFlashCartModeOptions(FlashCartMode.Custom);
    }

    private void SaveCurrentAppSettings()
    {
        if (!_settingsPersistenceEnabled)
        {
            return;
        }

        var cartridgeId = _comboBoxCartridgeType.SelectedItem is CartridgeDefinition cartridge
            ? cartridge.Id
            : (int?)null;
        var minRomSize = _comboBoxMinRomSize.SelectedItem is MinRomSizeOption minRomSizeOption
            ? minRomSizeOption.Value
            : (int?)null;
        var selectedFlashCartMode = _comboBoxFlashCartMode.SelectedItem is FlashCartModeOption flashCartModeOption
            ? flashCartModeOption.Mode
            : (FlashCartMode?)null;
        if(selectedFlashCartMode is not null && selectedFlashCartMode != FlashCartMode.FcMbcx) _fcReturnMode=selectedFlashCartMode.Value;
        var persistedWindowState = WindowState == FormWindowState.Maximized
            ? FormWindowState.Maximized
            : FormWindowState.Normal;
        var restoreBounds = persistedWindowState == FormWindowState.Normal
            ? Bounds
            : RestoreBounds;

        AppConfigService.SaveSettings(_currentCompilationFamily, new AppSettings
        {
            LanguageCode = _language.Code,
            BaseMenuPath = GetConfiguredBaseMenuPath(),
            OutputPath = GetConfiguredOutputPath(),
            BackgroundPath = _textBoxBackgroundPath.Text,
            GbcFontName = _currentCompilationFamily == CompilationFamily.Gbc
                ? GetSelectedGbcFontName()
                : null,
            CartridgeId = cartridgeId,
            MinRomSize = minRomSize,
            FlashCartMode = selectedFlashCartMode,
            GbaMenuType = GetSelectedGbaMenuType(),
            BatteryPresent = GetEffectiveBatteryPresent(),
            SplitOutput = _checkBoxSplitOutput.Checked,
            MinimalMenuPackage = _checkBoxMinimalMenuPackage.Checked,
            WindowState = persistedWindowState,
            WindowLeft = restoreBounds.Left,
            WindowTop = restoreBounds.Top,
            WindowWidth = restoreBounds.Width,
            WindowHeight = restoreBounds.Height,
            GameGridColumnWidths = CaptureGameGridColumnWidths(),
        });
    }

    private string? GetConfiguredBaseMenuPath()
    {
        var baseMenuPath = _textBoxBaseMenuPath.Text;
        if (ShouldUseDefaultBaseMenuPath(baseMenuPath))
        {
            return null;
        }

        return NormalizePathValue(baseMenuPath);
    }

    private string? GetConfiguredOutputPath()
    {
        var normalizedOutputPath = BuildManagedOutputPath(_textBoxOutputPath.Text);
        var outputFileName = Path.GetFileName(normalizedOutputPath);
        return IsKnownDefaultOutputFileName(outputFileName)
            ? null
            : outputFileName;
    }

    private int[] CaptureGameGridColumnWidths() =>
        _dataGridViewGames.Columns
            .Cast<DataGridViewColumn>()
            .Select(static column => column.Width)
            .ToArray();

    private void RestoreGameGridColumnWidths(IReadOnlyList<int>? widths)
    {
        if (widths is null || widths.Count == 0)
        {
            return;
        }

        var targetOffset = widths.Count == _dataGridViewGames.Columns.Count - 1 ? 1 : 0;
        if (widths.Count != _dataGridViewGames.Columns.Count &&
            widths.Count != _dataGridViewGames.Columns.Count - 1)
        {
            return;
        }

        for (var i = 0; i < Math.Min(widths.Count, _dataGridViewGames.Columns.Count - targetOffset); i++)
        {
            var column = _dataGridViewGames.Columns[i + targetOffset];
            column.Width = Math.Max(column.MinimumWidth, widths[i]);
        }
    }

    private void RestoreConfiguredWindowGeometry(AppSettings settings)
    {
        if (!TryGetConfiguredRestoreBounds(settings, out var restoreBounds) ||
            !Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(restoreBounds)))
        {
            return;
        }

        StartPosition = FormStartPosition.Manual;
        WindowState = FormWindowState.Normal;
        Bounds = restoreBounds;

        if (settings.WindowState == FormWindowState.Maximized)
        {
            WindowState = FormWindowState.Maximized;
        }
    }

    private static bool TryGetConfiguredRestoreBounds(AppSettings settings, out Rectangle restoreBounds)
    {
        if (!settings.WindowLeft.HasValue ||
            !settings.WindowTop.HasValue ||
            !settings.WindowWidth.HasValue ||
            !settings.WindowHeight.HasValue ||
            settings.WindowWidth.Value <= 0 ||
            settings.WindowHeight.Value <= 0)
        {
            restoreBounds = Rectangle.Empty;
            return false;
        }

        restoreBounds = new Rectangle(
            settings.WindowLeft.Value,
            settings.WindowTop.Value,
            settings.WindowWidth.Value,
            settings.WindowHeight.Value);
        return true;
    }

    private void SaveCurrentGameList()
    {
        if (!_gameListPersistenceEnabled)
        {
            return;
        }

        SyncManagedOutputPathWithCurrentContext();
        GameListSessionService.Save(_currentCompilationFamily, _games);
    }

    private async Task RestoreLastGameListAsync(CompilationFamily family)
    {
        var allSavedGames = GameListSessionService.Load(family);
        var savedGames = allSavedGames.Where(g => g.MdRom is null && g.OtherRom is null)
            .Select((savedGame, index) => new PendingSavedGameLoad(Guid.NewGuid(), index, savedGame))
            .ToList();
        var shouldAnimateCompletion = savedGames.Count > 0;
        var placeholderMap = PrepareLoadingPlaceholders(
            savedGames.Select(savedGame => new LoadingPlaceholderDescriptor(
                savedGame.PlaceholderId,
                string.IsNullOrWhiteSpace(savedGame.SavedGame.Title)
                    ? GetPreferredDefaultTitle(savedGame.SavedGame.RomPath, savedGame.SavedGame.GameTitle)
                    : savedGame.SavedGame.Title.Trim(),
                NormalizeRomPath(savedGame.SavedGame.RomPath)))
                .ToList(),
            clearExistingGames: true);

        try
        {
            SetGamesLoadingState(true, T("status", "loading_saved_list", "Loading previous ROM list..."), savedGames.Count);
            BeginGameLoadUiSession(placeholderMap);
            await LoadSavedGameEntryDataAsync(savedGames, EnqueueGameLoadProgress);
            if (IsDisposed)
            {
                return;
            }

            FlushPendingGameLoadProgress(forceAll: true);
            foreach (var saved in allSavedGames.Where(g => g.MdRom is not null))
            {
                var model = CreateMdGame(saved.MdRom!); model.Title = saved.Title; model.Enabled = saved.Enabled && model.Enabled; _games.Add(model);
            }
            foreach(var saved in allSavedGames.Where(g=>g.OtherRom is not null)){var model=CreateOtherGame(saved.OtherRom!,saved.RomPath);model.Title=saved.Title;_games.Add(model);}
            if (allSavedGames.Any(g => g.MdRom is not null || g.OtherRom is not null)) EnsureMdColumn();
            SyncManagedOutputPathWithCurrentContext(force: true);
            RefreshBuildCapacitySummary();
        }
        finally
        {
            if (!IsDisposed)
            {
                FlushPendingGameLoadProgress(forceAll: true);
                if (shouldAnimateCompletion)
                {
                    await CompleteGamesLoadingUiAsync();
                }

                SetActiveLoadingPlaceholder(Guid.Empty, false);
                RemoveRemainingLoadingPlaceholders(placeholderMap);
                EndGameLoadUiSession();
                SetGamesLoadingState(false, T("status", "ready", "Ready"));
            }
        }
    }

    private async Task LoadSavedGameEntryDataAsync(
        IReadOnlyList<PendingSavedGameLoad> savedGames,
        Action<GameLoadProgress>? reportProgress)
    {
        var completedCount = 0;
        using var workerLimiter = new SemaphoreSlim(MaxConcurrentMetadataWorkerCount);
        var tasks = savedGames
            .Select(async savedGame =>
            {
                await workerLimiter.WaitAsync();
                try
                {
                    var currentFileName = Path.GetFileName(savedGame.SavedGame.RomPath);
                    reportProgress?.Invoke(new GameLoadProgress(
                        savedGame.PlaceholderId,
                        Volatile.Read(ref completedCount),
                        savedGames.Count,
                        savedGame.Ordinal + 1,
                        currentFileName,
                        FinalizePlaceholder: false,
                        [],
                        []));

                    var resolvedGame = await ResolveGameEntryDataAsync(
                        savedGame.SavedGame);

                    var finalizedCount = Interlocked.Increment(ref completedCount);
                    reportProgress?.Invoke(new GameLoadProgress(
                        savedGame.PlaceholderId,
                        finalizedCount,
                        savedGames.Count,
                        savedGame.Ordinal + 1,
                        currentFileName,
                        FinalizePlaceholder: true,
                        [resolvedGame],
                        []));
                }
                finally
                {
                    workerLimiter.Release();
                }
            })
            .ToArray();

        await Task.WhenAll(tasks);
    }

    private async Task<ResolvedGameEntryData> ResolveGameEntryDataAsync(SavedGameEntry savedGame)
    {
        var normalizedPath = NormalizeRomPath(savedGame.RomPath);
        if (!SupportsPatchWorkflow())
        {
            return CreateResolvedGameEntryDataWithoutPatchMetadata(
                normalizedPath,
                savedGame.Title,
                savedGame.GameTitle,
                savedGame.SaveSlot,
                savedGame.HiddenKeys,
                savedGame.Enabled,
                savedGame.Map256m,
                savedGame.TitleFont,
                savedGame.CoverPath,
                savedGame.CoverCleared);
        }

        if (TryResolveSavedGameEntryDataFromSnapshot(savedGame, normalizedPath, out var resolvedGame))
        {
            return resolvedGame;
        }

        return await ResolveGameEntryDataAsync(
            normalizedPath,
            savedGame.Title,
            savedGame.SaveSlot,
            savedGame.HiddenKeys,
            savedGame.Enabled,
            savedGame.ApplySramPatch,
            savedGame.ApplyBatterylessPatch,
            savedGame.Map256m,
            savedGame.TitleFont,
            savedGame.GbcDedicatedSaveAllocationOverride,
            savedGame.CoverPath,
            savedGame.CoverCleared);
    }

    private async Task<ResolvedGameEntryData> ResolveGameEntryDataAsync(
        string romPath,
        string? title,
        string? saveSlot,
        string? hiddenKeys,
        bool enabled,
        bool? applySramPatchOverride,
        bool? applyBatterylessPatchOverride,
        bool? map256mOverride,
        int titleFont,
        bool? gbcDedicatedSaveAllocationOverride = null,
        string? coverPath = null,
        bool coverCleared = false)
    {
        var normalizedPath = NormalizeRomPath(romPath);
        if (!SupportsPatchWorkflow())
        {
            return CreateResolvedGameEntryDataWithoutPatchMetadata(
                normalizedPath,
                title,
                null,
                saveSlot,
                hiddenKeys,
                enabled,
                map256mOverride,
                titleFont,
                coverPath,
                coverCleared);
        }

        var cachedMetadata = await RomMetadataWorkerService.ResolveAsync(normalizedPath);
        var map256m = map256mOverride ?? cachedMetadata.Map256m;
        var saveTypeInfo = new DetectedSaveTypeInfo(
            cachedMetadata.SaveTypeKind,
            string.IsNullOrWhiteSpace(cachedMetadata.SaveTypeIdentifier)
                ? null
                : cachedMetadata.SaveTypeIdentifier);
        var sramPatchTargetIdentifier = string.IsNullOrWhiteSpace(cachedMetadata.SramPatchTargetIdentifier)
            ? null
            : cachedMetadata.SramPatchTargetIdentifier;
        var batterylessInspection = new BatterylessPatchInspection(
            cachedMetadata.BatterylessAlreadyPatched,
            cachedMetadata.BatterylessHasPayloadSpace,
            cachedMetadata.BatterylessCanExpand);

        return new ResolvedGameEntryData(
            normalizedPath,
            string.IsNullOrWhiteSpace(title) ? GetDefaultTitle(normalizedPath) : title.Trim(),
            string.Empty,
            saveSlot?.Trim() ?? string.Empty,
            hiddenKeys?.Trim() ?? string.Empty,
            enabled,
            applySramPatchOverride,
            applyBatterylessPatchOverride,
            map256m,
            saveTypeInfo,
            sramPatchTargetIdentifier,
            cachedMetadata.SramAlreadyPatched,
            batterylessInspection,
            titleFont <= 0 ? 1 : titleFont,
            gbcDedicatedSaveAllocationOverride,
            coverPath,
            coverCleared);
    }

    private ResolvedGameEntryData CreateResolvedGameEntryDataWithoutPatchMetadata(
        string normalizedPath,
        string? title,
        string? persistedGameTitle,
        string? saveSlot,
        string? hiddenKeys,
        bool enabled,
        bool? map256mOverride,
        int titleFont,
        string? coverPath = null,
        bool coverCleared = false)
    {
        var gameTitle = ResolveGameTitle(normalizedPath, persistedGameTitle);
        return new ResolvedGameEntryData(
            normalizedPath,
            string.IsNullOrWhiteSpace(title)
                ? GetPreferredDefaultTitle(normalizedPath, gameTitle)
                : title.Trim(),
            gameTitle,
            saveSlot?.Trim() ?? string.Empty,
            hiddenKeys?.Trim() ?? string.Empty,
            enabled,
            null,
            null,
            map256mOverride ?? false,
            new DetectedSaveTypeInfo(DetectedSaveTypeKind.Unknown, null),
            null,
            false,
            new BatterylessPatchInspection(false, false, false),
            titleFont <= 0 ? 1 : titleFont,
            null,
            coverPath,
            coverCleared);
    }

    private bool TryResolveSavedGameEntryDataFromSnapshot(
        SavedGameEntry savedGame,
        string normalizedPath,
        out ResolvedGameEntryData resolvedGame)
    {
        resolvedGame = default!;
        if (!savedGame.MetadataIdentified)
        {
            return false;
        }

        var gameTitle = ResolveGameTitle(normalizedPath, savedGame.GameTitle);
        resolvedGame = new ResolvedGameEntryData(
            normalizedPath,
            string.IsNullOrWhiteSpace(savedGame.Title)
                ? GetPreferredDefaultTitle(normalizedPath, gameTitle)
                : savedGame.Title.Trim(),
            gameTitle,
            savedGame.SaveSlot?.Trim() ?? string.Empty,
            savedGame.HiddenKeys?.Trim() ?? string.Empty,
            savedGame.Enabled,
            savedGame.ApplySramPatch,
            savedGame.ApplyBatterylessPatch,
            savedGame.Map256m,
            new DetectedSaveTypeInfo(
                savedGame.SaveTypeKind,
                string.IsNullOrWhiteSpace(savedGame.SaveTypeIdentifier)
                    ? null
                    : savedGame.SaveTypeIdentifier),
            string.IsNullOrWhiteSpace(savedGame.SramPatchTargetIdentifier)
                ? null
                : savedGame.SramPatchTargetIdentifier,
            savedGame.SramAlreadyPatched,
            new BatterylessPatchInspection(
                savedGame.BatterylessAlreadyPatched,
                savedGame.BatterylessHasPayloadSpace,
                savedGame.BatterylessCanExpand),
            savedGame.TitleFont <= 0 ? 1 : savedGame.TitleFont,
            savedGame.GbcDedicatedSaveAllocationOverride,
            savedGame.CoverPath,
            savedGame.CoverCleared);
        return true;
    }

    private GameEntryModel CreateGameEntryModel(ResolvedGameEntryData resolvedGame)
    {
        var applySramPatch = ResolveApplySramPatchSelection(
            resolvedGame.SaveTypeInfo.Kind,
            resolvedGame.SramPatchTargetIdentifier,
            resolvedGame.SramAlreadyPatched,
            resolvedGame.BatterylessInspection.AlreadyPatched,
            resolvedGame.ApplySramPatchOverride);
        var batterylessStatusDisplay = FormatBatterylessStatusDisplay(
            resolvedGame.SaveTypeInfo.Kind,
            resolvedGame.SramPatchTargetIdentifier,
            resolvedGame.SramAlreadyPatched,
            applySramPatch,
            resolvedGame.BatterylessInspection);
        var applyBatterylessPatch = ResolveApplyBatterylessPatchSelection(
            resolvedGame.SaveTypeInfo.Kind,
            resolvedGame.SramPatchTargetIdentifier,
            resolvedGame.SramAlreadyPatched,
            applySramPatch,
            resolvedGame.BatterylessInspection,
            resolvedGame.ApplyBatterylessPatchOverride);

        var model = new GameEntryModel
        {
            IsLoadingPlaceholder = false,
            Enabled = resolvedGame.Enabled,
            Title = resolvedGame.Title,
            GameTitle = resolvedGame.GameTitle,
            MbcTypeDisplay = string.Empty,
            SaveSupportDisplay = string.Empty,
            SaveAllocationDisplay = string.Empty,
            GbcDedicatedSaveAllocation = false,
            GbcDedicatedSaveAllocationOverride = resolvedGame.GbcDedicatedSaveAllocationOverride,
            GbcHasBatterySave = false,
            GbcEffectiveRamBankCount = 0,
            RamSizeDisplay = string.Empty,
            RomSizeDisplay = string.Empty,
            RomPath = resolvedGame.RomPath,
            SaveSlot = resolvedGame.SaveSlot,
            HiddenKeys = resolvedGame.HiddenKeys,
            SaveTypeKind = resolvedGame.SaveTypeInfo.Kind,
            SaveTypeIdentifier = resolvedGame.SaveTypeInfo.Identifier ?? string.Empty,
            SaveTypeDisplay = FormatSaveTypeDisplay(resolvedGame.SaveTypeInfo),
            SramPatchTargetIdentifier = resolvedGame.SramPatchTargetIdentifier ?? string.Empty,
            SramPatchRequirementDisplay = FormatSramPatchRequirementDisplay(
                resolvedGame.SaveTypeInfo.Kind,
                resolvedGame.SramPatchTargetIdentifier,
                resolvedGame.SramAlreadyPatched,
                resolvedGame.BatterylessInspection.AlreadyPatched),
            SramAlreadyPatched = resolvedGame.SramAlreadyPatched,
            ApplySramPatch = applySramPatch,
            BatterylessAlreadyPatched = resolvedGame.BatterylessInspection.AlreadyPatched,
            BatterylessHasPayloadSpace = resolvedGame.BatterylessInspection.HasPayloadSpace,
            BatterylessCanExpand = resolvedGame.BatterylessInspection.CanExpand,
            BatterylessStatusDisplay = batterylessStatusDisplay,
            ApplyBatterylessPatchOverride = resolvedGame.ApplyBatterylessPatchOverride,
            ApplyBatterylessPatch = applyBatterylessPatch,
            Map256m = resolvedGame.Map256m,
            TitleFont = resolvedGame.TitleFont,
            CoverPath = resolvedGame.CoverPath,
            CoverCleared = resolvedGame.CoverCleared,
        };

        if (!SupportsPatchWorkflow())
        {
            model.SaveTypeDisplay = string.Empty;
            model.SramPatchRequirementDisplay = string.Empty;
            model.ApplySramPatch = false;
            model.ApplyBatterylessPatchOverride = null;
            model.ApplyBatterylessPatch = false;
            model.BatterylessStatusDisplay = string.Empty;
        }

        RefreshGameHeaderMetadata(model);
        PopulateGameSizeDisplayValues(model);
        return model;
    }

    private BuildGameEntry CreateBuildGameEntry(GameEntryModel model, int rowNumber)
    {
        if (string.IsNullOrWhiteSpace(model.RomPath))
        {
            throw new InvalidOperationException(Tf("error", "row_empty_rom_path", "Row {0} has an empty ROM path.", rowNumber));
        }

        var batterylessInspection = new BatterylessPatchInspection(
            model.BatterylessAlreadyPatched,
            model.BatterylessHasPayloadSpace,
            model.BatterylessCanExpand);
        var sramReadyForBatteryless = IsSramReadyForBatteryless(
            model.SaveTypeKind,
            model.SramPatchTargetIdentifier,
            model.SramAlreadyPatched,
            model.ApplySramPatch);

        return new BuildGameEntry(
            model.Enabled,
            string.IsNullOrWhiteSpace(model.Title)
                ? GetPreferredDefaultTitle(model.RomPath, model.GameTitle)
                : model.Title.Trim(),
            NormalizeRomPath(model.RomPath),
            _currentCompilationFamily == CompilationFamily.Gbc
                ? null
                : ParseSaveSlot(model.SaveSlot, rowNumber),
            ParseHiddenKeys(model.HiddenKeys, rowNumber),
            model.ApplySramPatch,
            sramReadyForBatteryless,
            ResolveApplyBatterylessPatchSelection(
                model.SaveTypeKind,
                model.SramPatchTargetIdentifier,
                model.SramAlreadyPatched,
                model.ApplySramPatch,
                batterylessInspection,
                model.ApplyBatterylessPatchOverride),
            model.BatterylessAlreadyPatched,
            batterylessInspection.RequiresExpansion,
            model.SaveTypeKind,
            model.GbcDedicatedSaveAllocation,
            model.Map256m,
            model.TitleFont <= 0 ? 1 : model.TitleFont,
            model.CoverPath,
            model.CoverCleared);
    }

    private static GameEntryModel CreateLoadingPlaceholderGame(string title, string romPath) =>
        new()
        {
            IsLoadingPlaceholder = true,
            IsLoadingActive = false,
            Enabled = false,
            Title = title,
            GameTitle = string.Empty,
            GbcDedicatedSaveAllocation = false,
            GbcDedicatedSaveAllocationOverride = null,
            RomPath = romPath,
            SaveSlot = string.Empty,
            HiddenKeys = string.Empty,
            SaveTypeKind = DetectedSaveTypeKind.Unknown,
            SaveTypeIdentifier = string.Empty,
            SaveTypeDisplay = string.Empty,
            SramPatchTargetIdentifier = string.Empty,
            SramPatchRequirementDisplay = string.Empty,
            SramAlreadyPatched = false,
            ApplySramPatch = false,
            ApplyBatterylessPatch = false,
            ApplyBatterylessPatchOverride = null,
            BatterylessAlreadyPatched = false,
            BatterylessHasPayloadSpace = false,
            BatterylessCanExpand = false,
            BatterylessStatusDisplay = string.Empty,
            FileSizeDisplay = string.Empty,
            OccupiedSizeDisplay = string.Empty,
            MbcTypeDisplay = string.Empty,
            SaveSupportDisplay = string.Empty,
            SaveAllocationDisplay = string.Empty,
            GbcHasBatterySave = false,
            GbcEffectiveRamBankCount = 0,
            RamSizeDisplay = string.Empty,
            RomSizeDisplay = string.Empty,
            Map256m = false,
            TitleFont = 0,
        };

    private static bool HasSramPatchTarget(
        DetectedSaveTypeKind saveTypeKind,
        string? targetIdentifier) =>
        saveTypeKind != DetectedSaveTypeKind.None &&
        saveTypeKind != DetectedSaveTypeKind.Sram &&
        !string.IsNullOrWhiteSpace(targetIdentifier);

    private static bool IsSramPatchSelectable(
        DetectedSaveTypeKind saveTypeKind,
        string? targetIdentifier,
        bool sramAlreadyPatched,
        bool batterylessAlreadyPatched) =>
        !sramAlreadyPatched &&
        !batterylessAlreadyPatched &&
        HasSramPatchTarget(saveTypeKind, targetIdentifier);

    private static bool IsSramPatchSelectable(GameEntryModel game) =>
        !game.IsLoadingPlaceholder &&
        IsSramPatchSelectable(
            game.SaveTypeKind,
            game.SramPatchTargetIdentifier,
            game.SramAlreadyPatched,
            game.BatterylessAlreadyPatched);

    private static bool IsSramReadyForBatteryless(
        DetectedSaveTypeKind saveTypeKind,
        string? targetIdentifier,
        bool sramAlreadyPatched,
        bool applySramPatch)
    {
        if (saveTypeKind == DetectedSaveTypeKind.Sram || sramAlreadyPatched)
        {
            return true;
        }

        if (saveTypeKind == DetectedSaveTypeKind.None)
        {
            return false;
        }

        return HasSramPatchTarget(saveTypeKind, targetIdentifier) && applySramPatch;
    }

    private bool IsBatterylessPatchSelectable(
        DetectedSaveTypeKind saveTypeKind,
        string? targetIdentifier,
        bool sramAlreadyPatched,
        bool applySramPatch,
        BatterylessPatchInspection inspection) =>
        GetSelectedFlashCartMode().IsBatterylessMode() &&
        saveTypeKind != DetectedSaveTypeKind.None &&
        IsSramReadyForBatteryless(saveTypeKind, targetIdentifier, sramAlreadyPatched, applySramPatch) &&
        !inspection.AlreadyPatched &&
        inspection.CanInstallPayload;

    private bool IsBatterylessPatchSelectable(GameEntryModel game) =>
        !game.IsLoadingPlaceholder &&
        IsBatterylessPatchSelectable(
            game.SaveTypeKind,
            game.SramPatchTargetIdentifier,
            game.SramAlreadyPatched,
            game.ApplySramPatch,
            new BatterylessPatchInspection(
                game.BatterylessAlreadyPatched,
                game.BatterylessHasPayloadSpace,
                game.BatterylessCanExpand));

    private bool IsGbcSaveAllocationSelectable(GameEntryModel game) =>
        _currentCompilationFamily == CompilationFamily.Gbc &&
        !game.IsLoadingPlaceholder &&
        game.GbcHasBatterySave &&
        game.GbcEffectiveRamBankCount > 0;

    private static bool ResolveApplySramPatchSelection(
        DetectedSaveTypeKind saveTypeKind,
        string? targetIdentifier,
        bool sramAlreadyPatched,
        bool batterylessAlreadyPatched,
        bool? overrideValue)
    {
        if (!IsSramPatchSelectable(saveTypeKind, targetIdentifier, sramAlreadyPatched, batterylessAlreadyPatched))
        {
            return false;
        }

        return overrideValue ?? true;
    }

    private bool ResolveApplyBatterylessPatchSelection(
        DetectedSaveTypeKind saveTypeKind,
        string? targetIdentifier,
        bool sramAlreadyPatched,
        bool applySramPatch,
        BatterylessPatchInspection inspection,
        bool? overrideValue)
    {
        if (!IsBatterylessPatchSelectable(saveTypeKind, targetIdentifier, sramAlreadyPatched, applySramPatch, inspection))
        {
            return false;
        }

        return overrideValue ?? true;
    }

    private Dictionary<Guid, GameEntryModel> PrepareLoadingPlaceholders(
        IReadOnlyList<LoadingPlaceholderDescriptor> placeholders,
        bool clearExistingGames)
    {
        _dataGridViewGames.SuspendLayout();
        try
        {
            if (clearExistingGames)
            {
                _games.Clear();
            }

            var result = new Dictionary<Guid, GameEntryModel>(placeholders.Count);
            foreach (var placeholder in placeholders)
            {
                var model = CreateLoadingPlaceholderGame(placeholder.Title, placeholder.RomPath);
                result[placeholder.Id] = model;
                _games.Add(model);
            }

            return result;
        }
        finally
        {
            _dataGridViewGames.ResumeLayout();
        }
    }

    private void BeginGameLoadUiSession(IReadOnlyDictionary<Guid, GameEntryModel> placeholderMap)
    {
        ClearPendingGameLoadProgress();
        SetActiveLoadingPlaceholder(Guid.Empty, false);
        _loadingPlaceholderMap = placeholderMap;
    }

    private void EndGameLoadUiSession()
    {
        ClearPendingGameLoadProgress();
        SetActiveLoadingPlaceholder(Guid.Empty, false);
        _loadingPlaceholderMap = new Dictionary<Guid, GameEntryModel>();
    }

    private void EnqueueGameLoadProgress(GameLoadProgress progress) =>
        _pendingGameLoadProgress.Enqueue(progress);

    private void ClearPendingGameLoadProgress()
    {
        while (_pendingGameLoadProgress.TryDequeue(out _))
        {
        }
    }

    private void FlushPendingGameLoadProgress(bool forceAll)
    {
        if (IsDisposed)
        {
            return;
        }

        var stopwatch = forceAll ? null : Stopwatch.StartNew();
        var processedCount = 0;
        var finalizedProgress = new List<GameLoadProgress>();
        GameLoadProgress? latestProgress = null;

        while (_pendingGameLoadProgress.TryDequeue(out var progress))
        {
            latestProgress = progress;
            if (progress.FinalizePlaceholder)
            {
                finalizedProgress.Add(progress);
            }

            processedCount++;
            if (!forceAll &&
                (processedCount >= GamesLoadingUiBatchSize ||
                 (stopwatch is not null && stopwatch.ElapsedMilliseconds >= GamesLoadingUiTimeBudgetMs)))
            {
                break;
            }
        }

        if (finalizedProgress.Count > 0)
        {
            _dataGridViewGames.SuspendLayout();
        }

        try
        {
            foreach (var progress in finalizedProgress)
            {
                foreach (var logMessage in progress.LogMessages)
                {
                    AppendLog(logMessage);
                }

                ReplaceLoadingPlaceholder(progress.PlaceholderId, progress.ResolvedGames);
            }
        }
        finally
        {
            if (finalizedProgress.Count > 0)
            {
                _dataGridViewGames.ResumeLayout();
            }
        }

        if (finalizedProgress.Count > 0)
        {
            AutoAdjustGameGridColumns();
        }

        if (latestProgress is not null)
        {
            SetActiveLoadingPlaceholder(latestProgress.PlaceholderId, !latestProgress.FinalizePlaceholder);
            UpdateGamesLoadingProgress(latestProgress);
        }
    }

    private void SetActiveLoadingPlaceholder(Guid placeholderId, bool active)
    {
        var nextActivePlaceholderId = active ? placeholderId : Guid.Empty;
        if (_activeLoadingPlaceholderId == nextActivePlaceholderId)
        {
            return;
        }

        if (_activeLoadingPlaceholderId != Guid.Empty &&
            _loadingPlaceholderMap.TryGetValue(_activeLoadingPlaceholderId, out var previousPlaceholder))
        {
            previousPlaceholder.IsLoadingActive = false;
            InvalidateGameRow(previousPlaceholder);
        }

        _activeLoadingPlaceholderId = Guid.Empty;
        if (nextActivePlaceholderId == Guid.Empty)
        {
            return;
        }

        if (_loadingPlaceholderMap.TryGetValue(nextActivePlaceholderId, out var nextPlaceholder))
        {
            nextPlaceholder.IsLoadingActive = true;
            _activeLoadingPlaceholderId = nextActivePlaceholderId;
            InvalidateGameRow(nextPlaceholder);
        }
    }

    private void InvalidateGameRow(GameEntryModel game)
    {
        var rowIndex = _games.IndexOf(game);
        if (rowIndex >= 0 && rowIndex < _dataGridViewGames.Rows.Count)
        {
            _dataGridViewGames.InvalidateRow(rowIndex);
        }
    }

    private void ReplaceLoadingPlaceholder(
        Guid placeholderId,
        IReadOnlyList<ResolvedGameEntryData> resolvedGames)
    {
        if (!_loadingPlaceholderMap.TryGetValue(placeholderId, out var placeholder))
        {
            return;
        }

        var index = _games.IndexOf(placeholder);
        if (index < 0)
        {
            return;
        }

        if (resolvedGames.Count == 0)
        {
            _games.RemoveAt(index);
            return;
        }

        _games[index] = CreateGameEntryModel(resolvedGames[0]);
        for (var i = 1; i < resolvedGames.Count; i++)
        {
            _games.Insert(index + i, CreateGameEntryModel(resolvedGames[i]));
        }
    }

    private void RemoveRemainingLoadingPlaceholders(IReadOnlyDictionary<Guid, GameEntryModel> placeholderMap)
    {
        _dataGridViewGames.SuspendLayout();
        try
        {
            foreach (var placeholder in placeholderMap.Values)
            {
                var index = _games.IndexOf(placeholder);
                if (index >= 0)
                {
                    _games.RemoveAt(index);
                }
            }
        }
        finally
        {
            _dataGridViewGames.ResumeLayout();
        }
    }

    private static string NormalizePathValue(string path)
    {
        var trimmedPath = RepairPotentialIniEscapeCorruption(path).Trim();
        try
        {
            return Path.GetFullPath(trimmedPath);
        }
        catch
        {
            return trimmedPath;
        }
    }

    private static string RepairPotentialIniEscapeCorruption(string path) =>
        path
            .Replace("\r", "r", StringComparison.Ordinal)
            .Replace("\n", "n", StringComparison.Ordinal)
            .Replace("\t", "t", StringComparison.Ordinal);

    private static string NormalizeRomPath(string romPath)
    {
        return NormalizePathValue(romPath);
    }

    private static string GetDefaultTitle(string romPath)
    {
        try
        {
            return Path.GetFileNameWithoutExtension(romPath);
        }
        catch
        {
            return romPath;
        }
    }

    private static string GetPreferredDefaultTitle(string romPath, string? gameTitle)
    {
        if (!string.IsNullOrWhiteSpace(gameTitle))
        {
            return gameTitle.Trim();
        }

        return GetDefaultTitle(romPath);
    }

    private string ResolveGameTitle(string normalizedPath, string? persistedGameTitle = null)
    {
        if (_currentCompilationFamily == CompilationFamily.Gbc &&
            GameBoyRomHeaderService.TryRead(normalizedPath, out var metadata))
        {
            return metadata.HeaderTitle?.Trim() ?? string.Empty;
        }

        return string.IsNullOrWhiteSpace(persistedGameTitle)
            ? string.Empty
            : persistedGameTitle.Trim();
    }

    private void RefreshGameHeaderMetadata(GameEntryModel game)
    {
        if (game.IsLoadingPlaceholder)
        {
            game.GameTitle = string.Empty;
            game.MbcTypeDisplay = string.Empty;
            game.SaveSupportDisplay = string.Empty;
            game.SaveAllocationDisplay = string.Empty;
            game.GbcDedicatedSaveAllocation = false;
            game.GbcHasBatterySave = false;
            game.GbcEffectiveRamBankCount = 0;
            game.RamSizeDisplay = string.Empty;
            game.RomSizeDisplay = string.Empty;
            return;
        }

        var normalizedPath = NormalizeRomPath(game.RomPath);
        game.GameTitle = ResolveGameTitle(normalizedPath);
        if (_currentCompilationFamily == CompilationFamily.Gbc &&
            GameBoyRomHeaderService.TryRead(normalizedPath, out var metadata))
        {
            var defaultDedicatedSaveAllocation = metadata.HasBattery && metadata.EffectiveRamBankCount > 0;
            var dedicatedSaveAllocation = defaultDedicatedSaveAllocation &&
                (game.GbcDedicatedSaveAllocationOverride ?? true);
            game.GbcHasBatterySave = metadata.HasBattery;
            game.GbcEffectiveRamBankCount = metadata.EffectiveRamBankCount;
            game.GbcDedicatedSaveAllocation = dedicatedSaveAllocation;
            game.MbcTypeDisplay = GameBoyRomHeaderService.GetMbcTypeDisplay(metadata.CartridgeType);
            game.SaveSupportDisplay = metadata.EffectiveRamBankCount <= 0
                ? T("value", "gbc_none", "-")
                : dedicatedSaveAllocation
                ? T("value", "gbc_save_dedicated", "独立")
                : T("value", "gbc_save_shared", "共用");
            game.SaveAllocationDisplay = metadata.EffectiveRamBankCount <= 0
                ? T("value", "gbc_none", "-")
                : dedicatedSaveAllocation
                ? T("value", "gbc_save_dedicated", "独立区")
                : T("value", "gbc_save_shared", "共享区");
            game.RamSizeDisplay = GameBoyRomHeaderService.HasBuiltInMbc2Ram(metadata.CartridgeType)
                ? "512x4b"
                : metadata.RamSizeCode == 0
                ? T("value", "gbc_none", "-")
                : GameBoyRomHeaderService.GetRamSizeDisplay(metadata.RamSizeCode);
            game.RomSizeDisplay = GameBoyRomHeaderService.GetRomSizeDisplay(metadata.RomSizeCode);
        }
        else
        {
            game.MbcTypeDisplay = string.Empty;
            game.SaveSupportDisplay = string.Empty;
            game.SaveAllocationDisplay = string.Empty;
            game.GbcDedicatedSaveAllocation = false;
            game.GbcDedicatedSaveAllocationOverride = null;
            game.GbcHasBatterySave = false;
            game.GbcEffectiveRamBankCount = 0;
            game.RamSizeDisplay = string.Empty;
            game.RomSizeDisplay = string.Empty;
        }

        if (string.IsNullOrWhiteSpace(game.Title))
        {
            game.Title = GetPreferredDefaultTitle(normalizedPath, game.GameTitle);
        }
    }

    private static IEnumerable<string> GetDefaultBaseMenuCandidates(
        CompilationFamily family,
        FlashCartMode flashCartMode,
        GbaMenuType gbaMenuType)
    {
        if (family == CompilationFamily.Gbc)
        {
            return GetGbcDefaultBaseMenuCandidates(AppConfigService.LoadLanguageCode() ?? LanguageService.DefaultLanguageCode);
        }

        if (gbaMenuType == GbaMenuType.NsMenu)
        {
            return
            [
                Path.Combine(AppContext.BaseDirectory, "Assets", "universal_menu.gba"),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\GBA_MultiMenu_extended_NEW\GBA_MultiMenu_extended_NEW.gba")),
            ];
        }

        if (gbaMenuType == GbaMenuType.SimplifiedNsMenu)
        {
            return
            [
                Path.Combine(AppContext.BaseDirectory, "Assets", "simplified_ns_menu.gba"),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\GBA_MultiMenu_Simplified_NS\GBA_MultiMenu_Simplified_NS.gba")),
            ];
        }

        if (flashCartMode is FlashCartMode.WaterlineVirtualIron or FlashCartMode.Chm1 or FlashCartMode.Chm1NoBattery)
        {
            return
            [
                Path.Combine(AppContext.BaseDirectory, "Assets", "lk_multimenu_wl.gba"),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\GBA_MultiMenu_extended_WL\rom_builder\lk_multimenu_wl.gba")),
            ];
        }

        return
        [
            Path.Combine(AppContext.BaseDirectory, "Assets", "lk_multimenu.gba"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\GBA_MultiMenu_GUI-main\lk_multimenu.gba")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\GBA_MultiMenu_extended-main\rom_builder\lk_multimenu.gba")),
        ];
    }

    private static IEnumerable<string> GetAllDefaultBaseMenuCandidates()
    {
        foreach (var candidate in GetGbcDefaultBaseMenuCandidates(LanguageService.DefaultLanguageCode))
        {
            yield return candidate;
        }

        foreach (var candidate in GetGbcDefaultBaseMenuCandidates("en_US"))
        {
            yield return candidate;
        }

        var gbaCandidates = new[]
        {
            (FlashCartMode.NoBattery, GbaMenuType.Classic),
            (FlashCartMode.WaterlineVirtualIron, GbaMenuType.Classic),
            (FlashCartMode.NoBattery, GbaMenuType.NsMenu),
            (FlashCartMode.NoBattery, GbaMenuType.SimplifiedNsMenu),
        };
        foreach (var (flashCartMode, gbaMenuType) in gbaCandidates)
        {
            foreach (var candidate in GetDefaultBaseMenuCandidates(CompilationFamily.Gba, flashCartMode, gbaMenuType))
            {
                yield return candidate;
            }
        }
    }

    private static string? FindDefaultBaseMenuPath(
        CompilationFamily family,
        FlashCartMode flashCartMode,
        GbaMenuType gbaMenuType)
    {
        var resolvedMode = flashCartMode is FlashCartMode.WaterlineVirtualIron or FlashCartMode.Chm1 or FlashCartMode.Chm1NoBattery
            ? flashCartMode
            : FlashCartMode.NoBattery;
        return GetDefaultBaseMenuCandidates(family, resolvedMode, gbaMenuType).FirstOrDefault(File.Exists);
    }

    private static bool ShouldUseDefaultBaseMenuPath(string? baseMenuPath)
    {
        return string.IsNullOrWhiteSpace(baseMenuPath) || IsKnownDefaultBaseMenuPath(baseMenuPath);
    }

    private static bool IsKnownDefaultBaseMenuPath(string? baseMenuPath)
    {
        if (string.IsNullOrWhiteSpace(baseMenuPath))
        {
            return false;
        }

        var normalizedPath = NormalizePathValue(baseMenuPath);
        return GetAllDefaultBaseMenuCandidates()
            .Select(NormalizePathValue)
            .Any(candidate => string.Equals(candidate, normalizedPath, StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveEffectiveBaseMenuPath(
        string? baseMenuPath,
        CompilationFamily family,
        FlashCartMode flashCartMode,
        GbaMenuType gbaMenuType)
    {
        if (!ShouldUseDefaultBaseMenuPath(baseMenuPath))
        {
            return NormalizePathValue(baseMenuPath!);
        }

        return FindDefaultBaseMenuPath(family, flashCartMode, gbaMenuType) ?? baseMenuPath?.Trim() ?? string.Empty;
    }

    private void SyncBaseMenuPathWithSelectedGbaOptions(bool force = false)
    {
        if (_textBoxBaseMenuPath is null)
        {
            return;
        }

        var currentPath = _textBoxBaseMenuPath.Text;
        if (!force && !ShouldUseDefaultBaseMenuPath(currentPath))
        {
            return;
        }

        var targetPath = FindDefaultBaseMenuPath(
            _currentCompilationFamily,
            GetSelectedFlashCartMode(),
            GetSelectedGbaMenuType()) ?? string.Empty;
        if (string.Equals(
                string.IsNullOrWhiteSpace(currentPath) ? string.Empty : NormalizePathValue(currentPath),
                string.IsNullOrWhiteSpace(targetPath) ? string.Empty : NormalizePathValue(targetPath),
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _textBoxBaseMenuPath.Text = targetPath;
    }

    private static string GetManagedOutputDirectory()
    {
        var outputDirectory = Path.Combine(AppContext.BaseDirectory, "output");
        Directory.CreateDirectory(outputDirectory);
        return outputDirectory;
    }

    private string BuildManagedOutputPath(string? inputPath)
    {
        var fileName = Path.GetFileName(string.IsNullOrWhiteSpace(inputPath) ? string.Empty : inputPath.Trim());
        if (string.IsNullOrWhiteSpace(fileName) || IsKnownDefaultOutputFileName(fileName))
        {
            fileName = GetDefaultOutputFileName();
        }

        if (!Path.HasExtension(fileName))
        {
            fileName += _currentCompilationFamily == CompilationFamily.Gbc ? ".gbc" : ".gba";
        }

        return Path.Combine(GetManagedOutputDirectory(), fileName);
    }

    private void SyncManagedOutputPathWithCurrentContext(bool force = false)
    {
        if (_textBoxOutputPath is null)
        {
            return;
        }

        var currentPath = _textBoxOutputPath.Text;
        if (!force && !ShouldUseDefaultOutputPath(currentPath))
        {
            return;
        }

        var targetPath = BuildManagedOutputPath(currentPath);
        if (string.Equals(
                string.IsNullOrWhiteSpace(currentPath) ? string.Empty : NormalizePathValue(currentPath),
                NormalizePathValue(targetPath),
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _textBoxOutputPath.Text = targetPath;
    }

    private void SetBuildState(bool building)
    {
        _groupBoxBuildSettings.Enabled = !building && !_gamesLoading;
        _groupBoxGames.Enabled = !building;
        _buttonBuild.Enabled = !building
            && !_gamesLoading
            && !_buildBlockedByCapacity
            && string.IsNullOrWhiteSpace(_capacitySummaryErrorMessage);
        _buttonCancel.Enabled = building;
    }

    private void SetGamesLoadingState(bool loading, string message, int totalCount = 0)
    {
        var previousTotalCount = _gamesLoadingTotalCount;
        _gamesLoading = loading;
        _gamesLoadingStatusMessage = message;
        _gamesLoadingTotalCount = totalCount;
        if (!loading)
        {
            _gamesLoadingCurrentOrdinal = 0;
            _gamesLoadingCurrentFileName = string.Empty;
        }

        if (_panelGamesLoading is not null)
        {
            _panelGamesLoading.Visible = false;
        }

        if (_labelGamesLoading is not null)
        {
            _labelGamesLoading.Text = message;
        }

        if (_buildCancellation is null)
        {
            if (_groupBoxBuildSettings is not null)
            {
                _groupBoxBuildSettings.Enabled = !loading;
            }

            if (_buttonBuild is not null)
            {
                _buttonBuild.Enabled = !loading
                    && !_buildBlockedByCapacity
                    && string.IsNullOrWhiteSpace(_capacitySummaryErrorMessage);
            }

            if (_panelGameActions is not null)
            {
                _panelGameActions.Enabled = !loading;
            }

            if (_dataGridViewGames is not null)
            {
                _dataGridViewGames.ReadOnly = loading;
            }

            if (_progressBarBuild is not null)
            {
                if (loading)
                {
                    ResetGamesLoadingProgressBar(totalCount);
                }
            }

            if (_labelStatus is not null)
            {
                _labelStatus.Text = message;
            }
        }

        if (!loading)
        {
            RefreshIdleStatusMessage();
        }

        if (!loading)
        {
            _gamesLoadingProgressTimer.Stop();
        }
        else if (totalCount > 0)
        {
            _gamesLoadingProgressTimer.Start();
        }
    }

    private void CenterGamesLoadingPanel()
    {
        if (_panelGamesLoading is null || _panelGamesLoading.Controls.Count == 0)
        {
            return;
        }

        var loadingLayout = _panelGamesLoading.Controls[0];
        loadingLayout.Left = Math.Max(0, (_panelGamesLoading.ClientSize.Width - loadingLayout.Width) / 2);
        loadingLayout.Top = Math.Max(0, (_panelGamesLoading.ClientSize.Height - loadingLayout.Height) / 2);
    }

    private void UpdateGamesLoadingProgress(GameLoadProgress progress)
    {
        if (_buildCancellation is not null)
        {
            return;
        }

        _gamesLoadingCurrentOrdinal = progress.CurrentOrdinal;
        _gamesLoadingTotalCount = progress.TotalCount;
        _gamesLoadingCurrentFileName = progress.CurrentFileName;

        var totalCount = Math.Max(1, progress.TotalCount);
        _gamesLoadingProgressMaximum = Math.Max(1, totalCount * GamesLoadingProgressUnitsPerFile);
        var completedUnits = Math.Clamp(
            progress.CompletedCount * GamesLoadingProgressUnitsPerFile,
            0,
            _gamesLoadingProgressMaximum);
        var previewUnits = Math.Clamp(
            (progress.CompletedCount * GamesLoadingProgressUnitsPerFile) + GamesLoadingProgressPreviewUnits,
            0,
            _gamesLoadingProgressMaximum);

        if (progress.FinalizePlaceholder)
        {
            _gamesLoadingProgressTarget = completedUnits;
        }
        else
        {
            _gamesLoadingProgressTarget = Math.Max(_gamesLoadingProgressTarget, previewUnits);
        }

        ApplyGamesLoadingProgressBarValue(_gamesLoadingProgressValue);

        if (_gamesLoading && !_gamesLoadingProgressTimer.Enabled)
        {
            _gamesLoadingProgressTimer.Start();
        }

        if (_labelStatus is not null)
        {
            _labelStatus.Text = string.IsNullOrWhiteSpace(progress.CurrentFileName)
                ? T("status", "loading_roms", "Loading, please wait...")
                : Tf(
                    "status",
                    "loading_file",
                    "Loading {0} ({1}/{2})",
                    progress.CurrentFileName,
                    progress.CurrentOrdinal,
                    progress.TotalCount);
        }
    }

    private void ResetGamesLoadingProgressBar(int totalCount)
    {
        _gamesLoadingProgressMaximum = Math.Max(1, Math.Max(1, totalCount) * GamesLoadingProgressUnitsPerFile);
        _gamesLoadingProgressValue = 0;
        _gamesLoadingProgressTarget = 0;
        ApplyGamesLoadingProgressBarValue(0);
    }

    private void ApplyGamesLoadingProgressBarValue(int value)
    {
        if (_progressBarBuild is null)
        {
            return;
        }

        var clampedValue = Math.Clamp(value, 0, _gamesLoadingProgressMaximum);
        _progressBarBuild.Style = ProgressBarStyle.Continuous;
        _progressBarBuild.Minimum = 0;
        if (_progressBarBuild.Maximum != _gamesLoadingProgressMaximum)
        {
            _progressBarBuild.Maximum = _gamesLoadingProgressMaximum;
        }

        if (_progressBarBuild.Value != clampedValue)
        {
            _progressBarBuild.Value = clampedValue;
        }
    }

    private void CompleteGamesLoadingProgressBar()
    {
        if (_gamesLoadingProgressMaximum <= 0)
        {
            return;
        }

        _gamesLoadingProgressTimer.Stop();
        _gamesLoadingProgressTarget = _gamesLoadingProgressMaximum;
        _gamesLoadingProgressValue = _gamesLoadingProgressMaximum;
        ApplyGamesLoadingProgressBarValue(_gamesLoadingProgressMaximum);
    }

    private async Task CompleteGamesLoadingUiAsync()
    {
        if (_gamesLoadingProgressMaximum > 0)
        {
            CompleteGamesLoadingProgressBar();
            await Task.Delay(GamesLoadingCompletionDelayMs);
        }
    }

    private void gamesLoadingProgressTimer_Tick(object? sender, EventArgs e)
    {
        FlushPendingGameLoadProgress(forceAll: false);

        if (!_gamesLoading || _buildCancellation is not null)
        {
            _gamesLoadingProgressTimer.Stop();
            return;
        }

        if (_gamesLoadingProgressValue >= _gamesLoadingProgressTarget)
        {
            if (_gamesLoadingProgressValue >= _gamesLoadingProgressMaximum)
            {
                _gamesLoadingProgressTimer.Stop();
            }

            return;
        }

        var remaining = _gamesLoadingProgressTarget - _gamesLoadingProgressValue;
        var step = Math.Max(1, remaining / 6);
        _gamesLoadingProgressValue = Math.Min(
            _gamesLoadingProgressTarget,
            _gamesLoadingProgressValue + step);
        ApplyGamesLoadingProgressBarValue(_gamesLoadingProgressValue);
    }

    private void AppendLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        _textBoxLog.AppendText($"{FormatLogLine(message)}{Environment.NewLine}");
        ScrollLogToEnd();
    }

    private string FormatLogLine(string message) => $"[{DateTime.Now:HH:mm:ss}] {message}";

    private void ScrollLogToEnd()
    {
        if (_textBoxLog is null)
        {
            return;
        }

        _textBoxLog.SelectionStart = _textBoxLog.TextLength;
        _textBoxLog.SelectionLength = 0;
        _textBoxLog.ScrollToCaret();
    }

    private static Task RunFileTask(Action work) =>
        Task.Run(() => RunBackgroundFileWork(work));

    private static Task<T> RunFileTask<T>(Func<T> work) =>
        Task.Run(() => RunBackgroundFileWork(work));

    private static void RunBackgroundFileWork(Action work)
    {
        var currentThread = Thread.CurrentThread;
        var previousPriority = currentThread.Priority;
        try
        {
            currentThread.Priority = ThreadPriority.BelowNormal;
            work();
        }
        finally
        {
            TryRestoreThreadPriority(currentThread, previousPriority);
        }
    }

    private static T RunBackgroundFileWork<T>(Func<T> work)
    {
        var currentThread = Thread.CurrentThread;
        var previousPriority = currentThread.Priority;
        try
        {
            currentThread.Priority = ThreadPriority.BelowNormal;
            return work();
        }
        finally
        {
            TryRestoreThreadPriority(currentThread, previousPriority);
        }
    }

    private static void TryRestoreThreadPriority(Thread thread, ThreadPriority priority)
    {
        try
        {
            thread.Priority = priority;
        }
        catch
        {
        }
    }

    private void BuildUi(bool resetWindowGeometry = true)
    {
        SuspendLayout();

        AllowDrop = true;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        if (resetWindowGeometry)
        {
            ClientSize = DefaultWindowSize;
            StartPosition = FormStartPosition.CenterScreen;
        }

        MinimumSize = MinimumWindowSize;
        Text = $"{T("ui", "window_title", "Chis Multicart Builder")} {AppVersion} by {AppAuthor}";
        DragEnter -= Form1_DragEnter;
        DragDrop -= Form1_DragDrop;
        FormClosing -= Form1_FormClosing;
        Shown -= Form1_Shown;
        DragEnter += Form1_DragEnter;
        DragDrop += Form1_DragDrop;
        FormClosing += Form1_FormClosing;
        Shown += Form1_Shown;

        var mainLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(12, 10, 12, 10),
        };
        mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 212F));
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 58F));
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 42F));

        _groupBoxBuildSettings = BuildSettingsGroup();
        _groupBoxGames = BuildGamesGroup();
        var groupBoxLog = BuildLogGroup();

        mainLayout.Controls.Add(_groupBoxBuildSettings, 0, 0);
        mainLayout.Controls.Add(_groupBoxGames, 0, 1);
        mainLayout.Controls.Add(groupBoxLog, 0, 2);

        Controls.Add(mainLayout);
        RegisterGameGridToolTipDismissHandlers(this);
        ResumeLayout(false);
    }

    private GroupBox BuildSettingsGroup()
    {
        _textBoxBaseMenuPath = new TextBox
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Margin = new Padding(2, SettingsInputVerticalMargin, 2, SettingsInputVerticalMargin),
        };
        _textBoxBaseMenuPath.TextChanged += buildSettingControl_Changed;
        _textBoxBaseMenuPath.TextChanged += capacitySummaryRelevantControl_Changed;
        _textBoxOutputPath = new TextBox
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Margin = new Padding(2, SettingsInputVerticalMargin, 2, SettingsInputVerticalMargin),
        };
        _textBoxOutputPath.TextChanged += buildSettingControl_Changed;
        _textBoxBackgroundPath = new TextBox
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Margin = new Padding(2, SettingsInputVerticalMargin, 2, SettingsInputVerticalMargin),
        };
        _textBoxBackgroundPath.TextChanged += buildSettingControl_Changed;
        _comboBoxGbcFont = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Visible = false,
            Margin = new Padding(2, SettingsInputVerticalMargin, 2, SettingsInputVerticalMargin),
        };
        _comboBoxGbcFont.SelectedIndexChanged += buildSettingControl_Changed;
        _comboBoxGbaMenuType = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Margin = new Padding(2, SettingsInputVerticalMargin, 2, SettingsInputVerticalMargin),
        };
        _comboBoxGbaMenuType.SelectedIndexChanged += comboBoxGbaMenuType_SelectedIndexChanged;
        _checkBoxMinimalMenuPackage = new CheckBox
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Margin = new Padding(2),
            TextAlign = ContentAlignment.MiddleLeft,
            CheckAlign = ContentAlignment.MiddleLeft,
        };
        BindLocalizedControl(
            _checkBoxMinimalMenuPackage,
            "ui",
            "minimal_menu_package",
            "Minimal Package",
            "build_settings_tooltip",
            "minimal_menu_package",
            "Outputs only the 0x0B0000 NS menu and cover area without writing the configuration area.");
        _checkBoxMinimalMenuPackage.CheckedChanged += checkBoxMinimalMenuPackage_CheckedChanged;
        _comboBoxCartridgeType = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Margin = new Padding(2, SettingsInputVerticalMargin, 2, SettingsInputVerticalMargin),
        };
        _comboBoxCartridgeType.SelectedIndexChanged += buildSettingControl_Changed;
        _comboBoxMinRomSize = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Enabled = false,
            Margin = new Padding(2, SettingsInputVerticalMargin, 2, SettingsInputVerticalMargin),
        };
        _comboBoxMinRomSize.SelectedIndexChanged += comboBoxMinRomSize_SelectedIndexChanged;
        _comboBoxFlashCartMode = new GbaMultiMenuBuilder.Fc.ModeAlignedComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Margin = new Padding(2, SettingsInputVerticalMargin, 2, SettingsInputVerticalMargin),
        };
        _comboBoxFlashCartMode.SelectedIndexChanged += comboBoxFlashCartMode_SelectedIndexChanged;
        _comboBoxLanguage = new ComboBox
        {
            Anchor = AnchorStyles.Left,
            Width = 200,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Margin = new Padding(2, 0, 0, 0),
        };
        _comboBoxLanguage.SelectedIndexChanged += comboBoxLanguage_SelectedIndexChanged;
        _comboBoxLanguage.DropDownClosed += comboBoxLanguage_DropDownClosed;
        _checkBoxSplitOutput = new CheckBox
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Margin = new Padding(2),
            TextAlign = ContentAlignment.MiddleLeft,
            CheckAlign = ContentAlignment.MiddleLeft,
        };
        BindLocalizedControl(
            _checkBoxSplitOutput,
            "ui",
            "split_output_short",
            "NDS",
            "build_settings_tooltip",
            "split_output",
            "Exports the final ROM in 32 MiB split parts for compatible flashing workflows.");
        _checkBoxSplitOutput.CheckedChanged += buildSettingControl_Changed;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 6,
            RowCount = 4,
            Margin = new Padding(0),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, SettingsButtonColumnWidth));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, SettingsButtonColumnWidth));

        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, SettingsRowHeight));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, SettingsRowHeight));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, SettingsRowHeight));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, SettingsRowHeight));

        layout.Controls.Add(MakeBuildSettingsLabel(
            "ui",
            "base_menu",
            "Base Menu",
            "build_settings_tooltip",
            "base_menu",
            "Selects the base menu ROM used as the menu core for the compilation."), 0, 0);
        layout.Controls.Add(_textBoxBaseMenuPath, 1, 0);
        layout.Controls.Add(MakeBuildSettingsButton("ui", "browse_button", "Browse", buttonBrowseBaseMenu_Click), 2, 0);
        layout.Controls.Add(MakeBuildSettingsLabel(
            "ui",
            "output_rom",
            "Output ROM",
            "build_settings_tooltip",
            "output_rom",
            "Sets the output file name and path for the generated compilation ROM."), 3, 0);
        layout.Controls.Add(_textBoxOutputPath, 4, 0);
        layout.Controls.Add(MakeBuildSettingsButton("ui", "browse_button", "Browse", buttonBrowseOutput_Click), 5, 0);
        _labelBackgroundOrFont = MakeBuildSettingsLabel(
            "ui",
            "background",
            "Custom Background",
            "build_settings_tooltip",
            "background",
            "Optionally replaces the menu background image with a custom picture.");
        layout.Controls.Add(_labelBackgroundOrFont, 0, 1);
        layout.Controls.Add(_textBoxBackgroundPath, 1, 1);
        _buttonBrowseBackgroundOrFont = MakeBuildSettingsButton("ui", "browse_button", "Browse", buttonBrowseBackground_Click);
        layout.Controls.Add(_buttonBrowseBackgroundOrFont, 2, 1);
        layout.Controls.Add(MakeBuildSettingsLabel(
            "ui",
            "cartridge_type",
            "Cartridge Type",
            "build_settings_tooltip",
            "cartridge_type",
            "Chooses the target NOR FLASH model and command set used for layout and compatibility."), 3, 1);
        layout.Controls.Add(_comboBoxCartridgeType, 4, 1);
        _labelGbcFont = MakeBuildSettingsLabel(
            "ui",
            "gbc_font_source",
            "Chinese Font",
            "build_settings_tooltip",
            "gbc_font_source",
            "Requires a 12px font for GBC Chinese glyph generation and preview. 8px fonts are not supported.");
        layout.Controls.Add(_labelGbcFont, 0, 2);
        layout.Controls.Add(_comboBoxGbcFont, 1, 2);
        _buttonOpenGbcFontFolder = MakeBuildSettingsButton(
            "ui",
            "open_output_folder",
            "Open Folder",
            buttonOpenGbcFontFolder_Click);
        layout.Controls.Add(_buttonOpenGbcFontFolder, 2, 2);
        _labelGbaMenuType = MakeBuildSettingsLabel(
            "ui",
            "gba_menu_type",
            "Menu Type",
            "build_settings_tooltip",
            "gba_menu_type",
            "Selects the GBA menu interface independently from the flashcart hardware mode.");
        layout.Controls.Add(_labelGbaMenuType, 0, 2);
        layout.Controls.Add(_comboBoxGbaMenuType, 1, 2);
        layout.Controls.Add(_checkBoxMinimalMenuPackage, 2, 2);
        layout.Controls.Add(MakeBuildSettingsLabel(
            "ui",
            "flashcart_type",
            "Flashcart Type",
            "build_settings_tooltip",
            "flashcart_type",
            "Chooses a preset flashcart mode and links related detailed settings automatically."), 0, 3);
        layout.Controls.Add(_comboBoxFlashCartMode, 1, 3);
        layout.Controls.Add(MakeBuildSettingsLabel(
            "ui",
            "min_rom_size",
            "Min ROM Size",
            "build_settings_tooltip",
            "min_rom_size",
            "Sets the smallest ROM allocation unit supported by the cartridge and affects alignment."), 3, 3);
        layout.Controls.Add(_comboBoxMinRomSize, 4, 3);
        layout.Controls.Add(_checkBoxSplitOutput, 5, 3);

        var languagePanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Margin = new Padding(0),
            ColumnCount = 3,
            RowCount = 1,
        };
        languagePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        languagePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        languagePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        languagePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        languagePanel.Controls.Add(MakeBuildSettingsLabel(
            "ui",
            "language",
            "Language",
            "build_settings_tooltip",
            "language",
            "Switches the UI display language and refreshes the current window immediately."), 0, 0);
        languagePanel.Controls.Add(_comboBoxLanguage, 1, 0);
        var creditsLabel = new Label
        {
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            ForeColor = SystemColors.GrayText,
            TextAlign = ContentAlignment.MiddleRight,
            Margin = new Padding(12, 0, 0, 0),
        };
        BindLocalizedControl(
            creditsLabel,
            "ui",
            "credits_text",
            "Credits: lesserkuma (menu author), ZaindORp (fork author, original GUI author)");
        languagePanel.Controls.Add(creditsLabel, 2, 0);

        var container = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
        };
        container.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        container.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
        container.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        container.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        container.Controls.Add(languagePanel, 0, 0);
        container.Controls.Add(layout, 0, 1);
#if false

            Text = T(
                "ui",
                "credits_text",
                "婵°倧璁ｇ紞鈧梻? lesserkuma闂佹寧绋戦悧鎰亹瀹ュ纭€闁哄洦姘ㄧ粙濠囨煠閺夋寧瀚呯紒?| ZaindORp闂佹寧绋戦悧鍡涘垂鎼淬劌缁╂い鏍ㄧ懅缁嬪﹪鏌ら弶鎸庡珔缂佽鲸绻堝畷銏ゆ偄闁垮鈧倝鏌涘鐑╁亾閸愯尙鍘繛?GUI 婵炶揪绲剧划蹇涘焵椤掆偓閹锋垹妲?),
        };
#endif
        var groupBox = new GroupBox
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            Controls = { container },
        };
        BindLocalizedControl(groupBox, "ui", "build_settings", "Build Settings");
        return groupBox;
    }

    private GroupBox BuildGamesGroup()
    {
        _panelGameActions = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0),
        };
        _panelGameActions.Controls.Add(MakeFixedLocalizedButton("ui", "add_roms", "Add ROMs", buttonAddRoms_Click));
        _panelGameActions.Controls.Add(MakeFixedLocalizedButton("ui", "remove_selected", "Remove Selected", buttonRemoveSelected_Click));
        _panelGameActions.Controls.Add(MakeFixedLocalizedButton("ui", "clear_all", "Clear All", buttonClearAll_Click));
        _panelGameActions.Controls.Add(MakeFixedLocalizedButton("ui", "move_up", "Move Up", buttonMoveUp_Click));
        _panelGameActions.Controls.Add(MakeFixedLocalizedButton("ui", "move_down", "Move Down", buttonMoveDown_Click));
        _buttonPreviewMenu = MakeFixedLocalizedButton("ui", "preview_menu", "Preview Menu", buttonPreviewMenu_Click);
        _panelGameActions.Controls.Add(_buttonPreviewMenu);
        _panelGameActions.Controls.Add(MakeFixedLocalizedButton("ui", "view_config_area", "View Config", buttonViewConfigArea_Click));
        var buttonExportGbcFont = MakeFixedLocalizedButton("ui", "export_gbc_font", "Export Font", buttonExportGbcFont_Click);
        buttonExportGbcFont.Visible = false;
        _panelGameActions.Controls.Add(buttonExportGbcFont);

        _labelCapacitySummary = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0),
            Text = T("ui", "capacity_summary_unavailable", "Used: -- / Total: --"),
        };
        _labelSaveCapacitySummary = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0),
            Text = T("ui", "gbc_save_capacity_summary_unavailable", "Save Allocated/Total --/--"),
            Visible = false,
        };

        var summaryLayout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 2,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0),
        };
        summaryLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        summaryLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        summaryLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        summaryLayout.Controls.Add(_labelSaveCapacitySummary, 0, 0);
        summaryLayout.Controls.Add(_labelCapacitySummary, 0, 1);

        var headerLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 4),
        };
        headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        headerLayout.Controls.Add(_panelGameActions, 0, 0);
        headerLayout.Controls.Add(summaryLayout, 1, 0);

        _dataGridViewGames = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowDrop = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeColumns = true,
            AutoGenerateColumns = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
            MultiSelect = true,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            ShowCellToolTips = false,
            EnableHeadersVisualStyles = false,
        };
        _dataGridViewGames.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        _dataGridViewGames.DataError += dataGridViewGames_DataError;
        _dataGridViewGames.DragEnter += Form1_DragEnter;
        _dataGridViewGames.DragDrop += Form1_DragDrop;
        _dataGridViewGames.CellValueChanged += dataGridViewGames_CellValueChanged;
        _dataGridViewGames.CurrentCellDirtyStateChanged += dataGridViewGames_CurrentCellDirtyStateChanged;
        _dataGridViewGames.CellBeginEdit += dataGridViewGames_CellBeginEdit;
        _dataGridViewGames.MouseMove += dataGridViewGames_MouseMove;
        _dataGridViewGames.MouseLeave += dataGridViewGames_MouseLeave;
        _dataGridViewGames.MouseDown += dataGridViewGames_MouseDown;
        _dataGridViewGames.Scroll += dataGridViewGames_Scroll;
        _dataGridViewGames.CellFormatting += dataGridViewGames_CellFormatting;
        _dataGridViewGames.ColumnWidthChanged += dataGridViewGames_ColumnWidthChanged;
        ConfigureGameGridColumns();

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.Controls.Add(headerLayout, 0, 0);

        var contentPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 2, 0, 0),
        };
        contentPanel.Controls.Add(_dataGridViewGames);

        _labelGamesLoading = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.None,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = SystemColors.GrayText,
        };
        BindLocalizedControl(_labelGamesLoading, "status", "loading_saved_list", "Loading previous ROM list...");

        var loadingLayout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 1,
            Anchor = AnchorStyles.None,
            BackColor = Color.Transparent,
        };
        loadingLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        loadingLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        loadingLayout.Controls.Add(_labelGamesLoading, 0, 0);

        _panelGamesLoading = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = SystemColors.Control,
            Visible = false,
        };
        _panelGamesLoading.Controls.Add(loadingLayout);
        _panelGamesLoading.Resize += (_, _) =>
        {
            loadingLayout.Left = Math.Max(0, (_panelGamesLoading.ClientSize.Width - loadingLayout.Width) / 2);
            loadingLayout.Top = Math.Max(0, (_panelGamesLoading.ClientSize.Height - loadingLayout.Height) / 2);
        };

        contentPanel.Controls.Add(_panelGamesLoading);
        layout.Controls.Add(contentPanel, 0, 1);

        var groupBox = new GroupBox
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            Controls = { layout },
        };
        BindLocalizedControl(groupBox, "ui", "rom_list", "ROM List");
        return groupBox;
    }

    private GroupBox BuildLogGroup()
    {
        _textBoxLog = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
        };

        _progressBarBuild = new ProgressBar
        {
            Dock = DockStyle.Fill,
        };

        _labelStatus = new Label
        {
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleRight,
            Text = T("status", "ready", "Ready"),
        };

        _buttonBuild = MakeFixedLocalizedButton("ui", "build_button", "Build", buttonBuild_Click);
        _buttonBuild.Dock = DockStyle.Fill;
        _buttonCancel = MakeFixedLocalizedButton("ui", "cancel_button", "Cancel", buttonCancelBuild_Click);
        _buttonCancel.Enabled = false;
        _buttonCancel.Dock = DockStyle.Fill;
        var buttonOpenOutputFolder = MakeFixedLocalizedButton("ui", "open_output_folder", "Open Folder", buttonOpenOutputFolder_Click);
        buttonOpenOutputFolder.Dock = DockStyle.Fill;

        var actionLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 5,
            RowCount = 1,
        };
        actionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120F));
        actionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120F));
        actionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        actionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120F));
        actionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250F));
        actionLayout.Controls.Add(_buttonBuild, 0, 0);
        actionLayout.Controls.Add(_buttonCancel, 1, 0);
        actionLayout.Controls.Add(_progressBarBuild, 2, 0);
        actionLayout.Controls.Add(buttonOpenOutputFolder, 3, 0);
        actionLayout.Controls.Add(_labelStatus, 4, 0);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));
        layout.Controls.Add(_textBoxLog, 0, 0);
        layout.Controls.Add(actionLayout, 0, 1);

        var groupBox = new GroupBox
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            Controls = { layout },
        };
        BindLocalizedControl(groupBox, "ui", "build_log", "Build Log");
        return groupBox;
    }

    private void ConfigureGameGridColumns()
    {
        _dataGridViewGames.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = SequenceColumnName,
            HeaderText = T("grid", "sequence", "序号"),
            MinimumWidth = 50,
            Width = 60,
            Resizable = DataGridViewTriState.True,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        _dataGridViewGames.Columns.Add(new DataGridViewCheckBoxColumn
        {
            DataPropertyName = nameof(GameEntryModel.Enabled),
            HeaderText = T("grid", "use", "Use"),
            MinimumWidth = 50,
            Width = 60,
            Resizable = DataGridViewTriState.True,
        });
        _dataGridViewGames.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(GameEntryModel.Title),
            HeaderText = T("grid", "title", "Title"),
            MinimumWidth = 90,
            Width = 180,
            Resizable = DataGridViewTriState.True,
        });
        _dataGridViewGames.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(GameEntryModel.GameTitle),
            HeaderText = T("grid", "game_title", "Game Title"),
            MinimumWidth = 110,
            Width = 180,
            Resizable = DataGridViewTriState.True,
            ReadOnly = true,
        });
        _dataGridViewGames.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(GameEntryModel.MbcTypeDisplay),
            HeaderText = T("grid", "mbc_type", "MBC Type"),
            MinimumWidth = 90,
            Width = 100,
            Resizable = DataGridViewTriState.True,
            ReadOnly = true,
        });
        _dataGridViewGames.Columns.Add(new DataGridViewCheckBoxColumn
        {
            DataPropertyName = nameof(GameEntryModel.GbcDedicatedSaveAllocation),
            HeaderText = T("grid", "save_allocation", "Save Allocation"),
            MinimumWidth = 90,
            Width = 100,
            Resizable = DataGridViewTriState.True,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            ThreeState = false,
            TrueValue = true,
            FalseValue = false,
            ValueType = typeof(bool),
            FlatStyle = FlatStyle.Standard,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Alignment = DataGridViewContentAlignment.MiddleCenter,
            },
        });
        _dataGridViewGames.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(GameEntryModel.SaveSupportDisplay),
            HeaderText = T("grid", "save_support", "Save"),
            MinimumWidth = 90,
            Width = 100,
            Resizable = DataGridViewTriState.True,
            ReadOnly = true,
        });
        _dataGridViewGames.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(GameEntryModel.RamSizeDisplay),
            HeaderText = T("grid", "ram_size", "RAM"),
            MinimumWidth = 90,
            Width = 100,
            Resizable = DataGridViewTriState.True,
            ReadOnly = true,
        });
        _dataGridViewGames.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(GameEntryModel.RomSizeDisplay),
            HeaderText = T("grid", "rom_size", "ROM"),
            MinimumWidth = 90,
            Width = 100,
            Resizable = DataGridViewTriState.True,
            ReadOnly = true,
        });
        _dataGridViewGames.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(GameEntryModel.SaveTypeDisplay),
            HeaderText = T("grid", "save_type", "Save Type"),
            MinimumWidth = 110,
            Width = 140,
            Resizable = DataGridViewTriState.True,
            ReadOnly = true,
        });
        _dataGridViewGames.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(GameEntryModel.SramPatchRequirementDisplay),
            HeaderText = T("grid", "patch_conversion", "Patch Conversion"),
            MinimumWidth = 130,
            Width = 170,
            Resizable = DataGridViewTriState.True,
            ReadOnly = true,
        });
        _dataGridViewGames.Columns.Add(new DataGridViewCheckBoxColumn
        {
            DataPropertyName = nameof(GameEntryModel.ApplySramPatch),
            HeaderText = T("grid", "sram_patch", "SRAM Patch"),
            MinimumWidth = 85,
            Width = 95,
            Resizable = DataGridViewTriState.True,
        });
        _dataGridViewGames.Columns.Add(new DataGridViewCheckBoxColumn
        {
            DataPropertyName = nameof(GameEntryModel.ApplyBatterylessPatch),
            HeaderText = T("grid", "batteryless_patch", "Batteryless Patch"),
            MinimumWidth = 95,
            Width = 110,
            Resizable = DataGridViewTriState.True,
        });
        _dataGridViewGames.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(GameEntryModel.BatterylessStatusDisplay),
            HeaderText = T("grid", "batteryless_status", "Batteryless Status"),
            MinimumWidth = 115,
            Width = 150,
            Resizable = DataGridViewTriState.True,
            ReadOnly = true,
        });
        _dataGridViewGames.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(GameEntryModel.FileSizeDisplay),
            HeaderText = T("grid", "file_size", "File Size"),
            MinimumWidth = 80,
            Width = 95,
            Resizable = DataGridViewTriState.True,
            ReadOnly = true,
        });
        _dataGridViewGames.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(GameEntryModel.OccupiedSizeDisplay),
            HeaderText = T("grid", "occupied_size", "Occupied"),
            MinimumWidth = 90,
            Width = 110,
            Resizable = DataGridViewTriState.True,
            ReadOnly = true,
        });
        _dataGridViewGames.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(GameEntryModel.SaveSlot),
            HeaderText = T("grid", "save_slot", "Save Slot"),
            MinimumWidth = 70,
            Width = 80,
            Resizable = DataGridViewTriState.True,
        });
        _dataGridViewGames.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(GameEntryModel.HiddenKeys),
            HeaderText = T("grid", "hidden_keys", "Hidden Keys"),
            MinimumWidth = 120,
            Width = 150,
            Resizable = DataGridViewTriState.True,
        });
        _dataGridViewGames.Columns.Add(new DataGridViewCheckBoxColumn
        {
            DataPropertyName = nameof(GameEntryModel.Map256m),
            HeaderText = T("grid", "map_256m", "Map 256M"),
            MinimumWidth = 70,
            Width = 80,
            Resizable = DataGridViewTriState.True,
        });
        _dataGridViewGames.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(GameEntryModel.TitleFont),
            HeaderText = T("grid", "font", "Font"),
            MinimumWidth = 60,
            Width = 70,
            Resizable = DataGridViewTriState.True,
        });
        _dataGridViewGames.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(GameEntryModel.RomPath),
            HeaderText = T("grid", "rom_path", "ROM Path"),
            MinimumWidth = 200,
            Width = 420,
            Resizable = DataGridViewTriState.True,
        });

        RefreshGameGridColumnVisibility();
    }

    private void RefreshGameGridColumnVisibility()
    {
        if (_dataGridViewGames is null)
        {
            return;
        }

        var showPatchColumns = SupportsPatchWorkflow();
        var showGbaOnlyColumns = _currentCompilationFamily == CompilationFamily.Gba;
        var showGbcOnlyColumns = _currentCompilationFamily == CompilationFamily.Gbc;
        SetGameGridColumnVisibility(nameof(GameEntryModel.GameTitle), showGbcOnlyColumns);
        SetGameGridColumnVisibility(nameof(GameEntryModel.MbcTypeDisplay), showGbcOnlyColumns);
        SetGameGridColumnVisibility(nameof(GameEntryModel.SaveSupportDisplay), showGbcOnlyColumns);
        SetGameGridColumnVisibility(nameof(GameEntryModel.GbcDedicatedSaveAllocation), showGbcOnlyColumns);
        SetGameGridColumnVisibility(nameof(GameEntryModel.RamSizeDisplay), showGbcOnlyColumns);
        SetGameGridColumnVisibility(nameof(GameEntryModel.RomSizeDisplay), showGbcOnlyColumns);
        SetGameGridColumnVisibility(nameof(GameEntryModel.SaveTypeDisplay), showPatchColumns);
        SetGameGridColumnVisibility(nameof(GameEntryModel.SramPatchRequirementDisplay), showPatchColumns);
        SetGameGridColumnVisibility(nameof(GameEntryModel.ApplySramPatch), showPatchColumns);
        SetGameGridColumnVisibility(nameof(GameEntryModel.ApplyBatterylessPatch), showPatchColumns);
        SetGameGridColumnVisibility(nameof(GameEntryModel.BatterylessStatusDisplay), showPatchColumns);
        SetGameGridColumnVisibility(nameof(GameEntryModel.SaveSlot), showGbaOnlyColumns);
        SetGameGridColumnVisibility(nameof(GameEntryModel.HiddenKeys), showGbaOnlyColumns);
        SetGameGridColumnVisibility(nameof(GameEntryModel.Map256m), showGbaOnlyColumns);
        if (_buttonPreviewMenu is not null)
        {
            _buttonPreviewMenu.Visible = true;
        }
        AutoAdjustGameGridColumns();
    }

    private static IEnumerable<string> GetGbcDefaultBaseMenuCandidates(string? languageCode)
    {
        var useChineseMenu = string.Equals(
            string.IsNullOrWhiteSpace(languageCode) ? LanguageService.DefaultLanguageCode : languageCode.Trim(),
            LanguageService.DefaultLanguageCode,
            StringComparison.OrdinalIgnoreCase);
        var primaryAssetName = useChineseMenu ? "menu_zh.gb" : "menu_en.gb";
        var fallbackAssetName = useChineseMenu ? "menu_en.gb" : "menu_zh.gb";

        return
        [
            Path.Combine(AppContext.BaseDirectory, "Assets", primaryAssetName),
            Path.Combine(AppContext.BaseDirectory, "Assets", fallbackAssetName),
        ];
    }

    private bool IsSelectedGbcMenuChinese()
    {
        var fileName = Path.GetFileNameWithoutExtension(_textBoxBaseMenuPath?.Text ?? string.Empty);
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

        return string.Equals(
            _language?.Code ?? LanguageService.DefaultLanguageCode,
            LanguageService.DefaultLanguageCode,
            StringComparison.OrdinalIgnoreCase);
    }

    private void SetGameGridColumnVisibility(string dataPropertyName, bool visible)
    {
        if (_dataGridViewGames.Columns
            .Cast<DataGridViewColumn>()
            .FirstOrDefault(column => string.Equals(column.DataPropertyName, dataPropertyName, StringComparison.Ordinal))
            is DataGridViewColumn column)
        {
            column.Visible = visible;
        }
    }

    private static Label MakeLabel(string text) =>
        new()
        {
            Text = text,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(2, 0, 8, 0),
        };

    private Label MakeBuildSettingsLabel(
        string textSection,
        string textKey,
        string textFallback,
        string toolTipSection,
        string toolTipKey,
        string toolTipFallback)
    {
        var label = MakeLabel(string.Empty);
        label.AutoSize = true;
        label.Anchor = AnchorStyles.Left;
        label.TextAlign = ContentAlignment.MiddleLeft;
        label.Margin = new Padding(2, 0, 8, 0);
        BindLocalizedControl(label, textSection, textKey, textFallback, toolTipSection, toolTipKey, toolTipFallback);
        return label;
    }

    private Button MakeLocalizedButton(string section, string key, string fallback, EventHandler onClick, int width = 100)
    {
        var button = MakeButton(string.Empty, onClick, width);
        BindLocalizedControl(button, section, key, fallback);
        return button;
    }

    private Button MakeFixedLocalizedButton(string section, string key, string fallback, EventHandler onClick)
    {
        var button = MakeLocalizedButton(section, key, fallback, onClick, ActionButtonWidth);
        button.Width = ActionButtonWidth;
        _fixedWidthButtons.Add(button);
        return button;
    }

    private void BindLocalizedControl(
        Control control,
        string textSection,
        string textKey,
        string textFallback,
        string? toolTipSection = null,
        string? toolTipKey = null,
        string? toolTipFallback = null)
    {
        var binding = new LocalizedControlBinding(
            textSection,
            textKey,
            textFallback,
            toolTipSection,
            toolTipKey,
            toolTipFallback);
        control.Tag = binding;
        ApplyLocalizedBinding(control, binding);
    }

    private void ApplyLocalizedControlTree(Control root)
    {
        foreach (Control child in root.Controls)
        {
            if (child.Tag is LocalizedControlBinding binding)
            {
                ApplyLocalizedBinding(child, binding);
            }

            if (child.HasChildren)
            {
                ApplyLocalizedControlTree(child);
            }
        }
    }

    private void ApplyLocalizedBinding(Control control, LocalizedControlBinding binding)
    {
        control.Text = T(binding.TextSection, binding.TextKey, binding.TextFallback);
        if (control is Button button && button.Dock != DockStyle.Fill && !_fixedWidthButtons.Contains(button))
        {
            ResizeButtonForText(button);
        }

        if (!string.IsNullOrWhiteSpace(binding.ToolTipSection) &&
            !string.IsNullOrWhiteSpace(binding.ToolTipKey) &&
            !string.IsNullOrWhiteSpace(binding.ToolTipFallback))
        {
            _buildSettingsToolTip.SetToolTip(
                control,
                T(binding.ToolTipSection!, binding.ToolTipKey!, binding.ToolTipFallback!));
        }
    }

    private void RefreshBackgroundOrFontUi()
    {
        if (_labelBackgroundOrFont is null ||
            _textBoxBackgroundPath is null ||
            _comboBoxGbcFont is null ||
            _labelGbcFont is null ||
            _comboBoxGbaMenuType is null ||
            _labelGbaMenuType is null ||
            _checkBoxMinimalMenuPackage is null ||
            _buttonBrowseBackgroundOrFont is null ||
            _buttonOpenGbcFontFolder is null)
        {
            return;
        }

        var isGbc = _currentCompilationFamily == CompilationFamily.Gbc;
        var isGbcChineseMenu = isGbc && IsSelectedGbcMenuChinese();
        var isGbaCoverMenu = _currentCompilationFamily == CompilationFamily.Gba &&
            GetSelectedGbaMenuType() is GbaMenuType.NsMenu or GbaMenuType.SimplifiedNsMenu;
        _textBoxBackgroundPath.Visible = true;
        _comboBoxGbcFont.Visible = isGbcChineseMenu;
        _labelGbcFont.Visible = isGbcChineseMenu;
        _buttonOpenGbcFontFolder.Visible = isGbcChineseMenu;
        _comboBoxGbaMenuType.Visible = !isGbc;
        _labelGbaMenuType.Visible = !isGbc;
        _checkBoxMinimalMenuPackage.Visible = isGbaCoverMenu;
        if (!isGbaCoverMenu && _checkBoxMinimalMenuPackage.Checked)
        {
            _checkBoxMinimalMenuPackage.Checked = false;
        }
        _checkBoxSplitOutput.Enabled = !_checkBoxMinimalMenuPackage.Checked;

        var backgroundLabelKey = isGbaCoverMenu ? "cover_directory" : "background";
        var backgroundFallback = isGbaCoverMenu ? "Cover Directory" : "Custom Background";
        _labelBackgroundOrFont.Text = T("ui", backgroundLabelKey, backgroundFallback);
        var backgroundToolTip = isGbaCoverMenu
            ? T("build_settings_tooltip", "cover_directory", "Selects the folder containing cover_index.csv and cover images for the NS menu.")
            : isGbc
            ? T("build_settings_tooltip", "gbc_background", "Optionally uses an exact 160x144 image as the GBC menu background.")
            : T("build_settings_tooltip", "background", "Optionally replaces the menu background image with a custom picture.");
        _buildSettingsToolTip.SetToolTip(_labelBackgroundOrFont, backgroundToolTip);
        _buildSettingsToolTip.SetToolTip(_textBoxBackgroundPath, backgroundToolTip);
        _buttonBrowseBackgroundOrFont.Text = T("ui", "browse_button", "Browse");
        _buildSettingsToolTip.SetToolTip(_buttonBrowseBackgroundOrFont, backgroundToolTip);

        var fontToolTip = T("build_settings_tooltip", "gbc_font_source", "Requires a 12px font for GBC Chinese glyph generation and preview. 8px fonts are not supported.");
        _labelGbcFont.Text = T("ui", "gbc_font_source", "Chinese Font");
        _buildSettingsToolTip.SetToolTip(_labelGbcFont, fontToolTip);
        _buildSettingsToolTip.SetToolTip(_comboBoxGbcFont, fontToolTip);
        _buttonOpenGbcFontFolder.Text = T("ui", "open_output_folder", "Open Folder");
        _buildSettingsToolTip.SetToolTip(_buttonOpenGbcFontFolder, fontToolTip);

        var menuTypeToolTip = T("build_settings_tooltip", "gba_menu_type", "Selects the GBA menu interface independently from the flashcart hardware mode.");
        _labelGbaMenuType.Text = T("ui", "gba_menu_type", "Menu Type");
        _buildSettingsToolTip.SetToolTip(_labelGbaMenuType, menuTypeToolTip);
        _buildSettingsToolTip.SetToolTip(_comboBoxGbaMenuType, menuTypeToolTip);

        var minimalPackageToolTip = T(
            "build_settings_tooltip",
            "minimal_menu_package",
            "Outputs only the 0x0B0000 NS menu and cover area without writing the configuration area.");
        _checkBoxMinimalMenuPackage.Text = T("ui", "minimal_menu_package", "Minimal Package");
        _buildSettingsToolTip.SetToolTip(_checkBoxMinimalMenuPackage, minimalPackageToolTip);

    }

    private void RefreshGbcFontOptions(string? preferredFontName = null)
    {
        if (_comboBoxGbcFont is null)
        {
            return;
        }

        var options = GbcFontService.GetBuiltInFontOptions().ToList();
        var selectedFontName = GbcFontService.NormalizeBuiltInFontName(preferredFontName)
            ?? GbcFontService.NormalizeBuiltInFontName(GetSelectedGbcFontName())
            ?? GbcFontService.GetDefaultBuiltInFontFileName();

        _comboBoxGbcFont.DataSource = null;
        _comboBoxGbcFont.DisplayMember = nameof(GbcBuiltInFontOption.DisplayName);
        _comboBoxGbcFont.ValueMember = nameof(GbcBuiltInFontOption.FileName);
        _comboBoxGbcFont.DataSource = options;

        if (options.Count == 0)
        {
            return;
        }

        _comboBoxGbcFont.SelectedItem = options
            .FirstOrDefault(option => string.Equals(option.FileName, selectedFontName, StringComparison.OrdinalIgnoreCase))
            ?? options.FirstOrDefault(option => string.Equals(option.FileName, GbcFontService.GetDefaultBuiltInFontFileName(), StringComparison.OrdinalIgnoreCase))
            ?? options[0];
    }

    private string? GetSelectedGbcFontName() =>
        _comboBoxGbcFont.SelectedItem is GbcBuiltInFontOption fontOption
            ? fontOption.FileName
            : null;

    private string? ResolveSelectedGbcFontPath() =>
        _comboBoxGbcFont.SelectedItem is GbcBuiltInFontOption fontOption
            ? fontOption.FullPath
            : null;

    private void OpenBuiltInGbcFontFolder()
    {
        var fontDirectory = GbcFontService.GetBuiltInFontsDirectory();
        Directory.CreateDirectory(fontDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = fontDirectory,
            UseShellExecute = true,
        });
    }

    private void RefreshLocalizedGameGridColumns()
    {
        if (_dataGridViewGames.Columns.Count == 0)
        {
            return;
        }

        SetGameGridColumnHeaderByName(SequenceColumnName, "grid", "sequence", "序号");
        SetGameGridColumnHeader(nameof(GameEntryModel.Enabled), "grid", "use", "Use");
        SetGameGridColumnHeader(nameof(GameEntryModel.Title), "grid", "title", "Title");
        SetGameGridColumnHeader(nameof(GameEntryModel.GameTitle), "grid", "game_title", "Game Title");
        SetGameGridColumnHeader(nameof(GameEntryModel.MbcTypeDisplay), "grid", "mbc_type", "MBC Type");
        SetGameGridColumnHeader(nameof(GameEntryModel.SaveSupportDisplay), "grid", "save_support", "Save");
        SetGameGridColumnHeader(nameof(GameEntryModel.GbcDedicatedSaveAllocation), "grid", "save_allocation", "Save Allocation");
        SetGameGridColumnHeader(nameof(GameEntryModel.RamSizeDisplay), "grid", "ram_size", "RAM");
        SetGameGridColumnHeader(nameof(GameEntryModel.RomSizeDisplay), "grid", "rom_size", "ROM");
        SetGameGridColumnHeader(nameof(GameEntryModel.SaveTypeDisplay), "grid", "save_type", "Save Type");
        SetGameGridColumnHeader(nameof(GameEntryModel.SramPatchRequirementDisplay), "grid", "patch_conversion", "Patch Conversion");
        SetGameGridColumnHeader(nameof(GameEntryModel.ApplySramPatch), "grid", "sram_patch", "SRAM Patch");
        SetGameGridColumnHeader(nameof(GameEntryModel.ApplyBatterylessPatch), "grid", "batteryless_patch", "Batteryless Patch");
        SetGameGridColumnHeader(nameof(GameEntryModel.BatterylessStatusDisplay), "grid", "batteryless_status", "Batteryless Status");
        SetGameGridColumnHeader(nameof(GameEntryModel.FileSizeDisplay), "grid", "file_size", "File Size");
        SetGameGridColumnHeader(nameof(GameEntryModel.OccupiedSizeDisplay), "grid", "occupied_size", "Occupied");
        SetGameGridColumnHeader(nameof(GameEntryModel.SaveSlot), "grid", "save_slot", "Save Slot");
        SetGameGridColumnHeader(nameof(GameEntryModel.HiddenKeys), "grid", "hidden_keys", "Hidden Keys");
        SetGameGridColumnHeader(nameof(GameEntryModel.Map256m), "grid", "map_256m", "Map 256M");
        SetGameGridColumnHeader(nameof(GameEntryModel.TitleFont), "grid", "font", "Font");
        SetGameGridColumnHeader(nameof(GameEntryModel.RomPath), "grid", "rom_path", "ROM Path");
        AutoAdjustGameGridColumns();
    }

    private void SetGameGridColumnHeader(string dataPropertyName, string section, string key, string fallback)
    {
        if (_dataGridViewGames.Columns
            .Cast<DataGridViewColumn>()
            .FirstOrDefault(column => string.Equals(column.DataPropertyName, dataPropertyName, StringComparison.Ordinal))
            is DataGridViewColumn column)
        {
            column.HeaderText = T(section, key, fallback);
        }
    }

    private void SetGameGridColumnHeaderByName(string columnName, string section, string key, string fallback)
    {
        if (_dataGridViewGames.Columns
            .Cast<DataGridViewColumn>()
            .FirstOrDefault(column => string.Equals(column.Name, columnName, StringComparison.Ordinal))
            is DataGridViewColumn column)
        {
            column.HeaderText = T(section, key, fallback);
        }
    }

    private void AutoAdjustGameGridColumns()
    {
        if (_dataGridViewGames is null || _dataGridViewGames.Columns.Count == 0)
        {
            return;
        }

        _suppressGameGridColumnWidthPersistence = true;
        _dataGridViewGames.SuspendLayout();
        try
        {
            foreach (DataGridViewColumn column in _dataGridViewGames.Columns)
            {
                column.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
                if (!column.Visible)
                {
                    continue;
                }

                if (string.Equals(column.DataPropertyName, nameof(GameEntryModel.RomPath), StringComparison.Ordinal))
                {
                    continue;
                }

                var preferredWidth = column.GetPreferredWidth(DataGridViewAutoSizeColumnMode.AllCells, true);
                var maxWidth = GetMaxAutoWidth(column);
                column.Width = Math.Max(column.MinimumWidth, Math.Min(preferredWidth, maxWidth));
            }

            if (_dataGridViewGames.Columns
                .Cast<DataGridViewColumn>()
                .FirstOrDefault(column => string.Equals(column.DataPropertyName, nameof(GameEntryModel.RomPath), StringComparison.Ordinal) && column.Visible)
                is DataGridViewColumn romPathColumn)
            {
                var occupiedWidth = _dataGridViewGames.Columns
                    .Cast<DataGridViewColumn>()
                    .Where(column => column.Visible && !ReferenceEquals(column, romPathColumn))
                    .Sum(column => column.Width);
                var availableWidth = _dataGridViewGames.ClientSize.Width - occupiedWidth - SystemInformation.VerticalScrollBarWidth - 8;
                var preferredWidth = romPathColumn.GetPreferredWidth(DataGridViewAutoSizeColumnMode.DisplayedCells, true);
                romPathColumn.Width = Math.Max(
                    romPathColumn.MinimumWidth,
                    Math.Max(preferredWidth, availableWidth));
            }
        }
        finally
        {
            _dataGridViewGames.ResumeLayout();
            _suppressGameGridColumnWidthPersistence = false;
        }
    }

    private static int GetMaxAutoWidth(DataGridViewColumn column)
    {
        if (string.Equals(column.Name, SequenceColumnName, StringComparison.Ordinal))
        {
            return 64;
        }

        return column.DataPropertyName switch
        {
            nameof(GameEntryModel.Enabled) => 60,
            nameof(GameEntryModel.Title) => 220,
            nameof(GameEntryModel.GameTitle) => 220,
            nameof(GameEntryModel.MbcTypeDisplay) => 110,
            nameof(GameEntryModel.SaveSupportDisplay) => 90,
            nameof(GameEntryModel.GbcDedicatedSaveAllocation) => 110,
            nameof(GameEntryModel.RamSizeDisplay) => 90,
            nameof(GameEntryModel.RomSizeDisplay) => 90,
            nameof(GameEntryModel.SaveTypeDisplay) => 160,
            nameof(GameEntryModel.SramPatchRequirementDisplay) => 190,
            nameof(GameEntryModel.ApplySramPatch) => 95,
            nameof(GameEntryModel.ApplyBatterylessPatch) => 110,
            nameof(GameEntryModel.BatterylessStatusDisplay) => 170,
            nameof(GameEntryModel.FileSizeDisplay) => 95,
            nameof(GameEntryModel.OccupiedSizeDisplay) => 110,
            nameof(GameEntryModel.SaveSlot) => 90,
            nameof(GameEntryModel.HiddenKeys) => 160,
            nameof(GameEntryModel.Map256m) => 90,
            nameof(GameEntryModel.TitleFont) => 70,
            _ => 220,
        };
    }

    private void RefreshLocalizedMinRomSizeOptionNames()
    {
        foreach (var option in _minRomSizeOptions)
        {
            option.Name = option.Value switch
            {
                32 * 1024 => "32 KiB",
                4 * 1024 * 1024 => T("ui", "min_rom_size_4m", "4 MiB"),
                _ => FormatByteSize(option.Value),
            };
        }

        RefreshComboBoxBinding(_comboBoxMinRomSize);
    }

    private void RefreshLocalizedFlashCartModeOptionNames()
    {
        foreach (var option in _flashCartModeOptions)
        {
            option.Name = option.Mode switch
            {
                FlashCartMode.NoBattery => T("ui", "flashcart_no_battery", "GBA | No Battery"),
                FlashCartMode.WithBattery => T("ui", "flashcart_with_battery", "GBA | With Battery / FRAM"),
                FlashCartMode.Ferroelectric => T("ui", "flashcart_with_battery", "GBA | With Battery / FRAM"),
                FlashCartMode.WaterlineVirtualIron => T("ui", "flashcart_waterline_virtual_iron", "GBA | Waterline (Virtual Iron)"),
                FlashCartMode.Chm1 => T("ui", "flashcart_chm1", "GBA | 01G/02G Fragment Management (Battery / FRAM)"),
                FlashCartMode.Chm1NoBattery => T("ui", "flashcart_chm1_no_battery", "GBA | 01G/02G Fragment Management (Batteryless)"),
                FlashCartMode.GbcMaxMbcx512K => T("ui", "flashcart_gbc_max_mbcx_512k", "GBC | MAX/MBCX (512K)"),
                FlashCartMode.Custom => T("ui", "flashcart_custom", "Custom"),
                _ => option.Name,
            };
        }

        RefreshComboBoxBinding(_comboBoxFlashCartMode);
    }

    private void RefreshLocalizedGbaMenuTypeOptionNames()
    {
        foreach (var option in _gbaMenuTypeOptions)
        {
            option.Name = option.Type switch
            {
                GbaMenuType.NsMenu => T("ui", "gba_menu_ns", "NS Menu"),
                GbaMenuType.SimplifiedNsMenu => T("ui", "gba_menu_ns_simplified", "Simplified NS Menu"),
                _ => T("ui", "gba_menu_classic", "Classic Menu"),
            };
        }
        RefreshComboBoxBinding(_comboBoxGbaMenuType);
    }

    private void RefreshComboBoxBinding(ComboBox comboBox)
    {
        var dataSource = comboBox.DataSource;
        var bindingContext = BindingContext;
        if (dataSource is not null &&
            bindingContext is not null &&
            bindingContext[dataSource] is CurrencyManager currencyManager)
        {
            currencyManager.Refresh();
        }

        comboBox.Refresh();
    }

    private static Button MakeButton(string text, EventHandler onClick, int width = 100)
    {
        var button = new Button
        {
            Text = text,
            Width = width == 0 ? 100 : width,
            Height = 32,
            UseVisualStyleBackColor = true,
        };
        ResizeButtonForText(button);
        button.Click += onClick;
        return button;
    }

    private Button MakeBuildSettingsButton(string section, string key, string fallback, EventHandler onClick)
    {
        var button = MakeLocalizedButton(section, key, fallback, onClick);
        button.Dock = DockStyle.Fill;
        button.Margin = new Padding(2);
        button.Width = 100;
        return button;
    }

    private static void ResizeButtonForText(Button button)
    {
        const int horizontalPadding = 24;
        var preferredWidth = TextRenderer.MeasureText(
            button.Text,
            button.Font,
            Size.Empty,
            TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width + horizontalPadding;
        button.Width = Math.Max(button.Width, preferredWidth);
    }

    private void buttonBrowseBaseMenu_Click(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Filter = _currentCompilationFamily == CompilationFamily.Gbc
                ? "Game Boy Menu ROM (*.gb;*.gbc)|*.gb;*.gbc|All files (*.*)|*.*"
                : T("file_dialog", "gba_menu_filter", "GBA Menu ROM (*.gba)|*.gba|All files (*.*)|*.*"),
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _textBoxBaseMenuPath.Text = dialog.FileName;
        }
    }

    private void buttonBrowseOutput_Click(object? sender, EventArgs e)
    {
        var outputDirectory = GetManagedOutputDirectory();
        using var dialog = new SaveFileDialog
        {
            Filter = _currentCompilationFamily == CompilationFamily.Gbc
                ? "GBC ROM (*.gbc)|*.gbc|All files (*.*)|*.*"
                : T("file_dialog", "gba_rom_filter", "GBA ROM (*.gba)|*.gba|All files (*.*)|*.*"),
            DefaultExt = _currentCompilationFamily == CompilationFamily.Gbc ? "gbc" : "gba",
            FileName = Path.GetFileName(_textBoxOutputPath.Text),
            InitialDirectory = outputDirectory,
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _textBoxOutputPath.Text = BuildManagedOutputPath(dialog.FileName);
        }
    }

    private void buttonOpenOutputFolder_Click(object? sender, EventArgs e)
    {
        var outputDirectory = GetCurrentOutputDirectory();
        Directory.CreateDirectory(outputDirectory);

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = outputDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                T("dialog", "open_output_folder_failed_title", "Open Output Folder Failed"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void buttonBrowseBackground_Click(object? sender, EventArgs e)
    {
        if (_currentCompilationFamily == CompilationFamily.Gba &&
            GetSelectedGbaMenuType() is GbaMenuType.NsMenu or GbaMenuType.SimplifiedNsMenu)
        {
            using var folderDialog = new FolderBrowserDialog
            {
                Description = T("file_dialog", "cover_directory_dialog", "Select the NS cover directory."),
                UseDescriptionForTitle = true,
                SelectedPath = Directory.Exists(_textBoxBackgroundPath.Text)
                    ? _textBoxBackgroundPath.Text
                    : string.Empty,
                ShowNewFolderButton = false,
            };
            if (folderDialog.ShowDialog(this) == DialogResult.OK)
            {
                _textBoxBackgroundPath.Text = folderDialog.SelectedPath;
            }
            return;
        }

        using var dialog = new OpenFileDialog
        {
            Filter = T("file_dialog", "png_filter", "PNG files (*.png)|*.png|All files (*.*)|*.*"),
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _textBoxBackgroundPath.Text = dialog.FileName;
        }
    }

    private async void buttonAddRoms_Click(object? sender, EventArgs e)
    {
        if (_gamesLoading)
        {
            return;
        }

        using var dialog = new OpenFileDialog
        {
            Filter = BuildRomImportFilter(),
            CheckFileExists = true,
            Multiselect = true,
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            await AddRomFilesAsync(dialog.FileNames);
        }
    }

    private string BuildRomImportFilter() =>
        _currentCompilationFamily == CompilationFamily.Gbc
            ? "Supported ROM Files (*.gb;*.gbc;*.zip)|*.gb;*.gbc;*.zip|Game Boy ROM (*.gb;*.gbc)|*.gb;*.gbc|ZIP Archive (*.zip)|*.zip|All files (*.*)|*.*"
            : T("file_dialog", "rom_import_filter", "Supported ROM Files (*.gba;*.zip)|*.gba;*.zip|GBA ROM (*.gba)|*.gba|ZIP Archive (*.zip)|*.zip|All files (*.*)|*.*");

    private void buttonRemoveSelected_Click(object? sender, EventArgs e)
    {
        if (_dataGridViewGames.SelectedRows.Count == 0)
        {
            return;
        }

        var rows = _dataGridViewGames.SelectedRows.Cast<DataGridViewRow>()
            .OrderByDescending(static row => row.Index)
            .ToList();

        foreach (var row in rows)
        {
            if (row.DataBoundItem is GameEntryModel game)
            {
                _games.Remove(game);
            }
        }

        SaveCurrentGameList();
        RefreshBuildCapacitySummary();
        AutoAdjustGameGridColumns();
    }

    private void buttonClearAll_Click(object? sender, EventArgs e)
    {
        if (_games.Count == 0)
        {
            return;
        }

        _games.Clear();
        _dataGridViewGames.ClearSelection();
        SaveCurrentGameList();
        RefreshBuildCapacitySummary();
        AutoAdjustGameGridColumns();
    }

    private void buttonMoveUp_Click(object? sender, EventArgs e) => MoveSelectedRow(-1);

    private void buttonMoveDown_Click(object? sender, EventArgs e) => MoveSelectedRow(1);

    private async void buttonViewConfigArea_Click(object? sender, EventArgs e)
    {
        if (_gamesLoading)
        {
            return;
        }

        BuildRequest request;
        try
        {
            request = CreateBuildRequest(requireEnabledGames: false);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                T("dialog", "build_settings_error_title", "Build Settings Error"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        try
        {
            UseWaitCursor = true;
            var preview = await Task.Run(() => _builder.CreateConfigurationPreview(request, T));
            ShowConfigPreviewDialog(preview);
        }
        catch (Exception ex)
        {
            AppendLog(Tf("log", "config_preview_failed", "Config area preview failed: {0}", ex.Message));
            MessageBox.Show(
                this,
                ex.ToString(),
                T("dialog", "config_preview_failed_title", "View Config Area Failed"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private async void buttonPreviewMenu_Click(object? sender, EventArgs e)
    {
        if (_gamesLoading)
        {
            return;
        }

        BuildRequest request;
        try
        {
            request = CreateBuildRequest(requireEnabledGames: false);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                T("dialog", "build_settings_error_title", "Build Settings Error"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        if (request.Family == CompilationFamily.Gba &&
            request.GbaMenuType is GbaMenuType.NsMenu or GbaMenuType.SimplifiedNsMenu)
        {
            var games = _games.Where(game => !game.IsLoadingPlaceholder).ToList();
            if (games.Count == 0)
            {
                MessageBox.Show(
                    this,
                    T("error", "add_one_rom", "Add at least one ROM before building."),
                    T("dialog", "menu_preview_failed_title", "Preview Menu Failed"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            using var coverEditor = new GbaMenuCoverEditorDialog(
                games,
                request.GbaMenuType,
                request.BatteryPresent,
                T,
                request.BackgroundPath);
            if (coverEditor.ShowDialog(this) == DialogResult.OK)
            {
                _games.ResetBindings();
                SaveCurrentGameList();
            }
            return;
        }

        try
        {
            UseWaitCursor = true;
            var preview = await Task.Run(() => _builder.CreateMenuPreview(request, T));
            ShowMenuPreviewDialog(preview);
        }
        catch (Exception ex)
        {
            AppendLog(Tf("log", "menu_preview_failed", "Menu preview failed: {0}", ex.Message));
            MessageBox.Show(
                this,
                ex.ToString(),
                T("dialog", "menu_preview_failed_title", "Preview Menu Failed"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private async void buttonExportGbcFont_Click(object? sender, EventArgs e)
    {
        if (_gamesLoading)
        {
            return;
        }

        BuildRequest request;
        try
        {
            request = CreateBuildRequest(requireEnabledGames: false);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                T("dialog", "build_settings_error_title", "Build Settings Error"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        if (request.Games.All(static game => !game.Enabled))
        {
            MessageBox.Show(
                this,
                T("error", "add_one_rom", "Add at least one ROM before building."),
                T("dialog", "export_gbc_font_failed_title", "Export GBC Font Failed"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        if (request.GbcFontPath is null)
        {
            MessageBox.Show(
                this,
                T("error", "gbc_font_source_required", "Select a built-in GBC font first."),
                T("dialog", "export_gbc_font_failed_title", "Export GBC Font Failed"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        if (_currentCompilationFamily != CompilationFamily.Gbc)
        {
            MessageBox.Show(
                this,
                T("error", "gbc_font_export_only_for_gbc", "Font package export is only available for GBC MAX/MBCX builds."),
                T("dialog", "export_gbc_font_failed_title", "Export GBC Font Failed"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        var outputDirectory = GetCurrentOutputDirectory();
        Directory.CreateDirectory(outputDirectory);
        var suggestedFileName = Path.GetFileNameWithoutExtension(BuildManagedOutputPath(_textBoxOutputPath.Text));
        if (string.IsNullOrWhiteSpace(suggestedFileName))
        {
            suggestedFileName = "mbcxfnt";
        }

        using var dialog = new SaveFileDialog
        {
            Filter = T("file_dialog", "bin_filter", "Binary files (*.bin)|*.bin|All files (*.*)|*.*"),
            FileName = $"{suggestedFileName}_MBCXFNT.bin",
            InitialDirectory = outputDirectory,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            UseWaitCursor = true;
            var fontSection = await Task.Run(() => _builder.ExportGbcFontPackage(request, T));
            var characterList = await Task.Run(() => _builder.ExportGbcFontCharacterList(request, T));
            File.WriteAllBytes(dialog.FileName, fontSection.Data);
            File.WriteAllText(
                Path.ChangeExtension(dialog.FileName, ".txt"),
                fontSection.Details,
                Encoding.UTF8);
            File.WriteAllText(
                Path.Combine(
                    Path.GetDirectoryName(dialog.FileName) ?? string.Empty,
                    $"{Path.GetFileNameWithoutExtension(dialog.FileName)}.chars.txt"),
                characterList,
                Encoding.UTF8);
            AppendLog(Tf("log", "export_gbc_font_finished", "Exported GBC font package: {0}", dialog.FileName));
            MessageBox.Show(
                this,
                Tf("dialog", "export_gbc_font_finished_message", "Font package exported successfully.\n\n{0}", dialog.FileName),
                T("dialog", "export_gbc_font_finished_title", "Export GBC Font Finished"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            AppendLog(Tf("log", "export_gbc_font_failed", "GBC font export failed: {0}", ex.Message));
            MessageBox.Show(
                this,
                ex.ToString(),
                T("dialog", "export_gbc_font_failed_title", "Export GBC Font Failed"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private void ShowConfigPreviewDialog(ConfigPreviewReport preview)
    {
        var fullText = BuildConfigPreviewFullText(preview);
        using var dialog = new DpiAwareForm
        {
            StartPosition = FormStartPosition.CenterParent,
            Size = new Size(1080, 760),
            MinimumSize = new Size(820, 560),
            Text = Tf(
                "dialog",
                "config_preview_title",
                "{0} Config Area",
                _currentCompilationFamily == CompilationFamily.Gbc ? "GBC" : "GBA"),
            ShowInTaskbar = false,
        };

        var tabControl = new TabControl
        {
            Dock = DockStyle.Fill,
        };

        var summaryPage = new TabPage(T("ui", "config_preview_summary", "Summary"))
        {
            Tag = preview.Summary,
        };
        var summaryTextBox = CreateConfigPreviewTextBox();
        summaryTextBox.Text = preview.Summary;
        summaryPage.Controls.Add(summaryTextBox);
        tabControl.TabPages.Add(summaryPage);

        foreach (var section in preview.Sections)
        {
            var sectionText = BuildConfigPreviewSectionText(section);
            var page = new TabPage(section.Name)
            {
                Tag = sectionText,
            };
            var textBox = CreateConfigPreviewTextBox();
            textBox.Text = sectionText;
            page.Controls.Add(textBox);
            tabControl.TabPages.Add(page);
        }

        var buttonCopy = MakeButton(T("ui", "copy_button", "Copy"), (_, _) => Clipboard.SetText(fullText), 100);
        var buttonClose = MakeButton(T("ui", "close_button", "Close"), (_, _) => dialog.Close(), 100);
        dialog.AcceptButton = buttonClose;

        var buttonLayout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0),
        };
        buttonLayout.Controls.Add(buttonClose);
        buttonLayout.Controls.Add(buttonCopy);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(10),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
        layout.Controls.Add(tabControl, 0, 0);
        layout.Controls.Add(buttonLayout, 0, 1);

        dialog.Controls.Add(layout);
        dialog.ShowDialog(this);
    }

    private void ShowMenuPreviewDialog(MenuPreviewModel preview)
    {
        if (preview.Family == CompilationFamily.Gba &&
            preview.BackgroundBitmapData is { Length: BackgroundImageEncoder.Width * BackgroundImageEncoder.Height } bitmapData &&
            preview.BackgroundPaletteData is { Length: 0x200 } paletteData)
        {
            ShowGbaBackgroundPreviewDialog(bitmapData, paletteData);
            return;
        }

        var selectedIndex = Math.Clamp(preview.SelectedIndex, 0, Math.Max(0, preview.Entries.Count - 1));
        var currentPage = preview.Entries.Count == 0 ? 0 : preview.Entries[selectedIndex].PageIndex;
        var showDetailPage = false;

        using var dialog = new DpiAwareForm
        {
            StartPosition = FormStartPosition.CenterParent,
            Size = new Size(1120, 780),
            MinimumSize = new Size(900, 620),
            Text = Tf("dialog", "menu_preview_title", "{0} Menu Preview", preview.Family == CompilationFamily.Gbc ? "GBC" : "GBA"),
            ShowInTaskbar = false,
            KeyPreview = true,
        };

        var summaryTextBox = CreateConfigPreviewTextBox();
        summaryTextBox.Dock = DockStyle.Top;
        summaryTextBox.Height = 92;
        summaryTextBox.Text = preview.Summary;

        var canvas = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(227, 229, 235),
            BorderStyle = BorderStyle.FixedSingle,
        };

        void RefreshPreview()
        {
            if (preview.Entries.Count == 0)
            {
                selectedIndex = 0;
                currentPage = 0;
                showDetailPage = false;
            }
            else
            {
                selectedIndex = Math.Clamp(selectedIndex, 0, preview.Entries.Count - 1);
                currentPage = preview.Entries[selectedIndex].PageIndex;
            }

            canvas.Invalidate();
        }

        canvas.Paint += (_, args) =>
        {
            DrawMenuPreviewCanvas(args.Graphics, canvas.ClientRectangle, preview, currentPage, selectedIndex, showDetailPage);
        };

        dialog.KeyDown += (_, args) =>
        {
            if (preview.Entries.Count == 0)
            {
                return;
            }

            switch (args.KeyCode)
            {
                case Keys.Up:
                    selectedIndex = Math.Max(0, selectedIndex - 1);
                    showDetailPage = false;
                    RefreshPreview();
                    args.Handled = true;
                    break;
                case Keys.Down:
                    selectedIndex = Math.Min(preview.Entries.Count - 1, selectedIndex + 1);
                    showDetailPage = false;
                    RefreshPreview();
                    args.Handled = true;
                    break;
                case Keys.Left:
                    currentPage = Math.Max(0, currentPage - 1);
                    selectedIndex = preview.Entries.FirstOrDefault(entry => entry.PageIndex == currentPage)?.Index ?? selectedIndex;
                    showDetailPage = false;
                    RefreshPreview();
                    args.Handled = true;
                    break;
                case Keys.Right:
                    currentPage = Math.Min(preview.Entries.Count == 0 ? 0 : preview.Entries.Max(entry => entry.PageIndex), currentPage + 1);
                    selectedIndex = preview.Entries.FirstOrDefault(entry => entry.PageIndex == currentPage)?.Index ?? selectedIndex;
                    showDetailPage = false;
                    RefreshPreview();
                    args.Handled = true;
                    break;
                case Keys.Enter:
                case Keys.Space:
                    showDetailPage = !showDetailPage;
                    RefreshPreview();
                    args.Handled = true;
                    break;
            }
        };

        var buttonPrev = MakeButton(T("ui", "preview_prev_page", "Prev Page"), (_, _) =>
        {
            currentPage = Math.Max(0, currentPage - 1);
            var firstEntry = preview.Entries.FirstOrDefault(entry => entry.PageIndex == currentPage);
            if (firstEntry is not null)
            {
                selectedIndex = firstEntry.Index;
            }
            showDetailPage = false;
            RefreshPreview();
        }, 110);
        var buttonNext = MakeButton(T("ui", "preview_next_page", "Next Page"), (_, _) =>
        {
            var maxPage = preview.Entries.Count == 0 ? 0 : preview.Entries.Max(entry => entry.PageIndex);
            currentPage = Math.Min(maxPage, currentPage + 1);
            var firstEntry = preview.Entries.FirstOrDefault(entry => entry.PageIndex == currentPage);
            if (firstEntry is not null)
            {
                selectedIndex = firstEntry.Index;
            }
            showDetailPage = false;
            RefreshPreview();
        }, 110);
        var buttonToggle = MakeButton(T("ui", "preview_toggle_detail", "Toggle Detail"), (_, _) =>
        {
            showDetailPage = !showDetailPage;
            RefreshPreview();
        }, 120);
        var buttonClose = MakeButton(T("ui", "close_button", "Close"), (_, _) => dialog.Close(), 100);

        var buttonLayout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0),
        };
        buttonLayout.Controls.Add(buttonClose);
        buttonLayout.Controls.Add(buttonToggle);
        buttonLayout.Controls.Add(buttonNext);
        buttonLayout.Controls.Add(buttonPrev);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(10),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
        layout.Controls.Add(summaryTextBox, 0, 0);
        layout.Controls.Add(canvas, 0, 1);
        layout.Controls.Add(buttonLayout, 0, 2);

        dialog.Controls.Add(layout);
        RefreshPreview();
        dialog.ShowDialog(this);
    }

    private void ShowGbaBackgroundPreviewDialog(byte[] bitmapData, byte[] paletteData)
    {
        using var previewImage = DecodeGbaMenuBackground(bitmapData, paletteData);
        using var dialog = new DpiAwareForm
        {
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(960, 640),
            MinimumSize = new Size(540, 420),
            Text = T("dialog", "menu_background_preview_title", "GBA Menu Background Preview"),
            ShowInTaskbar = false,
        };

        var pictureBox = new NearestNeighborPictureBox
        {
            Dock = DockStyle.Fill,
            BackColor = SystemColors.Control,
            SizeMode = PictureBoxSizeMode.Zoom,
            Image = previewImage,
        };
        var buttonClose = MakeButton(T("ui", "close_button", "Close"), (_, _) => dialog.Close(), 100);
        var buttonLayout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(8, 5, 8, 5),
            Margin = Padding.Empty,
        };
        buttonLayout.Controls.Add(buttonClose);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46F));
        layout.Controls.Add(pictureBox, 0, 0);
        layout.Controls.Add(buttonLayout, 0, 1);
        dialog.Controls.Add(layout);
        dialog.ShowDialog(this);
        pictureBox.Image = null;
    }

    private static Bitmap DecodeGbaMenuBackground(byte[] bitmapData, byte[] paletteData)
    {
        var bitmap = new Bitmap(BackgroundImageEncoder.Width, BackgroundImageEncoder.Height);
        for (var y = 0; y < BackgroundImageEncoder.Height; y++)
        {
            var rowOffset = y * BackgroundImageEncoder.Width;
            for (var x = 0; x < BackgroundImageEncoder.Width; x++)
            {
                var paletteIndex = bitmapData[rowOffset + x];
                var paletteOffset = paletteIndex * 2;
                var rgb555 = (ushort)(paletteData[paletteOffset] | (paletteData[paletteOffset + 1] << 8));
                bitmap.SetPixel(
                    x,
                    y,
                    Color.FromArgb(
                        ExpandRgb555Component(rgb555 & 0x1F),
                        ExpandRgb555Component((rgb555 >> 5) & 0x1F),
                        ExpandRgb555Component((rgb555 >> 10) & 0x1F)));
            }
        }

        return bitmap;
    }

    private static int ExpandRgb555Component(int value) => (value << 3) | (value >> 2);

    private static RichTextBox CreateConfigPreviewTextBox() =>
        new()
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            WordWrap = false,
            DetectUrls = false,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = SystemColors.Window,
            Font = new Font(FontFamily.GenericMonospace, 9F),
        };

    private static string BuildConfigPreviewFullText(ConfigPreviewReport preview)
    {
        var builder = new StringBuilder();
        builder.AppendLine(preview.Title);
        builder.AppendLine(new string('=', preview.Title.Length));
        builder.AppendLine();
        builder.AppendLine(preview.Summary.TrimEnd());
        foreach (var section in preview.Sections)
        {
            builder.AppendLine();
            builder.AppendLine(BuildConfigPreviewSectionText(section));
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildConfigPreviewSectionText(ConfigPreviewSection section)
    {
        var builder = new StringBuilder();
        builder.AppendLine(section.Name);
        builder.AppendLine(new string('-', section.Name.Length));
        builder.AppendLine($"Offset: 0x{section.Offset:X8}");
        builder.AppendLine($"Length: 0x{section.Data.Length:X} ({section.Data.Length} bytes)");
        if (!string.IsNullOrWhiteSpace(section.Details))
        {
            builder.AppendLine();
            builder.AppendLine(section.Details.TrimEnd());
        }

        builder.AppendLine();
        builder.AppendLine("Hex Dump:");
        builder.AppendLine(FormatHexDump(section.Data, section.Offset));
        return builder.ToString().TrimEnd();
    }

    private static string FormatHexDump(IReadOnlyList<byte> data, int baseOffset)
    {
        if (data.Count == 0)
        {
            return "(No raw bytes)";
        }

        var builder = new StringBuilder();
        for (var offset = 0; offset < data.Count; offset += 16)
        {
            var lineLength = Math.Min(16, data.Count - offset);
            builder.Append($"{baseOffset + offset:X8}  ");
            for (var index = 0; index < 16; index++)
            {
                if (index < lineLength)
                {
                    builder.Append($"{data[offset + index]:X2} ");
                }
                else
                {
                    builder.Append("   ");
                }

                if (index == 7)
                {
                    builder.Append(' ');
                }
            }

            builder.Append(" |");
            for (var index = 0; index < lineLength; index++)
            {
                var value = data[offset + index];
                builder.Append(value is >= 0x20 and <= 0x7E ? (char)value : '.');
            }

            builder.Append('|');
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static void DrawMenuPreviewCanvas(
        Graphics graphics,
        Rectangle bounds,
        MenuPreviewModel preview,
        int currentPage,
        int selectedIndex,
        bool showDetailPage)
    {
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        graphics.Clear(Color.FromArgb(221, 225, 232));

        var screenWidth = 160;
        var screenHeight = 144;
        var scale = Math.Max(2, Math.Min(bounds.Width / screenWidth, bounds.Height / screenHeight));
        var screenRect = new Rectangle(
            bounds.Left + Math.Max(0, (bounds.Width - (screenWidth * scale)) / 2),
            bounds.Top + Math.Max(0, (bounds.Height - (screenHeight * scale)) / 2),
            screenWidth * scale,
            screenHeight * scale);

        using var outerBrush = new SolidBrush(Color.FromArgb(50, 62, 77));
        using var screenBrush = new SolidBrush(Color.FromArgb(246, 248, 243));
        using var accentBrush = new SolidBrush(Color.FromArgb(40, 71, 110));
        using var selectedBrush = new SolidBrush(Color.FromArgb(208, 226, 245));
        using var mutedBrush = new SolidBrush(Color.FromArgb(112, 122, 134));
        using var textBrush = new SolidBrush(Color.FromArgb(28, 34, 39));
        using var borderPen = new Pen(Color.FromArgb(90, 102, 118), Math.Max(1, scale / 4F));

        graphics.FillRectangle(outerBrush, screenRect.X - 24, screenRect.Y - 24, screenRect.Width + 48, screenRect.Height + 48);
        graphics.FillRectangle(screenBrush, screenRect);
        graphics.DrawRectangle(borderPen, screenRect);

        using var titleFont = new Font("Segoe UI", Math.Max(8F, scale * 1.35F), FontStyle.Bold, GraphicsUnit.Pixel);
        using var bodyFont = new Font("Segoe UI", Math.Max(7F, scale * 1.05F), FontStyle.Regular, GraphicsUnit.Pixel);
        using var monoFont = new Font(FontFamily.GenericMonospace, Math.Max(7F, scale * 0.95F), FontStyle.Regular, GraphicsUnit.Pixel);

        if (preview.Family == CompilationFamily.Gbc)
        {
            DrawGbcPixelPreview(graphics, screenRect, preview, currentPage, selectedIndex, showDetailPage, scale);
        }
        else
        {
            DrawPreviewHeader(graphics, screenRect, preview, currentPage, showDetailPage, accentBrush, textBrush, titleFont, bodyFont);
            DrawPreviewListPage(graphics, screenRect, preview, currentPage, selectedIndex, textBrush, mutedBrush, selectedBrush, bodyFont, monoFont, scale);
        }
    }

    private static void DrawGbcPixelPreview(
        Graphics graphics,
        Rectangle screenRect,
        MenuPreviewModel preview,
        int currentPage,
        int selectedIndex,
        bool showDetailPage,
        int scale)
    {
        var pixelBounds = new Rectangle(screenRect.X, screenRect.Y, screenRect.Width, screenRect.Height);
        using var backgroundBrush = new SolidBrush(Color.FromArgb(247, 248, 243));
        using var textBrush = new SolidBrush(Color.FromArgb(25, 31, 37));
        using var mutedBrush = new SolidBrush(Color.FromArgb(108, 118, 128));
        using var selectedBrush = new SolidBrush(Color.FromArgb(214, 228, 243));
        using var asciiFont = new Font(FontFamily.GenericMonospace, Math.Max(8F, scale * 1.15F), FontStyle.Regular, GraphicsUnit.Pixel);
        using var asciiFontBold = new Font(FontFamily.GenericMonospace, Math.Max(8F, scale * 1.15F), FontStyle.Bold, GraphicsUnit.Pixel);
        using var detailFont = new Font(FontFamily.GenericMonospace, Math.Max(8F, scale * 1.05F), FontStyle.Regular, GraphicsUnit.Pixel);
        const int uiMarginPx = 8;
        const int topBarTextY = 2;
        const int bottomBarTextY = 132;
        const int rowTopStartPx = 24;
        const int rowStepPx = 12;
        const int menuIndexX = 28;
        const int menuNameX = 52;

        graphics.FillRectangle(backgroundBrush, pixelBounds);
        graphics.DrawRectangle(Pens.Black, pixelBounds);

        if (!string.Equals(preview.LanguageCode, LanguageService.DefaultLanguageCode, StringComparison.OrdinalIgnoreCase))
        {
            DrawGbcEnglishTextPreview(graphics, screenRect, preview, currentPage, selectedIndex, showDetailPage, scale, textBrush, mutedBrush, selectedBrush, asciiFont, asciiFontBold);
            return;
        }

        if (showDetailPage)
        {
            DrawGbcDetailPage(graphics, screenRect, preview, selectedIndex, scale, textBrush, mutedBrush, asciiFont, asciiFontBold, detailFont);
            return;
        }

        var pageCount = preview.Entries.Count == 0 ? 1 : preview.Entries.Max(entry => entry.PageIndex) + 1;
        var previewGlyphs = preview.Entries.FirstOrDefault()?.GlyphBitmaps;
        var headerText = GbcMenuLayerBuilder.PreviewHeaderText;
        var footerText = GbcMenuLayerBuilder.PreviewFooterText;
        var versionText = GbcMenuLayerBuilder.PreviewVersionText;
        var versionX = 160 - uiMarginPx - MeasureGbcCompactTextWidth(previewGlyphs, preview.FontSizePx, versionText);
        var pageText = $"{currentPage + 1:D2}/{pageCount:D2}";
        var pageTextX = 160 - uiMarginPx - MeasureGbcCompactTextWidth(previewGlyphs, preview.FontSizePx, pageText);

        DrawGbcCompactText(graphics, previewGlyphs, preview.FontSizePx, headerText, screenRect.X + (uiMarginPx * scale), screenRect.Y + (topBarTextY * scale), scale, asciiFontBold, textBrush);
        DrawGbcCompactText(graphics, previewGlyphs, preview.FontSizePx, versionText, screenRect.X + (versionX * scale), screenRect.Y + (topBarTextY * scale), scale, asciiFont, mutedBrush);
        DrawGbcCompactText(graphics, previewGlyphs, preview.FontSizePx, pageText, screenRect.X + (pageTextX * scale), screenRect.Y + (bottomBarTextY * scale), scale, asciiFont, mutedBrush);
        DrawGbcCompactText(graphics, previewGlyphs, preview.FontSizePx, footerText, screenRect.X + (uiMarginPx * scale), screenRect.Y + (bottomBarTextY * scale), scale, asciiFont, mutedBrush);

        var entries = preview.Entries.Where(entry => entry.PageIndex == currentPage).OrderBy(entry => entry.RowIndex).ToList();
        if (entries.Count == 0)
        {
            DrawGbcCompactText(graphics, previewGlyphs, preview.FontSizePx, "No Game Found!", screenRect.X + (menuNameX * scale), screenRect.Y + (rowTopStartPx * scale), scale, asciiFont, textBrush);
            DrawGbcCompactText(graphics, previewGlyphs, preview.FontSizePx, "Check ChisMBCX", screenRect.X + (menuNameX * scale), screenRect.Y + ((rowTopStartPx + rowStepPx) * scale), scale, asciiFont, textBrush);
            return;
        }

        var selectedEntry = preview.Entries[Math.Clamp(selectedIndex, 0, preview.Entries.Count - 1)];
        var arrowPageIndex = selectedEntry.PageIndex == currentPage ? selectedEntry.RowIndex : 0;
        DrawGbcArrow(graphics, screenRect, arrowPageIndex, rowTopStartPx, rowStepPx, scale, textBrush);

        foreach (var entry in entries)
        {
            var rowTopPx = rowTopStartPx + (entry.RowIndex * rowStepPx);
            var limitedMainTitle = GbcMenuLayerBuilder.LimitPreviewMainMenuTitle(entry.DisplayTitle);
            if (entry.Index == selectedIndex)
            {
                graphics.FillRectangle(
                    selectedBrush,
                    screenRect.X,
                    screenRect.Y + (rowTopPx * scale),
                    160 * scale,
                    Math.Max(1, rowStepPx * scale));
            }

            DrawGbcMenuIndexText(graphics, entry.GlyphBitmaps, entry.GlyphSizePx, entry.Index + 1, screenRect.X + (menuIndexX * scale), screenRect.Y + (rowTopPx * scale), scale, asciiFont, textBrush);
            if (string.Equals(preview.LanguageCode, LanguageService.DefaultLanguageCode, StringComparison.OrdinalIgnoreCase))
            {
                DrawGbcCompactText(graphics, entry.GlyphBitmaps, entry.GlyphSizePx, limitedMainTitle, screenRect.X + (menuNameX * scale), screenRect.Y + (rowTopPx * scale), scale, asciiFont, textBrush);
            }
            else
            {
                DrawGbcCompactText(graphics, entry.GlyphBitmaps, entry.GlyphSizePx, limitedMainTitle, screenRect.X + (menuNameX * scale), screenRect.Y + (rowTopPx * scale), scale, asciiFont, textBrush);
            }
        }
    }

    private void buttonOpenGbcFontFolder_Click(object? sender, EventArgs e)
    {
        try
        {
            OpenBuiltInGbcFontFolder();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                T("dialog", "open_output_folder_failed_title", "Open Output Folder Failed"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private static void DrawGbcEnglishTextPreview(
        Graphics graphics,
        Rectangle screenRect,
        MenuPreviewModel preview,
        int currentPage,
        int selectedIndex,
        bool showDetailPage,
        int scale,
        Brush textBrush,
        Brush mutedBrush,
        Brush selectedBrush,
        Font asciiFont,
        Font asciiFontBold)
    {
        var glyphs = GbcFontService.GetGbdkDefaultAsciiGlyphs();
        if (showDetailPage)
        {
            DrawGbcEnglishDetailPreview(graphics, screenRect, preview, selectedIndex, scale, textBrush, mutedBrush, glyphs);
            return;
        }

        var pageCount = preview.Entries.Count == 0 ? 1 : preview.Entries.Max(entry => entry.PageIndex) + 1;
        DrawGbdkAsciiText(graphics, screenRect, glyphs, "MORI MENU EX", 32, 0, scale, textBrush);
        DrawGbdkAsciiText(graphics, screenRect, glyphs, "Ver1.04", 8, 136, scale, mutedBrush);
        DrawGbdkAsciiText(graphics, screenRect, glyphs, $"{currentPage + 1:D2}/{pageCount:D2}", 112, 136, scale, mutedBrush);

        var entries = preview.Entries.Where(entry => entry.PageIndex == currentPage).OrderBy(entry => entry.RowIndex).ToList();
        if (entries.Count == 0)
        {
            DrawGbdkAsciiText(graphics, screenRect, glyphs, "No Game Found!", 24, 64, scale, textBrush);
            DrawGbdkAsciiText(graphics, screenRect, glyphs, "Check ChisMBCX", 24, 72, scale, textBrush);
            return;
        }

        foreach (var entry in entries)
        {
            var rowTopPx = 8 + (entry.RowIndex * 8);
            if (entry.Index == selectedIndex)
            {
                graphics.FillRectangle(
                    selectedBrush,
                    screenRect.X,
                    screenRect.Y + (rowTopPx * scale),
                    screenRect.Width,
                    8 * scale);
            }

            DrawGbdkAsciiText(graphics, screenRect, glyphs, $"{entry.Index + 1:D2}.", 16, rowTopPx, scale, textBrush);
            DrawGbdkAsciiText(graphics, screenRect, glyphs, TrimGbcTextLine(entry.DisplayTitle, 15), 40, rowTopPx, scale, textBrush);
        }
    }

    private static void DrawGbcEnglishDetailPreview(
        Graphics graphics,
        Rectangle screenRect,
        MenuPreviewModel preview,
        int selectedIndex,
        int scale,
        Brush textBrush,
        Brush mutedBrush,
        IReadOnlyDictionary<ushort, byte[]> glyphs)
    {
        if (preview.Entries.Count == 0)
        {
            DrawGbdkAsciiText(graphics, screenRect, glyphs, "NO GAME", 48, 64, scale, textBrush);
            return;
        }

        var entry = preview.Entries[Math.Clamp(selectedIndex, 0, preview.Entries.Count - 1)];
        DrawGbdkAsciiText(graphics, screenRect, glyphs, "GAME DETAILS", 32, 0, scale, textBrush);
        DrawGbdkAsciiText(graphics, screenRect, glyphs, "Index:", 8, 16, scale, textBrush);
        DrawGbdkAsciiText(graphics, screenRect, glyphs, $"{entry.Index + 1:D2}", 16, 24, scale, textBrush);
        DrawGbdkAsciiText(graphics, screenRect, glyphs, "Name:", 8, 32, scale, textBrush);
        DrawGbdkAsciiText(graphics, screenRect, glyphs, TrimGbcTextLine(entry.DisplayTitle, 15), 16, 40, scale, textBrush);
        DrawGbdkAsciiText(graphics, screenRect, glyphs, "MBC:", 8, 48, scale, textBrush);
        DrawGbdkAsciiText(graphics, screenRect, glyphs, TrimGbcTextLine(entry.DetailTypeText, 15), 16, 56, scale, textBrush);
        DrawGbdkAsciiText(graphics, screenRect, glyphs, "ROM:", 8, 64, scale, textBrush);
        DrawGbdkAsciiText(graphics, screenRect, glyphs, TrimGbcTextLine(entry.DetailRomText, 15), 16, 72, scale, textBrush);
        DrawGbdkAsciiText(graphics, screenRect, glyphs, "RAM:", 8, 80, scale, textBrush);
        DrawGbdkAsciiText(graphics, screenRect, glyphs, TrimGbcTextLine(entry.DetailRamText, 15), 16, 88, scale, textBrush);
        DrawGbdkAsciiText(graphics, screenRect, glyphs, "BANK:", 8, 96, scale, textBrush);
        DrawGbdkAsciiText(graphics, screenRect, glyphs, TrimGbcTextLine(entry.DetailBankText, 15), 16, 104, scale, textBrush);
        DrawGbdkAsciiText(graphics, screenRect, glyphs, "A:Enter", 8, 128, scale, mutedBrush);
        DrawGbdkAsciiText(graphics, screenRect, glyphs, "B:Back", 96, 128, scale, mutedBrush);
        DrawGbdkAsciiText(graphics, screenRect, glyphs, "MORI MENU", 8, 136, scale, mutedBrush);
        DrawGbdkAsciiText(graphics, screenRect, glyphs, $"{entry.Index + 1:D2}/{preview.Entries.Count}", 112, 136, scale, mutedBrush);
    }

    private static void DrawGbdkAsciiText(
        Graphics graphics,
        Rectangle screenRect,
        IReadOnlyDictionary<ushort, byte[]> glyphs,
        string text,
        int xPx,
        int yPx,
        int scale,
        Brush brush)
    {
        for (var i = 0; i < text.Length && xPx + (i * 8) < 160; i++)
        {
            if (glyphs.TryGetValue(text[i], out var glyph))
            {
                DrawGlyphBitmapExact(graphics, glyph, screenRect.X + ((xPx + (i * 8)) * scale), screenRect.Y + (yPx * scale), 8, scale, 8);
            }
        }
    }

    private static string TrimGbcTextLine(string text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "UNKNOWN";
        }

        var normalized = text.Trim();
        return normalized.Length <= maxChars ? normalized : normalized[..maxChars];
    }

    private static void DrawPreviewHeader(
        Graphics graphics,
        Rectangle screenRect,
        MenuPreviewModel preview,
        int currentPage,
        bool showDetailPage,
        Brush accentBrush,
        Brush textBrush,
        Font titleFont,
        Font bodyFont)
    {
        var headerRect = new Rectangle(screenRect.X, screenRect.Y, screenRect.Width, Math.Max(28, screenRect.Height / 7));
        graphics.FillRectangle(accentBrush, headerRect);
        graphics.DrawString(
            preview.Family == CompilationFamily.Gbc ? "MBCX MENU PREVIEW" : "GBA MENU PREVIEW",
            titleFont,
            Brushes.White,
            headerRect.X + 10,
            headerRect.Y + 7);

        var pageCount = preview.Entries.Count == 0 ? 1 : preview.Entries.Max(entry => entry.PageIndex) + 1;
        var modeText = showDetailPage ? "DETAIL" : "LIST";
        var info = $"P {currentPage + 1}/{pageCount}  {modeText}  {preview.LanguageCode}  {preview.FontSizePx}px";
        graphics.DrawString(info, bodyFont, Brushes.WhiteSmoke, headerRect.Right - 210, headerRect.Y + 10);
    }

    private static void DrawPreviewListPage(
        Graphics graphics,
        Rectangle screenRect,
        MenuPreviewModel preview,
        int currentPage,
        int selectedIndex,
        Brush textBrush,
        Brush mutedBrush,
        Brush selectedBrush,
        Font bodyFont,
        Font monoFont,
        int scale)
    {
        var entries = preview.Entries.Where(entry => entry.PageIndex == currentPage).OrderBy(entry => entry.RowIndex).ToList();
        if (entries.Count == 0)
        {
            graphics.DrawString("No entries on this page.", bodyFont, mutedBrush, screenRect.X + 12, screenRect.Y + 42);
            return;
        }

        var top = screenRect.Y + Math.Max(34, screenRect.Height / 5);
        var rowHeight = preview.Family == CompilationFamily.Gbc
            ? Math.Max(16, (int)Math.Round(preview.FontSizePx * scale / 3.0))
            : Math.Max(12, screenRect.Height / 18);
        var numberWidth = Math.Max(26, scale * 14);
        var titleX = screenRect.X + scale * 18;

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var rowRect = new Rectangle(screenRect.X + 8, top + (i * rowHeight), screenRect.Width - 16, rowHeight - 1);
            if (entry.Index == selectedIndex)
            {
                graphics.FillRectangle(selectedBrush, rowRect);
            }

            var number = $"{entry.Index + 1:D2}.";
            graphics.DrawString(number, monoFont, textBrush, screenRect.X + 10, rowRect.Y + 2);
            if (preview.Family == CompilationFamily.Gbc &&
                string.Equals(preview.LanguageCode, LanguageService.DefaultLanguageCode, StringComparison.OrdinalIgnoreCase))
            {
                DrawPreviewEncodedTitle(
                    graphics,
                    entry,
                    titleX,
                    rowRect.Y + 2,
                    preview.FontSizePx,
                    scale,
                    textBrush,
                    mutedBrush);
            }
            else
            {
                var titleText = TrimPreviewText(entry.DisplayTitle, Math.Max(8, (screenRect.Width - titleX - 12) / Math.Max(8, scale * 2)));
                graphics.DrawString(titleText, bodyFont, textBrush, titleX, rowRect.Y + 2);
            }
        }
    }

    private static void DrawPreviewDetailPage(
        Graphics graphics,
        Rectangle screenRect,
        MenuPreviewModel preview,
        int selectedIndex,
        Brush textBrush,
        Brush mutedBrush,
        Brush accentBrush,
        Font bodyFont,
        Font titleFont,
        int scale)
    {
        if (preview.Entries.Count == 0)
        {
            graphics.DrawString("No entry selected.", bodyFont, mutedBrush, screenRect.X + 12, screenRect.Y + 42);
            return;
        }

        var selectedEntry = preview.Entries[Math.Clamp(selectedIndex, 0, preview.Entries.Count - 1)];
        var cardRect = new Rectangle(screenRect.X + 10, screenRect.Y + 38, screenRect.Width - 20, screenRect.Height - 48);
        graphics.FillRectangle(Brushes.WhiteSmoke, cardRect);
        graphics.DrawRectangle(Pens.Silver, cardRect);

        graphics.FillRectangle(accentBrush, cardRect.X, cardRect.Y, cardRect.Width, Math.Max(24, scale * 8));
        graphics.DrawString($"#{selectedEntry.Index + 1:D2}", titleFont, Brushes.White, cardRect.X + 8, cardRect.Y + 4);

        var titleY = cardRect.Y + Math.Max(34, scale * 11);
        if (preview.Family == CompilationFamily.Gbc &&
            string.Equals(preview.LanguageCode, LanguageService.DefaultLanguageCode, StringComparison.OrdinalIgnoreCase))
        {
            DrawPreviewEncodedTitle(
                graphics,
                selectedEntry,
                cardRect.X + 10,
                titleY,
                preview.FontSizePx,
                scale,
                textBrush,
                mutedBrush);
        }
        else
        {
            graphics.DrawString(selectedEntry.DisplayTitle, titleFont, textBrush, cardRect.X + 10, titleY);
        }

        var infoY = titleY + Math.Max(22, preview.FontSizePx * scale / 2);
        graphics.DrawString($"Header: {selectedEntry.HeaderTitle}", bodyFont, textBrush, cardRect.X + 10, infoY);
        graphics.DrawString(selectedEntry.SecondaryInfo, bodyFont, textBrush, cardRect.X + 10, infoY + 22);
        graphics.DrawString(
            selectedEntry.UsesDedicatedSave ? "Save: Dedicated" : "Save: Shared",
            bodyFont,
            textBrush,
            cardRect.X + 10,
            infoY + 44);
        graphics.DrawString(
            "Keys: Up/Down select, Left/Right page, Enter detail",
            bodyFont,
            mutedBrush,
            cardRect.X + 10,
            cardRect.Bottom - 26);
    }

    private static void DrawGbcDetailPage(
        Graphics graphics,
        Rectangle screenRect,
        MenuPreviewModel preview,
        int selectedIndex,
        int scale,
        Brush textBrush,
        Brush mutedBrush,
        Font asciiFont,
        Font asciiFontBold,
        Font detailFont)
    {
        if (preview.Entries.Count == 0)
        {
            return;
        }

        var entry = preview.Entries[Math.Clamp(selectedIndex, 0, preview.Entries.Count - 1)];
        var limitedDetailTitle = GbcMenuLayerBuilder.LimitPreviewDetailTitle(entry.DisplayTitle);
        var versionText = GbcMenuLayerBuilder.PreviewVersionText;
        var versionX = GbcMenuLayerBuilder.PreviewScreenWidthPx - GbcMenuLayerBuilder.PreviewUiSideMarginPx - MeasureGbcCompactTextWidth(entry.GlyphBitmaps, entry.GlyphSizePx, versionText);
        DrawGbcCompactText(graphics, entry.GlyphBitmaps, entry.GlyphSizePx, GbcMenuLayerBuilder.PreviewDetailTitleText, screenRect.X + (GbcMenuLayerBuilder.PreviewUiSideMarginPx * scale), screenRect.Y + (GbcMenuLayerBuilder.PreviewTopBarTextY * scale), scale, asciiFontBold, textBrush);
        DrawGbcCompactText(graphics, entry.GlyphBitmaps, entry.GlyphSizePx, versionText, screenRect.X + (versionX * scale), screenRect.Y + (GbcMenuLayerBuilder.PreviewTopBarTextY * scale), scale, asciiFont, mutedBrush);

        var detailLabels = new[]
        {
            GbcMenuLayerBuilder.PreviewDetailIndexLabelText,
            GbcMenuLayerBuilder.PreviewDetailNameLabelText,
            GbcMenuLayerBuilder.PreviewDetailTypeLabelText,
            GbcMenuLayerBuilder.PreviewDetailRomLabelText,
            GbcMenuLayerBuilder.PreviewDetailRamLabelText,
            GbcMenuLayerBuilder.PreviewDetailSaveLabelText,
        };

        var detailValues = new[]
        {
            $"{entry.Index + 1:D2}",
            limitedDetailTitle,
            entry.DetailTypeText,
            entry.DetailRomText,
            entry.DetailRamText,
            entry.DetailSaveText,
        };

        for (var row = 0; row < detailLabels.Length; row++)
        {
            var rowY = GbcMenuLayerBuilder.PreviewDetailRowTopY + (row * GbcMenuLayerBuilder.PreviewDetailRowStepY);
            DrawGbcCompactText(graphics, entry.GlyphBitmaps, entry.GlyphSizePx, detailLabels[row], screenRect.X + (GbcMenuLayerBuilder.PreviewDetailLabelX * scale), screenRect.Y + (rowY * scale), scale, detailFont, textBrush);
            DrawGbcCompactText(graphics, entry.GlyphBitmaps, entry.GlyphSizePx, detailValues[row], screenRect.X + (GbcMenuLayerBuilder.PreviewDetailValueX * scale), screenRect.Y + (rowY * scale), scale, detailFont, textBrush);
        }

        var detailFooterText = GbcMenuLayerBuilder.PreviewDetailActionText;
        var detailPageText = $"{entry.Index + 1:D2}/{preview.Entries.Count:D2}";
        var detailPageTextX = GbcMenuLayerBuilder.PreviewScreenWidthPx - GbcMenuLayerBuilder.PreviewUiSideMarginPx - MeasureGbcCompactTextWidth(entry.GlyphBitmaps, entry.GlyphSizePx, detailPageText);
        DrawGbcCompactText(graphics, entry.GlyphBitmaps, entry.GlyphSizePx, detailFooterText, screenRect.X + (GbcMenuLayerBuilder.PreviewUiSideMarginPx * scale), screenRect.Y + (GbcMenuLayerBuilder.PreviewBottomBarTextY * scale), scale, asciiFont, mutedBrush);
        DrawGbcCompactText(graphics, entry.GlyphBitmaps, entry.GlyphSizePx, detailPageText, screenRect.X + (detailPageTextX * scale), screenRect.Y + (GbcMenuLayerBuilder.PreviewBottomBarTextY * scale), scale, asciiFont, mutedBrush);
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
        var rowTopPx = rowTopStartPx + (rowIndex * rowStepPx);
        var arrowY = rowTopPx + (rowStepPx / 2);
        var points = new[]
        {
            new Point(screenRect.X + (26 * scale), screenRect.Y + (arrowY * scale)),
            new Point(screenRect.X + (20 * scale), screenRect.Y + ((arrowY - 4) * scale)),
            new Point(screenRect.X + (20 * scale), screenRect.Y + ((arrowY + 4) * scale)),
        };
        graphics.FillPolygon(brush, points);
    }

    private static void DrawGbcNameSlice(
        Graphics graphics,
        Rectangle screenRect,
        MenuPreviewEntry entry,
        int xPx,
        int yPx,
        int sizePx,
        int visibleChars,
        int scale,
        Brush textBrush,
        Brush missingBrush,
        int codeOffset)
    {
        var codes = ResolveGbcVisibleCodes(entry);
        if (codes.Count == 0)
        {
            DrawPixelAscii(graphics, "UNKNOWN", screenRect.X + (xPx * scale), screenRect.Y + (yPx * scale), SystemFonts.DefaultFont, missingBrush);
            return;
        }

        var drawCount = Math.Min(visibleChars, Math.Max(0, codes.Count - codeOffset));
        for (var i = 0; i < drawCount; i++)
        {
            var code = codes[codeOffset + i];
            var drawXPx = xPx + (i * sizePx);
            if (entry.GlyphBitmaps is not null && entry.GlyphBitmaps.TryGetValue(code, out var glyphBitmap))
            {
                DrawGlyphBitmapExact(graphics, glyphBitmap, screenRect.X + (drawXPx * scale), screenRect.Y + (yPx * scale), sizePx, scale, entry.GlyphSizePx);
            }
            else if (code < 0x80)
            {
                using var asciiFont = new Font(FontFamily.GenericMonospace, Math.Max(8F, sizePx * scale * 0.9F), FontStyle.Regular, GraphicsUnit.Pixel);
                graphics.DrawString(((char)code).ToString(), asciiFont, textBrush, screenRect.X + (drawXPx * scale), screenRect.Y + (yPx * scale));
            }
            else
            {
                graphics.DrawRectangle(Pens.DarkRed, screenRect.X + (drawXPx * scale), screenRect.Y + (yPx * scale), sizePx * scale, sizePx * scale);
            }
        }
    }

    private static IReadOnlyList<ushort> ResolveGbcVisibleCodes(MenuPreviewEntry entry)
    {
        if (entry.EncodedNameCodes is null || entry.EncodedNameCodes.Count == 0)
        {
            return [];
        }

        var codes = new List<ushort>(entry.EncodedNameCodes.Count);
        foreach (var code in entry.EncodedNameCodes)
        {
            if (code == 0)
            {
                break;
            }

            codes.Add(code);
        }

        return codes;
    }

    private static int ResolveGlyphBitmapSizePx(IReadOnlyList<byte> glyphBitmap, int preferredSizePx)
    {
        if (preferredSizePx > 0)
        {
            var rowBytes = (preferredSizePx + 7) / 8;
            if (glyphBitmap.Count == preferredSizePx * rowBytes)
            {
                return preferredSizePx;
            }
        }

        if (glyphBitmap.Count <= 0)
        {
            return 0;
        }

        var pixelCount = glyphBitmap.Count * 8;
        var dimension = (int)Math.Sqrt(pixelCount);
        return dimension * dimension == pixelCount ? dimension : 0;
    }

    private static int ReadGlyphBitmapRow(IReadOnlyList<byte> glyphBitmap, int glyphSizePx, int row)
    {
        var rowBytes = Math.Max(1, glyphBitmap.Count / Math.Max(1, glyphSizePx));
        var rowOffset = row * rowBytes;
        var rowBits = 0;
        for (var index = 0; index < rowBytes && rowOffset + index < glyphBitmap.Count; index++)
        {
            rowBits = (rowBits << 8) | glyphBitmap[rowOffset + index];
        }

        var unusedBits = (rowBytes * 8) - glyphSizePx;
        if (unusedBits > 0)
        {
            rowBits >>= unusedBits;
        }

        return rowBits;
    }

    private static void DrawGlyphBitmapExact(Graphics graphics, IReadOnlyList<byte> glyphBitmap, int x, int y, int sizePx, int scale, int preferredGlyphSizePx)
    {
        var glyphSizePx = ResolveGlyphBitmapSizePx(glyphBitmap, preferredGlyphSizePx);
        if (glyphSizePx <= 0)
        {
            return;
        }

        using var pixelBrush = new SolidBrush(Color.FromArgb(25, 31, 37));
        for (var sy = 0; sy < glyphSizePx; sy++)
        {
            var rowBits = ReadGlyphBitmapRow(glyphBitmap, glyphSizePx, sy);
            var y0 = (sy * sizePx) / glyphSizePx;
            var y1 = Math.Max(y0 + 1, (((sy + 1) * sizePx) + (glyphSizePx - 1)) / glyphSizePx);
            for (var sx = 0; sx < glyphSizePx; sx++)
            {
                if ((rowBits & (1 << (glyphSizePx - 1 - sx))) == 0)
                {
                    continue;
                }

                var x0 = (sx * sizePx) / glyphSizePx;
                var x1 = Math.Max(x0 + 1, (((sx + 1) * sizePx) + (glyphSizePx - 1)) / glyphSizePx);
                graphics.FillRectangle(pixelBrush, x + (x0 * scale), y + (y0 * scale), Math.Max(1, (x1 - x0) * scale), Math.Max(1, (y1 - y0) * scale));
            }
        }
    }

    private static void DrawPixelAscii(Graphics graphics, string text, int x, int y, Font font, Brush brush)
    {
        graphics.DrawString(text, font, brush, x, y);
    }

    private static string ExtractGbcInfoValue(string secondaryInfo, string prefix, string fallback)
    {
        if (string.IsNullOrWhiteSpace(secondaryInfo))
        {
            return fallback;
        }

        if (prefix == "MBC")
        {
            var parts = secondaryInfo.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 ? $"{parts[0]} {parts[1]}" : fallback;
        }

        return fallback;
    }

    private static void DrawPreviewEncodedTitle(
        Graphics graphics,
        MenuPreviewEntry entry,
        int x,
        int y,
        int sizePx,
        int scale,
        Brush textBrush,
        Brush missingBrush)
    {
        if (entry.EncodedNameCodes is null || entry.EncodedNameCodes.Count == 0)
        {
            graphics.DrawString("UNKNOWN", SystemFonts.DefaultFont, missingBrush, x, y);
            return;
        }

        var visibleCodes = new List<ushort>();
        foreach (var code in entry.EncodedNameCodes)
        {
            if (code == 0)
            {
                break;
            }

            visibleCodes.Add(code);
        }

        for (var i = 0; i < visibleCodes.Count; i++)
        {
            var code = visibleCodes[i];
            var drawX = x + (i * Math.Max(8, sizePx * scale / 3));
            if (entry.GlyphBitmaps is not null && entry.GlyphBitmaps.TryGetValue(code, out var glyphBitmap))
            {
                DrawGlyphBitmap(graphics, glyphBitmap, drawX, y, sizePx, scale, entry.GlyphSizePx);
            }
            else if (code < 0x80)
            {
                using var asciiFont = new Font("Segoe UI", Math.Max(8F, sizePx * scale / 2.7F), FontStyle.Regular, GraphicsUnit.Pixel);
                graphics.DrawString(((char)code).ToString(), asciiFont, textBrush, drawX, y);
            }
            else
            {
                graphics.DrawRectangle(Pens.DarkRed, drawX, y, Math.Max(8, sizePx * scale / 3), Math.Max(8, sizePx * scale / 3));
            }
        }
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

        var cursorX = x;
        for (var i = 0; i < text.Length; i++)
        {
            var code = (ushort)text[i];
            if (!glyphBitmaps.TryGetValue(code, out var glyphBitmap))
            {
                graphics.DrawString(text[i].ToString(), fallbackFont, brush, cursorX, y);
                cursorX += Math.Max(6, scale * 6);
                continue;
            }

            DrawGlyphBitmapExact(graphics, glyphBitmap, cursorX, y, targetSizePx, scale, preferredGlyphSizePx);
            var advancePx = code < 0x80
                ? MeasurePreviewGlyphAdvancePx(glyphBitmap, preferredGlyphSizePx, targetSizePx)
                : targetSizePx;
            cursorX += advancePx * scale;
            if (i + 1 < text.Length)
            {
                cursorX += (code < 0x80 ? asciiGapPx : cjkGapPx) * scale;
            }
        }
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
        var digits = value.ToString("D2", CultureInfo.InvariantCulture);
        if (glyphBitmaps is null || glyphBitmaps.Count == 0)
        {
            graphics.DrawString($"{digits}.", fallbackFont, brush, x, y);
            return;
        }

        const int targetSizePx = 12;
        var digitStepPx = MeasurePreviewDigitStepPx(glyphBitmaps, preferredGlyphSizePx, targetSizePx);
        for (var i = 0; i < digits.Length; i++)
        {
            if (!glyphBitmaps.TryGetValue(digits[i], out var digitBitmap))
            {
                graphics.DrawString(digits[i].ToString(), fallbackFont, brush, x + (i * digitStepPx * scale), y);
                continue;
            }

            DrawGlyphBitmapExact(graphics, digitBitmap, x + (i * digitStepPx * scale), y, targetSizePx, scale, preferredGlyphSizePx);
        }

        if (glyphBitmaps.TryGetValue('.', out var dotBitmap))
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
        var maxAdvancePx = 0;
        for (var ch = '0'; ch <= '9'; ch++)
        {
            if (!glyphBitmaps.TryGetValue(ch, out var glyphBitmap))
            {
                continue;
            }

            maxAdvancePx = Math.Max(maxAdvancePx, MeasurePreviewGlyphAdvancePx(glyphBitmap, preferredGlyphSizePx, targetSizePx));
        }

        return Math.Max(1, maxAdvancePx) + 2;
    }

    private static int MeasurePreviewGlyphAdvancePx(IReadOnlyList<byte> glyphBitmap, int preferredGlyphSizePx, int targetSizePx)
    {
        var glyphSizePx = ResolveGlyphBitmapSizePx(glyphBitmap, preferredGlyphSizePx);
        if (glyphSizePx <= 0)
        {
            return Math.Max(1, targetSizePx / 2);
        }

        var right = -1;
        for (var row = 0; row < glyphSizePx; row++)
        {
            var rowBits = ReadGlyphBitmapRow(glyphBitmap, glyphSizePx, row);
            for (var column = 0; column < glyphSizePx; column++)
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

        var widthPx = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var code = (ushort)text[i];
            if (glyphBitmaps is not null && glyphBitmaps.TryGetValue(code, out var glyphBitmap))
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

    private static void DrawGlyphBitmap(Graphics graphics, IReadOnlyList<byte> glyphBitmap, int x, int y, int sizePx, int scale, int preferredGlyphSizePx)
    {
        var glyphSizePx = ResolveGlyphBitmapSizePx(glyphBitmap, preferredGlyphSizePx);
        if (glyphSizePx <= 0)
        {
            return;
        }

        var targetSize = Math.Max(8, sizePx * scale / 3);
        using var pixelBrush = new SolidBrush(Color.FromArgb(28, 34, 39));
        for (var sy = 0; sy < glyphSizePx; sy++)
        {
            var rowBits = ReadGlyphBitmapRow(glyphBitmap, glyphSizePx, sy);
            var y0 = (sy * targetSize) / glyphSizePx;
            var y1 = Math.Max(y0 + 1, ((sy + 1) * targetSize + (glyphSizePx - 1)) / glyphSizePx);
            for (var sx = 0; sx < glyphSizePx; sx++)
            {
                if ((rowBits & (1 << (glyphSizePx - 1 - sx))) == 0)
                {
                    continue;
                }

                var x0 = (sx * targetSize) / glyphSizePx;
                var x1 = Math.Max(x0 + 1, ((sx + 1) * targetSize + (glyphSizePx - 1)) / glyphSizePx);
                graphics.FillRectangle(pixelBrush, x + x0, y + y0, x1 - x0, y1 - y0);
            }
        }
    }

    private static string TrimPreviewText(string text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "UNKNOWN";
        }

        var trimmed = text.Trim();
        return trimmed.Length <= maxChars ? trimmed : $"{trimmed[..Math.Max(1, maxChars - 1)]}…";
    }

    private async void buttonBuild_Click(object? sender, EventArgs e)
    {
        if(!_gamesLoading && MdBuildSelected){ShowFcWorkspace();return;}
        if (_gamesLoading)
        {
            return;
        }

        BuildRequest request;
        try
        {
            request = CreateBuildRequest();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                T("dialog", "build_settings_error_title", "Build Settings Error"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        if (TryRejectBuildByCapacity(request))
        {
            return;
        }

        SetBuildState(true);
        _textBoxLog.Clear();
        _gamesLoadingProgressTimer.Stop();
        _progressBarBuild.Style = ProgressBarStyle.Continuous;
        _progressBarBuild.Minimum = 0;
        _progressBarBuild.Maximum = 100;
        _progressBarBuild.Value = 0;
        _labelStatus.Text = T("status", "starting_build", "Starting build...");
        AppendLog(T("log", "starting_build", "Starting build."));

        var buildCancellation = new CancellationTokenSource();
        _buildCancellation = buildCancellation;
        var progress = new Progress<BuildProgressInfo>(info =>
        {
            if (!ReferenceEquals(_buildCancellation, buildCancellation) || buildCancellation.IsCancellationRequested)
            {
                return;
            }

            _progressBarBuild.Value = Math.Clamp(info.Percent, 0, 100);
            _labelStatus.Text = info.Message;
            AppendLog(info.Message);
        });

        try
        {
            var result = await Task.Run(() => _builder.Build(request, progress, buildCancellation.Token, T));
            AppendLog(Tf("log", "build_finished_output", "Build finished. Output: {0}", result.OutputPath));
            AppendLog(Tf("log", "rom_code", "ROM code: {0}", result.RomCode));
            AppendLog(Tf("log", "output_size", "Output size: {0:F2} MiB", result.OutputSize / 1024d / 1024d));
            if (result.SkippedTitles.Count > 0)
            {
                AppendLog(Tf("log", "skipped_games", "Skipped games: {0}", string.Join(", ", result.SkippedTitles)));
                MessageBox.Show(
                    this,
                    Tf("dialog", "build_finished_with_warnings_message",
                        "Build finished, but some ROMs did not fit on the cartridge:\n\n{0}",
                        string.Join("\n", result.SkippedTitles)),
                    T("dialog", "build_finished_with_warnings_title", "Build Finished With Warnings"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            else
            {
                MessageBox.Show(
                    this,
                    T("dialog", "build_finished_message", "Build finished successfully."),
                    T("dialog", "build_finished_title", "Build Finished"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog(T("log", "build_canceled", "Build canceled."));
            _labelStatus.Text = T("status", "canceled", "Canceled");
        }
        catch (Exception ex)
        {
            AppendLog(Tf("log", "build_failed", "Build failed: {0}", ex.Message));
            _labelStatus.Text = T("status", "failed", "Failed");
            MessageBox.Show(
                this,
                ex.ToString(),
                T("dialog", "build_failed_title", "Build Failed"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            _buildCancellation?.Dispose();
            _buildCancellation = null;
            SetBuildState(false);
        }
    }

    private bool TryRejectBuildByCapacity(BuildRequest request)
    {
        BuildLayoutEstimate estimate;
        try
        {
            estimate = _builder.AnalyzeLayoutQuick(request, T);
        }
        catch (Exception ex)
        {
            _capacitySummaryErrorMessage = ex.Message;
            AppendLog(Tf("log", "capacity_summary_failed", "Capacity analysis failed: {0}", ex.Message));
            _labelStatus.Text = ex.Message;
            RefreshBuildButtonAvailability();
            return true;
        }

        if (!estimate.ExceedsCapacity)
        {
            return false;
        }

        AppendLog(Tf(
            "log",
            "build_capacity_exceeded",
            "Build canceled: current ROM layout exceeds cartridge capacity. Required {0}, total {1}, overflow {2}.",
            FormatByteSize(estimate.UsedPayloadBytes),
            FormatByteSize(estimate.PayloadCapacityBytes),
            FormatByteSize(estimate.OverflowBytes)));
        MessageBox.Show(
            this,
            Tf(
                "dialog",
                "build_capacity_exceeded_message",
                "Current ROM layout exceeds cartridge capacity and cannot be built.\n\nRequired: {0}\nTotal: {1}\nOverflow: {2}",
                FormatByteSize(estimate.UsedPayloadBytes),
                FormatByteSize(estimate.PayloadCapacityBytes),
                FormatByteSize(estimate.OverflowBytes)),
            T("dialog", "build_capacity_exceeded_title", "Capacity Exceeded"),
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
        return true;
    }

    private void buttonCancelBuild_Click(object? sender, EventArgs e)
    {
        _buildCancellation?.Cancel();
    }

    private void dataGridViewGames_MouseMove(object? sender, MouseEventArgs e)
    {
        var hit = _dataGridViewGames.HitTest(e.X, e.Y);
        if (hit.Type == DataGridViewHitTestType.ColumnHeader && hit.ColumnIndex >= 0)
        {
            if (_hoveredGameGridHitType != DataGridViewHitTestType.ColumnHeader ||
                _hoveredGameGridColumnIndex != hit.ColumnIndex)
            {
                StartGameGridHeaderToolTipCountdown(hit.ColumnIndex);
            }

            return;
        }

        if (hit.Type == DataGridViewHitTestType.Cell &&
            hit.RowIndex >= 0 &&
            hit.ColumnIndex >= 0 &&
            !string.IsNullOrWhiteSpace(GetGameGridCellToolTip(hit.RowIndex, hit.ColumnIndex)))
        {
            if (_hoveredGameGridHitType != DataGridViewHitTestType.Cell ||
                _hoveredGameGridRowIndex != hit.RowIndex ||
                _hoveredGameGridColumnIndex != hit.ColumnIndex)
            {
                StartGameGridCellToolTipCountdown(hit.RowIndex, hit.ColumnIndex);
            }

            return;
        }

        ClearGameGridHeaderToolTip();
    }

    private void dataGridViewGames_CurrentCellDirtyStateChanged(object? sender, EventArgs e)
    {
        if (_dataGridViewGames.IsCurrentCellDirty)
        {
            _dataGridViewGames.CommitEdit(DataGridViewDataErrorContexts.Commit);
        }
    }

    private void dataGridViewGames_CellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0)
        {
            return;
        }

        if (e.ColumnIndex < 0 || e.ColumnIndex >= _dataGridViewGames.Columns.Count)
        {
            SaveCurrentGameList();
            RefreshBuildCapacitySummary();
            return;
        }

        var dataPropertyName = _dataGridViewGames.Columns[e.ColumnIndex].DataPropertyName;
        var changedGame = e.RowIndex >= 0 &&
            e.RowIndex < _dataGridViewGames.Rows.Count &&
            _dataGridViewGames.Rows[e.RowIndex].DataBoundItem is GameEntryModel boundGame
            ? boundGame
            : null;

        if (changedGame is not null &&
            dataPropertyName == nameof(GameEntryModel.ApplyBatterylessPatch))
        {
            changedGame.ApplyBatterylessPatchOverride = changedGame.ApplyBatterylessPatch;
        }

        if (changedGame is not null &&
            dataPropertyName == nameof(GameEntryModel.GbcDedicatedSaveAllocation))
        {
            changedGame.GbcDedicatedSaveAllocationOverride = changedGame.GbcDedicatedSaveAllocation;
            RefreshGameHeaderMetadata(changedGame);
            InvalidateGameRow(changedGame);
        }

        if (changedGame is not null &&
            dataPropertyName == nameof(GameEntryModel.RomPath))
        {
            changedGame.GbcDedicatedSaveAllocationOverride = null;
            RefreshGameHeaderMetadata(changedGame);
        }

        SaveCurrentGameList();

        if (dataPropertyName is nameof(GameEntryModel.Enabled)
            or nameof(GameEntryModel.RomPath)
            or nameof(GameEntryModel.SaveSlot)
            or nameof(GameEntryModel.HiddenKeys)
            or nameof(GameEntryModel.Map256m)
            or nameof(GameEntryModel.GbcDedicatedSaveAllocation)
            or nameof(GameEntryModel.ApplySramPatch)
            or nameof(GameEntryModel.ApplyBatterylessPatch))
        {
            if (dataPropertyName == nameof(GameEntryModel.ApplySramPatch))
            {
                RefreshGameBatterylessPatchSelectionValues();
                RefreshGamePatchOptionUi(persistGameList: true);
            }
            else if (dataPropertyName is nameof(GameEntryModel.RomPath) or nameof(GameEntryModel.ApplyBatterylessPatch))
            {
                RefreshGameSizeDisplayValues();
                RefreshGamePatchOptionUi(persistGameList: dataPropertyName == nameof(GameEntryModel.ApplyBatterylessPatch));
            }

            RefreshBuildCapacitySummary();
        }

        if (dataPropertyName is nameof(GameEntryModel.Title) or nameof(GameEntryModel.RomPath))
        {
            AutoAdjustGameGridColumns();
        }
    }

    private void dataGridViewGames_CellBeginEdit(object? sender, DataGridViewCellCancelEventArgs e)
    {
        if (e.RowIndex < 0 ||
            e.ColumnIndex < 0 ||
            e.RowIndex >= _dataGridViewGames.Rows.Count ||
            e.ColumnIndex >= _dataGridViewGames.Columns.Count)
        {
            return;
        }

        if (_dataGridViewGames.Rows[e.RowIndex].DataBoundItem is not GameEntryModel game)
        {
            return;
        }

        var propertyName = _dataGridViewGames.Columns[e.ColumnIndex].DataPropertyName;
        if (game.MdRom is not null && (propertyName is not (nameof(GameEntryModel.Title) or nameof(GameEntryModel.Enabled)) ||
            (propertyName == nameof(GameEntryModel.Enabled) && !game.MdRom.CanRead))) { e.Cancel = true; return; }
        if (propertyName == nameof(GameEntryModel.ApplySramPatch) && !IsSramPatchSelectable(game))
        {
            e.Cancel = true;
            return;
        }

        if (propertyName == nameof(GameEntryModel.ApplyBatterylessPatch) && !IsBatterylessPatchSelectable(game))
        {
            e.Cancel = true;
            return;
        }

        if (propertyName == nameof(GameEntryModel.GbcDedicatedSaveAllocation) && !IsGbcSaveAllocationSelectable(game))
        {
            e.Cancel = true;
            return;
        }
    }

    private void dataGridViewGames_CellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 ||
            e.ColumnIndex < 0 ||
            e.RowIndex >= _dataGridViewGames.Rows.Count ||
            e.ColumnIndex >= _dataGridViewGames.Columns.Count)
        {
            return;
        }

        if (_dataGridViewGames.Rows[e.RowIndex].DataBoundItem is not GameEntryModel game)
        {
            return;
        }

        if (string.Equals(_dataGridViewGames.Columns[e.ColumnIndex].Name, SequenceColumnName, StringComparison.Ordinal))
        {
            e.Value = (e.RowIndex + 1).ToString();
            e.FormattingApplied = true;
            return;
        }

        var propertyName = _dataGridViewGames.Columns[e.ColumnIndex].DataPropertyName;
        if (game.IsLoadingPlaceholder)
        {
            e.CellStyle.ForeColor = SystemColors.GrayText;
            e.CellStyle.SelectionForeColor = SystemColors.GrayText;
            if (game.IsLoadingActive)
            {
                var activeBackColor = Color.FromArgb(255, 244, 214);
                e.CellStyle.BackColor = activeBackColor;
                e.CellStyle.SelectionBackColor = activeBackColor;
            }
            else
            {
                e.CellStyle.BackColor = SystemColors.ControlLight;
                e.CellStyle.SelectionBackColor = SystemColors.ControlLight;
            }

            if (propertyName is nameof(GameEntryModel.SaveTypeDisplay)
                or nameof(GameEntryModel.MbcTypeDisplay)
                or nameof(GameEntryModel.SaveSupportDisplay)
                or nameof(GameEntryModel.RamSizeDisplay)
                or nameof(GameEntryModel.RomSizeDisplay)
                or nameof(GameEntryModel.SramPatchRequirementDisplay)
                or nameof(GameEntryModel.BatterylessStatusDisplay)
                or nameof(GameEntryModel.FileSizeDisplay)
                or nameof(GameEntryModel.OccupiedSizeDisplay)
                or nameof(GameEntryModel.GameTitle)
                or nameof(GameEntryModel.SaveSlot)
                or nameof(GameEntryModel.HiddenKeys)
                or nameof(GameEntryModel.TitleFont))
            {
                e.Value = string.Empty;
                e.FormattingApplied = true;
            }
            else if (propertyName is nameof(GameEntryModel.ApplySramPatch)
                or nameof(GameEntryModel.ApplyBatterylessPatch)
                or nameof(GameEntryModel.GbcDedicatedSaveAllocation))
            {
                e.Value = false;
                e.FormattingApplied = true;
            }

            return;
        }

        if (propertyName == nameof(GameEntryModel.ApplySramPatch) && !IsSramPatchSelectable(game))
        {
            e.CellStyle.ForeColor = SystemColors.GrayText;
            e.CellStyle.SelectionForeColor = SystemColors.GrayText;
            e.CellStyle.BackColor = SystemColors.ControlLight;
            e.CellStyle.SelectionBackColor = SystemColors.ControlLight;
            e.Value = false;
            e.FormattingApplied = true;
            return;
        }

        if (propertyName == nameof(GameEntryModel.ApplyBatterylessPatch) && !IsBatterylessPatchSelectable(game))
        {
            e.CellStyle.ForeColor = SystemColors.GrayText;
            e.CellStyle.SelectionForeColor = SystemColors.GrayText;
            e.CellStyle.BackColor = SystemColors.ControlLight;
            e.CellStyle.SelectionBackColor = SystemColors.ControlLight;
            e.Value = false;
            e.FormattingApplied = true;
            return;
        }

        if (propertyName == nameof(GameEntryModel.GbcDedicatedSaveAllocation) && !IsGbcSaveAllocationSelectable(game))
        {
            e.CellStyle.ForeColor = SystemColors.GrayText;
            e.CellStyle.SelectionForeColor = SystemColors.GrayText;
            e.CellStyle.BackColor = SystemColors.ControlLight;
            e.CellStyle.SelectionBackColor = SystemColors.ControlLight;
            e.FormattingApplied = false;
        }
    }

    private void dataGridViewGames_MouseLeave(object? sender, EventArgs e)
    {
        ClearGameGridHeaderToolTip();
    }

    private void dataGridViewGames_MouseDown(object? sender, MouseEventArgs e)
    {
        ClearGameGridHeaderToolTip();
    }

    private void dataGridViewGames_Scroll(object? sender, ScrollEventArgs e)
    {
        ClearGameGridHeaderToolTip();
    }

    private void dataGridViewGames_DataError(object? sender, DataGridViewDataErrorEventArgs e)
    {
        e.ThrowException = false;
    }

    private void dataGridViewGames_ColumnWidthChanged(object? sender, DataGridViewColumnEventArgs e)
    {
        if (_suppressGameGridColumnWidthPersistence)
        {
            return;
        }

        SaveCurrentAppSettings();
    }

    private void Form1_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_fcWorkspace?.IsBusy == true) { _fcWorkspace.Cancel(); e.Cancel = true; return; }

        _dataGridViewGames.EndEdit();
        SaveCurrentAppSettings();
        SaveCurrentGameList();
    }

    private async void Form1_Shown(object? sender, EventArgs e)
    {
        if (!_startupGameListRestorePending)
        {
            AutoAdjustGameGridColumns();
            return;
        }

        _startupGameListRestorePending = false;
        await RestoreLastGameListAsync(_currentCompilationFamily);
        AutoAdjustGameGridColumns();
    }

    private void Form1_DragEnter(object? sender, DragEventArgs e)
    {
        if (_gamesLoading)
        {
            e.Effect = DragDropEffects.None;
        }
        else if (e.Data?.GetData(DataFormats.FileDrop) is string[] files &&
            files.Any(file => Directory.Exists(file) || RomImportService.SupportsImportFile(file, _currentCompilationFamily)))
        {
            e.Effect = DragDropEffects.Copy;
        }
        else
        {
            e.Effect = DragDropEffects.None;
        }
    }

    private async void Form1_DragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] files)
        {
            return;
        }

        await AddRomFilesAsync(files);
    }

    private async Task AddRomFilesAsync(IEnumerable<string> filePaths)
    {
        if (_gamesLoading)
        {
            return;
        }

        var remainingFiles = await ImportMdRowsAsync(filePaths);
        var supportedFilePaths = remainingFiles
            .Where(filePath => RomImportService.SupportsImportFile(filePath, _currentCompilationFamily))
            .ToList();
        if (supportedFilePaths.Count == 0)
        {
            return;
        }

        var existingPaths = _games
            .Where(static game => !game.IsLoadingPlaceholder)
            .Select(static game => game.RomPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nextSaveSlot = GetNextSaveSlot();
        var batteryPresent = GetEffectiveBatteryPresent();
        var maxSaveSlotCount = batteryPresent ? GetMaxAssignableSaveSlotCount() : int.MaxValue;
        var added = 0;
        var shouldAnimateCompletion = false;
        Dictionary<Guid, GameEntryModel> placeholderMap = [];
        PendingImportCapacityContext? importCapacityContext = null;
        var importedResolvedGames = new ConcurrentQueue<ResolvedGameEntryData>();

        try
        {
            SetGamesLoadingState(true, T("status", "loading_roms", "Loading ROM files..."));
            var preview = await PreparePendingImportsAsync(supportedFilePaths, existingPaths, nextSaveSlot, batteryPresent, maxSaveSlotCount);
            if (IsDisposed)
            {
                return;
            }

            foreach (var message in preview.LogMessages)
            {
                AppendLog(message);
            }

            if (preview.PendingImports.Count == 0)
            {
                return;
            }

            importCapacityContext = CreatePendingImportCapacityContext(preview.PendingImports);
            placeholderMap = PrepareLoadingPlaceholders(
                preview.PendingImports.Select(import => new LoadingPlaceholderDescriptor(
                    import.PlaceholderId,
                    import.Candidate.Title,
                    import.Candidate.RomPath))
                    .ToList(),
                clearExistingGames: false);

            shouldAnimateCompletion = preview.PendingImports.Count > 0;
            SetGamesLoadingState(true, T("status", "loading_roms", "Loading ROM files..."), preview.PendingImports.Count);
            BeginGameLoadUiSession(placeholderMap);
            Action<GameLoadProgress> progress = info =>
            {
                if (info.ResolvedGames.Count > 0)
                {
                    Interlocked.Add(ref added, info.ResolvedGames.Count);
                    foreach (var resolvedGame in info.ResolvedGames)
                    {
                        importedResolvedGames.Enqueue(resolvedGame);
                    }
                }

                EnqueueGameLoadProgress(info);
            };
            await PrepareRomImportAsync(preview.PendingImports, progress);
            if (IsDisposed)
            {
                return;
            }

            FlushPendingGameLoadProgress(forceAll: true);
            if (added > 0)
            {
                LogBatterylessExpansionCapacityOverflow(importCapacityContext, importedResolvedGames.ToArray());
                AppendLog(Tf("log", "added_rom_files", "Added {0} ROM file(s).", added));
                SaveCurrentGameList();
                RefreshBuildCapacitySummary();
            }
        }
        finally
        {
            if (!IsDisposed)
            {
                FlushPendingGameLoadProgress(forceAll: true);
                if (shouldAnimateCompletion)
                {
                    await CompleteGamesLoadingUiAsync();
                }

                SetActiveLoadingPlaceholder(Guid.Empty, false);
                RemoveRemainingLoadingPlaceholders(placeholderMap);
                EndGameLoadUiSession();
                SetGamesLoadingState(false, T("status", "ready", "Ready"));
            }
        }
    }

    private async Task<PendingImportPreparation> PreparePendingImportsAsync(
        IReadOnlyList<string> filePaths,
        IReadOnlySet<string> existingPaths,
        int nextSaveSlot,
        bool batteryPresent,
        int maxSaveSlotCount)
    {
        var previewTasks = filePaths
            .Select((filePath, index) => RunFileTask(() => PreviewImportFile(filePath, index)))
            .ToArray();
        var previewResults = await Task.WhenAll(previewTasks);

        var pendingImports = new List<PendingImportFile>();
        var logMessages = new List<string>();
        var knownPaths = new HashSet<string>(existingPaths, StringComparer.OrdinalIgnoreCase);
        var ordinal = 1;

        foreach (var previewResult in previewResults.OrderBy(static result => result.SourceIndex))
        {
            foreach (var message in previewResult.LogMessages)
            {
                logMessages.Add(message);
            }

            foreach (var candidate in previewResult.Candidates)
            {
                var normalizedRomPath = NormalizeRomPath(candidate.RomPath);
                if (!knownPaths.Add(normalizedRomPath))
                {
                    continue;
                }

                var assignedSaveSlot = !batteryPresent || nextSaveSlot <= maxSaveSlotCount
                    ? nextSaveSlot.ToString()
                    : string.Empty;
                pendingImports.Add(new PendingImportFile(
                    Guid.NewGuid(),
                    previewResult.SourceIndex,
                    ordinal,
                    candidate with { RomPath = normalizedRomPath },
                    assignedSaveSlot));
                ordinal++;
                if (!string.IsNullOrWhiteSpace(assignedSaveSlot))
                {
                    nextSaveSlot++;
                }
            }
        }

        return new PendingImportPreparation(pendingImports, logMessages);
    }

    private ImportPreviewResult PreviewImportFile(string filePath, int sourceIndex)
    {
        IReadOnlyList<RomImportCandidate> candidates;
        try
        {
            candidates = RomImportService.Preview(filePath, _currentCompilationFamily);
            if(archiveTitles.TryGetValue(filePath,out string? title))candidates=candidates.Select(c=>c with{Title=title}).ToArray();
        }
        catch (InvalidDataException ex)
        {
            return new ImportPreviewResult(
                sourceIndex,
                filePath,
                [],
                [Tf("log", "rom_import_failed", "Import failed for {0}: {1}", Path.GetFileName(filePath), ex.Message)]);
        }
        catch (Exception ex)
        {
            return new ImportPreviewResult(
                sourceIndex,
                filePath,
                [],
                [Tf("log", "rom_import_failed", "Import failed for {0}: {1}", Path.GetFileName(filePath), ex.Message)]);
        }

        if (candidates.Count == 0 && Path.GetExtension(filePath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return new ImportPreviewResult(
                sourceIndex,
                filePath,
                [],
                [_currentCompilationFamily == CompilationFamily.Gbc
                    ? Tf("log", "zip_no_gbc_found", "No .gb/.gbc file found inside ZIP: {0}", Path.GetFileName(filePath))
                    : Tf("log", "zip_no_gba_found", "No .gba file found inside ZIP: {0}", Path.GetFileName(filePath))]);
        }

        return new ImportPreviewResult(sourceIndex, filePath, candidates, []);
    }

    private async Task PrepareRomImportAsync(
        IReadOnlyList<PendingImportFile> pendingImports,
        Action<GameLoadProgress>? reportProgress)
    {
        var completedCount = 0;
        using var workerLimiter = new SemaphoreSlim(MaxConcurrentMetadataWorkerCount);
        var tasks = pendingImports
            .Select(async pendingImport =>
            {
                await workerLimiter.WaitAsync();
                try
                {
                    var currentFileName = pendingImport.Candidate.DisplayName;
                    reportProgress?.Invoke(new GameLoadProgress(
                        pendingImport.PlaceholderId,
                        Volatile.Read(ref completedCount),
                        pendingImports.Count,
                        pendingImport.Ordinal,
                        currentFileName,
                        FinalizePlaceholder: false,
                        [],
                        []));

                    var logMessages = new List<string>();
                    var resolvedGames = new List<ResolvedGameEntryData>();
                    try
                    {
                        await Task.Run(() => RomImportService.Materialize(pendingImport.Candidate));
                    }
                    catch (InvalidDataException ex)
                    {
                        logMessages.Add(Tf("log", "rom_import_failed", "Import failed for {0}: {1}", currentFileName, ex.Message));
                        var failedCount = Interlocked.Increment(ref completedCount);
                        reportProgress?.Invoke(new GameLoadProgress(
                            pendingImport.PlaceholderId,
                            failedCount,
                            pendingImports.Count,
                            pendingImport.Ordinal,
                            currentFileName,
                            FinalizePlaceholder: true,
                            resolvedGames,
                            logMessages));
                        return;
                    }
                    catch (Exception ex)
                    {
                        logMessages.Add(Tf("log", "rom_import_failed", "Import failed for {0}: {1}", currentFileName, ex.Message));
                        var failedCount = Interlocked.Increment(ref completedCount);
                        reportProgress?.Invoke(new GameLoadProgress(
                            pendingImport.PlaceholderId,
                            failedCount,
                            pendingImports.Count,
                            pendingImport.Ordinal,
                            currentFileName,
                            FinalizePlaceholder: true,
                            resolvedGames,
                            logMessages));
                        return;
                    }

                    resolvedGames.Add(await ResolveGameEntryDataAsync(
                        pendingImport.Candidate.RomPath,
                        pendingImport.Candidate.Title,
                        pendingImport.SaveSlot,
                        string.Empty,
                        true,
                        null,
                        null,
                        null,
                        1));

                    var finalizedCount = Interlocked.Increment(ref completedCount);
                    reportProgress?.Invoke(new GameLoadProgress(
                        pendingImport.PlaceholderId,
                        finalizedCount,
                        pendingImports.Count,
                        pendingImport.Ordinal,
                        currentFileName,
                        FinalizePlaceholder: true,
                        resolvedGames,
                        logMessages));
                }
                finally
                {
                    workerLimiter.Release();
                }
            })
            .ToArray();

        await Task.WhenAll(tasks);
    }

    private int GetNextSaveSlot()
    {
        var maxSlot = 0;
        foreach (var game in _games)
        {
            if (game.IsLoadingPlaceholder)
            {
                continue;
            }

            if (int.TryParse(game.SaveSlot, out var parsed) && parsed > maxSlot)
            {
                maxSlot = parsed;
            }
        }

        return maxSlot + 1;
    }

    private int GetMaxAssignableSaveSlotCount()
    {
        if (_comboBoxCartridgeType.SelectedItem is not CartridgeDefinition cartridge)
        {
            return 0;
        }

        return CompilationBuilder.GetMaxBatterySaveSlotCount(cartridge, GetSelectedFlashCartMode());
    }

    private void MoveSelectedRow(int direction)
    {
        if (_dataGridViewGames.SelectedRows.Count != 1)
        {
            return;
        }

        var selectedRow = _dataGridViewGames.SelectedRows[0];
        var oldIndex = selectedRow.Index;
        var newIndex = oldIndex + direction;
        if (newIndex < 0 || newIndex >= _games.Count)
        {
            return;
        }

        var item = _games[oldIndex];
        _games.RemoveAt(oldIndex);
        _games.Insert(newIndex, item);
        _dataGridViewGames.ClearSelection();
        _dataGridViewGames.Rows[newIndex].Selected = true;
        SaveCurrentGameList();
    }

    private UiState CaptureUiState()
    {
        var selectedCartridgeId = _comboBoxCartridgeType.SelectedItem is CartridgeDefinition cartridge
            ? cartridge.Id
            : GetDefaultCartridgeIdForFamily(_currentCompilationFamily);
        var selectedMinRomSize = _comboBoxMinRomSize.SelectedItem is MinRomSizeOption minRomSize
            ? minRomSize.Value
            : _minRomSizeOptions.First().Value;
        var selectedFlashCartMode = _comboBoxFlashCartMode.SelectedItem is FlashCartModeOption flashCartMode
            ? flashCartMode.Mode
            : _flashCartModeOptions.First().Mode;
        var windowState = WindowState;
        var restoreBounds = windowState == FormWindowState.Normal
            ? Bounds
            : RestoreBounds;

        return new UiState(
            _textBoxBaseMenuPath.Text,
            _textBoxOutputPath.Text,
            _textBoxBackgroundPath.Text,
            GetSelectedGbcFontName() ?? string.Empty,
            selectedCartridgeId,
            selectedMinRomSize,
            selectedFlashCartMode,
            GetSelectedGbaMenuType(),
            _flashCartBatteryPresent,
            _checkBoxSplitOutput.Checked,
            _checkBoxMinimalMenuPackage.Checked,
            _textBoxLog.Text,
            _progressBarBuild.Value,
            _labelStatus.Text,
            _language.Code,
            windowState,
            restoreBounds);
    }

    private void ApplyLanguage(UiLanguage language)
    {
        ReleaseTransientMouseState();
        ClearGameGridHeaderToolTip();
        var selectedFlashCartMode = GetSelectedFlashCartMode();
        var selectedGbaMenuType = GetSelectedGbaMenuType();
        var selectedCartridgeId = _comboBoxCartridgeType.SelectedItem is CartridgeDefinition cartridge
            ? cartridge.Id
            : (int?)null;
        _language = language;

        SuspendLayout();
        ApplyLocalizedControlTree(this);
        Text = $"{T("ui", "window_title", "Chis Multicart Builder")} {AppVersion} by {AppAuthor}";
        RefreshLocalizedMinRomSizeOptionNames();
        var previousSuppressFlashCartSync = _suppressFlashCartSync;
        _suppressFlashCartSync = true;
        try
        {
            RefreshLocalizedFlashCartModeOptionNames();
            RefreshLocalizedGbaMenuTypeOptionNames();
            RestoreFlashCartModeSelection(selectedFlashCartMode);
            RestoreGbaMenuTypeSelection(selectedGbaMenuType);
        }
        finally
        {
            _suppressFlashCartSync = previousSuppressFlashCartSync;
        }

        RestoreCartridgeTypeSelection(selectedCartridgeId);
        RefreshBackgroundOrFontUi();
        PerformLayout();
        RefreshGameGridColumnVisibility();
        RefreshLocalizedGameGridColumns();
        RefreshGameGbcHeaderDisplayValues();
        RefreshGameSaveTypeDisplayValues();
        RefreshGameSramPatchRequirementDisplayValues();
        RefreshGameBatterylessPatchSelectionValues();
        SyncBaseMenuPathWithSelectedGbaOptions();
        _labelStatus.Text = T("status", "ready", "Ready");
        if (_gamesLoading && string.IsNullOrWhiteSpace(_gamesLoadingCurrentFileName))
        {
            _labelGamesLoading.Text = T("status", "loading_roms", "Loading, please wait...");
        }

        RefreshBuildCapacitySummary();
        ResumeLayout(true);
    }

    private void RestoreFlashCartModeSelection(FlashCartMode selectedMode)
    {
        if (_comboBoxFlashCartMode is null || _flashCartModeOptions.Count == 0)
        {
            return;
        }

        var selectedOption = _flashCartModeOptions.FirstOrDefault(option => option.Mode == selectedMode);
        if (selectedOption is not null)
        {
            _comboBoxFlashCartMode.SelectedItem = selectedOption;
        }
    }

    private void RestoreCartridgeTypeSelection(int? selectedCartridgeId)
    {
        if (!selectedCartridgeId.HasValue || _comboBoxCartridgeType is null)
        {
            return;
        }

        var selectedCartridge = _comboBoxCartridgeType.Items
            .OfType<CartridgeDefinition>()
            .FirstOrDefault(cartridge => cartridge.Id == selectedCartridgeId.Value);
        if (selectedCartridge is not null)
        {
            _comboBoxCartridgeType.SelectedItem = selectedCartridge;
        }
    }

    private void RestoreUiState(UiState state)
    {
        _textBoxBaseMenuPath.Text = state.BaseMenuPath;
        _textBoxOutputPath.Text = state.OutputPath;
        _textBoxBackgroundPath.Text = state.BackgroundPath;
        RefreshGbcFontOptions(state.GbcFontName);
        _flashCartBatteryPresent = state.BatteryPresent;
        _checkBoxSplitOutput.Checked = state.SplitOutput;
        _checkBoxMinimalMenuPackage.Checked = state.MinimalMenuPackage;
        _textBoxLog.Text = state.LogText;
        _progressBarBuild.Value = Math.Clamp(state.ProgressValue, _progressBarBuild.Minimum, _progressBarBuild.Maximum);
        _labelStatus.Text = T("status", "ready", "Ready");

        RefreshCartridgeTypeOptions(state.CartridgeId);
        RefreshMinRomSizeOptions(state.MinRomSizeValue);

        RefreshFlashCartModeOptions(state.FlashCartMode);
        RestoreGbaMenuTypeSelection(state.GbaMenuType);
        _flashCartBatteryPresent = state.BatteryPresent;
        _checkBoxSplitOutput.Checked = state.SplitOutput;
        _checkBoxMinimalMenuPackage.Checked = state.MinimalMenuPackage;
        _flashCartModeSelectionInitialized = true;
        _suppressFlashCartSync = false;
        RefreshBackgroundOrFontUi();
        RefreshGameGridColumnVisibility();

        _comboBoxLanguage.SelectedItem = _availableLanguages
            .FirstOrDefault(language => string.Equals(language.Code, state.LanguageCode, StringComparison.OrdinalIgnoreCase))
            ?? _availableLanguages.FirstOrDefault();

        _languageSelectionInitialized = true;
        _settingsPersistenceEnabled = true;
        _gameListPersistenceEnabled = true;
        SyncBaseMenuPathWithSelectedGbaOptions(force: ShouldUseDefaultBaseMenuPath(state.BaseMenuPath));
        SaveCurrentAppSettings();
        RefreshGameBatterylessPatchSelectionValues();
        RefreshBuildCapacitySummary();
    }

    private UiState CreateUiStateFromSettings(AppSettings settings)
    {
        var selectedGbaMenuType = settings.GbaMenuType ?? settings.FlashCartMode switch
        {
            FlashCartMode.NsMenu => GbaMenuType.NsMenu,
            FlashCartMode.SimplifiedNsMenu => GbaMenuType.SimplifiedNsMenu,
            _ => GbaMenuType.Classic,
        };
        var selectedFlashCartMode = settings.FlashCartMode is null or FlashCartMode.NsMenu or FlashCartMode.SimplifiedNsMenu
            ? GetDefaultFlashCartMode(_currentCompilationFamily)
            : settings.FlashCartMode.Value;
        var windowState = WindowState == FormWindowState.Minimized
            ? FormWindowState.Normal
            : WindowState;
        var restoreBounds = windowState == FormWindowState.Normal
            ? Bounds
            : RestoreBounds;

        return new UiState(
            ResolveEffectiveBaseMenuPath(
                settings.BaseMenuPath,
                _currentCompilationFamily,
                selectedFlashCartMode,
                selectedGbaMenuType),
            BuildManagedOutputPath(settings.OutputPath),
            string.IsNullOrWhiteSpace(settings.BackgroundPath) ? string.Empty : settings.BackgroundPath.Trim(),
            settings.GbcFontName ?? GbcFontService.GetDefaultBuiltInFontFileName(),
            settings.CartridgeId ?? GetDefaultCartridgeIdForFamily(_currentCompilationFamily),
            settings.MinRomSize ?? _minRomSizeOptions.First().Value,
            selectedFlashCartMode,
            selectedGbaMenuType,
            settings.BatteryPresent ?? ResolveBatteryPresent(selectedFlashCartMode, false),
            settings.SplitOutput ?? false,
            settings.MinimalMenuPackage ?? false,
            string.Empty,
            0,
            T("status", "ready", "Ready"),
            settings.LanguageCode ?? _language.Code,
            windowState,
            restoreBounds);
    }

    private async Task SwitchCompilationFamilyAsync(CompilationFamily targetFamily, FlashCartMode? preferredMode = null)
    {
        if (targetFamily == _currentCompilationFamily)
        {
            return;
        }

        _dataGridViewGames.EndEdit();
        var carriedRoms=_games.Where(g=>g.OtherRom is { } source &&
            (source.Platform==RomPlatform.Gba&&targetFamily==CompilationFamily.Gba || source.Platform==RomPlatform.Gbc&&targetFamily==CompilationFamily.Gbc))
            .Select(g=>g.RomPath).ToArray();
        SaveCurrentAppSettings();
        SaveCurrentGameList();

        _currentCompilationFamily = targetFamily;
        AppConfigService.SaveCompilationFamily(targetFamily);

        _settingsPersistenceEnabled = false;
        _gameListPersistenceEnabled = false;
        _suppressFlashCartSync = true;
        RefreshMinRomSizeOptions();
        var state = CreateUiStateFromSettings(AppConfigService.LoadSettings(targetFamily));
        RestoreUiState(state);
        await RestoreLastGameListAsync(targetFamily);
        if(carriedRoms.Length>0)await AddRomFilesAsync(carriedRoms);

        if (preferredMode.HasValue &&
            _comboBoxFlashCartMode.SelectedItem is FlashCartModeOption currentModeOption &&
            currentModeOption.Mode != preferredMode.Value)
        {
            _suppressFlashCartSync = true;
            _comboBoxFlashCartMode.SelectedItem = _flashCartModeOptions
                .FirstOrDefault(option => option.Mode == preferredMode.Value)
                ?? _comboBoxFlashCartMode.SelectedItem;
            _suppressFlashCartSync = false;

            ApplyFlashCartPreset(preferredMode.Value);
            RefreshGameBatterylessPatchSelectionValues();
            RefreshGamePatchOptionUi(persistGameList: true);
            SaveCurrentAppSettings();
            RefreshBuildCapacitySummary();
        }
    }

    private void RestoreWindowGeometry(UiState state)
    {
        var restoreBounds = state.RestoreBounds;
        if (restoreBounds.Width <= 0 || restoreBounds.Height <= 0)
        {
            restoreBounds = new Rectangle(Location, DefaultWindowSize);
        }

        StartPosition = FormStartPosition.Manual;
        WindowState = FormWindowState.Normal;
        Bounds = restoreBounds;

        if (state.WindowState == FormWindowState.Maximized)
        {
            WindowState = FormWindowState.Maximized;
        }

        BeginInvoke((MethodInvoker)(() =>
        {
            PerformLayout();
            Invalidate(true);
            Update();
        }));
    }

    private BuildRequest CreateBuildRequest(bool requireEnabledGames = true)
    {
        if (_games.Any(g => g.OtherRom is not null && g.Enabled)) throw new InvalidOperationException("列表包含其他平台ROM，请选择对应平台模式后制作。");
        if (_games.Any(g => g.MdRom is not null && g.Enabled)) throw new InvalidOperationException("请仅勾选有效的MD游戏，不能通过GB/GBA通道制作。");
        if (_comboBoxCartridgeType.SelectedItem is not CartridgeDefinition cartridge)
        {
            throw new InvalidOperationException(T("error", "select_cartridge_type", "Select a cartridge type."));
        }

        if (_comboBoxMinRomSize.SelectedItem is not MinRomSizeOption minRomSize)
        {
            throw new InvalidOperationException(T("error", "select_min_rom_size", "Select a minimum ROM size."));
        }

        if (_comboBoxFlashCartMode.SelectedItem is not FlashCartModeOption flashCartMode)
        {
            throw new InvalidOperationException(T("error", "select_flashcart_type", "Select a flashcart type."));
        }

        var gbaMenuType = GetSelectedGbaMenuType();
        var baseMenuPath = ResolveEffectiveBaseMenuPath(
            _textBoxBaseMenuPath.Text,
            _currentCompilationFamily,
            flashCartMode.Mode,
            gbaMenuType);
        if (string.IsNullOrWhiteSpace(baseMenuPath))
        {
            throw new InvalidOperationException(T("error", "base_menu_required", "Base menu ROM path is required."));
        }

        var outputPath = BuildManagedOutputPath(_textBoxOutputPath.Text);

        var games = new List<BuildGameEntry>(_games.Count);
        for (var i = 0; i < _games.Count; i++)
        {
            var model = _games[i];
            if (model.IsLoadingPlaceholder || model.MdRom is not null || model.OtherRom is not null)
            {
                continue;
            }

            games.Add(CreateBuildGameEntry(model, i + 1));
        }

        if (requireEnabledGames && games.All(static game => !game.Enabled))
        {
            throw new InvalidOperationException(T("error", "at_least_one_rom", "At least one ROM must be enabled."));
        }

        var batteryPresent = GetEffectiveBatteryPresent();
        var minimalMenuPackage = _currentCompilationFamily == CompilationFamily.Gba &&
            gbaMenuType is GbaMenuType.NsMenu or GbaMenuType.SimplifiedNsMenu &&
            _checkBoxMinimalMenuPackage.Checked;
        if (!minimalMenuPackage)
        {
            ValidateBatteryBackedSaveSlots(games, cartridge, batteryPresent);
        }
        var backgroundPath = string.IsNullOrWhiteSpace(_textBoxBackgroundPath.Text) ? null : _textBoxBackgroundPath.Text.Trim();
        var gbcFontPath = _currentCompilationFamily == CompilationFamily.Gbc && IsSelectedGbcMenuChinese()
            ? ResolveSelectedGbcFontPath()
            : null;
        return new BuildRequest(
            baseMenuPath,
            outputPath,
            backgroundPath,
            _currentCompilationFamily == CompilationFamily.Gbc ? gbcFontPath : null,
            cartridge,
            minRomSize.Value,
            flashCartMode.Mode,
            gbaMenuType,
            batteryPresent,
            _checkBoxSplitOutput.Checked,
            games,
            _currentCompilationFamily,
            minimalMenuPackage);
    }

    private void ValidateBatteryBackedSaveSlots(
        IReadOnlyList<BuildGameEntry> games,
        CartridgeDefinition cartridge,
        bool batteryPresent)
    {
        if (!batteryPresent)
        {
            return;
        }

        var maxSaveSlotCount = CompilationBuilder.GetMaxBatterySaveSlotCount(cartridge, GetSelectedFlashCartMode());
        for (var i = 0; i < games.Count; i++)
        {
            var game = games[i];
            if (!game.Enabled || !game.SaveSlot.HasValue)
            {
                continue;
            }

            if (game.SaveSlot.Value <= 0 || game.SaveSlot.Value > maxSaveSlotCount)
            {
                throw new InvalidOperationException(Tf("error", "row_invalid_save_slot", "Row {0} has an invalid save slot.", i + 1));
            }
        }
    }

    private int? ParseSaveSlot(string? text, int rowNumber)
    {
        if (string.IsNullOrWhiteSpace(text) || string.Equals(text.Trim(), "none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!int.TryParse(text.Trim(), out var parsed) || parsed <= 0)
        {
            throw new InvalidOperationException(Tf("error", "row_invalid_save_slot", "Row {0} has an invalid save slot.", rowNumber));
        }

        return parsed;
    }

    private ushort ParseHiddenKeys(string? text, int rowNumber)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        ushort mask = 0;
        var parts = text.Split([',', '+', ';', '|', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            mask |= part.ToUpperInvariant() switch
            {
                "A" => 1 << 0,
                "B" => 1 << 1,
                "SELECT" => 1 << 2,
                "START" => 1 << 3,
                "RIGHT" => 1 << 4,
                "LEFT" => 1 << 5,
                "UP" => 1 << 6,
                "DOWN" => 1 << 7,
                "R" => 1 << 8,
                "L" => 1 << 9,
                _ => throw new InvalidOperationException(Tf("error", "row_unknown_key_token", "Row {0} has an unknown key token: {1}", rowNumber, part)),
            };
        }

        return mask;
    }

    private void comboBoxLanguage_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (!_languageSelectionInitialized || _comboBoxLanguage.SelectedItem is not UiLanguage selectedLanguage)
        {
            return;
        }

        if (string.Equals(selectedLanguage.Code, _language.Code, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        AppConfigService.SaveLanguageCode(selectedLanguage.Code);
        QueueLanguageRefresh(selectedLanguage);
    }

    private void comboBoxLanguage_DropDownClosed(object? sender, EventArgs e)
    {
        if (_pendingLanguageRefresh is null || IsDisposed || Disposing)
        {
            return;
        }

        BeginInvoke(new Action(ApplyPendingLanguageRefresh));
    }

    private void QueueLanguageRefresh(UiLanguage selectedLanguage)
    {
        _pendingLanguageRefresh = selectedLanguage;
        HideBuildSettingsToolTip();

        if (_comboBoxLanguage.DroppedDown)
        {
            _comboBoxLanguage.DroppedDown = false;
            return;
        }

        BeginInvoke(new Action(ApplyPendingLanguageRefresh));
    }

    private void ApplyPendingLanguageRefresh()
    {
        if (_pendingLanguageRefresh is not UiLanguage language || IsDisposed || Disposing)
        {
            return;
        }

        _pendingLanguageRefresh = null;
        ApplyLanguage(language);
    }

    private void ReleaseTransientMouseState()
    {
        HideBuildSettingsToolTip();
        ClearGameGridHeaderToolTip();

        if (ActiveControl is ComboBox comboBox && comboBox.DroppedDown)
        {
            comboBox.DroppedDown = false;
        }

        if (ActiveControl is Control activeControl)
        {
            activeControl.Capture = false;
        }

        Capture = false;
        ReleaseCapture();
        ActiveControl = null;
    }

    private void HideBuildSettingsToolTip()
    {
        var wasActive = _buildSettingsToolTip.Active;
        _buildSettingsToolTip.Active = false;
        _buildSettingsToolTip.Active = wasActive;
    }

    private void comboBoxMinRomSize_SelectedIndexChanged(object? sender, EventArgs e)
    {
        SyncFlashCartModeWithCurrentSettings();
        RefreshGameBatterylessPatchSelectionValues();
        RefreshGamePatchOptionUi(persistGameList: true);
        SaveCurrentAppSettings();
        RefreshBuildCapacitySummary();
    }

    private void comboBoxGbaMenuType_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_suppressFlashCartSync || _currentCompilationFamily != CompilationFamily.Gba) return;
        SyncBaseMenuPathWithSelectedGbaOptions();
        RefreshBackgroundOrFontUi();
        SaveCurrentAppSettings();
        RefreshBuildCapacitySummary();
    }

    private void checkBoxMinimalMenuPackage_CheckedChanged(object? sender, EventArgs e)
    {
        if (_checkBoxMinimalMenuPackage.Checked && _checkBoxSplitOutput.Checked)
        {
            _checkBoxSplitOutput.Checked = false;
        }
        _checkBoxSplitOutput.Enabled = !_checkBoxMinimalMenuPackage.Checked;
        SaveCurrentAppSettings();
        RefreshBuildCapacitySummary();
    }

    private async void comboBoxFlashCartMode_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_suppressFlashCartSync || !_flashCartModeSelectionInitialized || _comboBoxFlashCartMode.SelectedItem is not FlashCartModeOption flashCartMode)
        {
            return;
        }

        if (flashCartMode.Mode == FlashCartMode.FcMbcx)
        {
            ShowFcWorkspace();
            return;
        }
        var targetFamily = GetCompilationFamilyForMode(flashCartMode.Mode);
        if (targetFamily != _currentCompilationFamily)
        {
            _suppressFlashCartSync = true;
            try
            {
                await SwitchCompilationFamilyAsync(targetFamily, flashCartMode.Mode);
            }
            finally
            {
                _suppressFlashCartSync = false;
            }

            return;
        }

        ApplyFlashCartPreset(flashCartMode.Mode);
        RefreshGameBatterylessPatchSelectionValues();
        RefreshGamePatchOptionUi(persistGameList: true);
        SaveCurrentAppSettings();
        RefreshBuildCapacitySummary();
    }

    private void buildSettingControl_Changed(object? sender, EventArgs e)
    {
        if (_currentCompilationFamily == CompilationFamily.Gbc)
        {
            RefreshBackgroundOrFontUi();
        }
        SaveCurrentAppSettings();
        RefreshBuildCapacitySummary();
    }

    private void capacitySummaryRelevantControl_Changed(object? sender, EventArgs e)
    {
        RefreshBuildCapacitySummary();
    }

    private void ApplyFlashCartPreset(FlashCartMode mode)
    {
        if (mode == FlashCartMode.Custom || mode == FlashCartMode.GbcMaxMbcx512K)
        {
            return;
        }

        _suppressFlashCartSync = true;
        if (mode is FlashCartMode.WaterlineVirtualIron or FlashCartMode.Chm1 or FlashCartMode.Chm1NoBattery)
        {
            var minRomIndex = _minRomSizeOptions.FindIndex(option => option.Value == 4 * 1024 * 1024);
            if (minRomIndex >= 0)
            {
                _comboBoxMinRomSize.SelectedIndex = minRomIndex;
            }
        }

        _flashCartBatteryPresent = ResolveBatteryPresent(mode, _flashCartBatteryPresent);
        _suppressFlashCartSync = false;
        RefreshFlashCartModeOptions(mode);
        SyncBaseMenuPathWithSelectedGbaOptions();
    }

    private void SyncCartridgeTypeAvailability()
    {
        if (_comboBoxCartridgeType.DataSource is null)
        {
            return;
        }

        var selectableCartridges = GetUiSelectableCartridges().ToList();
        if (selectableCartridges.Count == 0)
        {
            return;
        }

        var lockToWaterlineCompatibleFlash =
            _currentCompilationFamily == CompilationFamily.Gba &&
            GetSelectedFlashCartMode() == FlashCartMode.WaterlineVirtualIron;
        var targetCartridgeId = lockToWaterlineCompatibleFlash
            ? CartridgeDefinition.WaterlineCompatibleId
            : _comboBoxCartridgeType.SelectedItem is CartridgeDefinition currentCartridge
                ? currentCartridge.Id
                : GetDefaultCartridgeIdForFamily(_currentCompilationFamily);

        var targetIndex = selectableCartridges
            .FindIndex(cartridge => cartridge.Id == targetCartridgeId);
        if (targetIndex < 0)
        {
            targetIndex = 0;
        }

        _comboBoxCartridgeType.Enabled = !lockToWaterlineCompatibleFlash;
        if (_comboBoxCartridgeType.SelectedIndex != targetIndex)
        {
            _comboBoxCartridgeType.SelectedIndex = targetIndex;
        }
    }

    private void RefreshGameSaveTypeDisplayValues()
    {
        if (!SupportsPatchWorkflow())
        {
            foreach (var game in _games)
            {
                game.SaveTypeDisplay = string.Empty;
            }

            return;
        }

        foreach (var game in _games)
        {
            if (game.IsLoadingPlaceholder)
            {
                game.SaveTypeDisplay = string.Empty;
                continue;
            }

            var identifier = string.IsNullOrWhiteSpace(game.SaveTypeIdentifier) ? null : game.SaveTypeIdentifier;
            game.SaveTypeDisplay = FormatSaveTypeDisplay(new DetectedSaveTypeInfo(game.SaveTypeKind, identifier));
        }
    }

    private void RefreshGameGbcHeaderDisplayValues()
    {
        if (_currentCompilationFamily != CompilationFamily.Gbc)
        {
            return;
        }

        foreach (var game in _games)
        {
            RefreshGameHeaderMetadata(game);
        }
    }

    private void RefreshGameSramPatchRequirementDisplayValues()
    {
        if (!SupportsPatchWorkflow())
        {
            foreach (var game in _games)
            {
                game.SramPatchRequirementDisplay = string.Empty;
                game.ApplySramPatch = false;
            }

            return;
        }

        foreach (var game in _games)
        {
            if (game.IsLoadingPlaceholder)
            {
                game.SramPatchRequirementDisplay = string.Empty;
                game.ApplySramPatch = false;
                continue;
            }

            var targetIdentifier = string.IsNullOrWhiteSpace(game.SramPatchTargetIdentifier)
                ? null
                : game.SramPatchTargetIdentifier;
            game.SramPatchRequirementDisplay = FormatSramPatchRequirementDisplay(
                game.SaveTypeKind,
                targetIdentifier,
                game.SramAlreadyPatched,
                game.BatterylessAlreadyPatched);
            game.ApplySramPatch = ResolveApplySramPatchSelection(
                game.SaveTypeKind,
                targetIdentifier,
                game.SramAlreadyPatched,
                game.BatterylessAlreadyPatched,
                game.ApplySramPatch);
        }
    }

    private void RefreshGameBatterylessPatchSelectionValues()
    {
        if (!SupportsPatchWorkflow())
        {
            foreach (var game in _games)
            {
                game.BatterylessStatusDisplay = string.Empty;
                game.ApplyBatterylessPatch = false;
                game.ApplyBatterylessPatchOverride = null;
            }

            RefreshGameSizeDisplayValues();
            return;
        }

        foreach (var game in _games)
        {
            if (game.IsLoadingPlaceholder)
            {
                game.BatterylessStatusDisplay = string.Empty;
                game.ApplyBatterylessPatch = false;
                continue;
            }

            var inspection = new BatterylessPatchInspection(
                game.BatterylessAlreadyPatched,
                game.BatterylessHasPayloadSpace,
                game.BatterylessCanExpand);
            var targetIdentifier = string.IsNullOrWhiteSpace(game.SramPatchTargetIdentifier)
                ? null
                : game.SramPatchTargetIdentifier;
            game.BatterylessStatusDisplay = FormatBatterylessStatusDisplay(
                game.SaveTypeKind,
                targetIdentifier,
                game.SramAlreadyPatched,
                game.ApplySramPatch,
                inspection);
            game.ApplyBatterylessPatch = ResolveApplyBatterylessPatchSelection(
                game.SaveTypeKind,
                targetIdentifier,
                game.SramAlreadyPatched,
                game.ApplySramPatch,
                inspection,
                game.ApplyBatterylessPatchOverride);
        }

        RefreshGameSizeDisplayValues();
    }

    private void RefreshGameSizeDisplayValues()
    {
        foreach (var game in _games)
        {
            PopulateGameSizeDisplayValues(game);
        }
    }

    private void PopulateGameSizeDisplayValues(GameEntryModel game)
    {
        if (_comboBoxMinRomSize.SelectedItem is not MinRomSizeOption minRomSize ||
            !TryGetGameRomFileLength(game, out var rawSizeBytes))
        {
            game.FileSizeDisplay = string.Empty;
            game.OccupiedSizeDisplay = string.Empty;
            return;
        }

        game.FileSizeDisplay = FormatCompactSizeInMiB(rawSizeBytes);
        if (_currentCompilationFamily == CompilationFamily.Gbc &&
            GameBoyRomHeaderService.TryRead(game.RomPath, out var gbcMetadata))
        {
            var occupiedSizeBytes = RoundUpToAllocationUnit(
                Math.Max(gbcMetadata.EffectiveRomSizeBytes, minRomSize.Value),
                minRomSize.Value);
            game.OccupiedSizeDisplay = FormatCompactSizeInMiB(occupiedSizeBytes);
            return;
        }

        var patchedSizeBytes = EstimatePatchedGameRomPayloadBytes(game, rawSizeBytes, minRomSize.Value);
        game.OccupiedSizeDisplay = FormatCompactSizeInMiB(patchedSizeBytes);
    }

    private long EstimatePatchedGameRomPayloadBytes(GameEntryModel game, long rawSizeBytes, int minRomSize)
    {
        var flashCartMode = GetSelectedFlashCartMode();
        var batteryPresent = GetEffectiveBatteryPresent();
        var sramReadyForBatteryless = IsSramReadyForBatteryless(
            game.SaveTypeKind,
            game.SramPatchTargetIdentifier,
            game.SramAlreadyPatched,
            game.ApplySramPatch);
        var shouldApplyBatterylessPatch = ShouldApplyBatterylessPatchForSizeEstimate(
            game,
            flashCartMode,
            batteryPresent,
            sramReadyForBatteryless);
        var shouldReserveBatterylessRomSize = ShouldReserveBatterylessRomSizeForSizeEstimate(
            game,
            flashCartMode,
            batteryPresent,
            sramReadyForBatteryless);

        var sizeBytes = Math.Max(1L, rawSizeBytes);
        if (shouldApplyBatterylessPatch && game.BatterylessCanExpand && !game.BatterylessHasPayloadSpace)
        {
            sizeBytes = checked(sizeBytes + BatterylessPatchService.ExpansionSize);
        }

        var effectiveMinRomSize = shouldReserveBatterylessRomSize
            ? Math.Max(minRomSize, BatterylessPatchService.MinimumPatchedRomSize)
            : minRomSize;
        return RoundUpToAllocationUnit(Math.Max(sizeBytes, effectiveMinRomSize), minRomSize);
    }

    private static bool ShouldApplyBatterylessPatchForSizeEstimate(
        GameEntryModel game,
        FlashCartMode flashCartMode,
        bool batteryPresent,
        bool sramReadyForBatteryless)
    {
        if (!ShouldIncludeBatterylessPatchCapacity(flashCartMode, batteryPresent) ||
            !game.ApplyBatterylessPatch ||
            !sramReadyForBatteryless)
        {
            return false;
        }

        return game.SaveTypeKind != DetectedSaveTypeKind.None;
    }

    private static bool ShouldReserveBatterylessRomSizeForSizeEstimate(
        GameEntryModel game,
        FlashCartMode flashCartMode,
        bool batteryPresent,
        bool sramReadyForBatteryless)
    {
        if (!ShouldIncludeBatterylessPatchCapacity(flashCartMode, batteryPresent))
        {
            return false;
        }

        if (game.SaveTypeKind == DetectedSaveTypeKind.None)
        {
            return false;
        }

        return game.BatterylessAlreadyPatched || (sramReadyForBatteryless && game.ApplyBatterylessPatch);
    }

    private static bool ShouldIncludeBatterylessPatchCapacity(FlashCartMode flashCartMode, bool batteryPresent) =>
        !batteryPresent &&
        flashCartMode.IsBatterylessMode();

    private static bool TryGetGameRomFileLength(GameEntryModel game, out long rawSizeBytes)
    {
        rawSizeBytes = 0;
        if (string.IsNullOrWhiteSpace(game.RomPath))
        {
            return false;
        }

        var normalizedPath = NormalizeRomPath(game.RomPath);
        if (!File.Exists(normalizedPath))
        {
            return false;
        }

        rawSizeBytes = new FileInfo(normalizedPath).Length;
        return true;
    }

    private static string FormatCompactSizeInMiB(long bytes) =>
        $"{FormatByteSizeValue(bytes)}M";

    private void RefreshGamePatchOptionUi(bool persistGameList = false)
    {
        if (_dataGridViewGames is not null)
        {
            _dataGridViewGames.Refresh();
        }

        if (persistGameList)
        {
            SaveCurrentGameList();
        }
    }

    private void StartGameGridHeaderToolTipCountdown(int columnIndex)
    {
        _hoveredGameGridHitType = DataGridViewHitTestType.ColumnHeader;
        _hoveredGameGridColumnIndex = columnIndex;
        _hoveredGameGridRowIndex = -1;
        _gameGridHeaderToolTipTimer.Stop();
        if (_dataGridViewGames is not null)
        {
            _gameGridHeaderToolTip.Hide(_dataGridViewGames);
        }

        _gameGridHeaderToolTipTimer.Start();
    }

    private void StartGameGridCellToolTipCountdown(int rowIndex, int columnIndex)
    {
        _hoveredGameGridHitType = DataGridViewHitTestType.Cell;
        _hoveredGameGridRowIndex = rowIndex;
        _hoveredGameGridColumnIndex = columnIndex;
        _gameGridHeaderToolTipTimer.Stop();
        if (_dataGridViewGames is not null)
        {
            _gameGridHeaderToolTip.Hide(_dataGridViewGames);
        }

        _gameGridHeaderToolTipTimer.Start();
    }

    private void RegisterGameGridToolTipDismissHandlers(Control root)
    {
        if (root != _dataGridViewGames)
        {
            root.MouseEnter += dismissGameGridToolTip_MouseActivity;
            root.MouseMove += dismissGameGridToolTip_MouseActivity;
            root.MouseDown += dismissGameGridToolTip_MouseActivity;
        }

        foreach (Control child in root.Controls)
        {
            RegisterGameGridToolTipDismissHandlers(child);
        }
    }

    private void dismissGameGridToolTip_MouseActivity(object? sender, EventArgs e)
    {
        if (sender == _dataGridViewGames)
        {
            return;
        }

        ClearGameGridHeaderToolTip();
    }

    private void ClearGameGridHeaderToolTip()
    {
        _hoveredGameGridHitType = DataGridViewHitTestType.None;
        _hoveredGameGridColumnIndex = -1;
        _hoveredGameGridRowIndex = -1;
        _gameGridHeaderToolTipTimer.Stop();
        if (_dataGridViewGames is not null)
        {
            _gameGridHeaderToolTip.Hide(_dataGridViewGames);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    private void gameGridHeaderToolTipTimer_Tick(object? sender, EventArgs e)
    {
        _gameGridHeaderToolTipTimer.Stop();
        if (_hoveredGameGridColumnIndex < 0 || _hoveredGameGridColumnIndex >= _dataGridViewGames.Columns.Count)
        {
            return;
        }

        var location = _dataGridViewGames.PointToClient(Cursor.Position);
        var hit = _dataGridViewGames.HitTest(location.X, location.Y);
        if (_hoveredGameGridHitType == DataGridViewHitTestType.ColumnHeader)
        {
            if (hit.Type != DataGridViewHitTestType.ColumnHeader || hit.ColumnIndex != _hoveredGameGridColumnIndex)
            {
                return;
            }

            var headerText = GetGameGridColumnToolTip(_dataGridViewGames.Columns[_hoveredGameGridColumnIndex]);
            if (string.IsNullOrWhiteSpace(headerText))
            {
                return;
            }

            _gameGridHeaderToolTip.Show(headerText, _dataGridViewGames, location.X + 16, location.Y + 24, 10000);
            return;
        }

        if (_hoveredGameGridHitType != DataGridViewHitTestType.Cell ||
            hit.Type != DataGridViewHitTestType.Cell ||
            hit.RowIndex != _hoveredGameGridRowIndex ||
            hit.ColumnIndex != _hoveredGameGridColumnIndex)
        {
            return;
        }

        var cellText = GetGameGridCellToolTip(_hoveredGameGridRowIndex, _hoveredGameGridColumnIndex);
        if (string.IsNullOrWhiteSpace(cellText))
        {
            return;
        }

        var cellBounds = _dataGridViewGames.GetCellDisplayRectangle(
            _hoveredGameGridColumnIndex,
            _hoveredGameGridRowIndex,
            false);
        var toolTipX = Math.Max(cellBounds.X + 4, 4);
        var toolTipY = Math.Max(cellBounds.Bottom + 4, 4);
        _gameGridHeaderToolTip.Show(cellText, _dataGridViewGames, toolTipX, toolTipY, 10000);
    }

    private string GetGameGridColumnToolTip(DataGridViewColumn column)
    {
        if (string.Equals(column.Name, SequenceColumnName, StringComparison.Ordinal))
        {
            return T("grid_tooltip", "sequence", "Shows the current row number in the list.");
        }

        return column.DataPropertyName switch
        {
            nameof(GameEntryModel.Enabled) =>
                T("grid_tooltip", "use", "Controls whether this ROM is included when building."),
            nameof(GameEntryModel.Title) =>
                T("grid_tooltip", "title", "Name shown in the menu. Editing this does not rename the ROM file."),
            nameof(GameEntryModel.GameTitle) =>
                T("grid_tooltip", "game_title", "Original title read from the ROM header. This is read-only and does not directly change the menu title."),
            nameof(GameEntryModel.MbcTypeDisplay) =>
                T("grid_tooltip", "mbc_type", "Detected mapper/controller family read from the ROM header."),
            nameof(GameEntryModel.SaveSupportDisplay) =>
                T("grid_tooltip", "save_support", "Derived from the MBC type. Shows whether this ROM is a battery-backed save title."),
            nameof(GameEntryModel.GbcDedicatedSaveAllocation) =>
                T("grid_tooltip", "save_allocation", "Defaults from the ROM MBC battery type. Uncheck battery-backed RAM games to force them to use the shared area."),
            nameof(GameEntryModel.RamSizeDisplay) =>
                T("grid_tooltip", "ram_size", "Detected external RAM size code read from the ROM header."),
            nameof(GameEntryModel.RomSizeDisplay) =>
                T("grid_tooltip", "rom_size", "Detected ROM size code read from the ROM header."),
            nameof(GameEntryModel.RomPath) =>
                T("grid_tooltip", "rom_path", "Full path to the source ROM file."),
            nameof(GameEntryModel.SaveTypeDisplay) =>
                T("grid_tooltip", "save_type", "Detected original save chip/signature from the ROM."),
            nameof(GameEntryModel.SramPatchRequirementDisplay) =>
                T("grid_tooltip", "patch_conversion", "Shows whether this ROM needs SRAM patch conversion and whether the current builder supports it."),
            nameof(GameEntryModel.ApplySramPatch) =>
                T("grid_tooltip", "sram_patch", "Controls whether to apply the SRAM patch for this ROM when a supported patch target is available."),
            nameof(GameEntryModel.ApplyBatterylessPatch) =>
                T("grid_tooltip", "batteryless_patch", "Controls whether to apply the batteryless patch for this ROM. Only available in No Battery mode after SRAM save handling is ready."),
            nameof(GameEntryModel.BatterylessStatusDisplay) =>
                T("grid_tooltip", "batteryless_status", "Shows whether the ROM is already batteryless-patched, whether SRAM prep is complete, and whether there is room to place the batteryless payload."),
            nameof(GameEntryModel.FileSizeDisplay) =>
                T("grid_tooltip", "file_size", "Shows the original ROM file size in MiB."),
            nameof(GameEntryModel.OccupiedSizeDisplay) =>
                T("grid_tooltip", "occupied_size", "Shows the final cartridge occupancy in MiB after alignment and batteryless expansion."),
            nameof(GameEntryModel.SaveSlot) =>
                T("grid_tooltip", "save_slot", "Menu-managed save slot number. Games can share a slot. Leave empty or set none for no slot."),
            nameof(GameEntryModel.HiddenKeys) =>
                T("grid_tooltip", "hidden_keys", "This ROM only appears when these keys are held while the menu boots, for example L,R,DOWN."),
            nameof(GameEntryModel.Map256m) =>
                T("grid_tooltip", "map_256m", "Places the ROM on a 256M-aligned region to avoid mapper issues on some cartridges."),
            nameof(GameEntryModel.TitleFont) =>
                T("grid_tooltip", "font", "Menu font index used to draw the title."),
            _ => string.Empty,
        };
    }

    private string GetGameGridCellToolTip(int rowIndex, int columnIndex)
    {
        if (rowIndex < 0 ||
            columnIndex < 0 ||
            rowIndex >= _dataGridViewGames.Rows.Count ||
            columnIndex >= _dataGridViewGames.Columns.Count)
        {
            return string.Empty;
        }

        if (_dataGridViewGames.Rows[rowIndex].DataBoundItem is GameEntryModel { MdRom: not null } mdGame)
            return mdGame.MdRom.SourceLabel + "\n" + mdGame.MdStatus + "\n" + mdGame.MdRom.Assessment.Message;
        var column = _dataGridViewGames.Columns[columnIndex];
        if (column is DataGridViewCheckBoxColumn)
        {
            if (column.DataPropertyName == nameof(GameEntryModel.ApplySramPatch) &&
                _dataGridViewGames.Rows[rowIndex].DataBoundItem is GameEntryModel sramGame &&
                !IsSramPatchSelectable(sramGame))
            {
                return sramGame.SramPatchRequirementDisplay;
            }

            if (column.DataPropertyName == nameof(GameEntryModel.ApplyBatterylessPatch) &&
                _dataGridViewGames.Rows[rowIndex].DataBoundItem is GameEntryModel game &&
                !IsBatterylessPatchSelectable(game))
            {
                return game.BatterylessStatusDisplay;
            }

            return string.Empty;
        }

        var cell = _dataGridViewGames.Rows[rowIndex].Cells[columnIndex];
        return cell.FormattedValue?.ToString()?.Trim() ?? string.Empty;
    }

    private string FormatSaveTypeDisplay(DetectedSaveTypeInfo info)
    {
        var label = info.Kind switch
        {
            DetectedSaveTypeKind.None => T("value", "save_type_none", "No Save"),
            DetectedSaveTypeKind.Sram => "SRAM",
            DetectedSaveTypeKind.Eeprom => "EEPROM",
            DetectedSaveTypeKind.Flash => "FLASH",
            DetectedSaveTypeKind.Flash512 => "FLASH512",
            DetectedSaveTypeKind.Flash1M => "FLASH1M",
            _ => T("value", "save_type_unknown", "Unknown"),
        };

        if (string.IsNullOrWhiteSpace(info.Identifier) ||
            string.Equals(info.Identifier, label, StringComparison.OrdinalIgnoreCase) ||
            info.Kind == DetectedSaveTypeKind.Unknown ||
            info.Kind == DetectedSaveTypeKind.None)
        {
            return label;
        }

        if (info.Kind == DetectedSaveTypeKind.Sram &&
            !info.Identifier.StartsWith("SRAM", StringComparison.OrdinalIgnoreCase))
        {
            return Tf(
                "value",
                "save_type_sram_patched_from",
                "SRAM (patched from {0})",
                info.Identifier);
        }

        return $"{label} ({info.Identifier})";
    }

    private string FormatSramPatchRequirementDisplay(
        DetectedSaveTypeKind saveTypeKind,
        string? targetIdentifier,
        bool sramAlreadyPatched,
        bool batterylessAlreadyPatched)
    {
        if (batterylessAlreadyPatched)
        {
            return T("value", "sram_patch_already_batteryless", "Already Batteryless Patched");
        }

        if (sramAlreadyPatched)
        {
            return T("value", "sram_patch_already_converted", "Already Converted");
        }

        if (saveTypeKind == DetectedSaveTypeKind.None || saveTypeKind == DetectedSaveTypeKind.Sram)
        {
            return T("value", "sram_patch_not_needed", "Not Needed");
        }

        if (!string.IsNullOrWhiteSpace(targetIdentifier))
        {
            return Tf("value", "sram_patch_required_target", "Required ({0})", targetIdentifier);
        }

        if (saveTypeKind == DetectedSaveTypeKind.Unknown)
        {
            return T("value", "sram_patch_unknown", "Unknown");
        }

        return T("value", "sram_patch_unsupported", "Required (Unsupported)");
    }

    private string FormatBatterylessStatusDisplay(
        DetectedSaveTypeKind saveTypeKind,
        string? targetIdentifier,
        bool sramAlreadyPatched,
        bool applySramPatch,
        BatterylessPatchInspection inspection)
    {
        if (saveTypeKind == DetectedSaveTypeKind.None)
        {
            return T("value", "batteryless_not_needed", "Not Needed");
        }

        if (inspection.AlreadyPatched)
        {
            return T("value", "batteryless_already_patched", "Already Patched");
        }

        if (!IsSramReadyForBatteryless(saveTypeKind, targetIdentifier, sramAlreadyPatched, applySramPatch))
        {
            if (HasSramPatchTarget(saveTypeKind, targetIdentifier))
            {
                return T("value", "batteryless_needs_sram_patch", "Needs SRAM Patch");
            }

            return T("value", "batteryless_sram_unsupported", "SRAM Prep Unsupported");
        }

        if (inspection.HasPayloadSpace)
        {
            return T("value", "batteryless_space_ok", "Space OK");
        }

        if (inspection.CanExpand)
        {
            return T("value", "batteryless_expand_needed", "Needs Expand");
        }

        return T("value", "batteryless_no_space", "No Space");
    }

    private string GetCurrentOutputDirectory()
    {
        var outputPath = BuildManagedOutputPath(_textBoxOutputPath.Text);
        return Path.GetDirectoryName(outputPath) ?? GetManagedOutputDirectory();
    }

    private void RefreshBuildCapacitySummary()
    {
        if (_dataGridViewGames.Columns[nameof(GameEntryModel.MdStatus)] is { } mdColumn) mdColumn.Visible = _games.Any(g => g.MdRom is not null || g.OtherRom is not null);
        if (_games.Any(g => g.MdRom is not null && g.Enabled))
        {
            _buildBlockedByCapacity = !MdBuildSelected;
            _capacitySummaryErrorMessage = null;
            _labelCapacitySummary.Text = MdBuildSelected?"MD / MBCX · 32 MiB ROM · 512 KiB SRAM · 02第4槽":"请仅勾选同一平台的游戏后制作。";
            _labelSaveCapacitySummary.Visible = false; _buttonBuild.Enabled = !_gamesLoading && MdBuildSelected;
            return;
        }
        if (_labelCapacitySummary is null || _labelSaveCapacitySummary is null)
        {
            return;
        }

        if (_currentCompilationFamily == CompilationFamily.Gbc)
        {
            UpdateGbcSaveCapacitySummaryLabel();
        }

        try
        {
            _capacitySummaryErrorMessage = null;
            var patchedRequest = CreateBuildRequest(requireEnabledGames: false);
            var originalRequest = CreateUnpatchedCapacityBuildRequest(patchedRequest);
            var originalEstimate = _builder.AnalyzeLayoutQuick(originalRequest, T);
            var patchedEstimate = _builder.AnalyzeLayoutQuick(patchedRequest, T);
            _buildBlockedByCapacity = patchedEstimate.ExceedsCapacity;
            if (_currentCompilationFamily == CompilationFamily.Gbc)
            {
                var saveSummary = ComputeGbcSaveCapacitySummary();
                var saveOverflow = saveSummary.AllocatedBytes > saveSummary.TotalBytes;
                _labelSaveCapacitySummary.Visible = true;
                _labelSaveCapacitySummary.ForeColor = saveOverflow
                    ? Color.Firebrick
                    : SystemColors.ControlText;
                _labelSaveCapacitySummary.Text = saveOverflow
                    ? Tf(
                        "ui",
                        "gbc_save_capacity_summary_overflow",
                        "存档 已分配/总大小 {0}/{1}",
                        FormatStorageSizeShort(saveSummary.AllocatedBytes),
                        FormatStorageSizeShort(saveSummary.TotalBytes))
                    : Tf(
                        "ui",
                        "gbc_save_capacity_summary",
                        "存档 已分配/总大小 {0}/{1}",
                        FormatStorageSizeShort(saveSummary.AllocatedBytes),
                        FormatStorageSizeShort(saveSummary.TotalBytes));

                _labelCapacitySummary.ForeColor = _buildBlockedByCapacity
                    ? Color.Firebrick
                    : SystemColors.ControlText;
                _labelCapacitySummary.Text = _buildBlockedByCapacity
                    ? Tf(
                        "ui",
                        "gbc_capacity_summary_overflow",
                        "已用/总大小 {0}/{1}",
                        FormatStorageSizeShort(patchedEstimate.UsedPayloadBytes),
                        FormatStorageSizeShort(patchedEstimate.PayloadCapacityBytes))
                    : Tf(
                        "ui",
                        "gbc_capacity_summary",
                        "已用/总大小 {0}/{1}",
                        FormatStorageSizeShort(patchedEstimate.UsedPayloadBytes),
                        FormatStorageSizeShort(patchedEstimate.PayloadCapacityBytes));
            }
            else
            {
                _labelSaveCapacitySummary.Visible = false;
                _labelSaveCapacitySummary.Text = T("ui", "gbc_save_capacity_summary_unavailable", "存档 已分配/总大小 --/--");
                _labelSaveCapacitySummary.ForeColor = SystemColors.ControlText;
                _labelCapacitySummary.ForeColor = _buildBlockedByCapacity
                    ? Color.Firebrick
                    : SystemColors.ControlText;
                _labelCapacitySummary.Text = _buildBlockedByCapacity
                    ? Tf(
                        "ui",
                        "capacity_summary_overflow",
                        "Over Capacity: Before/After/Capacity {0}/{1}/{2}MiB",
                        FormatByteSizeValue(originalEstimate.UsedPayloadBytes),
                        FormatByteSizeValue(patchedEstimate.UsedPayloadBytes),
                        FormatByteSizeValue(patchedEstimate.PayloadCapacityBytes))
                    : Tf(
                        "ui",
                        "capacity_summary",
                        "Before/After/Capacity {0}/{1}/{2}MiB",
                        FormatByteSizeValue(originalEstimate.UsedPayloadBytes),
                        FormatByteSizeValue(patchedEstimate.UsedPayloadBytes),
                        FormatByteSizeValue(patchedEstimate.PayloadCapacityBytes));
            }
        }
        catch (Exception ex)
        {
            _buildBlockedByCapacity = false;
            _capacitySummaryErrorMessage = ex.Message;
            _labelSaveCapacitySummary.Visible = _currentCompilationFamily == CompilationFamily.Gbc;
            _labelSaveCapacitySummary.ForeColor = SystemColors.ControlText;
            _labelSaveCapacitySummary.Text = T("ui", "gbc_save_capacity_summary_unavailable", "存档 已分配/总大小 --/--");
            _labelCapacitySummary.ForeColor = SystemColors.ControlText;
            _labelCapacitySummary.Text = _currentCompilationFamily == CompilationFamily.Gbc
                ? T("ui", "gbc_capacity_summary_unavailable", "已用/总大小 --/--")
                : T("ui", "capacity_summary_unavailable", "Before/After/Capacity --/--/--MiB");
            AppendLog(Tf("log", "capacity_summary_failed", "Capacity analysis failed: {0}", ex.Message));
        }

        RefreshBuildButtonAvailability();
        RefreshIdleStatusMessage();
    }

    private void UpdateGbcSaveCapacitySummaryLabel()
    {
        var saveSummary = ComputeGbcSaveCapacitySummary();
        var saveOverflow = saveSummary.AllocatedBytes > saveSummary.TotalBytes;
        _labelSaveCapacitySummary.Visible = true;
        _labelSaveCapacitySummary.ForeColor = saveOverflow
            ? Color.Firebrick
            : SystemColors.ControlText;
        _labelSaveCapacitySummary.Text = saveOverflow
            ? Tf(
                "ui",
                "gbc_save_capacity_summary_overflow",
                "存档超限 已分配/总大小 {0}/{1}",
                FormatStorageSizeShort(saveSummary.AllocatedBytes),
                FormatStorageSizeShort(saveSummary.TotalBytes))
            : Tf(
                "ui",
                "gbc_save_capacity_summary",
                "存档 已分配/总大小 {0}/{1}",
                FormatStorageSizeShort(saveSummary.AllocatedBytes),
                FormatStorageSizeShort(saveSummary.TotalBytes));
    }

    private GbcSaveCapacitySummary ComputeGbcSaveCapacitySummary()
    {
        var allocatedBanks = _games
            .Where(game => !game.IsLoadingPlaceholder && game.Enabled && game.GbcDedicatedSaveAllocation && game.GbcEffectiveRamBankCount > 0)
            .Sum(game => game.GbcEffectiveRamBankCount);
        var allocatedBytes = allocatedBanks * GameBoyRomHeaderService.RamBankSize;
        var totalBytes = CompilationBuilder.GetGbcIndependentSaveCapacityBytes();
        return new GbcSaveCapacitySummary(allocatedBytes, totalBytes);
    }

    private static BuildRequest CreateUnpatchedCapacityBuildRequest(BuildRequest request)
    {
        var games = request.Games
            .Select(game => game with { ApplyBatterylessPatch = false })
            .ToList();
        return request with { Games = games };
    }

    private PendingImportCapacityContext? CreatePendingImportCapacityContext(IReadOnlyList<PendingImportFile> pendingImports)
    {
        if (pendingImports.Count == 0)
        {
            return null;
        }

        BuildLayoutEstimate estimate;
        BuildRequest request;
        try
        {
            request = CreateBuildRequest(requireEnabledGames: false);
            estimate = _builder.AnalyzeLayoutQuick(request, T);
        }
        catch
        {
            return null;
        }

        long addedPayloadBytes = 0;
        foreach (var pendingImport in pendingImports)
        {
            addedPayloadBytes += EstimatePendingImportPayloadBytes(pendingImport, request);
        }

        return new PendingImportCapacityContext(request, estimate, addedPayloadBytes);
    }

    private void RefreshBuildButtonAvailability()
    {
        if (_buttonBuild is null || _buildCancellation is not null)
        {
            return;
        }

        _buttonBuild.Enabled = !_gamesLoading && !_buildBlockedByCapacity && string.IsNullOrWhiteSpace(_capacitySummaryErrorMessage);
    }

    private void RefreshIdleStatusMessage()
    {
        if (_labelStatus is null || _gamesLoading || _buildCancellation is not null)
        {
            return;
        }

        _labelStatus.Text = !string.IsNullOrWhiteSpace(_capacitySummaryErrorMessage)
            ? _capacitySummaryErrorMessage
            : _buildBlockedByCapacity
                ? T("status", "capacity_exceeded", "Capacity Exceeded")
                : T("status", "ready", "Ready");
    }

    private void LogBatterylessExpansionCapacityOverflow(
        PendingImportCapacityContext? context,
        IReadOnlyList<ResolvedGameEntryData> resolvedImportedGames)
    {
        if (context is null || resolvedImportedGames.Count == 0)
        {
            return;
        }

        var affectedTitles = new List<string>();
        long analyzedAddedPayloadBytes = 0;
        foreach (var resolvedImportedGame in resolvedImportedGames)
        {
            var importedModel = CreateGameEntryModel(resolvedImportedGame);
            var buildGame = CreateBuildGameEntry(importedModel, 1);
            analyzedAddedPayloadBytes += _builder.EstimateGamePayloadBytesQuick(buildGame, context.Request, T);
            if (buildGame.ApplyBatterylessPatch && buildGame.BatterylessRequiresExpansion)
            {
                affectedTitles.Add(buildGame.Title);
            }
        }

        if (affectedTitles.Count == 0)
        {
            return;
        }

        var projectedRawPayloadBytes = context.Estimate.UsedPayloadBytes + context.RawAddedPayloadBytes;
        var projectedAnalyzedPayloadBytes = context.Estimate.UsedPayloadBytes + analyzedAddedPayloadBytes;
        if (projectedRawPayloadBytes > context.Estimate.PayloadCapacityBytes ||
            projectedAnalyzedPayloadBytes <= context.Estimate.PayloadCapacityBytes)
        {
            return;
        }

        var overflowBytes = projectedAnalyzedPayloadBytes - context.Estimate.PayloadCapacityBytes;
        AppendLog(Tf(
            "log",
            "batteryless_expand_capacity_exceeded",
            "Batteryless patch expansion overflow: {0}. Current ROM size exceeds capacity by {1} (projected {2} / total {3}).",
            string.Join(", ", affectedTitles.Distinct(StringComparer.OrdinalIgnoreCase)),
            FormatByteSize(overflowBytes),
            FormatByteSize(projectedAnalyzedPayloadBytes),
            FormatByteSize(context.Estimate.PayloadCapacityBytes)));
    }

    private static long EstimatePendingImportPayloadBytes(PendingImportFile pendingImport, BuildRequest request)
    {
        if (request.FlashCartMode == FlashCartMode.GbcMaxMbcx512K &&
            GameBoyRomHeaderService.TryRead(pendingImport.Candidate.RomPath, out var metadata))
        {
            return EstimateImportedRomPayloadBytes(
                metadata.EffectiveRomSizeBytes,
                request.MinRomSize);
        }

        return EstimateImportedRomPayloadBytes(
            pendingImport.Candidate.EstimatedSizeBytes,
            request.MinRomSize);
    }

    private static long EstimateImportedRomPayloadBytes(long rawSizeBytes, int minRomSize)
    {
        var normalizedSize = Math.Max(1L, rawSizeBytes);
        return RoundUpToAllocationUnit(Math.Max(normalizedSize, minRomSize), minRomSize);
    }

    private static long RoundUpToAllocationUnit(long sizeBytes, int allocationUnit) =>
        ((sizeBytes + allocationUnit - 1) / allocationUnit) * allocationUnit;

    private static string FormatByteSizeValue(long bytes) =>
        (bytes / 1024d / 1024d).ToString("0.##");

    private static string FormatByteSize(long bytes) => $"{bytes / 1024d / 1024d:F2} MiB";

    private static string FormatStorageSizeShort(long bytes)
    {
        if (bytes >= 1024L * 1024L)
        {
            return $"{bytes / 1024d / 1024d:0.##}MiB";
        }

        if (bytes >= 1024L)
        {
            return $"{bytes / 1024d:0.##}KB";
        }

        return $"{bytes}B";
    }
    
    private sealed class MinRomSizeOption
    {
        public MinRomSizeOption(string name, int value)
        {
            Name = name;
            Value = value;
        }

        public string Name { get; set; }

        public int Value { get; }

        public override string ToString() => Name;
    }

    private sealed class FlashCartModeOption
    {
        public FlashCartModeOption(string name, FlashCartMode mode)
        {
            Name = name;
            Mode = mode;
        }

        public string Name { get; set; }

        public FlashCartMode Mode { get; }

        public override string ToString() => Name;
    }

    private sealed class GbaMenuTypeOption
    {
        public GbaMenuTypeOption(string name, GbaMenuType type)
        {
            Name = name;
            Type = type;
        }

        public string Name { get; set; }

        public GbaMenuType Type { get; }

        public override string ToString() => Name;
    }

    private sealed record ResolvedGameEntryData(
        string RomPath,
        string Title,
        string GameTitle,
        string SaveSlot,
        string HiddenKeys,
        bool Enabled,
        bool? ApplySramPatchOverride,
        bool? ApplyBatterylessPatchOverride,
        bool Map256m,
        DetectedSaveTypeInfo SaveTypeInfo,
        string? SramPatchTargetIdentifier,
        bool SramAlreadyPatched,
        BatterylessPatchInspection BatterylessInspection,
        int TitleFont,
        bool? GbcDedicatedSaveAllocationOverride,
        string? CoverPath,
        bool CoverCleared);

    private sealed record LoadingPlaceholderDescriptor(
        Guid Id,
        string Title,
        string RomPath);

    private sealed record PendingSavedGameLoad(
        Guid PlaceholderId,
        int Ordinal,
        SavedGameEntry SavedGame);

    private sealed record ImportPreviewResult(
        int SourceIndex,
        string SourcePath,
        IReadOnlyList<RomImportCandidate> Candidates,
        IReadOnlyList<string> LogMessages);

    private sealed record PendingImportPreparation(
        IReadOnlyList<PendingImportFile> PendingImports,
        IReadOnlyList<string> LogMessages);

    private sealed record PendingImportFile(
        Guid PlaceholderId,
        int SourceIndex,
        int Ordinal,
        RomImportCandidate Candidate,
        string SaveSlot);

    private sealed record PendingImportCapacityContext(
        BuildRequest Request,
        BuildLayoutEstimate Estimate,
        long RawAddedPayloadBytes);

    private sealed record GbcSaveCapacitySummary(
        long AllocatedBytes,
        long TotalBytes);

    private sealed record GameLoadProgress(
        Guid PlaceholderId,
        int CompletedCount,
        int TotalCount,
        int CurrentOrdinal,
        string CurrentFileName,
        bool FinalizePlaceholder,
        IReadOnlyList<ResolvedGameEntryData> ResolvedGames,
        IReadOnlyList<string> LogMessages);

    private sealed record UiState(
        string BaseMenuPath,
        string OutputPath,
        string BackgroundPath,
        string GbcFontName,
        int CartridgeId,
        int MinRomSizeValue,
        FlashCartMode FlashCartMode,
        GbaMenuType GbaMenuType,
        bool BatteryPresent,
        bool SplitOutput,
        bool MinimalMenuPackage,
        string LogText,
        int ProgressValue,
        string StatusText,
        string LanguageCode,
        FormWindowState WindowState,
        Rectangle RestoreBounds);

    private sealed record LocalizedControlBinding(
        string TextSection,
        string TextKey,
        string TextFallback,
        string? ToolTipSection,
        string? ToolTipKey,
        string? ToolTipFallback);
}
