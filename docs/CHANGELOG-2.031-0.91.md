# Client 2.031 / Builder 0.91

## English

- Compact NOR information and log areas give the game list more space. English labels stay on one line and align with their controls.
- NES and MD MIX games use the Client's main workspace and menu actions.
- User-facing FC names are now NES. Existing internal identifiers and FC02 cartridge markers retain their format.
- Queued GBA ROMs can match and edit covers before burning. Automatic matching requires a separately supplied local cover catalog.
- Chinese GBC menus require a user-supplied 12px font. Unsupported 8px fonts are hidden and rejected during glyph generation. The English preview uses the menu's built-in ASCII glyphs and 16 entries per page.
- Bundled menu kernels are synchronized with the current local menu builds, including the NS capacity map and language updates.

Extract the new package into a separate folder. Copy your own cover library and 12px font into the corresponding directories as needed. Select the desired settings in the new tool. Updating the desktop tools does not write to the cartridge; use the menu-update operation when you want to update an on-card menu.

Both Windows x64 downloads require the **.NET 9 Desktop Runtime**. Games, covers and external fonts are not included. Existing cartridge formats are retained.

## 简体中文

- 压缩 NOR 信息区和日志区，增大游戏列表；英文标签保持单行并与控件对齐。
- NES、MD MIX 游戏共用上位机主工作区及菜单操作入口。
- 界面中的 FC 统一显示为 NES；内部标识及 FC02 卡带格式保持兼容。
- 待烧录 GBA ROM 可提前匹配、编辑封面；自动匹配需要另行准备本地封面图库。
- GBC 中文菜单强制使用自备 12px 字库；8px 不列出且不能生成字形。英文预览使用菜单内置 ASCII 字形，每页 16 项。
- 同步当前菜单内核，包括 NS 容量映射与中英文切换更新。

将新版解压到独立目录，按需复制自备封面图库和 12px 字库，再选择所需设置。更新电脑上的工具不会写入卡带；需要更新卡上菜单时，再执行更新菜单操作。

两个 Windows x64 下载包都需要 **.NET 9 桌面运行时**。不附带游戏、封面图库或外部字库，保留已有卡带格式。
