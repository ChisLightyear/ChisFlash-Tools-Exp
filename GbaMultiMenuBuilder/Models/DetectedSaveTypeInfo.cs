namespace GbaMultiMenuBuilder.Models;

public sealed record DetectedSaveTypeInfo(
    DetectedSaveTypeKind Kind,
    string? Identifier);
