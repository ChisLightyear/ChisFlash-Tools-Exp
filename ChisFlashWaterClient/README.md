# ChisFlashWaterClient

**上位机 2.046 r15**，配套合卡器共享源码 **0.92.5**。

支持 GBA 单卡／合卡、GB/GBC 固定槽位／RTC／自由合卡和配套 MIX 工作区。使用步骤与本版截图见[中文教程](../TUTORIAL.zh-CN.md)或[English guide](../TUTORIAL.en.md)。

从仓库根目录构建：

```powershell
dotnet build ChisFlashWaterClient/ChisFlashWaterClient.csproj -c Release
```

编译依赖旁边的 `GbaMultiMenuBuilder/` 共享源码，两个目录须一起保留。菜单、补丁及语言资源随项目提供；游戏、封面、外部字体由用户自备。

Client **2.046 r15**, paired with Builder shared sources **0.92.5**. Keep both project directories together when building. See the linked guides for current single-game, multicart, GB/GBC and MIX workflows.
