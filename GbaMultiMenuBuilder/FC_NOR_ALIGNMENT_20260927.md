# FC02 v7：NOR PRG载荷对齐（2026-09-27）

合卡器0.87 / 客户端2.027，与FPGA-DMG-02的0038核心配套。旧卡请从原NES/ZIP重新制作并烧录；不解析旧载荷布局，不能只改版本字节。机内SPI Flash无需格式化。

分配长度仍是原NES文件长度向上对齐16KiB，不额外占用ROM银行。卡内排列为 `[PRG][CHR及文件尾部数据][原16字节NES头][可选512字节Trainer][FF填充]`。PRG基址就是分配起址，CHR源地址为PRG基址+PRG长度，Trainer源地址为分配起址+原文件长度−512。扫描时旋转回原文件，NES头、Trainer和附加尾部逐字节保留。

FC02配置升至v7；MBCXCFGv2/MBCXROMv3、12px标题、8KiB WRAM bank表和拆分快照字段不变。由PC预先计算；FPGA不解析NES头。FcMbcxCodec.CopyStoredData统一处理导出及卡带扇区写入，跨载荷/头/Trainer边界的分段写入一致；保留邻居扇区、未改ROM跳写、目录最后提交、写前字体渲染和过期扫描保护。

验证：生产程序集对276个真实ROM及2个原始诊断ROM共1782项检查通过，包含逐字节重构、对齐/CHR/Trainer位置、137字节分段、FF填充、BIN导出/导入、扇区共享邻居、重命名重排不写ROM、过期扫描和非法配置拒绝。原12个大快照分配不变。Windows x64自包含发布成功，保留外置Assets/Fonts/mplus_hzk_12.ttf。未操作烧录器。
