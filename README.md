# EFT 1.1 Storyline Backport

Source for the **EFT 1.1 Storyline Backport (WIP)** add-on on SPT Forge. It brings the EFT 1.1 main story — **Tour of Tarkov** and **Falling Skies** — with the seven 3D trader rooms and the retail trader dialogues to SPT 4.1 / EFT 0.16, as a content pack for [VisitAPI](https://github.com/TricolourSky/VisitAPI).

The add-on has no DLL. It is data (JSON), images and Unity asset bundles. This repository holds all of the data and images, plus the source of the tools that produce every file in the release. The Unity bundles themselves are not stored here (see [Not in this repository](#not-in-this-repository)).

[中文说明](README.zh-CN.md)

## Contents

| Path | What it is |
|---|---|
| `pack/EFT11/` | The content pack from release 1.0.0, byte for byte as shipped, without its three item bundles. It installs to `SPT_Runtime/user/mods/VisitAPI-Server/packs/EFT11/`. |
| `tools/QuestPort/` | Ports quests, dialogues, texts and quest items from EFT 1.1 data and adapts them to SPT 4.1 / EFT 0.16. |
| `tools/ZoneExtract/` | Reads quest zones (position, rotation, box size) from EFT 1.1 level files. |
| `tools/VendorScene/` | Moves a 1.1 trader room scene out of an AssetRipper export into a Unity project and generates stubs for the game scripts it uses. `Unity/VendorSceneBuild.cs` is the editor script that builds the room bundle. |
| `tools/NativeShaderPatch/` | Post-processes a built room bundle: puts the original 1.1 shaders back, restores material keywords, special-format textures and reflection cubemaps, and verifies the result. |
| `release-1.0.0.sha256` | SHA-256 of every file in the 1.0.0 release, including the bundles that are not stored here. |

What `pack/EFT11/` contains:

| Folder | Content |
|---|---|
| `quests/` | 38 quests: Tour of Tarkov 21, Falling Skies 17 |
| `dialogues/` | The 94 retail trader dialogues (`dialogue.json`) and the story dialogues (`tour_1.1.json`, `extra_1.1.json`) |
| `locales/` | Chinese and English texts |
| `items/`, `loot/` | 6 quest items and their raid spawn points |
| `zones/` | 11 quest zones |
| `variables/` | 42 variable groups |
| `images/` | Chapter banners and icons from EFT 1.1 |
| `pack.json`, `bundles.json` | Pack manifest and the list of item bundles |

## Not in this repository

The release also contains Unity asset bundles built from EFT 1.1's own game assets. They are not stored here: a room bundle is 0.3–1.1 GB, far above GitHub's 100 MB file limit. Download the add-on from its SPT Forge page to get them. `release-1.0.0.sha256` lists their hashes.

| Release file | Size | How it is made |
|---|---|---|
| `BepInEx/plugins/VisitAPI/rooms/<traderId>_<trader>` ×7 | 0.3–1.1 GB each | The trader rooms, built with the room pipeline below |
| `BepInEx/plugins/VisitAPI/rooms/vendors_scripts` | 1.9 MB | The shared `Vendors_Scripts` scene (lighting, post-processing), same pipeline, in its own bundle |
| `BepInEx/plugins/VisitAPI/rooms/dialogue.json` | 12 MB | Client-side copy, identical to `pack/EFT11/dialogues/dialogue.json` |
| `…/packs/EFT11/bundles/…/*.bundle` ×3 | 16 MB | Quest item models, copied unchanged from the EFT 1.1 client |

## How the release is made

Inputs: the EFT 1.1 client, a capture of its backend responses (quest list, dialogues, texts), and the SPT 5.0 database.

- **Quests, dialogues, texts.** `QuestPort port` exports a chapter and the quests it depends on. `adapt` makes them load on SPT 4.1 / EFT 0.16: it removes reward types SPT 4.1 does not have, swaps 1.1-only condition types for stand-ins with the same id, and adds the `visitapi` flags that the VisitAPI chapter system reads. Further commands add diary links, dialogues and texts. All commands are listed at the top of `tools/QuestPort/Program.cs`.
- **Quest items.** Item templates, texts and handbook entries come from SPT 5.0. Models are copied unchanged from the 1.1 client. Spawn points are 1.1's fixed spawn points. `QuestPort items` does all of this in one step; the command was added after 1.0.0, and the 1.0.0 items were prepared the same way by hand.
- **Quest zones.** `ZoneExtract <classdata.tpk> <level file> <name regex> <maps>` prints the zone boxes in the pack's `zones` format.
- **Trader rooms.**
  1. Export the 1.1 client with [AssetRipper](https://github.com/AssetRipper/AssetRipper).
  2. `VendorScene extract <scene>` copies one room scene and everything it references into a Unity 2022.3.43 project set up from the community EFT modding SDK. Unity 2022.3.43 is the engine version of both EFT 0.16 and 1.1. The tool also generates stubs for the game scripts the scene uses.
  3. `Unity/VendorSceneBuild.cs` builds the bundle:
     `Unity.exe -batchmode -nographics -quit -projectPath <project> -executeMethod VisitAPI.Editor.VendorSceneBuild.BuildFromCli -scene Vendors_Prapor -bundle 54cb50c76803fa8b248b4571_prapor`
  4. `NativeShaderPatch patch` puts the original 1.1 shaders into the bundle and restores material keywords, special-format textures and reflection cubemaps from the scene's 1.1 level file. `unpack` extracts the result and `verify` compares it with the 1.1 originals.

## Building the tools

- .NET 10 SDK: `dotnet build tools/<Tool>`.
- `ZoneExtract` needs AssetsTools.NET, Mono.Cecil and the texture decoder that ship with [UABEA](https://github.com/nesrak1/UABEA): `dotnet build tools/ZoneExtract -p:Uabea=<UABEA folder>`.
- `NativeShaderPatch` needs `AssetsTools.NET.dll` next to its `.csproj`.
- Input and output paths are set at the top of each `Program.cs` for the author's machine; change them for yours. Code comments are in Chinese.

## License and credits

The pack's own files and the tools are MIT licensed (see `LICENSE`). Quest, dialogue, image and model data originate from Escape from Tarkov 1.1 by Battlestate Games. Not affiliated with Battlestate Games or the SPT team.

Framework: [VisitAPI](https://github.com/TricolourSky/VisitAPI) · Editor: [VisitAPI Editor](https://github.com/TricolourSky/VisitAPI-Editor)
