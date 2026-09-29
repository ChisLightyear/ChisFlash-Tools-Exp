# Batteryless Payload Reverse Notes

## Scope

This note summarizes the current reverse-engineering results for the two bundled batteryless payloads:

- `Assets/batteryless_payload.bin`
- `Assets/batteryless_payload_flash4_compat.bin`

It focuses on:

- payload header layout
- internal save-hook entrypoints
- per-family NOR command sequences in 16-bit mode
- why `compat-auto-detect` works on `S29GL01GS` while the old `extended` family failed
- whether the new payload can likely work on other NOR devices

This is a command-level comparison document, not a full opcode-by-opcode disassembly listing.

## Current Builder Behavior

The builder currently selects `compat-auto-detect` by default in `Services/BatterylessPatchService.cs`.

The cartridge-to-profile mapping in code is still:

- `Flash1`
  - `6600M0U0BE`
  - `F0095H0`
- `Flash2`
  - `MSP55LV100S`
  - `S29GL512`
- `Flash3`
  - `MSP54LV100`
- `Flash4`
  - `S29GL01GS`
  - `S70GL02G`

Important clarification:

- the old `extended` payload consumes the external `flash_profile` field and then jumps into a fixed profile
- the new `compat-auto-detect` payload does not consume an external `flash_profile` selector
- the new file name contains `flash4_compat` because it was introduced to replace the failing old `Flash4` route, but the payload itself contains multiple internal detect/write branches
- the builder currently applies this new payload more broadly than the set of NOR families that have already been proven by real hardware tests

So the new payload is not "Flash4 only" in the strict binary-structure sense.

## Payload Header Layout

### Old `batteryless_payload.bin`

File size: `2812` bytes

Header word count: `13`

| Index | Value | Meaning |
| --- | --- | --- |
| 00 | `0x080000C0` | original entry pointer placeholder |
| 01 | `0x00000000` | reserved |
| 02 | `0x00020000` | save size default |
| 03 | `0x00000000` | flash profile selector |
| 04 | `0x00000068` | patched entry ARM offset |
| 05 | `0x000000E1` | write SRAM THUMB hook offset |
| 06 | `0x00000131` | write EEPROM THUMB hook offset |
| 07 | `0x000000CD` | write FLASH THUMB hook offset |
| 08 | `0x0000015B` | EEPROM v111 posthook THUMB offset |
| 09 | `0x000003E1` | Flash1M chip erase THUMB hook offset |
| 10 | `0x000003F5` | Flash1M sector erase THUMB hook offset |
| 11 | `0x00000415` | Flash1M program byte THUMB hook offset |
| 12 | `0x00000455` | Flash1M copy byte THUMB hook offset |

What this implies:

- this payload is multi-profile
- it expects the builder to write a `flash_profile` value into header word `[03]`
- it contains extra `Flash1M`-specific hook entrypoints

### New `batteryless_payload_flash4_compat.bin`

File size: `2232` bytes

Header word count: `8`

| Index | Value | Meaning |
| --- | --- | --- |
| 00 | `0x080000C0` | original entry pointer placeholder |
| 01 | `0x00000000` | reserved |
| 02 | `0x00002000` | save size default |
| 03 | `0x00000054` | patched entry ARM offset |
| 04 | `0x000000CD` | write SRAM THUMB hook offset |
| 05 | `0x0000011D` | write EEPROM THUMB hook offset |
| 06 | `0x000000B9` | write FLASH THUMB hook offset |
| 07 | `0x00000147` | EEPROM v111 posthook THUMB offset |

What this implies:

- there is no external `flash_profile` selector field
- there are no dedicated `Flash1M` hook entrypoints in the header
- the payload chooses its internal write path by self-detect logic

## Old Payload Internal Routes

The old `extended` payload has five effective routes:

1. `Profile1`
2. `Profile2`
3. `Profile3`
4. `Profile4`
5. `Profile5`

The builder comments map them conceptually to these cartridge families:

- `Profile1`: `6600M0U0BE`-like
- `Profile2`: `MSP55LV100S` / `S29GL512`-like
- `Profile3`: `MSP54LV100`-like
- `Profile4`: `S29GL01G` / `S29GL01GS` / `S29GL256S` / `MT28EW01G` / `S70GL02G`-like
- `Profile5`: old legacy route

### Old Profile1

Erase preparation sequence:

```text
FF -> 60 -> D0 -> 20 -> D0 -> poll -> FF
```

Program sequence:

```text
EA -> 01FF -> load data -> D0 -> poll -> FF
```

Observed chunk step:

```text
0x400 bytes
```

Interpretation:

- old Intel-like or compatible buffer-write family
- large fixed chunking

### Old Profile2

Unlock addresses:

```text
0x08000AAA
0x08000554
```

Unlock values:

```text
AAA9
5556
```

Erase preparation sequence:

```text
AAA9 -> 5556 -> 8080 -> AAA9 -> 5556 -> 3030 -> poll FFFF -> F0F0
```

Program sequence:

```text
AAA9 -> 5556 -> 2526 -> 0F0F -> data -> 292A -> poll
```

Observed chunk step:

```text
~0x20 bytes
```

Interpretation:

- this is the special `AAA9/5556` family
- this route is present in the old payload
- this route has not yet been found as an explicit branch in the new compat payload

### Old Profile3

Unlock addresses:

```text
0x08000AAA
0x08000554
```

Unlock values:

```text
A9
56
```

Erase preparation sequence:

```text
A9 -> 56 -> 80 -> A9 -> 56 -> 30 -> poll FFFF -> F0
```

Program sequence:

```text
A9 -> 56 -> 26 -> 1F -> data -> 2A -> poll
```

Observed chunk step:

```text
0x40 bytes
```

Interpretation:

- `A9/56` command family
- old route uses a buffer-write style `26/2A`

### Old Profile4

Unlock addresses:

```text
0x08000AAA
0x08000554
```

Unlock values:

```text
AA
55
```

Erase preparation sequence:

```text
AA -> 55 -> 80 -> AA -> 55 -> 30 -> poll FFFF -> F0
```

Program sequence:

```text
AA -> 55 -> 25 -> 1F -> data -> 29 -> poll
```

Observed chunk step:

```text
0x40 bytes
```

Interpretation:

- `AA/55` command family
- this is the old route that should have been correct for `S29GL01GS` by family name
- real hardware testing showed that this route is still unreliable on `S29GL01GS`

### Old Profile5

Behavior:

- phase 1 reuses old `Profile1`
- phase 2 switches to single-word programming

Program sequence in phase 2:

```text
40 -> write word -> poll
```

Interpretation:

- legacy fallback route
- not the correct explanation for the `S29GL01GS` success case

## New Compat Payload Internal Records

The new `compat-auto-detect` payload contains four internal detect records:

1. `Record0`
2. `Record1`
3. `Record2`
4. `Record3`

The payload does its own detection and then enters one of these internal routes.

### Compat Record0

Detect sequence:

```text
[0x08000000] FF
[0x08000000] 90
[0x08000000] FF
[0x08000058] 42
read [0x080000B2]
[0x08000058] 96
[0x08000000] FF
```

Erase/program behavior:

- phase 1 is close to old `Profile1`
- phase 2 is close to old `Profile5`

Phase 2 programming style:

```text
40 -> single-word program -> poll
```

Interpretation:

- old-family detect using `42/96`
- single-word fallback route

### Compat Record1

Detect sequence:

```text
[0x08000000] FF
[0x08000000] 90
[0x08000000] FF
[0x08000058] 42
read [0x080000B2]
[0x08000058] 96
[0x08000000] FF
```

Erase/program behavior:

- phase 1 is again close to old `Profile1`
- phase 2 stays with old `Profile1`-like buffered route

Phase 2 programming style:

```text
EA -> 01FF -> data -> D0 -> poll -> FF
```

Interpretation:

- same `42/96` detect family as `Record0`
- this time it keeps the old buffered program style instead of downgrading to single-word programming

### Compat Record2

Detect sequence:

```text
F0 -> A9 -> 56 -> 90 -> read -> F0
```

Erase preparation sequence:

```text
A9 -> 56 -> 80 -> A9 -> 56 -> 30 -> poll -> F0
```

Program sequence:

```text
A9 -> 56 -> A0 -> write word -> poll
```

Interpretation:

- explicit `A9/56` family support exists in the new payload
- unlike old `Profile3`, the new payload does not use the old `26/2A` buffered path here
- it uses single-word program `A0`

### Compat Record3

Detect sequence:

```text
F0 -> AA -> 55 -> 90 -> read -> F0
```

Erase preparation sequence:

```text
AA -> 55 -> 80 -> AA -> 55 -> 30 -> poll -> F0
```

Program sequence:

```text
AA -> 55 -> 25 -> FF -> data -> 29 -> extra wait/loop -> 0F
```

Observed chunk step:

```text
0x200 bytes
```

Interpretation:

- explicit `AA/55` family support exists in the new payload
- this is the route that matched the successful `S29GL01GS` real hardware result
- this route is materially different from old `Profile4`, even though both are superficially "AA/55 + 25/29"

## Old Profile4 vs New Record3

This is the most important comparison for `S29GL01GS`.

### Common points

Both routes clearly belong to the same broad command family:

```text
AA -> 55 -> 80 -> AA -> 55 -> 30
AA -> 55 -> 25 -> ... -> 29
```

So the issue is not that the builder picked a completely unrelated NOR family.

### Different points

Old `Profile4`:

```text
AA -> 55 -> 25 -> 1F -> data -> 29 -> poll
chunk step = 0x40 bytes
```

New `Record3`:

```text
AA -> 55 -> 25 -> FF -> data -> 29 -> extra wait/loop -> 0F
chunk step = 0x200 bytes
```

The practical differences are:

- different word-count value
  - old: `1F`
  - new: `FF`
- different per-iteration transfer size
  - old: `0x40` bytes
  - new: `0x200` bytes
- different completion handling
  - old: direct poll
  - new: extra wait/loop and end command behavior
- different overall write-buffer model
  - old: short fixed fragments
  - new: full 256-word line style for GL-S-like devices

## Why New Record3 Matches GL-S Better

The local datasheet extract is:

- `obj/s29gl256s_datasheet_extract.txt`

Relevant lines:

- line `11`
  - maximum write buffer is `256 words/512 bytes`
- line `239`
  - programming is done via a `512-byte write buffer`
- line `259`
  - write-buffer-line uses `A15-A8`
- line `1161`
  - a word count greater than buffer size `255` is invalid
- line `1294`
  - write-buffer location must stay inside the selected write-buffer-line
- line `1322`
  - confirm command is `SA 0029h`
- line `1624`
  - the device uses explicit embedded-algorithm status monitoring methods
- line `3006`
  - write-buffer sequence is `555 AA / 2AA 55 / SA 25 / SA WC / ...`
- line `3007`
  - buffer confirm is `SA 29`

What matters here:

- `Record3` uses `AA/55 -> 25 -> FF -> ... -> 29`
- `FF` is consistent with the maximum `256 words` model when the count is encoded as `N - 1`
- `0x200 bytes` is exactly `512 bytes`

That is why `Record3` is much closer to the GL-S write-buffer programming model than old `Profile4`.

## Why `S29GL01GS` Worked Only With `compat-auto-detect`

Current real-hardware outcome in this repo:

- working
  - `__compat-auto-detect.gba`
- failed
  - `__extended-cart-profile4.gba`
  - `__extended-profile1.gba`
  - `__extended-legacy.gba`

Conclusion:

- `S29GL01GS -> Flash4` mapping is not the root cause
- the old `extended` payload's internal `Flash4` microcode is the unreliable part
- the new payload succeeds because its `AA/55` route is not the same as old `Profile4`

## Can The New Payload Work With Other NOR Devices

Short answer:

- yes, probably with some other NOR families
- no, it should not be described as universal support for all NOR chips

### Likely compatible families

#### 1. GL-S-like `AA/55` family

High confidence in principle:

- `S29GL01GS`
- `S29GL01G`
- `S29GL256S`
- `S29GL128S`
- `S29GL512S`
- `MT28EW01G`
- `S70GL02G`

Why:

- the builder comments already group these into the same `AA/55` family
- the new payload contains an explicit `AA/55` detect record
- the successful route uses a `25 / FF / 29` full write-buffer pattern that matches the GL-S family datasheet model in 16-bit mode

Important nuance:

- `S29GL512S` is the GL-S family part with trailing `S`
- the builder dropdown currently contains `S29GL512` without trailing `S`
- those should not be automatically treated as the same chip family without verification

#### 2. `A9/56` family

Plausibly compatible:

- `MSP54LV100`-like devices

Why:

- the new payload contains an explicit `A9/56` detect record
- it performs its own detect and write flow for that family

Current limitation:

- there is no real hardware success report for this family in the current repo yet

#### 3. Old `42/96` detect family

Plausibly compatible:

- `6600M0U0BE`-like devices
- related old-family parts compatible with the `FF/90/42/96` detect path

Why:

- the new payload contains two explicit records for that detect family

Current limitation:

- no current real hardware validation is recorded in this repo

### Family that is not yet proven in the new payload

#### `AAA9/5556` family

Currently not proven:

- `MSP55LV100S`-like
- builder item `S29GL512`-like

Why this is important:

- the old payload had a dedicated `Profile2` for this exact family
- in the new compat payload, no equally clear `AAA9/5556` route has been identified so far

Practical conclusion:

- at the moment, we should not claim that `compat-auto-detect` definitely supports the `AAA9/5556` family
- if we want to support that family with confidence, we need either:
  - real hardware validation
  - or a deeper reverse result that proves an `AAA9/5556` branch exists
  - or a separate known-good payload for that family

## Compatibility Summary

| Family | Old extended route | New compat route | Confidence |
| --- | --- | --- | --- |
| `FF/90/42/96` old family | `Profile1` / `Profile5` | `Record0` / `Record1` | medium |
| `AAA9/5556` | `Profile2` | not clearly identified yet | low |
| `A9/56` | `Profile3` | `Record2` | medium |
| `AA/55 + 25/29` GL-S-like | `Profile4` | `Record3` | high on family match, high on `S29GL01GS`, medium on untested chips |

## Bottom Line

The new payload is not just "the old payload but renamed".

It is a different binary with:

- different header structure
- different internal detection model
- different write-path selection logic
- materially different `AA/55` write-buffer behavior

For `S29GL01GS`, the new payload is the correct choice based on both:

- real hardware result
- and command-level similarity to the GL-S datasheet write-buffer procedure

For other NOR chips:

- `AA/55` GL-S-like parts are the strongest candidates for compatibility
- `A9/56` and the old `42/96` family are plausible but still need more hardware validation
- `AAA9/5556` support should currently be considered unproven

## Reference Files

- `Assets/batteryless_payload.bin`
- `Assets/batteryless_payload_flash4_compat.bin`
- `Services/BatterylessPatchService.cs`
- `Models/CartridgeDefinition.cs`
- `obj/s29gl256s_datasheet_extract.txt`
