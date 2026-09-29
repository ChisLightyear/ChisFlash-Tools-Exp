# 0.90 — MIX FC configuration correction

MIX can review and rebuild FC runtime configurations directly from games already on the card. Known content fingerprints and validated database entries identify legacy mapper mistakes; unknown boards remain marked for review. The FC configuration editor supports manual mapper/RAM settings without changing original ROM bytes. Overrides and RAM splits persist in MIX images and subsequent card scans.

A bounded reset-path analyzer recognizes the expanded-RAM startup copier used by some 640 KiB Chinese MMC3 variants. It offers Mapper 198 as an unverified candidate; it does not silently change unknown game variants.

Configuration-only updates retain ROM addresses and skip unchanged ROM sectors. FC RAM expansion preserves existing save-bank prefixes, allocates additional free banks, and initializes only new RAM. Capacity and incompatible save changes are rejected before writing. Existing FC02 v7 firmware remains compatible.

# 0.89 — MIX FC / MD

- Replace NES with the shared MIX workspace. FC and MD can coexist on one MBCX cartridge, with 16 KiB-aligned ROM allocation.
- Share 12px list/detail title mappings; preserve existing save allocations and initialize only new MD saves.
- Verify mixed NOR writes and publish both directories after payloads and saves are ready. Requires the paired MIX firmware.

## 0.82 NES — 2026-09-26

## 0.83 NES — 2026-09-26

- Remove obsolete core memory size limits from NES import and cartridge rescans; preserve full ROM payloads.
- Keep real cartridge and SRAM allocation bounds, mapper/header checks, and Mesen corrections.
- Fix legacy directory length overflow. Label validation as Import check rather than runtime compatibility.
- Pair with cartridge client 2.023 and DMG-02 release 0032. Shared production-code verification: 24 checks passed; no hardware writes.

- Accept non-power-of-two PRG/CHR files; retain byte-exact ROM payloads and physical bounds.
- Use header fallback for Mesen geometry mismatches and reserve real trainer RAM.
- Pair with the new 02 cartridge WRAM / native snapshot firmware.

## 0.81 NES — 2026-09-25

- Pair with the 02 runtime PRG bridge: accept MMC3 CHR up to 256 KiB, CNROM CHR up to 512 KiB, and MMC1 PRG-RAM up to 32 KiB.
- Preserve original NES bytes and MBCX directory layout. Hardware validation pending.

## 0.80 NES — 2026-09-25

- Add Mapper 1 / MMC1 header support, including submapper 0..5 and the existing PRG/CHR/RAM geometry checks used by the 02 FC core.
- Keep Mapper 0/2/3/4 rules unchanged; Mapper 1 adds MMC1 support; incompatible capacity or header combinations remain visible and unchecked.

## 0.79 NES — 2026-09-24

- Show actual included entries, separate unchecked/incompatible counts and visible reasons; output completion reports the actual directory count.
- Matching client 2.019 removes model-whitelist UI locks and routes detected FC cards directly to the NES workspace.

## 0.78 NES — 2026-09-24

- Shared FC workspace enables Burn after importing ROMs. The client performs a read-only card scan before confirmation, including erased MBCX cards; existing FC entries and write protection remain respected.

## 0.77 NES — 2026-09-24

- NES / CHISBOY MBCX mode accepts Dendy NES 2.0 headers and Mesen Dendy metadata for the matching 02 core.

# 0.75 FC — 2026-09-24

- 新增嵌入式 FC 工作区，两端共享 NES/MBCX 编解码。
- 支持 NES/ZIP 与 FC BIN，Mesen 数据库识别 RAM/电池，8 KiB 存档布局预留。
- 原始 ROM 不改；16 KiB ROM 银行，32 MiB / 99 项限制。
- 无测试/模拟器/硬件写入；存档只预留，尚未接通 02 持久化。

# 更新日志

## v0.74 - 2026-08-02

- Allow the upper computer to relocate an expanded NS cover pack into the erased menu-head gap instead of rejecting it because the previous pack allocation was smaller.
- Keep cover input downscaling and the GBA 56x56 indexed cover format unchanged.

## v0.73 - 2026-08-02

- Added the NS-only Minimal Package option for exporting the menu and cover area without rewriting the configuration area.
- Aligned classic-menu configuration fallback and cover ordering across normal NS and Simplified NS firmware.
- Added DPI-safe menu-type controls and synchronized the packaged NS firmware with the upper-computer client.

## Unreleased - 2026-07-10

- Restore the stable NEW3 Chinese and English menu runtimes; the in-development CHISNP unified runtime is no longer packaged.
- Emit `MBCXLYR1` version 6 foreground glyph masks for transparent GBC menu text.
- Preserve the original Fusion Pixel 8px monospaced English glyphs while making their background transparent.

## v0.65 - 2026-05-10

### 修复

- 修复 GBC 合卡器预览中的数字、符号、页码与真实菜单字体不一致的问题，改为使用同源像素字形。
- 修复 GBC 主菜单预览顶部、底部文案和页码的渲染错位问题。
- 同步 GBC 预览的标题长度限制，使主菜单和详情页都与真实菜单使用同一套截断规则。
- 重做 GBC 详情页预览布局，改为和真实菜单一致的字段结构与位置。
- GBC 模式下隐藏“自动分配存档槽”按钮，并先隐藏“导出字库”按钮，简化界面。

### 说明

- 版本号从 `0.63.0` 提升到 `0.65.0`。
## v0.63 - 2026-05-09

### 淇

- 鍙栨秷 GBC 涓昏彍鍗曟爣棰樻粴鍔紝鎭㈠闈欐€佹埅鏂紝闄嶄綆杩愯鏃舵€ц兘鍘嬪姏銆?- 璋冩暣涓昏彍鍗曟爣棰樻帓鐗堣鍒欙紝淇鑻辨枃瀛楁瘝鏁伴噺涓哄崟鏁版椂鍚庣画涓枃鏄剧ず鍗婁釜瀛楃殑闂銆?- 鍚屾鍚堝崱鍣ㄤ笌鑿滃崟 runtime 鐨勬爣棰橀暱搴﹀拰 tile 棰勭畻閫昏緫锛岄伩鍏嶆柊鑿滃崟灞傚拰鏃?runtime 涓嶄竴鑷淬€?
### 璇存槑

- 鐗堟湰鍙蜂粠 `0.62.0` 鎻愬崌鍒?`0.63.0`銆?
## v0.62 - 2026-05-09

### 淇

- 淇 GBC 鑿滃崟椤电爜搴曞浘姹℃煋瀵艰嚧闈欐鏃惰姳灞忕殑闂銆?- 淇涓荤晫闈㈤〉鐮佸彞鐐瑰榻愶紝閬垮厤涓綅鏁颁负 `1` 鏃剁偣浣嶅亸宸︺€?- 璋冩暣 GBC 鑿滃崟鏍囬闀垮害澶勭悊锛氳嫳鏂囨寜鏇寸獎瀛楀璁＄畻锛屽苟涓鸿秴闀挎爣棰樼敓鎴愭粴鍔ㄦ暟鎹€?- 鏍囬婊氬姩鏀逛负闈為樆濉炶鏁拌Е鍙戯紝鏀寔鏇村揩婊氬姩鍙傛暟锛屽苟淇灏鹃儴瀛楃娈嬬暀銆?- 淇闈欐€佹爣棰樿秴鍑烘粴鍔ㄧ獥鍙ｅ鑷村彸渚ф畫鐣欑殑闂銆?
### 璇存槑

- 鐗堟湰鍙蜂粠 `0.61.0` 鎻愬崌鍒?`0.62.0`銆?
## v0.61 - 2026-05-09

### 淇

- 璋冩暣 GBC 12px 瀛椾綋娓叉煋涓庝綅鍥捐鍙栵紝淇鍙樉绀哄彸鍗婅竟鐨勯棶棰樸€?- 淇鑿滃崟涓殑鍙ュ彿銆佸啋鍙风偣浣嶆牱寮忎笌鍨傜洿浣嶇疆锛屾仮澶嶄负鏇寸鍚?UI 棰勬湡鐨勫疄蹇冪偣鏁堟灉銆?- 寰皟 MBC 閫夋嫨灏忕獥涓庤鎯呴〉鐨勬寜閿煶鏁堟椂鏈猴紝浼樺寲璇︽儏椤垫柟鍚戦敭涓庡脊绐楁搷浣滄墜鎰熴€?
### 璇存槑

- 杩欐槸涓€娆″皬淇ˉ鐗堟湰锛岀増鏈彿浠?`0.6.0` 鎻愬崌鍒?`0.61.0`銆?
## v0.6 - 2026-05-08

### 璋冩暣

- 鏋勫缓璁剧疆闈㈡澘甯冨眬浼樺寲锛氳楂樼粺涓€銆佹帶浠朵笂涓嬭竟璺濆榻愩€佽緭鍏ユ涓庝笅鎷夋楂樺害涓€鑷淬€?- GBC 妯″紡涓枃瀛楀簱鏍囩淇涓轰腑鏂囨樉绀恒€?- 鐗堟湰鍙蜂粠 `0.5.0` 鎻愬崌鍒?`0.6.0`銆?
## v0.5 - 2026-04-16

### 璇存槑

- 鍙戝竷 `v0.5` 娴嬭瘯鐗堟墦鍖呫€?- 鐗堟湰鍙蜂粠 `0.4.0` 鎻愬崌鍒?`0.5.0`銆?
## v0.4 - 2026-04-11

### 璇存槑

- 鍙戝竷 `v0.4` 娴嬭瘯鐗堟墦鍖呫€?- 鍚屾褰撳墠宸ョ▼鐘舵€侊紝渚涙湰杞祴璇曢獙璇佷娇鐢ㄣ€?
## v0.3 - 2026-04-07

### 鏂板

- 杩愯鏃跺厤鐢佃ˉ涓侀摼璺寮忕粺涓€鍒?`compat-auto-detect` payload锛屼綔涓哄綋鍓嶅敮涓€鐨勬寮忓疄鐜拌矾寰勩€?- 鏋勫缓鍑嗗闃舵鏀寔鎸?ROM 骞跺彂澶勭悊锛屾渶澶?`8` 涓换鍔″苟琛岋紝鏈€缁堟帓甯冨拰杈撳嚭鍐欏叆浠嶄繚鎸佸崟绾跨▼銆?- `config.ini` 鐜板湪浼氫繚瀛樻洿澶氳繍琛岀姸鎬侊紝鍖呮嫭鏋勫缓閫夋嫨椤广€佽儗鏅浘璺緞銆佺獥鍙ｅ昂瀵哥姸鎬佸拰娓告垙鍒楄〃鍒楀銆?
### 璋冩暣

- 鍏嶇數琛ヤ竵鐜板湪瑕佹眰鍏堟弧瓒?SRAM 灏辩华鏉′欢锛涘凡缁忔墦杩?SRAM 琛ヤ竵鎴栧厤鐢佃ˉ涓佺殑 ROM 涓嶄細鍐嶉噸澶嶅鐞嗐€?- 瀹归噺浼扮畻鏀逛负涓ら樁娈垫鏌ワ細瀵煎叆闃舵鎸夊師濮?ROM 浣撶Н鍒ゆ柇锛屾瀯寤洪樁娈典細鎶婂厤鐢垫墿瀹逛竴璧疯鍏ワ紝鍐嶅仛鏈€缁?`4 MiB` 鍒嗘。浼扮畻銆?- 鏈€灏?ROM 澶у皬鍥哄畾涓?`4 MiB`锛孯OM 鍗犱綅缁熶竴鎸?`4 MiB` 鍚戜笂鍒嗘。銆?- UI 閫夐」杩涗竴姝ユ敹鏁涘埌褰撳墠瀹為檯鏀寔鐨勭‖浠舵祦绋嬶紝鍙繚鐣?`128MiB (01G)`銆乣256MiB (02G)` 鍜岀幇闃舵闇€瑕佺殑 Flashcart 妯″紡銆?- 灏忓閲?ROM 鐨勫厤鐢佃ˉ涓佷紭鍏堝皾璇曞鐢?ROM 灏鹃儴绌虹櫧鍖猴紝鍙湁鎵句笉鍒板悎閫備綅缃椂鎵嶄細瑙﹀彂 `0x80000` 鎵╁銆?- 榛樿 `base_menu` 鍜?`output_path` 涓嶅啀鍐欏叆 `config.ini`锛屽彧鏈夌敤鎴锋墜鍔ㄤ慨鏀瑰悗鎵嶄細鎸佷箙鍖栥€?
### 淇

- 褰撳厤鐢佃ˉ涓佸鑷存渶缁堜綋绉秴鍑烘墍閫夊崱瀹归噺鏃讹紝鏋勫缓浼氬湪杈撳嚭鍓嶈鎷︽埅锛屼笉鍐嶇敓鎴愪笉瀹屾暣缁撴灉銆?- 骞跺彂鏋勫缓鍑嗗闃舵鐨勮仛鍚堝紓甯稿凡鍋氳В鍖咃紝鍓嶇鎻愮ず浼氭洿娓呮櫚銆?
### 璇存槑

- 鏂扮殑 `compat-auto-detect` payload 宸茬粡鍦ㄥ綋鍓?`S29GL01GS` 瀹炴満鏍锋湰涓婇獙璇侀€氳繃銆?- 鍏朵粬 NOR 瀹舵棌浠嶆湭瀹屾垚鍏ㄨ鐩栧疄鏈洪獙璇侊紝鐜伴樁娈典笉瀹ｇО鈥滃畬鍏ㄦ櫘閫傗€濄€?
