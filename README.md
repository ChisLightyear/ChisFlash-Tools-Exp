# ChisFlash-Tools-Exp

ChisFlash 系列 GBA/GB/GBC 烧录卡的工具集（实验/加强线）。本仓库包含两个配套项目，二者共享部分源码与资源。

本仓库提供程序源码与菜单内核，不含游戏、封面图库或外部中文字库。游戏 ROM 需自备；封面和背景图可选；**GBC 中文菜单必须自备 12px 字库**，不支持 8px。详见 [未包含的内容](#未包含的内容)。

当前源码：**上位机 2.040 / 合卡器 0.92.1**。[更新说明](docs/CHANGELOG-2.040-0.92.1.md)。GBC候选卡型按8/32 MiB NOR容量筛选，用户选型决定模式；见[选型规则](docs/GBC-CARTRIDGE-SELECTION.md)。MIX 已接入 NES / MD / SNES 合卡、目录回读与存档管理，共用非连续 SRAM bank 表。**只读写 GLB2 v2 / LNC2，不兼容旧格式；不以核心 Mapper 白名单限制合卡、BIN 导出或烧录。** 见 [统一存储协议](docs/MIX-V2-STORAGE-CONTRACT.md)。可合卡不等于实机运行验证；[MBC5 8M五槽](docs/MBC5-8M-FIVE-SLOTS.md)保留。

**English version: [English](#english)**

## 📖 教程

完整的使用教程（含编译与使用两部分）：

- **简体中文** → [TUTORIAL.zh-CN.md](TUTORIAL.zh-CN.md)
- **English** → [TUTORIAL.en.md](TUTORIAL.en.md)

## 目录结构

| 目录 | 说明 |
|---|---|
| `ChisFlashWaterClient/` | 上位机（烧录客户端）。通过 CDC ACM 自定义协议与卡带通信，负责扫描、合卡生成、ROM 导入、存档读写、菜单重建等。WinForms / .NET 9 |
| `GbaMultiMenuBuilder/` | 合卡器（Multicart Builder）。负责把多张 ROM 打包成合卡镜像、生成菜单、封面处理、NES/MD/SNES MIX v2 支持。WinForms / .NET 9 |

## 两者关系

上位机引用合卡器的 `Models/`、`Services/`、`Fc/`、`Md/`、`Snes/` 源码及共享界面组件，因此**两个目录必须放在同一父目录下**保持相对路径不变。单独移动或改名任一目录都会导致编译失败。

```
ChisFlash-Tools-Exp/
├── ChisFlashWaterClient/
└── GbaMultiMenuBuilder/
```

## 构建

需要 .NET 9 SDK（含 Windows Desktop 运行时）。

```bash
# 上位机
dotnet build ChisFlashWaterClient/ChisFlashWaterClient.csproj -c Release

# 合卡器
dotnet build GbaMultiMenuBuilder/GbaMultiMenuBuilder.csproj -c Release
```

## 未包含的内容

以下内容不在本仓库内，需自行准备：

| 项目 | 是否必需 | 说明 |
|---|---|---|
| **游戏 ROM**（`.gba` / `.zip`） | ✅ **必需** | 程序不含任何游戏，必须自备 |
| **封面图库**（`ChisFlashWaterClient/covers/`） | ⭕ 可选 | 约 534 MiB 的 GBA 游戏封面 PNG 集合（2289 张）+ `cover_index.csv`，单独分发。缺失时自动封面匹配不可用，其余功能正常 |
| **自定义背景图** | ⭕ 可选 | 用来自定义菜单背景，缺失用默认背景 |
| **GBC 中文字库** | 中文菜单必需 | 自备 12px 字库，放入工具的 `Assets/Fonts/`，重新打开工具后选择；英文菜单无需外部字库 |
| **菜单 ROM 资源**（`Assets/` 内 `*.gba` / `*.gb`） | ⚙️ 已随仓库提供 | 开箱即用的默认菜单；如需替换请参考各项目 README |

构建产物（`bin/`、`obj/`）、发布包（`publish_*`）、打包 zip 均由 `.gitignore` 排除。

## 许可与第三方

- `GbaMultiMenuBuilder/Fc/MesenNesDB.csv` 来自 Mesen2 项目，许可原文见 `Fc/Mesen-LICENSE`（GPL v3）。
- 外部字体需自备并遵守其许可，格式要求见 [字库说明](GbaMultiMenuBuilder/Assets/Fonts/README.md)。

---

<a id="english"></a>

# English — ChisFlash-Tools-Exp

Toolchain for ChisFlash GBA/GB/GBC flash cartridges (experimental / enhanced line). This repository contains two companion projects that share some source files and resources.

This repository includes program source and menu kernels. Games, cover libraries and external Chinese fonts are not included. Supply your own game ROMs; covers and backgrounds are optional. **Chinese GBC menus require a 12px font; 8px fonts are not supported.** See [Not included](#not-included).

Current source: **Client 2.040 / Builder 0.92.1**. [Changes](docs/CHANGELOG-2.040-0.92.1.md). GBC NOR capacity filters cartridge candidates; an explicit user choice selects the mode. See the [selection rules](docs/GBC-CARTRIDGE-SELECTION.md). MIX packs, reads and manages saves for NES / MD / SNES with one noncontiguous SRAM map. **GLB2 v2 / LNC2 only; no legacy migration. Core mapper whitelists do not gate packing, BIN export or programming.** See the [storage contract](docs/MIX-V2-STORAGE-CONTRACT.md). Packing is not hardware gameplay validation. The [MBC5 five-slot mode](docs/MBC5-8M-FIVE-SLOTS.md) remains separate.

**中文版：[中文](#chisflash-tools-exp)**

## 📖 Tutorials

Full tutorials (build and usage):

- **简体中文** → [TUTORIAL.zh-CN.md](TUTORIAL.zh-CN.md)
- **English** → [TUTORIAL.en.md](TUTORIAL.en.md)

## Repository layout

| Directory | Description |
|---|---|
| `ChisFlashWaterClient/` | Client (burning client). Talks to the cartridge over a custom CDC ACM protocol; handles scanning, multicart generation, ROM import, save read/write, menu rebuilding. WinForms / .NET 9 |
| `GbaMultiMenuBuilder/` | Builder (Multicart Builder). Packs multiple ROMs into a multicart image, generates menus, handles covers, and supports NES / MD / SNES MIX v2 images. WinForms / .NET 9 |

## Relationship between the two

The Client references the Builder's `Models/`, `Services/`, `Fc/` and `Md/` sources and shared UI components. Therefore **both directories must stay under the same parent directory** so the relative paths remain valid. Moving or renaming either directory will break the build.

```
ChisFlash-Tools-Exp/
├── ChisFlashWaterClient/
└── GbaMultiMenuBuilder/
```

## Build

Requires the .NET 9 SDK (including the Windows Desktop runtime).

```bash
# Client
dotnet build ChisFlashWaterClient/ChisFlashWaterClient.csproj -c Release

# Builder
dotnet build GbaMultiMenuBuilder/GbaMultiMenuBuilder.csproj -c Release
```

## Not included

The following are not part of this repository and must be prepared separately:

| Item | Required | Notes |
|---|---|---|
| **Game ROMs** (`.gba` / `.zip`) | ✅ **Required** | The programs contain no games; bring your own |
| **Cover library** (`ChisFlashWaterClient/covers/`) | ⭕ Optional | ~534 MiB of GBA cover PNGs (2289 images) + `cover_index.csv`, distributed separately. Without it, automatic cover matching is unavailable; everything else works normally |
| **Custom background images** | ⭕ Optional | Used to customize the menu background; a default is used when absent |
| **GBC Chinese font** | Required for Chinese menus | Supply a 12px font in the tool's `Assets/Fonts/` directory, reopen the tool and select it; English menus need no external font |
| **Menu ROM resources** (`*.gba` / `*.gb` under `Assets/`) | ⚙️ Included | Ready-to-use default menus; see each project's README to replace them |

Build outputs (`bin/`, `obj/`), publish output (`publish_*`), and packaged zips are all excluded by `.gitignore`.

## License and third parties

- `GbaMultiMenuBuilder/Fc/MesenNesDB.csv` comes from the Mesen2 project; the license text is in `Fc/Mesen-LICENSE` (GPL v3).
- Supply external fonts under their respective licences; see [font requirements](GbaMultiMenuBuilder/Assets/Fonts/README.md).
