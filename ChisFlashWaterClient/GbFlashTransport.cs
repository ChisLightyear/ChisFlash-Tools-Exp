using ClientText = ChisFlashWaterClient.Services.LocalizedText;
using System.Buffers.Binary;
using System.IO.Ports;
using System.Text;

namespace ChisFlashWaterClient;

// GBFlash LK 总线命令沿用 FlashGBX 和工作区已有的 GBFlash 后端。
internal sealed class GbFlashTransport(SerialPort port, Action<int>? transferObserver)
{
    private bool? gbMode;
    private int firmware;
    private bool supportsCartPower;
    private (bool Gb, ushort Page)? flashConfiguration;
    private uint? transferSize;
    private bool? gbWriteCsPulse;
    private (byte Command, uint Address)? nextRomTransfer;
    private const int ReadLimit = 4096;
    private const int WriteLimit = 2048;

    public void Open()
    {
        port.BaudRate = 2_000_000;
        port.ReadTimeout = 1000;
        port.Open();
        port.DiscardInBuffer();
        Send([0xA1]);
        if (port.ReadByte() != 8) throw new IOException(ClientText.T("client", "gbflash_lk_response", "GBFlash did not return the expected LK identification response."));
        byte[] identity = new byte[8];
        ReadExact(identity);
        firmware = BinaryPrimitives.ReadUInt16BigEndian(identity.AsSpan(1, 2));
        if (identity[0] != (byte)'L') throw new IOException(ClientText.T("client", "gbflash_lk_firmware", "The firmware on this port did not identify itself as FlashGBX LK."));
        if (firmware >= 12)
        {
            byte[] name = new byte[port.ReadByte()];
            ReadExact(name);
            byte[] capabilities = new byte[2];
            ReadExact(capabilities);
            supportsCartPower = (capabilities[0] & 1) != 0;
            if (!Encoding.UTF8.GetString(name).Trim('\0', ' ').Equals("GBFlash", StringComparison.OrdinalIgnoreCase))
                throw new IOException(ClientText.T("client", "gbflash_model", "The device on this port did not identify itself as GBFlash."));
        }
        else if (identity[3] is not (5 or 12 or 13))
        {
            throw new IOException(ClientText.T("client", "gbflash_board", "Could not identify the GBFlash board on this port."));
        }
        port.ReadTimeout = 5000;
    }

    private void Prepare(bool gb)
    {
        if (gbMode == gb) return;
        nextRomTransfer = null;
        transferSize = null;
        gbWriteCsPulse = null;
        Command([gb ? (byte)0xA3 : (byte)0xA2], firmware >= 12);
        Command([gb ? (byte)0xA5 : (byte)0xA4], firmware >= 12);
        Variable(1, gb ? 11u : 12u, gb ? 1u : 0u);
        Variable(1, 0, gb ? 1u : 2u);
        if (!gb && firmware >= 12) Variable(1, 16, 0);
        if (supportsCartPower)
        {
            Send([0xF4]);
            if (port.ReadByte() == 0)
            {
                Command([0xF2]);
                Command([gb ? (byte)0xB4 : (byte)0xC9]);
            }
        }
        gbMode = gb;
        flashConfiguration = null;
    }

    public void Read(bool gb, bool ram, uint address, byte[] destination)
    {
        Prepare(gb);
        bool readsGbaRom = !gb && !ram;
        int prefix = readsGbaRom ? (int)(address & 1) : 0;
        int length = destination.Length + prefix;
        if (readsGbaRom) length += length & 1;
        byte[] buffer = length == destination.Length ? destination : new byte[length];
        address -= (uint)prefix;
        byte opcode = gb ? (byte)0xB1 : ram ? (byte)0xC3 : (byte)0xC1;
        bool continuesRomRead = !ram && nextRomTransfer == (opcode, address);
        nextRomTransfer = null;
        if (!continuesRomRead)
            Variable(4, 0, gb || ram ? address : address >> 1);
        if (gb && !continuesRomRead)
        {
            Variable(1, 1, ram ? 3u : 1u);
            Variable(1, 8, ram ? 1u : 0u);
        }
        for (int offset = 0; offset < buffer.Length; offset += ReadLimit)
        {
            int count = Math.Min(ReadLimit, buffer.Length - offset);
            SetTransferSize(count);
            Send([opcode]);
            ReadExact(buffer, offset, count);
            transferObserver?.Invoke(count);
        }
        if (!ram)
            nextRomTransfer = (opcode, address + (uint)buffer.Length);
        if (buffer != destination) buffer.AsSpan(prefix, destination.Length).CopyTo(destination);
    }

    public void WriteBus(bool gb, uint address, byte[] data)
    {
        Prepare(gb);
        int unit = gb ? 1 : 2;
        if (data.Length % unit != 0 || address % unit != 0)
            throw new InvalidOperationException(ClientText.T("client", "gbflash_bus_alignment", "The GBFlash bus write address and length must be aligned."));
        nextRomTransfer = null;
        var command = new byte[5 + unit];
        command[0] = gb ? (byte)0xB2 : (byte)0xC2;
        for (int offset = 0; offset < data.Length; offset += unit)
        {
            BinaryPrimitives.WriteUInt32BigEndian(command.AsSpan(1), (address + (uint)offset) / (uint)unit);
            if (gb) command[5] = data[offset];
            else BinaryPrimitives.WriteUInt16BigEndian(command.AsSpan(5), BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset)));
            Command(command, firmware >= 12);
        }
    }

    public void WriteRam(bool gb, uint address, byte[] data, bool flashSave = false)
    {
        Prepare(gb);
        nextRomTransfer = null;
        if (gb) SetGbWriteCsPulse(true);
        WriteBlocks(gb ? (byte)0xB3 : flashSave ? (byte)0xC7 : (byte)0xC4, address, data, flashSave);
    }

    public void ProgramRom(bool gb, uint address, byte[] data, ushort bufferWriteBytes)
    {
        Prepare(gb);
        if (gb) SetGbWriteCsPulse(false);
        int unit = gb ? 1 : 2;
        if (address % unit != 0 || data.Length % unit != 0)
            throw new InvalidOperationException(ClientText.T("client", "gbflash_nor_alignment", "The GBFlash NOR programming address and length must be aligned."));
        ushort page = (ushort)Math.Min((int)bufferWriteBytes, WriteLimit);
        if (flashConfiguration != (gb, page))
        {
            nextRomTransfer = null;
            uint unlock1 = gb ? 0x0AAAu : 0x555u;
            uint unlock2 = gb ? 0x0555u : 0x2AAu;
            (uint Address, ushort Value)[] sequence = page == 0
                ? [(unlock1, 0xAA), (unlock2, 0x55), (unlock1, 0xA0), (0, 0), (0, 0), (0, 0)]
                : [(unlock1, 0xAA), (unlock2, 0x55), (0, 0x25), (0, 0), (0, 0), (0, 0x29)];
            byte[] command = new byte[4 + sequence.Length * 6];
            command[0] = 0xA7;
            command[1] = 1;
            command[2] = page == 0 ? (byte)1 : (byte)2;
            command[3] = 1;
            for (int i = 0; i < sequence.Length; i++)
            {
                BinaryPrimitives.WriteUInt32BigEndian(command.AsSpan(4 + i * 6), sequence[i].Address);
                BinaryPrimitives.WriteUInt16BigEndian(command.AsSpan(8 + i * 6), sequence[i].Value);
            }
            Command(command, firmware >= 12);
            Variable(2, 1, page);
            flashConfiguration = (gb, page);
        }
        const byte opcode = 0xD3;
        bool continuesRomWrite = nextRomTransfer == (opcode, address);
        nextRomTransfer = null;
        WriteBlocks(opcode, address / (uint)unit, data, setAddress: !continuesRomWrite);
        nextRomTransfer = (opcode, address + (uint)data.Length);
    }

    private void WriteBlocks(byte opcode, uint address, byte[] data, bool flashSave = false, bool setAddress = true)
    {
        if (setAddress) Variable(4, 0, address);
        int prefix = flashSave ? 2 : 1;
        for (int offset = 0; offset < data.Length; offset += WriteLimit)
        {
            int count = Math.Min(WriteLimit, data.Length - offset);
            SetTransferSize(count);
            byte[] command = new byte[prefix + count];
            command[0] = opcode;
            if (flashSave) command[1] = 1;
            data.AsSpan(offset, count).CopyTo(command.AsSpan(prefix));
            Command(command);
            transferObserver?.Invoke(count);
        }
    }

    // LK 固件保留传输长度，并在连续 ROM 读写时推进地址。
    private void SetTransferSize(int count)
    {
        if (transferSize == count) return;
        Variable(2, 0, (uint)count);
        transferSize = (uint)count;
    }

    private void SetGbWriteCsPulse(bool enabled)
    {
        if (gbWriteCsPulse == enabled) return;
        Variable(1, 9, enabled ? 1u : 0u);
        gbWriteCsPulse = enabled;
    }

    private void Variable(byte size, uint identifier, uint value)
    {
        byte[] command = [0xA6, size, 0, 0, 0, 0, 0, 0, 0, 0];
        BinaryPrimitives.WriteUInt32BigEndian(command.AsSpan(2), identifier);
        BinaryPrimitives.WriteUInt32BigEndian(command.AsSpan(6), value);
        Command(command, firmware >= 12);
    }

    private void Send(byte[] command) => port.Write(command, 0, command.Length);

    private void Command(byte[] command, bool ack = true)
    {
        Send(command);
        if (ack && port.ReadByte() is int response && response != 1)
            throw new IOException(ClientText.Tf("client", "gbflash_ack", "GBFlash command 0x{0:X2} returned ACK 0x{1:X2}.", command[0], response));
    }

    private void ReadExact(byte[] data) => ReadExact(data, 0, data.Length);

    private void ReadExact(byte[] data, int offset, int count)
    {
        int end = offset + count;
        while (offset < end) offset += port.Read(data, offset, end - offset);
    }
}
