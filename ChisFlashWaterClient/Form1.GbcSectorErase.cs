using System.Threading;

namespace ChisFlashWaterClient;

public partial class Form1
{
    /// <summary>
    /// GBC 单卡 CFI 动态扇区表专用：返回按扇区擦除的委托，所有容量通用。
    /// 只在 bank 变化时才切 bank —— 一个 16 KiB bank 里的两个 8 KiB 小扇区共用同一次切换。
    /// 0x30 命令与状态轮询都落在扇区自身地址（SectorCommandAddress），与 MAX 路径口径一致。
    /// </summary>
    private Action<int, int> CreateGbcSectorEraser(
        WaterDeviceProtocol protocol, Func<int, bool> selectBank, CancellationToken cancellationToken)
    {
        return (sectorOffset, sectorSize) =>
        {
            // 盖中盖口径：扇区地址与大小不校验，读到的值直接下发，不因异常读数中止擦写。
            long eraseStart = StartLandscapeErase();
            int bank = GbcNorImageWriter.SectorRomBank(sectorOffset);
            if (!selectBank(bank))
                throw new InvalidOperationException(T("error", "erase_segment_failed", "Failed to switch GB ROM bank before erase."));

            uint commandAddress = GbcNorImageWriter.SectorCommandAddress(sectorOffset);
            if (!protocol.TryWriteGbc(0x0AAA, [0xAA], BeggarMaxWriteTimeoutMs) ||
                !protocol.TryWriteGbc(0x0555, [0x55], BeggarMaxWriteTimeoutMs) ||
                !protocol.TryWriteGbc(0x0AAA, [0x80], BeggarMaxWriteTimeoutMs) ||
                !protocol.TryWriteGbc(0x0AAA, [0xAA], BeggarMaxWriteTimeoutMs) ||
                !protocol.TryWriteGbc(0x0555, [0x55], BeggarMaxWriteTimeoutMs) ||
                !protocol.TryWriteGbc(commandAddress, [0x30], BeggarMaxWriteTimeoutMs))
            {
                throw new InvalidOperationException(T("error", "erase_segment_failed", "Failed to erase a GB flash sector."));
            }

            byte[] poll = [0x00];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!protocol.TryReadGbc(commandAddress, poll, BeggarMaxWriteTimeoutMs))
                    throw new InvalidOperationException(T("error", "erase_segment_failed", "Failed to poll GB flash sector erase status."));
                if (poll[0] == 0xFF) break;
                Thread.Sleep(BeggarMaxSectorErasePollDelayMs);
            }
            FinishLandscapeErase(eraseStart, sectorSize);
        };
    }
}
