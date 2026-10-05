# 上位机 2.040 / 合卡器 0.92.1

日期：2026-10-05

- GBC烧录卡候选改按实际NOR容量筛选：8 MiB只提供五槽3/4in1及MBCX RTC 8M；32 MiB提供16in1、MBCX RTC 32M、GB/GBC自由合卡和MIX。
- 增加“未读取容量／请选择卡型”，不默认选8M；删除扫描后无条件保留8M及按GBC/MAX身份自动选择自由合卡的逻辑。
- 容量只筛选候选，工作区由显式选型决定；检测到MIX目录或导入其他平台ROM不自动替用户选型。
- 切换端口清除容量和选型；语言刷新保留匹配选型；容量改变时只清除不匹配选型，不猜另一固件。
- RTC及16in1新增完整镜像导入、导出和烧录路径；保留原始字节，尾部FF，不生成自由合卡菜单或存档目录。16in1使用外部已制作的完整镜像。
- 共用完整NOR写入器支持8/32 MiB真实几何、全擦除、逐bank写入及读回校验；MBC5五槽保留8 MiB限定。SRAM不擦除。
- 合卡器版本保持0.92.1；MIX v2存储协议及NES Mapper不设核心白名单的规则保持。
- 构建后清理bin/obj和临时验证编译输出，保留验证源码、文档、源资产及交付包。

离线回归：选型及完整镜像36项、五槽625项、MIX存储224项、SNES基础52项、设备身份124项、GB全擦除109项均通过。

规则及平台模式见[GBC烧录卡选型](GBC-CARTRIDGE-SELECTION.md)。离线回归不访问烧录器，不宣称实卡验证。

## English

Client 2.040 filters GBC cartridge profiles by measured NOR capacity and requires explicit user selection; neither capacity nor ROM-directory signatures infer firmware. RTC/16in1 profiles use complete prepared images without injecting a free-multicart directory. The shared NOR writer supports full erase and byte verification for 8/32 MiB images while preserving SRAM. Builder remains 0.92.1 and MIX v2 protocol/mapper-neutral packing is unchanged. Temporary build outputs are cleaned after validation; source and delivery artifacts remain.
