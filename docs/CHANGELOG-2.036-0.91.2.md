# Client 2.036 / Builder 0.91.2

2026-10-05 · Local preview / 本地预览

## 简体中文

- 新增烧录卡选型「MBC5 8M · 3in1 / 4in1」，复用原有主界面，列表固定显示菜单、游戏1～4。
- 未添加菜单时，仅开放菜单和游戏1；游戏1支持32K～8M单ROM，从NOR起点开始。
- 添加匹配的固定菜单后，菜单保留首个1M，游戏槽位依次为1M、2M、4M、2M，起点分别为1M、2M、4M、6M。
- 游戏3为空或不超过2M时开放游戏4；超过2M时占用游戏4区域并置灰第四槽，不提供额外的3in1／4in1开关。
- 添加、替换和批量导入按完整布局校验；冲突时拒绝整次修改，不自动删除、截断或覆盖其他槽位。容量同时考虑文件长度与ROM头部声明的寻址空间。
- 支持双击、拖放、删除、清空列表、单槽ROM导出及完整8M镜像导出。未连接设备时可离线安排布局、导出镜像，交由其他烧录器完整烧录。
- 确认丐中丐、GB模式及匹配的8M NOR扫描后，提供整片NOR擦除、写入和逐字节校验；非启动区先写、菜单／启动区最后写。写入前重新核对NOR身份与CFI信息；失败、取消或选型切换后旧扫描失效。
- 扫描可读取完整8M内容，并按已知的`MULTICARTLDR`菜单头重建槽位；这只是内容识别，不代表能够自动识别卡带固件。
- 固定模式隐藏不适用的菜单生成、字库与WAITCNT选项；不再调用自由合卡目录或存档管理命令。共享AG32身份不明的设备仍不能发送烧录命令。
- 增加布局、镜像、模拟传输与离屏界面检查；没有在实卡上执行擦除、烧录或运行游戏。

### 当前边界

- 仅适用于8M MBC5单卡／3in1／4in1布局，尚未扩展到MBCX RTC 8M、32M MAX、16in1或自由合卡。
- 必须自备匹配卡带固件的固定菜单，不自动使用自由合卡菜单；未知菜单格式的扫描还原不保证正确。
- 显示合卡每游戏32K、单卡128K存档空间，但本模式尚未接入存档导入、导出与清空。ROM整片擦除不会清除SRAM。
- 能生成镜像和通过离线检查不等于游戏都能运行；菜单启动、实卡写入及游戏／存档兼容性仍需验证。
- 整片写入会清除NOR中的旧内容，包括未放入本次布局的游戏。操作前逐个备份ROM和存档；中断时可能无法启动。

合卡器保持0.91.2。本次只保存本地Git，不推送远程，不替换已有下载包。操作步骤见[MBC5 8M五槽试用说明](MBC5-8M-FIVE-SLOTS.md)。

## English

- Adds the MBC5 8 MiB 3in1 / 4in1 profile with five persistent rows: Menu and Games 1–4, within the existing UI.
- Without a menu, only Menu and Game 1 are available. Game 1 accepts a 32 KiB–8 MiB single ROM at NOR offset zero.
- With a matching fixed menu, the first 1 MiB is reserved. Game origins are 1, 2, 4 and 6 MiB; capacities are 1, 2, 4 and 2 MiB. Game 3 above 2 MiB disables Game 4 and occupies its region.
- File imports and replacements validate the complete layout atomically, including declared ROM addressing and save requirements. Conflicts never silently delete or truncate another game.
- Supports offline full-image export, individual ROM export and full-NOR erase/write/byte verification through a confirmed Beggar in GB mode with fresh matching 8 MiB CFI geometry. Boot/menu banks are programmed last.
- Full-image scans recognize the known `MULTICARTLDR` content header, not firmware identity. Unknown shared-ID AG32 products remain unable to receive programmer commands.
- Inapplicable menu/font/WAITCNT controls and free-multicart save commands are disabled. Save capacities are shown, but SRAM import/export/erase is not implemented in this profile; NOR rewriting does not erase SRAM.
- Covers only the 8 MiB MBC5 layout. Supply a matching fixed menu; no free-multicart menu is automatically substituted. Offline checks are not proof of physical programming, boot or game compatibility.

Back up ROMs and saves before a full rewrite: all previous NOR content is erased. Builder stays at 0.91.2. This is a local Git preview with no remote push or replacement of existing downloads.
