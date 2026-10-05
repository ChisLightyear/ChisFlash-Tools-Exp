# 上位机2.041 / 合卡器0.92.2

日期：2026-10-05

更正：本版“LK背景本来为黑色”的结论错误，已在2.042/0.92.3修复。mGBA确认同一菜单运行时为白底；原因是背景位图与调色板定位偏移8字节，见后续更新日志。

- 删除上位机GBA自动菜单选项及自动保留/识别分支。NS精简、NS完整、LK三种菜单明确指定，默认NS精简。空配置、异常值和旧auto配置不再选LK。
- 烧录、导入、菜单更新、01G/02G菜单处理和封面编辑统一使用明确菜单选择。水线/普通卡带布局不再决定菜单类型。
- 修复改变菜单后预览未刷新的问题；菜单尚未写入时从所选资源生成预览并标注待写入，不覆盖扫描数据。
- LK背景原始游戏列表区为黑色，背景预览不包含运行时标题；保留准确解码，不将黑色当作图像加载失败。
- 合卡器缺省菜单也改为NS精简；保留已保存的明确菜单选择。
- 菜单回归26项、GBC选型36项、MBC5五槽625项、MIX存储224项、SNES导入合卡52项均通过；没有实机烧录/运行验证。保留GBC容量选型、MIX v2和Mapper不设核心白名单的规则。
- 交付后清理bin/obj及临时编译输出，保留源资产、文档、测试源码及交付包。

详见[GBA菜单选择规则](GBA-MENU-SELECTION.md)。不修改菜单固件源码，不推送远程。

## English

Removed automatic GBA menu selection. Both tools default to Simplified NS while preserving explicit full NS/LK choices. Menu changes refresh the preview immediately; pending selected-menu previews are labeled and do not mutate cartridge data. The LK background intentionally contains a black list area. Regression tests cover explicit/default menu selection and previews without hardware access.
