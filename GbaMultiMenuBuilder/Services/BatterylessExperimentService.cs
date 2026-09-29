using GbaMultiMenuBuilder.Models;

namespace GbaMultiMenuBuilder.Services;

public static class BatterylessExperimentService
{
    private const string WorkerArgument = "--batteryless-experiment";

    public static bool TryRun(string[] args)
    {
        if (args.Length < 3 || !string.Equals(args[0], WorkerArgument, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            Run(args);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            Environment.ExitCode = 1;
            return true;
        }
    }

    private static void Run(string[] args)
    {
        var romPath = Path.GetFullPath(args[1]);
        if (!File.Exists(romPath))
        {
            throw new InvalidOperationException($"ROM not found: {romPath}");
        }

        var cartridge = ResolveCartridge(args[2]);
        var outputDirectory = args.Length >= 4
            ? Path.GetFullPath(args[3])
            : Path.Combine(Path.GetDirectoryName(romPath) ?? AppContext.BaseDirectory, "batteryless_experiments");
        Directory.CreateDirectory(outputDirectory);

        var sramPatchService = new SramPatchService();
        var batterylessPatchService = new BatterylessPatchService();
        var inputRomData = File.ReadAllBytes(romPath);

        if (batterylessPatchService.HasKnownMarker(inputRomData))
        {
            throw new InvalidOperationException(
                "Input ROM already contains a batteryless payload marker. Use an unpatched ROM or an SRAM-only ROM as the experiment base.");
        }

        byte[] sramReadyRomData = inputRomData;
        string sramStatus;
        var appliedSramPatch = sramPatchService.DetectAppliedPatchTarget(inputRomData);
        if (!string.IsNullOrWhiteSpace(appliedSramPatch))
        {
            sramStatus = $"already SRAM patched ({appliedSramPatch})";
        }
        else
        {
            var sramPatchResult = sramPatchService.TryApply(inputRomData);
            if (!sramPatchResult.Patched)
            {
                throw new InvalidOperationException(
                    "Could not auto-apply an SRAM patch to the input ROM. Use a ROM that still needs SRAM patching, or provide an SRAM-patched base ROM.");
            }

            sramReadyRomData = sramPatchResult.RomData;
            sramStatus = $"applied SRAM patch ({sramPatchResult.PatchSetName})";
        }

        Console.WriteLine($"Input ROM : {romPath}");
        Console.WriteLine($"Cartridge : {cartridge.Name} (id={cartridge.Id})");
        Console.WriteLine($"Output Dir: {outputDirectory}");
        Console.WriteLine($"SRAM Base : {sramStatus}");
        var result = batterylessPatchService.TryApply(sramReadyRomData, cartridge);
        if (!result.Patched || result.DebugInfo is null)
        {
            throw new InvalidOperationException("Integrated batteryless payload did not produce a patched ROM.");
        }

        var outputName = $"{Path.GetFileNameWithoutExtension(romPath)}__compat-auto-detect.gba";
        var outputPath = Path.Combine(outputDirectory, outputName);
        File.WriteAllBytes(outputPath, result.RomData);

        Console.WriteLine();
        Console.WriteLine("compat-auto-detect");
        Console.WriteLine("  Description : Integrated Maniac-family auto-detect payload.");
        Console.WriteLine($"  Output      : {outputPath}");
        Console.WriteLine($"  FlashProfile: {result.DebugInfo.FlashProfileName} ({result.DebugInfo.FlashProfile})");
        Console.WriteLine($"  Payload     : {result.DebugInfo.PayloadVariantName} @ 0x{result.DebugInfo.PayloadBase:X8}");
        Console.WriteLine($"  SaveSize    : 0x{result.DebugInfo.SaveSize:X}");
    }

    private static CartridgeDefinition ResolveCartridge(string token)
    {
        if (int.TryParse(token, out var id))
        {
            return CartridgeDefinition.All.FirstOrDefault(cartridge => cartridge.Id == id)
                ?? throw new InvalidOperationException($"Unknown cartridge id: {id}");
        }

        return CartridgeDefinition.All.FirstOrDefault(
                   cartridge => string.Equals(cartridge.Name, token, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException($"Unknown cartridge name: {token}");
    }
}
