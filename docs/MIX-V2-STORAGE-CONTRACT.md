# MIX v2：统一存储与启动协议

适用：上位机2.041、合卡器0.92.2。本文为桌面端已实现的数据合同，也是 MCU／FPGA 的接口依据。NES、MD、SNES 均作为已接入平台；桌面端不检查已安装核心或核心 Mapper 白名单，不以 SNES 接入状态阻止合卡、BIN 导出或烧录。本轮只验证桌面与模拟卡流程，不宣称实际运行、特殊芯片兼容或硬件写卡通过。

## 1. 不再兼容旧格式

项目仍在开发，直接采用新协议，不做旧格式回读、迁移、双写或自动降级。

- 目录标识 GLB2，版本2；记录适配版本2。
- 启动标识 LNC2，版本2。拒绝 GLB2 v1、LNC1、旧 FC02／MD02／MIX1。
- 旧卡不认作有效 MIX v2；界面需确认重建。只按用户当前导入列表重新建立，不自动迁移旧游戏／旧存档，不把旧格式当作空卡。
- 存档归档中的稳定内容身份不变，但旧目录本身不再解析。先保留旧工具／备份，再从原始 ROM 重建。
- 其他 GB/GBC/GBA 及 MBC5 模式不受本规则改变。

## 合卡、导出与烧录资格

- NES Mapper 0–4095、submapper 0–15 只检查格式可编码范围，不维护核心支持白名单。Mapper 191/0、未知但可编码的 Mapper/submapper 均可导入、导出与烧录。
- 不要求用户安装或确认某个核心才能操作；不添加“SNES 待同步”的额外确认。游戏能否运行由运行核心处理，不用桌面合卡结果替代实机验证。
- NES 特殊主机及 SNES 特殊芯片原始元数据保留，提示不作为核心兼容性拦截。手动 NES 配置同样不添加核心 CHR-ROM/CHR-RAM 上限。
- 保留真实格式与安全检查：截断/损坏文件、不可编码字段、实际 NOR/SRAM 容量、独占 bank 冲突、卡带写保护、操作取消、写前确认与读回验证。
- UI 的可打包状态与失败数仅表示格式/数据检查结果，不能命名为游戏“不兼容”或承诺所有游戏运行。

## 2. 统一空间和地址

所有多字节字段为小端，偏移均相对所在结构起点。物理地址均为卡带 NOR/SRAM 的线性字节地址；明确注明 bank／word 的字段除外。

- NOR：实际检测容量与32 MiB取小值；ROM起点至少0x100000，分配长度及起点按16 KiB对齐。
- NOR前1 MiB是目录和资源前缀，不是 GB菜单ROM，不在其中安装 FPGA bitstream。
- SRAM：512 KiB =64×8 KiB；0–59独占、60–63共享工作RAM。
- NES、MD、SNES使用同一分配器，不分平台容量、不要求MD/SNES连续物理bank。
- 独占映射单游戏最多60项，受总池容量约束；共享最多4项。128 KiB存档需16项，不再套旧NES八项表限制。保留共享池并不是所有平台硬件运行上限声明。
- 无电池工作RAM共享逻辑bank0..n-1映射到物理60..60+n-1。带电池区域独占；空间不足报错，不自动改为共享。

运行端寻址规则：


tag = logical_address / 8192
within = logical_address % 8192
physical_address = bank_map[tag] * 8192 + within


先检查logical_address在实际RAM字节数内、tag小于映射长度，再访问。映射顺序是游戏的逻辑顺序，不按物理bank重新排序。不要使用起点+偏移、first_bank+tag替代映射。未分配尾部字节不属于该游戏。

## 3. GLB2头：64字节，NOR 0x20000

|偏移|大小|含义|
|---|---|---|
|0|4|ASCII GLB2|
|4|u16|版本2|
|6|u16|记录长度96|
|8|u16|游戏数|
|10|u16|保留0|
|12|u32|记录起点，固定0x20040|
|16|u32|元数据使用末端，不超过0x100000|
|20|u32|ROM分配末端；空库为0x100000|
|24|u32|保留0|
|28|u32|SRAM容量524288|
|32–59|28|保留0|
|60|u32|头前60字节CRC32|

数量受目录资源、NOR和SRAM限制，不再每平台99项。旧99项常量不用于MIX。

## 4. 游戏记录：96字节

|偏移|大小|含义|
|---|---|---|
|0|16|规范ROM数据SHA256前16字节，稳定内容身份|
|16|u32|核心ID：NES 0x20001、MD 0x20002、SNES 0x20003|
|20|u16|平台：NES=3、MD=4、SNES=5|
|22|u16|适配版本2|
|24|u32|NOR ROM线性起点|
|28|u32|规范ROM文件实际字节数|
|32|u32|NOR分配字节数，16 KiB对齐|
|36|u32|LNC2启动块地址|
|40|u16|完整启动块长度|
|42|u16|保留0|
|44|u32|512字节列表标题位图地址|
|48|u32|1024字节详情标题位图地址|
|52|u32|128字节名称／信息地址|
|56|u32|保留0，禁止按此字段恢复旧连续存档地址|
|60|u32|RAM实际字节数，与平台负载一致|
|64|u16|0无RAM、1独占电池RAM、2共享工作RAM|
|66|u8|能力掩码：NES=7、MD/SNES=3；非实机兼容标志|
|67|u8|保留0|
|68|u32|NES状态布局0x202f7ea8；MD/SNES为0|
|72|u32|完整SRAM bank表的NOR地址，无RAM时0|
|76|u16|表项数 = ceil(RAM字节数/8192)|
|78|u16|映射ABI=1，即8 KiB粒度完整表|
|80|u32|粒度8192|
|84|u32|bank表CRC32；空表CRC=0|
|88|u32|保留0|
|92|u32|记录前92字节CRC32|

每项是一个u8物理bank编号。元数据资产必须位于记录数组之后、头中元数据末端之前，彼此不重叠。表长度／CRC／范围必须验证；独占bank<60、不得重复、不同游戏不得重叠。共享表严格为60..60+n-1。ROM区域也不得重叠。记录、负载、RAM类型和字节数必须互相一致。

CRC32使用反射多项式0xedb88320、初值0xffffffff、最终按位取反。与当前桌面代码一致，不使用CRC作为真实性认证。

## 5. LNC2启动块

|偏移|大小|含义|
|---|---|---|
|0|4|ASCII LNC2|
|4|u32|核心ID，与记录一致|
|8|u16|版本2|
|10|u16|负载长度：NES128、MD32、SNES32|
|12|u32|负载CRC32|
|16|变长|平台负载|

完整bank表不重复塞入平台负载。MCU必须同时读取记录中的RAM信息和映射表，校验后把完整表下发给对应核心。加载顺序及SPI寄存器布局由运行端工程同步；本文不伪造已实现的寄存器或固件能力。

### NES负载：128字节，NES2 v1

标识NES2（偏移0），偏移4版本1；不是旧FC02 v7。偏移5为镜像／电池／trainer／可写CHR／PRG和CHR尺寸标志；6 submapper；8 u16 mapper。7、10、11和48–55保留0，不存旧八项bank表。

|偏移|类型|含义|
|---|---|---|
|12/16|u32/u32|PRG物理地址／字节数|
|20/24|u32/u32|CHR ROM物理地址／字节数，无CHR ROM地址0|
|28|u32|CHR RAM合计|
|32|u32|卡带RAM实际字节数|
|36/40|u32/u16|trainer物理地址／512字节；无trainer均0|
|44|u32|0x100000/CHR容量|
|56|u32|0x2000000/PRG字节数|
|60|u32|CHR容量-1|
|64/68/72/76|u32×4|状态总长度／可写CHR长度／spill起点／布局标识|
|80|4|RC01元数据标识|
|84|u8|来源修正类型0–3|
|88/92|u32/u32|工作RAM／持久CHR RAM字节数|
|96–127|32|四组8字节ASCII详情信息|

偏移5位0镜像、位1四屏、位2电池、位3trainer、位4 mapper252/268可写CHR、位5 PRG容量是二次幂、位6 CHR容量是二次幂。NES写入物理正文时把原文件头／trainer旋到正文尾，导出还原，不丢文件字节。SHA身份基于还原后的规范NES文件。

### MD负载：32字节

|偏移|类型|含义|
|---|---|---|
|0|u16|ROM起始16 KiB bank|
|2|u16|现有区域／板型flags|
|4|u32|ROM长度，16位word数|
|8|u32|保留0；不再是连续SRAM起点|
|12|u32|持久RAM实际字节数|
|16–31|16|保留0|

保留MD的区域／特殊板型识别、规范字节序、存档容量规则。所有RAM访问改按记录中的完整bank表；不可再执行save_base+offset。

### SNES负载：32字节

|偏移|类型|含义|
|---|---|---|
|0|u32|NOR ROM线性字节地址|
|4|u32|规范ROM正文实际长度|
|8|u32|RAM实际字节数|
|12|u32|规范ROM内部头位置|
|16|u64|头声明ROM字节数，保留与实际长度的差异|
|24|u8|映射：LoROM=1、HiROM=2、ExLoROM=3、ExHiROM=4|
|25|u8|原始map mode，位4含FastROM线索|
|26|u8|原始cartridge type|
|27|u8|原始country|
|28|u8|coprocessor subtype原值；缺失时0|
|29|u8|位0电池、位1checksum/complement成对有效、位2存在subtype|
|30–31|2|保留0|

头位置分别0x7fc0、0xffc0、0x407fc0、0x40ffc0。可信512字节copier头仅从内存规范副本移除，不修改原文件。缺失subtype与原值0xff通过位2区分。成对校验标志不代表计算过完整ROM checksum。

可信映射／头部允许参与合卡。特殊芯片原始字段完整保留，但桌面不替核心宣称支持；运行端按实际已实现芯片拒绝或启动，不把未知变体默认为普通板型。

## 6. 保存／更新规则

- 所有平台先保留已有逻辑bank前缀，再给增长部分分配空闲bank。不隐式搬移、缩小或切换电池类型。
- 存档归档按稳定内容ID、类型及实际字节数匹配，按逻辑顺序聚合／散写映射；不含游戏名、路径、端口或设备身份。
- 新独占区域与扩展尾部按映射逐bank初始化。已有有效字节保持不变，尾部只写新增范围。
- 当前初始化：NES为00；MD flags位3为1时00，否则FF；SNES为FF。此为明确桌面初始化策略，不宣称所有游戏首次启动行为已实测。
- 共享工作RAM不在持久存档初始化／归档恢复中擦写。
- 全部布局、ROM验证与标题渲染先完成，才开始NOR擦写；缺字体不能在擦写后才失败。
- MIX按变化扇区读改写／读回验证，先失效目录头，再写ROM及必要SRAM，最后提交有效目录头。不是全片擦除，也不承诺断电事务恢复旧卡。
- SRAM恢复不是事务性，失败不能报告成功，可重新导入重试。

## 7. 运行端接口清单

1. MCU只接受本文版本；读完整bank表及CRC，不截断为8项。
2. 三核心采用相同逻辑→物理映射公式，检查实际RAM长度与表长。
3. NES改读NES2，bank表不从旧负载取；MD删除连续起点假设；SNES实现本负载及合法芯片检查。
4. bank寄存器初始化、重置、跨核心切换需全部刷新，防止旧游戏映射残留。
5. 运行端保留已确认ROM桥、读写时序及保护约束。桌面协议更新不授权更改时钟或硬件线路。
6. 完成运行端仿真与实卡ROM／存档／断电检查后，才能另行更新兼容性结论。

## 8. 验证与代码入口

共享生产实现位于GbaMultiMenuBuilder/Fc和Snes，客户端链接同一源码，不复制另一套分配器。

- FcMbcxCodec：三平台ROM／SRAM分配、NES参数及ROM还原。
- GameLibraryDirectory：GLB2 v2、LNC2、完整映射表及存档归档。
- SnesMbcxCodec：SNES启动配置／回读／ROM一致性检查。
- FcDirectoryReader：只读目录、启动参数、名称及bank表，不读取全ROM／标题位图。
- FcCardEngine：模拟或实际传输后端的扫描、写前校验、逐bank初始化及目录提交。
- tests/MixStorage：碎片化三平台分配、128 KiB、目录／ROM往返、CRC／越界／重复bank、旧版本拒绝、归档与模拟写卡。

离线测试不访问串口、不烧录卡带、不证明游戏可运行。额外覆盖 Mapper 191/0、4095/15、未知 submapper、真实 BIN 文件导出/回读、默认勾选、集成烧录按钮、模拟写卡及写保护；这些流程不按核心白名单设门槛。

## English summary

Client2.041 and Builder0.92.2 write/read GLB2 version2 and LNC2 version2 only. No legacy decoding, migration or downgrade. NES, MD and SNES use the same 8 KiB noncontiguous SRAM map, stored as a bounded per-record asset with its own CRC. Dedicated banks0–59 cannot overlap; shared working banks60–63 remain reserved. Map length follows RAM demand, so128 KiB uses16 entries. Preserve existing logical prefixes; initialize/restore by the full map, never by first-bank-plus-offset. NES, MD and SNES are integrated platforms. The desktop accepts encodable NES mapper/submapper values (0–4095 / 0–15) without checking installed cores or runtime support whitelists; no SNES synchronization gate is added to packing, export or programming. File validity, actual storage capacity, bank overlap and card write protection remain checked. Runtime implementations follow this contract; successful packing is not evidence of gameplay or coprocessor compatibility.
