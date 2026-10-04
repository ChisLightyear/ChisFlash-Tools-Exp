# SNES MIX 导入预览

适用：上位机2.037／合卡器0.91.3。这是SNES识别、导入和单ROM导出预览，不是已完成的SNES合卡烧录功能。

## 怎么试

1. 进入烧录卡选型中的MIX模式，选择添加ROM，或把本地`.sfc`、`.smc`、ZIP拖入列表；无需连接卡带。
2. 查看核心列的`SNES`与Mapper列的LoROM／HiROM／ExLoROM／ExHiROM；信息来源与单元格提示给出特殊芯片／区域线索及拒绝原因。
3. SNES行默认不勾选。尚未确认启动协议，不能勾选后直接生成可启动的MIX镜像或写卡，也不能用NES配置编辑器修改它。
4. 上位机选中行后可使用ROM导出，得到`.sfc`文件。合卡器使用单ROM导出操作。可信copier头从导出副本移除，ROM正文不重排、不修补、不改变原始文件。

ZIP中每个游戏成员单独显示；资源叉、说明文件和嵌套ZIP不导入。路径仅在内存中按成员索引读取，不按压缩包里的相对路径解压。多游戏ZIP不会只拿第一份ROM。

## 容量与识别边界

- 接受32K对齐、32K～8M的ROM正文，可附带512字节copier头。不是任意交错／压缩SNES格式的转换器。
- 自动`.bin`分类目前探测普通LoROM／HiROM头；扩展映射请保留`.sfc`／`.smc`扩展名。
- 容量以实际ROM正文显示，头部声明不一致会提示；不会默默裁切或填充到声明长度。
- 「头部校验对有效」只指checksum／complement成对字段，不代表计算并验证了完整ROM校验和。
- 芯片名称是头部线索，不是已安装核心的支持表；未知变体、异常或歧义不会自动当作普通板型。
- SRAM需求只展示，不分配、迁移或初始化SNES存档。修改版识别成功也不代表游戏能运行。

## 接下来缺什么

需要核对配套SNES MCU／核心／菜单源码中的核心标识、平台编号、启动负载版本，以及NOR寻址、映射／速度参数、特殊芯片准入与SRAM寻址／上限。随后才能实现SNES目录生成／回读、混合分配、配置重建、存档管理和写卡校验。不能随便取一个新ID或沿用MD格式。

在此之前，原NES／MD合卡仍可用；未勾选的SNES行不会写入它们的镜像。NES／MD标题位图仍需自备配套12px字体，预览包不分发外部字库或游戏。

## English

Select MIX, then add/drop SFC, SMC or ZIP files. Inspect mapping, ROM/RAM sizes and chip hints. Individual SFC exports preserve the ROM body and remove only a credible 512-byte copier header from the copy. SNES rows are excluded from packing/programming until the matching core ID, launch payload and firmware/menu contract are confirmed. No SNES save management, hardware programming or gameplay validation is claimed.
