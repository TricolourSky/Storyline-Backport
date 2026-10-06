# EFT 1.1 Storyline Backport

SPT Forge 上 **EFT 1.1 Storyline Backport (WIP)** 附加包的源码。它把 EFT 1.1 的主线——**塔科夫之旅**、**陨落星辰**、**探秘“迷宫”**、**神秘蓝焰**和**意外证人**——连同 7 位商人的 3D 房间和零售对话带到 SPT 4.1 / EFT 0.16，做成 [VisitAPI](https://github.com/TricolourSky/VisitAPI) 的内容包（1.2.0 需要 VisitAPI 1.3.6）。

附加包没有 DLL，只有数据（JSON）、图片、语音和 Unity 资源包。这个仓库放全部数据和图片，以及生成发布包里每一个文件所用工具的源码。Unity 资源包和语音本身不放在这里（见[不在仓库里的文件](#不在仓库里的文件)）。

[English](README.md)

## 内容

| 路径 | 是什么 |
|---|---|
| `pack/EFT11/` | 1.2.0 发布包里的内容包，和发布的文件逐字节一致，只少了 34 个物品资源包和 15 条语音。安装位置是 `SPT_Runtime/user/mods/VisitAPI-Server/packs/EFT11/`。 |
| `tools/QuestPort/` | 从 EFT 1.1 的数据移植任务、对话、文本和任务物品，并适配到 SPT 4.1 / EFT 0.16。 |
| `tools/ZoneExtract/` | 从 EFT 1.1 的关卡文件里读出任务区域（位置、朝向、碰撞框大小）。 |
| `tools/VoiceRip/` | 从 EFT 1.1 的口型资源包里取出某个商人的语音，转成 MP3。 |
| `tools/LabyrinthPort/`、`tools/LabyrinthNotes/` | 探秘“迷宫”专用步骤的小工具（见下）。 |
| `tools/VendorScene/` | 把 AssetRipper 导出的 1.1 商人房间场景搬进 Unity 工程，并为场景用到的游戏脚本生成占位类。`Unity/VendorSceneBuild.cs` 是打房间资源包的编辑器脚本。 |
| `tools/NativeShaderPatch/` | 房间资源包打好之后的后处理：换回 1.1 原版着色器，还原材质关键字、特殊格式贴图和反射立方图，再校验结果。 |
| `release-1.2.0.sha256`、`release-1.1.0.sha256`、`release-1.0.0.sha256` | 每个版本发布包里每个文件的 SHA-256，包括没放进仓库的文件。 |

`pack/EFT11/` 里有：

| 文件夹 | 内容 |
|---|---|
| `quests/` | 72 个任务：塔科夫之旅 21 个，陨落星辰 17 个，探秘“迷宫” 12 个，神秘蓝焰 9 个，意外证人 13 个 |
| `dialogues/` | 94 段零售商人对话（`dialogue.json`）和剧情对话（`tour_1.1.json`、`extra_1.1.json`、`labyrinth_1.1.json`、`bluefire_1.1.json`、`witness_1.1.json`） |
| `locales/` | 中文、英文和俄文文本 |
| `items/`、`loot/` | 38 件任务物品（笔记、录音带、钥匙等）和它们在战局里的刷新点，另有剧情用到的 52 件 EFT 1.1 物品（`Shared_1.1.json`） |
| `botloot/` | 给 AI 身上加的物品：Reshala 可能带着他储藏处的钥匙（意外证人），概率和 SPT 5.0 一样 |
| `zones/` | 28 个任务区域 |
| `variables/` | 42 个变量组 |
| `traders/` | 剧情里出现的 5 位 EFT 1.1 商人（基础文件和头像） |
| `images/` | EFT 1.1 的章节横幅和图标 |
| `pack.json`、`bundles.json` | 包清单和物品资源包列表 |

## 不在仓库里的文件

发布包里还有用 EFT 1.1 游戏资源打出来的 Unity 资源包，以及从 1.1 客户端取出的语音，没有放进这里：一个房间包有 0.3–1.1 GB，远超 GitHub 单个文件 100 MB 的上限；物品模型和语音是游戏自己的文件。要这些文件请从 SPT Forge 页面下载附加包；`release-1.2.0.sha256` 里有它们的哈希。

| 发布包里的文件 | 大小 | 怎么做的 |
|---|---|---|
| `BepInEx/plugins/VisitAPI/rooms/<商人id>_<商人>` ×7 | 每个 0.3–1.1 GB | 商人房间，用下面的房间流程打包 |
| `BepInEx/plugins/VisitAPI/rooms/vendors_scripts` | 1.9 MB | 所有房间共用的 `Vendors_Scripts` 场景（光照、后处理），同一流程，单独成包 |
| `BepInEx/plugins/VisitAPI/rooms/dialogue.json` | 12 MB | 客户端用的副本，和 `pack/EFT11/dialogues/dialogue.json` 完全相同 |
| `…/packs/EFT11/bundles/…/*.bundle` ×34 | 103 MB | 任务物品模型，从 EFT 1.1 客户端原样拷贝 |
| `…/packs/EFT11/voice/688246518448b05efd61d461/*.mp3` ×15 | 0.6 MB | Mr. Kerman 的语音，用 `VoiceRip` 取出 |

## 发布包是怎么做出来的

原料：EFT 1.1 客户端、它和服务器通信的抓包（任务列表、对话、文本），以及 SPT 5.0 的数据库。

- **任务、对话、文本。** `QuestPort port` 导出一个章节和它依赖的任务。`adapt` 让它们能在 SPT 4.1 / EFT 0.16 上加载：删掉 SPT 4.1 没有的奖励类型，把 1.1 独有的条件类型换成同 id 的替身，再加上 VisitAPI 章节系统读取的 `visitapi` 标记。其余命令补日记链接、对话和文本。全部命令列在 `tools/QuestPort/Program.cs` 开头。
- **任务物品。** 物品模板、文本和手册条目取自 SPT 5.0；模型从 1.1 客户端原样拷贝；刷新点是 1.1 的固定刷新点。`QuestPort items` 一步做完这些。这个命令是 1.0.0 之后才加的，1.0.0 的物品是按同样的步骤手工做的。
- **任务区域。** `ZoneExtract <classdata.tpk> <关卡文件> <名字正则> <地图>` 按内容包 `zones` 的格式输出区域框。
- **探秘“迷宫”。** 和其他章节一样用 `QuestPort` 移植。`LabyrinthPort` 把章节的隐藏开章任务并进章节文件，并设好迷宫专用的 `visitapi` 标记。`LabyrinthNotes` 加入研究助理的笔记（它们的隐藏任务、1.1 的变量奖励改写成 `visitapi.setVariables`、笔记物品），把笔记和观察室钥匙的刷新点从 1.1 原样抄过来，并给 1.1 里没有名字的子任务起上所在章节的名字。两个工具都只往包文件里追加。
- **神秘蓝焰、意外证人。** 和其他章节一样用 `QuestPort` 移植。1.1 里「读到某件物品就开始」的任务，`adapt` 把这件物品写进 `visitapi.startOnItems`，玩家拿到它时任务开始。意外证人有两处地点在 EFT 0.16 的地图上不存在（街区阿纳斯塔西娅公寓的入口、海关 Reshala 的储藏处），这两处的任务区域和笔记手工挪到了旁边进得去的地方。`botloot/` 让 Reshala 按 SPT 5.0 的概率带上储藏处的钥匙。
- **商人。** `traders/<id>/` 是 SPT 5.0 数据库里这位商人的 `base.json` 和头像。SPT 5.0 里存成字符串的两个字段（`discount`、`repair.quality`）改写成数字，这是 SPT 4.1 要的格式。章节任务大多挂在 Player Trader 名下，所以它默认解锁（SPT 5.0 里是锁着的）。
- **语音。** `VoiceRip ids` 收集某个商人的对话用到的口型 id，`ripso` 从 1.1 客户端的口型资源包里取出这些语音，`mp3` 转换格式。
- **商人房间。**
  1. 用 [AssetRipper](https://github.com/AssetRipper/AssetRipper) 导出 1.1 客户端。
  2. `VendorScene extract <场景>` 把一个房间场景和它引用的全部资源拷进一个 Unity 2022.3.43 工程（在社区的 EFT 模组 SDK 基础上搭的），并为场景用到的游戏脚本生成占位类。EFT 0.16 和 1.1 用的都是 Unity 2022.3.43。
  3. `Unity/VendorSceneBuild.cs` 打包：
     `Unity.exe -batchmode -nographics -quit -projectPath <工程> -executeMethod VisitAPI.Editor.VendorSceneBuild.BuildFromCli -scene Vendors_Prapor -bundle 54cb50c76803fa8b248b4571_prapor`
  4. `NativeShaderPatch patch` 把 1.1 原版着色器放回包里，并按场景的 1.1 关卡文件还原材质关键字、特殊格式贴图和反射立方图；`unpack` 解开结果，`verify` 和 1.1 原件逐项比对。

## 编译工具

- 需要 .NET 10 SDK：`dotnet build tools/<工具名>`。
- `ZoneExtract` 要用 [UABEA](https://github.com/nesrak1/UABEA) 自带的 AssetsTools.NET、Mono.Cecil 和贴图解码库：`dotnet build tools/ZoneExtract -p:Uabea=<UABEA 文件夹>`。`VoiceRip` 用同一处的 AssetsTools.NET：`dotnet build tools/VoiceRip -p:Uabea=<UABEA 文件夹>`。
- `NativeShaderPatch` 要把 `AssetsTools.NET.dll` 放在它的 `.csproj` 旁边。
- 输入输出路径写在各个 `Program.cs` 开头，是作者本机的路径，换成你自己的。代码注释是中文。

## 许可和致谢

内容包自己的文件和工具按 MIT 许可（见 `LICENSE`）。任务、对话、图片和模型数据来自 Battlestate Games 的 Escape from Tarkov 1.1。本项目与 Battlestate Games 和 SPT 团队无关。

框架：[VisitAPI](https://github.com/TricolourSky/VisitAPI) · 编辑器：[VisitAPI Editor](https://github.com/TricolourSky/VisitAPI-Editor)
