using GbaMultiMenuBuilder.Fc;
using GbaMultiMenuBuilder.Models;
namespace GbaMultiMenuBuilder;
public partial class Form1
{
    private FcWorkspaceControl? _fcWorkspace;
    private FlashCartMode _fcReturnMode;
    private readonly HashSet<string> queuedFcSources=new(StringComparer.OrdinalIgnoreCase);
    private async void ShowFcWorkspace()
    {
        if(_gamesLoading || _buildCancellation is not null)return;
        // Keep the previous GB/GBA workspace and its settings intact.
        _suppressFlashCartSync=true;
        _comboBoxFlashCartMode.SelectedItem=_flashCartModeOptions.FirstOrDefault(o=>o.Mode==_fcReturnMode);
        _suppressFlashCartSync=false;
        var previous=_groupBoxBuildSettings.Parent!;
        if(_fcWorkspace is null || _fcWorkspace.IsDisposed)
        {
            _fcWorkspace=new FcWorkspaceControl((k,f)=>T("fc",k,f));
            _fcWorkspace.ExitRequested+=()=>{_fcWorkspace.Visible=false;_groupBoxBuildSettings.Parent!.Visible=true;};
            Controls.Add(_fcWorkspace);
        }
        if(_fcWorkspace.Parent!=this)Controls.Add(_fcWorkspace);
        _fcWorkspace.RefreshLanguage();
        previous.Visible=false;_fcWorkspace.Visible=true;_fcWorkspace.BringToFront();
        var queued=_games.Where(g=>(g.OtherRom?.Platform==Services.RomPlatform.Fc||g.MdRom is not null)&&queuedFcSources.Add(g.OtherRom?.SourcePath??g.MdRom?.SourcePath??g.RomPath)).Select(g=>g.OtherRom?.SourcePath??g.MdRom?.SourcePath??g.RomPath).ToArray();
        if(queued.Length>0)await _fcWorkspace.ImportAsync(queued);
    }
}
