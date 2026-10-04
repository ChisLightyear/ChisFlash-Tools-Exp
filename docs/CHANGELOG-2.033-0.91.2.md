# Client 2.033 / Builder 0.91.2

## English

- MIX scans read directory metadata, launch configuration and names without reading ROM payloads or title bitmaps. ROM data is loaded on demand for explicit export, configuration checks and rebuild/write operations.
- The shared Cartridge > Rebuild Menu action now supports MIX configuration-only rebuilds. It rebuilds the on-card directory, launch configuration and titles while retaining ROM addresses and bytes, manual configuration and save mappings.
- Pending imports, deletions, renames, checkboxes and configuration edits are not applied by this action and remain available afterward.
- Preflight validates source metadata and full ROM content identities and rejects ROM relocation/content changes or save-map changes before erase. Unchanged configurations perform no NOR writes.
- A valid writable scanned card and confirmation are required. Failure invalidates the source snapshot. Other GB/GBA maintenance and erase actions remain disabled for MIX cards.
- Retain the existing AG32 2.42 pairing. This source update does not install firmware or establish physical-card compatibility or measured scan speed.

The repository remains private. This update changes source only; the existing 2.031 / 0.91 download packages and bundled menu kernels are retained. Games, cover libraries, external fonts, private environment configuration and hardware-test records are not included.

## 简体中文

- MIX 普通扫描只读取目录元数据、启动配置和名称，不读取 ROM 载荷或标题位图；导出、配置检查和重建/写入等明确操作才按需加载 ROM。
- 共用的“卡带 → 重建菜单”入口支持 MIX 配置专用重建。依据卡上已有游戏重建目录、启动配置和标题，保留 ROM 地址及字节、手动配置和存档映射。
- 不应用工作区待处理的导入、删除、重命名、勾选或配置改动；这些改动仍保留供后续操作使用。
- 写入前验证源元数据与完整 ROM 内容身份，拒绝 ROM 搬移、内容变化和存档映射变化；配置未变时不写 NOR。
- 必须有有效可写的扫描结果并经确认；失败使源快照失效。MIX 卡继续禁用其他 GB/GBA 维护与擦除入口。
- 保持既有 AG32 2.42 配套。本次源码更新不安装固件，不代表实卡兼容性或扫描耗时已经验证。

仓库保持私有。本次只更新源码，保留现有 2.031 / 0.91 下载包及配套菜单内核；不包含游戏、封面库、外部字库、私人环境配置或硬件体验记录。
