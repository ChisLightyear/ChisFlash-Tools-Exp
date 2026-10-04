using System.ComponentModel;
using System.IO.Compression;

namespace GbaMultiMenuBuilder.Fc;

// Shared embedded workspace: both desktop applications use the same metadata,
// layout and UI. No GB menu/template/save-patch controls enter this surface.
public sealed class FcWorkspaceControl : UserControl
{
    private readonly Func<string,string,string> text;
    private readonly Func<IFcCardBackend>? backendFactory;
    private readonly BindingList<FcGame> games=[];
    private readonly DataGridView grid=new(){Dock=DockStyle.Fill,AutoGenerateColumns=false,AllowUserToAddRows=false,AllowUserToDeleteRows=false,MultiSelect=true,SelectionMode=DataGridViewSelectionMode.FullRowSelect,RowHeadersVisible=false};
    private readonly Label summary=new(){AutoSize=true,Dock=DockStyle.Fill,Padding=new Padding(4)};
    private readonly Label status=new(){AutoSize=true,Dock=DockStyle.Fill,Padding=new Padding(4)};
    private readonly ProgressBar progress=new(){Dock=DockStyle.Fill,Maximum=100};
    private readonly FlowLayoutPanel actions=new(){Dock=DockStyle.Fill,AutoSize=true,WrapContents=true};
    private readonly Button cancel=new(){AutoSize=true,Enabled=false};
    private readonly Button scan=new(){AutoSize=true};
    private readonly Button burn=new(){AutoSize=true};
    private readonly List<Button> idleButtons=[];
    private readonly List<(Control Control,string Key,string Fallback)> labels=[];
    private CancellationTokenSource? operation;
    private FcCardSnapshot? snapshot;
    private bool refreshing;
    private bool imageReplacement;
    private IReadOnlyList<FcGame>? imageSnapshot;
    private readonly bool integrated;
    private readonly ToolTip summaryToolTip = new();
    public bool IsBusy=>operation is not null;
    public bool CanBurn => burn.Enabled;
    public bool CanRebuildConfiguration => backendFactory is not null&&!IsBusy&&snapshot is {IsFc:true,Writable:true};
    public bool HasGames => games.Count > 0;
    public bool HasSelection => grid.CurrentRow?.DataBoundItem is FcGame;
    public bool CanExportSelection => grid.CurrentRow?.DataBoundItem is FcGame { RomBytes: > 0 };
    public bool CanEditConfiguration => grid.CurrentRow?.DataBoundItem is FcGame { IsNes: true };
    public int GameCount => games.Count;
    public FcCardSnapshot? Snapshot => snapshot;
    public string StatusText => status.Text;
    public event Action? StateChanged;
    public event Action<string>? StatusChanged;
    public event Action<FcProgress>? ProgressChanged;
    // Imported files are not a card snapshot. They may initiate a read-only
    // preflight; edits to previously scanned entries must first be rescanned.
    private bool CanScanBeforeBurn=>games.Any(g=>g.Enabled)&&games.All(g=>g.OriginalOffset<0);
    public event Action? ExitRequested;
    public event Action<bool>? BusyChanged;
    public void Cancel()=>operation?.Cancel();
    private string L(string key,string fallback)=>text(key,fallback);
    public FcWorkspaceControl(Func<string,string,string> localize,Func<IFcCardBackend>? backend=null,bool integrated=false)
    {
        text=localize;backendFactory=backend;this.integrated=integrated;Dock=DockStyle.Fill;Padding=integrated?Padding.Empty:new Padding(12);AllowDrop=true;
        var layout=new TableLayoutPanel(){Dock=DockStyle.Fill,ColumnCount=1,RowCount=6};
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,20));layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var title=new Label(){Text=L("mode","MIX | NES + MD + SNES (MBCX)"),Font=new Font(Font.FontFamily,13,FontStyle.Bold),AutoSize=true,Padding=new Padding(2,2,2,10)};
        labels.Add((title,"mode","MIX | NES + MD + SNES (MBCX)"));
        layout.Controls.Add(title,0,0);layout.Controls.Add(actions,0,1);layout.Controls.Add(grid,0,2);layout.Controls.Add(summary,0,3);layout.Controls.Add(progress,0,4);layout.Controls.Add(status,0,5);Controls.Add(layout);
        AddButton("back","Other modes",(_,_)=>ExitRequested?.Invoke());
        AddButton("add","Add NES / MD / SNES / ZIP",async(_,_)=>{using var d=new OpenFileDialog(){Multiselect=true,Filter="NES / MD / SNES / ZIP (*.nes;*.md;*.gen;*.smd;*.bin;*.sfc;*.smc;*.zip)|*.nes;*.md;*.gen;*.smd;*.bin;*.sfc;*.smc;*.zip"};if(d.ShowDialog(this)==DialogResult.OK)await ImportAsync(d.FileNames);});
        AddButton("remove","Remove",(_,_)=>RemoveSelected());
        AddButton("clear","Clear",(_,_)=>ClearGames());
        AddButton("up","Up",(_,_)=>MoveRow(-1));AddButton("down","Down",(_,_)=>MoveRow(1));
        AddButton("title_preview","12px title preview",(_,_)=>PreviewTitle());
        if(backendFactory is not null)AddButton("rebuild_menu","重建菜单",async(_,_)=>await RebuildCardConfigurationAsync());
        AddButton("rebuild_config","检查 / 重建 NES 配置",(_,_)=>RebuildConfigurations());
        AddButton("edit_config","修正 NES 配置",(_,_)=>EditConfiguration());
        AddButton("import_image","Open MIX .bin",async(_,_)=>await ImportImageAsync());
        AddButton("export","Build .bin",async(_,_)=>await ExportAsync());
        scan.Text=L("scan","Scan MIX card");scan.Visible=backend is not null;scan.Click+=async(_,_)=>await ScanAsync();actions.Controls.Add(scan);
        burn.Text=L("burn","Burn MIX card");burn.Visible=backend is not null;burn.Click+=async(_,_)=>await BurnAsync();actions.Controls.Add(burn);
        labels.Add((scan,"scan","Scan MIX card"));labels.Add((burn,"burn","Burn MIX card"));labels.Add((cancel,"cancel","Cancel"));
        cancel.Text=L("cancel","Cancel");cancel.Click+=(_,_)=>Cancel();actions.Controls.Add(cancel);
        grid.Columns.Add(new DataGridViewCheckBoxColumn(){DataPropertyName=nameof(FcGame.Enabled),HeaderText=L("enabled","Use"),Tag=("enabled","Use"),Width=45});
        Column(nameof(FcGame.Platform),"platform","Core",55);Column(nameof(FcGame.Name),"name","Name",220,false);Column(nameof(FcGame.MapperDisplay),"mapper","Mapper",65);
        Column(nameof(FcGame.HeaderMapperDisplay),"header_mapper","原始 Mapper",80);
        Column(nameof(FcGame.Prg),"prg","PRG",85);Column(nameof(FcGame.Chr),"chr","CHR",85);
        Column(nameof(FcGame.Ram),"ram","RAM / allocation",130);Column(nameof(FcGame.Battery),"battery","Battery",65);
        Column(nameof(FcGame.SaveMode),"save_mode","Save area",85);Column(nameof(FcGame.SaveBankList),"save_banks","SRAM banks",90);Column(nameof(FcGame.Metadata),"source","Metadata source",180);
        Column(nameof(FcGame.Compatibility),"compatibility","Import check",320);
        Column(nameof(FcGame.Snapshot),"snapshot","Snapshot",130);
        var savesMenu=new ContextMenuStrip();
        var exportRom=new ToolStripMenuItem(L("export_rom","Export selected ROM")){Name="exportRom"};
        exportRom.Click+=async(_,_)=>await ExportSelectedRomAsync();
        var exportSaves=new ToolStripMenuItem("导出 MIX 存档");var importSaves=new ToolStripMenuItem("导入 MIX 存档");
        exportSaves.Click+=async(_,_)=>await ExportSavesAsync();importSaves.Click+=async(_,_)=>await ImportSavesAsync();
        savesMenu.Items.AddRange([exportRom,new ToolStripSeparator(),exportSaves,importSaves]);
        savesMenu.Opening+=(_,_)=>exportRom.Enabled=!IsBusy&&CanExportSelection;
        savesMenu.Opening+=(_,_)=>{exportSaves.Enabled=!IsBusy&&(backendFactory is null?imageSnapshot is not null:snapshot is {IsFc:true});importSaves.Enabled=!IsBusy&&(backendFactory is null?games.Count>0:snapshot is {IsFc:true,Writable:true});};
        grid.ContextMenuStrip=savesMenu;
        grid.DataSource=games;
        grid.SelectionChanged+=(_,_)=>StateChanged?.Invoke();
        status.TextChanged+=(_,_)=>StatusChanged?.Invoke(status.Text);
        grid.CurrentCellDirtyStateChanged+=(_,_)=>{if(grid.IsCurrentCellDirty)grid.CommitEdit(DataGridViewDataErrorContexts.Commit);};
        grid.CellValueChanged+=(_,_)=>RefreshSummary();grid.DataError+=(_,e)=>{e.ThrowException=false;};
        grid.CellFormatting+=(_,e)=>{
            if(e.RowIndex>=0&&grid.Rows[e.RowIndex].DataBoundItem is FcGame {Snes:not null})e.CellStyle!.ForeColor=Color.DimGray;
            if(e.Value is string message)e.Value=NesConfiguration.Localize(message,text);
            if(e.Value is string value){string? key=value switch{"Yes"=>"yes","No"=>"no","None"=>"none","Shared"=>"shared","Dedicated"=>"dedicated","OK"=>"ok","Mesen DB (validated)"=>"metadata_db","Mesen DB + mapper fallback"=>"metadata_db_fallback","iNES / mapper fallback"=>"metadata_fallback","Known content correction"=>"metadata_known","Manual configuration"=>"metadata_manual","Card configuration"=>"metadata_card","Board corrected; gameplay unverified"=>"board_unverified","Legacy MMC3 over 512 KiB: check mapper"=>"legacy_large_mmc3","Legacy header: board not independently confirmed"=>"legacy_unconfirmed","Known correction available: rebuild configuration"=>"correction_available","Card override retained; review configuration manually"=>"override_retained",_=>null};if(key is not null)e.Value=L(key,value);}
        };
        grid.CellToolTipTextNeeded+=(_,args)=>{
            if(args.RowIndex>=0&&grid.Rows[args.RowIndex].DataBoundItem is FcGame {Snes:{ } snes} game)
                args.ToolTipText=$"SNES · {snes.MappingDisplay} · {snes.ChipDisplay} · {snes.RegionDisplay}\n{game.Compatibility}";
        };
        DragEnter+=(_,e)=>e.Effect=!IsBusy&&e.Data?.GetDataPresent(DataFormats.FileDrop)==true?DragDropEffects.Copy:DragDropEffects.None;
        DragDrop+=async(_,e)=>{if(!IsBusy&&e.Data?.GetData(DataFormats.FileDrop) is string[] paths)await ImportAsync(paths);};
        grid.AllowDrop=true;grid.DragEnter+=(_,e)=>e.Effect=!IsBusy&&e.Data?.GetDataPresent(DataFormats.FileDrop)==true?DragDropEffects.Copy:DragDropEffects.None;
        grid.DragDrop+=async(_,e)=>{if(!IsBusy&&e.Data?.GetData(DataFormats.FileDrop) is string[] paths)await ImportAsync(paths);};
        if(integrated)
        {
            layout.Controls.Clear();
            layout.RowStyles.Clear();
            layout.RowCount=2;
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,28));
            summary.AutoSize=false;
            summary.AutoEllipsis=true;
            layout.Controls.Add(grid,0,0);
            layout.Controls.Add(summary,0,1);
            grid.BackgroundColor=Color.White;
            grid.BorderStyle=BorderStyle.None;
            grid.AllowUserToResizeRows=false;
            grid.RowTemplate.Height=26;
            title.Dispose();
            actions.Visible=false;
        }
        status.Text=L("notice","ROM banks: 16 KiB. SRAM banks: 8 KiB; battery-backed areas are dedicated and non-battery areas are shared.");RefreshSummary();
    }
    public void InvalidateSnapshot()
    {
        if(IsBusy)return;
        snapshot=null;
        RefreshSummary();
    }
    public void RemoveSelected()
    {
        if(IsBusy)return;
        foreach(var game in grid.SelectedRows.Cast<DataGridViewRow>().Select(row=>row.DataBoundItem).OfType<FcGame>().ToList())games.Remove(game);
        RefreshSummary();
    }
    public void ClearGames()
    {
        if(IsBusy)return;
        if(games.Count>0&&MessageBox.Show(this,L("clear_confirm","Clear this workspace list? The card is not written until Burn."),L("mode","MIX | NES + MD + SNES (MBCX)"),MessageBoxButtons.YesNo)!=DialogResult.Yes)return;
        games.Clear();imageReplacement=false;RefreshSummary();
    }
    public async Task ExportSelectedRomAsync()
    {
        if(IsBusy||grid.CurrentRow?.DataBoundItem is not FcGame game||game.RomBytes==0)return;
        string name=string.Concat(game.Name.Select(character=>Path.GetInvalidFileNameChars().Contains(character)?'_':character));
        string extension=game.Snes is not null?"sfc":game.Md is null?"nes":"md";
        using var dialog=new SaveFileDialog{FileName=name+"."+extension,Filter=$"ROM (*.{extension})|*.{extension}",DefaultExt=extension,AddExtension=true,OverwritePrompt=true};
        if(dialog.ShowDialog(this)!=DialogResult.OK)return;
        await RunAsync(async(ct,reporter)=>{await EnsureRomDataAsync(new[]{game.Id},ct,reporter);byte[] data=games.Single(item=>item.Id==game.Id).Data.ToArray();await File.WriteAllBytesAsync(dialog.FileName,data,ct);status.Text=L("rom_exported","ROM exported.");});
    }
    protected override void Dispose(bool disposing)
    {
        if(disposing)
        {
            operation?.Cancel();
            summaryToolTip.Dispose();
            if(integrated){actions.Dispose();status.Dispose();progress.Dispose();}
        }
        base.Dispose(disposing);
    }
    private void Column(string property,string key,string fallback,int width,bool readOnly=true)=>grid.Columns.Add(new DataGridViewTextBoxColumn(){DataPropertyName=property,HeaderText=L(key,fallback),Tag=(key,fallback),Width=width,ReadOnly=readOnly});
    private void AddButton(string key,string fallback,EventHandler handler){var b=new Button(){Text=L(key,fallback),AutoSize=true};b.Click+=handler;labels.Add((b,key,fallback));idleButtons.Add(b);actions.Controls.Add(b);}
    public void RefreshLanguage()
    {
        foreach(var label in labels)label.Control.Text=L(label.Key,label.Fallback);
        foreach(DataGridViewColumn column in grid.Columns)if(column.Tag is ValueTuple<string,string> tag)column.HeaderText=L(tag.Item1,tag.Item2);
        grid.Invalidate();RefreshSummary();
    }
    public void PreviewTitle()
    {
        grid.EndEdit();
        if(grid.CurrentRow?.DataBoundItem is not FcGame game)return;
        try
        {
            var title=FcTitleRenderer.Render(game.Name);
            using var list=FcTitleRenderer.Preview(title.List,208,12);
            using var detail=FcTitleRenderer.Preview(title.Detail,224,28);
            using var dialog=new Form(){Text=L("title_preview","12px title preview"),StartPosition=FormStartPosition.CenterParent,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,MaximizeBox=false,MinimizeBox=false};
            var panel=new FlowLayoutPanel(){FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoSize=true,Padding=new Padding(12)};
            panel.Controls.Add(new Label(){Text=game.Name,AutoSize=true,MaximumSize=new Size(896,0)});
            panel.Controls.Add(new Label(){Text=L("title_list_preview","List (208 x 12 pixels, 4x zoom)"),AutoSize=true});
            panel.Controls.Add(new PictureBox(){Image=list,SizeMode=PictureBoxSizeMode.AutoSize});
            panel.Controls.Add(new Label(){Text=L("title_detail_preview","Details (two 12px lines, 4x zoom)"),AutoSize=true});
            panel.Controls.Add(new PictureBox(){Image=detail,SizeMode=PictureBoxSizeMode.AutoSize});
            panel.Controls.Add(new Label(){Text=title.MissingCharacters.Length==0?L("title_exact_preview","These exact 12px bitmaps are written to the card."):L("title_missing","Missing glyphs (shown as squares):")+" "+title.MissingCharacters,AutoSize=true});
            dialog.Controls.Add(panel);dialog.ShowDialog(this);
        }
        catch(Exception ex){MessageBox.Show(this,ex.Message,L("title_preview","12px title preview"),MessageBoxButtons.OK,MessageBoxIcon.Error);}
    }
    public void MoveRow(int delta)
    {
        if(grid.CurrentRow?.DataBoundItem is not FcGame item)return;int from=games.IndexOf(item),to=from+delta;if(to<0||to>=games.Count)return;
        games.RemoveAt(from);games.Insert(to,item);grid.ClearSelection();grid.Rows[to].Selected=true;grid.CurrentCell=grid.Rows[to].Cells[1];RefreshSummary();
    }
    public void EditConfiguration()
    {
        if(IsBusy||grid.CurrentRow?.DataBoundItem is not FcGame game||!game.IsNes)return;
        _=RunAsync(async(ct,reporter)=>{await EnsureRomDataAsync(new[]{game.Id},ct,reporter);EditLoadedConfiguration();});
    }
    private void EditLoadedConfiguration()
    {
        grid.EndEdit();if(grid.CurrentRow?.DataBoundItem is not FcGame game||!game.IsNes)return;
        try{
            using var dialog=new FcConfigurationDialog(game,text);
            if(dialog.ShowDialog(this)!=DialogResult.OK||dialog.Result is null)return;
            var staged=games.Select(g=>g.Copy()).ToList();staged.Single(g=>g.Id==game.Id).Info=dialog.Result;
            FcMbcxCodec.Plan(staged,snapshot?.Capacity??FcMbcxCodec.CapacityBytes,snapshot?.IsFc==true);
            game.Info=dialog.Result;games.ResetBindings();RefreshSummary();status.Text=L("config_staged","配置已更新到列表；点击“烧录 MIX 卡”写入。ROM 内容不变。");
        }catch(Exception ex){MessageBox.Show(this,ex.Message,L("edit_config","修正 NES 配置"),MessageBoxButtons.OK,MessageBoxIcon.Warning);}
    }
    public Task RebuildCardConfigurationAsync()=>RunAsync(async(ct,progress)=>{
        var expected=snapshot??throw new InvalidOperationException(L("rescan_required","Scan MIX card before rebuilding its configuration."));
        if(!expected.IsFc||!expected.Writable)throw new InvalidOperationException(L("rebuild_menu_invalid","A valid writable MIX card is required."));
        var backend=backendFactory?.Invoke()??throw new InvalidOperationException("No card programmer is available.");
        if(MessageBox.Show(this,L("rebuild_menu_confirm","Rebuild the directory, launch configuration and title data from games already on this card? Pending list edits are not applied. ROM content, manual settings and saves are preserved."),L("rebuild_menu","重建菜单"),MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
        snapshot=null;
        var result=await Task.Run(()=>backend.RebuildConfiguration(expected,ct,progress),ct);
        for(int index=0;index<games.Count;index++){
            var staged=games[index];var original=expected.Games.FirstOrDefault(game=>game.Id==staged.Id);var rebuilt=result.Games.FirstOrDefault(game=>game.Id==staged.Id);
            if(original is null||rebuilt is null)continue;
            games[index]=new FcGame{Id=staged.Id,Name=staged.Name,Enabled=staged.Enabled,Info=staged.Info==original.Info?rebuilt.Info:staged.Info,Md=staged.Md,Snes=staged.Snes,Data=rebuilt.Data,OriginalRomBytes=rebuilt.RomBytes,OriginalOffset=rebuilt.OriginalOffset,OriginalAllocated=rebuilt.OriginalAllocated,OriginalConfig=rebuilt.OriginalConfig,DirectoryIdentity=rebuilt.DirectoryIdentity,OriginalSaveMap=rebuilt.OriginalSaveMap,OriginalBattery=rebuilt.OriginalBattery,OriginalStoredRamBytes=rebuilt.OriginalStoredRamBytes};
        }
        snapshot=result;games.ResetBindings();RefreshSummary();status.Text=L("rebuild_menu_done","MIX configuration rebuilt; ROM data and saves unchanged.");
    });
    public void RebuildConfigurations()
    {
        if(IsBusy)return;
        _=RunAsync(async(ct,reporter)=>{await EnsureRomDataAsync(games.Where(game=>game.Enabled&&game.IsNes).Select(game=>game.Id).ToArray(),ct,reporter);RebuildLoadedConfigurations();});
    }
    private void RebuildLoadedConfigurations()
    {
        grid.EndEdit();var staged=games.Select(g=>g.Copy()).ToList();var lines=new List<string>();
        try{
            foreach(var g in staged.Where(g=>g.Enabled&&g.IsNes)){
                var old=g.Info;var next=NesConfiguration.Reidentify(g);
                if(!next.CanPack){lines.Add(g.Name+": "+next.Error);continue;}
                NesConfiguration.Validate(g,next);g.Info=next;
                lines.Add(g.Name+"\r\n  "+NesConfiguration.Describe(old)+"\r\n  → "+NesConfiguration.Describe(next)+"\r\n  "+NesConfiguration.Localize(next.MetadataSource,text)+(next.Warning is null?"":"; "+NesConfiguration.Localize(next.Warning,text)));
            }
            if(lines.Count==0)return;
            FcMbcxCodec.Plan(staged,snapshot?.Capacity??FcMbcxCodec.CapacityBytes,snapshot?.IsFc==true);
            using var dialog=new Form(){Text=L("rebuild_config","检查 / 重建 NES 配置"),StartPosition=FormStartPosition.CenterParent,Size=new Size(800,540),MinimizeBox=false};
            var preview=new TextBox(){ReadOnly=true,Multiline=true,ScrollBars=ScrollBars.Both,Dock=DockStyle.Fill,Text=L("rebuild_explain","检查所有勾选的 NES 游戏；保留手动修正。未确定的板型不会猜测。下面只更新列表，尚不写卡。")+"\r\n\r\n"+string.Join("\r\n\r\n",lines)};
            var apply=new Button(){Text=L("apply_config","应用到列表"),AutoSize=true,DialogResult=DialogResult.OK};
            var cancel=new Button(){Text=L("cancel","取消"),AutoSize=true,DialogResult=DialogResult.Cancel};
            var buttons=new FlowLayoutPanel(){Dock=DockStyle.Bottom,AutoSize=true};buttons.Controls.AddRange([apply,cancel]);dialog.Controls.Add(preview);dialog.Controls.Add(buttons);dialog.AcceptButton=apply;dialog.CancelButton=cancel;
            dialog.Shown+=(_,_)=>{preview.Select(0,0);apply.Focus();};
            if(dialog.ShowDialog(this)!=DialogResult.OK)return;
            foreach(var g in games){var next=staged.Single(s=>s.Id==g.Id);g.Info=next.Info;}
            games.ResetBindings();RefreshSummary();status.Text=L("config_staged","配置已更新到列表；点击“烧录 MIX 卡”写入。ROM 内容不变。");
        }catch(Exception ex){MessageBox.Show(this,ex.Message,L("rebuild_config","检查 / 重建 NES 配置"),MessageBoxButtons.OK,MessageBoxIcon.Warning);}
    }
    private void RefreshSummary()
    {
        if(refreshing)return;refreshing=true;
        try
        {
            foreach(var g in games)g.SaveBankList="—";
            var valid=games.Where(g=>g.Enabled&&g.Info.CanPack).ToList();
            var plan=FcMbcxCodec.Plan(valid,snapshot?.Capacity??FcMbcxCodec.CapacityBytes,snapshot?.IsFc==true);
            foreach(var entry in plan.Entries)entry.Game.SaveBankList=entry.SaveMap.Length==0?L("none","None"):string.Join(",",entry.SaveMap);
            grid.Invalidate();
            summary.Text=string.Format(L("summary","{0} ROMs (metadata/card capacity) | occupied end {1:F2}/{2:F0} MiB | dedicated SRAM {3}/60 x 8 KiB | shared {4}/4 x 8 KiB"),
                plan.Entries.Count,plan.EndBytes/1048576d,(snapshot?.Capacity??FcMbcxCodec.CapacityBytes)/1048576d,plan.DedicatedBanks,plan.SharedBanks);
            summary.Text+=Environment.NewLine+L("core_support_notice","MIX v2: NES / MD / SNES integrated with shared ROM/SRAM pools. No core mapper whitelist gates packing, export or programming.");
            summary.Text+=Environment.NewLine+string.Format(L("correction_counts","自动识别/纠错 {0}｜手动修正 {1}｜待核对 {2}；扫描原卡后可检查 / 重建配置。"),
                games.Count(g=>g.IsNes&&g.Info.CorrectionKind is 1 or 2),games.Count(g=>g.IsNes&&g.Info.CorrectionKind==3),games.Count(g=>g.IsNes&&g.Info.Warning is not null&&g.Info.CorrectionKind==0));
            int unselected=games.Count(g=>!g.Enabled),unsupported=games.Count(g=>!g.Info.CanPack);
            summary.Text+=Environment.NewLine+string.Format(L("selection_counts","Imported {0} | checked {1} | unchecked {2} | invalid data {3}; unchecked games are not written."),games.Count,games.Count(g=>g.Enabled),unselected,unsupported);
            if(unsupported>0)summary.Text+=Environment.NewLine+L("excluded_names","Invalid format/data:")+" "+string.Join("; ",games.Where(g=>!g.Info.CanPack).Take(3).Select(g=>g.Name+": "+g.Info.Error))+(unsupported>3?" …":"");
            int invalid=games.Count(g=>g.Enabled&&!g.Info.CanPack);if(invalid>0)summary.Text+=$" | {invalid} "+L("unsupported","invalid format/data (uncheck or remove)");
            if(backendFactory is not null)
            {
                if(snapshot is null)summary.Text+=Environment.NewLine+(CanScanBeforeBurn
                    ?L("burn_scan_first","Click Burn MIX card: the card will be read before write confirmation.")
                    :L("scan_required","Scan MIX card, or import NES / MD / SNES ROMs to start a new burn."));
                else if(!snapshot.Writable)summary.Text+=Environment.NewLine+L("write_locked","Burn disabled: NOR capacity is unavailable or the card is write-protected. Check the scan result and scan again.");
            }
            burn.Enabled=!IsBusy&&backendFactory is not null&&invalid==0&&
                (snapshot is {Writable:true} || snapshot is null&&CanScanBeforeBurn);
        }
        catch(Exception ex){summary.Text=ex.Message;burn.Enabled=false;}
        finally
        {
            if(integrated)
            {
                summaryToolTip.SetToolTip(summary,summary.Text);
                summary.Text=summary.Text.Split(Environment.NewLine)[0];
            }
            refreshing=false;scan.Enabled=!IsBusy;StateChanged?.Invoke();
        }
    }
    private async Task EnsureRomDataAsync(IEnumerable<Guid> identities,CancellationToken ct,IProgress<FcProgress> reporter)
    {
        var selected=identities.ToHashSet();var pending=games.Where(game=>selected.Contains(game.Id)&&!game.HasRomData).Select(game=>game.Copy()).ToArray();
        if(pending.Length==0)return;
        var expected=snapshot??throw new InvalidOperationException("Scan the source card before reading ROM content.");
        var backend=backendFactory?.Invoke()??throw new InvalidOperationException("No programmer is available to load card ROMs.");
        var loaded=await Task.Run(()=>backend.LoadGames(expected,pending,ct,reporter),ct);ct.ThrowIfCancellationRequested();
        foreach(var game in loaded){int index=games.ToList().FindIndex(item=>item.Id==game.Id);if(index>=0)games[index]=game;}
        snapshot=expected with {Games=expected.Games.Select(original=>{var match=loaded.FirstOrDefault(game=>game.Id==original.Id);return match is null?original:original.WithData(match.Data,match.DirectoryIdentity,match.Info);}).ToArray()};
        games.ResetBindings();
    }
    private async Task RunAsync(Func<CancellationToken,IProgress<FcProgress>,Task> work)
    {
        if(IsBusy)return;grid.EndEdit();operation=new();var activeOperation=operation;var token=operation.Token;
        foreach(var b in idleButtons)b.Enabled=false;grid.Enabled=false;scan.Enabled=false;burn.Enabled=false;cancel.Enabled=true;BusyChanged?.Invoke(true);
        var reporter=new Progress<FcProgress>(p=>{if(ReferenceEquals(operation,activeOperation)&&!token.IsCancellationRequested&&!IsDisposed){status.Text=p.Message;progress.Value=Math.Clamp(p.Percent,0,100);ProgressChanged?.Invoke(p);}});
        try{await work(token,reporter);}
        catch(OperationCanceledException){status.Text=L("cancelled","Cancelled; rescan the card after an interrupted write.");}
        catch(Exception ex){status.Text=ex.Message;MessageBox.Show(this,ex.Message,L("mode","MIX | NES + MD + SNES (MBCX)"),MessageBoxButtons.OK,MessageBoxIcon.Warning);}
        finally{operation.Dispose();operation=null;foreach(var b in idleButtons)b.Enabled=true;grid.Enabled=true;cancel.Enabled=false;BusyChanged?.Invoke(false);RefreshSummary();}
    }
    public Task ImportAsync(IEnumerable<string> paths)=>RunAsync(async(ct,p)=>{
        var inputs=paths.ToArray();
        var list=await Task.Run(()=>{
            List<FcGame> imported=[];
            foreach(string path in Services.RomArchiveImportService.ExpandPaths(inputs))
            foreach(var source in Services.RomArchiveImportService.Preview(path))
            {
                ct.ThrowIfCancellationRequested();
                if(source.Platform==Services.RomPlatform.Snes){
                    try{imported.Add(Snes.SnesRomImportService.CreateGame(Snes.SnesRomImportService.ReadRom(source,ct),source.Title));}
                    catch(Exception exception)when(exception is IOException or InvalidDataException){
                        var info=Snes.SnesRomHeaderService.Parse([]) with{Error=exception.Message};
                        imported.Add(new FcGame{Name=source.Title,Enabled=false,Snes=info,
                            Info=new NesRomInfo(-1,0,0,0,0,0,0,0,false,false,0,"SNES header",exception.Message)});
                    }
                }else if(source.Platform==Services.RomPlatform.Md){
                    var md=source.MdRom!;
                    if(md.CanRead)imported.Add(Md.MdMbcxCodec.CreateGame(Md.MdRomImportService.ReadRom(md),source.Title));
                    else imported.Add(new FcGame{Name=source.Title,Data=[],Enabled=false,Md=new(0,0),
                        Info=new NesRomInfo(-1,0,0,0,0,0,0,0,false,false,0,"MD header",md.Assessment.Message)});
                }else if(source.Platform==Services.RomPlatform.Fc){
                    if(source.Length>FcMbcxCodec.CapacityBytes)throw new InvalidDataException("NES file exceeds card capacity.");
                    byte[] data;
                    if(source.EntryIndex is int index){using var zip=Md.MdZipFile.OpenRead(source.SourcePath);using var input=zip.Entries[index].Open();data=new byte[checked((int)source.Length)];input.ReadExactly(data);}
                    else data=File.ReadAllBytes(source.SourcePath);
                    var info=NesRomHeaderService.Parse(data);imported.Add(new FcGame{Name=source.Title,Data=data,Info=info,Enabled=info.CanPack});
                }else imported.Add(new FcGame{Name=source.Title,Data=[],Enabled=false,
                    Info=new NesRomInfo(-1,0,0,0,0,0,0,0,false,false,0,source.Platform.ToString(),"MIX supports NES / MD / SNES")});
            }
            return imported;
        },ct);
        ct.ThrowIfCancellationRequested();foreach(var game in list)games.Add(game);
        status.Text=string.Format(L("import_counts","Import complete: {0} checked, {1} unchecked (including {2} invalid data). Unchecked games are not included."),games.Count(g=>g.Enabled),games.Count(g=>!g.Enabled),games.Count(g=>!g.Info.CanPack));
    });
    public async Task ImportImageAsync()
    {
        using var d=new OpenFileDialog(){Filter="MIX MBCX image (*.bin)|*.bin"};
        if(d.ShowDialog(this)!=DialogResult.OK)return;
        if(games.Count>0&&MessageBox.Show(this,L("image_confirm","Replace this workspace list with the MIX image? The card is not written yet."),L("mode","MIX | NES + MD + SNES (MBCX)"),MessageBoxButtons.YesNo)!=DialogResult.Yes)return;
        await RunAsync(async(ct,p)=>{
            var loaded=await Task.Run(()=>{
                using var file=File.OpenRead(d.FileName);
                if(file.Length<FcMbcxCodec.PrefixBytes||file.Length>FcMbcxCodec.CapacityBytes)throw new InvalidDataException("Invalid MIX image length.");
                byte[] Read(int address,int length){ct.ThrowIfCancellationRequested();if(address<0||length<0||address>file.Length-length)throw new InvalidDataException("MIX image range exceeds file length.");var bytes=new byte[length];file.Position=address;file.ReadExactly(bytes);return bytes;}
                return FcMbcxCodec.Read(Read(0,FcMbcxCodec.PrefixBytes),(int)file.Length,Read,ct);
            },ct);
            ct.ThrowIfCancellationRequested();imageSnapshot=loaded.Select(game=>game.Copy()).ToArray();games.Clear();imageReplacement=true;
            // Offline image locations are not a snapshot of the connected card.
            foreach(var g in loaded)games.Add(new FcGame{Name=g.Name,Data=g.Data,Info=g.Info,Md=g.Md,Snes=g.Snes,Enabled=g.Info.CanPack});
            status.Text=L("image_loaded","MIX image loaded; choose Burn to write it to the scanned card.");
        });
    }
    public async Task ExportSavesAsync()
    {
        using var destination=new SaveFileDialog(){FileName="MIX-saves.json",Filter="MIX save archive (*.json)|*.json",DefaultExt="json",AddExtension=true};
        if(destination.ShowDialog(this)!=DialogResult.OK)return;
        string? sramPath=null;
        if(backendFactory is null){using var raw=new OpenFileDialog(){Filter="512 KiB SRAM (*.sav)|*.sav"};if(raw.ShowDialog(this)!=DialogResult.OK)return;sramPath=raw.FileName;}
        await RunAsync(async(ct,reporter)=>{
            IReadOnlyList<GameLibraryDirectory.SaveImage> saves;
            if(backendFactory is null){if(imageSnapshot is null)throw new InvalidOperationException("Open the original MIX card image first.");saves=GameLibraryDirectory.ExportSaves(imageSnapshot,await File.ReadAllBytesAsync(sramPath!,ct));}
            else {var expected=snapshot??throw new InvalidOperationException("Scan the card first.");var backend=backendFactory();saves=await Task.Run(()=>backend.ExportSaves(expected,ct,reporter),ct);}
            await File.WriteAllTextAsync(destination.FileName,System.Text.Json.JsonSerializer.Serialize(saves),ct);status.Text="MIX 存档已导出。";
        });
    }
    public async Task ImportSavesAsync()
    {
        using var archive=new OpenFileDialog(){Filter="MIX save archive (*.json)|*.json"};if(archive.ShowDialog(this)!=DialogResult.OK)return;
        string? targetPath=null;
        if(backendFactory is null){using var destination=new SaveFileDialog(){FileName="GLB2-SRAM.sav",Filter="512 KiB SRAM (*.sav)|*.sav",DefaultExt="sav",AddExtension=true};if(destination.ShowDialog(this)!=DialogResult.OK)return;targetPath=destination.FileName;}
        else if(MessageBox.Show(this,"按稳定游戏 ID 恢复当前卡上的存档？只写 SRAM，不写 ROM。中断后可重新导入。","MIX 存档",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes)return;
        await RunAsync(async(ct,reporter)=>{
            var saves=System.Text.Json.JsonSerializer.Deserialize<List<GameLibraryDirectory.SaveImage>>(await File.ReadAllTextAsync(archive.FileName,ct))??throw new InvalidDataException("Invalid MIX save archive.");
            if(backendFactory is null){var plan=FcMbcxCodec.Plan(games.Select(game=>game.Copy()).ToArray(),FcMbcxCodec.CapacityBytes,false);var target=GameLibraryDirectory.ImportSaves(plan.Entries,saves,Enumerable.Repeat((byte)255,524288).ToArray());await File.WriteAllBytesAsync(targetPath!,target,ct);status.Text="已生成配套 SRAM；未归档区域填充 FF。请同时使用同一列表构建的 GLB2 镜像。";}
            else {var expected=snapshot??throw new InvalidOperationException("Scan the target card first.");var backend=backendFactory();await Task.Run(()=>backend.ImportSaves(expected,saves,ct,reporter),ct);status.Text="MIX 存档已恢复并回读验证。";}
        });
    }
    public async Task ExportAsync()
    {
        using var d=new SaveFileDialog(){FileName="MIX_MBCX.bin",Filter="MIX card image (*.bin)|*.bin",DefaultExt="bin",AddExtension=true,OverwritePrompt=true};
        if(d.ShowDialog(this)!=DialogResult.OK)return;
        await RunAsync(async(ct,p)=>{await EnsureRomDataAsync(games.Where(game=>game.Enabled).Select(game=>game.Id).ToArray(),ct,p);var copy=games.Select(g=>g.Copy()).ToList();await Task.Run(()=>FcMbcxCodec.Export(d.FileName,copy,ct,p),ct);status.Text=string.Format(L("export_counts","MIX image created with {0} games; {1} unchecked rows excluded."),copy.Count(g=>g.Enabled),copy.Count(g=>!g.Enabled));});
    }
    public Task ScanAsync()=>RunAsync(async(ct,p)=>{
        if(backendFactory is null)return;
        if(games.Count>0&&MessageBox.Show(this,L("scan_confirm","Rescan card entries and keep imported files? Unwritten edits to card entries will be replaced."),L("mode","MIX | NES + MD + SNES (MBCX)"),MessageBoxButtons.YesNo)!=DialogResult.Yes)return;
        await ReadCardAsync(ct,p);
    });
    private async Task ReadCardAsync(CancellationToken ct,IProgress<FcProgress> p)
    {
        var pending=games.Where(g=>imageReplacement||g.OriginalOffset<0).Select(g=>g.Copy()).ToList();
        // A failed/cancelled scan must not leave an old writable snapshot.
        snapshot=null;
        var backend=backendFactory!();var result=await Task.Run(()=>backend.Scan(ct,p),ct);
        ct.ThrowIfCancellationRequested();
        snapshot=result;games.Clear();if(!imageReplacement)foreach(var g in result.Games)games.Add(g.Copy());
        foreach(var g in pending)games.Add(g);
        status.Text=!result.Writable?L("write_locked","Burn disabled: NOR capacity is unavailable or the card is write-protected. Check the scan result and scan again."):
            result.Warning??(result.IsFc?L("scan_directory_only","MIX directory loaded; ROM content is read only when needed for rebuild/export."):L("convert_notice","This is not an MIX card. Add NES ROMs; burning requires explicit conversion confirmation."));
    }
    private async Task PrepareBurnAsync(CancellationToken ct,IProgress<FcProgress> p)
    {
        if(backendFactory is null)throw new InvalidOperationException("No card programmer is available.");
        if(snapshot is null)
        {
            if(!CanScanBeforeBurn)throw new InvalidOperationException(L("rescan_required","Scan MIX card again before retrying edits to card entries."));
            await ReadCardAsync(ct,p);
        }
        if(snapshot is not {Writable:true})throw new InvalidOperationException(L("write_locked","Burn disabled: NOR capacity is unavailable or the card is write-protected. Check the scan result and scan again."));
        // The scan may add existing games or discover a smaller NOR. Validate
        // the merged list against the actual card before asking to write it.
        FcMbcxCodec.Plan(games.Select(g=>g.Copy()).ToList(),snapshot.Capacity,snapshot.IsFc);
        RefreshSummary();
    }
    public Task BurnAsync()=>RunAsync(async(ct,p)=>{
        await PrepareBurnAsync(ct,p);
        bool convert=!snapshot!.IsFc;
        string prompt=convert?L("convert_confirm","Replace this card's existing GB/GBC/unknown directory with a dedicated MIX image? Existing games will no longer be listed."):
            L("burn_confirm","Apply this NES / MD / SNES MIX v2 list? Unchanged ROMs and existing saves are preserved; new RAM is initialized by bank.");
        if(MessageBox.Show(this,prompt,L("mode","MIX | NES + MD + SNES (MBCX)"),MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes)return;
        var copy=games.Select(g=>g.Copy()).ToList();var expected=snapshot;var backend=backendFactory!();
        // A failure invalidates the snapshot: stale metadata can never be reused.
        snapshot=null;
        var result=await Task.Run(()=>backend.Write(expected,copy,convert,ct,p),ct);
        snapshot=result;imageReplacement=false;games.Clear();foreach(var g in result.Games)games.Add(g.Copy());status.Text=string.Format(L("burn_counts","MIX card directory updated: {0} games. {1} unchecked rows were excluded; existing saves preserved; new RAM initialized by bank."),result.Games.Count,copy.Count(g=>!g.Enabled));
    });
}
