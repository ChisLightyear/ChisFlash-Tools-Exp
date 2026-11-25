using System.Diagnostics;

namespace ChisFlashWaterClient;

// 同一次烧录可能包含多个游戏段和菜单段。各段只在本阶段结束时累计。
internal sealed class GbaRomTiming
{
    internal TimeSpan Erase { get; private set; }
    internal TimeSpan Program { get; private set; }
    internal long ImageBytes { get; private set; }
    internal long ProgrammedBytes { get; private set; }

    internal void FinishErase(long start) => Erase += Stopwatch.GetElapsedTime(start);

    internal void FinishProgram(long start, long imageBytes, long programmedBytes)
    {
        Program += Stopwatch.GetElapsedTime(start);
        ImageBytes += imageBytes;
        ProgrammedBytes += programmedBytes;
    }
}
