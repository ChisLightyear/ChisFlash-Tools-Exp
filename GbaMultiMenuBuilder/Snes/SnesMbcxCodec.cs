using System.Buffers.Binary;
using GbaMultiMenuBuilder.Fc;

namespace GbaMultiMenuBuilder.Snes;

public static class SnesMbcxCodec
{
    public const int ConfigBytes = 32;
    public const string CoreNotice = "MIX v2 配套核心待同步；可合卡不等于可运行。Requires the paired MIX v2 core; packing is not gameplay compatibility.";

    private static bool Same(SnesRomInfo expected, SnesRomInfo actual) =>
        expected.HasValidHeader && actual.HasValidHeader && expected.Mapping == actual.Mapping &&
        expected.HeaderOffset == actual.HeaderOffset && expected.RomBytes == actual.RomBytes &&
        expected.DeclaredRomBytes == actual.DeclaredRomBytes && expected.RamBytes == actual.RamBytes &&
        expected.MapMode == actual.MapMode && expected.CartridgeType == actual.CartridgeType &&
        expected.Country == actual.Country && expected.CoprocessorSubtype == actual.CoprocessorSubtype &&
        expected.ChecksumPairValid == actual.ChecksumPairValid;

    public static void ValidateGame(FcGame game)
    {
        var snes = game.Snes;
        if (snes is null || !snes.HasValidHeader || game.Md is not null || game.RomBytes != snes.RomBytes ||
            game.Info.StoredRamBytes != snes.RamBytes || game.Info.Battery != snes.Battery ||
            game.HasRomData && !Same(snes, SnesRomHeaderService.Parse(game.Data)))
            throw new InvalidDataException("SNES ROM does not match its MIX v2 configuration.");
    }

    public static byte[] BuildConfig(FcPlacement placement)
    {
        ValidateGame(placement.Game);
        var snes = placement.Game.Snes!;
        byte[] config = new byte[ConfigBytes];
        void Word(int offset, int value) => BinaryPrimitives.WriteUInt32LittleEndian(config.AsSpan(offset), checked((uint)value));
        Word(0, placement.Offset); Word(4, snes.RomBytes); Word(8, snes.RamBytes); Word(12, snes.HeaderOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(config.AsSpan(16), checked((ulong)snes.DeclaredRomBytes));
        config[24] = (byte)snes.Mapping; config[25] = snes.MapMode; config[26] = snes.CartridgeType;
        config[27] = snes.Country; config[28] = snes.CoprocessorSubtype ?? 0;
        config[29] = (byte)((snes.Battery ? 1 : 0) | (snes.ChecksumPairValid ? 2 : 0) | (snes.CoprocessorSubtype.HasValue ? 4 : 0));
        return config;
    }

    internal static FcGame ReadDirectoryGame(ReadOnlySpan<byte> config, string name, int length, int offset,
        int allocated, byte[] bankMap, string identity)
    {
        if (config.Length != ConfigBytes) throw new InvalidDataException("Invalid SNES launch configuration length.");
        int romOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(config));
        int bytes = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(config[4..]));
        int ram = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(config[8..]));
        int header = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(config[12..]));
        ulong declared = BinaryPrimitives.ReadUInt64LittleEndian(config[16..]);
        var mapping = (SnesMapping)config[24];
        int expectedHeader = mapping switch { SnesMapping.LoRom => 0x7FC0, SnesMapping.HiRom => 0xFFC0,
            SnesMapping.ExLoRom => 0x407FC0, SnesMapping.ExHiRom => 0x40FFC0, _ => -1 };
        if (romOffset != offset || bytes != length || bytes < 32768 || bytes > SnesRomHeaderService.MaxRomBytes ||
            bytes % 32768 != 0 || expectedHeader < 0 || header != expectedHeader || header > length - 64 ||
            ram < 0 || ram > 60 * 8192 || ram != 0 && (ram < 1024 || (ram & (ram - 1)) != 0) ||
            declared > long.MaxValue || declared < 1024 || (declared & (declared - 1)) != 0 ||
            (config[29] & ~7) != 0 || (config[29] & 4) == 0 && config[28] != 0 || config[30] != 0 || config[31] != 0)
            throw new InvalidDataException("Invalid SNES MIX v2 configuration.");
        var snes = new SnesRomInfo(name, mapping, header, length, (long)declared, ram, config[25],
            config[26], config[27], false, (config[29] & 2) != 0, null, null)
            { CoprocessorSubtype = (config[29] & 4) == 0 ? null : config[28] };
        if (((config[29] & 1) != 0) != snes.Battery) throw new InvalidDataException("SNES battery flag mismatch.");
        var info = new NesRomInfo(-1, 0, length, 0, snes.Battery ? 0 : ram, snes.Battery ? ram : 0,
            0, 0, snes.Battery, false, 0, $"SNES card · {snes.ChipDisplay}", null) { Warning = CoreNotice };
        var game = new FcGame { Name = name, Snes = snes, Info = info, OriginalRomBytes = length,
            OriginalOffset = offset, OriginalAllocated = allocated, OriginalSaveMap = bankMap,
            OriginalBattery = snes.Battery, OriginalStoredRamBytes = ram, OriginalConfig = config.ToArray(),
            DirectoryIdentity = identity };
        ValidateGame(game);
        return game;
    }
}
