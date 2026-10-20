namespace GbaMultiMenuBuilder.Fc;

public sealed record FcSnapshotPlan(int TotalBytes,int WritableChrBytes,int SpillBase)
{
    public const uint Layout=0xc5083224;
    public const int FixedBytes=15182,PrimaryBytes=131072,ChrCapacity=524288;
    public bool Enabled=>TotalBytes!=0;
    public int SpillBytes=>Math.Max(0,TotalBytes-PrimaryBytes);
    public static FcSnapshotPlan Create(int mapper,int chrRom,int chrRam,int workRam)
    {
        int live=chrRom==0?chrRam:chrRom;
        int writable=chrRom==0||mapper is 252 or 268?live:0;
        long total=(long)FixedBytes+workRam+writable;
        long spill=Math.Max(0,total-PrimaryBytes);
        long tail=((long)live+4095)&~4095L;
        bool fits=live>0&&live<=ChrCapacity&&workRam>=0&&total<=ChrCapacity&&(spill==0||tail+spill<=ChrCapacity);
        return new(fits?(int)total:0,writable,fits&&spill>0?(int)tail:0);
    }
}
