# ChisFlash-Tools-Exp

ChisFlash 烧录卡工具集：**上位机 2.046 r15 / 合卡器 0.92.5**。

- [下载完整工具包](https://github.com/moribaka/ChisFlash-Tools-Exp/releases/tag/v2.046-r15-0.92.5)
- [简体中文教程](TUTORIAL.zh-CN.md) · [English guide](TUTORIAL.en.md)
- [本次更新](docs/CHANGELOG-2.046-r15-0.92.5.md)
- [GBC 自由合卡固件、菜单和教程](https://github.com/ChisLightyear/ChisFlash-GBC-FreeMulticart)

本仓库目前为私有仓库，源码和下载页面需要访问权限。

## 两个工具

| 工具 | 用途 |
|---|---|
| `ChisFlashWaterClient` 上位机 | 通过配套烧录器扫描、写入游戏、管理菜单和存档；支持单卡、合卡及对应 GB/GBC/MIX 工作区 |
| `GbaMultiMenuBuilder` 合卡器 | 离线生成完整合卡镜像，交给兼容烧录器写入；自身不连接卡带烧录 |

上位机链接合卡器的共享源码。编译时保留两个同名目录，并排放在仓库根目录；使用下载包时分别运行 `client/` 或 `builder/` 中的程序。

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

包含两个工具的源码、语言文件、菜单成品、补丁资源、教程和离线验证工程。

不附带游戏 ROM、封面图库或外部中文字库。GBC 中文菜单需自备 12px 字库，放在工具的 `Assets/Fonts/`；英文菜单使用内置字形。MIX 标题当前使用用户提供的 `Assets/Fonts/mplus_hzk_12.ttf`。详见[字库说明](GbaMultiMenuBuilder/Assets/Fonts/README.md)。NS 菜单源码不在本次发布范围。

第三方数据库说明见 [SOURCE_PROVENANCE.md](GbaMultiMenuBuilder/Fc/SOURCE_PROVENANCE.md)，Mesen 许可原文见 [Mesen-LICENSE](GbaMultiMenuBuilder/Fc/Mesen-LICENSE)。项目整体许可证尚未指定，现有第三方声明保留。

## English

**Client 2.046 r15 / Builder 0.92.5**. [Download](https://github.com/moribaka/ChisFlash-Tools-Exp/releases/tag/v2.046-r15-0.92.5) · [User guide](TUTORIAL.en.md) · [Release notes](docs/CHANGELOG-2.046-r15-0.92.5.md).

The Client works with supported programmers; the Builder creates complete images for compatible programmers. Keep both source directories side by side: the Client links shared Builder sources. The Windows x64 download contains both applications and their .NET runtime.

GBA profiles are grouped into multicart and single-game choices. Recognized 256 Mbit chips with 32 MiB capacity use the single-game workflow. GB/GBC capacity filters candidates; users choose the actual cartridge configuration. MIX supports NES/MD/SNES with GLB2 v2 / LNC2 v2 and full-title extensions requiring matching firmware.

Build on Windows with .NET 9 SDK or a newer SDK supporting `net9.0-windows`, using the commands above. All required menu binaries are local to `Assets/`; no developer-drive paths or external source repositories are required.

Games, covers and external Chinese fonts are not included. GBC Chinese menus require a user-supplied 12px font; MIX titles currently use `Assets/Fonts/mplus_hzk_12.ttf`. NS menu source is not included. No repository-wide project licence has been assigned; existing third-party notices remain. This repository and its downloads currently require private-repository access.
