# FC metadata sources

Mesen2 source checkout: upstream Mesen2 repository, commit b9fa69ddc6d0a331fb103fdb5eef6904305703c2.
MesenNesDB.csv is an unmodified copy of UI/Dependencies/MesenNesDB.txt (database header dated 2022-07-21, based on NES 2.0 header DB / NesCartDB / Nestopia). Source license retained as Mesen-LICENSE (GPL v3).

The C# implementation is local. Its metadata decisions follow Core/NES/GameDatabase.cpp, Loaders/iNesLoader.cpp, NesHeader.cpp, BaseMapper.cpp and the supported mapper defaults. No emulator executable is launched. ROM CRC32 is evaluated only when the user imports/scans a ROM.

MBCX geometry comes from the existing GameBoyRomHeaderService (16 KiB ROM banks, 8 KiB SRAM banks) and the MBC5 menu RTL (MBC5menu.v). SRAM 0..59 dedicated / 60..63 shared follows the existing desktop MAX/MBCX allocator.
