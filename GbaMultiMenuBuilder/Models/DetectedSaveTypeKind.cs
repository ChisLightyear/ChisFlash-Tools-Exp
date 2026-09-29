namespace GbaMultiMenuBuilder.Models;

public enum DetectedSaveTypeKind
{
    Unknown = 0,
    None = 1,
    Sram = 2,
    Eeprom = 3,
    Flash = 4,
    Flash512 = 5,
    Flash1M = 6,
}
