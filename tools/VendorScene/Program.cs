using System.Text;
using System.Text.RegularExpressions;

// VisitAPI 1.3.0 —— 把 AssetRipper 导出的 1.1 商人房间抽进 Unity SDK 工程。
//   index                 建 guid → 资产路径 索引（缓存到 index.tsv，312K 个 .meta 只扫一次）
//   analyze <场景名>       算这个场景的传递引用闭包，报个数和体积
//   extract <场景名>       剥掉 MonoBehaviour → 算闭包 → 连 .meta 拷进 SDK（GUID 原样，引用才不断）
// 场景名例：Vendors_Prapor
//
// 为什么剥 MonoBehaviour：AssetRipper 生成的脚本是 Il2CppDummyDll 反编译产物（带 [Token]/[Address]，
// 引用整条 EFT 类型链），**编不过**。第一轮只验「f2 场景能否在 f1 打包并在 SPT 加载」，
// 几何体+灯光+相机点就够；验通之后再按 DEV_NOTES #69 那套「同 GUID 空壳桩」把组件加回来。

static class Paths
{
    // 导出原先在旧仓库 VisitAPI Rework\tools 下；旧仓库 2026-10-03 退役清理（商人房间不再重打）。
    // 要再跑抽取，先按 Dev_Note M4.2 用 AssetRipper 重新导出到这个位置。
    public const string Export = @"E:\项目\VisitAPI\.work\EFT111_Vendors_Export\ExportedProject\Assets";
    // 2026-09-05 起打包工程是隔离工程 IsolatedSDK（坑 #122 的 StripUnusedMeshComponents=0、#105 的 MultiFlareSdk 改名都只在这份里），
    // 主 SDK EscapeFromTushonka-SDK 不再打商人房间。仍可用环境变量 VISITAPI_VENDOR_SDK 覆写。
    public static readonly string Sdk = Environment.GetEnvironmentVariable("VISITAPI_VENDOR_SDK")
        ?? @"E:\项目\VisitAPI\.work\native_bundle\IsolatedSDK";
    public const string Spt = @"D:\EFT";                 // 2026-09-05 起游戏目录 D:\SPT → D:\EFT（SPT 4.1.4）
    public static readonly string Tool = AppContext.BaseDirectory;
    public static string Cache => Path.Combine(Tool, "index.tsv");
    public static string Scene(string name) => Path.Combine(Export, "Content", "Locations", "Venders", name + ".unity");
}

static class Program
{
    static readonly Regex GuidRe = new(@"guid: ([0-9a-f]{32})", RegexOptions.Compiled);
    const string ULipPlugin = "Plugins/uLipSync.Runtime.dll";

    // 这些目录下的文件是「叶子」：不再往里找引用（网格/贴图体积大且不引用别人）
    static readonly string[] Leaf =
    {
        "Mesh/", "Texture2D/", "Texture2DArray/", "Texture3D/", "Shader/", "ComputeShader/", "Scripts/",
        "Plugins/", "AudioClip/", "TerrainData/", "NavMeshData/", "OcclusionCullingData/", "Avatar/",
        "LightProbes/", "ShaderVariantCollection/", "BillboardAsset/", "SpeedTreeWindAsset/", "TextAsset/",
    };

    // 第一轮不能进 SDK 的东西：
    //   Scripts/   AssetRipper 生成的是 Il2CppDummyDll 反编译产物，**编不过** → Unity 一导入就红，打不了包
    //   Plugins/   同上，是 dll 镜像；uLipSync 单独装 0.16 真 DLL，并沿用 1.1 meta GUID
    static readonly string[] Excluded = { "Scripts/", "Plugins/", "TextAsset/" };

    // ⚠️ `MonoBehaviour/` 是 ScriptableObject 资产，**不能整个排掉**：Prapor 那 436 个里有 3 个 `SoundBank`
    //    （环境音的音频库，`AmbientSoundPlayer._ambientBank` 指着它，排掉就没声音，实机实证）。
    //    另外 431 个是 uLipSync 烘焙数据；现在用 0.16 真 DLL 接住，必须一并收进来。
    //    所以按脚本挑：认得的类（在白名单或 SDK 里）才收。

    // ⚠️ `Content/` **不能整个排掉**：里面只有两种东西 ——
    //    ① 原始未剥离的 `.unity`（该排：场景自引用会把它再拷一份，带着 328 个缺脚本的组件）
    //    ② `<场景名>/LightingData.asset` = **烘焙光照**（绝不能排）
    //    第一轮把整个 Content/ 排掉，结果房间只剩环境光、平板发白（M4g 问题 3）。
    static bool Skip(string rel) =>
        Excluded.Any(p => rel.StartsWith(p, StringComparison.Ordinal)) ||
        (rel.StartsWith("Content/", StringComparison.Ordinal) && rel.EndsWith(".unity", StringComparison.Ordinal));

    // ── 要保留的组件：**自动判定，不再手工列白名单** ──────────
    //
    // 方法论（Memory.MD §6.2.0）：「1.1 商人 3D 场景包里有啥你就用啥」。
    // 所以判据只有一条 —— **0.16 的 Assembly-CSharp 里有没有这个类**：
    //   有 → 造桩，运行时自动绑上游戏真类
    //   没有 → 绑不上（日志 `The referenced script ... is missing!`），得从 IL2CPP 重写进插件，本轮先剥掉
    //
    // ⚠️ 手工白名单是错的做法（2026-09-02 实证）：我只留了 27 个，而 0.16 其实接得住 **42** 个，
    //    被漏掉的里面有 `TOD_Scattering`/`ChromaticAberration`/`VolumetricLightRenderer`/`Undithering`/`FlareLight`
    //    这些**画面效果类**，很可能就是光影不对的原因。
    /// <summary>
    /// ⚠️ **即使 0.16 有也不要**的组件 —— 这条是防回归（M4i 拍过板，M4k 换成自动判定后又被带回来了）。
    /// Meta XR Audio SDK 的真类住在独立 DLL 里，AssetRipper 没反编译 → 桩必然缺字段 → 运行时真类拿到 null，
    /// `MetaSpatialAudioSource.Awake()` NRE 顺着 `NPCAudioSourceSpatializeController` 把整条 NPC 音频链带死。
    /// 代价只是没有空间音效定位；环境音和台词本身不依赖它们。
    /// </summary>
    static readonly HashSet<string> Banned = new(StringComparer.Ordinal)
    {
        "MetaSpatialAudioSource", "NPCAudioSourceSpatializeController",
        // 2026-09-05：Therapist 房间挂了一个 `MultiFlare.MultiFlare`——0.16 里它是 `[Obsolete]` 的空类（一个字段一行逻辑都没有）。
        // 给它造桩 = 在 namespace MultiFlare 里放一个叫 MultiFlare 的类，同命名空间下其余桩写的 `MultiFlare.Flare` 全部被它遮蔽（CS0426）。
        // 剥掉零损失。
        "MultiFlare",
    };

    /// <summary>
    /// 1.1 独有、0.16 没有，但**插件里已经自己实现了**的类（Memory.MD §6.2.0「套不上就重写」）。
    /// 走 DEV_NOTES #69 的 `VISIT_STUBS` 那套：桩放进带 asmdef 的 **`VisitAPI` 程序集**，
    /// 运行时 Unity 按（程序集名, 命名空间, 类名）去找 —— 正好是我们的 BepInEx 插件（AssemblyName=VisitAPI）。
    /// 清单里加一个名字之前，**必须先在插件里写好同名同命名空间的真类**，否则照样绑不上。
    /// </summary>
    static readonly HashSet<string> Rewritten = new(StringComparer.Ordinal)
    {
        "NPCSceneAudioController", "NPCCrossfadeAnimationSoundPlayer", "AudioClipConfigDictionary", "ULipSyncCurvesPlayer",
        // 2026-09-06：Fence 的 105 句台词用的是 1.1 独有的 `Cutscene.BakedDataAudioOnly : uLipSync.BakedData`（只放音频不驱口型），
        // 之前当成 0.16 接不住的类整批剥掉 → Fence 无声。桩是手写的（StubGen 只会生成 : MonoBehaviour，这个得 : uLipSync.BakedData），
        // 放在 _VisitStubs\BakedDataAudioOnly.cs，guid 用 1.1 原脚本的；插件里同名类在 Native111\VendorLipSync.cs。
        "BakedDataAudioOnly",
        // 2026-09-06：1.1 的季节开关（Skier / Jaeger 房里 SUMMER / WINTER 两组物件各挂一个）。手写桩 _VisitStubs\SeasonEnvironment.cs
        //（字段是 byte 枚举数组，StubGen 会写成 int[] 对不上序列化布局）；插件里同名类在 Native111\SeasonGate.cs。
        "SeasonEnvironment",
        // 2026-09-07：1.1 的头发专用打光（头部渲染器上一个，把 ≤4 盏灯喂给 Characters/TraiderHair 的 _HairLight* 属性）。
        // 剥掉 = 不戴帽子的商人头发一团死黑。手写桩 _VisitStubs\HairLights.cs（List<Light> _lights）；插件里同名类 Native111\HairLights.cs。
        "HairLights",
    };

    static HashSet<string> _keep;

    /// <summary>给 UiPrefab 用：0.16 接得住的类表 + 往共用桩目录写桩。</summary>
    internal static HashSet<string> KeepPublic => Keep;
    internal static void WriteStubsPublic(Dictionary<string, string> kept)
    {
        StubGen.SetExisting(SdkAllTypes());
        WriteStubs(StubDir, kept, new List<string>());
    }

    static HashSet<string> Keep
    {
        get
        {
            if (_keep != null) return _keep;
            _keep = new HashSet<string>(StringComparer.Ordinal);
            var have = Survey.GameTypes();
            var global = Survey.GlobalTypes();
            global.UnionWith(SdkGlobalTypes());   // 冲突主要来自 SDK 自己的脚本
            StubGen.BuildIndex(Path.Combine(Paths.Export, "Scripts", "Assembly-CSharp"));
            var clash = 0;
            foreach (var cls in StubGen.AllClasses())
            {
                if (Banned.Contains(cls)) continue;
                if (Rewritten.Contains(cls)) { _keep.Add(cls); continue; }
                var ns = StubGen.NamespaceOf(cls);
                if (!have.Contains(ns.Length > 0 ? ns + "." + cls : cls)) continue;
                // 桩声明的命名空间和某个全局类同名 → CS0101，编不过（`MultiFlare` 就是这么撞的）
                if (ns.Length > 0 && global.Contains(ns.Split('.')[0])) { clash++; continue; }
                _keep.Add(cls);
            }
            Console.WriteLine($"要接的类：{_keep.Count:N0} 个（0.16 自动判定 + {Rewritten.Count} 个重写进插件的）"
                            + (clash > 0 ? $"；另有 {clash} 个因命名空间与全局类重名而排除" : "")
                            + $"；黑名单 {Banned.Count} 个");
            return _keep;
        }
    }

    static int Main(string[] args)
    {
        if (args.Length == 0) { Console.WriteLine("用法: VendorScene index | analyze <场景名> | extract <场景名> | survey <场景名> | fixcontrollers"); return 1; }
        switch (args[0])
        {
            case "index": BuildIndex(); return 0;
            case "analyze":
                if (args.Length < 2) { Console.WriteLine("缺场景名"); return 1; }
                Analyze(args[1]); return 0;
            case "extract":
                if (args.Length < 2) { Console.WriteLine("缺场景名"); return 1; }
                Extract(args[1]); return 0;
            case "survey":
                if (args.Length < 2) { Console.WriteLine("缺场景名"); return 1; }
                Survey.Run(args[1]); return 0;
            case "uiprefab":
                // 1.1 UI 场景子树 → prefab（加载屏等），见 UiPrefab.cs
                UiPrefab.Run(args); return 0;
            case "fixcontrollers":
                // 把工程里已有的全部 .controller 的 StateMachineBehaviour 脚本引用按同一套映射改指（坑 #100 的通用版）
                StubGen.BuildIndex(Path.Combine(Paths.Export, "Scripts", "Assembly-CSharp"));   // SdkScripts 要查 1.1 类的命名空间
                RewriteControllers(Directory.EnumerateFiles(Path.Combine(Paths.Sdk, "Assets", "VisitAPI", "Vendors"), "*.controller", SearchOption.AllDirectories).ToList(), LoadIndex());
                return 0;
            default: Console.WriteLine("未知命令: " + args[0]); return 1;
        }
    }

    // ── guid 索引 ────────────────────────────────────────────
    static Dictionary<string, string> BuildIndex()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var n = 0;
        var buf = new byte[400];
        foreach (var meta in Directory.EnumerateFiles(Paths.Export, "*.meta", SearchOption.AllDirectories))
        {
            n++;
            if ((n & 0xFFFF) == 0) Console.WriteLine($"  ...已扫 {n:N0} 个 .meta，{sw.Elapsed.TotalSeconds:F0} 秒");
            int read;
            try { using var fs = File.OpenRead(meta); read = fs.Read(buf, 0, buf.Length); }
            catch { continue; }
            var m = GuidRe.Match(Encoding.ASCII.GetString(buf, 0, read));
            if (!m.Success) continue;
            var rel = Path.GetRelativePath(Paths.Export, meta[..^5]).Replace('\\', '/');
            map.TryAdd(m.Groups[1].Value, rel);
        }
        using (var w = new StreamWriter(Paths.Cache, false, new UTF8Encoding(false)))
            foreach (var kv in map) w.WriteLine(kv.Key + "\t" + kv.Value);
        Console.WriteLine($"索引完成：{n:N0} 个 .meta → {map.Count:N0} 个 guid，用时 {sw.Elapsed.TotalSeconds:F0} 秒");
        Console.WriteLine("缓存：" + Paths.Cache);
        return map;
    }

    internal static Dictionary<string, string> LoadIndexPublic() => LoadIndex();

    static Dictionary<string, string> LoadIndex()
    {
        if (!File.Exists(Paths.Cache)) return BuildIndex();
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(Paths.Cache))
        {
            var t = line.IndexOf('\t');
            if (t > 0) map.TryAdd(line[..t], line[(t + 1)..]);
        }
        Console.WriteLine($"载入索引：{map.Count:N0} 个 guid");
        return map;
    }

    static bool IsLeaf(string rel) => Leaf.Any(p => rel.StartsWith(p, StringComparison.Ordinal));

    // ── 传递闭包 ────────────────────────────────────────────
    static void Analyze(string sceneName)
    {
        var idx = LoadIndex();
        var scenePath = Paths.Scene(sceneName);
        if (!File.Exists(scenePath)) { Console.WriteLine("场景不存在: " + scenePath); return; }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var resolved = Closure(File.ReadAllText(scenePath), idx, out var unresolved);

        // ── 统计 ────────────────────────────────────────────
        var byFolder = new Dictionary<string, (int n, long size)>(StringComparer.Ordinal);
        long total = 0;
        foreach (var rel in resolved)
        {
            var slash = rel.IndexOf('/');
            var folder = slash < 0 ? "(根)" : rel[..slash];
            var full = Path.Combine(Paths.Export, rel.Replace('/', '\\'));
            long size = 0;
            foreach (var f in new[] { full, full + ".meta" })
                if (File.Exists(f)) size += new FileInfo(f).Length;
            total += size;
            byFolder.TryGetValue(folder, out var cur);
            byFolder[folder] = (cur.n + 1, cur.size + size);
        }

        Console.WriteLine();
        Console.WriteLine($"=== {sceneName} 传递闭包（用时 {sw.Elapsed.TotalSeconds:F1} 秒）===");
        Console.WriteLine($"{"目录",-26}{"个数",8}{"体积",14}");
        foreach (var kv in byFolder.OrderByDescending(k => k.Value.size))
            Console.WriteLine($"{kv.Key,-26}{kv.Value.n,8:N0}{Mb(kv.Value.size),14}");
        Console.WriteLine(new string('-', 48));
        Console.WriteLine($"{"合计",-26}{resolved.Count,8:N0}{Mb(total),14}");
        Console.WriteLine($"未解析 guid: {unresolved.Count:N0}（多半是内置资源/包内资源，正常）");

        var dump = Path.Combine(Paths.Tool, "closure_" + sceneName + ".tsv");
        using (var w = new StreamWriter(dump, false, new UTF8Encoding(false)))
            foreach (var rel in resolved.OrderBy(r => r, StringComparer.Ordinal)) w.WriteLine(rel);
        Console.WriteLine("闭包清单已写出：" + dump);
    }

    static List<string> Closure(string startText, Dictionary<string, string> idx, out Dictionary<string, int> unresolved)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        unresolved = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match m in GuidRe.Matches(startText))
            if (seen.Add(m.Groups[1].Value)) queue.Enqueue(m.Groups[1].Value);
        Console.WriteLine($"直接引用 {seen.Count:N0} 个 guid，展开传递闭包…");

        var resolved = new List<string>();
        while (queue.Count > 0)
        {
            var g = queue.Dequeue();
            if (!idx.TryGetValue(g, out var rel)) { unresolved[g] = 0; continue; }
            resolved.Add(rel);
            if (IsLeaf(rel)) continue;
            var full = Path.Combine(Paths.Export, rel.Replace('/', '\\'));
            if (!File.Exists(full) || new FileInfo(full).Length > 20L * 1024 * 1024) continue;
            string text;
            try { text = File.ReadAllText(full); } catch { continue; }
            foreach (Match m in GuidRe.Matches(text))
                if (seen.Add(m.Groups[1].Value)) queue.Enqueue(m.Groups[1].Value);
        }
        return resolved;
    }

    // ── 剥 MonoBehaviour ───────────────────────────────────
    // Unity 场景 YAML 是一串 `--- !u!<类号> &<文件内 id>` 文档。114 = MonoBehaviour。
    // 删掉这些文档还不够：GameObject 的 m_Component 列表里还挂着它们的 fileID，
    // 留着会让 Unity 导入时报「缺失脚本」，别处指向它们的引用也要清成 0。
    static string StripMonoBehaviours(string scene, Dictionary<string, string> idx, out int removed,
                                      out Dictionary<string, string> keptGuids)
    {
        var docs = Regex.Split(scene, @"(?m)^--- !u!");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        keptGuids = new Dictionary<string, string>(StringComparer.Ordinal);   // 类名 → 脚本 guid
        foreach (var d in docs.Skip(1))
        {
            var m = Regex.Match(d, @"^(\d+) &(\d+)");
            if (!m.Success || m.Groups[1].Value != "114") continue;
            // 白名单里的组件留下，其余剥掉
            var sc = Regex.Match(d, @"m_Script: \{fileID: -?\d+, guid: ([0-9a-f]{32})");
            if (sc.Success && idx.TryGetValue(sc.Groups[1].Value, out var rel))
            {
                if (rel == ULipPlugin) continue;
                if (rel.StartsWith("Scripts/", StringComparison.Ordinal))
                {
                    var cls = Path.GetFileNameWithoutExtension(rel);
                    if (Keep.Contains(cls)) { keptGuids[cls] = sc.Groups[1].Value; continue; }
                }
            }
            ids.Add(m.Groups[2].Value);
        }
        removed = ids.Count;

        var sb = new StringBuilder(docs[0]);
        foreach (var d in docs.Skip(1))
        {
            var m = Regex.Match(d, @"^(\d+) &(\d+)");
            if (m.Success && ids.Contains(m.Groups[2].Value)) continue;
            var body = d;
            // GameObject 组件表里指向 MonoBehaviour 的那几行整行删掉
            body = Regex.Replace(body, @"(?m)^  - component: \{fileID: (\d+)\}\r?\n",
                mm => ids.Contains(mm.Groups[1].Value) ? "" : mm.Value);
            // 其余散落的引用清零（同一文件内的引用没有 guid 段）
            body = Regex.Replace(body, @"\{fileID: (\d+)\}",
                mm => ids.Contains(mm.Groups[1].Value) ? "{fileID: 0}" : mm.Value);
            sb.Append("--- !u!").Append(body);
        }
        return sb.ToString();
    }

    // ── SDK 里已有的脚本 ───────────────────────────────────
    // SDK（EscapeFromTushonka）自带 472 个 EFT 脚本，覆盖服装/外观体系。
    // 这些类**绝不能再造桩** —— 同一个程序集里出现两个同名类，Unity 直接编译报错。
    // 改成把场景里的 m_Script guid 改指到 SDK 那份（字段还更精确，因为是真源码）。
    /// <summary>
    /// SDK 自己的脚本里**没有命名空间**的类型名。
    /// ⚠️ 冲突发生在 SDK 侧不是游戏侧：`Assets/Scripts/MultiFlare.cs` 声明了全局类 `MultiFlare`，
    /// 而给 `FlareLight` 生成的桩命名空间恰好也叫 `MultiFlare` → C# 直接 CS0101，整个工程编不过。
    /// </summary>
    static HashSet<string> SdkGlobalTypes() => ScanSdk(globalOnly: true);

    /// <summary>
    /// SDK 自己脚本里声明的所有类型（全名 + 简单名）。
    /// ⚠️ 生成**共用依赖类型**前必须查一遍：SDK 里已经有的就别再生成，
    /// 否则同名重复定义（CS0101）—— `SeasonsMaterialsGroup` 和 `EFT.Impostors.AmplifyImpostorsArray` 就是这么撞的。
    /// 主类走 `SdkScripts()` 按文件名查（查到就改指），依赖类型之前漏了这一步。
    /// </summary>
    static HashSet<string> SdkAllTypes() => ScanSdk(globalOnly: false);

    static HashSet<string> ScanSdk(bool globalOnly)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        // ⚠️ 要扫**整个 Assets**：SDK 的类不只住在 Assets\Scripts（Systems / Content / Plugins 里都有）
        var root = Path.Combine(Paths.Sdk, "Assets");
        if (!Directory.Exists(root)) return set;
        var stubs = Path.Combine("VisitAPI", "Vendors", "_Stubs");
        foreach (var cs in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (cs.Contains(stubs, StringComparison.OrdinalIgnoreCase)) continue;   // 我们自己生成的不算
            string t;
            try { t = File.ReadAllText(cs); } catch { continue; }
            var ns = Regex.Match(t, @"(?m)^namespace\s+([\w.]+)").Groups[1].Value;
            if (globalOnly && ns.Length > 0) continue;                    // 有命名空间就不会和命名空间声明撞
            foreach (Match m in Regex.Matches(t, @"(?m)^([ \t]*)(?:public|internal)\s+(?:sealed\s+|abstract\s+|static\s+|partial\s+)*(?:class|struct|enum|interface)\s+([A-Za-z_]\w*)"))
            {
                var indent = m.Groups[1].Value.Replace("    ", "\t").Length;
                var name = m.Groups[2].Value;
                // ⚠️ **只认顶层类型**：嵌套类型（缩进更深）就算同名也没用 ——
                //    我们的桩是按裸名引用的，而嵌套类型要写 `外层.内层` 才解析得到
                //    （`CullingSettings` 嵌在 SDK 的 `AmbientLight` 里，按简单名误判成「已有」就会漏生成 → CS0246）。
                var topLevel = ns.Length == 0 ? indent == 0 : indent <= 1;
                if (!topLevel) continue;
                if (globalOnly) { set.Add(name); continue; }
                set.Add(ns.Length > 0 ? ns + "." + name : name);          // 判重只用全名，简单名太松
            }
        }
        return set;
    }

    /// <summary>
    /// SDK 自带脚本：类名 → (guid, 路径)。2026-09-05 起**按文件里真声明的类**收，不再只看文件名：
    /// ① 文件里必须真有 `class 同名`（隔离工程把 SDK 的全局类 `MultiFlare` 改名成了 `MultiFlareSdk`，文件名没变——
    ///    改指过去等于指向一个 Unity 绑不上的死脚本）；
    /// ② 命名空间必须和 1.1 的真类一致（坑 #105：SDK 的全局 `ProFlareAtlas : MonoBehaviour` 和 1.1 的
    ///    `MultiFlare.ProFlareAtlas : ScriptableObject` 同名不同类，改指过去数据全丢）。不一致的当 SDK 没有，走造桩。
    /// </summary>
    static Dictionary<string, (string Guid, string Path)> SdkScripts()
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        var root = Path.Combine(Paths.Sdk, "Assets", "Scripts");
        if (!Directory.Exists(root)) return map;
        foreach (var meta in Directory.EnumerateFiles(root, "*.cs.meta", SearchOption.AllDirectories))
        {
            var g = Regex.Match(File.ReadAllText(meta), @"guid: ([0-9a-f]{32})");
            if (!g.Success) continue;
            var cs = meta[..^5];
            var name = Path.GetFileNameWithoutExtension(cs);
            string text;
            try { text = File.ReadAllText(cs); } catch { continue; }
            if (!Regex.IsMatch(text, @"\b(class|struct)\s+" + Regex.Escape(name) + @"\b")) continue;       // ① 文件名 ≠ 类名的死脚本不算
            var ns = Regex.Match(text, @"(?m)^\s*namespace\s+([\w.]+)").Groups[1].Value;
            var want = StubGen.NamespaceOf(name);
            if (!string.Equals(ns, want, StringComparison.Ordinal))                                          // ② 命名空间不一致的不算
            {
                if (Keep.Contains(name)) Console.WriteLine($"  SDK 同名脚本命名空间不一致，不改指: {name}（SDK '{ns}' vs 1.1 '{want}'）");
                continue;
            }
            map.TryAdd(name, (g.Groups[1].Value, cs));
        }
        return map;
    }

    // ── 核对并补齐 SDK 脚本的字段 ──────────────────────────
    /// <summary>
    /// ⚠️ **「改指 SDK 零风险」是错的**（2026-09-02 实机实证）。
    /// SDK 的脚本是**别的 EFT 版本**的反编译产物，可能比 1.1 的真类少字段；
    /// 少一个，Unity 打包时就把那个字段的数据**静默丢掉**，运行时游戏真类拿到 null —— 不报错、只是不工作。
    ///
    /// 实例：SDK 的 `AmbientLight` 只有 4 个 Shader 字段，缺 `AnalyticSourceShader`，
    /// 于是 `Initialize()` 里 `new Material(null)` 每帧抛 ArgumentNullException（实机一次访问 2556 条），
    /// 整套环境光/反射从没启动过 —— 这就是「光影发白」的根因。
    ///
    /// 修法：按 1.1 的真类型把缺的字段补进 SDK 脚本（它本来就是反编译产物，补齐才是对的）。
    /// </summary>
    static void PatchSdkFields(string cls, Dictionary<string, (string Guid, string Path)> sdk, List<string> warn)
    {
        var csPath = sdk[cls].Path;
        var have = StubGen.FieldNamesIn(csPath, cls, out var bse);
        for (var i = 0; i < 4 && bse.Length > 0 && !StubGen.IsUnityBase(bse); i++)
        {
            if (!sdk.TryGetValue(bse, out var b)) break;                   // 基类不在 SDK 里就查不下去了
            have.UnionWith(StubGen.FieldNamesIn(b.Path, bse, out bse));
        }
        var lines = new StringBuilder();
        foreach (var (type, name, raw) in StubGen.MappedFields(cls, warn))
        {
            if (have.Contains(name)) continue;
            if (type == null || !Resolvable(type)) { warn.Add($"{cls}.{name} 缺字段但类型不保险({raw})，没补"); continue; }
            lines.Append($"\n\tpublic {type} {name};\n");
        }
        if (lines.Length == 0) return;
        EnsureUsings(csPath);
        var block = "\n\t// ==== VisitAPI 自动补：1.1 的真类有、SDK 这份反编译产物缺 ====\n"
                  + "\t// 不补的话这些字段的数据在打包时会被静默丢掉，运行时游戏真类拿到 null。\n" + lines;
        Console.WriteLine(StubGen.AppendToClass(csPath, cls, block)
            ? $"  ⚠️ 补字段 {cls}: {Regex.Matches(lines.ToString(), @"\s(\w+);").Count} 个 → {Path.GetFileName(csPath)}"
            : $"  ⚠️ {cls} 缺字段但插不进去（没找到类的花括号）: {csPath}");
    }

    /// <summary>
    /// 只补**类型一定解析得到**的字段：Unity 内置类型（含 `MonoBehaviour` 这种 PPtr 兜底）+ 它们的数组/List。
    /// ⚠️ 自定义依赖类型一律不补 —— 那要靠 `_Stubs` 里另生成的类，跨命名空间很容易 CS0246/CS0234，
    ///    而**一个编译错误就让整个 SDK 工程停摆、连 bundle 都打不出来**，代价远大于少补一个字段。
    ///    少补的会打印出来，需要时再单独处理。
    /// </summary>
    static bool Resolvable(string mapped)
    {
        var t = mapped.Trim();
        while (t.EndsWith("[]", StringComparison.Ordinal)) t = t[..^2].Trim();
        var g = Regex.Match(t, @"^List<(.+)>$");
        return g.Success ? Resolvable(g.Groups[1].Value) : t == "int" || StubGen.IsBuiltin(t);
    }

    /// <summary>补进去的字段可能用到 `List&lt;&gt;` / `AudioMixerGroup` / `UnityEvent` —— SDK 那份脚本未必 using 全了。</summary>
    static void EnsureUsings(string file)
    {
        var src = File.ReadAllText(file);
        var add = new[] { "System.Collections.Generic", "UnityEngine", "UnityEngine.Audio", "UnityEngine.Events" }
            .Where(u => !Regex.IsMatch(src, @"(?m)^using\s+" + Regex.Escape(u) + @"\s*;")).ToList();
        if (add.Count > 0)
            File.WriteAllText(file, string.Concat(add.Select(u => $"using {u};\n")) + src, new UTF8Encoding(true));
    }

    /// <summary>
    /// `VisitAPI` 程序集桩的家（DEV_NOTES #69 的 `VISIT_STUBS` 同款）。
    /// ⚠️ **不能自己建 asmdef**：SDK 里早就有一个叫 `VisitAPI` 的了
    ///    （`Assets\VisitAPI\ChapterUI\Scripts\VisitAPI\VisitAPI.asmdef`，章节 UI 那套用的），
    ///    同名两个 asmdef 会让 Unity 直接报错。这里放 **asmref**，把本目录挂进那个已有程序集。
    ///    程序集名就是运行时要找的名字，和插件的 `AssemblyName=VisitAPI` 对上，缺一不可。
    /// </summary>
    static string VisitStubDir => Path.Combine(Paths.Sdk, "Assets", "VisitAPI", "Vendors", "_VisitStubs");

    static void InstallULipSync()
    {
        var dir = Path.Combine(Paths.Sdk, "Assets", "VisitAPI", "Vendors", "_Plugins");
        Directory.CreateDirectory(dir);
        var dst = Path.Combine(dir, "uLipSync.Runtime.dll");
        File.Copy(Path.Combine(Paths.Spt, "EscapeFromTarkov_Data", "Managed", "uLipSync.Runtime.dll"), dst, true);
        var meta = File.ReadAllText(Path.Combine(Paths.Export, ULipPlugin.Replace('/', '\\') + ".meta"));
        meta = Regex.Replace(meta,
            @"(?m)(^\s*Editor: Editor\r?\n\s*second:\r?\n\s*enabled:) 0$",
            "$1 1");
        File.WriteAllText(dst + ".meta", meta, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>
    /// 兼容 shader 源码（头发 / 雾片）覆盖到**本场景自己带的**同名 shader 壳上。
    /// 2026-09-05 改：只在本场景目录里已有那张壳（连 .meta）时才覆盖；别的场景目录里已经有一份（同 GUID）就不再复制——
    /// 以前每个场景目录都塞一份同 GUID 文件，Unity 导入时 GUID 冲突、随机给其中一份换新 GUID，引用就断了。
    /// 最终 bundle 里这些 shader 仍会在 NativeShaderPatch 阶段被 1.1 原始 Shader 对象整个替换，源码只是让 Unity 肯打包。
    /// </summary>
    static void InstallCompatShaders(string outDir)
    {
        var srcDir = Path.Combine(Paths.Tool, "Shaders");
        var dstDir = Path.Combine(outDir, "Shader");
        var vendors = Path.GetDirectoryName(outDir)!;
        foreach (var src in Directory.EnumerateFiles(srcDir, "*.shader"))
        {
            var name = Path.GetFileName(src);
            var dst = Path.Combine(dstDir, name);
            if (File.Exists(dst + ".meta")) { File.Copy(src, dst, true); Console.WriteLine($"  兼容 shader 覆盖: {name}"); continue; }
            var elsewhere = Directory.EnumerateFiles(vendors, name + ".meta", SearchOption.AllDirectories)
                .Any(p => !p.StartsWith(outDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            Console.WriteLine(elsewhere ? $"  兼容 shader 已在别的场景目录: {name}（不重复放）" : $"  兼容 shader 本场景不引用也没别处有: {name}（跳过）");
        }
    }

    /// <summary>
    /// 工程里（除本场景目录外）已经存在的资产 GUID：闭包里撞上的直接跳过不再拷一份。
    /// Unity 按 GUID 认资产、不看目录，一份就够；同 GUID 拷两份反而触发「GUID 冲突、换新 GUID」把引用弄断。
    /// 顺带省掉大头的导入时间（各房间共用的道具/贴图/口型烘焙数据体积很大）。
    /// </summary>
    static HashSet<string> ExistingGuids(string outDir)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var vendors = Path.GetDirectoryName(outDir)!;
        if (!Directory.Exists(vendors)) return set;
        var buf = new byte[400];
        foreach (var meta in Directory.EnumerateFiles(vendors, "*.meta", SearchOption.AllDirectories))
        {
            if (meta.StartsWith(outDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            int read;
            try { using var fs = File.OpenRead(meta); read = fs.Read(buf, 0, buf.Length); }
            catch { continue; }
            var m = GuidRe.Match(Encoding.ASCII.GetString(buf, 0, read));
            if (m.Success) set.Add(m.Groups[1].Value);
        }
        Console.WriteLine($"  工程里已有资产 {set.Count:N0} 个 GUID（闭包里撞上的不再拷）");
        return set;
    }

    /// <summary>
    /// 动画控制器里的 StateMachineBehaviour（`AnimationEventsStateBehaviour` 那一族）也是按脚本 GUID 引用的，
    /// 场景/资产的改指没覆盖到它——坑 #100：SDK 已有同名脚本但 GUID 是另一套，打包时整族变「丢失脚本」，
    /// 互动音效事件源整个消失。这里对 .controller 里每个 m_Script 按同一套规矩改指：
    /// 有同 GUID 桩 → 原样；SDK 有同名脚本 → 改指 SDK 的 GUID；都没有 → 原样并警告。
    /// </summary>
    static void RewriteControllers(List<string> files, Dictionary<string, string> idx)
    {
        if (files.Count == 0) return;
        var sdk = SdkScripts();
        var stubs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dir in new[] { StubDir, VisitStubDir })
            if (Directory.Exists(dir))
                foreach (var meta in Directory.EnumerateFiles(dir, "*.cs.meta"))
                {
                    var g = GuidRe.Match(File.ReadAllText(meta));
                    if (g.Success) stubs.Add(g.Groups[1].Value);
                }
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var changed = 0;
            var notes = new List<string>();
            foreach (var guid in Regex.Matches(text, @"m_Script: \{fileID: -?\d+, guid: ([0-9a-f]{32})").Select(m => m.Groups[1].Value).Distinct().ToList())
            {
                if (stubs.Contains(guid) || sdk.Values.Any(v => v.Guid == guid)) continue;     // 桩 / 已经是 SDK 的：不动
                if (!idx.TryGetValue(guid, out var rel) || !rel.StartsWith("Scripts/", StringComparison.Ordinal)) { notes.Add($"{guid[..8]}… 不在导出索引里"); continue; }
                var cls = Path.GetFileNameWithoutExtension(rel);
                if (!sdk.TryGetValue(cls, out var target)) { notes.Add($"{cls} 既无桩也不在 SDK（打包会变丢失脚本）"); continue; }
                var n = Regex.Matches(text, guid).Count;
                text = text.Replace(guid, target.Guid, StringComparison.Ordinal);
                changed += n;
                notes.Add($"{cls} {guid[..8]}… → SDK {target.Guid[..8]}… ×{n}");
            }
            if (changed > 0) File.WriteAllText(file, text, new UTF8Encoding(false));
            Console.WriteLine($"  控制器 {Path.GetFileName(file)}: 改指 {changed} 处" + (notes.Count > 0 ? "；" + string.Join("；", notes) : ""));
        }
    }

    /// <summary>闭包里的 .controller 用到的脚本类，先收进 kept——它们和场景组件走同一套「改指 SDK / 造桩」判定。</summary>
    static void CollectControllerClasses(List<string> resolved, Dictionary<string, string> idx, Dictionary<string, string> kept)
    {
        foreach (var rel in resolved.Where(r => r.StartsWith("AnimatorController/", StringComparison.Ordinal)))
        {
            var f = Path.Combine(Paths.Export, rel.Replace('/', '\\'));
            if (!File.Exists(f)) continue;
            foreach (Match g in Regex.Matches(File.ReadAllText(f), @"m_Script: \{fileID: -?\d+, guid: ([0-9a-f]{32})"))
            {
                if (!idx.TryGetValue(g.Groups[1].Value, out var r2) || !r2.StartsWith("Scripts/", StringComparison.Ordinal)) continue;
                var c = Path.GetFileNameWithoutExtension(r2);
                if (Keep.Contains(c)) kept.TryAdd(c, g.Groups[1].Value);
                else Console.WriteLine($"  ⚠️ 控制器 {Path.GetFileName(rel)} 用到 0.16 接不住的类 {c}（会变丢失脚本）");
            }
        }
    }

    static void WriteAsmref()
    {
        foreach (var stale in new[] { "VisitAPI.asmdef", "VisitAPI.asmdef.meta" })   // 早先误建过 asmdef 就清掉
        {
            var p = Path.Combine(VisitStubDir, stale);
            if (File.Exists(p)) File.Delete(p);
        }
        var f = Path.Combine(VisitStubDir, "VisitAPI.asmref");
        File.WriteAllText(f, "{\n  \"reference\": \"VisitAPI\"\n}\n", new UTF8Encoding(false));
        File.WriteAllText(f + ".meta",
            $"fileFormatVersion: 2\nguid: {StableGuid("asmref.VisitAPI")}\nAssemblyDefinitionReferenceImporter:\n  externalObjects: {{}}\n  userData:\n  assetBundleName:\n  assetBundleVariant:\n",
            new UTF8Encoding(false));
    }

    // ── 写空壳桩 ───────────────────────────────────────────
    // 关键：`.cs.meta` 里的 guid 必须和原导出**一模一样**，场景里的 m_Script 引用才接得上。
    // 目录里**不放 asmdef** —— 没有 asmdef 的脚本进默认程序集 Assembly-CSharp，
    // 正好和游戏里真类所在的程序集同名，运行时才认得出来。
    /// <summary>
    /// 桩的家：**全工程共用一个目录，不按场景分**。
    /// ⚠️ 按场景各放一份的话，两个场景都要的类会生成两个同名文件、都编进 `Assembly-CSharp` → CS0101。
    /// 一个类一个文件（guid 取原导出那份，跨场景本来就一致），重复抽取只是覆盖，天然幂等。
    /// </summary>
    static string StubDir => Path.Combine(Paths.Sdk, "Assets", "VisitAPI", "Vendors", "_Stubs");

    static void WriteStubs(string dir, IEnumerable<KeyValuePair<string, string>> kept, List<string> warn)
    {
        Directory.CreateDirectory(dir);
        foreach (var (cls, guid) in kept)
        {
            // 2026-09-05 起：已经存在的桩**一律不覆盖**——有几份是手改过的（坑 #105 的 ProFlareAtlas / _FlareSettings），
            // 自动生成会把手改冲掉。要重生成就先手动删掉那个文件。
            if (File.Exists(Path.Combine(dir, cls + ".cs"))) { Console.WriteLine($"  桩 {cls} 已存在，保留不覆盖"); continue; }
            var src = StubGen.Emit(cls, warn);
            if (src == null) continue;
            // 同名的依赖类型文件（另一场景先写的）要清掉，不然一个类两份定义
            foreach (var stale in new[] { Path.Combine(dir, "_" + cls + ".cs"), Path.Combine(dir, "_" + cls + ".cs.meta") })
                if (File.Exists(stale)) File.Delete(stale);
            // 桩源码带 BOM 写：Unity/Roslyn 读无 BOM 的 UTF-8 有时会按系统 ANSI 猜，注释里一个非 ASCII 字符就能编不过
            File.WriteAllText(Path.Combine(dir, cls + ".cs"), src, new UTF8Encoding(true));
            File.WriteAllText(Path.Combine(dir, cls + ".cs.meta"),
                $"fileFormatVersion: 2\nguid: {guid}\nMonoImporter:\n  externalObjects: {{}}\n  serializedVersion: 2\n  defaultReferences: []\n  executionOrder: 0\n  icon: {{instanceID: 0}}\n  userData:\n  assetBundleName:\n  assetBundleVariant:\n",
                new UTF8Encoding(false));
            var nf = Regex.Matches(src, @"\[SerializeField\]").Count;
            Console.WriteLine($"  桩 {StubGen.NamespaceOf(cls)}.{cls}  {nf} 个字段");
        }
        // 共用的 [Serializable] 依赖类型：一个类型一个文件，同样写进共用目录。
        // ⚠️ 已经作为**主类桩**生成过的要跳过 —— 否则同一个类被写成 `X.cs` 和 `_X.cs` 两份，CS0101
        //    （`AmplifyImpostorsArray` / `SeasonsMaterialsGroup` 就是这么和自己撞的）。
        //    桩目录是**跨场景共用**的，所以还要看盘上有没有 —— 另一个场景可能已经把它当主类写过了。
        var mainNames = new HashSet<string>(kept.Select(k => k.Key), StringComparer.Ordinal);
        foreach (var (name, src) in StubGen.SharedFiles())
        {
            if (mainNames.Contains(name) || File.Exists(Path.Combine(dir, name + ".cs"))) continue;
            var f = Path.Combine(dir, "_" + name + ".cs");
            if (File.Exists(f)) continue;   // 同上：已有的共用类型文件不覆盖（_FlareSettings 是手改过的）
            File.WriteAllText(f, src, new UTF8Encoding(true));
            // guid 由类名派生 —— 每次抽取都一样，重复抽取不会让 Unity 以为是新资产
            File.WriteAllText(f + ".meta",
                $"fileFormatVersion: 2\nguid: {StableGuid("shared." + name)}\nMonoImporter:\n  externalObjects: {{}}\n  serializedVersion: 2\n  defaultReferences: []\n  executionOrder: 0\n  icon: {{instanceID: 0}}\n  userData:\n  assetBundleName:\n  assetBundleVariant:\n",
                new UTF8Encoding(false));
        }
        if (StubGen.SharedCount > 0) Console.WriteLine($"  共用类型 {StubGen.SharedCount} 个（各一个文件）");
    }

    static string StableGuid(string key)
    {
        var h = System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexStringLower(h);
    }

    // ── 修环境光 ───────────────────────────────────────────
    /// <summary>
    /// 原场景 `m_AmbientMode: 0`（Skybox）—— 环境光本来由**烘焙光照数据**提供，
    /// 但 AssetRipper 导不出 `LightingDataAsset` 的真实内容（Prapor 那份只有 9.9 KB，是个空壳），
    /// 于是退化成「Unity 默认天空盒从四面八方均匀打光」：平板、零对比、发白（M4g 问题 3）。
    ///
    /// 场景自己带着正确的环境色（`m_AmbientSkyColor`，Prapor 是暗灰蓝 0.212/0.227/0.259），
    /// 改成 `3`（Flat）就用它，比默认天空盒接近原味得多。
    ///
    /// ⚠️ **这条猜测已作废，不再改场景**（2026-09-02）：
    /// M4j 实机证明改生效了（日志 `ambient=Flat`）但画面一模一样 —— 不是环境光模式的问题。
    /// 真因是 `AmbientLight` 组件每帧崩（见 `PatchSdkFields`），整套环境光/反射根本没启动。
    /// 而且 `Vendors_Scripts` 里的原生 `LevelSettings` 运行时**会自己设 RenderSettings**，
    /// 我们再按猜测改一道只会和它打架。按 §6.2.0「场景里有什么就用什么」——原样保留，只报个数。
    /// </summary>
    static void ReportAmbient(string scene)
    {
        var m = Regex.Match(scene, @"(?ms)^RenderSettings:.*?^\s*m_AmbientMode: (\d+)");
        Console.WriteLine(m.Success
            ? $"  环境光模式 m_AmbientMode={m.Groups[1].Value}（0=Skybox 1=Trilight 3=Flat）—— 原样保留，交给原生 LevelSettings"
            : "  场景里没有 RenderSettings 段");
    }

    // ── 抽进 SDK ───────────────────────────────────────────
    static void Extract(string sceneName)
    {
        var idx = LoadIndex();
        var scenePath = Paths.Scene(sceneName);
        if (!File.Exists(scenePath)) { Console.WriteLine("场景不存在: " + scenePath); return; }

        var scene = StripMonoBehaviours(File.ReadAllText(scenePath), idx, out var removed, out var kept);
        Console.WriteLine($"剥掉 {removed:N0} 个 MonoBehaviour，保留 {kept.Count} 个");
        ReportAmbient(scene);
        var missing = Keep.Where(k => !kept.ContainsKey(k)).ToList();
        if (missing.Count > 0) Console.WriteLine($"  （白名单里这些本场景没有，正常）：{string.Join(", ", missing)}");

        var outDir = Path.Combine(Paths.Sdk, "Assets", "VisitAPI", "Vendors", sceneName);
        if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        Directory.CreateDirectory(outDir);
        InstallULipSync();
        var existing = ExistingGuids(outDir);

        var resolved = Closure(scene, idx, out _).Distinct(StringComparer.Ordinal).ToList();
        CollectAssetClasses(resolved, idx, kept);
        CollectControllerClasses(resolved, idx, kept);
        Console.WriteLine($"  连资产用到的一起，共 {kept.Count} 个类要接");
        var guidMap = MapScripts(ref scene, kept);
        var (n, copied) = CopyAssets(resolved, idx, guidMap, outDir, existing);
        RewriteControllers(Directory.EnumerateFiles(outDir, "*.controller", SearchOption.AllDirectories).ToList(), idx);
        // 场景本体最后写（内容改过，.meta 照抄以保住 GUID）
        var sceneDst = Path.Combine(outDir, sceneName + ".unity");
        File.WriteAllText(sceneDst, scene, new UTF8Encoding(false));
        if (File.Exists(scenePath + ".meta")) File.Copy(scenePath + ".meta", sceneDst + ".meta", true);
        InstallCompatShaders(outDir);
        Console.WriteLine($"抽取完成：{n:N0} 个资产 / {Mb(copied)} → {outDir}");
    }

    /// 白名单里的类不只出现在场景组件上，也出现在 ScriptableObject 资产上（`SoundBank` 就只在资产里），
    /// 所以要先算完闭包、把资产用到的类也收进来，再统一决定「改指 SDK 还是造桩」。
    static void CollectAssetClasses(List<string> resolved, Dictionary<string, string> idx, Dictionary<string, string> kept)
    {
        foreach (var rel in resolved.Where(r => r.StartsWith("MonoBehaviour/", StringComparison.Ordinal)))
        {
            var f = Path.Combine(Paths.Export, rel.Replace('/', '\\'));
            if (!File.Exists(f)) continue;
            var g = Regex.Match(File.ReadAllText(f), @"m_Script: \{fileID: -?\d+, guid: ([0-9a-f]{32})");
            if (!g.Success || !idx.TryGetValue(g.Groups[1].Value, out var r2) || !r2.StartsWith("Scripts/", StringComparison.Ordinal)) continue;
            var c = Path.GetFileNameWithoutExtension(r2);
            if (Keep.Contains(c)) kept.TryAdd(c, g.Groups[1].Value);
        }
    }

    /// SDK 已有的 → 改指过去（**并核对字段，缺的补上**）；0.16 有的 → Assembly-CSharp 桩；重写的 → VisitAPI 程序集桩。
    /// 返回 类名 → 最终 guid（只记改过的），场景文本里的引用就地替换。
    static Dictionary<string, string> MapScripts(ref string scene, Dictionary<string, string> kept)
    {
        var warn = new List<string>();
        StubGen.BuildIndex(Path.Combine(Paths.Export, "Scripts", "Assembly-CSharp"));
        StubGen.SetExisting(SdkAllTypes());   // SDK 已有的依赖类型别再生成一份
        var sdk = SdkScripts();
        var guidMap = new Dictionary<string, string>(StringComparer.Ordinal);
        // 已有同 GUID 桩的类优先于「改指 SDK」：坑 #105——SDK 的同名 ProFlareAtlas 是另一个类（全局 MonoBehaviour），
        // 改指过去数据全丢；隔离工程里给它放了同 GUID 的真桩，就该原样引用它。
        bool HasStub(string cls) => File.Exists(Path.Combine(StubDir, cls + ".cs")) || File.Exists(Path.Combine(VisitStubDir, cls + ".cs"));
        var rest = kept.Where(k => !sdk.ContainsKey(k.Key) || HasStub(k.Key)).ToList();
        foreach (var (cls, oldGuid) in kept.Where(k => sdk.ContainsKey(k.Key) && !HasStub(k.Key)))
        {
            guidMap[cls] = sdk[cls].Guid;
            scene = scene.Replace(oldGuid, sdk[cls].Guid, StringComparison.Ordinal);
            Console.WriteLine($"  改指 SDK: {cls}  {oldGuid[..8]}… → {sdk[cls].Guid[..8]}…");
            PatchSdkFields(cls, sdk, warn);
        }
        var visitStub = rest.Where(k => Rewritten.Contains(k.Key)).ToList();
        WriteStubs(StubDir, rest.Where(k => !Rewritten.Contains(k.Key)), warn);
        if (visitStub.Count > 0)
        {
            // 换程序集：依赖类型要各自一份（asmdef 引不到 Assembly-CSharp），所以先清空再攒
            StubGen.ResetShared();
            StubGen.SetExisting(new HashSet<string>(StringComparer.Ordinal));
            Console.WriteLine($"  VisitAPI 程序集桩（插件里自己实现的 1.1 独有类）{visitStub.Count} 个：");
            WriteStubs(VisitStubDir, visitStub, warn);
            WriteAsmref();
        }
        foreach (var w in warn.Distinct()) Console.WriteLine($"  ⚠️ {w}");
        return guidMap;
    }

    /// 依赖闭包逐个拷进 SDK：排除规则见 Skip；ScriptableObject 资产认得脚本的才收，并把 m_Script 一起改指（和场景里同一个映射）
    static (int n, long copied) CopyAssets(List<string> resolved, Dictionary<string, string> idx, Dictionary<string, string> guidMap, string outDir, HashSet<string> existing)
    {
        long copied = 0;
        var n = 0;
        var skipped = new Dictionary<string, int>(StringComparer.Ordinal);
        void Bump(string key) => skipped[key] = skipped.TryGetValue(key, out var c) ? c + 1 : 1;
        var relGuid = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in idx) relGuid.TryAdd(kv.Value, kv.Key);
        foreach (var rel in resolved)
        {
            if (Skip(rel)) { Bump(rel.Split('/')[0] + "/"); continue; }
            if (relGuid.TryGetValue(rel, out var g0) && existing.Contains(g0)) { Bump("(工程里已有)"); continue; }
            var src = Path.Combine(Paths.Export, rel.Replace('/', '\\'));
            if (!File.Exists(src)) continue;
            string rewritten = null;
            if (rel.StartsWith("MonoBehaviour/", StringComparison.Ordinal))
            {
                var text = File.ReadAllText(src);
                var g = Regex.Match(text, @"m_Script: \{fileID: -?\d+, guid: ([0-9a-f]{32})");
                var scriptRel = g.Success && idx.TryGetValue(g.Groups[1].Value, out var r) ? r : null;
                var cls = scriptRel != null && scriptRel.StartsWith("Scripts/", StringComparison.Ordinal) ? Path.GetFileNameWithoutExtension(scriptRel) : null;
                if (scriptRel != ULipPlugin && (cls == null || !Keep.Contains(cls))) { Bump("MonoBehaviour/"); Bump("  资产脚本剥掉: " + (cls ?? scriptRel ?? "?")); continue; }   // 按类分项列出，别再把整批台词静默剥掉（Fence 坑）
                if (cls != null && guidMap.TryGetValue(cls, out var newGuid)) text = text.Replace(g.Groups[1].Value, newGuid, StringComparison.Ordinal);
                rewritten = text;
            }
            var dst = Path.Combine(outDir, rel.Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            if (rewritten != null) File.WriteAllText(dst, rewritten, new UTF8Encoding(false));
            else File.Copy(src, dst, true);
            copied += new FileInfo(src).Length;
            n++;
            if (File.Exists(src + ".meta")) { File.Copy(src + ".meta", dst + ".meta", true); copied += new FileInfo(src + ".meta").Length; }
        }
        foreach (var kv in skipped.OrderByDescending(k => k.Value)) Console.WriteLine($"  跳过 {kv.Key,-18} {kv.Value,5:N0} 个");
        return (n, copied);
    }

    static string Mb(long b) => b >= 1L << 30 ? $"{b / 1024.0 / 1024 / 1024:F2} GB" : $"{b / 1024.0 / 1024:F1} MB";
}
