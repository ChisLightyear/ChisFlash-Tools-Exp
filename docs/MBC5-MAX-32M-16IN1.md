# MBC5 MAX 32M：菜单与16个固定游戏槽

适用：上位机2.043。选择「MBC5 MAX 32M · 16in1」，使用与8M固定合卡相同的添加、替换、删除、扫描、导出及完整NOR烧录操作。

## 布局

|槽位|NOR起点|容量上限|合卡存档|
|---|---|---|---|
|菜单|0|预留1 MiB|不分配|
|游戏1|1 MiB|1 MiB|32 KiB|
|游戏2～16|2、4、6…30 MiB|各2 MiB|各32 KiB|

布局按MAX v1.22的ROM/RAM地址选择逻辑和16in1菜单的bank查找表核对：游戏1的bank起点为0x40，游戏2～16为0x80、0x100…0x780。存档起点为游戏索引×32 KiB，总计512 KiB。MAX固定固件把每游戏ROM限制在其槽中，不采用8M游戏3借用第四槽的规则。

未添加菜单时仅菜单行和游戏1开放；游戏1从NOR起点0写入，允许32 KiB～32 MiB文件，存档上限128 KiB。文件容量与头部声明寻址大小均参与检查。单卡文件是否可运行由实际游戏和固件决定。

## 操作

1. 明确选择MAX 32M卡型。32 MiB NOR只筛选候选，不代替用户选择固件。
2. 做合卡时，将匹配的16in1菜单导入菜单行，随后添加最多16个GB/GBC游戏。游戏1不得超过1 MiB，其余不得超过2 MiB，每游戏存档需求不得超过32 KiB。
3. 支持双击、拖放、批量导入、逐槽替换/删除/导出及清空工作区。批量文件全部验证成功才应用；错误不覆盖原内容。已有游戏2～16时不能直接移除菜单。
4. 未连接烧录器时可导出完整32 MiB `.gb`镜像，预留和空白区域填FF。8M与32M工作区分别保留内容。
5. 使用已确认的丐中丐、GB模式及32 MiB NOR扫描后，支持全NOR擦除、逐bank编程及逐字节读回校验；前1 MiB启动区域最后编程。写前重新核对NOR身份、容量和CFI几何，失败/取消后重新扫描。

扫描读取完整32 MiB，按所选固定布局还原各槽。标准菜单按已知MULTICARTLDR头识别；现有MoriMENU 16in1 EX 1.02没有标准GB头，仅以其完整32 KiB SHA256识别，不把任意无头文件当菜单。匹配菜单由使用者准备，工具不内置或修改菜单/卡带固件。

固定模式尚未提供SRAM存档导入、导出或清空；整NOR擦写不擦除SRAM。槽位变化后原槽存档仍保留，不能视为自动随游戏搬移。

验证使用合成ROM和模拟NOR，覆盖全部16槽、30 MiB起点高bank、容量错误、批量失败、扫描重建与8/32M切换。尚未进行实卡擦写、游戏启动或存档验收。

## English

Client 2.043 adds a fixed Menu + 16 Games workspace for MBC5 MAX 32 MiB. The menu reserves the first 1 MiB; Game 1 starts at 1 MiB with a 1 MiB limit; Games 2–16 start at 2, 4, …30 MiB with 2 MiB limits. Each multicart game has 32 KiB SRAM. MAX does not merge adjacent game slots.

Without a menu, only Menu/Game 1 are enabled and a single ROM is placed at zero. Imports check actual and declared ROM sizes and save requirements atomically. Full-image export pads unused bytes with FF. Programming requires a fresh matching Beggar/GB/CFI scan, erases NOR, writes the first 1 MiB last, and verifies all banks. SRAM is preserved; fixed-profile save management is not implemented. Both 8/32 MiB workspaces retain their own contents. Scan recognizes the standard MULTICARTLDR menu or the exact headerless EX 1.02 menu hash. Tests use synthetic ROMs and fake devices; no physical programming or gameplay verification has been performed.
