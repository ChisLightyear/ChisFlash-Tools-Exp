using GbaMultiMenuBuilder.Services;

namespace ChisFlashWaterClient;

public partial class Form1
{
    private void UpdateNativeSaveCommands(bool idle, bool hasGame)
    {
        menuSave.Enabled = true;
        bool available = idle && hasGame;
        menuExportSelectedSave.Enabled = menuItemExportSave.Enabled = available;
        menuImportSelectedSave.Enabled = menuItemImportSave.Enabled = available;
        menuClearSelectedSave.Enabled = menuItemClearSave.Enabled = false;
        menuExportChipSave.Visible = menuImportChipSave.Visible = menuClearChipSave.Visible = false;
        menuItemExportChipSaveContext.Visible = menuItemImportChipSaveContext.Visible = menuItemClearChipSaveContext.Visible = false;
        menuChipSaveSeparator.Visible = menuItemChipSaveSeparator.Visible = false;
    }

    private async Task<SelectedSaveTargetInfo?> ResolveSaveTransferTargetAsync(bool importing)
    {
        if (IsGbaSingleCardMode() || IsChisFlash10Mode() || IsGbcRawImageMode() || IsMbc5FixedSlotMode())
        {
            if (!EnsureSelectedPortHasActiveScan(out string reason))
            {
                ShowSaveFailure(reason);
                return null;
            }
            if (!IsBeggarCompatibleSelectedProgrammer())
            {
                ShowSaveFailure(ShellText("此卡型的原始存档读写需要盖中盖兼容烧录器。", "This save type requires a Beggar-compatible programmer."));
                return null;
            }
            if (IsGbcRawImageMode())
            {
                string port = GetSelectedPortName()!;
                SetBusy(true, ShellText("读取卡内游戏的存档规格…", "Reading save size from the cartridge…"));
                try
                {
                    return await Task.Run(() => ReadRtcSramTarget(port));
                }
                finally
                {
                    SetBusy(false, T("status", "ready", "Ready"));
                }
            }
            if (IsMbc5FixedSlotMode())
            {
                int index = SelectedMbc5Slot();
                if (index > 0 && mbc5ScannedSaveLayout is { } layout &&
                    layout.GetContent(index) is { } game && game.SaveSizeBytes > 0)
                {
                    Mbc5SlotDefinition slot = layout.GetSlot(index);
                    return CreateGbcRamSaveTarget(game.Title, slot.SaveOffsetBytes,
                        Math.Min(game.SaveSizeBytes, slot.SaveCapacityBytes),
                        Mbc5SlotName(index) + " · SRAM");
                }
                ShowSaveFailure(ShellText("所选槽位在卡内没有可用存档。", "The selected on-card slot has no available save."));
                return null;
            }

            if (GetHeadAreaGame() is not { } entry)
            {
                ShowSaveFailure(T("error", "save_action_requires_entry", "Please select a valid game entry first."));
                return null;
            }
            BurnProfileMode profile = GetSelectedBurnProfileMode();
            var kind = profile switch
            {
                BurnProfileMode.ChisFlashFlash11 or BurnProfileMode.ChisFlashFlash11Rtc => SelectedSaveStorageKind.BeggarFlash,
                BurnProfileMode.ChisFlashFram10 or BurnProfileMode.ChisFlashFram12 => SelectedSaveStorageKind.BeggarFram,
                _ => SelectedSaveStorageKind.BeggarChipRam,
            };
            string medium = kind switch
            {
                SelectedSaveStorageKind.BeggarFlash => "FLASH",
                SelectedSaveStorageKind.BeggarFram => "FRAM",
                _ => "SRAM",
            };
            return new SelectedSaveTargetInfo
            {
                Entry = entry, Kind = kind, SaveLength = kind == SelectedSaveStorageKind.BeggarFlash ? 0x10000 : 0x8000,
                MaximumTransferLength = 0x20000, SourceDescription = medium + ShellText(" 存档芯片", " save chip"),
            };
        }

        if (TryResolveSelectedSaveTarget(out var target, out string failure, forImport: importing))
            return target;
        ShowSaveFailure(failure);
        return null;
    }

    private SelectedSaveTargetInfo ReadRtcSramTarget(string port)
    {
        using var protocol = new WaterDeviceProtocol(port, ReportTransferredBytes);
        OpenProgrammerProtocol(protocol, ProgrammerKind.BeggarLegacy);
        byte[] header = new byte[0x150];
        if (!protocol.TryReadGbc(0, header))
            throw new IOException(ShellText("读取卡带游戏头失败。", "Could not read the cartridge header."));
        var metadata = GameBoyRomHeaderService.Read("cartridge.gb", header.AsSpan());
        if (!metadata.HasValidNintendoLogo || !metadata.HasBattery || metadata.RamSizeCode == 0 ||
            metadata.EffectiveRamBankCount is < 1 or > 8)
            throw new InvalidOperationException(ShellText("卡内游戏头没有声明可读取的 RTC 卡 SRAM 存档。", "The on-card header does not declare accessible RTC-cartridge SRAM."));
        int length = GetGbcSramLength(metadata);
        return CreateGbcRamSaveTarget(metadata.HeaderTitle, 0, length, ShellText("SRAM（游戏存档）", "Game SRAM"));
    }

    private static int GetGbcSramLength(GameBoyRomMetadata metadata) =>
        metadata.RamSizeCode == 1 ? 0x800 : metadata.EffectiveRamBankCount * GameBoyRomHeaderService.RamBankSize;

    private static SelectedSaveTargetInfo CreateGbcRamSaveTarget(string title, int address, int length, string source) => new()
    {
        Entry = new WaterCartEntry { HeaderGame = new DetectedNorGameInfo { Title = title } },
        Kind = SelectedSaveStorageKind.GbcPhysicalRam,
        SaveAddress = (uint)address,
        SaveLength = length,
        SourceDescription = source,
    };

    private void ShowSaveFailure(string message)
    {
        labelStatus.Text = message;
        AppendLog(message);
    }

    private bool ConfirmSaveImport(string title, SelectedSaveTargetInfo target, int fileLength, int importLength)
    {
        string size = FormatSaveTransferSize(importLength);
        string message = ShellText(
            $"将为《{title}》写入 {size} 存档数据。\n目标位置：{target.SourceDescription}",
            $"Write {size} of save data for {title}?\nDestination: {target.SourceDescription}");
        if (fileLength > importLength)
            message += ShellText("\n文件超出目标容量，只导入前 " + size + "。", "\nThe file exceeds the target capacity. Only the first " + size + " will be imported.");
        if (target.Kind == SelectedSaveStorageKind.BeggarFlash)
            message += ShellText("\n会先擦除整片存档 FLASH。", "\nThe save FLASH chip will be erased before writing.");
        if (target.Kind == SelectedSaveStorageKind.BeggarMaxRamSlot && target.SaveLength > importLength)
            message += ShellText("\n完整槽位大小为 " + FormatSaveTransferSize(target.SaveLength) + "，尾部填充 FF。",
                "\nThe full slot is " + FormatSaveTransferSize(target.SaveLength) + ". The remainder is filled with FF.");
        if (target.Kind == SelectedSaveStorageKind.BeggarMaxRamSlot && target.Entry.UsesSharedSave)
            message += ShellText("\n写入会覆盖使用同一 SRAM 的游戏存档。", "\nWriting will overwrite saves sharing this SRAM.");
        return MessageBox.Show(this, message, T("dialog", "import_save_title", "Import Save"),
            MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK;
    }

    private static string FormatSaveTransferSize(int length) => length % 1024 == 0
        ? FormatKiBSize(length) : $"{length} B";

    private bool TryResolveActiveBeggarSave(WaterCartEntry entry, WaterCartScanResult scan, bool forImport, out int size)
    {
        size = 0;
        var status = scan.MenuConfig.Status;
        if (status.LastBootSaveType == 0 || status.LastBootSaveIndex == 0xFF ||
            entry.SaveIndex != status.LastBootSaveIndex) return false;
        if (scan.IsChm1)
        {
            if (forImport) return false;
            size = 0x20000;
            return true;
        }
        return IsCurrentChipSaveEntry(entry) && TryResolveCurrentChipSaveEntry(out _, out size);
    }

    private byte[] TransferGbcPhysicalSave(WaterDeviceProtocol protocol, uint start, int length, byte[]? importData = null)
    {
        // 原版按物理 SRAM bank 寻址，每次最多传输 4096 字节。
        byte[] data = importData ?? new byte[length];
        if (!TrySetBeggarMaxRamEnabled(protocol, true)) throw new IOException(protocol.DescribeLastAckFailure());
        int currentBank = -1;
        for (int offset = 0; offset < data.Length;)
        {
            uint address = start + (uint)offset;
            int bank = (int)(address >> 13);
            int bankOffset = (int)(address & 0x1FFF);
            if (currentBank != bank)
            {
                if (!TrySwitchBeggarMaxRamBank(protocol, bank)) throw new IOException(protocol.DescribeLastAckFailure());
                currentBank = bank;
            }
            int count = Math.Min(data.Length - offset, Math.Min(4096, 0x2000 - bankOffset));
            uint window = (uint)(0xA000 + bankOffset);
            if (importData is null)
            {
                byte[] chunk = new byte[count];
                if (!protocol.TryReadGbc(window, chunk)) throw new IOException(ShellText("读取 GBC 存档失败。", "Could not read the GBC save."));
                chunk.CopyTo(data, offset);
            }
            else if (!protocol.TryWriteGbc(window, data.AsSpan(offset, count).ToArray()))
                throw new IOException(protocol.DescribeLastAckFailure());
            offset += count;
        }
        return data;
    }

    private byte[] TransferGbaNativeSave(WaterDeviceProtocol protocol, SelectedSaveStorageKind kind, int length, byte[]? importData = null)
    {
        byte[] data = importData ?? new byte[length];
        bool flash = kind == SelectedSaveStorageKind.BeggarFlash;
        bool fram = kind == SelectedSaveStorageKind.BeggarFram;
        if (flash && importData is not null)
        {
            WriteFlashCommand(0x80);
            WriteFlashCommand(0x10);
            byte[] status = new byte[1];
            do
            {
                Thread.Sleep(200);
                if (!protocol.TryReadRam(0, status)) throw new IOException(ShellText("读取 FLASH 擦除状态失败。", "Could not read FLASH erase status."));
            } while (status[0] != 0xFF);
        }

        for (int offset = 0; offset < data.Length; offset += 4096)
        {
            uint address = (uint)(offset & 0xFFFF);
            if (address == 0)
            {
                byte bank = (byte)(offset >> 16);
                if (flash)
                {
                    WriteFlashCommand(0xB0);
                    WriteRamByte(0, bank);
                }
                else if (!protocol.TryWriteRom(0x800000, BitConverter.GetBytes((ushort)bank)))
                    throw new IOException(protocol.DescribeLastAckFailure());
            }
            int count = Math.Min(4096, data.Length - offset);
            if (importData is null)
            {
                byte[] chunk = new byte[count];
                if (!(fram ? protocol.TryReadFramSave(address, chunk) : protocol.TryReadRam(address, chunk)))
                    throw new IOException(ShellText("读取 GBA 存档失败。", "Could not read the GBA save."));
                chunk.CopyTo(data, offset);
            }
            else
            {
                byte[] chunk = data.AsSpan(offset, count).ToArray();
                bool written = flash ? protocol.TryProgramFlashSave(address, chunk)
                    : fram ? protocol.TryWriteFramSave(address, chunk) : protocol.TryWriteRam(address, chunk);
                if (!written) throw new IOException(protocol.DescribeLastAckFailure());
            }
        }
        return data;

        void WriteFlashCommand(byte command)
        {
            WriteRamByte(0x5555, 0xAA);
            WriteRamByte(0x2AAA, 0x55);
            WriteRamByte(0x5555, command);
        }

        void WriteRamByte(uint address, byte value)
        {
            if (!protocol.TryWriteRam(address, [value])) throw new IOException(protocol.DescribeLastAckFailure());
        }
    }
}
