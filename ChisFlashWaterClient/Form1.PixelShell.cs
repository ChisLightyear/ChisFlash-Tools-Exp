namespace ChisFlashWaterClient;

public partial class Form1
{
    private string? confirmedPort;
    private string lastProgrammerPort = Services.AppConfigService.LoadLastProgrammerPort();
    private string? analyzedPort;
    private bool WorkspaceUnlocked => analyzedPort is not null && analyzedPort == GetSelectedPortName();
    private bool keepMulticartList;
    private bool analyzingShellCard;

    private string ShellText(string zh, string en) => language.Code.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? zh : en;

    private void RefreshAndConnectProgrammer()
    {
        RefreshPorts(resetAnalysisState: true);
        if (GetSelectedPortName() is not null) ConnectSelectedProgrammer();
    }

    private void ConnectSelectedProgrammer()
    {
        if (!TryGetSelectedProgrammerKind(out _, out string reason))
        {
            confirmedPort = null;
            labelStatus.Text = reason;
        }
        else if (GetSelectedPortName() is string port && confirmedPort != port)
        {
            confirmedPort = null;
            try
            {
                if (serialPorts.Values.Any(entry => entry.IsGbFlash && entry.PortName == port))
                {
                    using var connection = new WaterDeviceProtocol(port);
                    connection.OpenGbFlash();
                }
                else
                {
                    using var connection = new System.IO.Ports.SerialPort(port);
                    connection.Open();
                }
                confirmedPort = port;
                if (lastProgrammerPort != port)
                {
                    Services.AppConfigService.SaveLastProgrammerPort(port);
                    lastProgrammerPort = port;
                }
            }
            catch (Exception ex)
            {
                labelStatus.Text = GetFriendlyScanFailureMessage(ex, port);
                AppendLog(labelStatus.Text);
            }
        }
        RefreshPixelShell(isBusy);
    }

    private void RefreshPixelShell(bool busy)
    {
        if (dataGridViewRoms is null || Disposing || IsDisposed) return;
        bool ready = WorkspaceUnlocked && !analyzingShellCard;
        // 页面沿用选型下拉框的单卡／合卡分组，目录是否存在只影响列表内容。
        bool single = IsGbaSingleProfileSelected() || IsGbcRawImageMode();
        if (!WorkspaceUnlocked) keepMulticartList = false;
        if (ready && burnProfileModeSelectionInitialized && !analyzingShellCard) keepMulticartList = !single;

        if (gbaAddMenuArrow is not null && gbaAddMenuCommand is not null)
        {
            gbaAddMenuArrow.Visible = keepMulticartList &&
                (IsMbc5MaxMode() || IsGbcFreeWorkspace() || SupportsGbaCardMode() && !IsGbaMulticartUnavailable());
            gbaAddMenuArrow.Enabled = !busy && !isAnalyzingQueueImports;
            bool hasMenu = IsMbc5MaxMode() ? ActiveMbc5Layout.HasMenu :
                IsGbcFreeWorkspace() ? HasGbcFreeMenu() : HasGbaListMenu();
            gbaAddMenuArrow.AccessibleName = gbaAddMenuCommand.Text = hasMenu
                ? ShellText("更换菜单…", "Change menu…") : ShellText("添加菜单…", "Add menu…");
        }
        groupBoxList.Enabled = ready;
        comboBoxLanguage.Enabled = ready && !busy && !isAnalyzingQueueImports;
        comboBoxPorts.Enabled = !busy && !isAnalyzingQueueImports && !analyzingShellCard;
        panelProgrammerModeHost.Enabled = !busy && !isAnalyzingQueueImports && !analyzingShellCard;
        menuDevice.Enabled = ready;
        menuCartridge.Enabled &= ready;
        menuRom.Enabled &= ready;
        menuSave.Enabled &= ready;
        dataGridViewRoms.BackgroundColor = ready ? SystemColors.Window : SystemColors.ControlDark;
        buttonScan.Enabled = buttonRefreshPorts.Enabled = !busy && !isAnalyzingQueueImports && !analyzingShellCard;
        buttonScan.Visible = true;
        menuCartridge.Visible = menuRom.Visible = menuSave.Visible = true;
        menuScan.Visible = ready;
    }
}
