# GBA 菜单必须明确选择

上位机2.041 / 合卡器0.92.2。默认NS精简菜单；没有“自动菜单”。

## 菜单选项

- NS精简菜单：默认，使用simplified_ns_menu.gba。
- NS完整菜单：明确选择后使用universal_menu.gba。
- LK菜单：明确选择后使用对应卡带布局的LK资源；水线布局用wl_magic_menu.gba，普通布局用lk_multimenu.gba。卡带布局只决定同一LK菜单的适配资源，不决定菜单类型。

上位机空配置、未知值及旧auto值均按NS精简处理；不再根据当前卡带菜单签名自动决定菜单类型。已保存的明确LK或NS完整选择保留。合卡器无菜单设置时也默认NS精简，已有明确菜单设置保留。

## 黑色预览的原因

LK资源的游戏列表区域本身为黑色。背景预览显示的是背景位图，没有游戏标题等运行时叠加层；因此黑色列表区域不代表解码失败或游戏无法运行。

旧界面改变菜单选择后只更新操作按钮，没有重建预览，仍可能显示扫描到的LK背景。现在选择变化立即刷新：

- 选型与扫描到的菜单一致时，保留当前卡带的背景或首屏预览，包括用户已修改的内容。
- 选型不同、尚未扫描或没有有效菜单时，从明确选中的菜单资源生成预览，标注“所选菜单／待写入”。
- 待写入预览只读，不使用当前卡带的封面浏览器或背景写入操作冒充目标菜单；更新菜单或烧录后才实际生效。
- 选中资源缺失时显示无法加载，不悄悄换成另一菜单。

预览为桌面绘制，不是实机截图；NS首屏以菜单类型和目录/封面元数据绘制，LK背景按ROM中的位图及RGB555调色板解码。

## 验证

26项回归覆盖菜单选项、空/auto配置默认、明确LK/NS保留、水线和普通布局加载NS精简、LK实际黑色像素、菜单切换立即刷新、待写入标记、不修改扫描数据、无卡预览及合卡器默认值。测试不访问烧录器，不修改菜单固件源码。

## English

Menus are explicit: Simplified NS (default), full NS, or LK. Cartridge layout never auto-selects a menu family. Old auto/unset values resolve to Simplified NS. The LK list area is black in its bundled background; background-only decoding does not include runtime game titles. Selection changes immediately refresh the preview. A different or uninstalled selected menu is shown as pending and does not modify the cartridge. Matching installed menus retain the cartridge preview. Missing assets are reported without silently switching menus.
