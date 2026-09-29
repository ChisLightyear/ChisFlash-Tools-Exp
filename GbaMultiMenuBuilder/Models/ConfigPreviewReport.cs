namespace GbaMultiMenuBuilder.Models;

public sealed record ConfigPreviewReport(
    string Title,
    string Summary,
    IReadOnlyList<ConfigPreviewSection> Sections);

public sealed record ConfigPreviewSection(
    string Name,
    int Offset,
    byte[] Data,
    string Details);
