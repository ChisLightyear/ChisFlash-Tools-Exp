namespace ChisFlashWaterClient;

public partial class Form1
{
    /// <summary>
    /// GBC 单卡烧录时按盖中盖口径现场重读一次 CFI 窗口：容量、写缓冲、逐扇区表
    /// 优先使用现场读数；窗口全 FF 或全 00 时复用已有几何或选定镜像布局。
    /// </summary>
    private static GbcCfiBurnGeometry ReadGbcBurnGeometry(WaterDeviceProtocol protocol, NorGeometryInfo measured, int imageLength)
    {
        byte[] cfi = new byte[GbcCfiSectorMap.CfiWindowBytes];
        protocol.TryWriteGbc(0x00AA, [0x98], BeggarMaxWriteTimeoutMs);
        protocol.TryReadGbc(0x0000, cfi, BeggarMaxWriteTimeoutMs);
        protocol.TryWriteGbc(0x0000, [0xF0], BeggarMaxWriteTimeoutMs);
        return GbcCfiSectorMap.ReadBurnGeometry(cfi, measured, imageLength);
    }
}
