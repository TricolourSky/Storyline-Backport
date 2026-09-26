using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// 「哪些 1.1 的类 0.16 接得住、哪些必须重写」—— 离线给出确定答案，不靠实机试错。
///
/// 能这么干的前提：**SPT 4.1.3 是 Mono 不是 IL2CPP**，
/// `EscapeFromTarkov_Data\Managed\Assembly-CSharp.dll` 是真的托管程序集，类表可以直接读。
///
/// 对照物是**原生 1.1 导出**（SORA 2026-09-02 定：tarkin 不是权威）。
/// 输出两张表：① 0.16 里有的 → 造桩直接绑；② 0.16 里没有的 → 按核心方法论从 IL2CPP 重写进插件。
/// </summary>
static class Survey
{
    static HashSet<string> _game, _global;

    /// <summary>
    /// 游戏里**没有命名空间**的类型名。
    /// 用途：桩要是声明了个命名空间，而它正好和某个全局类同名，C# 直接 CS0101
    /// （实例：SDK 有全局类 `MultiFlare`，而 `FlareLight` 的命名空间恰好也叫 `MultiFlare`）。
    /// </summary>
    public static HashSet<string> GlobalTypes() => _global ??= Scan((ns, name) => ns.Length == 0 ? new[] { name } : Array.Empty<string>());

    /// <summary>0.16 游戏程序集里的类型全名表（抽取时用来判定「这个 1.1 的类接不接得住」）；简单名也记一份，方便只知道类名时查。</summary>
    public static HashSet<string> GameTypes() => _game ??= Scan((ns, name) => new[] { ns.Length > 0 ? ns + "." + name : name, name });

    /// <summary>扫一遍游戏程序集的类型表，每个类型交给 pick 决定记哪些名字。</summary>
    static HashSet<string> Scan(Func<string, string, IEnumerable<string>> pick)
    {
        var dll = Path.Combine(Paths.Spt, "EscapeFromTarkov_Data", "Managed", "Assembly-CSharp.dll");
        if (!File.Exists(dll)) throw new FileNotFoundException("找不到游戏程序集（判定要靠它）: " + dll);
        var set = new HashSet<string>(StringComparer.Ordinal);
        using var fs = File.OpenRead(dll);
        using var pe = new PEReader(fs);
        var md = pe.GetMetadataReader();
        foreach (var h in md.TypeDefinitions)
        {
            var t = md.GetTypeDefinition(h);
            set.UnionWith(pick(md.GetString(t.Namespace), md.GetString(t.Name)));
        }
        return set;
    }

    public static void Run(string sceneName)
    {
        var have = GameTypes();
        Console.WriteLine($"0.16 的 Assembly-CSharp：{have.Count / 2:N0} 个类型");
        StubGen.BuildIndex(Path.Combine(Paths.Export, "Scripts", "Assembly-CSharp"));
        // 原始（未剥离）的 1.1 场景 —— 权威对照物
        var orig = Path.Combine(Paths.Export, "Content", "Locations", "Venders", sceneName + ".unity");
        if (!File.Exists(orig)) { Console.WriteLine("找不到原场景: " + orig); return; }
        var (counts, dllBacked) = CountScripts(orig, Program.LoadIndexPublic());
        var ok = new List<(string Cls, string Ns, int N)>();
        var rewrite = new List<(string Cls, string Ns, int N)>();
        foreach (var (cls, n) in counts)
        {
            var ns = StubGen.NamespaceOf(cls);
            (have.Contains(ns.Length > 0 ? ns + "." + cls : cls) ? ok : rewrite).Add((cls, ns, n));
        }
        Print($"═══ {sceneName}：0.16 **接得住**的（造桩即可）{ok.Count} 个类 ═══", ok);
        Print($"═══ {sceneName}：0.16 **没有**、必须重写进插件的 {rewrite.Count} 个类 ═══", rewrite);
        Console.WriteLine();
        Console.WriteLine($"另有 {dllBacked} 个组件的脚本住在独立 DLL 里（AssetRipper 没反编译，如 uLipSync / Meta XR Audio）");
        var outFile = Path.Combine(Paths.Tool, "survey_" + sceneName + ".tsv");
        using var w = new StreamWriter(outFile, false, new UTF8Encoding(false));
        w.WriteLine("状态\t个数\t命名空间\t类名");
        foreach (var (cls, ns, n) in ok.OrderByDescending(x => x.N)) w.WriteLine($"接得住\t{n}\t{ns}\t{cls}");
        foreach (var (cls, ns, n) in rewrite.OrderByDescending(x => x.N)) w.WriteLine($"要重写\t{n}\t{ns}\t{cls}");
        Console.WriteLine("清单已写出：" + outFile);
    }

    /// 场景里每个 MonoBehaviour 的脚本类名 → 出现次数；脚本住在独立 DLL 里（索引里没有 Scripts/ 路径）的另计
    static (Dictionary<string, int> counts, int dllBacked) CountScripts(string scenePath, Dictionary<string, string> idx)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var dllBacked = 0;
        foreach (var d in Regex.Split(File.ReadAllText(scenePath), @"(?m)^--- !u!").Skip(1))
        {
            if (!d.StartsWith("114 ", StringComparison.Ordinal)) continue;
            var g = Regex.Match(d, @"m_Script: \{fileID: -?\d+, guid: ([0-9a-f]{32})");
            if (!g.Success) continue;
            if (!idx.TryGetValue(g.Groups[1].Value, out var rel) || !rel.StartsWith("Scripts/", StringComparison.Ordinal)) { dllBacked++; continue; }
            var cls = Path.GetFileNameWithoutExtension(rel);
            counts[cls] = counts.TryGetValue(cls, out var c) ? c + 1 : 1;
        }
        return (counts, dllBacked);
    }

    static void Print(string title, List<(string Cls, string Ns, int N)> rows)
    {
        Console.WriteLine();
        Console.WriteLine(title);
        foreach (var (cls, ns, n) in rows.OrderByDescending(x => x.N))
            Console.WriteLine($"  {n,4} ×  {(ns.Length > 0 ? ns + "." : "")}{cls}");
    }
}
