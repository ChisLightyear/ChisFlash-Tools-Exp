# Client 2.035 / Builder 0.91.2

## English

- Shared AG32 USB identifiers no longer identify a device as WaterLink by themselves. WaterLink classification requires an explicit USB product identity and the expected VID/PID/interface.
- Read bus-reported USB product metadata, including the composite parent device. Generic CDC interface labels, manufacturer names and Windows friendly names do not establish WaterLink identity.
- Missing or ambiguous metadata stays Unknown. Unconfirmed devices show their product name in the port list and do not receive programmer-command access. Older generic-name firmware needs a dedicated product identity before it can be identified reliably.
- GBA/GB controls remain visible. Confirmed WaterLink devices select GBA with both options disabled; idle Beggar devices retain selectable GBA/GB modes. Programmatic selection does not run the user-change handler or invalidate scan state.
- Includes 124 offline product-identity/mode-selector checks. No single-ROM programming workflow or cartridge-firmware changes are included in this update.

Builder remains at 0.91.2. Existing downloads are unchanged. Offline checks and builds do not constitute physical cartridge read/write validation.

## 简体中文

- 不再仅凭 AG32 共用 USB ID 就判定 WaterLink。必须同时具备明确的 USB 产品身份及匹配的 VID/PID/接口。
- 读取 USB 总线上报的产品信息，包括复合设备父节点；通用 CDC 接口名称、厂家名称和 Windows 友好名称不作为 WaterLink 产品依据。
- 产品信息缺失或不明确时保持未知。串口列表显示未确认设备的产品名，不开放烧录器命令；旧固件若仅提供通用名称，需增加专属产品身份后才能可靠识别。
- GBA/GB 选框保留显示。确认 WaterLink 后选中 GBA、两项置灰；丐中丐空闲时仍可切换 GBA/GB。程序自动勾选不会触发用户切换处理或使扫描状态失效。
- 新增 124 项产品识别与模式选框离线检查。本次不包含普通单 ROM 烧录流程，也不修改卡带固件。

合卡器保持 0.91.2，现有下载包不变。离线检查与编译通过不代表实卡读写验证完成。
