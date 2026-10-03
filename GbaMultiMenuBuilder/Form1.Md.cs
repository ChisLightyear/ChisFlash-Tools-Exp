using GbaMultiMenuBuilder.Md;
using GbaMultiMenuBuilder.Models;
using GbaMultiMenuBuilder.Services;

namespace GbaMultiMenuBuilder;

public partial class Form1
{

    private bool MdBuildSelected => _games.Any(g=>g.Enabled) && _games.Where(g=>g.Enabled).All(g=>g.MdRom?.CanRead==true);
    private readonly Dictionary<string,string> archiveTitles=new(StringComparer.OrdinalIgnoreCase);
    private static string MdIdentity(MdRomCandidate item) => item.SourcePath + "|" + (item.ArchiveIndex?.ToString() ?? "file");

    private GameEntryModel CreateMdGame(MdRomCandidate item, string? error = null) => new()
    {
        MdRom = item, MdStatus = MdRomImportService.ImportStatus(item,error), Enabled = item.CanRead && error is null,
        Title = item.Title, GameTitle = item.Assessment.ProductCode.Trim(), RomPath = item.SourcePath,
        FileSizeDisplay = $"{item.RomBytes / 1048576.0:0.###} MiB", RomSizeDisplay = $"{item.RomBytes / 1048576.0:0.###} MiB",
        OccupiedSizeDisplay = "—", SaveTypeDisplay = "MD", MbcTypeDisplay = "MD", SaveSupportDisplay = "MBCX电池SRAM",
        ApplySramPatch = false, ApplyBatterylessPatch = false, SaveTypeKind = DetectedSaveTypeKind.Unknown
    };

    private void EnsureMdColumn()
    {
        if (_dataGridViewGames.Columns[nameof(GameEntryModel.MdStatus)] is null)
            _dataGridViewGames.Columns.Add(new DataGridViewTextBoxColumn { Name = nameof(GameEntryModel.MdStatus),
                DataPropertyName = nameof(GameEntryModel.MdStatus), HeaderText = "导入状态", ReadOnly = true, Width = 210 });
        _dataGridViewGames.Columns[nameof(GameEntryModel.MdStatus)]!.Visible = _games.Any(g => g.MdRom is not null || g.OtherRom is not null);
        _dataGridViewGames.Columns[nameof(GameEntryModel.MdStatus)]!.DisplayIndex = 3;
        foreach(DataGridViewRow row in _dataGridViewGames.Rows)
            if(row.DataBoundItem is GameEntryModel { MdRom:not null } game)
                row.Cells[nameof(GameEntryModel.MdStatus)].Style.ForeColor=game.MdRom.Assessment.Excluded?Color.Firebrick:SystemColors.ControlText;
    }

    // Runs before the existing GB/GBA path. All MD members become ordinary
    // rows in the same grid; archives are identified by member index, not name.
    private async Task<List<string>> ImportMdRowsAsync(IEnumerable<string> paths)
    {
        SetGamesLoadingState(true,"解析ROM / ZIP…");
        try
        {
            var batch = await Task.Run(() =>
            {
                var items = new List<(MdRomCandidate Item,string? Error)>();var others=new List<(ArchiveRomSource Source,string File)>();var remaining = new List<string>();var messages = new List<string>();
                foreach (string path in RomArchiveImportService.ExpandPaths(paths))
                {
                    try
                    {
                        var sources=RomArchiveImportService.Preview(path);
                        if(sources.Count==0){remaining.Add(path);continue;}
                        foreach (var source in sources)
                        {
                            if(source.MdRom is not { } item)
                            {
                                string file=RomArchiveImportService.Materialize(source);others.Add((source,file));continue;
                            }
                            string? error = null;
                            if(item.CanRead)try { _ = MdRomImportService.ReadRom(item); } catch(Exception ex) when(ex is IOException or InvalidDataException){error=ex.Message;}
                            items.Add((item with{Title=source.Title},error));
                        }
                    }
                    catch(Exception ex)when(ex is IOException or InvalidDataException or UnauthorizedAccessException){messages.Add(Path.GetFileName(path)+": "+ex.Message);}
                }
                return (items,others,remaining,messages);
            });
            if(IsDisposed)return [];
            foreach(var (item,error) in batch.items)
            {
                var existing=_games.FirstOrDefault(g=>g.MdRom is not null&&MdIdentity(g.MdRom).Equals(MdIdentity(item),StringComparison.OrdinalIgnoreCase));
                var model=CreateMdGame(item,error);
                if(existing is null)_games.Add(model);else _games[_games.IndexOf(existing)]=model;
            }
            foreach(string message in batch.messages)AppendLog(message);
            foreach(var (source,file) in batch.others)
            {
                archiveTitles[file]=source.Title;
                bool matches=source.Platform==RomPlatform.Gba&&_currentCompilationFamily==CompilationFamily.Gba || source.Platform==RomPlatform.Gbc&&_currentCompilationFamily==CompilationFamily.Gbc;
                if(matches){batch.remaining.Add(file);continue;}
                var model=CreateOtherGame(source,file);
                var old=_games.FirstOrDefault(g=>g.OtherRom?.SourcePath==source.SourcePath&&g.OtherRom.EntryIndex==source.EntryIndex);
                if(old is null)_games.Add(model);else _games[_games.IndexOf(old)]=model;
            }
            if(batch.items.Count>0){EnsureMdColumn();AppendLog($"已解析 {batch.items.Count} 个MD游戏，已加入主列表；请选择 MIX 模式，可与 NES 游戏一起合卡。");}
            if(batch.others.Count>0)EnsureMdColumn();
            return batch.remaining;
        }
        finally { if(!IsDisposed){SetGamesLoadingState(false,"Ready");SaveCurrentGameList();RefreshBuildCapacitySummary();} }
    }

    private GameEntryModel CreateOtherGame(ArchiveRomSource source,string file)=>new(){OtherRom=source,Enabled=false,
        Title=source.Title,RomPath=file,GameTitle=source.EntryName,FileSizeDisplay=$"{source.Length/1048576.0:0.###} MiB",
        SaveTypeDisplay=(source.Platform == GbaMultiMenuBuilder.Services.RomPlatform.Fc ? "NES" : source.Platform.ToString().ToUpperInvariant()),MdStatus=$"{(source.Platform == GbaMultiMenuBuilder.Services.RomPlatform.Fc ? "NES" : source.Platform.ToString().ToUpperInvariant())} · 已解析，请选择对应模式"};
}
