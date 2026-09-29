using System.Buffers.Binary;

namespace ChisFlashWaterClient.Services;

public sealed class WaitcntPatchResult
{
    public int PatchedCount { get; init; }
    public IReadOnlyList<int> Offsets { get; init; } = [];
}

public static class WaitcntPatchService
{
    private const uint WaitcntAddress = 0x04000204;
    private const int ThumbLdrBackoffWords = 256;
    private const int ArmLdrBackoffWords = 1024;

    public static WaitcntPatchResult Apply(Span<byte> romData)
    {
        List<int> offsets = [];
        int wordCount = romData.Length / sizeof(uint);

        for (int wordIndex = 0; wordIndex < wordCount; wordIndex++)
        {
            int byteOffset = wordIndex * sizeof(uint);
            if (BinaryPrimitives.ReadUInt32LittleEndian(romData.Slice(byteOffset, sizeof(uint))) != WaitcntAddress)
            {
                continue;
            }

            int startThumbHalfword = Math.Max(0, wordIndex - ThumbLdrBackoffWords) * 2;
            int targetHalfword = wordIndex * 2;
            int startArmWord = Math.Max(0, wordIndex - ArmLdrBackoffWords);

            if (!HasThumbLdrPc(romData, startThumbHalfword, targetHalfword) &&
                !HasArmLdrPc(romData, startArmWord, wordIndex))
            {
                continue;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(romData.Slice(byteOffset, sizeof(uint)), 0);
            offsets.Add(byteOffset);
        }

        return new WaitcntPatchResult
        {
            PatchedCount = offsets.Count,
            Offsets = offsets,
        };
    }

    private static bool HasThumbLdrPc(ReadOnlySpan<byte> romData, int startHalfword, int targetHalfword)
    {
        int halfwordCount = romData.Length / sizeof(ushort);
        int endHalfword = Math.Min(targetHalfword, halfwordCount);

        for (int halfwordIndex = startHalfword; halfwordIndex < endHalfword; halfwordIndex++)
        {
            ushort instruction = BinaryPrimitives.ReadUInt16LittleEndian(
                romData.Slice(halfwordIndex * sizeof(ushort), sizeof(ushort)));
            uint opcode = (uint)instruction >> 11;
            if (opcode != 0x09)
            {
                continue;
            }

            uint imm8 = (uint)instruction & 0xFFu;
            uint target = ((uint)halfwordIndex & ~1u) + imm8 * 2u + 2u;
            if (target == (uint)targetHalfword)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasArmLdrPc(ReadOnlySpan<byte> romData, int startWord, int targetWord)
    {
        for (int wordIndex = startWord; wordIndex < targetWord; wordIndex++)
        {
            uint instruction = BinaryPrimitives.ReadUInt32LittleEndian(
                romData.Slice(wordIndex * sizeof(uint), sizeof(uint)));
            uint opcode = (instruction >> 20) & 0xFFu;
            uint rn = (instruction >> 16) & 0x0Fu;
            if (opcode != 0x59u || rn != 15u)
            {
                continue;
            }

            uint imm12 = instruction & 0xFFFu;
            if ((imm12 & 3u) != 0)
            {
                continue;
            }

            uint target = (uint)wordIndex + (imm12 >> 2) + 2u;
            if (target == (uint)targetWord)
            {
                return true;
            }
        }

        return false;
    }
}
