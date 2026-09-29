using System.Buffers.Binary;
using System.Text;
using GbaMultiMenuBuilder.Models;

namespace GbaMultiMenuBuilder.Services;

public sealed class BatterylessPatchService
{
    public const int MinimumPatchedRomSize = 0x400000;
    public const int ExpansionSize = 0x80000;

    private const int MaxRomSize = 0x02000000;
    private const int AlignmentSize = 0x40000;
    private const int AutoDetectPayloadHeaderWordCount = 8;
    private const int ThumbThunkPatchLength = 8;
    private const int ArmThunkPatchLength = 12;
    private static readonly byte[] PrimaryMarker = Encoding.ASCII.GetBytes("<3 from Maniac");
    private static readonly byte[] LegacyMarker = Encoding.ASCII.GetBytes("Batteryless mod by Lesserkuma");
    private static readonly byte[] ThumbBranchThunk = [0x00, 0x4B, 0x18, 0x47];
    private static readonly byte[] ArmBranchThunk = [0x00, 0x30, 0x9F, 0xE5, 0x13, 0xFF, 0x2F, 0xE1];
    private static readonly byte[] OldIrqAddress = [0xFC, 0x7F, 0x00, 0x03];
    private static readonly byte[] NewIrqAddress = [0xF4, 0x7F, 0x00, 0x03];
    private static readonly byte[] WriteSramSignature = [0x30, 0xB5, 0x05, 0x1C, 0x0C, 0x1C, 0x13, 0x1C, 0x0B, 0x4A, 0x10, 0x88, 0x0B, 0x49, 0x08, 0x40];
    private static readonly byte[] WriteSram2Signature = [0x80, 0xB5, 0x83, 0xB0, 0x6F, 0x46, 0x38, 0x60, 0x79, 0x60, 0xBA, 0x60, 0x09, 0x48, 0x09, 0x49];
    private static readonly byte[] WriteSramRamSignature = [0x04, 0xC0, 0x90, 0xE4, 0x01, 0xC0, 0xC1, 0xE4, 0x2C, 0xC4, 0xA0, 0xE1, 0x01, 0xC0, 0xC1, 0xE4];
    private static readonly byte[] WriteEepromSignature = [0x70, 0xB5, 0x00, 0x04, 0x0A, 0x1C, 0x40, 0x0B, 0xE0, 0x21, 0x09, 0x05, 0x41, 0x18, 0x07, 0x31, 0x00, 0x23, 0x10, 0x78];
    private static readonly byte[] WriteFlashSignature = [0x70, 0xB5, 0x00, 0x03, 0x0A, 0x1C, 0xE0, 0x21, 0x09, 0x05, 0x41, 0x18, 0x01, 0x23, 0x1B, 0x03];
    private static readonly byte[] WriteFlash2Signature = [0x7C, 0xB5, 0x90, 0xB0, 0x00, 0x03, 0x0A, 0x1C, 0xE0, 0x21, 0x09, 0x05, 0x09, 0x18, 0x01, 0x23];
    private static readonly byte[] WriteFlash3Signature = [0xF0, 0xB5, 0x90, 0xB0, 0x0F, 0x1C, 0x00, 0x04, 0x04, 0x0C, 0x03, 0x48, 0x00, 0x68, 0x40, 0x89];
    private static readonly byte[] Flash1mChipEraseSignature = [0x0E, 0x21, 0x09, 0x06, 0xFF, 0x24, 0x80, 0x22, 0x13, 0x4B, 0x52, 0x02, 0x01, 0x3A, 0x8C, 0x54, 0xFC, 0xD1];
    private static readonly byte[] Flash1mSectorEraseV102Signature = [0x13, 0x49, 0xFF, 0x25, 0x08, 0x22, 0x00, 0x00, 0x52, 0x02, 0x01, 0x3A, 0xA5, 0x54, 0xFC, 0xD1];
    private static readonly byte[] Flash1mSectorEraseV103Signature = [0x14, 0x49, 0xFF, 0x25, 0x08, 0x22, 0x00, 0x00, 0x52, 0x02, 0x01, 0x3A, 0xA5, 0x54, 0xFC, 0xD1];
    private static readonly byte[] Flash1mProgramByteSignature = [0x0C, 0x4A, 0xAA, 0x20, 0x00, 0x00, 0x0B, 0x49, 0x55, 0x20, 0x00, 0x00, 0xA0, 0x20, 0x00, 0x00, 0x27, 0x70, 0x09, 0x48];
    private static readonly byte[] Flash1mCopyByteSignature = [0x0A, 0x4C, 0xAA, 0x22, 0x00, 0x00, 0x09, 0x4B, 0x55, 0x22, 0x00, 0x00, 0xA0, 0x22, 0x00, 0x00, 0x02, 0x78, 0x0A, 0x70];
    private static readonly byte[] WriteEepromV11EpiloguePatch = [0x07, 0x49, 0x08, 0x47];
    private static readonly byte[] WriteEepromV111Signature = [0x0A, 0x88, 0x80, 0x21, 0x09, 0x06, 0x0A, 0x43, 0x02, 0x60, 0x07, 0x48, 0x00, 0x47, 0x00, 0x00];
    private static readonly Lazy<PayloadInfo> AutoDetectPayload = new(LoadAutoDetectPayload);

    public bool HasKnownMarker(ReadOnlySpan<byte> romData) =>
        FindPattern(romData, PrimaryMarker) >= 0 || FindPattern(romData, LegacyMarker) >= 0;

    public BatterylessPatchInspection Analyze(ReadOnlySpan<byte> romData)
    {
        if (romData.Length > MaxRomSize)
        {
            return new BatterylessPatchInspection(false, false, false);
        }

        var payloadLength = AutoDetectPayload.Value.Bytes.Length;
        var romSize = AlignToNextBoundary(romData.Length, AlignmentSize);
        var working = GC.AllocateUninitializedArray<byte>(romSize);
        Array.Fill(working, (byte)0xFF);
        romData.CopyTo(working);

        var hasPayloadSpace = FindPayloadBase(working, romSize, payloadLength) >= 0;
        var canExpand = romSize + ExpansionSize <= MaxRomSize;
        return new BatterylessPatchInspection(
            HasKnownMarker(romData),
            hasPayloadSpace,
            canExpand);
    }

    public BatterylessPatchResult TryApply(
        byte[] romData,
        CartridgeDefinition cartridge)
    {
        ArgumentNullException.ThrowIfNull(romData);
        ArgumentNullException.ThrowIfNull(cartridge);

        var inspection = Analyze(romData);
        if (inspection.AlreadyPatched)
        {
            return new BatterylessPatchResult(romData, false, true, null);
        }

        if (romData.Length > MaxRomSize)
        {
            throw new InvalidOperationException("ROM too large to patch for batteryless saving.");
        }

        var romSize = AlignToNextBoundary(romData.Length, AlignmentSize);
        var working = GC.AllocateUninitializedArray<byte>(romSize);
        Array.Fill(working, (byte)0xFF);
        Buffer.BlockCopy(romData, 0, working, 0, romData.Length);
        var flashProfile = ResolveFlashProfile(cartridge);
        var payload = AutoDetectPayload.Value;

        var foundIrqReference = false;
        var irqReferencePatches = new List<IrqPatchLocation>();
        for (var offset = 0; offset <= romSize - OldIrqAddress.Length; offset += 4)
        {
            if (!Matches(working, offset, OldIrqAddress))
            {
                continue;
            }

            foundIrqReference = true;
            var beforeBytes = CaptureBytesHex(working, offset, OldIrqAddress.Length);
            CopyBytes(working, offset, NewIrqAddress);
            irqReferencePatches.Add(new IrqPatchLocation(offset, beforeBytes, CaptureBytesHex(working, offset, NewIrqAddress.Length)));
        }

        if (!foundIrqReference)
        {
            throw new InvalidOperationException("Could not find any reference to the IRQ handler address.");
        }

        var payloadBase = FindPayloadBase(working, romSize, payload.Bytes.Length);
        if (payloadBase < 0)
        {
            if (!inspection.CanExpand)
            {
                throw new InvalidOperationException("No free space was found for the batteryless payload, and the ROM cannot be expanded further.");
            }

            var expandedSize = romSize + ExpansionSize;
            Array.Resize(ref working, expandedSize);
            Array.Fill(working, (byte)0xFF, romSize, expandedSize - romSize);
            romSize = expandedSize;
            payloadBase = romSize - AlignmentSize - payload.Bytes.Length;
        }

        Buffer.BlockCopy(payload.Bytes, 0, working, payloadBase, payload.Bytes.Length);
        WriteUInt32(working, payloadBase + 4, 0);
        if (payload.SupportsFlashProfile)
        {
            WriteUInt32(working, payloadBase + 12, (uint)flashProfile);
        }

        if (working[3] != 0xEA)
        {
            throw new InvalidOperationException("Unexpected ROM entrypoint instruction.");
        }

        var originalEntrypointOffset = working[0] | (working[1] << 8) | (working[2] << 16);
        var originalEntrypointAddress = 0x08000000u + 8u + ((uint)originalEntrypointOffset << 2);
        WriteUInt32(working, payloadBase, originalEntrypointAddress);

        var newEntrypointAddress = 0x08000000u + (uint)payloadBase + payload.PatchedEntrypointOffset;
        WriteUInt32(working, 0, 0xEA000000u | ((newEntrypointAddress - 0x08000008u) >> 2));

        var foundWriteLocation = false;
        var configuredSaveSize = 0u;
        var hookPatches = new List<HookPatchLocation>();
        for (var offset = 0; offset < romSize - 64; offset += 2)
        {
            if (Matches(working, offset, WriteSramSignature))
            {
                foundWriteLocation = true;
                var beforeBytes = CaptureBytesHex(working, offset, ThumbThunkPatchLength);
                PatchThumbThunk(working, offset, 0x08000000u + (uint)payloadBase + payload.WriteSramPatchedOffset);
                hookPatches.Add(new HookPatchLocation("SRAM-Thumb", offset, beforeBytes, CaptureBytesHex(working, offset, ThumbThunkPatchLength)));
                configuredSaveSize = 0x8000;
                WriteUInt32(working, payloadBase + 8, configuredSaveSize);
            }

            if (Matches(working, offset, WriteSram2Signature))
            {
                foundWriteLocation = true;
                var beforeBytes = CaptureBytesHex(working, offset, ThumbThunkPatchLength);
                PatchThumbThunk(working, offset, 0x08000000u + (uint)payloadBase + payload.WriteSramPatchedOffset);
                hookPatches.Add(new HookPatchLocation("SRAM-Thumb2", offset, beforeBytes, CaptureBytesHex(working, offset, ThumbThunkPatchLength)));
                configuredSaveSize = 0x8000;
                WriteUInt32(working, payloadBase + 8, configuredSaveSize);
            }

            if (Matches(working, offset, WriteSramRamSignature))
            {
                foundWriteLocation = true;
                var beforeBytes = CaptureBytesHex(working, offset, ArmThunkPatchLength);
                PatchArmThunk(working, offset, 0x08000000u + (uint)payloadBase + payload.WriteSramPatchedOffset);
                hookPatches.Add(new HookPatchLocation("SRAM-ARM", offset, beforeBytes, CaptureBytesHex(working, offset, ArmThunkPatchLength)));
                configuredSaveSize = 0x8000;
                WriteUInt32(working, payloadBase + 8, configuredSaveSize);
            }

            if (Matches(working, offset, WriteEepromSignature))
            {
                foundWriteLocation = true;
                var beforeBytes = CaptureBytesHex(working, offset, ThumbThunkPatchLength);
                PatchThumbThunk(working, offset, 0x08000000u + (uint)payloadBase + payload.WriteEepromPatchedOffset);
                hookPatches.Add(new HookPatchLocation("EEPROM", offset, beforeBytes, CaptureBytesHex(working, offset, ThumbThunkPatchLength)));
                configuredSaveSize = 0x2000;
                WriteUInt32(working, payloadBase + 8, configuredSaveSize);
            }

            if (Matches(working, offset, WriteFlashSignature))
            {
                foundWriteLocation = true;
                var beforeBytes = CaptureBytesHex(working, offset, ThumbThunkPatchLength);
                PatchThumbThunk(working, offset, 0x08000000u + (uint)payloadBase + payload.WriteFlashPatchedOffset);
                hookPatches.Add(new HookPatchLocation("FLASH", offset, beforeBytes, CaptureBytesHex(working, offset, ThumbThunkPatchLength)));
                configuredSaveSize = 0x10000;
                WriteUInt32(working, payloadBase + 8, configuredSaveSize);
            }

            if (Matches(working, offset, WriteFlash2Signature))
            {
                foundWriteLocation = true;
                var beforeBytes = CaptureBytesHex(working, offset, ThumbThunkPatchLength);
                PatchThumbThunk(working, offset, 0x08000000u + (uint)payloadBase + payload.WriteFlashPatchedOffset);
                hookPatches.Add(new HookPatchLocation("FLASH2", offset, beforeBytes, CaptureBytesHex(working, offset, ThumbThunkPatchLength)));
                configuredSaveSize = 0x10000;
                WriteUInt32(working, payloadBase + 8, configuredSaveSize);
            }

            if (Matches(working, offset, WriteFlash3Signature))
            {
                foundWriteLocation = true;
                var beforeBytes = CaptureBytesHex(working, offset, ThumbThunkPatchLength);
                PatchThumbThunk(working, offset, 0x08000000u + (uint)payloadBase + payload.WriteFlashPatchedOffset);
                hookPatches.Add(new HookPatchLocation("FLASH3", offset, beforeBytes, CaptureBytesHex(working, offset, ThumbThunkPatchLength)));
                configuredSaveSize = 0x20000;
                WriteUInt32(working, payloadBase + 8, configuredSaveSize);
            }

            if (payload.SupportsFlash1mHooks &&
                TryPatchFlash1mThumbFunction(working, offset, Flash1mChipEraseSignature, 0x08000000u + (uint)payloadBase + payload.WriteFlash1mChipErasePatchedOffset, "FLASH1M-ChipErase", out var hookPatch))
            {
                foundWriteLocation = true;
                hookPatches.Add(hookPatch!);
                configuredSaveSize = 0x20000;
                WriteUInt32(working, payloadBase + 8, configuredSaveSize);
            }

            if (payload.SupportsFlash1mHooks &&
                TryPatchFlash1mThumbFunction(working, offset, Flash1mSectorEraseV102Signature, 0x08000000u + (uint)payloadBase + payload.WriteFlash1mSectorErasePatchedOffset, "FLASH1M-SectorEraseV102", out hookPatch))
            {
                foundWriteLocation = true;
                hookPatches.Add(hookPatch!);
                configuredSaveSize = 0x20000;
                WriteUInt32(working, payloadBase + 8, configuredSaveSize);
            }

            if (payload.SupportsFlash1mHooks &&
                TryPatchFlash1mThumbFunction(working, offset, Flash1mSectorEraseV103Signature, 0x08000000u + (uint)payloadBase + payload.WriteFlash1mSectorErasePatchedOffset, "FLASH1M-SectorEraseV103", out hookPatch))
            {
                foundWriteLocation = true;
                hookPatches.Add(hookPatch!);
                configuredSaveSize = 0x20000;
                WriteUInt32(working, payloadBase + 8, configuredSaveSize);
            }

            if (payload.SupportsFlash1mHooks &&
                TryPatchFlash1mThumbFunction(working, offset, Flash1mProgramByteSignature, 0x08000000u + (uint)payloadBase + payload.WriteFlash1mProgramBytePatchedOffset, "FLASH1M-ProgramByte", out hookPatch))
            {
                foundWriteLocation = true;
                hookPatches.Add(hookPatch!);
                configuredSaveSize = 0x20000;
                WriteUInt32(working, payloadBase + 8, configuredSaveSize);
            }

            if (payload.SupportsFlash1mHooks &&
                TryPatchFlash1mThumbFunction(working, offset, Flash1mCopyByteSignature, 0x08000000u + (uint)payloadBase + payload.WriteFlash1mCopyBytePatchedOffset, "FLASH1M-CopyByte", out hookPatch))
            {
                foundWriteLocation = true;
                hookPatches.Add(hookPatch!);
                configuredSaveSize = 0x20000;
                WriteUInt32(working, payloadBase + 8, configuredSaveSize);
            }

            if (Matches(working, offset, WriteEepromV111Signature))
            {
                foundWriteLocation = true;
                var epilogueBefore = CaptureBytesHex(working, offset + 12, 8);
                CopyBytes(working, offset + 12, WriteEepromV11EpiloguePatch);
                hookPatches.Add(new HookPatchLocation("EEPROM-V111-Epilogue", offset + 12, epilogueBefore, CaptureBytesHex(working, offset + 12, 8)));
                var posthookBefore = CaptureBytesHex(working, offset + 44, 4);
                WriteUInt32(working, offset + 44, 0x08000000u + (uint)payloadBase + payload.WriteEepromV111PosthookOffset);
                hookPatches.Add(new HookPatchLocation("EEPROM-V111-PosthookPtr", offset + 44, posthookBefore, CaptureBytesHex(working, offset + 44, 4)));
                configuredSaveSize = 0x2000;
                WriteUInt32(working, payloadBase + 8, configuredSaveSize);
            }
        }

        if (!foundWriteLocation)
        {
            throw new InvalidOperationException("Could not find a save write function to hook. The ROM may not need the batteryless patch yet, or it still needs an SRAM patch first.");
        }

        var remainingOldIrqReferenceCount = 0;
        for (var offset = 0; offset <= romSize - OldIrqAddress.Length; offset += 4)
        {
            if (Matches(working, offset, OldIrqAddress))
            {
                remainingOldIrqReferenceCount++;
            }
        }

        return new BatterylessPatchResult(
            working,
            true,
            false,
            new BatterylessPatchDebugInfo(
                payloadBase,
                payload.Bytes.Length,
                irqReferencePatches,
                remainingOldIrqReferenceCount,
                configuredSaveSize,
                (uint)flashProfile,
                flashProfile.ToString(),
                payload.VariantName,
                hookPatches));
    }

    // Match the menu runtime families in GBA_MultiMenu_extended-main/source/flash.c:
    // type1 = 6600M0U0BE-like (FF/60/D0 + 0xEA/0x1FF buffer write)
    // type2 = MSP55LV100S/S29GL512-like (AAA9/5556 command family)
    // type3 = MSP54LV100-like (A9/56 command family)
    // type4 = S29GL01G/S29GL01GS/S29GL256S/MT28EW01G/S70GL02G-like (AA/55 command family)
    // legacy = older Lesserkuma type4 family (FF/60/D0 erase + EA/1FF 1 KiB buffer write)
    private static BatterylessFlashProfile ResolveFlashProfile(CartridgeDefinition cartridge) => cartridge.Id switch
    {
        2 => BatterylessFlashProfile.Flash1,
        4 => BatterylessFlashProfile.Flash1,
        1 => BatterylessFlashProfile.Flash2,
        6 => BatterylessFlashProfile.Flash2,
        3 => BatterylessFlashProfile.Flash3,
        CartridgeDefinition.WaterlineCompatibleId => BatterylessFlashProfile.Flash4,
        5 => BatterylessFlashProfile.Flash4,
        _ => throw new InvalidOperationException($"Unsupported flash profile for cartridge type: {cartridge.Name}"),
    };

    private static PayloadInfo LoadAutoDetectPayload() =>
        LoadPayload(
            "batteryless_payload_flash4_compat.bin",
            "maniac-auto-detect",
            AutoDetectPayloadHeaderWordCount,
            includeFlashProfile: false,
            includeFlash1mHooks: false);

    private static PayloadInfo LoadPayload(
        string assetFileName,
        string variantName,
        int headerWordCount,
        bool includeFlashProfile,
        bool includeFlash1mHooks)
    {
        var assemblyDirectory = Path.GetDirectoryName(typeof(BatterylessPatchService).Assembly.Location) ?? AppContext.BaseDirectory;
        var candidatePaths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", assetFileName),
            Path.Combine(AppContext.BaseDirectory, assetFileName),
            Path.Combine(assemblyDirectory, "Assets", assetFileName),
            Path.Combine(assemblyDirectory, assetFileName),
            Path.GetFullPath(Path.Combine(assemblyDirectory, "..", "..", "..", "..", "Assets", assetFileName)),
        };

        var payloadPath = candidatePaths.FirstOrDefault(File.Exists);
        if (payloadPath is null)
        {
            throw new InvalidOperationException($"{assetFileName} was not found.");
        }

        var payloadBytes = File.ReadAllBytes(payloadPath);
        if (payloadBytes.Length < headerWordCount * sizeof(uint))
        {
            throw new InvalidOperationException($"{assetFileName} is invalid.");
        }

        uint ReadHeader(int index) => BinaryPrimitives.ReadUInt32LittleEndian(payloadBytes.AsSpan(index * sizeof(uint), sizeof(uint)));

        return new PayloadInfo(
            variantName,
            payloadBytes,
            includeFlashProfile,
            includeFlash1mHooks,
            includeFlashProfile ? ReadHeader(4) : ReadHeader(3),
            includeFlashProfile ? ReadHeader(5) : ReadHeader(4),
            includeFlashProfile ? ReadHeader(6) : ReadHeader(5),
            includeFlashProfile ? ReadHeader(7) : ReadHeader(6),
            includeFlashProfile ? ReadHeader(8) : ReadHeader(7),
            includeFlash1mHooks ? ReadHeader(9) : 0,
            includeFlash1mHooks ? ReadHeader(10) : 0,
            includeFlash1mHooks ? ReadHeader(11) : 0,
            includeFlash1mHooks ? ReadHeader(12) : 0);
    }

    private static int AlignToNextBoundary(int value, int alignment)
    {
        if (value % alignment == 0)
        {
            return value;
        }

        return value + (alignment - (value % alignment));
    }

    private static int FindPayloadBase(ReadOnlySpan<byte> romData, int romSize, int payloadLength)
    {
        for (var payloadBase = romSize - AlignmentSize - payloadLength; payloadBase >= 0; payloadBase -= AlignmentSize)
        {
            var span = romData.Slice(payloadBase, AlignmentSize + payloadLength);
            if (IsAll(span, 0x00) || IsAll(span, 0xFF))
            {
                return payloadBase;
            }
        }

        return -1;
    }

    private static bool IsAll(ReadOnlySpan<byte> data, byte value)
    {
        for (var index = 0; index < data.Length; index++)
        {
            if (data[index] != value)
            {
                return false;
            }
        }

        return true;
    }

    private static int FindPattern(ReadOnlySpan<byte> data, ReadOnlySpan<byte> pattern)
    {
        if (pattern.Length == 0 || data.Length < pattern.Length)
        {
            return -1;
        }

        for (var start = 0; start <= data.Length - pattern.Length; start++)
        {
            if (data.Slice(start, pattern.Length).SequenceEqual(pattern))
            {
                return start;
            }
        }

        return -1;
    }

    private static bool Matches(ReadOnlySpan<byte> data, int offset, ReadOnlySpan<byte> pattern)
    {
        if (offset < 0 || offset + pattern.Length > data.Length)
        {
            return false;
        }

        return data.Slice(offset, pattern.Length).SequenceEqual(pattern);
    }

    private static void CopyBytes(Span<byte> destination, int offset, ReadOnlySpan<byte> data)
    {
        data.CopyTo(destination.Slice(offset, data.Length));
    }

    private static void PatchThumbThunk(Span<byte> romData, int offset, uint targetAddress)
    {
        CopyBytes(romData, offset, ThumbBranchThunk);
        WriteUInt32(romData, offset + ThumbBranchThunk.Length, targetAddress);
    }

    private static void PatchArmThunk(Span<byte> romData, int offset, uint targetAddress)
    {
        CopyBytes(romData, offset, ArmBranchThunk);
        WriteUInt32(romData, offset + ArmBranchThunk.Length, targetAddress);
    }

    private static bool TryPatchFlash1mThumbFunction(Span<byte> romData, int writeLocation, ReadOnlySpan<byte> signature, uint targetAddress, string hookName, out HookPatchLocation? hookPatch)
    {
        if (!Matches(romData, writeLocation, signature))
        {
            hookPatch = null;
            return false;
        }

        var functionStart = FindThumbFunctionStart(romData, writeLocation, 0x80);
        if (functionStart < 0)
        {
            hookPatch = null;
            return false;
        }

        var beforeBytes = CaptureBytesHex(romData, functionStart, ThumbThunkPatchLength);
        PatchThumbThunk(romData, functionStart, targetAddress);
        hookPatch = new HookPatchLocation(hookName, functionStart, beforeBytes, CaptureBytesHex(romData, functionStart, ThumbThunkPatchLength));
        return true;
    }

    private static int FindThumbFunctionStart(ReadOnlySpan<byte> romData, int location, int maxBacktrackBytes)
    {
        var bytesBacktracked = 0;
        while (location >= 2 && bytesBacktracked <= maxBacktrackBytes)
        {
            var halfword = romData[location] | (romData[location + 1] << 8);
            if ((halfword & 0xFF00) == 0xB500)
            {
                return location;
            }

            location -= 2;
            bytesBacktracked += 2;
        }

        return -1;
    }

    private static string CaptureBytesHex(ReadOnlySpan<byte> data, int offset, int length)
    {
        if (offset < 0 || offset >= data.Length || length <= 0)
        {
            return string.Empty;
        }

        var cappedLength = Math.Min(length, data.Length - offset);
        var bytes = new string[cappedLength];
        for (var index = 0; index < cappedLength; index++)
        {
            bytes[index] = $"{data[offset + index]:X2}";
        }

        return string.Join(' ', bytes);
    }

    private static void WriteUInt32(Span<byte> romData, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(romData.Slice(offset, sizeof(uint)), value);
    }

    public sealed record BatterylessPatchResult(
        byte[] RomData,
        bool Patched,
        bool AlreadyPatched,
        BatterylessPatchDebugInfo? DebugInfo);

    public sealed record BatterylessPatchDebugInfo(
        int PayloadBase,
        int PayloadLength,
        IReadOnlyList<IrqPatchLocation> IrqReferencePatches,
        int RemainingOldIrqReferenceCount,
        uint SaveSize,
        uint FlashProfile,
        string FlashProfileName,
        string PayloadVariantName,
        IReadOnlyList<HookPatchLocation> HookPatches);

    public sealed record IrqPatchLocation(int Offset, string BeforeBytes, string AfterBytes);

    public sealed record HookPatchLocation(string HookName, int Offset, string BeforeBytes, string AfterBytes);

    private enum BatterylessFlashProfile : uint
    {
        Flash1 = 1,
        Flash2 = 2,
        Flash3 = 3,
        Flash4 = 4,
        Legacy = 5,
    }

    private sealed record PayloadInfo(
        string VariantName,
        byte[] Bytes,
        bool SupportsFlashProfile,
        bool SupportsFlash1mHooks,
        uint PatchedEntrypointOffset,
        uint WriteSramPatchedOffset,
        uint WriteEepromPatchedOffset,
        uint WriteFlashPatchedOffset,
        uint WriteEepromV111PosthookOffset,
        uint WriteFlash1mChipErasePatchedOffset,
        uint WriteFlash1mSectorErasePatchedOffset,
        uint WriteFlash1mProgramBytePatchedOffset,
        uint WriteFlash1mCopyBytePatchedOffset);

}
