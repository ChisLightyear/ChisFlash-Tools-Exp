namespace ChisFlashWaterClient;

public enum Mbc5LayoutError
{
    None,
    InvalidSlot,
    MenuRequired,
    FourthSlotOccupied,
    FourthSlotUnavailable,
    GamesRequireMenu,
    RomTooSmall,
    RomTooLarge,
    SaveTooLarge,
}

public sealed class Mbc5SlotContent
{
    private readonly byte[] data;

    public Mbc5SlotContent(byte[] romData, int addressableSizeBytes, int saveSizeBytes, string title, string source)
    {
        ArgumentNullException.ThrowIfNull(romData);
        if (addressableSizeBytes < romData.Length || saveSizeBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(addressableSizeBytes));
        data = romData.ToArray();
        AddressableSizeBytes = addressableSizeBytes;
        SaveSizeBytes = saveSizeBytes;
        Title = title;
        Source = source;
    }

    public ReadOnlyMemory<byte> Data => data;
    public int AddressableSizeBytes { get; }
    public int SaveSizeBytes { get; }
    public string Title { get; }
    public string Source { get; }
}

public readonly record struct Mbc5SlotDefinition(int Index, bool Enabled, int OffsetBytes, int CapacityBytes, int SaveOffsetBytes, int SaveCapacityBytes);

public sealed class Mbc5EightMiBLayout
{
    public const int MiB = 0x100000;
    public const int CapacityBytes = 8 * MiB;
    public const int SlotCount = 5;
    private readonly Mbc5SlotContent?[] contents = new Mbc5SlotContent?[SlotCount];

    public bool HasMenu => contents[0] is not null;
    public bool IsThreeInOne => HasMenu && contents[3]?.AddressableSizeBytes > 2 * MiB;
    public bool HasContent => contents.Any(content => content is not null);
    public Mbc5SlotContent? GetContent(int slot) => contents[ValidateSlot(slot)];

    public Mbc5SlotDefinition GetSlot(int slot)
    {
        ValidateSlot(slot);
        if (slot == 0)
            return new(slot, true, 0, MiB, 0, 0);
        if (!HasMenu)
            return new(slot, slot == 1, 0, slot == 1 ? CapacityBytes : 0, 0, slot == 1 ? 0x20000 : 0);
        int offset = slot switch { 1 => MiB, 2 => 2 * MiB, 3 => 4 * MiB, _ => 6 * MiB };
        int capacity = slot switch { 1 => MiB, 2 => 2 * MiB, 3 => 4 * MiB, _ => 2 * MiB };
        return new(slot, slot != 4 || !IsThreeInOne, offset, capacity, (slot - 1) * 0x8000, 0x8000);
    }

    public bool TrySet(int slot, Mbc5SlotContent? content, out Mbc5LayoutError error)
    {
        error = Mbc5LayoutError.None;
        if (slot < 0 || slot >= SlotCount)
        {
            error = Mbc5LayoutError.InvalidSlot;
            return false;
        }
        Mbc5SlotContent?[] proposed = contents.ToArray();
        proposed[slot] = content;
        if (!Validate(proposed, out error))
            return false;
        proposed.CopyTo(contents, 0);
        return true;
    }

    public void Clear() => Array.Clear(contents);

    public Mbc5EightMiBLayout Snapshot()
    {
        var snapshot = new Mbc5EightMiBLayout();
        contents.CopyTo(snapshot.contents, 0);
        return snapshot;
    }

    public byte[] BuildImage()
    {
        if (!Validate(contents, out Mbc5LayoutError error))
            throw new InvalidOperationException(error.ToString());
        byte[] image = new byte[CapacityBytes];
        Array.Fill(image, (byte)0xFF);
        for (int slot = 0; slot < SlotCount; slot++)
            if (contents[slot] is { } content)
                content.Data.Span.CopyTo(image.AsSpan(GetSlot(slot).OffsetBytes));
        return image;
    }

    private static bool Validate(Mbc5SlotContent?[] proposed, out Mbc5LayoutError error)
    {
        error = Mbc5LayoutError.None;
        bool hasMenu = proposed[0] is not null;
        if (!hasMenu && proposed.Skip(2).Any(content => content is not null))
        {
            error = Mbc5LayoutError.GamesRequireMenu;
            return false;
        }
        bool threeInOne = hasMenu && proposed[3]?.AddressableSizeBytes > 2 * MiB;
        if (threeInOne && proposed[4] is not null)
        {
            error = Mbc5LayoutError.FourthSlotOccupied;
            return false;
        }
        for (int slot = 0; slot < SlotCount; slot++)
        {
            if (proposed[slot] is not { } content)
                continue;
            int capacity = slot switch { 0 => MiB, 1 => hasMenu ? MiB : CapacityBytes, 2 => 2 * MiB, 3 => 4 * MiB, _ => 2 * MiB };
            if (content.Data.Length < (slot == 0 ? 0x150 : 0x8000))
            {
                error = Mbc5LayoutError.RomTooSmall;
                return false;
            }
            if (content.Data.Length > capacity || (slot != 0 && content.AddressableSizeBytes > capacity))
            {
                error = Mbc5LayoutError.RomTooLarge;
                return false;
            }
            if (slot != 0 && content.SaveSizeBytes > (hasMenu ? 0x8000 : 0x20000))
            {
                error = Mbc5LayoutError.SaveTooLarge;
                return false;
            }
        }
        return true;
    }

    private static int ValidateSlot(int slot) => slot is >= 0 and < SlotCount
        ? slot : throw new ArgumentOutOfRangeException(nameof(slot));
}
