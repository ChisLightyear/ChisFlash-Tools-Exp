namespace ChisFlashWaterClient.Services;

/// <summary>根据实际读写函数识别常见 SRAM 转换，不以残留 SDK 标签代替代码证据。</summary>
public static class GbaSaveCodeAnalyzer
{
    private const int LibraryWindow = 0x20000;
    private static readonly byte[] EepromSramRead = Convert.FromHexString("00040A1C400BE021090541180731002308781070013301320139072BF8D9002070BC02BC0847");
    private static readonly byte[] EepromSramWrite = Convert.FromHexString("70B500040A1C400BE021090541180731002310780870013301320139072BF8D9002070BC02BC0847");
    private static readonly byte[] FlashSramRead = Convert.FromHexString("70B5A0B000034018E0210905091808781070013B01320131002BF8D1002020B070BC02BC0847");
    private static readonly byte[] FlashSramWrite = Convert.FromHexString("7CB590B000030A1CE0210905091801231B0310780870013B01320131002BF8D1002010B07CBC02BC0847");
    private static readonly byte[] FlashOldSramWrite = Convert.FromHexString("70B500030A1CE0210905091801231B0310780870013B01320131002BF8D1002070BC02BC0847");
    private static readonly byte[] ReproBank = Convert.FromHexString("054B80210902092212069F44902109050000000008707047");
    private static readonly byte[] ReproProgram = Convert.FromHexString("0A4CAA220000094B55220000A022000002780A70");
    private static readonly byte[] ReproSector102 = Convert.FromHexString("1349FF25082200005202013AA554FCD100000000000000000000");
    private static readonly byte[] ReproSector103 = Convert.FromHexString("1449FF25082200005202013AA554FCD100000000000000000000");
    private static readonly byte[] FlashBank = Convert.FromHexString("054BAA211970054A55211170B0211970E021090508707047");
    private static readonly byte[] FlashId = Convert.FromHexString("064AAA20107005495520087090201070");
    private static readonly byte[] NativeProgram1 = Convert.FromHexString("0A4CAA222270094B55221A70A022227002780A70");
    private static readonly byte[] NativeProgram2 = Convert.FromHexString("0C4AAA2010700B4955200870A020107027700948");
    private static readonly byte[] LegacyBank = Convert.FromHexString("10B5FFB40C4C084B0C492380074B084A0B800B4B00041A80400B043B1880054B0B802380FFBC0DE04A5900004B4F000001000000000000000000FE090400FE092400FE0910BD");
    private static readonly byte[] LegacyRead = Convert.FromHexString("F0B50C1C1E1C24043604171C24143614FFB40004050C2405000D0000FFF7B4FE2D03240DE02336042D191B05360CED18002E06D02B78013E3B7001350137002EF8D1FFBCF0BD");

    public static DetectedSaveTypeInfo? Analyze(ReadOnlySpan<byte> rom)
    {
        int legacy = rom.IndexOf(LegacyBank);
        while (legacy >= 0)
        {
            int entry = legacy - 40;
            if (entry >= 0 && (entry & 1) == 0 && entry + 0x278 + LegacyRead.Length <= rom.Length &&
                rom.Slice(entry, 4).SequenceEqual(new byte[] { 0x12, 0xE0, 0, 0 }) &&
                rom.Slice(entry + 0x278, LegacyRead.Length).SequenceEqual(LegacyRead))
                return new(DetectedSaveTypeKind.Sram, null) { SizeBytes = 0x20000, IsSramPatched = true, CodeEvidence = "SRAM_LEGACY_BANKED_128K" };
            int next = rom[(legacy + 1)..].IndexOf(LegacyBank);
            legacy = next >= 0 ? legacy + 1 + next : -1;
        }
        int converted = Paired(rom, EepromSramRead, EepromSramWrite);
        string? evidence = converted >= 0 ? "EEPROM_SRAM_READ_WRITE" : null;
        int? size = converted >= 0 ? 0x2000 : null;
        if (converted < 0)
        {
            converted = Paired(rom, FlashSramRead, FlashSramWrite);
            evidence = converted >= 0 ? "FLASH_SRAM_READ_WRITE" : null;
            size = converted >= 0 ? 0x10000 : null;
        }
        if (converted < 0)
        {
            converted = Paired(rom, FlashOldSramWrite, Convert.FromHexString("7CB50007000CE0210905091801231B03FF200870013B0131002BFAD100207CBC02BC0847"));
            evidence = converted >= 0 ? "FLASH_SRAM_WRITE_ERASE" : null;
            size = converted >= 0 ? 0x10000 : null;
        }
        int bank = Paired(rom, ReproBank, ReproProgram);
        if (bank >= 0 && (Near(rom, ReproSector102, bank) || Near(rom, ReproSector103, bank)))
        {
            converted = bank;
            evidence = "FLASH1M_SRAM_BANK_PROGRAM_ERASE";
            size = 0x20000;
        }
        if (converted >= 0)
        {
            if (Near(rom, NativeProgram1, converted) || Near(rom, NativeProgram2, converted))
                return new(DetectedSaveTypeKind.Unknown, null) { CodeEvidence = "MIXED_SRAM_FLASH_CODE" };
            return new(DetectedSaveTypeKind.Sram, null) { SizeBytes = size, IsSramPatched = true, CodeEvidence = evidence };
        }

        // 无标签的 FLASH1M 改版仍可能保留完整的原版换页和 ID 查询函数。
        int native = Paired(rom, FlashBank, FlashId);
        return native >= 0
            ? new(DetectedSaveTypeKind.Flash, null) { CodeEvidence = "FLASH_BANK_AND_ID" }
            : null;
    }

    private static int Paired(ReadOnlySpan<byte> rom, byte[] first, byte[] second)
    {
        int start = 0;
        while (start <= rom.Length - first.Length)
        {
            int found = rom[start..].IndexOf(first);
            if (found < 0) break;
            int position = start + found;
            if ((position & 1) == 0 && Near(rom, second, position)) return position;
            start = position + 1;
        }
        return -1;
    }

    private static bool Near(ReadOnlySpan<byte> rom, byte[] pattern, int position)
    {
        int start = Math.Max(0, position - LibraryWindow);
        int end = Math.Min(rom.Length, position + LibraryWindow);
        ReadOnlySpan<byte> window = rom[start..end];
        int offset = window.IndexOf(pattern);
        while (offset >= 0)
        {
            if (((start + offset) & 1) == 0) return true;
            start += offset + 1;
            if (start >= end) break;
            window = rom[start..end];
            offset = window.IndexOf(pattern);
        }
        return false;
    }
}
