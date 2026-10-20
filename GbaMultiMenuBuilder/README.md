# GbaMultiMenuBuilder

**合卡器 0.92.5**，配套上位机 **2.046 r15**。

离线生成 GBA、GB/GBC 及配套 MIX（NES / MD / SNES）完整镜像，交给支持相应卡带的烧录器写入。合卡器本身不烧录实卡。

当前使用步骤与截图见[中文教程](../TUTORIAL.zh-CN.md)或[English guide](../TUTORIAL.en.md)。从仓库根目录构建：

```powershell
dotnet build GbaMultiMenuBuilder/GbaMultiMenuBuilder.csproj -c Release
```

默认菜单位于 `Assets/`；外部中文字库和封面不随包分发，详见[字库说明](Assets/Fonts/README.md)。构建不访问其他菜单源码工程或开发机目录。

Builder **0.92.5**, paired with Client **2.046 r15**. Generates complete offline images for compatible programmers. GBA, GB/GBC and MIX layouts are separate. Menu binaries are bundled in `Assets/`; supply games, covers and external fonts yourself.
