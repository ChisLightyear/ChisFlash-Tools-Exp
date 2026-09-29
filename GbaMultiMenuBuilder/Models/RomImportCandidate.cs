namespace GbaMultiMenuBuilder.Models;

public sealed record RomImportCandidate(
    string SourcePath,
    string RomPath,
    string Title,
    string DisplayName,
    string? ArchiveEntryPath,
    long EstimatedSizeBytes);
