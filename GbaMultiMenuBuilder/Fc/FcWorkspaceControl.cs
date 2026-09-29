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
    public bool IsBusy=>operation is not null;
    // Imported files are not a card snapshot. They may initiate a read-only
    // preflight; edits to previously scanned entries must first be rescanned.
    private bool CanScanBeforeBurn=>games.Any(g=>g.Enabled)&&games.All(g=>g.OriginalOffset<0);
    public event Action? ExitRequested;
    public event Action<bool>? BusyChanged;
    public void Cancel()=>operation?.Cancel();
    private string L(string key,string fallback)=>text(key,fallback);
    public FcWorkspaceControl(Func<string,string,string> localize,Func<IFcCardBackend>? backend=null)
    {
        text=localize;backendFactory=backend;Dock=DockStyle.Fill;Padding=new Padding(12);AllowDrop=true;
        var layout=new TableLayoutPanel(){Dock=DockStyle.Fill,ColumnCount=1,RowCount=6};
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,20));layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var title=new Label(){Text=L("mode","MIX | FC + MD (MBCX)"),Font=new Font(Font.FontFamily,13,FontStyle.Bold),AutoSize=true,Padding=new Padding(2,2,2,10)};
        labels.Add((title,"mode","MIX | FC + MD (MBCX)"));
        layout.Controls.Add(title,0,0);layout.Controls.Add(actions,0,1);layout.Controls.Add(grid,0,2);layout.Controls.Add(summary,0,3);layout.Controls.Add(progress,0,4);layout.Controls.Add(status,0,5);Controls.Add(layout);
        AddButton("back","Other modes",(_,_)=>ExitRequested?.Invoke());
        AddButton("add","Add FC / MD / ZIP",async(_,_)=>{using var d=new OpenFileDialog(){Multiselect=true,Filter="FC / MD / ZIP (*.nes;*.md;*.gen;*.smd;*.bin;*.zip)|*.nes;*.md;*.gen;*.smd;*.bin;*.zip"};if(d.ShowDialog(this)==DialogResult.OK)await ImportAsync(d.FileNames);});
        AddButton("remove","Remove",(_,_)=>{foreach(var r in grid.SelectedRows.Cast<DataGridViewRow>().Select(r=>r.DataBoundItem).OfType<FcGame>().ToList())games.Remove(r);RefreshSummary();});
        AddButton("clear","Clear",(_,_)=>{if(games.Count==0||MessageBox.Show(this,L("clear_confirm","Clear this workspace list? The card is not written until Burn."),L("mode","MIX | FC + MD (MBCX)"),MessageBoxButtons.YesNo)==DialogResult.Yes){games.Clear();imageReplacement=false;RefreshSummary();}});
        AddButton("up","Up",(_,_)=>MoveRow(-1));AddButton("down","Down",(_,_)=>MoveRow(1));
        AddButton("title_preview","12px title preview",(_,_)=>PreviewTitle());
        AddButton("rebuild_config","检查 / 重建 FC 配置",(_,_)=>RebuildConfigurations());
        AddButton("edit_config","修正 FC 配置",(_,_)=>EditConfiguration());
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
        grid.DataSource=games;
        grid.CurrentCellDirtyStateChanged+=(_,_)=>{if(grid.IsCurrentCellDirty)grid.CommitEdit(DataGridViewDataErrorContexts.Commit);};
        grid.CellValueChanged+=(_,_)=>RefreshSummary();grid.DataError+=(_,e)=>{e.ThrowException=false;};
        grid.CellFormatting+=(_,e)=>{
            if(e.Value is string message)e.Value=NesConfiguration.Localize(message,text);
            if(e.Value is string value){string? key=value switch{"Yes"=>"yes","No"=>"no","None"=>"none","Shared"=>"shared","Dedicated"=>"dedicated","OK"=>"ok","Mesen DB (validated)"=>"metadata_db","Mesen DB + mapper fallback"=>"metadata_db_fallback","iNES / mapper fallback"=>"metadata_fallback","Known content correction"=>"metadata_known","Manual configuration"=>"metadata_manual","Card configuration"=>"metadata_card","Board corrected; gameplay unverified"=>"board_unverified","Legacy MMC3 over 512 KiB: check mapper"=>"legacy_large_mmc3","Legacy header: board not independently confirmed"=>"legacy_unconfirmed","Known correction available: rebuild configuration"=>"correction_available","Card override retained; review configuration manually"=>"override_retained",_=>null};if(key is not null)e.Value=L(key,value);}
        };
        DragEnter+=(_,e)=>e.Effect=!IsBusy&&e.Data?.GetDataPresent(DataFormats.FileDrop)==true?DragDropEffects.Copy:DragDropEffects.None;
        DragDrop+=async(_,e)=>{if(!IsBusy&&e.Data?.GetData(DataFormats.FileDrop) is string[] paths)await ImportAsync(paths);};
        grid.AllowDrop=true;grid.DragEnter+=(_,e)=>e.Effect=!IsBusy&&e.Data?.GetDataPresent(DataFormats.FileDrop)==true?DragDropEffects.Copy:DragDropEffects.None;
        grid.DragDrop+=async(_,e)=>{if(!IsBusy&&e.Data?.GetData(DataFormats.FileDrop) is string[] paths)await ImportAsync(paths);};
        status.Text=L("notice","ROM banks: 16 KiB. SRAM banks: 8 KiB; battery-backed areas are dedicated and non-battery areas are shared.");RefreshSummary();
    }
    private void Column(string property,string key,string fallback,int width,bool readOnly=true)=>grid.Columns.Add(new DataGridViewTextBoxColumn(){DataPropertyName=property,HeaderText=L(key,fallback),Tag=(key,fallback),Width=width,ReadOnly=readOnly});
    private void AddButton(string key,string fallback,EventHandler handler){var b=new Button(){Text=L(key,fallback),AutoSize=true};b.Click+=handler;labels.Add((b,key,fallback));idleButtons.Add(b);actions.Controls.Add(b);}
    public void RefreshLanguage()
    {
        foreach(var label in labels)label.Control.Text=L(label.Key,label.Fallback);
        foreach(DataGridViewColumn column in grid.Columns)if(column.Tag is ValueTuple<string,string> tag)column.HeaderText=L(tag.Item1,tag.Item2);
        grid.Invalidate();RefreshSummary();
    }
    private void PreviewTitle()
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
    private void MoveRow(int delta)
    {
        if(grid.CurrentRow?.DataBoundItem is not FcGame item)return;int from=games.IndexOf(item),to=from+delta;if(to<0||to>=games.Count)return;
        games.RemoveAt(from);games.Insert(to,item);grid.ClearSelection();grid.Rows[to].Selected=true;grid.CurrentCell=grid.Rows[to].Cells[1];RefreshSummary();
    }
    private void EditConfiguration()
    {
        grid.EndEdit();if(grid.CurrentRow?.DataBoundItem is not FcGame game||game.Md is not null)return;
        try{
            using var dialog=new FcConfigurationDialog(game,text);
            if(dialog.ShowDialog(this)!=DialogResult.OK||dialog.Result is null)return;
            var staged=games.Select(g=>g.Copy()).ToList();staged.Single(g=>g.Id==game.Id).Info=dialog.Result;
            FcMbcxCodec.Plan(staged,snapshot?.Capacity??FcMbcxCodec.CapacityBytes,snapshot?.IsFc==true);
            game.Info=dialog.Result;games.ResetBindings();RefreshSummary();status.Text=L("config_staged","配置已更新到列表；点击“烧录 MIX 卡”写入。ROM 内容不变。");
        }catch(Exception ex){MessageBox.Show(this,ex.Message,L("edit_config","修正 FC 配置"),MessageBoxButtons.OK,MessageBoxIcon.Warning);}
    }
    private void RebuildConfigurations()
    {
        grid.EndEdit();var staged=games.Select(g=>g.Copy()).ToList();var lines=new List<string>();
        try{
            foreach(var g in staged.Where(g=>g.Enabled&&g.Md is null)){
                var old=g.Info;var next=NesConfiguration.Reidentify(g);
                if(!next.Supported){lines.Add(g.Name+": "+next.Error);continue;}
                NesConfiguration.Validate(g,next);g.Info=next;
                lines.Add(g.Name+"\r\n  "+NesConfiguration.Describe(old)+"\r\n  → "+NesConfiguration.Describe(next)+"\r\n  "+NesConfiguration.Localize(next.MetadataSource,text)+(next.Warning is null?"":"; "+NesConfiguration.Localize(next.Warning,text)));
            }
            if(lines.Count==0)return;
            FcMbcxCodec.Plan(staged,snapshot?.Capacity??FcMbcxCodec.CapacityBytes,snapshot?.IsFc==true);
            using var dialog=new Form(){Text=L("rebuild_config","检查 / 重建 FC 配置"),StartPosition=FormStartPosition.CenterParent,Size=new Size(800,540),MinimizeBox=false};
            var preview=new TextBox(){ReadOnly=true,Multiline=true,ScrollBars=ScrollBars.Both,Dock=DockStyle.Fill,Text=L("rebuild_explain","检查所有勾选的 FC 游戏；保留手动修正。未确定的板型不会猜测。下面只更新列表，尚不写卡。")+"\r\n\r\n"+string.Join("\r\n\r\n",lines)};
            var apply=new Button(){Text=L("apply_config","应用到列表"),AutoSize=true,DialogResult=DialogResult.OK};
            var cancel=new Button(){Text=L("cancel","取消"),AutoSize=true,DialogResult=DialogResult.Cancel};
            var buttons=new FlowLayoutPanel(){Dock=DockStyle.Bottom,AutoSize=true};buttons.Controls.AddRange([apply,cancel]);dialog.Controls.Add(preview);dialog.Controls.Add(buttons);dialog.AcceptButton=apply;dialog.CancelButton=cancel;
            dialog.Shown+=(_,_)=>{preview.Select(0,0);apply.Focus();};
            if(dialog.ShowDialog(this)!=DialogResult.OK)return;
            foreach(var g in games){var next=staged.Single(s=>s.Id==g.Id);g.Info=next.Info;}
            games.ResetBindings();RefreshSummary();status.Text=L("config_staged","配置已更新到列表；点击“烧录 MIX 卡”写入。ROM 内容不变。");
        }catch(Exception ex){MessageBox.Show(this,ex.Message,L("rebuild_config","检查 / 重建 FC 配置"),MessageBoxButtons.OK,MessageBoxIcon.Warning);}
    }
    private void RefreshSummary()
    {
        if(refreshing)return;refreshing=true;
        try
        {
            foreach(var g in games)g.SaveBankList="—";
            var valid=games.Where(g=>g.Enabled&&g.Info.Supported).ToList();
            var plan=FcMbcxCodec.Plan(valid,snapshot?.Capacity??FcMbcxCodec.CapacityBytes,snapshot?.IsFc==true);
            foreach(var entry in plan.Entries)entry.Game.SaveBankList=entry.SaveMap.Length==0?L("none","None"):string.Join(",",entry.SaveMap);
            grid.Invalidate();
            summary.Text=string.Format(L("summary","{0}/198 ROMs (99 per core) | occupied end {1:F2}/{2:F0} MiB | dedicated SRAM {3}/60 x 8 KiB | shared {4}/4 x 8 KiB"),
                plan.Entries.Count,plan.EndBytes/1048576d,(snapshot?.Capacity??FcMbcxCodec.CapacityBytes)/1048576d,plan.DedicatedBanks,plan.SharedBanks);
            summary.Text+=Environment.NewLine+L("core_support_notice","FC and MD share this card; select slot 3 for FC or slot 4 for MD. Requires the paired MIX MCU firmware.");
            summary.Text+=Environment.NewLine+string.Format(L("correction_counts","自动识别/纠错 {0}｜手动修正 {1}｜待核对 {2}；扫描原卡后可检查 / 重建配置。"),
                games.Count(g=>g.Md is null&&g.Info.CorrectionKind is 1 or 2),games.Count(g=>g.Md is null&&g.Info.CorrectionKind==3),games.Count(g=>g.Md is null&&g.Info.Warning is not null&&g.Info.CorrectionKind==0));
            int unselected=games.Count(g=>!g.Enabled),unsupported=games.Count(g=>!g.Info.Supported);
            summary.Text+=Environment.NewLine+string.Format(L("selection_counts","Imported {0} | checked {1} | unchecked {2} | incompatible {3}; unchecked games are not written."),games.Count,games.Count(g=>g.Enabled),unselected,unsupported);
            if(unsupported>0)summary.Text+=Environment.NewLine+L("excluded_names","Incompatible:")+" "+string.Join("; ",games.Where(g=>!g.Info.Supported).Take(3).Select(g=>g.Name+": "+g.Info.Error))+(unsupported>3?" …":"");
            int invalid=games.Count(g=>g.Enabled&&!g.Info.Supported);if(invalid>0)summary.Text+=$" | {invalid} "+L("unsupported","unsupported (uncheck or remove)");
            if(backendFactory is not null)
            {
                if(snapshot is null)summary.Text+=Environment.NewLine+(CanScanBeforeBurn
                    ?L("burn_scan_first","Click Burn MIX card: the card will be read before write confirmation.")
                    :L("scan_required","Scan MIX card, or import NES ROMs to start a new burn."));
                else if(!snapshot.Writable)summary.Text+=Environment.NewLine+L("write_locked","Burn disabled: NOR capacity is unavailable or the card is write-protected. Check the scan result and scan again.");
            }
            burn.Enabled=!IsBusy&&backendFactory is not null&&invalid==0&&
                (snapshot is {Writable:true} || snapshot is null&&CanScanBeforeBurn);
        }
        catch(Exception ex){summary.Text=ex.Message;burn.Enabled=false;}
        finally {refreshing=false;scan.Enabled=!IsBusy;}
    }
    private async Task RunAsync(Func<CancellationToken,IProgress<FcProgress>,Task> work)
    {
        if(IsBusy)return;grid.EndEdit();operation=new();var activeOperation=operation;var token=operation.Token;
        foreach(var b in idleButtons)b.Enabled=false;grid.Enabled=false;scan.Enabled=false;burn.Enabled=false;cancel.Enabled=true;BusyChanged?.Invoke(true);
        var reporter=new Progress<FcProgress>(p=>{if(ReferenceEquals(operation,activeOperation)&&!token.IsCancellationRequested&&!IsDisposed){status.Text=p.Message;progress.Value=Math.Clamp(p.Percent,0,100);}});
        try{await work(token,reporter);}
        catch(OperationCanceledException){status.Text=L("cancelled","Cancelled; rescan the card after an interrupted write.");}
        catch(Exception ex){status.Text=ex.Message;MessageBox.Show(this,ex.Message,L("mode","MIX | FC + MD (MBCX)"),MessageBoxButtons.OK,MessageBoxIcon.Warning);}
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
                if(source.Platform==Services.RomPlatform.Md){
                    var md=source.MdRom!;
                    if(md.CanRead)imported.Add(Md.MdMbcxCodec.CreateGame(Md.MdRomImportService.ReadRom(md),source.Title));
                    else imported.Add(new FcGame{Name=source.Title,Data=[],Enabled=false,Md=new(0,0),
                        Info=new NesRomInfo(-1,0,0,0,0,0,0,0,false,false,0,"MD header",md.Assessment.Message)});
                }else if(source.Platform==Services.RomPlatform.Fc){
                    if(source.Length>FcMbcxCodec.CapacityBytes)throw new InvalidDataException("FC file exceeds card capacity.");
                    byte[] data;
                    if(source.EntryIndex is int index){using var zip=Md.MdZipFile.OpenRead(source.SourcePath);using var input=zip.Entries[index].Open();data=new byte[checked((int)source.Length)];input.ReadExactly(data);}
                    else data=File.ReadAllBytes(source.SourcePath);
                    var info=NesRomHeaderService.Parse(data);imported.Add(new FcGame{Name=source.Title,Data=data,Info=info,Enabled=info.Supported});
                }else imported.Add(new FcGame{Name=source.Title,Data=[],Enabled=false,
                    Info=new NesRomInfo(-1,0,0,0,0,0,0,0,false,false,0,source.Platform.ToString(),"MIX supports FC / MD only")});
            }
            return imported;
        },ct);
        ct.ThrowIfCancellationRequested();foreach(var game in list)games.Add(game);
        status.Text=string.Format(L("import_counts","Import complete: {0} checked, {1} unchecked (including {2} incompatible). Unchecked games are not included."),games.Count(g=>g.Enabled),games.Count(g=>!g.Enabled),games.Count(g=>!g.Info.Supported));
    });
    private async Task ImportImageAsync()
    {
        using var d=new OpenFileDialog(){Filter="MIX MBCX image (*.bin)|*.bin"};
        if(d.ShowDialog(this)!=DialogResult.OK)return;
        if(games.Count>0&&MessageBox.Show(this,L("image_confirm","Replace this workspace list with the MIX image? The card is not written yet."),L("mode","MIX | FC + MD (MBCX)"),MessageBoxButtons.YesNo)!=DialogResult.Yes)return;
        await RunAsync(async(ct,p)=>{
            var loaded=await Task.Run(()=>{
                using var file=File.OpenRead(d.FileName);
                if(file.Length<FcMbcxCodec.PrefixBytes||file.Length>FcMbcxCodec.CapacityBytes)throw new InvalidDataException("Invalid MIX image length.");
                byte[] Read(int address,int length){ct.ThrowIfCancellationRequested();if(address<0||length<0||address>file.Length-length)throw new InvalidDataException("MIX image range exceeds file length.");var bytes=new byte[length];file.Position=address;file.ReadExactly(bytes);return bytes;}
                return FcMbcxCodec.Read(Read(0,FcMbcxCodec.PrefixBytes),(int)file.Length,Read,ct);
            },ct);
            ct.ThrowIfCancellationRequested();games.Clear();imageReplacement=true;
            // Offline image locations are not a snapshot of the connected card.
            foreach(var g in loaded)games.Add(new FcGame{Name=g.Name,Data=g.Data,Info=g.Info,Md=g.Md,Enabled=g.Info.Supported});
            status.Text=L("image_loaded","MIX image loaded; choose Burn to write it to the scanned card.");
        });
    }
    private async Task ExportAsync()
    {
        using var d=new SaveFileDialog(){FileName="MIX_MBCX.bin",Filter="MIX card image (*.bin)|*.bin",DefaultExt="bin",AddExtension=true,OverwritePrompt=true};
        if(d.ShowDialog(this)!=DialogResult.OK)return;
        await RunAsync(async(ct,p)=>{var copy=games.Select(g=>g.Copy()).ToList();await Task.Run(()=>FcMbcxCodec.Export(d.FileName,copy,ct,p),ct);status.Text=string.Format(L("export_counts","MIX image created with {0} games; {1} unchecked rows excluded."),copy.Count(g=>g.Enabled),copy.Count(g=>!g.Enabled));});
    }
    public Task ScanAsync()=>RunAsync(async(ct,p)=>{
        if(backendFactory is null)return;
        if(games.Count>0&&MessageBox.Show(this,L("scan_confirm","Rescan card entries and keep imported files? Unwritten edits to card entries will be replaced."),L("mode","MIX | FC + MD (MBCX)"),MessageBoxButtons.YesNo)!=DialogResult.Yes)return;
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
            result.Warning??(result.IsFc?L("scan_done","MIX card loaded."):L("convert_notice","This is not an MIX card. Add NES ROMs; burning requires explicit conversion confirmation."));
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
    private Task BurnAsync()=>RunAsync(async(ct,p)=>{
        await PrepareBurnAsync(ct,p);
        bool convert=!snapshot!.IsFc;
        string prompt=convert?L("convert_confirm","Replace this card's existing GB/GBC/unknown directory with a dedicated MIX image? Existing games will no longer be listed."):
            L("burn_confirm","Apply this FC / MD list to the card? Deleted entries release space. Existing saves are preserved; new MD save areas are initialized.");
        if(MessageBox.Show(this,prompt,L("mode","MIX | FC + MD (MBCX)"),MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes)return;
        var copy=games.Select(g=>g.Copy()).ToList();var expected=snapshot;var backend=backendFactory!();
        // A failure invalidates the snapshot: stale metadata can never be reused.
        snapshot=null;
        var result=await Task.Run(()=>backend.Write(expected,copy,convert,ct,p),ct);
        snapshot=result;imageReplacement=false;games.Clear();foreach(var g in result.Games)games.Add(g.Copy());status.Text=string.Format(L("burn_counts","MIX card directory updated: {0} games. {1} unchecked rows were excluded; existing saves preserved; new MD saves initialized."),result.Games.Count,copy.Count(g=>!g.Enabled));
    });
}
