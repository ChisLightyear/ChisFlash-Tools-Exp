using System;
using System.Diagnostics;
using System.IO.Ports;
using System.Threading;

namespace ChisFlashWaterClient;

public sealed class WaterDeviceProtocol : IDisposable
{
    public const byte AckOk = 0xAA;

    private readonly SerialPort port;
    private readonly Action<int>? transferObserver;
    public byte? LastAckByte { get; private set; }
    public bool LastAckTimedOut { get; private set; }
    private bool lastReadTimedOut;

    public WaterDeviceProtocol(string portName, Action<int>? transferObserver = null)
    {
        this.transferObserver = transferObserver;
        port = new SerialPort
        {
            BaudRate = 9600,
            DataBits = 8,
            Parity = Parity.None,
            StopBits = StopBits.One,
            PortName = portName,
        };
    }

    public void Open()
    {
        port.Open();
        port.RtsEnable = true;
        port.DtrEnable = true;
        port.RtsEnable = false;
        port.DtrEnable = false;
        DiscardInputSafely();
    }

    public void Dispose()
    {
        if (port.IsOpen)
            port.Close();

        port.Dispose();
    }

    public bool TryReadLegacyNorId(out byte[] id, int timeoutMs = Timeout.Infinite)
    {
        SendPackage([0xF0]);
        id = new byte[8];
        return TryReadPayload(id, timeoutMs);
    }

    public bool TryReadLocalMagicId(out byte[] payload)
    {
        byte[] ignored = new byte[1];
        payload = new byte[8];
        foreach (uint address in new uint[] { 0xFFF0, 0xFFE0, 0xFFD0 })
        {
            if (!TryReadRam(address, ignored, 3000)) return false;
        }
        return TryReadRam(0xFFF0, payload, 3000);
    }

    public bool TryReadExtendedId(out byte[] payload)
    {
        SendPackage([0xA4]);
        payload = new byte[32];
        return TryReadPayload(payload, 1500);
    }

    public bool TryReadSysInfo(out WaterSysInfo info, int timeoutMs = 1500)
    {
        SendPackage([0xA2]);
        byte[] payload = new byte[32];
        if (!TryReadPayload(payload, timeoutMs))
        {
            info = default;
            return false;
        }

        info = new WaterSysInfo(
            BitConverter.ToUInt32(payload, 0),
            BitConverter.ToUInt32(payload, 4),
            BitConverter.ToUInt32(payload, 8),
            BitConverter.ToUInt32(payload, 12),
            BitConverter.ToUInt32(payload, 16),
            BitConverter.ToUInt32(payload, 20),
            BitConverter.ToUInt32(payload, 24),
            payload[28],
            payload[29] != 0,
            payload[30],
            payload[31]);
        return true;
    }

    public bool TryReadSaveSlotSummary(out byte[] payload, int timeoutMs = 3000)
    {
        SendPackage([0xA5]);
        payload = new byte[4 + 1 + WaterSaveSlotSummaryInfo.SlotCount];
        return TryReadPayload(payload, timeoutMs);
    }

    public bool TryReadSaveSlotSummaryFiltered(IReadOnlyCollection<byte> gameSlots, out byte[] payload, int timeoutMs = 3000)
    {
        byte[] slots = gameSlots
            .Where(slot => slot < WaterSaveSlotSummaryInfo.SlotCount)
            .Take(WaterSaveSlotSummaryInfo.SlotCount)
            .ToArray();
        byte[] pack = new byte[1 + 1 + slots.Length];
        pack[0] = 0xA6;
        pack[1] = (byte)slots.Length;
        Array.Copy(slots, 0, pack, 2, slots.Length);

        SendPackage(pack);
        payload = new byte[4 + 1 + (slots.Length * 2)];
        return TryReadPayload(payload, timeoutMs);
    }

    public bool TryReadSaveSlotWindow(byte gameSlot, uint slotOffset, byte[] buffer, int timeoutMs = 3000)
    {
        byte[] pack = new byte[1 + 1 + 4 + 2];
        pack[0] = 0xA7;
        pack[1] = gameSlot;
        Array.Copy(BitConverter.GetBytes(slotOffset), 0, pack, 2, 4);
        Array.Copy(BitConverter.GetBytes((ushort)buffer.Length), 0, pack, 6, 2);

        SendPackage(pack);
        return TryReadPayload(buffer, timeoutMs);
    }

    public bool TryReadSpiFlash(uint byteAddress, byte[] buffer, int timeoutMs = Timeout.Infinite)
    {
        byte[] pack = new byte[1 + 4 + 2];
        pack[0] = 0xA8;
        Array.Copy(BitConverter.GetBytes(byteAddress), 0, pack, 1, 4);
        Array.Copy(BitConverter.GetBytes((ushort)buffer.Length), 0, pack, 5, 2);

        SendPackage(pack);
        return TryReadPayload(buffer, timeoutMs);
    }

    public void SetCartPower(byte mode)
    {
        SendPackage([0xA0, mode]);
        PulseControlSignals(tailDelayMs: 10);
    }

    public void SetCartPhiDiv(byte divider)
    {
        SendPackage([0xA1, (byte)(divider & 0x7F)]);
        PulseControlSignals(tailDelayMs: 0);
    }

    public bool TryReadSpiFlashRange(uint byteAddress, byte[] buffer, int chunkSize = 4096, int timeoutMsPerChunk = Timeout.Infinite)
    {
        if (buffer.Length == 0)
            return true;

        chunkSize = Math.Clamp(chunkSize, 1, ushort.MaxValue);

        for (int offset = 0; offset < buffer.Length; offset += chunkSize)
        {
            int readLength = Math.Min(chunkSize, buffer.Length - offset);
            byte[] chunk = new byte[readLength];

            if (!TryReadSpiFlash(byteAddress + (uint)offset, chunk, timeoutMsPerChunk))
                return false;

            Buffer.BlockCopy(chunk, 0, buffer, offset, readLength);
        }

        return true;
    }

    public bool TryEraseSpiFlashBlock(uint byteAddress, int timeoutMs = 60000)
    {
        byte[] pack = new byte[1 + 4];
        pack[0] = 0xA9;
        Array.Copy(BitConverter.GetBytes(byteAddress), 0, pack, 1, 4);

        SendPackage(pack);
        return TryReadAck(timeoutMs);
    }

    public bool TryWriteSpiFlash(uint byteAddress, byte[] bytes, int timeoutMs = 60000)
    {
        byte[] pack = new byte[1 + 4 + bytes.Length];
        pack[0] = 0xAA;
        Array.Copy(BitConverter.GetBytes(byteAddress), 0, pack, 1, 4);
        Array.Copy(bytes, 0, pack, 5, bytes.Length);

        SendPackage(pack);
        return TryReadAck(timeoutMs);
    }

    public bool TryWriteRom(uint wordAddress, byte[] bytes, int timeoutMs = Timeout.Infinite)
    {
        byte[] pack = new byte[1 + 4 + bytes.Length];
        pack[0] = 0xF5;
        Array.Copy(BitConverter.GetBytes(wordAddress), 0, pack, 1, 4);
        Array.Copy(bytes, 0, pack, 5, bytes.Length);

        SendPackage(pack);
        return TryReadAck(timeoutMs);
    }

    public bool TryReadRom(uint byteAddress, byte[] buffer, int timeoutMs = Timeout.Infinite)
    {
        byte[] pack = new byte[1 + 4 + 2];
        pack[0] = 0xF6;
        Array.Copy(BitConverter.GetBytes(byteAddress), 0, pack, 1, 4);
        Array.Copy(BitConverter.GetBytes((ushort)buffer.Length), 0, pack, 5, 2);

        SendPackage(pack);
        return TryReadPayload(buffer, timeoutMs);
    }

    public bool TryReadRomRange(uint byteAddress, byte[] buffer, int chunkSize = 4096, int timeoutMsPerChunk = Timeout.Infinite)
    {
        if (buffer.Length == 0)
            return true;

        chunkSize = Math.Clamp(chunkSize, 1, ushort.MaxValue);

        for (int offset = 0; offset < buffer.Length; offset += chunkSize)
        {
            int readLength = Math.Min(chunkSize, buffer.Length - offset);
            byte[] chunk = new byte[readLength];

            if (!TryReadRom(byteAddress + (uint)offset, chunk, timeoutMsPerChunk))
                return false;

            Buffer.BlockCopy(chunk, 0, buffer, offset, readLength);
        }

        return true;
    }

    public bool TryEraseLegacyRomChip(int timeoutMs = 3000)
    {
        SendPackage([0xF1]);
        return TryReadAck(timeoutMs);
    }

    /// <summary>
    /// 0xAD —— 请求固件在单个片选周期内完成一整条 NOR 擦除命令序列。
    /// payload 由 (uint16 字偏移, uint16 数据) 对组成，按顺序写入同一个 CS 周期。
    ///
    /// 存在的意义：旧路径由上位机逐字下发每条命令，每条都是一次独立的
    /// USB 往返 + 独立 CS 周期，是 USB 直插擦除电流偏高的主因之一。
    ///
    /// 命令码选择：0xF7 已被 CMD_RAM_WRITE 占用（盖中盖分桶切换在用），
    /// 不能复用；0xAD 在固件 cmd_code_t 与上位机协议里均未被占用。
    /// </summary>
    public bool TryWriteEraseSequence(uint wordAddress, byte[] payload, int timeoutMs = 60000)
    {
        byte[] pack = new byte[1 + 4 + payload.Length];
        pack[0] = 0xAD;
        Array.Copy(BitConverter.GetBytes(wordAddress), 0, pack, 1, 4);
        Array.Copy(payload, 0, pack, 5, payload.Length);

        SendPackage(pack);
        return TryReadAck(timeoutMs);
    }

    [Obsolete("Firmware-side 0xF3 erase is unsafe. Use the host-controlled per-sector passthrough erase sequence.", error: true)]
    public bool TryEraseRomSector(uint byteAddress, int timeoutMs = 60000)
    {
        throw new NotSupportedException(
            "Firmware-side 0xF3 sector erase is disabled. Use the host-controlled per-sector passthrough erase sequence.");
    }

    public bool TryProgramRom(uint byteAddress, byte[] bytes, ushort bufferWriteBytes, int timeoutMs = 60000)
    {
        byte[] pack = new byte[1 + 4 + 2 + bytes.Length];
        pack[0] = 0xF4;
        Array.Copy(BitConverter.GetBytes(byteAddress), 0, pack, 1, 4);
        Array.Copy(BitConverter.GetBytes(bufferWriteBytes), 0, pack, 5, 2);
        Array.Copy(bytes, 0, pack, 7, bytes.Length);

        SendPackage(pack);
        return TryReadAck(timeoutMs);
    }

    public bool TryWriteRam(uint byteAddress, byte[] bytes, int timeoutMs = Timeout.Infinite)
    {
        byte[] pack = new byte[1 + 4 + bytes.Length];
        pack[0] = 0xF7;
        Array.Copy(BitConverter.GetBytes(byteAddress), 0, pack, 1, 4);
        Array.Copy(bytes, 0, pack, 5, bytes.Length);

        SendPackage(pack);
        return TryReadAck(timeoutMs);
    }

    public bool TryReadRam(uint byteAddress, byte[] buffer, int timeoutMs = Timeout.Infinite)
    {
        byte[] pack = new byte[1 + 4 + 2];
        pack[0] = 0xF8;
        Array.Copy(BitConverter.GetBytes(byteAddress), 0, pack, 1, 4);
        Array.Copy(BitConverter.GetBytes((ushort)buffer.Length), 0, pack, 5, 2);

        SendPackage(pack);
        return TryReadPayload(buffer, timeoutMs);
    }

    public bool TryReadRamRange(uint byteAddress, byte[] buffer, int chunkSize = 4096, int timeoutMsPerChunk = Timeout.Infinite)
    {
        if (buffer.Length == 0)
            return true;

        chunkSize = Math.Clamp(chunkSize, 1, ushort.MaxValue);

        for (int offset = 0; offset < buffer.Length; offset += chunkSize)
        {
            int readLength = Math.Min(chunkSize, buffer.Length - offset);
            byte[] chunk = new byte[readLength];

            if (!TryReadRam(byteAddress + (uint)offset, chunk, timeoutMsPerChunk))
                return false;

            Buffer.BlockCopy(chunk, 0, buffer, offset, readLength);
        }

        return true;
    }

    public bool TryWriteGbc(uint byteAddress, byte[] bytes, int timeoutMs = Timeout.Infinite)
    {
        byte[] pack = new byte[1 + 4 + bytes.Length];
        pack[0] = 0xFA;
        Array.Copy(BitConverter.GetBytes(byteAddress), 0, pack, 1, 4);
        Array.Copy(bytes, 0, pack, 5, bytes.Length);

        SendPackage(pack);
        return TryReadAck(timeoutMs);
    }

    public bool TryReadGbc(uint byteAddress, byte[] buffer, int timeoutMs = Timeout.Infinite)
    {
        byte[] pack = new byte[1 + 4 + 2];
        pack[0] = 0xFB;
        Array.Copy(BitConverter.GetBytes(byteAddress), 0, pack, 1, 4);
        Array.Copy(BitConverter.GetBytes((ushort)buffer.Length), 0, pack, 5, 2);

        SendPackage(pack);
        return TryReadPayload(buffer, timeoutMs);
    }

    public bool TryProgramGbcRom(uint byteAddress, byte[] bytes, ushort bufferWriteBytes, int timeoutMs = 60000)
    {
        byte[] pack = new byte[1 + 4 + 2 + bytes.Length];
        pack[0] = 0xFC;
        Array.Copy(BitConverter.GetBytes(byteAddress), 0, pack, 1, 4);
        Array.Copy(BitConverter.GetBytes(bufferWriteBytes), 0, pack, 5, 2);
        Array.Copy(bytes, 0, pack, 7, bytes.Length);

        SendPackage(pack);
        return TryReadAck(timeoutMs);
    }

    private void SendPackage(byte[] bytes)
    {
        lastReadTimedOut = false;
        DiscardInputSafely();
        int size = 2 + bytes.Length + 2;
        byte[] buffer = new byte[size];

        Array.Copy(BitConverter.GetBytes((ushort)size), 0, buffer, 0, 2);
        Array.Copy(bytes, 0, buffer, 2, bytes.Length);

        port.Write(buffer, 0, size);
        transferObserver?.Invoke(bytes.Length);
    }

    private void PulseControlSignals(int tailDelayMs)
    {
        Thread.Sleep(10);
        port.RtsEnable = true;
        port.DtrEnable = true;
        port.RtsEnable = false;
        port.DtrEnable = false;
        if (tailDelayMs > 0)
        {
            Thread.Sleep(tailDelayMs);
        }
    }

    private bool TryReadAck(int timeoutMs)
    {
        LastAckByte = null;
        LastAckTimedOut = false;
        Stopwatch stopwatch = Stopwatch.StartNew();

        while (port.BytesToRead == 0)
        {
            if (timeoutMs >= 0 && stopwatch.ElapsedMilliseconds >= timeoutMs)
            {
                LastAckTimedOut = true;
                DiscardInputSafely();
                return false;
            }
        }

        byte[] ack = new byte[1];
        port.Read(ack, 0, 1);
        LastAckByte = ack[0];
        DiscardInputSafely();

        return ack[0] == AckOk;
    }

    public string DescribeLastAckFailure()
    {
        if (lastReadTimedOut) return "Read timeout";
        if (LastAckTimedOut)
        {
            return "ACK timeout";
        }

        if (!LastAckByte.HasValue)
        {
            return "ACK failed";
        }

        return $"unexpected ACK byte 0x{LastAckByte.Value:X2}";
    }

    private bool TryReadPayload(byte[] payload, int timeoutMs)
    {
        int expectedCount = payload.Length + 2;
        byte[] buffer = new byte[expectedCount];
        Stopwatch stopwatch = Stopwatch.StartNew();
        int receivedCount = 0;

        while (receivedCount < expectedCount)
        {
            int bytesToRead = port.BytesToRead;
            if (bytesToRead > 0)
            {
                if (bytesToRead + receivedCount > expectedCount)
                    bytesToRead = expectedCount - receivedCount;

                port.Read(buffer, receivedCount, bytesToRead);
                receivedCount += bytesToRead;
                stopwatch.Restart();
            }
            else
            {
                if (timeoutMs >= 0 && stopwatch.ElapsedMilliseconds >= timeoutMs)
                {
                    lastReadTimedOut = true;
                    DiscardInputSafely();
                    return false;
                }
            }
        }

        Array.Copy(buffer, 2, payload, 0, payload.Length);
        transferObserver?.Invoke(payload.Length);
        return true;
    }

    private void DiscardInputSafely()
    {
        try
        {
            port.DiscardInBuffer();
        }
        catch
        {
        }
    }
}
