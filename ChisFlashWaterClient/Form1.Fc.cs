using GbaMultiMenuBuilder.Fc;
namespace ChisFlashWaterClient;
public partial class Form1
{
    private FcWorkspaceControl? fcWorkspace;
    private readonly Func<IFcCardBackend>? fcBackendOverride;
    private bool HasActiveMixWorkspace=>fcWorkspace is {IsDisposed:false,Visible:true};
    private void UpdateMixRebuildMenu(bool busy,bool standalone=false)
    {
        menuCartridge.Enabled=true;
        // Disable only the legacy card commands on the MIX surface. The MIX
        // commands must be restored on every refresh, including menu opening.
        foreach(ToolStripItem item in menuCartridge.DropDownItems)
            item.Enabled=!busy && (item==menuFcScan || item==menuFcTools);
        menuUpdateMenu.Text=T("ui","force_rebuild_menu","重建菜单");
        menuUpdateMenu.Enabled=!busy;
        menuEraseEntireNor.Enabled=!busy&&CanEraseEntireNorCurrentScan(out _);
        if(standalone){menuRom.Enabled=false;menuSave.Enabled=false;menuScan.Enabled=false;}
    }
    private readonly HashSet<string> queuedFcSources = new(StringComparer.OrdinalIgnoreCase);

    private async Task<bool> OpenDetectedFcCardAsync(WaterCartScanResult result)
    {
        RememberGbcProfileCapacity(result);
        RefreshBurnProfileModeOptions(GetSelectedBurnProfileMode());
        if (!result.IsFcMbcxContent || GetSelectedBurnProfileMode() != BurnProfileMode.FcMbcx) return false;
        ResetCurrentCartAnalysisView();
        SetBusy(false, T("fc", "ready", "MIX ready"));
        await ActivateFcWorkspaceAsync();
        fcHardwareInfo = result;
        if (IsFcWorkspaceActive) await fcWorkspace!.ScanAsync();
        return true;
    }

    private async void ShowFcWorkspace()
    {
        await RunUiActionSafelyAsync(ActivateFcWorkspaceAsync);
    }
    private IFcCardBackend CreateFcBackend()
    {
        if(fcBackendOverride is not null)return fcBackendOverride();
        string port=GetSelectedPortName()??throw new InvalidOperationException("Select a programmer port first.");
        return new FcBackend(this,port);
    }
    private sealed class FcBackend(Form1 owner,string port) : IFcCardBackend
    {
        public FcCardSnapshot RebuildConfiguration(FcCardSnapshot expected,CancellationToken ct,IProgress<FcProgress> progress)
        {using var io=new FcIo(owner,port,ct);return FcCardEngine.RebuildConfiguration(io,expected,ct,progress);}
        public IReadOnlyList<FcGame> LoadGames(FcCardSnapshot expected,IReadOnlyList<FcGame> games,CancellationToken ct,IProgress<FcProgress> progress)
        {using var io=new FcIo(owner,port,ct);return FcCardEngine.LoadGames(io,expected,games,ct,progress);}
        public IReadOnlyList<GameLibraryDirectory.SaveImage> ExportSaves(FcCardSnapshot expected,CancellationToken ct,IProgress<FcProgress> progress)
        {using var io=new FcIo(owner,port,ct);return FcCardEngine.ExportSaves(io,expected,ct,progress);}
        public void ImportSaves(FcCardSnapshot expected,IReadOnlyList<GameLibraryDirectory.SaveImage> saves,CancellationToken ct,IProgress<FcProgress> progress)
        {using var io=new FcIo(owner,port,ct);FcCardEngine.ImportSaves(io,expected,saves,ct,progress);}
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
        private int selectedRomBank=-1;
        public string DeviceId {get;}
        public int Capacity=>device.Geometry.DeviceSizeBytes;
        public int SectorBytes=>device.Geometry.SectorSizeBytes;
        public bool Writable=>device.Geometry.DeviceSizeBytes==FcMbcxCodec.CapacityBytes&&!device.IsWriteProtected;
        public FcIo(Form1 owner,string port,CancellationToken ct)
        {
            this.owner=owner;ct.ThrowIfCancellationRequested();protocol=new WaterDeviceProtocol(port,owner.ReportTransferredBytes);
            try
            {
                owner.OpenProgrammerProtocol(protocol,ProgrammerKind.BeggarLegacy);
                if(!WaterCartScanner.TryScanBeggarMaxGbc(protocol,owner.AppendLog,out var detected,scanContent:false))
                    throw new InvalidOperationException("MBCX NOR probe failed.");
                device=detected;DeviceId=port+"|"+detected.RawNorIdHex;
                owner.UpdateFcHardwareInfo(device);
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
                if(selectedRomBank!=bank){if(!TrySwitchBeggarMaxRomBank(protocol,bank))throw new IOException("MIX ROM bank selection failed.");selectedRomBank=bank;}
                if(!protocol.TryReadGbc((uint)(GetBeggarMaxBankBaseAddress(bank)+within),chunk,3000))throw new IOException("MIX MBCX read failed.");
                chunk.CopyTo(data,offset);offset+=size;
            }
            return data;
        }
        public void RewriteSector(int address,byte[] data,CancellationToken ct)
        {
            if(!Writable||address<0||address%SectorBytes!=0||data.Length!=SectorBytes||address>Math.Min(Capacity,FcMbcxCodec.CapacityBytes)-SectorBytes)
                throw new InvalidOperationException("Invalid/protected NES sector write.");
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(120));
            selectedRomBank=-1;
            try{owner.RewriteBeggarMaxSector(protocol,address,data,device.Geometry,"NES MBCX",deadline.Token);}
            catch(OperationCanceledException)when(!ct.IsCancellationRequested){throw new TimeoutException("NES sector write timed out; rescan before retrying.");}
            finally {selectedRomBank=-1;}
        }
        public byte[] ReadSave(int start,int size,CancellationToken ct)
        {
            if(start<0||size<0||start>524288-size)throw new InvalidDataException("Invalid MIX SRAM read range.");
            byte[] data=new byte[size];
            selectedRomBank=-1;
            if(!TrySetBeggarMaxRamEnabled(protocol,true))throw new IOException("SRAM enable failed.");
            try {
                for(int offset=0;offset<size;){
                    ct.ThrowIfCancellationRequested();int address=start+offset,bytes=Math.Min(4096,Math.Min(size-offset,8192-(address&8191)));byte[] chunk=new byte[bytes];
                    if(!TrySwitchBeggarMaxRamBank(protocol,address>>13)||!protocol.TryReadGbc((uint)(0xa000+(address&8191)),chunk,3000))throw new IOException("MIX SRAM read failed.");
                    chunk.CopyTo(data,offset);offset+=bytes;
                }
            } finally {TrySetBeggarMaxRamEnabled(protocol,false);}
            return data;
        }
        public void WriteSave(int start,byte[] data,CancellationToken ct)
        {
            if(!Writable||start<0||start>60*8192-data.Length)throw new InvalidDataException("Invalid/protected MIX save range.");
            selectedRomBank=-1;
            if(!TrySetBeggarMaxRamEnabled(protocol,true))throw new IOException("SRAM enable failed.");
            try {
                for(int offset=0;offset<data.Length;){
                    ct.ThrowIfCancellationRequested();int address=start+offset,bytes=Math.Min(4096,Math.Min(data.Length-offset,8192-(address&8191)));var chunk=data.AsSpan(offset,bytes).ToArray();byte[] verify=new byte[bytes];
                    if(!TrySwitchBeggarMaxRamBank(protocol,address>>13))throw new IOException("SRAM bank failed.");
                    uint bus=(uint)(0xa000+(address&8191));
                    if(!protocol.TryWriteGbc(bus,chunk,3000)||!protocol.TryReadGbc(bus,verify,3000)||!chunk.AsSpan().SequenceEqual(verify))throw new IOException("MIX SRAM verify failed.");
                    offset+=bytes;
                }
            } finally {TrySetBeggarMaxRamEnabled(protocol,false);}
        }
        public void InitializeSave(int start,int size,byte fill,CancellationToken ct)
        {
            if(start<0||size<0||start>60*8192-size)throw new InvalidDataException("Invalid MIX save range.");
            selectedRomBank=-1;
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
