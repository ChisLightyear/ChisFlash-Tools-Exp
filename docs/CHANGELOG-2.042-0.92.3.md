# 上位机2.042 / 合卡器0.92.3

日期：2026-10-05

- 修复LK菜单黑色预览。mGBA运行同一份普通/水线LK菜单确认白底；先前“黑色正常”的结论及对应测试已更正。
- 根因：按字体标记减固定0/0x40间距，实际间距为0x08/0x48，导致位图和调色板位置都多偏移8字节；白色索引读到错误的黑色调色板项。
- LK改为从ROM引用的位图/调色板地址定位，验证连续范围且结果唯一。预览、卡带扫描、背景更新/保留、合卡器读取/替换统一使用同一定位器，避免错误范围写入。
- 保留NS精简默认和明确菜单选择；NS资源保持带签名的布局。菜单资源二进制及固件源码均未修改。
- 回归检查mGBA实际DMA地址、白色像素、读写一致、替换不影响代码/字体、无引用或歧义时拒绝猜位置。
- 本地mGBA截图为ROM启动画面，非实卡验证；编译缓存交付后清理。

详见[GBA菜单选择与预览](GBA-MENU-SELECTION.md)。

## English

Corrected LK black previews: the actual mGBA-rendered menu has a white background. The old fixed font-marker gap placed both bitmap and palette eight bytes too late. LK background extraction and replacement now share a unique ROM-pointer-based locator with range validation; code and font data outside that region are preserved. Explicit menu selection and the Simplified NS default remain. Firmware/menu ROM assets are unchanged.
