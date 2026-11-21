namespace ChisFlashWaterClient;

public partial class Form1
{
    /// <summary>
    /// GBC 单卡烧录时按盖中盖口径现场重读一次 CFI 窗口：容量、写缓冲、逐扇区表
    /// 全部取自这一次读，不复用扫描缓存。
    /// 读写返回值沿用原版的处理，由调用方按现场读出的容量检查镜像长度。
    /// </summary>
    private static GbcCfiBurnGeometry ReadGbcBurnGeometry(WaterDeviceProtocol protocol)
    {
        byte[] cfi = new byte[GbcCfiSectorMap.CfiWindowBytes];
        protocol.TryWriteGbc(0x00AA, [0x98], BeggarMaxWriteTimeoutMs);
        protocol.TryReadGbc(0x0000, cfi, BeggarMaxWriteTimeoutMs);
        protocol.TryWriteGbc(0x0000, [0xF0], BeggarMaxWriteTimeoutMs);
        return GbcCfiSectorMap.ReadBurnGeometry(cfi);
    }
}
