namespace ChisFlashWaterClient;

/// <summary>
/// 8 MiB GB/GBC 单卡 NOR 的内部扇区布局。
/// 只供擦写时自动选用，不向用户展示；一级类（8M 单卡）由 CFI 容量决定。
/// </summary>
public enum GbcNorSectorLayout
{
    /// <summary>未收录，如实暴露，不猜。</summary>
    Unknown,
    /// <summary>头部小扇区（Bottom boot）：8×8 KiB + 127×64 KiB。</summary>
    BootAtStart,
    /// <summary>尾部小扇区（Top boot）：127×64 KiB + 8×8 KiB。</summary>
    BootAtEnd,
    /// <summary>均匀扇区：128×64 KiB，无小扇区。</summary>
    Uniform,
    /// <summary>
    /// 未收录但 ID 合法（非全 00/FF）时的保守擦除布局：两端各 8×8 KiB、中间 126×64 KiB。
    /// 头部/尾部小扇区都擦到，落在 64 KiB 大扇区上的重复擦无害。不给用户感知。
    /// </summary>
    Conservative,
}

/// <summary>
/// GB/GBC 单卡 NOR ID 收录表，8 位 byte 模式 [X00, X02, X1C, X1E]。ID 只决定型号与收录，
/// 容量一律由 CFI 单次读给出，扇区布局一律由 GbcCfiSectorMap 从 CFI region 表动态生成。
/// 11-18 定案：GBC 换到丐中盖的动态路，本文件里的 GbcNorSectorLayout / GetSectors /
/// ResolveSectorLayout 降级为离线对照与取证数据，产品擦写路径不再调用。
/// ID 只管型号显示，完全不参与写入准入：没读到 / 全 00 / 全 FF / 未收录都不拦写入。
/// </summary>
public static class GbcNorIdCatalog
{
    public const int EightMiBBytes = 8 * 1024 * 1024;
    public const int SmallSectorSizeBytes = 8 * 1024;
    public const int LargeSectorSizeBytes = 64 * 1024;
    public const int SmallSectorCount = 8;
    public const int LargeSectorCount = 127;
    public const int UniformSectorCount = 128;
    /// <summary>保守表：两端各 8×8 KiB 之外，中间按 64 KiB 排 126 个。</summary>
    public const int ConservativeLargeSectorCount = UniformSectorCount - 2;

    public const int ThirtyTwoMiBBytes = 32 * 1024 * 1024;
    /// <summary>32 MiB 档按手册均为均匀 128 KiB 扇区，共 256 个。</summary>
    public const int ThirtyTwoMiBSectorSizeBytes = 128 * 1024;
    public const int ThirtyTwoMiBSectorCount = 256;

    private readonly record struct GbcNorIdEntry(
        byte[] Id,
        int MatchLength,
        string Name,
        GbcNorSectorLayout Layout);

    // LV 系 autoselect 只定义 X00/X02，X1C/X1E 未定义，故只按前 2 字节精确匹配；
    // GL / Spansion 系定义完整 4 字节。手册取证见 .workbuddy/memory/2026-11-17.md。
    private static readonly GbcNorIdEntry[] Entries =
    [
        new([0xC2, 0xCB, 0x00, 0x00], 2, "MX29LV640EB", GbcNorSectorLayout.BootAtStart),
        new([0xC2, 0x7E, 0x10, 0x00], 4, "MX29GL640E T/B Bottom", GbcNorSectorLayout.BootAtStart),
        new([0x01, 0x7E, 0x10, 0x00], 4, "S29GL064N Model 04", GbcNorSectorLayout.BootAtStart),
        new([0xC2, 0xC9, 0x00, 0x00], 2, "MX29LV640ET", GbcNorSectorLayout.BootAtEnd),
        new([0xC2, 0x7E, 0x10, 0x01], 4, "MX29GL640E T/B Top", GbcNorSectorLayout.BootAtEnd),
        new([0x01, 0x7E, 0x10, 0x01], 4, "S29GL064N Model 03", GbcNorSectorLayout.BootAtEnd),
        new([0xC2, 0x7E, 0x0C, 0x01], 4, "MX29GL640E H/L", GbcNorSectorLayout.Uniform),
        new([0x01, 0x7E, 0x0C, 0x01], 4, "S29GL064N 01/02/V1/V2", GbcNorSectorLayout.Uniform),
        new([0x01, 0x7E, 0x13, 0x01], 4, "S29GL064N 06/07/V6/V7", GbcNorSectorLayout.Uniform),
    ];

    private readonly record struct GbcThirtyTwoMiBIdEntry(byte[] Id, string Name);

    // 32 MiB 档（256 Mbit）：只收支持 x8（BYTE#=0）的型号。
    // S29GL256S（GL-S 全系）、JS28F256P30 / 28F256P30、28F256G18 均为 x16-only，不收录。
    // S29GL256S 的 x16 word ID 与 S29GL256N/P 完全相同，ID 分不出来，按 11-18 定案不处理；
    // 型号名也不写 S。取证见 .workbuddy/memory/2026-11-18.md。
    private static readonly GbcThirtyTwoMiBIdEntry[] Entries32MiB =
    [
        new([0x01, 0x7E, 0x22, 0x01], "S29GL256N/P"),
        new([0x89, 0x7E, 0x22, 0x01], "JS28F256M29EWL/EWH / MT28EW256ABA"),
        new([0xC2, 0x7E, 0x22, 0x01], "MX29GL256E/F"),
        new([0x20, 0x7E, 0x22, 0x01], "M29W256GH/GL"),
        new([0x9D, 0x7E, 0x22, 0x01], "IS29GL256"),
    ];

    /// <summary>
    /// 32 MiB 单卡按完整 4 字节 byte ID 精确查收录表。只给型号，不给容量、不给扇区布局。
    /// 未收录返回 false，不按前缀猜、不放宽、不拿 CFI 反推。
    /// </summary>
    public static bool TryMatch32MiB(byte[] id, out string name)
    {
        name = string.Empty;
        if (!IsPlausibleFlashId(id))
            return false;

        foreach (GbcThirtyTwoMiBIdEntry entry in Entries32MiB)
        {
            if (id.AsSpan(0, 4).SequenceEqual(entry.Id))
            {
                name = entry.Name;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 按完整 ID 查收录条目。未收录返回 false，不猜、不按相似字节放宽、不拿 CFI 反推。
    /// </summary>
    public static bool TryMatch(byte[] id, out string name, out GbcNorSectorLayout layout)
    {
        name = string.Empty;
        layout = GbcNorSectorLayout.Unknown;
        if (id.Length < 4 || id.All(value => value is 0x00 or 0xFF))
        {
            return false;
        }

        foreach (GbcNorIdEntry entry in Entries)
        {
            if (id.AsSpan(0, entry.MatchLength).SequenceEqual(entry.Id.AsSpan(0, entry.MatchLength)))
            {
                name = entry.Name;
                layout = entry.Layout;
                return true;
            }
        }

        return false;
    }

    public static GbcNorSectorLayout GetSectorLayout(byte[] id) =>
        TryMatch(id, out _, out GbcNorSectorLayout layout) ? layout : GbcNorSectorLayout.Unknown;

    /// <summary>
    /// 查表 / 显示用：是否读到了一个「有可能属于真芯片」的 ID。
    /// 只排除没读到 / 全 00 / 全 FF，不校验字节是否像已知型号——未收录是收录范围问题，不是读错。
    /// 本方法不参与写入准入：查不到型号只显示 Unknown，不阻止烧录。
    /// </summary>
    public static bool IsPlausibleFlashId(byte[] id) =>
        id.Length >= 4 && !id.All(value => value is 0x00 or 0xFF);

    /// <summary>
    /// 离线对照用：按 ID 给出静态布局。ID 没读到 → Unknown；已收录 → 对应布局；
    /// 未收录但 ID 合法 → Conservative 保守表。产品写入不再走这里（改走 CFI 动态表）。
    /// </summary>
    public static GbcNorSectorLayout ResolveSectorLayout(byte[] id)
    {
        if (!IsPlausibleFlashId(id))
            return GbcNorSectorLayout.Unknown;
        return TryMatch(id, out _, out GbcNorSectorLayout layout)
            ? layout
            : GbcNorSectorLayout.Conservative;
    }

    /// <summary>
    /// 返回擦除时应逐个下发的扇区区间，按地址递增排列。
    /// 仅供擦写路径内部使用，不给用户感知。
    /// </summary>
    public static IReadOnlyList<(int Offset, int Size)> GetSectors(GbcNorSectorLayout layout)
    {
        List<(int Offset, int Size)> sectors = new(UniformSectorCount);
        switch (layout)
        {
            case GbcNorSectorLayout.BootAtStart:
                for (int i = 0; i < SmallSectorCount; i++)
                {
                    sectors.Add((i * SmallSectorSizeBytes, SmallSectorSizeBytes));
                }

                for (int i = 0; i < LargeSectorCount; i++)
                {
                    sectors.Add((SmallSectorCount * SmallSectorSizeBytes + i * LargeSectorSizeBytes, LargeSectorSizeBytes));
                }

                break;

            case GbcNorSectorLayout.BootAtEnd:
                for (int i = 0; i < LargeSectorCount; i++)
                {
                    sectors.Add((i * LargeSectorSizeBytes, LargeSectorSizeBytes));
                }

                for (int i = 0; i < SmallSectorCount; i++)
                {
                    sectors.Add((LargeSectorCount * LargeSectorSizeBytes + i * SmallSectorSizeBytes, SmallSectorSizeBytes));
                }

                break;

            case GbcNorSectorLayout.Uniform:
                for (int i = 0; i < UniformSectorCount; i++)
                {
                    sectors.Add((i * LargeSectorSizeBytes, LargeSectorSizeBytes));
                }

                break;

            case GbcNorSectorLayout.Conservative:
                // 两端都按 8 KiB 下发：不管实际芯片小扇区在头还是在尾都能擦到；
                // 落在 64 KiB 大扇区上的那几发会重复擦同一物理扇区，无害。
                for (int i = 0; i < SmallSectorCount; i++)
                {
                    sectors.Add((i * SmallSectorSizeBytes, SmallSectorSizeBytes));
                }

                int middleOffset = SmallSectorCount * SmallSectorSizeBytes;
                for (int i = 0; i < ConservativeLargeSectorCount; i++)
                {
                    sectors.Add((middleOffset + i * LargeSectorSizeBytes, LargeSectorSizeBytes));
                }

                int tailOffset = middleOffset + ConservativeLargeSectorCount * LargeSectorSizeBytes;
                for (int i = 0; i < SmallSectorCount; i++)
                {
                    sectors.Add((tailOffset + i * SmallSectorSizeBytes, SmallSectorSizeBytes));
                }

                break;

            default:
                return Array.Empty<(int Offset, int Size)>();
        }

        return sectors;
    }
}
