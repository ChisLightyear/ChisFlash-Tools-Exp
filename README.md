# ChisFlash-Tools-Exp

ChisFlash 系列 GBA/GB/GBC 烧录卡的工具集（实验/加强线）。本仓库包含两个配套项目，二者共享部分源码与资源。

⚠️ **本仓库只包含程序代码，不含游戏、封面等素材。** 使用前请自备 **游戏 ROM**（必需）；封面、背景图、中文字库均为可选，缺失不影响构建与烧录。详见 [未包含的内容](#未包含的内容)。

**English version: [English](#english)**

## 📖 教程

完整的使用教程（含编译与使用两部分）：

- **简体中文** → [TUTORIAL.zh-CN.md](TUTORIAL.zh-CN.md)
- **English** → [TUTORIAL.en.md](TUTORIAL.en.md)

## 目录结构

| 目录 | 说明 |
|---|---|
| `ChisFlashWaterClient/` | 上位机（烧录客户端）。通过 CDC ACM 自定义协议与卡带通信，负责扫描、合卡生成、ROM 导入、存档读写、菜单重建等。WinForms / .NET 9 |
| `GbaMultiMenuBuilder/` | 合卡器（Multicart Builder）。负责把多张 ROM 打包成合卡镜像、生成菜单、封面处理、FC(NES)/MD 卡带支持。WinForms / .NET 9 |

## 两者关系

上位机通过 `<Compile Include="..\GbaMultiMenuBuilder\...">` 引用合卡器的 `Models/`、`Services/`、`Fc/`、`Md/` 源码与字体资源（共 **29 条**跨目录引用：Models 12 / Services 13 / Fc 2 / Md 1 / Assets 1），因此**两个目录必须放在同一父目录下**保持相对路径不变。单独移动或改名任一目录都会导致编译失败。

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
| **GBC 中文字库** | ⭕ 可选 | 仅 GBC 中文菜单需要 |
| **菜单 ROM 资源**（`Assets/` 内 `*.gba` / `*.gb`） | ⚙️ 已随仓库提供 | 开箱即用的默认菜单；如需替换请参考各项目 README |

构建产物（`bin/`、`obj/`）、发布包（`publish_*`）、打包 zip 均由 `.gitignore` 排除。

## 许可与第三方

- `GbaMultiMenuBuilder/Fc/MesenNesDB.csv` 来自 Mesen2 项目，许可原文见 `Fc/Mesen-LICENSE`（GPL v3）。
- 字体资源版权归各自作者所有，详见 `Assets/Fonts/`。

---

<a id="english"></a>

# English — ChisFlash-Tools-Exp

Toolchain for ChisFlash GBA/GB/GBC flash cartridges (experimental / enhanced line). This repository contains two companion projects that share some source files and resources.

⚠️ **This repository contains program code only — no games, covers, or other assets.** Bring your own **game ROMs** (required). Covers, background images, and the Chinese font are optional; missing ones do not affect building or flashing. See [Not included](#not-included).

**中文版：[中文](#chisflash-tools-exp)**

## 📖 Tutorials

Full tutorials (build and usage):

- **简体中文** → [TUTORIAL.zh-CN.md](TUTORIAL.zh-CN.md)
- **English** → [TUTORIAL.en.md](TUTORIAL.en.md)

## Repository layout

| Directory | Description |
|---|---|
| `ChisFlashWaterClient/` | Client (burning client). Talks to the cartridge over a custom CDC ACM protocol; handles scanning, multicart generation, ROM import, save read/write, menu rebuilding. WinForms / .NET 9 |
| `GbaMultiMenuBuilder/` | Builder (Multicart Builder). Packs multiple ROMs into a multicart image, generates menus, handles covers, and supports FC (NES) / MD cartridges. WinForms / .NET 9 |

## Relationship between the two

The Client references the Builder's `Models/`, `Services/`, `Fc/`, `Md/` sources and font assets via `<Compile Include="..\GbaMultiMenuBuilder\...">` (**29 cross-directory references**: Models 12 / Services 13 / Fc 2 / Md 1 / Assets 1). Therefore **both directories must stay under the same parent directory** so the relative paths remain valid. Moving or renaming either directory will break the build.

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
| **GBC Chinese font** | ⭕ Optional | Only needed for the Chinese GBC menu |
| **Menu ROM resources** (`*.gba` / `*.gb` under `Assets/`) | ⚙️ Included | Ready-to-use default menus; see each project's README to replace them |

Build outputs (`bin/`, `obj/`), publish output (`publish_*`), and packaged zips are all excluded by `.gitignore`.

## License and third parties

- `GbaMultiMenuBuilder/Fc/MesenNesDB.csv` comes from the Mesen2 project; the license text is in `Fc/Mesen-LICENSE` (GPL v3).
- Font assets are copyright their respective authors; see `Assets/Fonts/`.
