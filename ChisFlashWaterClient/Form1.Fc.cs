using GbaMultiMenuBuilder.Fc;
namespace ChisFlashWaterClient;
public partial class Form1
{
    private FcWorkspaceControl? fcWorkspace;
    private float fcInfoRowHeight;
    private readonly Func<IFcCardBackend>? fcBackendOverride;
    private readonly HashSet<string> queuedFcSources=new(StringComparer.OrdinalIgnoreCase);
    private async Task<bool> OpenDetectedFcCardAsync(WaterCartScanResult result)
    {
        if(!result.IsFcMbcxContent)return false;
        ResetCurrentCartAnalysisView();
        SetBusy(false,T("fc","detected","FC data card detected; opening the MIX workspace."));
        ShowFcWorkspace();
        if(fcWorkspace is not null)await fcWorkspace.ScanAsync();
        return true;
    }
    private async void ShowFcWorkspace()
    {
        if(isBusy||isAnalyzingQueueImports||fcWorkspace?.Visible==true)return;
        fcInfoRowHeight=rootLayout.RowStyles[1].Height;
        // Restore the original selector behind this embedded FC surface.
        burnProfileModeSelectionInitialized=false;
        comboBoxBurnProfileMode.SelectedItem=burnProfileModeOptions.FirstOrDefault(o=>o.Mode==BurnProfileMode.BeggarMaxSram);
        burnProfileModeSelectionInitialized=true;
        if(fcWorkspace is null||fcWorkspace.IsDisposed)
        {
            fcWorkspace=new FcWorkspaceControl((k,f)=>T("fc",k,f),CreateFcBackend);
            fcWorkspace.BusyChanged+=busy=>SetBusy(busy,T("fc",busy?"working":"ready",busy?"FC operation in progress":"FC ready"));
            fcWorkspace.ExitRequested+=()=>{
                fcWorkspace.Visible=false;rootLayout.Controls.Remove(fcWorkspace);rootLayout.Controls.Add(splitContainerContent,0,2);
                groupBoxInfo.Visible=true;splitContainerContent.Visible=true;rootLayout.RowStyles[1].Height=fcInfoRowHeight;mainMenuStrip.Visible=true;
                // FC may have converted/written the card; no stale GB scan may survive.
                ResetCurrentCartAnalysisView();RebuildWorkspaceRows();
            };

        }
        groupBoxInfo.Visible=false;splitContainerContent.Visible=false;rootLayout.Controls.Remove(splitContainerContent);
        if(fcWorkspace.Parent!=rootLayout)rootLayout.Controls.Add(fcWorkspace,0,2);
        rootLayout.RowStyles[1].Height=0;mainMenuStrip.Visible=false;
        fcWorkspace.RefreshLanguage();fcWorkspace.Visible=true;fcWorkspace.BringToFront();
        var queued=pendingImportItems.Where(item=>(item.FcRom is not null||item.MdRom is not null)&&queuedFcSources.Add(item.MdRom?.SourcePath??item.SourceImportKey??item.FilePath)).Select(item=>item.MdRom?.SourcePath??item.SourceImportKey??item.FilePath).ToArray();
        if(queued.Length>0)await fcWorkspace.ImportAsync(queued);
    }
    private IFcCardBackend CreateFcBackend()
    {
        if(fcBackendOverride is not null)return fcBackendOverride();
        if(GetRequiredSelectedProgrammerKind()!=ProgrammerKind.BeggarLegacy||GetSelectedBeggarCartMode()!=BeggarCartMode.Gb)
            throw new InvalidOperationException(T("fc","programmer_required","Select the existing GB/MAX MBCX programmer and GB physical mode first."));
        string port=GetSelectedPortName()??throw new InvalidOperationException("Select a programmer port first.");
        return new FcBackend(this,port);
    }
    private sealed class FcBackend(Form1 owner,string port) : IFcCardBackend
    {
        public FcCardSnapshot Scan(CancellationToken ct,IProgress<FcProgress> progress)
        {using var io=new FcIo(owner,port,ct);return FcCardEngine.Scan(io,ct,progress);}
        public FcCardSnapshot Write(FcCardSnapshot expected,IReadOnlyList<FcGame> games,bool convert,CancellationToken ct,IProgress<FcProgress> progress)
        {using var io=new FcIo(owner,port,ct);return FcCardEngine.Write(io,expected,games,convert,ct,progress);}
    }
    private sealed class FcIo : IFcCardIo
    {
        private readonly Form1 owner;
        private readonly WaterDeviceProtocol protocol;
        private readonly WaterCartScanResult device;
        public string DeviceId {get;}
        public int Capacity=>device.Geometry.DeviceSizeBytes;
        public int SectorBytes=>device.Geometry.SectorSizeBytes;
        public bool Writable=>device.Geometry.DeviceSizeBytes>0&&!device.IsWriteProtected;
        public FcIo(Form1 owner,string port,CancellationToken ct)
        {
            this.owner=owner;ct.ThrowIfCancellationRequested();protocol=new WaterDeviceProtocol(port,owner.ReportTransferredBytes);
            try
            {
                owner.OpenProgrammerProtocol(protocol,ProgrammerKind.BeggarLegacy);
                if(!WaterCartScanner.TryScanBeggarMaxGbc(protocol,owner.AppendLog,out var detected,scanContent:false))
                    throw new InvalidOperationException("MBCX NOR probe failed.");
                device=detected;DeviceId=port+"|"+detected.RawNorIdHex;
            }
            catch{protocol.Dispose();throw;}
        }
        public byte[] Read(int address,int length,CancellationToken ct)
        {
            if(address<0||length<0||address>Math.Min(Capacity,FcMbcxCodec.CapacityBytes)-length)throw new ArgumentOutOfRangeException(nameof(address));
            var data=new byte[length];
            for(int offset=0;offset<length;)
            {
                ct.ThrowIfCancellationRequested();int absolute=address+offset,bank=absolute>>14,within=absolute&0x3fff;
                int size=Math.Min(4096,Math.Min(length-offset,16384-within));var chunk=new byte[size];
                if(!TrySwitchBeggarMaxRomBank(protocol,bank)||!protocol.TryReadGbc((uint)(GetBeggarMaxBankBaseAddress(bank)+within),chunk,3000))throw new IOException("FC MBCX read failed.");
                chunk.CopyTo(data,offset);offset+=size;
            }
            return data;
        }
        public void RewriteSector(int address,byte[] data,CancellationToken ct)
        {
            if(!Writable||address<0||address%SectorBytes!=0||data.Length!=SectorBytes||address>Math.Min(Capacity,FcMbcxCodec.CapacityBytes)-SectorBytes)
                throw new InvalidOperationException("Invalid/protected FC sector write.");
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(120));
            try{owner.RewriteBeggarMaxSector(protocol,address,data,device.Geometry,"FC MBCX",deadline.Token);}
            catch(OperationCanceledException)when(!ct.IsCancellationRequested){throw new TimeoutException("FC sector write timed out; rescan before retrying.");}
        }
        public void InitializeSave(int start,int size,byte fill,CancellationToken ct)
        {
            if(start<0||size<0||start>60*8192-size)throw new InvalidDataException("Invalid MIX save range.");
            if(!TrySetBeggarMaxRamEnabled(protocol,true))throw new IOException("SRAM enable failed.");
            try {
                for(int address=start;address<start+size;){
                    ct.ThrowIfCancellationRequested();int n=Math.Min(4096,Math.Min(start+size-address,8192-(address&8191)));
                    if(!TrySwitchBeggarMaxRamBank(protocol,address>>13))throw new IOException("SRAM bank failed.");
                    byte[] bytes=new byte[n];Array.Fill(bytes,fill);byte[] verify=new byte[n];uint bus=(uint)(0xa000+(address&8191));
                    if(!protocol.TryWriteGbc(bus,bytes,3000)||!protocol.TryReadGbc(bus,verify,3000)||!bytes.AsSpan().SequenceEqual(verify))
                        throw new IOException("MIX SRAM verify failed.");
                    address+=n;
                }
            } finally {TrySetBeggarMaxRamEnabled(protocol,false);}
        }
        public void Dispose()=>protocol.Dispose();
    }
}
