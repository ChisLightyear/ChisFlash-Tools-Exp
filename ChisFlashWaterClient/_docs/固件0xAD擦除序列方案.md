# 固件侧 0xAD「单 CS 周期擦除序列」实施方案（待审）

> 目标：消除 USB 直插擦除时「7 条命令 × 各自 USB 往返 + 各自独立 CS 周期」的开销。
> 状态：**方案待审，尚未改任何固件代码。**

---

## 0. 先修正一个我上一轮的错误

我最初把这个新命令定为 **`0xF7`**，这是**错的**。

查 `cdc_msc3/src/main.c:181`：

```c
CMD_RAM_WRITE = 0xF7,
```

`0xF7` 是 **RAM 写**，上位机 `WaterDeviceProtocol.TryWriteRam()`（`WaterDeviceProtocol.cs:285-288`）在用它，
盖中盖的分桶切换 `TrySwitchLegacyBeggarBucketLocal()` 也依赖它（写 RAM 地址 2/3）。

**后果会有多严重**：如果真按 `0xF7` 发擦除 payload，固件会走 `CMD_RAM_WRITE` 分支
（`main.c:1775-1784`）——那是个空壳，只回 ACK、什么都不做。上位机拿到 ACK 以为成功，
**静默跳过逐字下发**，然后卡在等 `0xFFFF` 直到超时。等于把擦除功能弄坏。

已改为 **`0xAD`**（`cmd_code_t` 与上位机协议均未占用；候选里 `0xE9`/`0xAB`/`0xAC`/`0xAE`/`0xAF` 也空）。

---

## 1. 命令定义

### 1.1 协议格式（上位机 → 固件）

```
[uint16 包长][0xAD][uint32 wordBase][(uint16 offset, uint16 value) × N][uint16 CRC]
```

- `wordBase`：目标 NOR 的字地址基址（USB 直插路径 = `targetIndex * 0x4000000`）
- 每个命令对 4 字节：`uint16 offset`（相对 wordBase 的字偏移）+ `uint16 value`
- payload 长度必须是 4 的倍数
- 当前上位机固定发 6 对 = 24 字节：`555/AA` `2AA/55` `555/80` `555/AA` `2AA/55` `555/<10|30>`

### 1.2 新增 enum 项

`src/main.c:166` 附近（`CMD_SPI_FLASH_WRITE = 0xAA` 之后）：

```c
CMD_ROM_ERASE_SEQ = 0xAD,
```

---

## 2. 固件侧改动点（3 处）

### 改动 1 — `cmd_code_t` 加枚举

**位置**：`src/main.c:166` 后
**改动**：加 `CMD_ROM_ERASE_SEQ = 0xAD,`

### 改动 2 — 新增底层原语：单 CS 周期内按 (偏移,数据) 对写多字

**位置**：`src/main.c`，紧接 `cart_rom_write_words_raw()`（575-604 行）之后

**为什么需要新原语**：现有的三个写原语都不满足需求——

| 原语 | 行号 | 不满足的地方 |
|---|---|---|
| `cart_rom_write_words_raw` | 575 | 地址固定，不对 CS 周期内的每个字重新设地址 |
| `cart_rom_write_le_words_raw` | 606 | 同上（且是 LE 字节流，不是分开的 offset/value） |
| `cart_rom_write_le_words` | 670 | 内部分块，每块一次 CS 周期 |

新原语结构（**关键：每个字之前都要重设地址，因为 CPLD 会自动 +1**）：

```c
static void cart_rom_write_erase_seq(uint32_t word_base,
                                     const uint8_t *pairs,
                                     uint16_t pair_count)
{
  volatile uint32_t *const rom_addr  = (volatile uint32_t *)CART_REG_ROM_ADDR;
  volatile uint32_t *const data_reg  = (volatile uint32_t *)CART_REG_DATA;
  volatile uint32_t *const read_reg  = (volatile uint32_t *)CART_REG_READ;
  volatile uint32_t *const write_reg = (volatile uint32_t *)CART_REG_WRITE;
  volatile uint32_t *const cs_reg    = (volatile uint32_t *)CART_REG_CS;
  volatile uint32_t *const active_strobe   = write_reg;
  volatile uint32_t *const inactive_strobe = read_reg;

  *inactive_strobe = 1u;
  *active_strobe   = 1u;
  *cs_reg          = 0;               // 片选拉低 —— 整条序列共用这一个 CS 周期
  cart_bus_wait_cycles(NOR_ADDR_SETUP_CYCLES);

  for (uint16_t i = 0; i < pair_count; i++) {
    const uint16_t word_offset = (uint16_t)(pairs[0] | (pairs[1] << 8));
    const uint16_t value       = (uint16_t)(pairs[2] | (pairs[3] << 8));
    pairs += 4u;

    *rom_addr = word_base + (uint32_t)word_offset;   // ← 逐字重设，见 §2.1
    cart_bus_wait_cycles(NOR_ADDR_SETUP_CYCLES);
    *data_reg = 0xFFFF0000u | value;
    cart_bus_wait_cycles(NOR_ADDR_SETUP_CYCLES);
    *active_strobe = 0u;                             // /WE 低（CPLD 会在此刻 +1）
    cart_bus_wait_cycles(NOR_WRITE_LOW_CYCLES);
    *active_strobe = 1u;                             // /WE 高
    cart_bus_wait_cycles(NOR_RECOVERY_CYCLES);
  }

  cart_bus_wait_cycles(NOR_RECOVERY_CYCLES);
  *active_strobe   = 1u;
  *inactive_strobe = 1u;
  *cs_reg          = 1;               // 片选释放
}
```

### 2.1 ⚠️ CPLD 会自动递增地址 —— 已核实，本原语必须逐字重设

这是本方案最容易踩、且**我已从源码确认**的坑。

`logic/rom_address.v:451-454`：

```verilog
REG_WRITE: begin
    if ((usb_nwr == 1'b0) && ahb_hwdata[0] && (usb_cs1 == 1'b0))
        usb_word_addr <= usb_word_addr + 28'd1;   // ← /WE 翻转即自动 +1
    usb_nwr <= ahb_hwdata[0];
end
```

且 `rom_address.v:500` **把 `usb_word_addr` 直接选到外部总线上**：

```verilog
assign selected_rom_addr = usb_mode ? usb_word_addr : cart_mode_rom_addr;
```

**推论**：写 `REG_ROM_ADDR` 之后，**每一次 `/WE` 拉低→拉高，地址就自己 +1**。

这正好解释了现有 `cart_rom_write_words_raw()` 为什么「地址只设一次」——
它是为**连续地址**写的，靠 CPLD 自增来推进，效率最高。

但擦除序列的偏移是 **`0x555` → `0x2AA` → `0x555`** 这种来回跳的，
所以**必须每个字之前都重设一次 `*rom_addr`**，否则第二个字会落到 `0x556` 而不是 `0x2AA`，
命令序列直接失效（NOR 不会执行擦除，且可能被当成普通写）。

**这也提示了另一个方案（更简单，但更慢）**：既然 CPLD 会自增，
理论上可以在**每个字之后独占一次 CS 周期**（即退化成现在的行为）——那就没有意义了。
所以逐字重设地址是必须的，这也是为什么现有三个原语都不能直接复用。

> **需要你确认的硬件问题**：`usb_word_addr` 是**寄存器**，写 `REG_ROM_ADDR` 后
> 要到**下一个 `ahb_valid_write` 周期**才生效。而 `*rom_addr` 与 `*active_strobe`
> 是两个独立的 MMIO 写，中间隔了一次 AHB 事务。
> **请确认 CPLD 侧 `usb_word_addr` 的更新与 `usb_nwr` 的下降沿之间有足够余量** ——
> 从代码看 `usb_word_addr <=` 是非阻塞赋值，而 `rom_addr` 是组合输出给 NOR，
> 所以地址应在 `/WE` 拉低前就已稳定。但这是本方案唯一的时序不确定点，**建议先在实机上验证**。

### 改动 3 — 命令处理器

**位置**：`src/main.c:1598` 附近（`handle_rom_erase_chip` 旁边）

```c
static void handle_rom_erase_seq(const uint8_t *payload, uint16_t payload_len)
{
  if (payload_len < (USB_CMD_BASE_ADDR_SIZE + 4u) ||
      ((payload_len - USB_CMD_BASE_ADDR_SIZE) % 4u) != 0u) {
    cdc_send_ack();          // 或明确回错误码——见 3.1
    return;
  }

  const uint32_t word_base  = read_u32_le(payload);
  const uint8_t *pairs      = payload + USB_CMD_BASE_ADDR_SIZE;
  const uint16_t pair_count = (uint16_t)((payload_len - USB_CMD_BASE_ADDR_SIZE) / 4u);

  // 与既有擦除路径保持一致：确保 NOR 已探测（F-B4 的那条 nor_probe_ensure）
  nor_probe_ensure();

  cart_rom_write_erase_seq(word_base, pairs, pair_count);

  // 关键：命令序列打完后立即返回，不等擦除完成。
  // DQ7 轮询由上位机负责（WaitForWaterLinkSectorErase），
  // 这样等待期间总线才是静默的 —— 这正是省电的目的。
  cdc_send_ack();
}
```

**分发处**：`main.c:1758` 之后插入

```c
case CMD_ROM_ERASE_SEQ:
  handle_rom_erase_seq(payload, payload_len);
  break;
```

---

## 3. 与现有代码的两个一致性要点

### 3.1 `default` 分支不回 ACK —— 这是好事，别改

`handle_command_packet` 的 `default:`（`main.c:1788`）**什么都不做，不回 ACK**。
正是因为这个行为，旧固件收到 `0xAD` 会静默超时，上位机 `TryReadAck` 返回 `false` → 触发回退。
**如果把 default 改成统一回 ACK，能力探测就失效了，必须同步改上位机判断逻辑。**

### 3.2 不要在固件侧做 DQ7 轮询

固件侧现有 `rom_wait_for_done()`（`main.c:776`）的轮询是**连续无间隔**打 CS 周期，
比上位机的 200ms 轮询凶得多（这正是 `TryEraseWaterLinkChipTarget` 里 `Thread.Sleep(100)`
之后交给上位机轮询的原因）。新命令**故意不含等待**，等待全部交给上位机。

### 3.3 为什么不能复用 `cart_rom_write_words_raw` 直接传地址数组

除了 §2.1 的 CPLD 自增问题，还有个更隐蔽的坑：
`cart_rom_write_words_raw` 的 `addr` 参数**只在进入时设一次**（`main.c:585`），
之后循环里全靠 CPLD 自增。如果拿它来发擦除序列，六个字会落在
`0x555, 0x556, 0x557, ...` —— **完全错误的 NOR 命令**，擦除不会发生，
而且可能被 NOR 当作普通写操作，把 `0x555` 附近的数据写坏。

**这也是为什么现有代码里 `TryEraseWaterLinkChipTarget` 要发 7 个独立的命令** ——
每条都走一次完整的 `handle_rom_write` → 一次 `cart_rom_write_words_raw(word_count=1)`，
靠**每字一个独立 CS 周期**来规避自增问题。

即：**当前上位机的 7 次往返不只是「笨」，而是绕开 CPLD 自增的唯一办法。**
这正是 `0xAD` 存在的真正理由。

---

## 4. 预期收益与验证

| 项 | 改动前 | 改动后 |
|---|---|---|
| 命令下发 | 7 次 USB 往返 + 7 个独立 CS 周期 | **1 次 USB 往返 + 1 个 CS 周期** |
| 等待轮询 | 已优化到 200ms | 不变（上位机侧） |
| 整条链路唤醒次数（命令阶段） | 7 次 | 1 次 |

**验证步骤**：
1. 先跑实验 A/B（上位机侧已加 `SkipPpbUnlockForPowerMeasurement` 开关）摸清当前 0.15A 的构成
2. 固件改完烧录后，擦除电流应再降一档；日志会**不再出现** `erase_seq_fallback` 那条
3. 回归验证：擦除后 `VerifyErasedNorTarget` 必须仍然全 `0xFFFF` 通过

**⚠️ 与实验的顺序**：建议**先做实验再改固件**。如果实验显示 PPB 解锁才是大头（我倾向于这个判断），
那固件这块的收益可能只有零点零几 A，优先级就该往后放。

---

## 5. 本方案未覆盖的（明确不做）

- **不动 `NOR_*_CYCLES = 0u` / `NOR_*_TIMEOUT_MS = 0u`**（`main.c:107-116`）
  —— 这是 F-B1 的死循环变砖隐患，但改它会影响所有 NOR 访问时序，
  应该作为独立改动单独评估，不混在功耗优化里。
- **不动 `0xF1` / `0xF5` / `0xF6`** 等既有命令语义。
- **不动 CPLD 侧 Verilog**（`rom_address.v` 的 `usb_mode = bus3v3` 硬切换、
  USB 空闲停时钟等），那些属于下一阶段。
