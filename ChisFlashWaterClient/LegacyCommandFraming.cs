using System.Buffers.Binary;

namespace ChisFlashWaterClient;

public static class LegacyCommandFraming
{
    public static IReadOnlyList<byte[]> BuildFrames(byte[] command, bool carbonateFlashGbx)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Length == 0) throw new ArgumentException("命令不能为空。", nameof(command));
        if (!carbonateFlashGbx || SafeLead(command.Length + 4)) return [Encode(command)];

        byte opcode = command[0];
        bool program = opcode is 0xF4 or 0xFC;
        bool wordAddress = opcode == 0xF5;
        bool gbaWords = opcode is 0xF4 or 0xF5;
        if (opcode is not (0xF4 or 0xF5 or 0xF7 or 0xFA or 0xFC))
            throw new NotSupportedException("碳酸钙兼容模式不支持此长度的命令。");
        int header = program ? 7 : 5;
        if (command.Length <= header) throw new ArgumentException("写入命令缺少数据。", nameof(command));
        int remaining = command.Length - header;
        int unit = gbaWords ? 2 : 1;
        if (remaining % unit != 0) throw new ArgumentException("GBA写入数据须按16位对齐。", nameof(command));
        uint address = BinaryPrimitives.ReadUInt32LittleEndian(command.AsSpan(1, 4));
        int buffer = opcode == 0xF4 ? BinaryPrimitives.ReadUInt16LittleEndian(command.AsSpan(5, 2)) : 0;
        int offset = 0;
        var result = new List<byte[]>();
        while (remaining > 0)
        {
            int count = Math.Min(remaining, 4096);
            int alignment = Math.Max(unit, buffer);
            // 优先保持NOR缓冲页边界；尾段不足一页时只在该页内部拆分。
            if (buffer > 0 && (address + (uint)offset) % (uint)buffer != 0)
                count = Math.Min(count, buffer - (int)((address + (uint)offset) % (uint)buffer));
            int aligned = count / alignment * alignment;
            while (aligned > 0 && !SafeLead(aligned + header + 4)) aligned -= alignment;
            if (aligned > 0) count = aligned;
            else
            {
                count = Math.Min(count, alignment);
                count -= count % unit;
                while (count > 0 && !SafeLead(count + header + 4)) count -= unit;
            }
            if (count <= 0) throw new InvalidOperationException("无法生成碳酸钙兼容分包。");
            byte[] part = new byte[header + count];
            command.AsSpan(0, header).CopyTo(part);
            uint partAddress = checked(address + (uint)(wordAddress ? offset / 2 : offset));
            BinaryPrimitives.WriteUInt32LittleEndian(part.AsSpan(1, 4), partAddress);
            command.AsSpan(header + offset, count).CopyTo(part.AsSpan(header));
            result.Add(Encode(part));
            offset += count;
            remaining -= count;
        }
        return result;
    }

    private static bool SafeLead(int frameLength) => (frameLength & 0xFF) < 0x44;

    private static byte[] Encode(byte[] command)
    {
        int size = checked(command.Length + 4);
        if (size > ushort.MaxValue) throw new ArgumentException("命令长度超出协议范围。", nameof(command));
        byte[] frame = new byte[size];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, (ushort)size);
        command.CopyTo(frame, 2);
        return frame;
    }
}
