# 上位机 2.046 r15 / 合卡器 0.92.5

发布日期：2026-10-20。发布标识：`v2.046-r15-0.92.5`。

## 功能基线

- 同步上位机 2.046 r15、合卡器 0.92.5 及共享源码。保留 GBC RTC 与 MAX 16in1 的已有改动。
- GBA 单卡／合卡复用既有窗口和列表；分组标题不可点击，待确认型号不可选。已收录的 256 Mbit NOR 与32 MiB容量共同确定单卡限制。
- 单卡从地址0写原 ROM，不自动加补丁或菜单；合卡由菜单行与游戏行组织待写草稿。GBA 头部扫描与完整配置分析分开。
- GBA 擦写保留配套原版流程；未恢复额外的写后逐字节比较。
- MIX 保留 GLB2 v2 / LNC2 v2、跨平台非连续 SRAM 分配、通用启动序列和 TTL1 完整名称扩展。扩展需要匹配固件。

## 发布整理

- 菜单与补丁统一放入各工具 `Assets/`，取消本机盘符、仓库外菜单工程及字体仓库的构建引用与自动查找。
- 菜单成品保持各工具原有配套；中文GBC菜单的既有差异见 [资源说明](MENU-ASSETS.md)。
- 不打包游戏、封面图库、外部字体、运行日志、个人设置或调试符号。NS 菜单仅包含成品。
- 合卡器窗口版本改为读取程序集版本，避免继续显示旧的0.92.3。
- 重写中英文使用教程，更新8张本版实际界面演示图；截图使用合成数据，不含实卡操作。
- 提供 Windows x64 独立运行包：`client/` 与 `builder/`，附带 .NET 运行时、教程、版本说明和校验清单。

## 验证与限制

两项目 Release 构建通过。上位机保留已有一条 CS0162 警告，合卡器无编译警告。离线回归覆盖字库、菜单预览、GBC选型、固定槽位、MIX/SNES/完整名称、GBA扫描与草稿、存档代码分析、USB身份及GB全片擦除。

验证使用合成 ROM、模拟设备或离屏界面，没有连接、擦写卡带或证明游戏实机兼容。GBC中文菜单和MIX标题仍需自备配套字库。当前英文界面的部分新控件保留中文标签。

## English

Client 2.046 r15 and Builder 0.92.5 are synchronized with shared sources and bundled local menu assets. This release includes the current GBA single-game/multicart workflow, noninteractive profile headings, GB/GBC configurations and MIX full-title support.

Developer-drive paths, external menu-project lookups and external-font repository lookups were removed. Builder window version now follows the assembly version. Updated Chinese/English guides include eight screenshots from these builds. The Windows x64 package contains both tools and their runtime.

Release builds and offline regression checks pass; the Client retains one existing CS0162 warning. No physical cartridge was accessed. Supply Chinese GBC/MIX fonts separately, use matching firmware for MIX extensions, and do not interpret offline tests as gameplay validation.
