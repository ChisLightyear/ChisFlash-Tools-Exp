namespace GbaMultiMenuBuilder.Fc;

public sealed class FcConfigurationDialog : Form
{
    private readonly FcGame game;
    private readonly Func<string,string,string> text;
    private readonly NumericUpDown mapper=Number(4095),sub=Number(15),work=Number(480),save=Number(480),chr=Number(512),saveChr=Number(480);
    private readonly CheckBox battery=new(){AutoSize=true};
    private readonly Label description=new(){AutoSize=true,MaximumSize=new Size(630,0)};
    private NesRomInfo? automatic;
    public NesRomInfo? Result {get;private set;}
    private string L(string k,string f)=>text(k,f);
    private static NumericUpDown Number(int max)=>new(){Minimum=0,Maximum=max,Width=120};
    public FcConfigurationDialog(FcGame game,Func<string,string,string> text)
    {
        this.game=game;this.text=text;
        Text=L("edit_config","修正 NES 配置");StartPosition=FormStartPosition.CenterParent;
        AutoSize=true;AutoSizeMode=AutoSizeMode.GrowAndShrink;MaximizeBox=false;MinimizeBox=false;
        var layout=new TableLayoutPanel(){AutoSize=true,ColumnCount=2,Padding=new Padding(16),Dock=DockStyle.Fill};
        Controls.Add(layout);
        void Wide(Control c){int row=layout.RowCount++;layout.Controls.Add(c,0,row);layout.SetColumnSpan(c,2);}
        void Field(string label,Control c){int row=layout.RowCount++;layout.Controls.Add(new Label(){Text=label,AutoSize=true,Padding=new Padding(0,5,20,5)},0,row);layout.Controls.Add(c,1,row);}
        Wide(new Label(){Text=game.Name,AutoSize=true,MaximumSize=new Size(630,0)});
        Wide(new Label(){Text=L("config_explain","配置单独保存；原 ROM 不变。应用后仍需点击“烧录 MIX 卡”。未知板型请核对资料，不按文件名猜测。"),AutoSize=true,MaximumSize=new Size(630,0)});
        Wide(new Label(){Text=L("header_mapper","原始 Mapper")+": "+game.HeaderMapperDisplay+"; PRG "+game.Info.PrgBytes/1024+" KiB; CHR-ROM "+game.Info.ChrBytes/1024+" KiB",AutoSize=true});
        Field("Mapper",mapper);Field("Submapper",sub);
        foreach(var n in new[]{work,save,chr,saveChr}){n.DecimalPlaces=3;n.Increment=1;}
        Field(L("work_ram","工作 RAM (KiB)"),work);Field(L("save_ram","存档 RAM (KiB)"),save);
        Field("CHR-RAM (KiB)",chr);Field(L("save_chr_ram","持久 CHR-RAM (KiB)"),saveChr);
        battery.Text=L("battery","电池");Field("",battery);Wide(description);
        var buttons=new FlowLayoutPanel(){AutoSize=true,WrapContents=true,MaximumSize=new Size(660,0)};
        var detect=new Button(){Text=L("auto_identify","恢复自动识别"),AutoSize=true};
        detect.Click+=(_,_)=>{try{var i=NesRomHeaderService.Parse(game.Data);if(!i.Supported)throw new InvalidDataException(i.Error);LoadInfo(i);automatic=i;}catch(Exception ex){ShowError(ex);}};
        var defaults=new Button(){Text=L("mapper_defaults","按 Mapper 填写 RAM"),AutoSize=true};
        defaults.Click+=(_,_)=>{try{LoadInfo(NesConfiguration.Defaults(game.Data,(int)mapper.Value,(int)sub.Value));}catch(Exception ex){ShowError(ex);}};
        var detected=NesRomHeaderService.Parse(game.Data);
        var candidate=new Button(){Text=L("use_candidate","填写候选板型（待确认）"),AutoSize=true,Visible=detected.SuggestedMapper.HasValue};
        candidate.Click+=(_,_)=>{try{if(detected.SuggestedMapper is int id)LoadInfo(NesConfiguration.Defaults(game.Data,id,0));}catch(Exception ex){ShowError(ex);}};
        var apply=new Button(){Text=L("apply_config","应用到列表"),AutoSize=true};
        apply.Click+=(_,_)=>{
            try{
                int Bytes(NumericUpDown n){decimal value=n.Value*1024;if(value!=decimal.Truncate(value))throw new InvalidDataException(L("ram_bytes","RAM 容量必须是整数个字节。"));return (int)value;}
                var result=NesConfiguration.Manual(game,(int)mapper.Value,(int)sub.Value,Bytes(work),Bytes(save),Bytes(chr),Bytes(saveChr),battery.Checked);
                if(automatic is not null&&NesConfiguration.SameGeometry(result,automatic)){NesConfiguration.Validate(game,automatic);result=automatic;}
                Result=result;DialogResult=DialogResult.OK;Close();
            }catch(Exception ex){ShowError(ex);}
        };
        var cancel=new Button(){Text=L("cancel","取消"),AutoSize=true,DialogResult=DialogResult.Cancel};
        buttons.Controls.AddRange([detect,defaults,candidate,apply,cancel]);Wide(buttons);AcceptButton=apply;CancelButton=cancel;
        foreach(var n in new[]{mapper,sub,work,save,chr,saveChr})n.ValueChanged+=(_,_)=>automatic=null;
        battery.CheckedChanged+=(_,_)=>automatic=null;
        LoadInfo(game.Info);
        // Opening and accepting without edits retains the existing provenance.
        automatic=game.Info;
    }
    private void LoadInfo(NesRomInfo info)
    {
        mapper.Value=Math.Clamp(info.Mapper,0,4095);sub.Value=Math.Clamp(info.Submapper,0,15);work.Value=Math.Clamp(info.WorkRamBytes/1024m,0,32);save.Value=Math.Clamp(info.SaveRamBytes/1024m,0,32);
        chr.Value=Math.Clamp(info.ChrRamBytes/1024m,0,512);saveChr.Value=Math.Clamp(info.SaveChrRamBytes/1024m,0,32);battery.Checked=info.Battery;
        description.Text=NesConfiguration.Localize(info.MetadataSource,text)+(info.Error is not null?"\n"+info.Error:info.Warning is null?"":"\n"+NesConfiguration.Localize(info.Warning,text));automatic=null;
    }
    private void ShowError(Exception ex)=>MessageBox.Show(this,ex.Message,Text,MessageBoxButtons.OK,MessageBoxIcon.Warning);
}
