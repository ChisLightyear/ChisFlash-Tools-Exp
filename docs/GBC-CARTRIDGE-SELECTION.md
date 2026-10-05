# GBC 烧录卡选型规则

上位机 2.040。容量只决定候选卡型，用户选择卡型后才决定工作模式；不根据容量、卡内目录或旧选项推断固件。

## 候选列表

| 实际 NOR 容量 | 候选卡型 | 工作模式 |
| --- | --- | --- |
| 8 MiB | MBC5 8M · 3in1 / 4in1 | 原有五槽：菜单＋游戏1～4 |
| 8 MiB | MBCX RTC 8M | 一个 ROM／完整 BIN 镜像 |
| 32 MiB | MAX 32M · 16in1 | 已制作好的完整 BIN 镜像 |
| 32 MiB | MBCX RTC 32M | 一个 ROM／完整 BIN 镜像 |
| 32 MiB | 自由合卡 32M · GB/GBC | 原有 GB/GBC 自由合卡工作区 |
| 32 MiB | MIX · NES / MD / SNES | MIX v2 工作区 |

未读取容量时显示“未读取容量 · 请选择卡型”，不默认选择8M；离线用户可以明确选型。检测到8/32 MiB后只显示对应候选；其他已测容量显示实际数值并保留“请选择”，不虚构板型。

## 扫描与切换

- 用户还未选型时，GB扫描只探测NOR/CFI，不自动解释为自由合卡，不从MIX目录自动进入MIX。
- 扫描后保留容量仍匹配的显式选型；8M→32M或32M→8M使旧选型失效，回到“请选择”，不擅自选另一固件。
- 切换语言、刷新界面或切换工作区，不遗失已检测的同端口容量；切换端口、GB/GBA或探测失败时清除容量缓存。
- RTC和16in1完整镜像流程保留导入字节，尾部补FF至所选卡型容量，不插入自由合卡菜单、MBC配置或存档目录。16in1布局制作不在本模式内完成。
- 完整镜像烧录先重新核对实际NOR身份、容量及扇区几何，再全擦除NOR、写入、逐bank读回校验；SRAM不擦除。
- 写前确认、写保护和实际容量检查保留；不检查核心Mapper白名单或固件支持名单。
- GB/GBC自由合卡、MIX v2、五槽、GBA/WaterLink保持各自规则；不修改固件或恢复旧MIX格式。

## 验证

离屏UI和模拟NOR验证覆盖8/32 MiB候选、旧8M选型清除、MIX目录不自动切换、语言与端口切换、完整镜像字节保留/FF填充、32M高位bank及写前容量不符拒绝。测试不访问串口，不证明实机烧录或游戏运行。

## English

Detected NOR capacity filters cartridge candidates; only an explicit user choice selects the execution mode. Unread capacity never defaults to 8M. 8 MiB exposes fixed MBC5 3/4in1 and MBCX RTC 8M; 32 MiB exposes 16in1, MBCX RTC 32M, GB/GBC free multicart and MIX. A compatible explicit choice survives scans and language refreshes; incompatible capacity or a changed port clears it. ROM-directory signatures never choose firmware. RTC/16in1 use prepared complete images without free-multicart metadata insertion. Full-image writes verify actual NOR geometry before erasing and verify every bank afterward; SRAM is untouched.
