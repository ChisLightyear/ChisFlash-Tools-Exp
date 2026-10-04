# Client 2.037 / Builder 0.91.3

SNES import preview / SNES导入预览 · 本地版本，未推送远程

## 简体中文

- MIX增加SNES导入预览，支持`.sfc`、`.smc`及ZIP中的全部可识别成员，不再误归为MD。可信的普通LoROM／HiROM `.bin`也可识别；重复ZIP名称保留独立成员索引。
- 解析LoROM／HiROM／ExLoROM／ExHiROM头，展示映射、ROM／RAM容量、FastROM标志、区域与特殊芯片线索。DSP／ST01x等未确认变体不会被猜成具体芯片。
- 可信头部的512字节copier头只从内存副本移除；原ROM与ZIP不修改。单ROM导出使用`.sfc`，不套用NES头部旋转或MD字节序转换。
- SNES行默认不勾选、明确显示「启动协议待确认」。不能进入NES配置修正；即使调用者手动改勾选／兼容性标志，布局器与GLB2写入器仍拒绝SNES，模拟卡带测试确认拒绝路径没有NOR／SRAM写入。
- 上位机与合卡器均保留SNES识别结果，转入MIX时继续显示；原有NES／MD及8M MBC5五槽模式保留。
- ROM／ZIP大小与成员身份校验、取消、映射歧义、损坏复位头及SRAM指数上溢均有离线检查。以合成字形夹具验证现有NES目录往返，不依赖或分发外部字体。

本地只读样本：SFC目录355份ROM中348份识别到可信头，包含17份特殊芯片头、34份copier头；另抽查40份修改版，31份识别到可信头。这里只验证读取与元数据解析，不是游戏运行、核心兼容性或烧录验收。ROM、具体游戏记录与本机路径不进入发布包。

**尚未完成SNES完整合卡／实卡启动。** 当前已核实的MIX目录只提供NES／MD适配，尚无已确认的SNES核心ID、启动配置负载与配套固件契约；不能仅凭头部字段杜撰协议。SNES存档管理也未开放。后续需对接配套SNES固件／菜单源码，再增加目录往返、模拟写卡和实卡验证。

参见[SNES MIX导入预览说明](SNES-MIX-IMPORT-PREVIEW.md)。MBC5五槽功能见[2.036更新记录](CHANGELOG-2.036-0.91.2.md)。不安装或修改任何卡带固件，不替换既有下载。

## English

- Adds SNES inspection/import to both MIX workspaces: raw SFC/SMC, credible ordinary LoROM/HiROM BIN files, and all recognized ZIP members. Duplicate member names retain their indices.
- Parses standard/extended mapping headers, ROM/RAM sizes, speed/region flags and coprocessor hints. Unconfirmed DSP/ST variants remain explicitly unconfirmed.
- Removes a 512-byte copier header only from an in-memory copy with a credible header. Individual exports use SFC; source files and archives remain unchanged.
- SNES rows are unchecked with an explicit unconfirmed-launch-adapter message. NES editing is unavailable. Layout/GLB2 guards reject forced SNES inclusion before NOR/SRAM writes.
- Existing NES/MD and MBC5 five-slot behavior remains available. Offline checks cover headers, archives, cancellation, corruption, adapter guards and integrated UI.

This is not complete SNES MIX packing or hardware support. The installed-core ID, launch payload and matching firmware/menu contract must be confirmed before enabling it. Header inspection is not proof of boot, game compatibility or save operation. Local Git only; no remote push or firmware installation.
