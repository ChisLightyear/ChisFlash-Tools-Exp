# 上位机 2.039 / 合卡器 0.92.1

日期：2026-10-05

## 修复 MIX 操作门槛

- 删除 NES 核心 Mapper/submapper 白名单。原先 Mapper 191/0 被解析为错误，导致默认未勾选、BIN 导出异常及烧录不可用；现在仅检查可编码范围 Mapper 0–4095 / submapper 0–15。
- 解析、手动配置、BIN 构建、卡带目录回读及集成 UI 共用可打包状态；未知但可编码的 Mapper/submapper 不阻止操作。
- NES 特殊主机原始文件与元数据保留，相关信息只提示、不拒绝；移除手动配置中的核心 CHR-ROM/CHR-RAM 容量门槛。
- NES、MD、SNES 均按已接入平台处理；移除烧录前额外的核心待同步确认，不因 SNES 接入状态禁用操作。
- 中英文 MIX 文案改为格式/数据检查失败，不将可打包状态称为游戏兼容性。

## 保留的检查

- 文件完整性、协议可编码范围、实际 NOR/SRAM 空间、独占 bank 防重叠、已有存档逻辑前缀、写保护、写前确认和读回验证。
- GLB2 v2 / LNC2 v2 协议不变，不恢复旧格式解析或迁移，不修改其他 GB/GBC/GBA/MBC5 模式或运行端固件。

## 回归范围

- MIX 离线回归 224 项：Mapper 191/0、4095/15、191/7、默认勾选、真实 BIN 导出/ROM 字节回读、延迟读卡、集成烧录按钮及模拟写卡。
- 验证截断 NES、超出编码范围、实际容量不足及卡带写保护仍拒绝；原三平台碎片化 SRAM、防重叠、存档归档及旧版本拒绝回归保留。
- SNES 基础 52 项；本地只读 SNES 样本回归分别 355/463 个成员、1459/1741 项检查；MBC5 五槽 625 项、设备身份 124 项、GB 全擦除 109 项、字体 31 项均通过。
- 测试不访问烧录器，不包含游戏文件，不证明实机游戏或特殊芯片运行。

## English

Removed the NES runtime mapper whitelist from desktop packing eligibility. Mapper 191/0 and any encodable mapper/submapper (0–4095 / 0–15) can be imported, exported and programmed. NES, MD and SNES are integrated; there is no extra pending-core confirmation. Actual file validity, storage capacity, SRAM overlap, write protection and programming confirmation remain enforced. GLB2 v2 / LNC2 v2 is unchanged. Offline tests cover real BIN export/readback and the integrated burn button without accessing hardware.
