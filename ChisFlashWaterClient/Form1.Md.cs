using ClientText = ChisFlashWaterClient.Services.LocalizedText;
using GbaMultiMenuBuilder.Md;
using GbaMultiMenuBuilder.Services;

namespace ChisFlashWaterClient;

public partial class Form1
{
    private bool CanWriteMdQueue => pendingImportItems.Count>0 && pendingImportItems.All(i=>i.MdRom?.CanRead==true && i.MdReadError is null);
    // Reading local MD files is independent of a programmer/card scan. All
    // writes are performed through the shared MIX workspace.
    private async Task ImportMdWithoutCardAsync(IEnumerable<string> paths)
    {
        if(isAnalyzingQueueImports)return;
        string[] inputs=paths.ToArray();SetQueueAnalysisState(true,inputs.Length);
        try
        {
            var batch=await Task.Run(()=>{
                var items=new List<PendingRomImportItem>();var messages=new List<string>();
                foreach(string path in RomArchiveImportService.ExpandPaths(inputs))
                {
                    try
                    {
                        if(RomArchiveImportService.Preview(path).Count==0){messages.Add(Path.GetFileName(path)+ClientText.T("client", "no_game_files", ": No game files found."));continue;}
                        var found=CreatePendingRomImportItemsFromSource(path,false);items.AddRange(found.Items);messages.AddRange(found.Messages);
                    }
                    catch(Exception ex)when(ex is IOException or InvalidDataException or UnauthorizedAccessException){messages.Add(Path.GetFileName(path)+": "+ex.Message);}
                }
                return(items,messages);
            });
            if(IsDisposed)return;
            foreach(var item in batch.items)
            {
                var previous=pendingImportItems.FirstOrDefault(old=>item.MdRom is not null
                    ? old.MdRom?.SourcePath==item.MdRom.SourcePath&&old.MdRom.ArchiveIndex==item.MdRom.ArchiveIndex
                    : old.MdRom is null&&old.FilePath.Equals(item.FilePath,StringComparison.OrdinalIgnoreCase));
                if(previous is null)pendingImportItems.Add(item);else pendingImportItems[pendingImportItems.IndexOf(previous)]=item;
            }
            foreach(string message in batch.messages)AppendLog(message);
            if(batch.items.Count>0)AppendLog(ClientText.Tf("client", "games_parsed", "Parsed {0} game(s) and added them to the main list.", batch.items.Count));
            RebuildWorkspaceRows();
        }
        finally{if(!IsDisposed)SetQueueAnalysisState(false,inputs.Length);}
    }

    private bool HasUnreadyPlatformImports(IEnumerable<WorkspaceListRow> rows)=>rows.Any(row=>row.PendingItem is { } item&&
        (item.ImportedPlatform is RomPlatform.Md or RomPlatform.Fc or RomPlatform.Snes ||
         item.ImportedPlatform==RomPlatform.Gbc&&!IsGbcWorkspaceMode() || item.ImportedPlatform==RomPlatform.Gba&&IsGbcWorkspaceMode()));
}
