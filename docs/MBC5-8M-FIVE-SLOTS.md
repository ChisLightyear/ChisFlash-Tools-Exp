# MBC5 8M 五槽试用说明

适用版本：上位机2.036本地预览。这里只介绍8M MBC5的单卡／3in1／4in1模式，不是MBCX或自由合卡教程。

## 准备

- 准备丐中丐烧录器，以及已安装对应8M MBC5固件的卡带；离线合成完整ROM时无需连接烧录器。
- 自备合法来源的GB／GBC游戏ROM；做合卡时另准备匹配固件的3in1／4in1菜单ROM。
- 操作已有卡带前，先备份ROM和存档。本预览的固定模式还不能导出存档，请使用原有适配工具完成备份。
- 解压测试包后直接运行`ChisFlashWaterClient.exe`；不要只复制EXE。自包含win-x64测试包不需要另装.NET运行时。

## 1. 选择卡型

选择「MBC5 8M · 3in1 / 4in1」。界面固定显示五行，不必另外选择3in1或4in1。

| 槽位 | 未添加菜单 | 已添加菜单时的NOR起点 | 合卡容量上限 |
|---|---|---|---|
| 菜单 | 可添加 | 0 | 1M保留区 |
| 游戏1 | 可添加32K～8M单ROM，起点0 | 1M | 1M |
| 游戏2 | 灰色 | 2M | 2M |
| 游戏3 | 灰色 | 4M | 4M |
| 游戏4 | 灰色 | 6M | 2M |

这里的K、M分别指KiB、MiB。槽容量固定，小ROM余下的空间不转借给其他槽。

## 2. 添加ROM

双击目标槽位或把ROM拖到对应行，也可选中行后使用添加／替换操作。

- 做单卡：不放菜单，只把ROM放入游戏1。
- 做合卡：先把匹配的固定菜单放入菜单行，再添加各游戏。
- 游戏3为空或不超过2M：游戏4开放，可形成1+2+2+2布局。
- 游戏3超过2M、最多4M：游戏4置灰，可形成1+2+4布局。
- 游戏4已有内容时，不能直接把游戏3换成超过2M的ROM；请先移除游戏4。菜单移除前也需先移除游戏2～4。
- 单卡游戏1超过1M时，不能直接添加菜单；请先替换成适合合卡的游戏1或移除它。

列表显示ROM实际大小、槽容量、存档空间和NOR起点。ROM头部声明的寻址空间也参与容量判断；异常或冲突会拒绝操作，而不是静默覆盖。

## 3. 导出完整ROM

离线时主按钮为导出完整8M镜像；也可使用「导出完整8M ROM」。生成的镜像始终为8MiB，未使用区域填充`FF`。

使用GBFlash等其他烧录器时，将这份完整镜像从NOR起点进行完整擦写，具体操作按对应烧录器说明。不要把各槽ROM直接首尾拼接。

「清空列表」只移除本地待写内容，不擦除实卡。

## 4. 使用丐中丐完整写入

1. 连接烧录器，选中GB和上述卡型，扫描卡带。需要确认8M NOR与受支持的CFI几何信息，设备身份不明或容量不匹配时不开放写入。
2. 扫描会读取卡带内容；准备写入的布局如果已修改，重新扫描前会询问是否替换。按需安排菜单和游戏。
3. 使用主按钮，阅读并确认整片写入提示。执行流程是全NOR擦除、写入新镜像、逐字节校验；不是只重置配置区。
4. 等待流程完成再拔卡。完成、失败或取消后，下一次写入需要重新扫描；中断后可能需要再次完整写入。

**整片擦除会删除旧ROM；未加入本次布局的游戏不会保留。它不清除SRAM存档。** 本版本没有实际执行实卡烧录验证，请先用可恢复的测试卡验证。

## 5. 建议核对

- 空列表：只有菜单和游戏1可操作，游戏2～4灰色。
- 加菜单：游戏1上限变1M，游戏2～4开放；加大游戏3时第四槽自动灰色。
- 槽位冲突：弹出说明，原有内容保留；删除菜单不会悄悄丢掉其他游戏。
- 导出文件：大小为8MiB，游戏起点与表格一致。
- 实卡：先验证擦除／写入／校验，再验证菜单启动、各游戏切换及存档隔离。离线通过不能代替这些结果。

## 当前限制

固定模式的存档菜单暂不可用，只显示合卡每游戏32K／单卡128K的容量。已知菜单头`MULTICARTLDR`用于识别布局内容，不是自动判定自由合卡或其他固件。卡型选择仍由使用者确认；导出成功不代表所有游戏都兼容。

## English quick start

Select **MBC5 8M · 3in1 / 4in1**. Without a menu, Game 1 is a single ROM at offset zero (32 KiB–8 MiB). Add a matching fixed menu to enable the multicart origins 1/2/4/6 MiB with capacities 1/2/4/2 MiB. Game 3 above 2 MiB occupies Game 4's region; conflicts are rejected without deleting other slots.

Double-click or drop files onto a slot, then export a complete 8 MiB image for another programmer. A confirmed Beggar in GB mode with a matching fresh scan can perform a full NOR erase, write and verification. Back up first: old ROMs are removed, SRAM is not erased, and save management is not implemented in this profile. Menu/firmware compatibility and physical operation still need testing.
