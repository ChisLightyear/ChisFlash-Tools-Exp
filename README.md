# ChisFlash-Tools-Exp

ChisFlash 烧录卡工具集：**上位机 2.046 r15 / 合卡器 0.92.5**。

- [下载完整工具包](https://github.com/ChisLightyear/ChisFlash-Tools-Exp/releases/tag/v2.046-r15-0.92.5)
- [简体中文教程](TUTORIAL.zh-CN.md) · [English guide](TUTORIAL.en.md)
- [本次更新](docs/CHANGELOG-2.046-r15-0.92.5.md)
- [GBC 自由合卡固件、菜单和教程](https://github.com/ChisLightyear/ChisFlash-GBC-FreeMulticart)

源码和下载页面均公开。下载页分别提供上位机与合卡器的 Windows x64 已编译包；教程、截图和源码保留在仓库。

## 两个工具

| 工具 | 用途 |
|---|---|
| `ChisFlashWaterClient` 上位机 | 通过配套烧录器扫描、写入游戏、管理菜单和存档；支持单卡、合卡及对应 GB/GBC/MIX 工作区 |
| `GbaMultiMenuBuilder` 合卡器 | 离线生成完整合卡镜像，交给兼容烧录器写入；自身不连接卡带烧录 |

上位机链接合卡器的共享源码。编译时保留两个同名目录，并排放在仓库根目录；下载对应程序包后完整解压，运行包根目录中的 EXE。

## 这个版本

- GBA 烧录卡按“合卡／单卡”分组，分组标题不可点击；1.1V 暂为待确认项目。
- 已收录的 256 Mbit NOR 配合 32 MiB 实测容量进入单卡流程，只显示游戏1；单卡原 ROM 不自动打补丁或插入菜单。
- 支持合卡的 GBA 工作区采用菜单行＋游戏行，先编辑草稿，再明确烧录；“分析卡带”和“分析配置”用途分开。
- GB/GBC 容量只筛选候选卡型，由用户选择固定槽位、RTC、自由合卡或 MIX 模式。
- MIX 使用 GLB2 v2 / LNC2 v2，支持 NES / MD / SNES 和完整名称；相应扩展需要配套固件支持。

![上位机 2.046 r15](docs/images/zh-CN/client_02_multicart.png)

截图来自本版程序，使用合成 ROM 和模拟卡带数据，不代表实卡验证。

## 构建

Windows，.NET 9 SDK 或支持 `net9.0-windows` 的更新 SDK：

```powershell
dotnet build ChisFlashWaterClient/ChisFlashWaterClient.csproj -c Release
dotnet build GbaMultiMenuBuilder/GbaMultiMenuBuilder.csproj -c Release
```

菜单和补丁成品位于各工具的 `Assets/`。项目不引用开发机盘符、旁边的菜单工程或外部字体仓库。无需安装 GBDK 或 Quartus 即可编译桌面工具。

## 内容与资源

包含两个工具的源码、语言文件、菜单成品、补丁资源和使用教程。

不附带游戏 ROM、封面图库或外部中文字库。GBC 中文菜单需自备 12px 字库，放在工具的 `Assets/Fonts/`；英文菜单使用内置字形。MIX 标题当前使用用户提供的 `Assets/Fonts/mplus_hzk_12.ttf`。详见[字库说明](GbaMultiMenuBuilder/Assets/Fonts/README.md)。NS 菜单源码不在本次发布范围。

第三方数据库说明见 [SOURCE_PROVENANCE.md](GbaMultiMenuBuilder/Fc/SOURCE_PROVENANCE.md)，Mesen 许可原文见 [Mesen-LICENSE](GbaMultiMenuBuilder/Fc/Mesen-LICENSE)。项目整体许可证尚未指定，现有第三方声明保留。

## English

**Client 2.046 r15 / Builder 0.92.5**. [Download](https://github.com/ChisLightyear/ChisFlash-Tools-Exp/releases/tag/v2.046-r15-0.92.5) · [User guide](TUTORIAL.en.md) · [Release notes](docs/CHANGELOG-2.046-r15-0.92.5.md).

The Client works with supported programmers; the Builder creates complete images for compatible programmers. Keep both source directories side by side: the Client links shared Builder sources. Separate Windows x64 compiled downloads are provided for the Client and Builder, each with its .NET runtime. Tutorials, screenshots and sources remain in the repository.

GBA profiles are grouped into multicart and single-game choices. Recognized 256 Mbit chips with 32 MiB capacity use the single-game workflow. GB/GBC capacity filters candidates; users choose the actual cartridge configuration. MIX supports NES/MD/SNES with GLB2 v2 / LNC2 v2 and full-title extensions requiring matching firmware.

Build on Windows with .NET 9 SDK or a newer SDK supporting `net9.0-windows`, using the commands above. All required menu binaries are local to `Assets/`; no developer-drive paths or external source repositories are required.

Games, covers and external Chinese fonts are not included. GBC Chinese menus require a user-supplied 12px font; MIX titles currently use `Assets/Fonts/mplus_hzk_12.ttf`. NS menu source is not included. No repository-wide project licence has been assigned; existing third-party notices remain. This repository and its downloads are public.

## 鸣谢

本项目的开发参考和使用了以下社区项目，感谢各位作者的贡献：

- **[lesserkuma / GBA_MultiMenu](https://github.com/lesserkuma/GBA_MultiMenu)**：提供了 GBA 多游戏菜单的原始基础。
- **[ZaindORp（orzgithub）/ GBA_MultiMenu_extended](https://github.com/orzgithub/GBA_MultiMenu_extended)**：提供了扩展菜单、卡带适配和合卡布局方面的基础。
- **[ZaindORp（orzgithub）/ GBA_MultiMenu_GUI](https://github.com/orzgithub/GBA_MultiMenu_GUI)**：原合卡制作 GUI 及补丁整合流程，为本项目的桌面合卡器提供了重要参考。
- **[metroid-maniac / gba-auto-batteryless-patcher](https://github.com/metroid-maniac/gba-auto-batteryless-patcher)**：提供了 GBA 免电保存补丁及配套负载的基础。
- **[bbsan2k / Flash1M_Repro_SRAM_Patcher](https://github.com/bbsan2k/Flash1M_Repro_SRAM_Patcher)**：提供了 GBA SRAM 补丁表及 1Mbit 存档银行切换处理的参考实现。
- **[SourMesen / Mesen2](https://github.com/SourMesen/Mesen2)**：本项目使用其 NES 游戏数据库，并参考其 Mapper、ROM 和存档参数识别规则。
- **[linscon（julpage）/ beggar_socket](https://github.com/julpage/beggar_socket)**：丐中丐烧录器的通信协议和原版上位机，为本项目的兼容通信与烧录流程提供了基础。
- **[TakWolf / Fusion Pixel](https://github.com/TakWolf/fusion-pixel-font)**：提供了经典系菜单使用的像素字体。

桌面程序基于 **Microsoft .NET / Windows Forms**，并使用 `System.IO.Ports`、`System.Management` 等组件。

同时感谢参与测试、反馈问题和分享卡带资料的朋友们。

## Acknowledgements

This project builds on and draws inspiration from the following community projects. We thank their authors for their contributions:

- **[lesserkuma / GBA_MultiMenu](https://github.com/lesserkuma/GBA_MultiMenu)**: The original foundation for the GBA multicart menu.
- **[ZaindORp (orzgithub) / GBA_MultiMenu_extended](https://github.com/orzgithub/GBA_MultiMenu_extended)**: The foundation for extended menus, cartridge support and multicart layouts.
- **[ZaindORp (orzgithub) / GBA_MultiMenu_GUI](https://github.com/orzgithub/GBA_MultiMenu_GUI)**: The original multicart-building GUI and integrated patching workflow, which provided an important reference for our desktop Builder.
- **[metroid-maniac / gba-auto-batteryless-patcher](https://github.com/metroid-maniac/gba-auto-batteryless-patcher)**: The foundation for GBA batteryless-saving patches and their payloads.
- **[bbsan2k / Flash1M_Repro_SRAM_Patcher](https://github.com/bbsan2k/Flash1M_Repro_SRAM_Patcher)**: Reference implementations for GBA SRAM patch tables and bank switching for 1 Mbit saves.
- **[SourMesen / Mesen2](https://github.com/SourMesen/Mesen2)**: The NES game database used by this project, along with references for mapper, ROM and save-parameter identification.
- **[linscon (julpage) / beggar_socket](https://github.com/julpage/beggar_socket)**: The Beggar programmer protocol and original desktop client, which provided the foundation for compatible communication and programming workflows.
- **[TakWolf / Fusion Pixel](https://github.com/TakWolf/fusion-pixel-font)**: The pixel font used in the Classic-family menus.

The desktop applications are built with **Microsoft .NET / Windows Forms** and use components including `System.IO.Ports` and `System.Management`.

We also thank everyone who has helped test the tools, reported issues and shared cartridge information.
