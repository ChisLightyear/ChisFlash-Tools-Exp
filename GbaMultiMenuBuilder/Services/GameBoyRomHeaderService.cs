using System.Text;

namespace GbaMultiMenuBuilder.Services;

public static class GameBoyRomHeaderService
{
    public const int RomBankSize = 16 * 1024;
    public const int RamBankSize = 8 * 1024;
    private const int HeaderTitleOffset = 0x134;
    private const int HeaderTitleLength = 15;

    private static readonly byte[] NintendoLogo =
    [
        0xCE, 0xED, 0x66, 0x66, 0xCC, 0x0D, 0x00, 0x0B,
        0x03, 0x73, 0x00, 0x83, 0x00, 0x0C, 0x00, 0x0D,
        0x00, 0x08, 0x11, 0x1F, 0x88, 0x89, 0x00, 0x0E,
        0xDC, 0xCC, 0x6E, 0xE6, 0xDD, 0xDD, 0xD9, 0x99,
        0xBB, 0xBB, 0x67, 0x63, 0x6E, 0x0E, 0xEC, 0xCC,
        0xDD, 0xDC, 0x99, 0x9F, 0xBB, 0xB9, 0x33, 0x3E,
    ];

    private static readonly HashSet<byte> BatteryBackedCartridgeTypes =
    [
        0x03,
        0x06,
        0x09,
        0x0D,
        0x0F,
        0x10,
        0x13,
        0x1B,
        0x1E,
        0x22,
        0xFF,
    ];

    private static readonly IReadOnlyDictionary<byte, string> MbcTypeNames = new Dictionary<byte, string>
    {
        [0x00] = "ROM",
        [0x01] = "MBC1",
        [0x02] = "MBC1",
        [0x03] = "MBC1",
        [0x05] = "MBC2",
        [0x06] = "MBC2",
        [0x08] = "ROM",
        [0x09] = "ROM",
        [0x0B] = "MMM01",
        [0x0C] = "MMM01",
        [0x0D] = "MMM01",
        [0x0F] = "MBC3",
        [0x10] = "MBC30",
        [0x11] = "MBC3",
        [0x12] = "MBC3",
        [0x13] = "MBC3",
        [0x19] = "MBC5",
        [0x1A] = "MBC5",
        [0x1B] = "MBC5",
        [0x1C] = "MBC5",
        [0x1D] = "MBC5",
        [0x1E] = "MBC5",
        [0x20] = "MBC6",
        [0x22] = "MBC7",
        [0xFC] = "CAMERA",
        [0xFD] = "TAMA5",
        [0xFE] = "HuC3",
        [0xFF] = "HuC1",
    };

    private static readonly IReadOnlyDictionary<byte, string> RomSizeDescriptions = new Dictionary<byte, string>
    {
        [0x00] = "32KB",
        [0x01] = "64KB",
        [0x02] = "128KB",
        [0x03] = "256KB",
        [0x04] = "512KB",
        [0x05] = "1MB",
        [0x06] = "2MB",
        [0x07] = "4MB",
        [0x08] = "8MB",
        [0x52] = "1.1MB",
        [0x53] = "1.2MB",
        [0x54] = "1.5MB",
    };

    private static readonly IReadOnlyDictionary<byte, string> RamSizeDescriptions = new Dictionary<byte, string>
    {
        [0x00] = "0KB",
        [0x01] = "2KB",
        [0x02] = "8KB",
        [0x03] = "32KB",
        [0x04] = "128KB",
        [0x05] = "64KB",
    };

    public static bool TryRead(string romPath, out GameBoyRomMetadata metadata)
    {
        try
        {
            metadata = Read(romPath);
            return true;
        }
        catch
        {
            metadata = null!;
            return false;
        }
    }

    public static GameBoyRomMetadata Read(string romPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(romPath);
        var normalizedPath = Path.GetFullPath(romPath);
        var romData = File.ReadAllBytes(normalizedPath);
        return Read(normalizedPath, romData);
    }

    public static GameBoyRomMetadata Read(string romPath, ReadOnlySpan<byte> romData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(romPath);
        if (romData.Length < 0x150)
        {
            throw new InvalidOperationException($"ROM is too small to contain a valid Game Boy header: {romPath}");
        }

        var cartridgeType = romData[0x147];
        var romSizeCode = romData[0x148];
        var ramSizeCode = romData[0x149];
        var declaredRomBankCount = GetDeclaredRomBankCount(romSizeCode);
        var declaredRomSizeBytes = checked((long)declaredRomBankCount * RomBankSize);
        var fileSizeBytes = romData.Length;
        var fileRomBankCount = Math.Max(2, DivCeil(fileSizeBytes, RomBankSize));
        var effectiveRomBankCount = NormalizeRomBankCountToAddressableSize(Math.Max(fileRomBankCount, declaredRomBankCount));
        var effectiveRomSizeBytes = checked((long)effectiveRomBankCount * RomBankSize);
        var ramBankCount = GetRamBankCount(ramSizeCode);
        var effectiveRamBankCount = GetEffectiveRamBankCount(cartridgeType, ramSizeCode);
        var headerTitle = ReadHeaderTitle(romData);
        var hasValidNintendoLogo = romData.Slice(0x104, NintendoLogo.Length).SequenceEqual(NintendoLogo);

        return new GameBoyRomMetadata(
            Path.GetFullPath(romPath),
            headerTitle,
            cartridgeType,
            romSizeCode,
            ramSizeCode,
            declaredRomBankCount,
            fileRomBankCount,
            effectiveRomBankCount,
            declaredRomSizeBytes,
            effectiveRomSizeBytes,
            ramBankCount,
            effectiveRamBankCount,
            BatteryBackedCartridgeTypes.Contains(cartridgeType),
            hasValidNintendoLogo);
    }

    public static int GetDeclaredRomBankCount(byte romSizeCode) =>
        romSizeCode switch
        {
            0x00 => 2,
            0x01 => 4,
            0x02 => 8,
            0x03 => 16,
            0x04 => 32,
            0x05 => 64,
            0x06 => 128,
            0x07 => 256,
            0x08 => 512,
            0x52 => 72,
            0x53 => 80,
            0x54 => 96,
            _ => 0,
        };

    public static int NormalizeRomBankCountToAddressableSize(int romBankCount)
    {
        if (romBankCount <= 2)
        {
            return 2;
        }

        foreach (var candidate in new[] { 4, 8, 16, 32, 64, 128, 256, 512 })
        {
            if (romBankCount <= candidate)
            {
                return candidate;
            }
        }

        return romBankCount;
    }

    public static long NormalizeRomSizeBytesToAddressableSize(long romSizeBytes)
    {
        var bankCount = Math.Max(2, DivCeilLong(Math.Max(1L, romSizeBytes), RomBankSize));
        return checked((long)NormalizeRomBankCountToAddressableSize(bankCount) * RomBankSize);
    }

    public static int GetRamBankCount(byte ramSizeCode) =>
        ramSizeCode switch
        {
            0x00 => 0,
            0x01 => 1,
            0x02 => 1,
            0x03 => 4,
            0x04 => 16,
            0x05 => 8,
            _ => 0,
        };

    public static string GetMbcTypeDisplay(byte cartridgeType) =>
        MbcTypeNames.TryGetValue(cartridgeType, out var value)
            ? value
            : $"0x{cartridgeType:X2}";

    public static string GetRomSizeDisplay(byte romSizeCode) =>
        RomSizeDescriptions.TryGetValue(romSizeCode, out var value)
            ? value
            : $"0x{romSizeCode:X2}";

    public static string GetRamSizeDisplay(byte ramSizeCode) =>
        RamSizeDescriptions.TryGetValue(ramSizeCode, out var value)
            ? value
            : $"0x{ramSizeCode:X2}";

    public static bool HasBuiltInMbc2Ram(byte cartridgeType) =>
        cartridgeType is 0x05 or 0x06;

    public static int GetEffectiveRamBankCount(byte cartridgeType, byte ramSizeCode)
    {
        if (HasBuiltInMbc2Ram(cartridgeType))
        {
            // Align with the fixed-directory runtime expectation in new2:
            // MBC2 save support still consumes one logical SRAM bank in metadata.
            return 1;
        }

        return GetRamBankCount(ramSizeCode);
    }

    private static string ReadHeaderTitle(ReadOnlySpan<byte> romData)
    {
        var titleBytes = new List<byte>(HeaderTitleLength);
        for (var offset = HeaderTitleOffset;
            offset < HeaderTitleOffset + HeaderTitleLength && offset < romData.Length;
            offset++)
        {
            var value = romData[offset];
            if (value == 0)
            {
                break;
            }

            if (value is < 0x20 or > 0x7E)
            {
                value = (byte)' ';
            }

            titleBytes.Add(value);
        }

        if (titleBytes.Count == 0)
        {
            return string.Empty;
        }

        return Encoding.ASCII.GetString(titleBytes.ToArray()).Trim();
    }

    private static int DivCeil(int value, int divisor) => (value / divisor) + ((value % divisor) != 0 ? 1 : 0);

    private static int DivCeilLong(long value, int divisor) => checked((int)((value / divisor) + ((value % divisor) != 0 ? 1 : 0)));
}

public sealed record GameBoyRomMetadata(
    string RomPath,
    string HeaderTitle,
    byte CartridgeType,
    byte RomSizeCode,
    byte RamSizeCode,
    int DeclaredRomBankCount,
    int FileRomBankCount,
    int EffectiveRomBankCount,
    long DeclaredRomSizeBytes,
    long EffectiveRomSizeBytes,
    int RamBankCount,
    int EffectiveRamBankCount,
    bool HasBattery,
    bool HasValidNintendoLogo);
