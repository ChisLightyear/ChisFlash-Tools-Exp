# GbaMultiMenuBuilder（Chis 合卡制作器）

ChisFlash 系列 GBA/GB/GBC 烧录卡的合卡器（Multicart Builder）—— **离线**把多个游戏合成**一个合卡 ROM 文件**，再交给任意 GBA 烧录器烧录。

**English version: [English](#english)**

## 功能

- 选择基础菜单 ROM
- 选择卡带容量与烧录卡类型（无电池 / 带电池（FRAM）/ 水线卡（Virtual FRAM））
- 添加多张 `.gba` ROM 到列表，支持拖放导入
- 逐条编辑标题、存档槽、隐藏按键、`map_256m`、标题字体
- 可选替换菜单背景（`240x160` PNG）
- 直接构建出最终合卡 ROM
- 按 32 MiB 分卷输出
- 支持 FC(NES) / MD 合卡（MIX 模式）

容量下拉框**有意只保留两项**：`128MiB (01G)` 与 `256MiB (02G)` —— 对应硬件流程中实际支持的 NOR 家族。

## 构建

打开 `GbaMultiMenuBuilder.sln`，或：

```powershell
dotnet build -c Release
```

或发布（框架依赖，不带运行时）：

```powershell
dotnet publish GbaMultiMenuBuilder.csproj -c Release -r win-x64 --no-self-contained -o publish
```

## 界面文件

界面文本从可执行文件同级的 `Languages/*.ini` 加载：

- `Languages/zh_CN.ini`（默认）
- `Languages/en_US.ini`

运行时设置存于同级的 `config.ini`（语言、构建选项、基础/输出/背景路径、窗口几何、列宽等）。从界面切换语言会立即刷新当前窗口。

## 存档类型规则

存档类型识别规则从同级的 `save_types.ini` 加载，按大类分组：

- `[EEPROM]`
- `[FLASH]`
- `[SRAM]`

规则格式：

- `SIGNATURE=VersionDigitCount`
- `SIGNATURE=Kind|VersionDigitCount`

示例：

- `EEPROM_V=3`
- `FLASH1M_V=Flash1M|3`
- `SRAM_F_V=3`

## 隐藏按键格式

「隐藏按键」列接受以下标记，可用 `,` 组合或用 `+` 表示同时按下：

- `L` / `R` / `SELECT` / `START`
- `UP` / `DOWN` / `LEFT` / `RIGHT`
- `A` / `B`

示例：`L,R,DOWN`、`SELECT+START`

## 免电补丁

`无电池` 构建支持免电补丁。

当前行为：

- 必须先完成 SRAM 补丁，才会应用免电补丁。
- 已打过 SRAM 补丁或已打过免电补丁的 ROM 视为已就绪，不会重复打 SRAM 补丁。
- 运行时以集成式 `compat-auto-detect` 载荷作为**唯一**官方免电实现。
- 旧的 `extended` 载荷族已不在正式补丁路径中。
- 逆向细节见 `docs/batteryless_payload_compare.md`。
- 新载荷尚未证实对所有 NOR 家族通用。目前确认覆盖旧的 `42/96` 族、`A9/56` 族、
  以及 `AA/55` 的 GL-S 类；`AAA9/5556` 在新载荷中仍未证实。

实现位置：

- 集成补丁逻辑：`Services/BatterylessPatchService.cs`
- `--batteryless-experiment` 现在对已就绪 SRAM 的测试 ROM 生成同样的集成式自动探测载荷

`S29GL01GS` 实测结果：

- 测试 ROM：`赛尔达传说 - 缩小帽[天使汉化组](简)(JP)(128Mb)`
- 可用变体：`__compat-auto-detect.gba`
- 测试中失败的变体：`__extended-cart-profile4.gba`、`__extended-profile1.gba`、`__extended-legacy.gba`

结论：

- `S29GL01GS` 应使用集成式 `compat-auto-detect` 免电载荷。
- 问题不能单纯用卡带—Flash 配置映射解释；`S29GL01GS -> Flash4` 在合卡器里仍是正确映射。
- 实际现象是旧的 `extended` 载荷族在该卡带上不可靠，而集成式 `compat-auto-detect` 可靠。

## FC (NES) 模式

支持 `NES | CHISBOY专用卡（MBCX）`：NES/ZIP、BIN 导入导出，Mesen RAM/电池补全，
8 KiB 独立/共享存档映射。详见 [FC_MODE.md](FC_MODE.md) 与
[NOR 载荷对齐](FC_NOR_ALIGNMENT_20260927.md)。

FC02v7 对齐 PRG 载荷、配套 02 的 0038 核心，原 NES 无损还原、拆分快照和 12px 标题保留。
**旧卡需从原 NES / ZIP 重新制作并烧录。**

## 说明

- 目标框架 `net9.0-windows`，WinForms；界面风格刻意贴近传统 Windows 工具。
- 本项目引用仓库外同级目录的菜单 ROM（`..\GBA_MultiMenu_*\`）与封面库；
  **这些引用全部带 `Exists()` 条件**，路径不存在时自动回落到自带的 `Assets\`，因此仓库可独立构建。

---

<a id="english"></a>

# English — GbaMultiMenuBuilder (Chis Multicart Builder)

The Builder (Multicart Builder) for ChisFlash GBA/GB/GBC flash cartridges — **offline**, it combines multiple games into **a single multicart ROM file**, which you then burn with any GBA burner.

**中文版：[中文](#gbamultimenubuilderchis-合卡制作器)**

## Features

- Select the base menu ROM
- Choose cartridge capacity and flashcart type (No Battery / With Battery (FRAM) / Waterline cart (Virtual FRAM))
- Add multiple `.gba` ROMs to a list, with drag-and-drop import
- Edit title, save slot, hidden keys, `map_256m`, and title font per entry
- Optionally replace the menu background with a `240x160` PNG
- Build the final multicart ROM directly
- Split output into 32 MiB parts
- FC (NES) / MD multicarts supported (MIX mode)

The capacity selector is **intentionally limited to two options**: `128MiB (01G)` and `256MiB (02G)` — the NOR families actually supported in our hardware workflow.

## Build

Open `GbaMultiMenuBuilder.sln`, or:

```powershell
dotnet build -c Release
```

Or publish (framework-dependent, without the runtime):

```powershell
dotnet publish GbaMultiMenuBuilder.csproj -c Release -r win-x64 --no-self-contained -o publish
```

## UI Files

UI text is loaded from `Languages/*.ini` next to the executable:

- `Languages/zh_CN.ini` (default)
- `Languages/en_US.ini`

Runtime settings live in `config.ini` next to the executable (language, build selections, base/output/background paths, window geometry, column widths). Switching the language from the UI refreshes the current window immediately.

## Save Type Rules

Save type detection rules are loaded from `save_types.ini` next to the executable, grouped by major category:

- `[EEPROM]`
- `[FLASH]`
- `[SRAM]`

Rule format:

- `SIGNATURE=VersionDigitCount`
- `SIGNATURE=Kind|VersionDigitCount`

Examples:

- `EEPROM_V=3`
- `FLASH1M_V=Flash1M|3`
- `SRAM_F_V=3`

## Hidden Keys Format

The "Hidden Keys" column accepts the following tokens; combine with `,` for a sequence or `+` for simultaneous presses:

- `L` / `R` / `SELECT` / `START`
- `UP` / `DOWN` / `LEFT` / `RIGHT`
- `A` / `B`

Examples: `L,R,DOWN`, `SELECT+START`

## Batteryless Patching

Batteryless patching is supported for `No Battery` builds.

Current behavior:

- SRAM patching must be ready first before batteryless patching is applied.
- ROMs already SRAM-patched or already batteryless-patched are treated as prepared and will not be SRAM-patched again.
- The runtime uses the integrated `compat-auto-detect` payload as the **only** official batteryless implementation.
- The old `extended` payload family is no longer part of the formal patch path.
- Reverse-engineering notes are in `docs/batteryless_payload_compare.md`.
- The new payload is not yet proven universal across every NOR family. Confirmed coverage includes the old `42/96` family, the `A9/56` family, and the `AA/55` GL-S-like family; `AAA9/5556` remains unproven in the new payload.

Implementation reference:

- Integrated patch logic: `Services/BatterylessPatchService.cs`
- `--batteryless-experiment` now produces the same integrated auto-detect payload for SRAM-ready test ROMs

Measured result on `S29GL01GS`:

- Test ROM: `赛尔达传说 - 缩小帽[天使汉化组](简)(JP)(128Mb)`
- Working variant: `__compat-auto-detect.gba`
- Failed variants during testing: `__extended-cart-profile4.gba`, `__extended-profile1.gba`, `__extended-legacy.gba`

Conclusion:

- `S29GL01GS` should use the integrated `compat-auto-detect` batteryless payload.
- The failure is not explained by the cartridge-to-flash-profile mapping alone; `S29GL01GS -> Flash4` is still the correct mapping in the builder.
- In practice, the old `extended` payload family is unreliable on this cartridge, while the integrated `compat-auto-detect` payload is reliable.

## FC (NES) Mode

Supports `NES | CHISBOY-dedicated cart (MBCX)`: NES/ZIP and BIN import/export, Mesen RAM/battery completion, and 8 KiB independent/shared save mapping. See [FC_MODE.md](FC_MODE.md) and [NOR payload alignment](FC_NOR_ALIGNMENT_20260927.md).

FC02v7 aligns the PRG payload and pairs with the 02 0038 core; lossless NES restoration, split snapshots, and 12px titles are retained. **Older cards must be rebuilt from the original NES / ZIP and re-flashed.**

## Notes

- Target framework `net9.0-windows`, WinForms; the UI style deliberately follows traditional Windows tooling.
- This project references menu ROMs (`..\GBA_MultiMenu_*\`) and the cover library from sibling directories outside the repository. **All of those references carry `Exists()` conditions** and fall back to the bundled `Assets\` when the paths are absent, so the repository builds standalone.
