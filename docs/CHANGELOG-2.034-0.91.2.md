# Client 2.034 / Builder 0.91.2

## English

- GB/MAX now supports Cartridge > Erase Entire NOR. Its byte-write chip-erase sequence matches Beggar's MBC5 client; it does not use the GBA erase command.
- Erase no longer requires recognized menu/directory content or a chip-name whitelist. Existing GB/GBC, FC or MD contents do not restrict raw NOR erase. Scanned MIX cartridges expose the same action.
- A selected compatible programmer, a valid physical scan/capacity and both destructive-operation confirmations remain required. Separate SRAM is not erased.
- The GB path polls completion with a timeout, then verifies every byte across the detected capacity, including the final partial bank. Failed commands, reads, verification or bank restoration do not report success.
- Starting erase invalidates the previous scan. MIX keeps imported work but requires its own rescan before further card operations. Errors also require a fresh scan.
- Includes 109 offline fake-transport checks; the Windows client builds successfully. Physical-cartridge validation is pending. Builder remains at 0.91.2.

This source update does not replace the existing 2.031 / 0.91 downloads or change bundled menu kernels. It does not install cartridge firmware or establish game compatibility.

## 简体中文

- GB/MAX 新增“卡带 → 全片擦除”。采用与 Beggar 原版 MBC5 上位机一致的字节写全片擦除命令，不复用 GBA 擦除命令。
- 不再要求识别到菜单、合卡目录或芯片名称白名单；卡内为 GB/GBC、FC 或 MD 内容均不限制原始 NOR 全片擦除。已扫描的 MIX 卡也开放同一入口。
- 保留兼容烧录器选择、有效物理扫描/容量与两次危险操作确认；不会擦除独立 SRAM。
- GB 擦除完成轮询带超时，随后逐字节校验检测到的全部容量，包含最后不足一个分区的尾部。命令、读取、校验或分区恢复失败都不报成功。
- 开始擦除后原扫描结果失效。MIX 保留已导入工作，但后续卡带操作必须重新扫描；擦除异常后也需重新扫描。
- 新增 109 项离线假传输回归检查，Windows 上位机编译通过；尚待实卡验证。合卡器版本保持 0.91.2。

本次源码更新不替换现有 2.031 / 0.91 下载包，也不改变配套菜单内核；不安装卡带固件，不代表游戏兼容性已经验证。
