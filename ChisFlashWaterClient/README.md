# ChisFlashWaterClient

ChisFlash 系列 GBA/GB/GBC 烧录卡的上位机（烧录客户端）。

**English version: [English](#english)**

## 功能

- **设备通信**：通过 CDC ACM 自定义二进制协议与卡带交互（非 USB MSC）
- **卡带扫描**：识别 NOR Flash 型号、容量、菜单配置与已有游戏目录
- **合卡写入**：把多张 ROM 以「自由合卡」格式写入卡带，支持碎片管理与槽位布局
- **ROM 导入 / 导出**：支持 `.gba` / `.gb` / `.gbc` / `.nes` 及 ZIP 打包导入
- **存档读写**：独立区 / 共享区存档的导入导出
- **菜单重建**：检测并修复菜单目录、WL 映射、itemlist/status 元数据
- **封面支持**：GBA 游戏封面匹配与打包（封面图库 `covers/` 单独分发，未随仓库提供）
- **多语言**：`Languages/*.ini`，含简体中文与英文

## 依赖

- .NET 9 SDK（需 Windows Desktop 运行时）
- `System.IO.Ports`、`System.Management`

## 构建

```bash
dotnet build ChisFlashWaterClient.csproj -c Release
```

或直接用 Visual Studio 打开 `ChisFlashWaterClient.sln`。

## 与合卡器的关系

本项目引用 `../GbaMultiMenuBuilder/` 下的 `Models/`、`Services/`、`Fc/`、`Md/` 源码与字体资源，因此**两个项目目录必须保持同级**。

## 说明

- 本仓库只包含源码工程，不含编译产物、发布包或封面图库。
- 封面图库（`covers/`，约 534 MiB）需单独准备；缺失时自动封面匹配不可用，其余功能正常。
- 目标框架 `net9.0-windows`，WinForms。

---

<a id="english"></a>

# English — ChisFlashWaterClient

The Client (burning client) for ChisFlash GBA/GB/GBC flash cartridges.

**中文版：[中文](#chisflashwaterclient)**

## Features

- **Device communication**: talks to the cartridge over a custom binary CDC ACM protocol (not USB MSC)
- **Cartridge scanning**: identifies the NOR flash model, capacity, menu configuration, and existing game directory
- **Multicart writing**: writes multiple ROMs to the cartridge in the "free multicart" format, with fragmentation management and slot layout
- **ROM import / export**: supports `.gba` / `.gb` / `.gbc` / `.nes` and ZIP-packed imports
- **Save read/write**: import and export of independent-area / shared-area saves
- **Menu rebuilding**: detects and repairs menu directories, WL mappings, and itemlist/status metadata
- **Cover support**: GBA cover matching and packing (the `covers/` library is distributed separately and is not part of this repository)
- **Localization**: `Languages/*.ini`, with Simplified Chinese and English

## Dependencies

- .NET 9 SDK (requires the Windows Desktop runtime)
- `System.IO.Ports`, `System.Management`

## Build

```bash
dotnet build ChisFlashWaterClient.csproj -c Release
```

Or open `ChisFlashWaterClient.sln` directly in Visual Studio.

## Relationship with the Builder

This project references the `Models/`, `Services/`, `Fc/`, `Md/` sources and font assets under `../GbaMultiMenuBuilder/`, so **both project directories must stay at the same level**.

## Notes

- This repository contains source projects only — no build outputs, publish packages, or cover library.
- The cover library (`covers/`, ~534 MiB) must be prepared separately; without it, automatic cover matching is unavailable while everything else works normally.
- Target framework `net9.0-windows`, WinForms.
