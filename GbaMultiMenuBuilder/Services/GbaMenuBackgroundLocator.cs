using System.Buffers.Binary;

namespace GbaMultiMenuBuilder.Services;

public static class GbaMenuBackgroundLocator
{
    public const int BitmapBytes = 240 * 160;
    public const int PaletteBytes = 512;
    public const int RegionBytes = BitmapBytes + PaletteBytes;
    private const uint RomAddress = 0x08000000;
    private static readonly byte[] FontMarker = [0x52, 0x54, 0x46, 0x4E, 0xFF, 0xFE];

    public static bool TryFind(ReadOnlySpan<byte> rom, out int offset)
    {
        offset = -1;
        int fontStart = rom.IndexOf(FontMarker);
        if (fontStart < RegionBytes) return false;
        if (rom.IndexOf("UMMF"u8) >= 0 || rom.IndexOf("SNSF"u8) >= 0)
        {
            int candidate = fontStart - RegionBytes;
            if (candidate % 4 != 0 || candidate > rom.Length - RegionBytes) return false;
            offset = candidate;
            return true;
        }
        Dictionary<uint, int> references = [];
        for (int position = 0; position <= fontStart - 4; position += 4)
        {
            uint address = BinaryPrimitives.ReadUInt32LittleEndian(rom[position..]);
            if (address < RomAddress || address >= RomAddress + fontStart) continue;
            references.TryAdd(address, position);
        }
        foreach (var reference in references)
        {
            int candidate = checked((int)(reference.Key - RomAddress));
            if (candidate < 0xC0 || candidate % 4 != 0 || candidate > fontStart - RegionBytes || reference.Value >= candidate) continue;
            uint paletteAddress = reference.Key + BitmapBytes;
            if (!references.TryGetValue(paletteAddress, out int paletteReference) || paletteReference >= candidate) continue;
            if (offset >= 0 && offset != candidate) { offset = -1; return false; }
            offset = candidate;
        }
        return offset >= 0;
    }
}
