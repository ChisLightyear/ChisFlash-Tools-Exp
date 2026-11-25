using ClientText = ChisFlashWaterClient.Services.LocalizedText;
using System.Diagnostics;
namespace ChisFlashWaterClient;

/// <summary>
/// 盖中盖原版单游戏 GBA 擦写流程。
/// 依据 FPGA-DMG-01/丐中丐/上位机/ChisFlashBurner/mission_gba.cs。
/// 不经过合卡改写、分桶切换、写后探针或回读校验。
/// </summary>
public static class LegacyGbaRomWriter
{
    public static (TimeSpan Erase, TimeSpan Program) Write(
        byte[] image,
        Action<uint, ushort> writeWord,
        Func<uint, int, byte[]> readRom,
        Action<uint, byte[], ushort> programRom,
        Action<int> delay,
        Action<int, int, string>? progress = null,
        CancellationToken cancellationToken = default,
        Action? beforeErase = null,
        Func<long>? startSectorErase = null,
        Action<long, int>? finishSectorErase = null,
        Action<int>? beginErase = null,
        Action? beginProgram = null,
        (int Capacity, int SectorSize, int BufferSize)? writeLayout = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (bufferBytes, deviceBytes) = ReadSize(writeWord, readRom);
        // 原版随后通过 gba_isMultiCard 再查询一次容量；1.0V 为单卡，保留相同事务。
        ReadSize(writeWord, readRom);
        int layoutSector = 0;
        if (deviceBytes <= 1 && writeLayout is { } layout)
        {
            deviceBytes = layout.Capacity > 1 ? layout.Capacity : image.Length;
            bufferBytes = layout.BufferSize;
            layoutSector = layout.SectorSize;
        }
        if (image.Length > deviceBytes) throw new InvalidOperationException(ClientText.T("client", "flash_no_space", "Not enough flash space."));

        TimeSpan eraseTime = TimeSpan.Zero;
        if (beforeErase is not null)
        {
            long start = Stopwatch.GetTimestamp();
            beforeErase();
            eraseTime = Stopwatch.GetElapsedTime(start);
        }
        byte[] first = readRom(0, 512);
        if (first.Any(value => value != 0xFF))
        {
            long start = Stopwatch.GetTimestamp();
            int[] sectorAddresses = layoutSector == 0 ? ReadSectors(writeWord, readRom)
                : Enumerable.Range(0, (image.Length + layoutSector - 1) / layoutSector).Select(i => i * layoutSector).ToArray();
            int last = -1;
            for (int index = 0; index < sectorAddresses.Length; index++)
            {
                if (sectorAddresses[index] >= image.Length - 1) break;
                last = index;
            }
            // 原版按高地址到低地址擦除文件覆盖的扇区。
            if (last >= 0) beginErase?.Invoke(last + 1);
            for (int index = last; index >= 0; index--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                uint address = (uint)sectorAddresses[index];
                progress?.Invoke(index + 1, last + 1, "erase");
                long eraseStart = startSectorErase?.Invoke() ?? 0;
                writeWord(0x555, 0xAA);
                writeWord(0x2AA, 0x55);
                writeWord(0x555, 0x80);
                writeWord(0x555, 0xAA);
                writeWord(0x2AA, 0x55);
                writeWord(address >> 1, 0x30);
                ushort status;
                do
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    status = BitConverter.ToUInt16(readRom(address, 2), 0);
                    delay(20);
                } while (status != 0xFFFF);
                int sectorEnd = layoutSector > 0 ? sectorAddresses[index] + layoutSector :
                    index + 1 < sectorAddresses.Length ? sectorAddresses[index + 1] : deviceBytes;
                finishSectorErase?.Invoke(eraseStart, sectorEnd - sectorAddresses[index]);
            }
            eraseTime += Stopwatch.GetElapsedTime(start);
        }

        beginProgram?.Invoke();
        long programStart = Stopwatch.GetTimestamp();
        for (int offset = 0; offset < image.Length; offset += 4096)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int length = Math.Min(4096, image.Length - offset);
            // 包含全 FF 数据也按原版发送，不另作空白包过滤。
            programRom((uint)offset, image.AsSpan(offset, length).ToArray(), (ushort)bufferBytes);
            progress?.Invoke(offset + length, image.Length, "write");
        }
        return (eraseTime, Stopwatch.GetElapsedTime(programStart));
    }

    private static (int BufferBytes, int DeviceBytes) ReadSize(
        Action<uint, ushort> writeWord, Func<uint, int, byte[]> readRom)
    {
        writeWord(0x55, 0x98);
        byte[] cfi = readRom(0x27 << 1, 20);
        writeWord(0, 0xF0);
        int capacityExponent = BitConverter.ToUInt16(cfi, 0);
        // 大于 int 范围的容量沿用盖中盖原版的转换结果。
        int capacity = capacityExponent < 31 ? 1 << capacityExponent : int.MinValue;
        int exponent = BitConverter.ToUInt16(cfi, 6);
        return (exponent == 0 ? 0 : (int)Math.Pow(2, exponent), capacity);
    }

    private static int[] ReadSectors(Action<uint, ushort> writeWord, Func<uint, int, byte[]> readRom)
    {
        writeWord(0x55, 0x98);
        int regions = BitConverter.ToInt16(readRom(0x2C << 1, 4), 0);
        byte[] value = readRom(0x2D << 1, 4);
        int count0 = ((value[2] << 8) | value[0]) + 1;
        value = readRom(0x2F << 1, 4);
        int size0 = ((value[2] << 8) | value[0]) * 256;
        value = readRom(0x31 << 1, 4);
        int count1 = ((value[2] << 8) | value[0]) + 1;
        value = readRom(0x33 << 1, 4);
        int size1 = ((value[2] << 8) | value[0]) * 256;
        int boot = BitConverter.ToInt16(readRom(0x4F << 1, 4), 0);
        writeWord(0, 0xF0);
        int[] addresses = new int[regions == 1 ? count0 : count0 + count1];
        if (addresses.Length > 65535) return addresses;
        if (boot == 2 && regions == 2)
        {
            for (int i = 0; i < count0; i++) addresses[i] = i * size0;
            for (int i = 0; i < count1; i++) addresses[count0 + i] = count0 * size0 + i * size1;
        }
        else if (boot == 3 && regions == 2)
        {
            for (int i = 0; i < count1; i++) addresses[i] = i * size1;
            for (int i = 0; i < count0; i++) addresses[count1 + i] = count1 * size1 + i * size0;
        }
        else
        {
            for (int i = 0; i < count0; i++) addresses[i] = i * size0;
        }
        return addresses;
    }
}
