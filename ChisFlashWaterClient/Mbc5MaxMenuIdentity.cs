using System.Security.Cryptography;

namespace ChisFlashWaterClient;

public static class Mbc5MaxMenuIdentity
{
    // The existing EX 1.02 fixed menu has no standard Nintendo header. Recognize
    // its exact bytes rather than treating arbitrary headerless files as menus.
    private const string ExtendedMenuSha256 = "228BD9948D981FE717A51513A86FF4BF2AB6C35368513742233EC2F350FB271C";
    public static bool IsKnownExtendedMenu(ReadOnlySpan<byte> data) =>
        data.Length == 0x8000 && Convert.ToHexString(SHA256.HashData(data)) == ExtendedMenuSha256;
}
