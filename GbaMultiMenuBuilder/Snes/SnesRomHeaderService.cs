using System.Buffers.Binary;
using System.Text;

namespace GbaMultiMenuBuilder.Snes;

public enum SnesMapping { Unknown, LoRom, HiRom, ExLoRom, ExHiRom }

public sealed record SnesRomInfo(string Title, SnesMapping Mapping, int HeaderOffset, int RomBytes,
    long DeclaredRomBytes, int RamBytes, byte MapMode, byte CartridgeType, byte Country,
    bool HasCopierHeader, bool ChecksumPairValid, string? Error, string? Warning)
{
    public bool HasValidHeader => Error is null;
    public bool FastRom => (MapMode & 0x10) != 0;
    public byte? CoprocessorSubtype { get; init; }
    public bool Battery => (CartridgeType & 15) is 2 or 5 or 6 or 9 or 10;
    public string MappingDisplay => Mapping switch
    {
        SnesMapping.LoRom => "LoROM", SnesMapping.HiRom => "HiROM",
        SnesMapping.ExLoRom => "ExLoROM", SnesMapping.ExHiRom => "ExHiROM", _ => "Unknown",
    };
    public string RegionDisplay => Country is 0 or 1 or 13 or 15 ? "NTSC" : Country is >= 2 and <= 12 ? "PAL" : "Unknown";
    public string ChipDisplay => CartridgeType is 0 or 1 or 2 ? "Standard" : (CartridgeType & 15) < 3
        ? $"Special type 0x{CartridgeType:X2} (chip unconfirmed)" : (CartridgeType >> 4) switch
    {
        0 => "DSP (variant unconfirmed)", 1 => "Super FX", 2 => "OBC1", 3 => "SA-1",
        4 => "S-DD1", 5 => "S-RTC", 14 when CartridgeType == 0xE3 => "Super Game Boy",
        14 when CartridgeType == 0xE5 => "Satellaview",
        15 => CoprocessorSubtype switch
        {
            0 => (CartridgeType & 15) == 9 ? "SPC7110 + RTC" : "SPC7110",
            1 => "ST010 / ST011 (variant unconfirmed)", 2 => "ST018", 0x10 => "CX4",
            _ => $"Special type 0x{CartridgeType:X2} (subtype unconfirmed)",
        },
        _ => $"Special type 0x{CartridgeType:X2}",
    };
}

public static class SnesRomHeaderService
{
    public const int MaxRomBytes = 8 * 1024 * 1024;

    public static SnesRomInfo Parse(ReadOnlySpan<byte> data, long? sourceBytes = null)
    {
        long bytes = sourceBytes ?? data.Length;
        bool copier = bytes % 1024 == 512;
        int skip = copier ? 512 : 0;
        int romBytes = bytes - skip is >= 0 and <= MaxRomBytes ? (int)(bytes - skip) : 0;
        SnesRomInfo Failure(string reason) => new("", SnesMapping.Unknown, -1, romBytes, 0, 0, 0, 0, 0,
            copier, false, reason, null);
        if (bytes < 32768 || bytes > MaxRomBytes + 512L || romBytes < 32768 || romBytes % 32768 != 0)
            return Failure("SNES ROM须为32K对齐、32K～8M；不自动处理交错或压缩格式。Require a 32 KiB-aligned SNES ROM, 32 KiB–8 MiB; interleaved/compressed formats are not normalized.");
        var candidates = new List<(int Score, SnesRomInfo Info)>();
        foreach (var candidate in new[] { (0x7FC0, SnesMapping.LoRom), (0xFFC0, SnesMapping.HiRom),
                     (0x407FC0, SnesMapping.ExLoRom), (0x40FFC0, SnesMapping.ExHiRom) })
        {
            int address = candidate.Item1 + skip;
            if (address > data.Length - 64 || candidate.Item1 > romBytes - 64) continue;
            ReadOnlySpan<byte> header = data.Slice(address, 64);
            byte mode = header[21], type = header[22], romExponent = header[23], ramExponent = header[24], region = header[25];
            int baseMode = mode & 0xEF;
            bool mappingMatches = candidate.Item2 switch
            {
                SnesMapping.LoRom or SnesMapping.ExLoRom => baseMode is 0x20 or 0x22 or 0x23 or 0x2A,
                SnesMapping.HiRom => baseMode == 0x21,
                _ => baseMode == 0x25,
            };
            ushort reset = BinaryPrimitives.ReadUInt16LittleEndian(header[60..]);
            if (!mappingMatches || reset < 0x8000 || romExponent is < 5 or > 13 || ramExponent > 9) continue;
            ushort complement = BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
            ushort checksum = BinaryPrimitives.ReadUInt16LittleEndian(header[30..]);
            bool pair = checksum != 0 && complement != 0 && (checksum ^ complement) == 0xFFFF;
            int printable = 0;
            foreach (byte character in header[..21]) if (character is >= 32 and < 127) printable++;
            int score = 16 + (pair ? 8 : 0) + printable / 7;
            int resetOffset = candidate.Item2 is SnesMapping.HiRom or SnesMapping.ExHiRom
                ? (candidate.Item1 & ~0xFFFF) + reset : (candidate.Item1 & ~0x7FFF) + (reset & 0x7FFF);
            if (resetOffset + skip < data.Length)
            {
                byte opcode = data[resetOffset + skip];
                if (opcode is 0x78 or 0x18 or 0x38 or 0x9C or 0x4C or 0x5C) score += 4;
                else if (opcode is 0 or 0xFF or 0xCC) score -= 8;
            }
            if (score < 16) continue;
            long declared = 1024L << romExponent;
            int ram = ramExponent == 0 ? 0 : 1024 << ramExponent;
            string title = Encoding.ASCII.GetString(header[..21]).TrimEnd(' ', '\0');
            string? warning = !pair ? "头部校验对异常；未验证完整ROM校验和。Header checksum pair invalid; full ROM checksum is not verified." : null;
            if (declared != romBytes) warning = (warning is null ? "" : warning + " ") + "ROM实际长度与头部容量不一致。ROM length differs from its header size.";
            var info = new SnesRomInfo(title, candidate.Item2, candidate.Item1, romBytes, declared, ram, mode, type, region,
                copier, pair, null, warning) { CoprocessorSubtype = header[26] == 0x33 ? data[address - 1] : null };
            candidates.Add((score, info));
        }
        if (candidates.Count == 0) return Failure("未找到可信SNES映射／复位头；请核对文件或交错格式。No credible SNES mapping/reset header; check the file or interleaving.");
        var ranked = candidates.OrderByDescending(item => item.Score).ToArray();
        if (ranked.Length > 1 && ranked[0].Score == ranked[1].Score && ranked[0].Info.Mapping != ranked[1].Info.Mapping)
            return Failure("SNES映射头存在歧义，不自动选择。Ambiguous SNES mapping headers; no automatic selection.");
        return ranked[0].Info;
    }
}
