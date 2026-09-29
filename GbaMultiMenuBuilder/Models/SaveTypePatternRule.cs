using System.Text;

namespace GbaMultiMenuBuilder.Models;

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
