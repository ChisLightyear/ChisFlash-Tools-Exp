namespace GbaMultiMenuBuilder.Services;

public static class GameMetadataReader
{
    public static bool ShouldEnableMap256m(string romPath)
    {
        using var stream = File.OpenRead(romPath);
        if (stream.Length < 0xB0)
        {
            return false;
        }

        stream.Seek(0xAC, SeekOrigin.Begin);
        Span<byte> code = stackalloc byte[4];
        stream.ReadExactly(code);
        return ShouldEnableMap256mFromRomCode(code);
    }

    public static bool ShouldEnableMap256m(ReadOnlySpan<byte> romData)
    {
        if (romData.Length < 0xB0)
        {
            return false;
        }

        return ShouldEnableMap256mFromRomCode(romData.Slice(0xAC, 4));
    }

    private static bool ShouldEnableMap256mFromRomCode(ReadOnlySpan<byte> romCode) =>
        romCode.Length >= 3 &&
        romCode[0] == (byte)'B' &&
        romCode[1] == (byte)'P' &&
        (romCode[2] == (byte)'G' || romCode[2] == (byte)'R');
}
