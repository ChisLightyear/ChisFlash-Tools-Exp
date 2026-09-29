using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using GbaMultiMenuBuilder.Models;

namespace GbaMultiMenuBuilder.Services;

public static class RomImportService
{
    private const string ImportedRomsDirectoryName = "imported_roms";

    public static bool SupportsImportFile(string filePath)
    {
        return SupportsImportFile(filePath, null);
    }

    public static bool SupportsImportFile(string filePath, CompilationFamily? family)
    {
        var extension = Path.GetExtension(filePath);
        if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IsSupportedRomExtension(extension, family) || extension.Equals(".nes",StringComparison.OrdinalIgnoreCase) || Md.MdCartridgePolicy.IsDiagnosticExtension(extension);
    }

    public static IReadOnlyList<ImportedRomFile> Import(string filePath)
    {
        var result = new List<ImportedRomFile>();
        foreach (var candidate in Preview(filePath))
        {
            Materialize(candidate);
            result.Add(new ImportedRomFile(candidate.RomPath, candidate.Title));
        }

        return result;
    }

    public static IReadOnlyList<RomImportCandidate> Preview(string filePath)
    {
        return Preview(filePath, null);
    }

    public static IReadOnlyList<RomImportCandidate> Preview(string filePath, CompilationFamily? family)
    {
        if (!File.Exists(filePath))
        {
            return [];
        }

        Md.MdCartridgePolicy.RejectImport(filePath);

        var extension = Path.GetExtension(filePath);
        if (IsSupportedRomExtension(extension, family))
        {
            var normalizedPath = Path.GetFullPath(filePath);
            return
            [
                new RomImportCandidate(
                    normalizedPath,
                    normalizedPath,
                    Path.GetFileNameWithoutExtension(normalizedPath),
                    Path.GetFileName(normalizedPath),
                    null,
                    new FileInfo(normalizedPath).Length)
            ];
        }

        if (!extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        return PreviewZip(filePath, family);
    }

    public static void Materialize(RomImportCandidate candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.ArchiveEntryPath))
        {
            if (!File.Exists(candidate.RomPath))
            {
                throw new FileNotFoundException("ROM file was not found.", candidate.RomPath);
            }

            return;
        }

        using var archive = ZipFile.OpenRead(candidate.SourcePath);
        var entry = archive.GetEntry(candidate.ArchiveEntryPath.Replace('\\', '/'))
            ?? archive.Entries.FirstOrDefault(item =>
                string.Equals(
                    item.FullName.Replace('\\', '/'),
                    candidate.ArchiveEntryPath.Replace('\\', '/'),
                    StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            throw new InvalidDataException($"ZIP entry was not found: {candidate.ArchiveEntryPath}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(candidate.RomPath)!);
        using var input = entry.Open();
        using var output = File.Create(candidate.RomPath);
        input.CopyTo(output);
    }

    private static IReadOnlyList<RomImportCandidate> PreviewZip(string zipPath, CompilationFamily? family)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var normalizedZipPath = Path.GetFullPath(zipPath);
        var previewResult = new List<RomImportCandidate>();

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name) ||
                !IsSupportedRomExtension(Path.GetExtension(entry.Name), family))
            {
                continue;
            }

            using (var input = entry.Open()) Md.MdCartridgePolicy.RejectImport(input, entry.Length, entry.Name);
            var outputPath = BuildImportedRomPath(zipPath, entry.FullName);
            previewResult.Add(new RomImportCandidate(
                normalizedZipPath,
                outputPath,
                Path.GetFileNameWithoutExtension(entry.Name),
                entry.Name,
                entry.FullName.Replace('\\', '/'),
                entry.Length));
        }

        return previewResult;
    }

    private static string BuildImportedRomPath(string zipPath, string entryFullName)
    {
        var hashInput = $"{Path.GetFullPath(zipPath)}|{entryFullName.Replace('\\', '/')}";
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(hashInput)));
        var safeTitle = SanitizeFileName(Path.GetFileNameWithoutExtension(entryFullName));
        if (string.IsNullOrWhiteSpace(safeTitle))
        {
            safeTitle = "rom";
        }

        if (safeTitle.Length > 40)
        {
            safeTitle = safeTitle[..40];
        }

        var extension = Path.GetExtension(entryFullName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".gba";
        }

        return Path.Combine(GetImportedRomsDirectory(), $"{hash}_{safeTitle}{extension}");
    }

    private static bool IsSupportedRomExtension(string extension, CompilationFamily? family)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        return family switch
        {
            CompilationFamily.Fc => extension.Equals(".nes", StringComparison.OrdinalIgnoreCase),
            CompilationFamily.Gbc =>
                extension.Equals(".gb", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".gbc", StringComparison.OrdinalIgnoreCase),
            CompilationFamily.Gba =>
                extension.Equals(".gba", StringComparison.OrdinalIgnoreCase),
            _ =>
                extension.Equals(".gba", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".gb", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".gbc", StringComparison.OrdinalIgnoreCase),
        };
    }

    private static string GetImportedRomsDirectory()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, ImportedRomsDirectoryName);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(fileName.Length);
        foreach (var character in fileName)
        {
            builder.Append(invalidChars.Contains(character) ? '_' : character);
        }

        return builder.ToString().Trim();
    }
}
