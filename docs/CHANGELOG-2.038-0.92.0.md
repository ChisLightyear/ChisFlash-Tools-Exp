# 上位机2.038／合卡器0.92.0 — MIX v2

## 改造结果

- NES、MD、SNES共用ROM／SRAM分配器；MD及SNES支持非连续SRAM bank。目录保存完整映射表，不从首bank推导后续地址。
- 单游戏bank表不再限制8项；按实际需求及独占60／共享4个物理bank池检查。128 KiB RAM使用16项；NES RAM配置编辑范围同步到独占池容量。
- SNES接入混合布局、GLB2目录生成／快速回读、启动负载、延迟ROM读取、整卡镜像及存档归档／恢复。可信SNES默认参与合卡，特殊芯片仅保留元数据线索。
- 只写入／读取GLB2 v2、LNC2 v2及新NES2启动负载。删除旧MIX目录解码与MD连续分配分支，不迁移旧格式，不双写、不降级。
- 三平台的已有存档逻辑bank前缀保持；新增／扩展区域逐映射bank初始化，持久归档不写共享工作区。目录头最后提交，NOR逐扇区读回检查。
- 两工具共享同一生产源码；中英文UI统一NES＋MD＋SNES入口，并在烧录确认中提示v2需要同步MCU／核心。
- 长期规则见仓库AGENTS.md；二进制字段、CRC、寻址公式、初始化策略及运行端同步清单见[MIX v2存储协议](MIX-V2-STORAGE-CONTRACT.md)。

## 验证

- MIX存储回归169项：碎片化三平台分配、128 KiB、超过99个游戏、完整映射往返、CRC／越界／重复bank、归档、旧版本拒绝、模拟写卡、目录头提交顺序、失败与取消路径。
- SNES基础／离屏UI52项；本地只读样本355项源文件／成员参与测试，合计1459项检查；另一组463项源文件／成员合计1741项检查。可信头分别348和381；可信且符合SRAM池需求的样本逐项验证新启动配置与规范ROM字节往返。源ROM／ZIP未改写，不进入源码或交付包。
- MBC5五槽625项、设备身份124项、GB全擦除109项、12px字体31项通过。
- 两工具Release编译与win-x64自包含发布通过。保留客户端既有Form1.Actions.cs不可达代码警告CS0162，不处理无关分支。
- 所有设备测试使用假后端；没有串口连接、卡带擦写或实机游戏测试。合卡成功不是运行兼容性验证。

## 使用边界

旧卡先备份ROM／存档，再从原始ROM重建。旧MCU／核心不能启动本格式；运行端必须按协议同步完整bank表和SNES启动参数。特殊芯片准入由实际核心实现决定，不能把识别到芯片写成已经支持。其他GB/GBC/GBA、MBC5模式保持各自格式及流程。

交付程序不含游戏、封面图库、外部字体或私人配置。已有中文标题依然需要自备配套12px字库。

## English

Client2.038 / Builder0.92.0 introduce MIX v2-only storage with a shared noncontiguous SRAM allocator for NES, MD and SNES. Complete bank maps are bounded and CRC-checked per-record assets;128 KiB uses16 entries. SNES now participates in packing, directory scanning, launch metadata, ROM export and save archives. No old-format decoder/migration/downgrade remains. Existing save prefixes are retained and new bytes initialized through each mapped bank. The matching MCU and cores must implement the documented contract before gameplay. Offline checks and successful packing are not hardware or coprocessor compatibility evidence.
