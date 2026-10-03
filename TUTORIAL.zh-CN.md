# ChisFlash 工具集 使用教程（简体中文）

仓库里有两个程序，**互相独立，按需选一个用即可**：

## 🔥 上位机 `ChisFlashWaterClient`（推荐）

**直接连卡带，想怎么改就怎么改。** 选好烧录卡类型，剩下全自动：

- **任意增删游戏** —— 卡里已有的游戏，想加就加、想删就删，互不影响
- **自由更新** —— 换游戏、换菜单、改背景，随时改随时写，不用从头重做
- **自由导出存档** —— 单个游戏的存档可单独导出 / 导入
- **自动打补丁** —— **无需人工干预**，按烧录卡类型自动判断并打上 SRAM / 免电补丁

## 📦 合卡器 `GbaMultiMenuBuilder`

**离线把多个游戏合成一个合卡 ROM 文件**，再拿**任意** GBA 烧录器烧进去。
适合传统烧录器，或想先做好一个成品文件留着。

English version: [TUTORIAL.en.md](TUTORIAL.en.md)

---

## 快速上手（只用上位机）

1. [装好 .NET 9 运行时](#1-环境准备)（不想自己编译就跳过[第一部分](#第一部分编译)）
2. USB 插上 Beggar 烧录器
3. 打开上位机 → **选串口** → **选模式（GBA/GB）** → **选烧录卡类型**
4. 加游戏 → **开始烧录**（补丁自动打）

---

## 目录

- 📦 **第一部分：编译**（已折叠，[点此展开](#第一部分编译)）← 只想用、不想自己编译的可以跳过
  - [1. 环境准备](#1-环境准备)
  - [2. 获取源码](#2-获取源码)
  - [3. 编译](#3-编译)
  - [4. 运行](#4-运行)
  - [5. 编译常见问题](#5-编译常见问题)
- 🔧 [硬件准备](#硬件准备)
- 🔥 [上位机使用流程](#上位机使用流程)
  - [1. 上位机界面区域](#1-上位机界面区域)
  - [2. 连接](#2-连接)
  - [3. 加游戏并烧录](#3-加游戏并烧录)
  - [4. 存档](#4-存档)
- 📦 [合卡器使用流程](#合卡器使用流程)
  - [1. 合卡器界面区域](#1-合卡器界面区域)
  - [2. 合卡器需要你自己准备的文件](#2-合卡器需要你自己准备的文件)
  - [3. 操作步骤](#3-操作步骤)
  - [4. 其他操作与超容处理](#4-其他操作与超容处理)
- 🔬 [进阶操作](#进阶操作)
- ⚠️ [使用注意事项](#使用注意事项)
- 📚 [附录：快速参考](#附录快速参考)

---

<details>
<summary><b>📦 第一部分：编译 —— 点此展开 / 收起</b></summary>

<a id="第一部分编译"></a>
<a id="part-1-build"></a>

## 1. 环境准备

### 1. 必需：.NET 9 SDK

两个程序都是 **.NET 9 + WinForms** 项目，只能在 **Windows** 上编译和运行。

下载地址：https://dotnet.microsoft.com/download/dotnet/9.0

选择 **.NET 9.0 SDK**（不是 Runtime，SDK 才含编译器）中的 **Windows x64** 版本。

安装完成后验证：

```bash
dotnet --version
```

**期望输出**：`9.x.xxx`（开头是 9 即可）

如果提示「找不到命令」，说明安装时没勾选加入 PATH，重新运行安装程序勾选即可，或重启终端。

### 2. 可选：Visual Studio

不想用命令行的话，可以装 **Visual Studio 2022 (17.12 或更高)**，安装时勾选：

- 「.NET 桌面开发」工作负载

这样 `dotnet` 命令和图形化编译器一起就有了。

### 3. 可选：Git

用于克隆仓库。下载：https://git-scm.com/download/win

如果不想装 Git，也可以在 GitHub 网页上点 **Code → Download ZIP** 下载压缩包。

---

## 2. 获取源码

### 1. 命令行克隆（推荐）

```bash
git clone https://github.com/moribaka/ChisFlash-Tools-Exp.git
cd ChisFlash-Tools-Exp
```

### 2. 网页下载

在仓库页面点 **Code → Download ZIP**，解压到任意目录。

### 3. ⚠️ 目录结构不能改

解压或克隆后，目录**必须**是这个样子：

```
ChisFlash-Tools-Exp/
├── ChisFlashWaterClient/     ← 上位机
└── GbaMultiMenuBuilder/      ← 合卡器
```

**两个文件夹必须并排放在同一个父目录下，名字不能改。**

**为什么？** 上位机在编译时会去「隔壁」的 `GbaMultiMenuBuilder` 目录读取 **29 个源码文件**（见 `ChisFlashWaterClient.csproj` 里的 `Include="..\GbaMultiMenuBuilder\..."`）。

这个 `..\` 表示「上一级目录」，是**相对路径**。所以：

- ✅ 两个文件夹并排 → 能找到，编译成功
- ❌ 把其中一个单独挪到别处 → 找不到，编译报错「文件不存在」
- ❌ 改掉文件夹名字 → 同样报错

**如果你只想编译其中一个程序**，也必须保留两个文件夹的完整结构，不能只下载一个。

---

## 3. 编译

### 1. 编译两个程序

在 `ChisFlash-Tools-Exp` 目录下打开终端（命令行 / PowerShell），依次执行：

**编译上位机：**

```bash
dotnet build ChisFlashWaterClient/ChisFlashWaterClient.csproj -c Release
```

**编译合卡器：**

```bash
dotnet build GbaMultiMenuBuilder/GbaMultiMenuBuilder.csproj -c Release
```

**首次编译会联网下载 NuGet 依赖包**，需要几分钟。之后编译会快很多。

成功后输出结尾类似：

```
已成功生成。
    0 个警告
    0 个错误
```

### 2. 编译产物位置

| 程序 | 产物路径 |
|---|---|
| 上位机 | `ChisFlashWaterClient/bin/Release/net9.0-windows/ChisFlashWaterClient.exe` |
| 合卡器 | `GbaMultiMenuBuilder/bin/Release/net9.0-windows/Chis合卡制作器.exe` |

### 3. 用 Visual Studio 编译（替代方案）

1. 双击 `ChisFlashWaterClient/ChisFlashWaterClient.sln` 或 `GbaMultiMenuBuilder/GbaMultiMenuBuilder.sln`
2. 顶部工具栏把配置从 `Debug` 切到 `Release`
3. 菜单 **生成 → 生成解决方案**（或按 `Ctrl+Shift+B`）

---

## 4. 运行

### 1. 直接运行编译产物

进入产物目录，双击 `.exe`：

```bash
## 上位机
ChisFlashWaterClient/bin/Release/net9.0-windows/ChisFlashWaterClient.exe

## 合卡器
GbaMultiMenuBuilder/bin/Release/net9.0-windows/Chis合卡制作器.exe
```

### 2. 打包成一个独立文件夹（发布）

如果要把程序拷给别人用，用 `publish` 命令：

```bash
## 上位机（框架依赖，体积小，对方需装 .NET 9 桌面运行时）
dotnet publish ChisFlashWaterClient/ChisFlashWaterClient.csproj -c Release -r win-x64 -o dist/上位机

## 合卡器
dotnet publish GbaMultiMenuBuilder/GbaMultiMenuBuilder.csproj -c Release -r win-x64 -o dist/合卡器
```

输出目录（`dist/上位机`、`dist/合卡器`）里的内容就是完整的可分发程序，整个文件夹拷给对方即可。

**默认是「框架依赖」模式**：体积小（几十 MB），但对方机器需要装 .NET 9 桌面运行时。

如果希望**不依赖运行时、拷过去就能跑**，加 `--self-contained true`：

```bash
dotnet publish ChisFlashWaterClient/ChisFlashWaterClient.csproj -c Release -r win-x64 --self-contained true -o dist/上位机-独立版
```

代价是体积会涨到 100 MB 以上，一般不推荐。

### 3. 程序需要哪些文件

**上位机**运行时需要同目录下有：

- `Languages/` 目录（界面语言文件）
- `Assets/` 目录（菜单 ROM 资源）
- `save_types.ini`（存档类型识别规则）
- `covers/` 目录（**可选**，游戏封面库，缺失只影响封面显示）

**合卡器**运行时需要同目录下有：

- `Languages/` 目录
- `Assets/` 目录（含 `Fonts/` 字库）
- `save_types.ini`、`save_type_overrides.ini`

这些文件在 `dotnet publish` 时会自动一并输出，不用手动拷。

---

## 5. 编译常见问题

### 「未能找到 ../GbaMultiMenuBuilder/...」

**原因**：目录结构不对，两个文件夹没并排。

**解决**：确认 `ChisFlashWaterClient` 和 `GbaMultiMenuBuilder` 在同一父目录下，且名字完全一致（注意大小写）。

### 「找不到 .NET SDK」/「The current .NET SDK does not support targeting .NET 9.0」

**原因**：没装 .NET 9 SDK，或装的是旧版本。

**解决**：运行 `dotnet --list-sdks` 查看已安装版本，确保有 `9.x` 开头的。没有就去官网装。

### 编译成功但程序启动闪退

**原因**：多半是缺少运行所需的文件（如 `Languages/`）。

**解决**：从 `bin/Release/net9.0-windows/` 整个目录运行，不要只把 `.exe` 单独拷出来。

### NuGet 下载超时 / 卡在「正在还原」

**原因**：网络访问 nuget.org 不稳定。

**解决**：配置国内镜像源，或检查代理设置。

### 杀毒软件报毒 / 删除 exe

**原因**：自编译的未签名 exe 常被误报。

**解决**：添加信任目录，或临时关闭实时防护。

</details>

## 硬件准备

### 1. 需要的硬件

| 硬件 | 说明 |
|---|---|
| **烧录器** | ChisFlash 系列烧录器（Beggar），通过 USB 连接电脑 |
| **卡带** | 可烧录的 ChisFlash 卡带，见下表 |
| **USB 数据线** | 连接烧录器和电脑 |

#### 支持的卡带型号与容量

| 卡带 | 支持容量 |
|---|---|
| **ChisFlash 1.0G** | **128 MiB**（01G）/ **256 MiB**（02G） |
| **ChisFlash WaterLink**（水联） | **128 MiB（01G）** / **256 MiB（02G）** |
| **ChisFlash MAX/MBCX**（自由合卡固件） | **32 MiB NOR** + **512 KiB SRAM** |

### 2. 需要自己准备的文件

程序提供工具和菜单内核，不含游戏、封面图库或外部中文字库。请按用途准备：

| 项目 | 是否必需 | 说明 |
|---|---|---|
| **游戏 ROM** | ✅ 必需 | `.gba` 游戏文件（或 `.zip`）。没有替代品，必须自备 |
| **基础菜单** | ⚙️ 可省 | 合卡器 `Assets/` 自带默认菜单，自动选用 |
| **封面 / 背景图** | ⭕ 可选 | 让菜单好看，缺了照样能构建、能烧录 |
| **中文字库** | 中文菜单必需 | 自备 12px 字库；8px 不支持。放入工具的 `Assets/Fonts/`，重新打开工具后选择 |

### 3. 识别设备

用 USB 线把烧录器插到电脑上，然后**打开上位机**：

1. 点 **刷新**（或直接打开软件，会自动扫描）
2. 在 **串口** 下拉框里选带**烧录器名字**的那一项 —— 比如 `ChisFlash Beggar`、`ChisFlash WaterLink`
3. 选完会自动识别

---

## 上位机使用流程

直接连卡带，**不需要先做出合卡文件**。选好烧录卡类型后，补丁、映射、存档槽全自动。

### 1. 上位机界面区域

截图使用假 ROM 和模拟卡带数据，未连接或烧录真实卡带。

![上位机主界面](docs/images/zh-CN/client_01_main.png)

| 区域 | 内容 |
|---|---|
| **顶部工具栏** | 串口选择、刷新、分析卡带、模式（GBA/GB）、各功能菜单 |
| **ROM 工作区** | 待烧录队列 |
| **卡内 ROM 列表** | 卡带里已有的游戏 |
| **日志** | 操作记录与错误信息 |

上位机用的菜单 ROM 由程序自带，放在**程序目录的 `Assets\` 文件夹**（`lk_multimenu.gba`、`wl_magic_menu.gba`、`universal_menu.gba`、`simplified_ns_menu.gba` 等）。发布包已带好，**不需要自己准备**；想换成自制菜单时，见 [进阶操作 · 菜单相关](#1-菜单相关)。

### 2. 连接

1. **串口** → 选写有**烧录器名字**的那一项（如 `ChisFlash Beggar`）；看不到就点 **刷新**
2. **模式** → `GBA` 或 `GB`，按实际卡带选
3. **烧录卡类型** → 选与实物一致的（选错补丁会打错）
4. 点 **分析卡带** → 卡带里的游戏会出现在 **卡内 ROM 列表**

### 3. 加游戏并烧录

- 在 **ROM 工作区**点 **添加 ROM**，选 `.gba` 文件（可多个）
- 点 **开始烧录** —— **过程中不要拔线、不要关窗口**
- 完成后建议再点一次 **分析卡带**，确认游戏已进卡

### 4. 存档

| 功能 | 说明 |
|---|---|
| **导出存档 / 导入存档** | 单个游戏的存档导出成 `.sav`，或写回卡带 |
| **导出芯片存档 / 导入芯片存档** | 直接读写 Flash 芯片里的存档区 |
| **清空存档** | 清除卡带的存档数据 |

---
## 合卡器使用流程

**目标**：把多个 `.gba` 游戏打包成**一个**合卡 ROM 文件（离线做，先出文件）。
合好的文件可以拿**任意** GBA 烧录器烧进卡带 —— 这条路线适合传统烧录器。

### 1. 合卡器界面区域

启动后主要分三块：

![合卡器主界面](docs/images/zh-CN/builder_01_main.png)

| 区域 | 内容 |
|---|---|
| **构建设置** | 基础菜单、输出 ROM、自定义背景、封面目录、中文字库、容量、烧录卡选型、菜单类型 |
| **ROM 列表** | 添加游戏、排序、分配存档槽 |
| **构建日志** | 显示进度与结果 |

### 2. 合卡器需要你自己准备的文件

仓库**只提供程序**，不含游戏、封面、菜单等素材。以下几项要自己放进来：

| 项目 | 是否必需 | 要准备什么 | 放哪 / 怎么选 |
|---|---|---|---|
| **游戏 ROM** | ✅ 必需 | `.gba` 或 `.zip` 游戏文件 | 用「添加 ROM」选，或拖进列表（放哪都行） |
| **基础菜单** | ⚙️ 可省 | 菜单内核 ROM（`.gba`） | 程序**目录下的 `Assets\` 文件夹**，详见下方说明 |
| **封面目录** | ⭕ 可选 | `cover_index.csv` + 封面图片 | 用「封面目录」的「浏览」指定目录 |
| **自定义背景** | ⭕ 可选 | 指定尺寸的背景图 | 用「自定义背景」的「浏览」选图片 |
| **中文字库** | 中文菜单必需 | 自备 12px 中文字库 | 放入 `Assets/Fonts/`，重新打开工具，在中文字库列表中选择 |

**基础菜单的位置**：程序所在目录下的 **`Assets\` 文件夹**，文件名固定，按「菜单类型 + 烧录卡选型」自动选用：

| 菜单类型 | 烧录卡选型 | 文件名 |
|---|---|---|
| 经典菜单 | 普通 / 无电池 | `lk_multimenu.gba` |
| 经典菜单 | 水线卡（虚铁） | `lk_multimenu_wl.gba` |
| NS 菜单 | **任意**（全支持） | `universal_menu.gba` |
| 精简 NS 菜单 | **任意**（全支持） | `simplified_ns_menu.gba` |

发布包（`publish`）里这 4 个文件已经带好，**不用自己准备**。只有想换成自制菜单时，才把文件放进 `Assets\` 并改成上表对应的文件名。

### 3. 操作步骤

**① 设参数**（⬅️ 需与实际卡带匹配）

| 选项 | 说明 |
|---|---|
| **容量** | GBA 侧：**01G（128 MiB）** / **02G（256 MiB）**；GBC 侧：**32 / 128 / 256 MiB**。必须与实际卡带一致 |
| **烧录卡选型** | `GBA \| 01G/02G（有电池/铁电）`、`GBA \| 01G/02G（无电池）`、`GBA \| 水线卡（虚铁）`、`GBC \| MAX/MBCX（512K）` |
| **菜单类型** | `经典菜单` / `NS 菜单` / `精简 NS 菜单` |
| **基础菜单** | **一般不用动**，用程序 `Assets\` 文件夹里的默认菜单，会跟着「菜单类型 / 烧录卡选型」自动换 |
| **输出 ROM** | 选生成文件保存到哪（如 `output/我的合卡.gba`） |

**② 加游戏**（⬅️ 需自己准备 `.gba` 文件）

- 点 **添加 ROM**，或把 `.gba` **拖进**列表；支持多选、整个文件夹、`.zip` 自动解压
- 用 **上移 / 下移** 调菜单显示顺序，**删除所选** 移除，**自动分配存档槽** 自动分 SRAM


**③ 开始构建**

- 点 **开始构建**，看 **构建日志** 到成功，**打开目录** 定位输出文件

![ROM 列表已填充](docs/images/zh-CN/builder_02_romlist.png)

**④（可选）设置封面**（⬅️ 需自己准备封面图和 `cover_index.csv`）

- **封面目录** 指定图片目录 → **自动匹配** 按文件名配对

### 4. 其他操作与超容处理

| 功能 | 说明 |
|---|---|
| **按 32 MiB 分卷输出** | 超大容量卡带切成多个文件 |
| **应用 SRAM 补丁** | 修改游戏以适配 SRAM 存档 |
| **预览菜单** | 构建前预览菜单显示效果 |
| **查看配置** | 查看容量占用与配置详情 |
| **导出字库** | 导出中文点阵字库 |

构建日志出现 **「已超容」** 时：减少游戏数量，或换更大容量的卡带。
日志里 **「补丁前/后/容量」** 三个数字可对比找出超标原因（部分游戏打完补丁体积会变）。

---

## 进阶操作

以下功能在 **卡带(&C) / ROM(&R) / 存档(&S)** 菜单里，**操作有风险，请先备份存档**。

### 1. 菜单相关

| 功能 | 说明 |
|---|---|
| **更新菜单** | 把卡带菜单更新为当前版本的菜单 ROM |
| **重建菜单** | 强制重建菜单（适用于菜单显示异常） |
| **使用指定菜单文件重建** | 弹框选一个 `.gba` 菜单 ROM（放哪都行），用它替换卡带上的菜单 |
| **修复菜单** | 修复菜单目录 / 元数据损坏 |
| **修复魔术管理** | 修复 WL（WaterLink）映射 |
| **更新至魔术管理** | 把旧菜单升级为魔术管理模式 |
| **更新背景图** | 更换菜单背景图片（需 240×160 PNG，≤256 色） |
| **擦除配置** | 清除菜单配置区（保留游戏） |

### 2. 芯片操作

| 功能 | 说明 |
|---|---|
| **检测魔术管理** | 检测卡带是否为魔术管理格式 |
| **解锁 PPB** | 解除 Flash 的 PPB 写保护（某些被锁芯片需要） |
| **全片擦除** | ⚠️ **抹掉整片 NOR，所有数据（含游戏）全没了** |
| **碎片测试** | 测试碎片管理功能 |
| **时序设置** | 查看当前 NOR 读写时序参数 |

---

## 使用注意事项

### 1. 数据安全

- **烧录写卡过程中断电 / 拔线，可能导致卡带半写损坏**，需要全片擦除后重来
- **烧录过程中不要关闭上位机窗口**
- 操作重要的卡带前，先用 **导出存档** 备份
- 全片擦除前，先 **导出 ROM** 备份游戏

### 2. 参数匹配

- **合卡器的容量、烧录卡选型** 必须与实物卡带一致
- **上位机的模式（GBA / GB）** 必须与卡带类型一致
- 芯片型号不同，时序参数不同，遇到读写不稳定可试 **时序设置**

### 3. 游戏兼容性

- 不是所有游戏都能正常合卡，部分游戏有特殊 mapper 或存档机制
- 免电（batteryless）补丁并非对所有 Flash 型号都通用
- 合卡成功不代表所有游戏都能正常通关，建议逐个测试

### 4. 版本配套

**合卡器和上位机的版本需要配套使用**，因为两者共享源码。

- 两端版本号可以在界面标题栏或「关于」里看到
- 版本差距过大时，生成的合卡可能无法被上位机正确处理

---

## 附录：快速参考

### 自备文件速查

| 文件 | 必需？ | 没它行不行 |
|---|---|---|
| 游戏 ROM（`.gba` / `.zip`） | ✅ 必需 | ❌ 不行，没游戏就没得合 |
| 基础菜单 ROM | ⚙️ 可省 | ✅ 行，用 `Assets/` 自带的 |
| 封面图 + `cover_index.csv` | ⭕ 可选 | ✅ 行，只是菜单没封面 |
| 自定义背景图 | ⭕ 可选 | ✅ 行，用默认背景 |
| GBC 中文字库 | 中文菜单必需 | GBA 和 GBC 英文菜单无需外部字库；中文必须有 12px 字库 |

### 两条路线速查

**A. 上位机路线（推荐，边合边改）**

```
1. 编译（或用现成的 exe）
   dotnet build ChisFlashWaterClient/ChisFlashWaterClient.csproj -c Release

2. 插上烧录器 → 启动上位机 → 选串口 → 选模式 → 选烧录卡类型 → 分析卡带

3. 添加 ROM → 开始烧录（补丁自动打）

4. 想改就改：卡内列表里删游戏、再添新游戏，随时重烧，不用重做整个合卡
```

**B. 合卡器路线（离线出文件，给传统烧录器）**

```
1. 编译
   dotnet build GbaMultiMenuBuilder/GbaMultiMenuBuilder.csproj -c Release

2. 启动合卡器 → 选基础菜单 + 卡带参数 → 添加游戏 → 开始构建
   → 得到 output/我的合卡.gba

3. 拿任意 GBA 烧录器把这个 .gba 烧进卡带
```

### 目录速查

```
ChisFlash-Tools-Exp/
├── ChisFlashWaterClient/         上位机
│   ├── ChisFlashWaterClient.sln
│   ├── Languages/                界面语言
│   ├── Assets/                   ★ 上位机的菜单 ROM（自带）
│   │   ├── lk_multimenu.gba        经典菜单
│   │   ├── wl_magic_menu.gba       经典菜单（水线卡）
│   │   ├── universal_menu.gba      NS 菜单
│   │   ├── simplified_ns_menu.gba  精简 NS 菜单
│   │   └── menu_zh.gb / menu_en.gb GBC 菜单
│   └── covers/                   封面库（未随仓库提供）
└── GbaMultiMenuBuilder/          合卡器
    ├── GbaMultiMenuBuilder.sln
    ├── Fc/                        NES 卡带支持
    ├── Md/                        MD 卡带支持
    ├── Languages/                界面语言
    └── Assets/                   ★ 合卡器的菜单 ROM（自带）
        ├── lk_multimenu.gba        经典菜单
        ├── lk_multimenu_wl.gba     经典菜单（水线卡）
        ├── universal_menu.gba      NS 菜单
        ├── simplified_ns_menu.gba  精简 NS 菜单
        ├── menu_zh.gb / menu_en.gb GBC 菜单
        └── Fonts/                  字库
```

两个程序的菜单文件**各自独立**，都放在自己的 `Assets\` 里，发布包已自带。

### 第三方资源

- `GbaMultiMenuBuilder/Fc/MesenNesDB.csv` 来自 [Mesen2](https://github.com/SourMesen/Mesen2)，许可原文见 `Fc/Mesen-LICENSE`（GPL v3）
- `GbaMultiMenuBuilder/Assets/Fonts/` 字体版权归各自作者所有
