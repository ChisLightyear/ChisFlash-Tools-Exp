using System.Diagnostics;
using System.Text.Json;
using GbaMultiMenuBuilder.Models;

namespace GbaMultiMenuBuilder.Services;

public static class RomMetadataWorkerService
{
    private const string WorkerArgument = "--rom-metadata-worker";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static bool TryRun(string[] args)
    {
        if (args.Length < 2 || !string.Equals(args[0], WorkerArgument, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var romPath = args[1];
            var entry = RomMetadataCacheService.Resolve(romPath);
            var response = new WorkerResponse(
                entry.RomPath,
                entry.FileName,
                entry.FileLength,
                entry.LastWriteUtcTicks,
                entry.Map256m,
                entry.SaveTypeKind,
                entry.SaveTypeIdentifier,
                entry.SramPatchTargetIdentifier,
                entry.SramAlreadyPatched,
                entry.BatterylessAlreadyPatched,
                entry.BatterylessHasPayloadSpace,
                entry.BatterylessCanExpand);

            Console.Out.Write(JsonSerializer.Serialize(response, JsonOptions));
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.Write(ex.ToString());
            Environment.ExitCode = 1;
            return true;
        }
    }

    public static async Task<RomMetadataCacheEntry> ResolveAsync(string romPath, CancellationToken cancellationToken = default)
    {
        if (RomMetadataCacheService.TryResolveCached(romPath, out var cachedEntry))
        {
            return cachedEntry;
        }

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
        {
            return RomMetadataCacheService.Resolve(romPath);
        }

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(processPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        process.StartInfo.ArgumentList.Add(WorkerArgument);
        process.StartInfo.ArgumentList.Add(romPath);

        try
        {
            process.Start();
            TrySetWorkerPriority(process);

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync(cancellationToken);
            var output = await outputTask;
            var error = await errorTask;

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                    ? $"Metadata worker exited with code {process.ExitCode}."
                    : error.Trim());
            }

            var response = JsonSerializer.Deserialize<WorkerResponse>(output, JsonOptions);
            if (response is null)
            {
                throw new InvalidOperationException("Metadata worker returned an empty response.");
            }

            return new RomMetadataCacheEntry(
                string.IsNullOrWhiteSpace(response.RomPath) ? romPath : response.RomPath,
                response.FileName ?? Path.GetFileName(romPath),
                response.FileLength,
                response.LastWriteUtcTicks,
                response.Map256m,
                response.SaveTypeKind,
                response.SaveTypeIdentifier ?? string.Empty,
                response.SramPatchTargetIdentifier ?? string.Empty,
                response.SramAlreadyPatched,
                response.BatterylessAlreadyPatched,
                response.BatterylessHasPayloadSpace,
                response.BatterylessCanExpand);
        }
        catch
        {
            return RomMetadataCacheService.Resolve(romPath);
        }
    }

    private static void TrySetWorkerPriority(Process process)
    {
        try
        {
            process.PriorityClass = ProcessPriorityClass.Idle;
        }
        catch
        {
        }
    }

    private sealed record WorkerResponse(
        string RomPath,
        string FileName,
        long FileLength,
        long LastWriteUtcTicks,
        bool Map256m,
        DetectedSaveTypeKind SaveTypeKind,
        string SaveTypeIdentifier,
        string SramPatchTargetIdentifier,
        bool SramAlreadyPatched,
        bool BatterylessAlreadyPatched,
        bool BatterylessHasPayloadSpace,
        bool BatterylessCanExpand);
}
