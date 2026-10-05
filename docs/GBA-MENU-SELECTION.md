# GBA 菜单必须明确选择

上位机2.042 / 合卡器0.92.3。默认NS精简菜单；没有“自动菜单”。

## 菜单选项

- NS精简菜单：默认，使用simplified_ns_menu.gba。
- NS完整菜单：明确选择后使用universal_menu.gba。
- LK菜单：明确选择后使用对应卡带布局的LK资源；水线布局用wl_magic_menu.gba，普通布局用lk_multimenu.gba。卡带布局只决定同一LK菜单的适配资源，不决定菜单类型。

上位机空配置、未知值及旧auto值均按NS精简处理；不再根据当前卡带菜单签名自动决定菜单类型。已保存的明确LK或NS完整选择保留。合卡器无菜单设置时也默认NS精简，已有明确菜单设置保留。

## 黑色预览的原因

LK菜单运行时的列表背景为白色。此前黑色预览来自背景区定位偏移：按字体标记减固定间距会多偏移8字节，位图和调色板一起错位，白色像素索引被映射成黑色。mGBA运行同一份菜单已确认白底，之前“黑色正常”的结论撤销。

现在LK按ROM代码引用的位图/调色板地址定位，并要求两者连续且结果唯一；上位机扫描、预览、背景更新与合卡器读取/替换共用定位器，不再猜字体前的固定间距。水线资源对应位图0xAED8、调色板0x144D8；普通资源对应0xAD18、0x14318。NS资源保留其带签名的布局规则。

旧界面改变菜单选择后只更新操作按钮，没有重建预览，仍可能显示扫描到的LK背景。现在选择变化立即刷新：

- 选型与扫描到的菜单一致时，保留当前卡带的背景或首屏预览，包括用户已修改的内容。
- 选型不同、尚未扫描或没有有效菜单时，从明确选中的菜单资源生成预览，标注“所选菜单／待写入”。
- 待写入预览只读，不使用当前卡带的封面浏览器或背景写入操作冒充目标菜单；更新菜单或烧录后才实际生效。
- 选中资源缺失时显示无法加载，不悄悄换成另一菜单。

预览为桌面绘制，不是实机截图；NS首屏以菜单类型和目录/封面元数据绘制，LK背景按ROM中的位图及RGB555调色板解码。

## 验证

回归覆盖明确菜单默认值、mGBA确认的白色像素及DMA源地址、两种LK资源、上位机/扫描器/合卡器同址、背景替换范围与代码/字体保护、无引用/歧义定位拒绝、菜单切换及无卡预览。桌面测试不访问烧录器；mGBA只运行原菜单ROM，不修改固件源码。

## English

Menus are explicit: Simplified NS (default), full NS, or LK. Cartridge layout never auto-selects a menu family. Old auto/unset values resolve to Simplified NS. LK has a white list background in mGBA. The previous black preview was an eight-byte bitmap/palette offset error; that explanation is corrected. LK now uses unique ROM references to the contiguous bitmap and palette, shared by reading and writing paths. Selection changes immediately refresh the preview. A different or uninstalled selected menu is shown as pending and does not modify the cartridge. Matching installed menus retain the cartridge preview. Missing assets are reported without silently switching menus.
