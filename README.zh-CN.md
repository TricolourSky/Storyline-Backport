# EFT 1.1 Storyline Backport

SPT Forge 上 **EFT 1.1 Storyline Backport (WIP)** 附加包的源码。它把 EFT 1.1 的主线——**塔科夫之旅**和**陨落星辰**——连同 7 位商人的 3D 房间和零售对话带到 SPT 4.1 / EFT 0.16，做成 [VisitAPI](https://github.com/TricolourSky/VisitAPI) 的内容包。

附加包没有 DLL，只有数据（JSON）、图片和 Unity 资源包。这个仓库放全部数据和图片，以及生成发布包里每一个文件所用工具的源码。Unity 资源包本身不放在这里（见[不在仓库里的文件](#不在仓库里的文件)）。

[English](README.md)

## 内容

| 路径 | 是什么 |
|---|---|
| `pack/EFT11/` | 1.0.0 发布包里的内容包，和发布的文件逐字节一致，只少了 3 个物品资源包。安装位置是 `SPT_Runtime/user/mods/VisitAPI-Server/packs/EFT11/`。 |
| `tools/QuestPort/` | 从 EFT 1.1 的数据移植任务、对话、文本和任务物品，并适配到 SPT 4.1 / EFT 0.16。 |
| `tools/ZoneExtract/` | 从 EFT 1.1 的关卡文件里读出任务区域（位置、朝向、碰撞框大小）。 |
| `tools/VendorScene/` | 把 AssetRipper 导出的 1.1 商人房间场景搬进 Unity 工程，并为场景用到的游戏脚本生成占位类。`Unity/VendorSceneBuild.cs` 是打房间资源包的编辑器脚本。 |
| `tools/NativeShaderPatch/` | 房间资源包打好之后的后处理：换回 1.1 原版着色器，还原材质关键字、特殊格式贴图和反射立方图，再校验结果。 |
| `release-1.0.0.sha256` | 1.0.0 发布包里每个文件的 SHA-256，包括没放进仓库的资源包。 |

`pack/EFT11/` 里有：

| 文件夹 | 内容 |
|---|---|
| `quests/` | 38 个任务：塔科夫之旅 21 个，陨落星辰 17 个 |
| `dialogues/` | 94 段零售商人对话（`dialogue.json`）和剧情对话（`tour_1.1.json`、`extra_1.1.json`） |
| `locales/` | 中文和英文文本 |
| `items/`、`loot/` | 6 件任务物品和它们在战局里的刷新点 |
| `zones/` | 11 个任务区域 |
| `variables/` | 42 个变量组 |
| `images/` | EFT 1.1 的章节横幅和图标 |
| `pack.json`、`bundles.json` | 包清单和物品资源包列表 |

## 不在仓库里的文件

发布包里还有用 EFT 1.1 游戏资源打出来的 Unity 资源包，没有放进这里：一个房间包有 0.3–1.1 GB，远超 GitHub 单个文件 100 MB 的上限。要这些文件请从 SPT Forge 页面下载附加包；`release-1.0.0.sha256` 里有它们的哈希。

| 发布包里的文件 | 大小 | 怎么做的 |
|---|---|---|
| `BepInEx/plugins/VisitAPI/rooms/<商人id>_<商人>` ×7 | 每个 0.3–1.1 GB | 商人房间，用下面的房间流程打包 |
| `BepInEx/plugins/VisitAPI/rooms/vendors_scripts` | 1.9 MB | 所有房间共用的 `Vendors_Scripts` 场景（光照、后处理），同一流程，单独成包 |
| `BepInEx/plugins/VisitAPI/rooms/dialogue.json` | 12 MB | 客户端用的副本，和 `pack/EFT11/dialogues/dialogue.json` 完全相同 |
| `…/packs/EFT11/bundles/…/*.bundle` ×3 | 16 MB | 任务物品模型，从 EFT 1.1 客户端原样拷贝 |

## 发布包是怎么做出来的

原料：EFT 1.1 客户端、它和服务器通信的抓包（任务列表、对话、文本），以及 SPT 5.0 的数据库。

- **任务、对话、文本。** `QuestPort port` 导出一个章节和它依赖的任务。`adapt` 让它们能在 SPT 4.1 / EFT 0.16 上加载：删掉 SPT 4.1 没有的奖励类型，把 1.1 独有的条件类型换成同 id 的替身，再加上 VisitAPI 章节系统读取的 `visitapi` 标记。其余命令补日记链接、对话和文本。全部命令列在 `tools/QuestPort/Program.cs` 开头。
- **任务物品。** 物品模板、文本和手册条目取自 SPT 5.0；模型从 1.1 客户端原样拷贝；刷新点是 1.1 的固定刷新点。`QuestPort items` 一步做完这些。这个命令是 1.0.0 之后才加的，1.0.0 的物品是按同样的步骤手工做的。
- **任务区域。** `ZoneExtract <classdata.tpk> <关卡文件> <名字正则> <地图>` 按内容包 `zones` 的格式输出区域框。
- **商人房间。**
  1. 用 [AssetRipper](https://github.com/AssetRipper/AssetRipper) 导出 1.1 客户端。
  2. `VendorScene extract <场景>` 把一个房间场景和它引用的全部资源拷进一个 Unity 2022.3.43 工程（在社区的 EFT 模组 SDK 基础上搭的），并为场景用到的游戏脚本生成占位类。EFT 0.16 和 1.1 用的都是 Unity 2022.3.43。
  3. `Unity/VendorSceneBuild.cs` 打包：
     `Unity.exe -batchmode -nographics -quit -projectPath <工程> -executeMethod VisitAPI.Editor.VendorSceneBuild.BuildFromCli -scene Vendors_Prapor -bundle 54cb50c76803fa8b248b4571_prapor`
  4. `NativeShaderPatch patch` 把 1.1 原版着色器放回包里，并按场景的 1.1 关卡文件还原材质关键字、特殊格式贴图和反射立方图；`unpack` 解开结果，`verify` 和 1.1 原件逐项比对。

## 编译工具

- 需要 .NET 10 SDK：`dotnet build tools/<工具名>`。
- `ZoneExtract` 要用 [UABEA](https://github.com/nesrak1/UABEA) 自带的 AssetsTools.NET、Mono.Cecil 和贴图解码库：`dotnet build tools/ZoneExtract -p:Uabea=<UABEA 文件夹>`。
- `NativeShaderPatch` 要把 `AssetsTools.NET.dll` 放在它的 `.csproj` 旁边。
- 输入输出路径写在各个 `Program.cs` 开头，是作者本机的路径，换成你自己的。代码注释是中文。

## 许可和致谢

内容包自己的文件和工具按 MIT 许可（见 `LICENSE`）。任务、对话、图片和模型数据来自 Battlestate Games 的 Escape from Tarkov 1.1。本项目与 Battlestate Games 和 SPT 团队无关。

框架：[VisitAPI](https://github.com/TricolourSky/VisitAPI) · 编辑器：[VisitAPI Editor](https://github.com/TricolourSky/VisitAPI-Editor)
