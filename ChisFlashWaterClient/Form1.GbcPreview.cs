using GbaMultiMenuBuilder.Services;
using ClientAppConfigService = ChisFlashWaterClient.Services.AppConfigService;

namespace ChisFlashWaterClient;

public partial class Form1
{
    private void ShowGbcMenuPreviewDialog()
    {
        using var dialog = new DpiAwareForm
        {
            Text = T("ui", "gbc_menu_preview", "GBC Menu Preview"),
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(760, 640),
            MinimumSize = new Size(560, 480),
            Font = Font,
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(12),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var settings = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            Margin = Padding.Empty,
        };
        var fontLabel = CreateToolbarLabel();
        fontLabel.Text = T("ui", "gbc_font_source", "GBC Font");
        var fonts = GbcFontService.GetBuiltInFontOptions().ToList();
        var fontSelector = new ComboBox
        {
            Name = "gbcPreviewFont",
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 260,
            Margin = new Padding(6, 7, 6, 7),
            DisplayMember = "DisplayName",
            DataSource = fonts,
            BindingContext = new BindingContext(),
        };
        fontSelector.SelectedItem = fonts.FirstOrDefault(font =>
            string.Equals(font.FileName, GetSelectedGbcFontName(), StringComparison.OrdinalIgnoreCase))
            ?? fonts.FirstOrDefault();
        settings.Controls.AddRange([fontLabel, fontSelector]);

        var picture = new NearestNeighborPictureBox
        {
            Name = "gbcPreviewImage",
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = SystemColors.Control,
            Margin = new Padding(0, 8, 0, 8),
        };
        var hint = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            Margin = new Padding(4, 4, 4, 8),
        };
        layout.Controls.Add(settings, 0, 0);
        layout.Controls.Add(picture, 0, 1);
        layout.Controls.Add(hint, 0, 2);
        dialog.Controls.Add(layout);

        void RefreshPreview()
        {
            fontSelector.Enabled = !isBusy && IsSelectedGbcMenuChinese();
            fontLabel.Enabled = fontSelector.Enabled;
            Image? previous = picture.Image;
            picture.Image = null;
            previous?.Dispose();
            try
            {
                picture.Image = RenderGbcPreviewImage(BuildGbcWorkspacePreviewModel(), 2);
                SetMenuBackgroundPreviewState((Image)picture.Image.Clone(), true);
                hint.Text = IsSelectedGbcMenuChinese()
                    ? T("ui", "gbc_preview_settings_hint", "Changes are previewed immediately and will be used the next time you burn or update the menu.")
                    : T("ui", "gbc_preview_english_hint", "The English menu uses its built-in font. The selected language will be used the next time you burn or update the menu.");
            }
            catch (Exception ex)
            {
                hint.Text = ex.Message;
            }
        }

        fontSelector.SelectedIndexChanged += (_, _) =>
        {
            if (fontSelector.SelectedItem is GbcBuiltInFontOption font)
            {
                selectedGbcFontName = font.FileName;
                ClientAppConfigService.SaveGbcFontName(font.FileName);
                RefreshPreview();
            }
        };
        dialog.FormClosed += (_, _) => picture.Image?.Dispose();
        RefreshPreview();
        dialog.ShowDialog(this);
    }
}
