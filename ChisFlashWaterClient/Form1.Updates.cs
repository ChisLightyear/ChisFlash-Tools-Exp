using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using ChisFlashWaterClient.Services;

namespace ChisFlashWaterClient;

public partial class Form1
{
    private ToolStripMenuItem menuHelp = null!;
    private ToolStripMenuItem menuCheckForUpdates = null!;
    private ToolStripMenuItem menuSyncGbaMenu = null!;
    private ClientReleaseUpdate? availableUpdate;
    private bool checkingForUpdates;

    private void BuildUpdateMenu()
    {
        menuHelp = new ToolStripMenuItem();
        menuCheckForUpdates = new ToolStripMenuItem();
        menuCheckForUpdates.Click += async (_, _) => await CheckForUpdatesAsync(showResult: true);
        menuSyncGbaMenu = new ToolStripMenuItem();
        menuSyncGbaMenu.Click += async (_, _) => await RunUiActionSafelyAsync(SyncGbaMenuAsync);
        menuHelp.DropDownItems.AddRange([menuCheckForUpdates, menuSyncGbaMenu]);
        mainMenuStrip.Items.Add(menuHelp);
    }

    private void RefreshUpdateMenuText()
    {
        menuHelp.Text = availableUpdate is null
            ? T("updates", "help", "Help")
            : T("updates", "help_available", "Help · Update available");
        menuCheckForUpdates.Text = checkingForUpdates
            ? T("updates", "checking", "Checking for updates…")
            : T("updates", "check", "Check for Updates…");
        menuSyncGbaMenu.Text = T("ui", "sync_gba_menu", "Sync Menus…");
    }

    private async Task CheckForUpdatesAsync(bool showResult)
    {
        if (checkingForUpdates) return;
        checkingForUpdates = true;
        menuCheckForUpdates.Enabled = false;
        RefreshUpdateMenuText();
        try
        {
            Version current = ClientReleaseUpdate.ParseVersion(AppDisplayInfo.Version)
                ?? throw new InvalidOperationException(T("updates", "invalid_version", "The current client version could not be read."));
            ClientReleaseUpdate latest = await ClientReleaseUpdate.FetchLatestAsync()
                ?? throw new InvalidOperationException(T("updates", "no_package", "No client package was found on the public release page."));
            if (IsDisposed || Disposing) return;
            availableUpdate = latest.Version > current ? latest : null;
            if (!showResult) return;

            if (availableUpdate is null)
            {
                MessageBox.Show(this, Tf("updates", "up_to_date",
                    "You have v{0}. No newer version was found on the public release page.", AppDisplayInfo.Version),
                    T("updates", "title", "Client Updates"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string tag = latest.IsPrerelease ? Tf("updates", "preview", "{0} (pre-release)", latest.Tag) : latest.Tag;
            if (MessageBox.Show(this, Tf("updates", "available",
                "Current version: {0}\nAvailable version: {1}\n\nOpen the GitHub download page?", AppDisplayInfo.Version, tag),
                T("updates", "title", "Client Updates"), MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                Process.Start(new ProcessStartInfo(latest.DownloadPage) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or JsonException
            or InvalidOperationException or KeyNotFoundException or Win32Exception)
        {
            if (showResult && !IsDisposed && !Disposing)
                MessageBox.Show(this, Tf("updates", "failed", "Could not check for updates.\n{0}", ex.Message),
                    T("updates", "title", "Client Updates"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            checkingForUpdates = false;
            if (!IsDisposed && !Disposing)
            {
                menuCheckForUpdates.Enabled = true;
                RefreshUpdateMenuText();
            }
        }
    }
}
