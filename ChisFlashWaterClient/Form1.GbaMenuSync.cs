using ChisFlashWaterClient.Services;

namespace ChisFlashWaterClient;

public partial class Form1
{
    private async Task SyncGbaMenuAsync()
    {
        if (isBusy || isAnalyzingQueueImports) return;
        string status = T("ui", "sync_gba_menu_ready", "Ready");
        SetBusy(true, T("ui", "sync_gba_menu_fetching", "Reading menu releases from GitHub…"));
        try
        {
            var releases = await GbaMenuSync.GetReleasesAsync();
            if (releases.Count == 0)
                throw new InvalidOperationException(T("ui", "sync_gba_menu_empty", "No published release contains both NS and NS Lite."));

            string title = T("ui", "sync_gba_menu", "Sync Menus…");
            using var dialog = new Form
            {
                Text = title, StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false,
                ShowInTaskbar = false, ClientSize = new Size(480, 152), AutoScaleMode = AutoScaleMode.Dpi,
            };
            var current = new Label
            {
                Left = 16, Top = 16, Width = 448,
                Text = Tf("ui", "sync_gba_menu_current", "Current: {0}",
                    GbaMenuSync.GetActiveTag() ?? T("ui", "sync_gba_menu_bundled", "Bundled menus")),
            };
            var prompt = new Label
            {
                Left = 16, Top = 43, Width = 448,
                Text = T("ui", "sync_gba_menu_choose", "Select a published menu version from GitHub:"),
            };
            var versions = new ComboBox { Left = 16, Top = 67, Width = 448, DropDownStyle = ComboBoxStyle.DropDownList };
            foreach (var release in releases)
                versions.Items.Add(release.IsPrerelease
                    ? Tf("ui", "sync_gba_menu_preview", "{0} (preview)", release.Tag)
                    : release.Tag);
            versions.SelectedIndex = 0;
            var sync = new Button { Left = 273, Top = 110, Width = 92, Text = T("ui", "sync_gba_menu_button", "Sync"), DialogResult = DialogResult.OK };
            var cancel = new Button { Left = 372, Top = 110, Width = 92, Text = T("ui", "sync_gba_menu_cancel", "Cancel"), DialogResult = DialogResult.Cancel };
            dialog.Controls.AddRange([current, prompt, versions, sync, cancel]);
            dialog.AcceptButton = sync;
            dialog.CancelButton = cancel;
            if (dialog.ShowDialog(this) != DialogResult.OK) return;

            var selected = releases[versions.SelectedIndex];
            SetBusy(true, Tf("ui", "sync_gba_menu_downloading", "Downloading NS and NS Lite from {0}…", selected.Tag));
            await GbaMenuSync.SyncAsync(selected);
            status = Tf("ui", "sync_gba_menu_done", "Menu sync complete: {0}", selected.Tag);
            AppendLog(status);
        }
        finally
        {
            SetBusy(false, status);
        }
    }
}
