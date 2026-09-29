using System.Buffers.Binary;
using ChisFlashWaterClient.Services;

namespace ChisFlashWaterClient;

public static class GbaMenuRomService
{
    private const string UniversalMenuFileName = "universal_menu.gba";
    private const string SimplifiedNsMenuFileName = "simplified_ns_menu.gba";
    private const int CoverTrailerOffset = 0x0AFFF0;
    private const int CoverTrailerSize = 16;
    private const int PreparedMenuLength = 0x0B0000;
    private static readonly byte[] UniversalMenuSignature = "UMMF"u8.ToArray();
    private static readonly byte[] SimplifiedNsMenuSignature = "SNSF"u8.ToArray();

    public static byte[] LoadUniversalMenuRom(out string sourcePath)
    {
        foreach (string candidate in EnumerateUniversalMenuCandidates())
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            byte[] menuRomBytes = File.ReadAllBytes(candidate);
            ValidateUniversalMenuRom(menuRomBytes);
            sourcePath = candidate;
            return PrepareCoverMenuRom(menuRomBytes);
        }

        throw new FileNotFoundException(
            LocalizedText.T(
                "error",
                "universal_menu_not_found",
                "The new GBA menu ROM was not found. Make sure Assets\\universal_menu.gba is installed with the client."));
    }

    public static bool HasUniversalMenuSignature(ReadOnlySpan<byte> menuRomBytes) =>
        menuRomBytes.IndexOf(UniversalMenuSignature) >= 0;

    public static bool SupportsCovers(ReadOnlySpan<byte> menuRomBytes) =>
        HasUniversalMenuSignature(menuRomBytes) || HasSimplifiedNsMenuSignature(menuRomBytes);

    public static bool HasSimplifiedNsMenuSignature(ReadOnlySpan<byte> menuRomBytes) =>
        menuRomBytes.IndexOf(SimplifiedNsMenuSignature) >= 0;

    public static byte[] LoadSimplifiedNsMenuRom(out string sourcePath)
    {
        foreach (string candidate in EnumerateSimplifiedNsMenuCandidates())
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            byte[] menuRomBytes = File.ReadAllBytes(candidate);
            ValidateUniversalMenuRom(menuRomBytes);
            if (!HasSimplifiedNsMenuSignature(menuRomBytes))
            {
                throw new InvalidOperationException(
                    LocalizedText.T(
                        "error",
                        "simplified_ns_menu_signature_missing",
                        "The selected menu ROM is not a Simplified NS Menu build."));
            }

            sourcePath = candidate;
            return PrepareCoverMenuRom(menuRomBytes);
        }

        throw new FileNotFoundException(
            LocalizedText.T(
                "error",
                "simplified_ns_menu_not_found",
                "The Simplified NS Menu ROM was not found. Make sure Assets\\simplified_ns_menu.gba is installed with the client."));
    }

    public static void ValidateUniversalMenuRom(byte[] menuRomBytes)
    {
        ArgumentNullException.ThrowIfNull(menuRomBytes);

        if (!WaterCartScanner.HasValidGbaHeader(menuRomBytes))
        {
            throw new InvalidOperationException(
                LocalizedText.T(
                    "error",
                    "universal_menu_invalid_gba",
                    "The new GBA menu resource does not have a valid GBA ROM header."));
        }

        if (!SupportsCovers(menuRomBytes))
        {
            throw new InvalidOperationException(
                LocalizedText.T(
                    "error",
                    "universal_menu_signature_missing",
                    "The selected menu ROM does not contain an NS menu capability signature."));
        }

        if (menuRomBytes.Length > WaterMagicUpgradeBuilder.WlItemListOffset)
        {
            throw new InvalidOperationException(
                LocalizedText.Tf(
                    "error",
                    "universal_menu_too_large",
                    "The new GBA menu ROM is too large and overlaps the reserved item list at 0x{0:X6}.",
                    WaterMagicUpgradeBuilder.WlItemListOffset));
        }
    }

    public static byte[] PrepareCoverMenuRom(byte[] menuRomBytes)
    {
        ValidateUniversalMenuRom(menuRomBytes);
        return EnsureCoverPackRegion(menuRomBytes);
    }

    private static IEnumerable<string> EnumerateUniversalMenuCandidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "Assets", UniversalMenuFileName);
        yield return Path.Combine(AppContext.BaseDirectory, UniversalMenuFileName);
    }

    private static IEnumerable<string> EnumerateSimplifiedNsMenuCandidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "Assets", SimplifiedNsMenuFileName);
        yield return Path.Combine(AppContext.BaseDirectory, SimplifiedNsMenuFileName);
    }

    private static byte[] EnsureCoverPackRegion(byte[] menuRomBytes)
    {
        if (menuRomBytes.Length > PreparedMenuLength)
        {
            throw new InvalidOperationException(
                LocalizedText.T(
                    "error",
                    "universal_menu_cover_region_too_small",
                    "The NS menu ROM is larger than the fixed 0x0B0000 cover/menu head area."));
        }

        if (menuRomBytes.Length >= CoverTrailerOffset + CoverTrailerSize)
        {
            GbaMenuFirstScreenPreviewInfo preview = GbaMenuFirstScreenPreviewCodec.Decode(menuRomBytes, []);
            if (preview.IsDetected)
            {
                return menuRomBytes;
            }
        }

        using Bitmap placeholder = new(GbaMenuCoverPackBuilder.CoverWidth, GbaMenuCoverPackBuilder.CoverHeight);
        using (Graphics graphics = Graphics.FromImage(placeholder))
        {
            graphics.Clear(Color.White);
        }

        GbaMenuCoverPackBuildResult pack = GbaMenuCoverPackBuilder.Build(
            [new GbaMenuCoverSource("UNKN", placeholder)]);
        int packOffset = Align4(menuRomBytes.Length);
        if (packOffset + pack.PackData.Length > CoverTrailerOffset)
        {
            throw new InvalidOperationException(
                LocalizedText.T(
                    "error",
                    "universal_menu_cover_region_too_small",
                    "The new GBA menu ROM leaves too little room for the editable cover pack."));
        }

        byte[] prepared = Enumerable.Repeat((byte)0xFF, PreparedMenuLength).ToArray();
        menuRomBytes.CopyTo(prepared, 0);
        pack.PackData.CopyTo(prepared, packOffset);

        Span<byte> trailer = prepared.AsSpan(CoverTrailerOffset, CoverTrailerSize);
        "GCVT"u8.CopyTo(trailer);
        BinaryPrimitives.WriteUInt16LittleEndian(trailer[4..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(trailer[6..], CoverTrailerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(trailer[8..], (uint)packOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(trailer[12..], (uint)(CoverTrailerOffset - packOffset));
        return prepared;
    }

    private static int Align4(int value) => (value + 3) & ~3;
}
