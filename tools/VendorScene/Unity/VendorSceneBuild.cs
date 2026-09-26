// VisitAPI 1.3.0 —— 把 1.1 的商人房间场景打成 AssetBundle。
//
// 场景由 tools\VendorScene（C#）从 AssetRipper 导出里抽进 Assets/VisitAPI/Vendors/<场景名>/。
// 产物落到 <VisitAPI Rework>\Client\art\bundles\vendors\，**不自动部署** —— 装进游戏是单独一步。
//
// ⚠️ Unity 铁律：**一个 bundle 要么全是场景、要么全是资产，不能混**。
//    所以这里只喂 .unity，它依赖的网格/贴图/材质由 BuildPipeline 自动收进去。
//
// 命令行：
//   Unity.exe -batchmode -nographics -quit -projectPath <SDK> \
//     -executeMethod VisitAPI.Editor.VendorSceneBuild.BuildFromCli -scene Vendors_Prapor -bundle 54cb50c76803fa8b248b4571_prapor
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace VisitAPI.Editor
{
    public static class VendorSceneBuild
    {
        const string Root = "Assets/VisitAPI/Vendors";
        static readonly string OutDir = Path.GetFullPath(Path.Combine(
            Application.dataPath, "..", "..", "build_output"));

        [MenuItem("Tools/VisitAPI/打包商人房间 (Prapor)")]
        public static void BuildPrapor() => Build("Vendors_Prapor", "54cb50c76803fa8b248b4571_prapor");

        /// <summary>
        /// 在编辑器里打开商人房间 + 公共脚本场景（和打进 bundle 的那两个一模一样）。
        /// ⚠️ **编辑器里的观感≠游戏里的观感**：AssetRipper 不反编译着色器，工程里这批 `.shader` 全是
        /// 只输出纯白的空壳；游戏里是插件的 `SceneShaders.Fix()` 在运行时按名字换成真着色器的。
        /// 所以编辑器里发白是**正常的**，别拿它当 bug。要看的是几何/灯光/组件挂载。
        /// </summary>
        [MenuItem("Tools/VisitAPI/打开商人房间 (Prapor + Scripts)")]
        public static void OpenPrapor()
        {
            UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
                $"{Root}/Vendors_Prapor/Vendors_Prapor.unity", UnityEditor.SceneManagement.OpenSceneMode.Single);
            var common = $"{Root}/Vendors_Scripts/Vendors_Scripts.unity";
            if (File.Exists(common))
                UnityEditor.SceneManagement.EditorSceneManager.OpenScene(common, UnityEditor.SceneManagement.OpenSceneMode.Additive);
            Debug.Log("[VisitAPI] 已打开 Vendors_Prapor + Vendors_Scripts（着色器是空壳，发白正常）");
        }

        /// <param name="sceneName">
        /// 逗号分隔可以打多个场景。**第一个必须是商人房间** ——
        /// 插件拿 `GetAllScenePaths()[0]` 当房间场景名（`NarrateEntry.RegisterScene`）。
        /// 1.1 的结构是 `VendorScenePreset { NarrateScene[] Scenes }`，一个商人本来就是多个场景：
        /// 房间 + 公共的 `Vendors_Scripts`（LevelSettings / 灯光管理 / 环境反射探针都在那儿）。
        /// </param>
        public static bool Build(string sceneName, string bundleName)
        {
            var names = sceneName.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
            var scenes = names.Select(n => $"{Root}/{n}/{n}.unity").ToArray();
            foreach (var s in scenes)
                if (!File.Exists(s)) { Debug.LogError("[VisitAPI] 场景不存在: " + s); return false; }

            Directory.CreateDirectory(OutDir);
            var build = new AssetBundleBuild { assetBundleName = bundleName, assetNames = scenes };
            var manifest = BuildPipeline.BuildAssetBundles(OutDir, new[] { build },
                BuildAssetBundleOptions.ChunkBasedCompression, BuildTarget.StandaloneWindows64);

            var outFile = Path.Combine(OutDir, bundleName);
            if (manifest == null || !File.Exists(outFile)) { Debug.LogError("[VisitAPI] 打包失败: " + sceneName); return false; }
            Debug.Log($"[VisitAPI] 房间打包完成：{scenes.Length} 个场景 [{string.Join(", ", names)}] → {outFile} ({new FileInfo(outFile).Length / 1024 / 1024} MB)");
            return true;
        }

        public static void BuildFromCli()
        {
            var argv = Environment.GetCommandLineArgs();
            var scene = Arg(argv, "-scene") ?? "Vendors_Prapor";
            var bundle = Arg(argv, "-bundle") ?? "54cb50c76803fa8b248b4571_prapor";
            Debug.Log($"[VisitAPI] CLI 打包：scene={scene} bundle={bundle}");
            EditorApplication.Exit(Build(scene, bundle) ? 0 : 1);
        }

        /// <summary>
        /// 一次 Unity 会话打多个包（资产导入只做一次）：
        ///   -executeMethod VisitAPI.Editor.VendorSceneBuild.BuildManyFromCli -jobs "包名=场景1,场景2;包名2=场景"
        /// 每个包单独调一次 BuildAssetBundles —— 同一次调用里两个包不能都含同一个场景（Unity 报资产重复归属）。
        /// 2026-09-05 起公共场景 Vendors_Scripts 单独成包 `vendors_scripts`：两个房间包都带它的话，
        /// 包内文件名 BuildPlayer-Vendors_Scripts 撞车，Unity 拒绝加载第二个包。
        /// </summary>
        public static void BuildManyFromCli()
        {
            var argv = Environment.GetCommandLineArgs();
            var jobs = Arg(argv, "-jobs");
            if (string.IsNullOrEmpty(jobs)) { Debug.LogError("[VisitAPI] 缺 -jobs"); EditorApplication.Exit(1); return; }
            var ok = true;
            var done = 0;
            foreach (var job in jobs.Split(';'))
            {
                if (job.Trim().Length == 0) continue;
                var eq = job.IndexOf('=');
                if (eq <= 0) { Debug.LogError("[VisitAPI] 任务格式错: " + job); ok = false; break; }
                var bundle = job.Substring(0, eq).Trim();
                var scenes = job.Substring(eq + 1).Trim();
                Debug.Log($"[VisitAPI] CLI 打包 {done + 1}: bundle={bundle} scene={scenes}");
                if (!Build(scenes, bundle)) { ok = false; break; }
                done++;
            }
            Debug.Log($"[VisitAPI] CLI 批量打包结束：{done} 个包，{(ok ? "全部成功" : "有失败")}");
            EditorApplication.Exit(ok ? 0 : 1);
        }

        /// <summary>
        /// UI 资产包：把某个目录下的全部资产（prefab / Sprite / 贴图 / 动画控制器 / 动画）打成一个资产 bundle（章节屏那套 ChapterUIBuild 的通用版）。
        ///   Unity.exe -batchmode -nographics -quit -projectPath <SDK> -executeMethod VisitAPI.Editor.VendorSceneBuild.BuildUiFromCli
        ///     -root Assets/VisitAPI/NarrateLoading -bundle visitapi_narrateloading.bundle
        /// 2026-09-05 首个用途：1.1 商人加载屏。
        /// </summary>
        public static void BuildUiFromCli()
        {
            var argv = Environment.GetCommandLineArgs();
            var root = Arg(argv, "-root");
            var bundle = Arg(argv, "-bundle");
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(bundle)) { Debug.LogError("[VisitAPI] 缺 -root / -bundle"); EditorApplication.Exit(1); return; }
            var assets = AssetDatabase.FindAssets("", new[] { root })
                .Select(AssetDatabase.GUIDToAssetPath).Where(p => !AssetDatabase.IsValidFolder(p) && !p.EndsWith(".cs")).Distinct().ToArray();
            Directory.CreateDirectory(OutDir);
            var build = new AssetBundleBuild { assetBundleName = bundle, assetNames = assets };
            var manifest = BuildPipeline.BuildAssetBundles(OutDir, new[] { build },
                BuildAssetBundleOptions.ChunkBasedCompression, BuildTarget.StandaloneWindows64);
            var ok = manifest != null && File.Exists(Path.Combine(OutDir, bundle));
            Debug.Log($"[VisitAPI] UI 包 {bundle}: {(ok ? "完成" : "失败")}，{assets.Length} 个资产 [{string.Join(", ", assets.Take(12))}{(assets.Length > 12 ? ", …" : "")}]");
            EditorApplication.Exit(ok ? 0 : 1);
        }

        /// <summary>
        /// 附加资产包：一个材质挂着某张 shader 壳，打成小 bundle；随后 NativeShaderPatch patchone 把壳换成 1.1 真 shader。
        /// 用途：1.1 的延迟光照 shader 塞进场景包没人引用就不会被加载，单独成包由插件 LoadAllAssets 取材质再拿 shader。
        ///   Unity.exe -batchmode -nographics -quit -projectPath <SDK> -executeMethod VisitAPI.Editor.VendorSceneBuild.BuildExtrasFromCli
        ///     -shader "Hidden/Internal-DeferredShadingEFT" -bundle visitapi_deferred11
        /// </summary>
        public static void BuildExtrasFromCli()
        {
            var argv = Environment.GetCommandLineArgs();
            var shaderName = Arg(argv, "-shader");
            var bundle = Arg(argv, "-bundle") ?? "visitapi_extras";
            var shader = shaderName != null ? Shader.Find(shaderName) : null;
            if (shader == null) { Debug.LogError("[VisitAPI] shader 壳不存在: " + shaderName); EditorApplication.Exit(1); return; }
            const string dir = "Assets/VisitAPI/Extras";
            Directory.CreateDirectory(dir);
            var matPath = $"{dir}/{bundle}.mat";
            AssetDatabase.CreateAsset(new Material(shader) { name = bundle }, matPath);
            AssetDatabase.SaveAssets();
            Directory.CreateDirectory(OutDir);
            var build = new AssetBundleBuild { assetBundleName = bundle, assetNames = new[] { matPath } };
            var manifest = BuildPipeline.BuildAssetBundles(OutDir, new[] { build },
                BuildAssetBundleOptions.ChunkBasedCompression, BuildTarget.StandaloneWindows64);
            var ok = manifest != null && File.Exists(Path.Combine(OutDir, bundle));
            Debug.Log($"[VisitAPI] 附加包 {bundle}: {(ok ? "完成" : "失败")} shader={shaderName}");
            EditorApplication.Exit(ok ? 0 : 1);
        }

        static string Arg(string[] argv, string key)
        {
            var i = Array.IndexOf(argv, key);
            return i >= 0 && i + 1 < argv.Length ? argv[i + 1] : null;
        }
    }
}
