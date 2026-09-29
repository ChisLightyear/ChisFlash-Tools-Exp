using System.Text;
using GbaMultiMenuBuilder.Services;

namespace ChisFlashWaterClient.Services;

public static class SaveTypeDetectorService
{
    private static readonly SramPatchService SramPatchService = new();

    public static DetectedSaveTypeInfo Detect(string romPath)
    {
        byte[] romData = File.ReadAllBytes(romPath);
        return Detect(romData);
    }

    public static DetectedSaveTypeInfo Detect(ReadOnlySpan<byte> romData)
    {
        string? appliedPatchTarget = SramPatchService.DetectAppliedPatchTarget(romData);
        if (!string.IsNullOrWhiteSpace(appliedPatchTarget))
        {
            DetectedSaveTypeInfo originalInfo = DetectFromRules(romData);
            return new DetectedSaveTypeInfo(
                DetectedSaveTypeKind.Sram,
                string.IsNullOrWhiteSpace(originalInfo.Identifier)
                    ? appliedPatchTarget
                    : originalInfo.Identifier);
        }

        return DetectFromRules(romData);
    }

    private static DetectedSaveTypeInfo DetectFromRules(ReadOnlySpan<byte> romData)
    {
        foreach (SaveTypePatternRule rule in SaveTypeRuleConfigService.LoadRules())
        {
            if (TryFindIdentifier(romData, rule, out string identifier))
            {
                return new DetectedSaveTypeInfo(rule.Kind, identifier);
            }
        }

        return new DetectedSaveTypeInfo(DetectedSaveTypeKind.Unknown, null);
    }

    private static bool TryFindIdentifier(
        ReadOnlySpan<byte> romData,
        SaveTypePatternRule rule,
        out string identifier)
    {
        if (rule.VersionDigitCount > 0)
        {
            return TryFindVersionedIdentifier(
                romData,
                rule.SignatureBytes,
                rule.Signature,
                rule.VersionDigitCount,
                out identifier);
        }

        if (ContainsAscii(romData, rule.SignatureBytes))
        {
            identifier = rule.Signature;
            return true;
        }

        identifier = string.Empty;
        return false;
    }

    private static bool TryFindVersionedIdentifier(
        ReadOnlySpan<byte> romData,
        byte[] prefixBytes,
        string prefix,
        int digitCount,
        out string identifier)
    {
        for (int i = 0; i <= romData.Length - (prefixBytes.Length + digitCount); i++)
        {
            if (!romData.Slice(i, prefixBytes.Length).SequenceEqual(prefixBytes))
            {
                continue;
            }

            bool valid = true;
            for (int digitIndex = 0; digitIndex < digitCount; digitIndex++)
            {
                byte value = romData[i + prefixBytes.Length + digitIndex];
                if (value is < (byte)'0' or > (byte)'9')
                {
                    valid = false;
                    break;
                }
            }

            if (!valid)
            {
                continue;
            }

            identifier = Encoding.ASCII.GetString(romData.Slice(i, prefixBytes.Length + digitCount));
            return true;
        }

        identifier = string.Empty;
        return false;
    }

    private static bool ContainsAscii(ReadOnlySpan<byte> romData, byte[] pattern)
    {
        for (int i = 0; i <= romData.Length - pattern.Length; i++)
        {
            if (romData.Slice(i, pattern.Length).SequenceEqual(pattern))
            {
                return true;
            }
        }

        return false;
    }
}
