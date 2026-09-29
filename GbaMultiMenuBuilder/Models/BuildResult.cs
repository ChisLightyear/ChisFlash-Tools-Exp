namespace GbaMultiMenuBuilder.Models;

public sealed record BuildResult(
    string OutputPath,
    string RomCode,
    int OutputSize,
    IReadOnlyList<string> SkippedTitles,
    string SectorMap);
