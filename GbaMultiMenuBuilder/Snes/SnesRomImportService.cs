using GbaMultiMenuBuilder.Fc;
using GbaMultiMenuBuilder.Md;
using GbaMultiMenuBuilder.Services;

namespace GbaMultiMenuBuilder.Snes;

public static class SnesRomImportService
{
    public const string LaunchUnavailable = "SNES已识别，待对接MIX启动协议；暂不可合卡或写卡。SNES recognized; MIX launch adapter is not confirmed. Packing/programming is disabled.";

    public static byte[] ReadRom(ArchiveRomSource source, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (source.Platform != RomPlatform.Snes || source.Length is < 32768 or > SnesRomHeaderService.MaxRomBytes + 512L)
            throw new InvalidDataException("Invalid SNES source or size (maximum 8 MiB + 512-byte copier header).");
        byte[] Read(Stream input)
        {
            byte[] bytes = new byte[checked((int)source.Length)];
            int offset = 0;
            while (offset < bytes.Length)
            {
                cancellation.ThrowIfCancellationRequested();
                int read = input.Read(bytes, offset, Math.Min(65536, bytes.Length - offset));
                if (read == 0) throw new EndOfStreamException("Truncated SNES ROM.");
                offset += read;
            }
            if (input.ReadByte() != -1) throw new InvalidDataException("SNES source length changed; import again.");
            return bytes;
        }
        if (source.EntryIndex is int index)
        {
            using var archive = MdZipFile.OpenRead(source.SourcePath);
            if (index < 0 || index >= archive.Entries.Count) throw new InvalidDataException("ZIP changed; import again.");
            var entry = archive.Entries[index];
            if (entry.FullName != source.EntryName || entry.Length != source.Length) throw new InvalidDataException("ZIP member changed; import again.");
            using var input = entry.Open();
            return Read(input);
        }
        using var file = File.OpenRead(source.SourcePath);
        if (file.Length != source.Length) throw new InvalidDataException("SNES source length changed; import again.");
        return Read(file);
    }

    public static FcGame CreateGame(byte[] raw, string title)
    {
        SnesRomInfo snes = SnesRomHeaderService.Parse(raw);
        byte[] data = snes.HasValidHeader && snes.HasCopierHeader ? raw.AsSpan(512).ToArray() : raw.ToArray();
        string reason = snes.Error ?? LaunchUnavailable;
        var info = new NesRomInfo(-1, 0, data.Length, 0, snes.Battery ? 0 : snes.RamBytes,
            snes.Battery ? snes.RamBytes : 0, 0, 0, snes.Battery, false, 0,
            $"SNES header · {snes.ChipDisplay}", reason) { Warning = snes.Warning };
        return new FcGame { Name = title, Data = data, Snes = snes, Info = info, Enabled = false };
    }
}
