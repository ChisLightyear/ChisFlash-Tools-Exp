using System.Text;

namespace ChisFlashWaterClient;

public enum DetectedSaveTypeKind
{
    Unknown = 0,
    None = 1,
    Sram = 2,
    Eeprom = 3,
    Flash = 4,
    Flash512 = 5,
    Flash1M = 6,
}

public sealed record DetectedSaveTypeInfo(
    DetectedSaveTypeKind Kind,
    string? Identifier)
{
    public int? SizeBytes { get; init; }
    public bool IsSramPatched { get; init; }
    public string? CodeEvidence { get; init; }
}

public sealed class SaveTypePatternRule
{
    public SaveTypePatternRule(string signature, DetectedSaveTypeKind kind, int versionDigitCount)
    {
        if (string.IsNullOrWhiteSpace(signature))
        {
            throw new ArgumentException("Signature cannot be empty.", nameof(signature));
        }

        if (versionDigitCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(versionDigitCount));
        }

        Signature = signature.Trim();
        Kind = kind;
        VersionDigitCount = versionDigitCount;
        SignatureBytes = Encoding.ASCII.GetBytes(Signature);
    }

    public string Signature { get; }

    public byte[] SignatureBytes { get; }

    public DetectedSaveTypeKind Kind { get; }

    public int VersionDigitCount { get; }
}
