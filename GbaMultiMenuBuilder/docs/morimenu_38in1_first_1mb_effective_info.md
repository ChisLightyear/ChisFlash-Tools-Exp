# MoriMenu-38in1.gbc 前 1MB 有效信息总表

目标文件：`bin\Debug\net9.0-windows\output\MoriMenu-38in1.gbc`

说明：下面把前 `1MB` 里有意义的内容全部汇总成一张表，不再分段；大段纯 `0xFF` 空白只用一行说明，不逐字节展开。

| 地址 | 内容 | 说明 |
|---|---|---|
| `0x000020 ~ 0x003047` | 菜单代码/数据有效区 | 菜单本体真正有内容的范围 |
| `0x000100` | `18 55 FF FF CE ED 66 66 CC 0D 00 0B 03 73 00 83` | 菜单头入口 + Nintendo Logo 开始 |
| `0x000104` | `CE ED 66 66 CC 0D 00 0B 03 73 00 83 00 0C 00 0D` | Nintendo Logo |
| `0x000134` | `00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00` | 菜单 ROM 头标题为空 |
| `0x000147` | `00` | 菜单头 `cartridge type = 0x00` |
| `0x000148` | `00` | 菜单头 `rom size code = 0x00` |
| `0x000149` | `00` | 菜单头 `ram size code = 0x00` |
| `0x00014D` | `53` | 菜单头校验值 |
| `0x003048 ~ 0x01FFFF` | 空白/保留 | 当前没有有效内容，跳过 |
| `0x020000 ~ 0x02001F` | `MBCXCFG` 有效区 | 固定配置头实际写入范围 |
| `0x020000` | `4D 42 43 58 43 46 47 FF` | Magic = `MBCXCFG` |
| `0x020008` | `01 00` | `version = 1` |
| `0x02000A` | `01` | `cart_save_mode = 1`，即 `CART_SAVE_BATTERYLESS` |
| `0x02000B` | `01` | `ui_language = 1`，当前是中文 |
| `0x02000C` | `08` | `font_size_px = 8` |
| `0x02000E` | `26 00` | `rom_count = 38` |
| `0x020010` | `00 00 04 00` | `dir_offset = 0x040000` |
| `0x020014` | `00 00 08 00` | `dir_length = 0x080000` |
| `0x020018` | `00 00 0C 00` | `font_offset = 0x0C0000` |
| `0x02001C` | `00 00 04 00` | `font_length = 0x040000` |
| `0x020020 ~ 0x03FFFF` | 空白/保留 | 配置头之后当前没有有效内容 |
| `0x040000 ~ 0x040017` | `MBCXROM` 有效区 | 固定目录头实际写入范围 |
| `0x040000` | `4D 42 43 58 52 4F 4D FF` | Magic = `MBCXROM` |
| `0x040008` | `01 00` | `version = 1` |
| `0x04000A` | `26 00` | `entry_count = 38` |
| `0x04000C` | `80 00` | `entry_size = 128` |
| `0x040010` | `40 00 04 00` | `entry_base = 0x040040` |
| `0x040014` | `C0 FF 07 00` | `entry_region_length = 0x07FFC0` |
| `0x040040` | `idx=0, name="G&W??2", rom_bank=64, bank_count=32, ram_banks=1, mbc=0x03, battery=1, sram_map=0` | 目录项 0 |
| `0x0400C0` | `idx=1, name="NBA JAM???", rom_bank=96, bank_count=32, ram_banks=0, mbc=0x01, battery=0, sram_map=-` | 目录项 1 |
| `0x040140` | `idx=2, name="SD???? ???????", rom_bank=128, bank_count=32, ram_banks=1, mbc=0x03, battery=1, sram_map=1` | 目录项 2 |
| `0x0401C0` | `idx=3, name="???", rom_bank=160, bank_count=32, ram_banks=1, mbc=0x03, battery=1, sram_map=2` | 目录项 3 |
| `0x040240` | `idx=4, name="?????", rom_bank=192, bank_count=32, ram_banks=1, mbc=0x03, battery=1, sram_map=3` | 目录项 4 |
| `0x0402C0` | `idx=5, name="????????????", rom_bank=224, bank_count=32, ram_banks=0, mbc=0x01, battery=0, sram_map=-` | 目录项 5 |
| `0x040340` | `idx=6, name="?1????????", rom_bank=256, bank_count=32, ram_banks=1, mbc=0x03, battery=1, sram_map=4` | 目录项 6 |
| `0x0403C0` | `idx=7, name="????2", rom_bank=288, bank_count=32, ram_banks=0, mbc=0x01, battery=0, sram_map=-` | 目录项 7 |
| `0x040440` | `idx=8, name="????", rom_bank=320, bank_count=32, ram_banks=1, mbc=0x03, battery=1, sram_map=5` | 目录项 8 |
| `0x0404C0` | `idx=9, name="??Z ?????", rom_bank=352, bank_count=32, ram_banks=1, mbc=0x03, battery=1, sram_map=6` | 目录项 9 |
| `0x040540` | `idx=10, name="???4", rom_bank=384, bank_count=32, ram_banks=0, mbc=0x01, battery=0, sram_map=-` | 目录项 10 |
| `0x0405C0` | `idx=11, name="???5", rom_bank=416, bank_count=32, ram_banks=0, mbc=0x01, battery=0, sram_map=-` | 目录项 11 |
| `0x040640` | `idx=12, name="?????? ?????2", rom_bank=448, bank_count=32, ram_banks=1, mbc=0x1B, battery=1, sram_map=7` | 目录项 12 |
| `0x0406C0` | `idx=13, name="?? ???", rom_bank=480, bank_count=32, ram_banks=0, mbc=0x01, battery=0, sram_map=-` | 目录项 13 |
| `0x040740` | `idx=14, name="?? ????2", rom_bank=512, bank_count=32, ram_banks=0, mbc=0x01, battery=0, sram_map=-` | 目录项 14 |
| `0x0407C0` | `idx=15, name="?? ???????", rom_bank=544, bank_count=32, ram_banks=0, mbc=0x01, battery=0, sram_map=-` | 目录项 15 |
| `0x040840` | `idx=16, name="?? ??95", rom_bank=576, bank_count=32, ram_banks=0, mbc=0x01, battery=0, sram_map=-` | 目录项 16 |
| `0x0408C0` | `idx=17, name="?? ??96", rom_bank=608, bank_count=32, ram_banks=0, mbc=0x01, battery=0, sram_map=-` | 目录项 17 |
| `0x040940` | `idx=18, name="???3", rom_bank=640, bank_count=8, ram_banks=0, mbc=0x01, battery=0, sram_map=-` | 目录项 18 |
| `0x0409C0` | `idx=19, name="?????2", rom_bank=648, bank_count=8, ram_banks=0, mbc=0x01, battery=0, sram_map=-` | 目录项 19 |
| `0x040A40` | `idx=20, name="???? TRAX", rom_bank=656, bank_count=8, ram_banks=0, mbc=0x01, battery=0, sram_map=-` | 目录项 20 |
| `0x040AC0` | `idx=21, name="????1", rom_bank=664, bank_count=8, ram_banks=0, mbc=0x01, battery=0, sram_map=-` | 目录项 21 |
| `0x040B40` | `idx=22, name="????2", rom_bank=672, bank_count=8, ram_banks=0, mbc=0x01, battery=0, sram_map=-` | 目录项 22 |
| `0x040BC0` | `idx=23, name="?????1", rom_bank=680, bank_count=8, ram_banks=0, mbc=0x01, battery=0, sram_map=-` | 目录项 23 |
| `0x040C40` | `idx=24, name="??????", rom_bank=688, bank_count=8, ram_banks=0, mbc=0x01, battery=0, sram_map=-` | 目录项 24 |
| `0x040CC0` | `idx=25, name="?????", rom_bank=696, bank_count=8, ram_banks=0, mbc=0x01, battery=0, sram_map=-` | 目录项 25 |
| `0x040D40` | `idx=26, name="???2", rom_bank=704, bank_count=8, ram_banks=0, mbc=0x01, battery=0, sram_map=-` | 目录项 26 |
| `0x040DC0` | `idx=27, name="G&W??1", rom_bank=712, bank_count=16, ram_banks=1, mbc=0x03, battery=1, sram_map=8` | 目录项 27 |
| `0x040E40` | `idx=28, name="V-????", rom_bank=728, bank_count=16, ram_banks=0, mbc=0x01, battery=0, sram_map=-` | 目录项 28 |
| `0x040EC0` | `idx=29, name="??????????2", rom_bank=744, bank_count=16, ram_banks=1, mbc=0x06, battery=1, sram_map=9` | 目录项 29 |
| `0x040F40` | `idx=30, name="??????????", rom_bank=760, bank_count=16, ram_banks=0, mbc=0x01, battery=0, sram_map=-` | 目录项 30 |
| `0x040FC0` | `idx=31, name="???3", rom_bank=776, bank_count=16, ram_banks=0, mbc=0x01, battery=0, sram_map=-` | 目录项 31 |
| `0x041040` | `idx=32, name="????? ????", rom_bank=792, bank_count=16, ram_banks=0, mbc=0x01, battery=0, sram_map=-` | 目录项 32 |
| `0x0410C0` | `idx=33, name="????", rom_bank=808, bank_count=16, ram_banks=0, mbc=0x01, battery=0, sram_map=-` | 目录项 33 |
| `0x041140` | `idx=34, name="???1", rom_bank=824, bank_count=16, ram_banks=0, mbc=0x01, battery=0, sram_map=-` | 目录项 34 |
| `0x0411C0` | `idx=35, name="???2", rom_bank=840, bank_count=16, ram_banks=0, mbc=0x01, battery=0, sram_map=-` | 目录项 35 |
| `0x041240` | `idx=36, name="???3", rom_bank=856, bank_count=16, ram_banks=0, mbc=0x01, battery=0, sram_map=-` | 目录项 36 |
| `0x0412C0` | `idx=37, name="???2", rom_bank=872, bank_count=8, ram_banks=0, mbc=0x01, battery=0, sram_map=-` | 目录项 37 |
| `0x0412D6 ~ 0x0BFFFF` | 空白/保留 | 当前目录项之后没有有效内容 |
| `0x0C0000 ~ 0x0FFFFF` | 空白/保留 | 字体/资源预留区当前是空的 |
| `0x100000` 之后 | 游戏 ROM 正文区 | 前 1MB 预留区结束，真正的游戏内容从这里开始 |

