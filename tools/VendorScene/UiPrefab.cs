using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// 把 AssetRipper 导出的 1.1 UI 场景里的一棵子树抽成能进 SDK 工程的 prefab（章节屏那套 extract_chapter_ui.py 的 C# 版，本机没有 Python）。
///   VendorScene uiprefab &lt;导出 Assets 根&gt; &lt;场景.unity&gt; &lt;根物体名&gt; &lt;父物体名&gt; &lt;输出目录&gt; &lt;prefab 名&gt;
/// 引用改写规则（DEV_NOTES #69 同款）：
///   · UnityEngine.UI.dll / Unity.TextMeshPro.dll 的 dll 引用（fileID = 类全名的 MD4）→ Unity 包里的脚本 GUID（ugui_guids.txt + TMP 固定表）
///   · Assembly-CSharp 的类：0.16 有的 → 同 GUID 空壳桩（写进 Vendors\_Stubs，和房间共用）；没有的 → 整个组件剥掉（插件运行时自己驱动）
///   · TMP 字体 / 字体材质引用一律清空（运行时从游戏现成文字上抄）
///   · Sprite / Texture2D / AnimatorController / AnimationClip / Material 连 .meta 一起拷（.anim 里的组件引用同样改写）
/// 2026-09-05 首个用途：1.1 的商人加载屏 DialogueLoadingScreen（PreloaderUIScene 里，故障风背景 + 转圈计时环 + 商人名牌）。
/// </summary>
static class UiPrefab
{
    static readonly Regex RefRe = new(@"\{fileID: (-?\d+), guid: ([0-9a-f]{32}), type: (\d)\}", RegexOptions.Compiled);
    static readonly string[] UguiClasses =
    {
        "Image", "RawImage", "Button", "Toggle", "ToggleGroup", "ScrollRect", "Scrollbar", "Mask", "RectMask2D",
        "HorizontalLayoutGroup", "VerticalLayoutGroup", "GridLayoutGroup", "ContentSizeFitter", "LayoutElement", "AspectRatioFitter",
        "Outline", "Shadow", "Slider", "GraphicRaycaster", "CanvasScaler", "Dropdown", "InputField", "Text",
    };
    static readonly Dictionary<string, string> TmpGuids = new(StringComparer.Ordinal)
    {
        ["TextMeshProUGUI"] = "f4688fdb7df04437aeb418b961361dc5",
        ["TMP_SubMeshUI"] = "",      // 空 = 剥掉（章节屏运行时也是把 SubMesh 直接销毁）
        ["TMP_FontAsset"] = "",
        ["TMP_SpriteAsset"] = "",
    };

    public static void Run(string[] a)
    {
        if (a.Length < 7) { Console.WriteLine("用法: VendorScene uiprefab <导出Assets根> <场景.unity> <根物体名> <父物体名> <输出目录> <prefab名>"); return; }
        var export = Path.GetFullPath(a[1]);
        var scenePath = Path.GetFullPath(a[2]);
        var rootName = a[3];
        var parentName = a[4];
        var outDir = Path.GetFullPath(a[5]);
        var prefabName = a[6];

        var idx = MetaIndex(export);
        var ugui = LoadTable(Path.Combine(Paths.Tool, "ugui_guids.txt"));
        var dllFid = new Dictionary<(string Dll, int Fid), string>();
        string uiDll = null, tmpDll = null;
        foreach (var kv in idx)
        {
            if (kv.Value.EndsWith("/UnityEngine.UI.dll", StringComparison.Ordinal)) uiDll = kv.Key;
            if (kv.Value.EndsWith("/Unity.TextMeshPro.dll", StringComparison.Ordinal)) tmpDll = kv.Key;
        }
        if (uiDll != null) foreach (var c in UguiClasses) dllFid[(uiDll, DllFileId("UnityEngine.UI", c))] = "ugui:" + c;
        if (tmpDll != null) foreach (var c in TmpGuids.Keys) dllFid[(tmpDll, DllFileId("TMPro", c))] = "tmp:" + c;
        Console.WriteLine($"导出索引 {idx.Count:N0} 条；UI dll={(uiDll ?? "?")[..8]} TMP dll={(tmpDll ?? "?")[..8]}");

        var text = File.ReadAllText(scenePath);
        var docs = Subtree(text, rootName, parentName);
        Console.WriteLine($"子树 {docs.Count} 个对象（根 '{rootName}'，父 '{parentName}'）");

        // ── MonoBehaviour：按脚本决定留 / 剥 ──
        var remove = new HashSet<string>(StringComparer.Ordinal);
        var scriptRewrite = new Dictionary<string, string>(StringComparer.Ordinal);   // "guid|fid" → 新引用文本
        var keptGameClasses = new Dictionary<string, string>(StringComparer.Ordinal);  // 0.16 有的 Assembly-CSharp 类 → guid
        StubGen.BuildIndex(Path.Combine(export, "Scripts", "Assembly-CSharp"));
        foreach (var (id, cls, body) in docs)
        {
            if (cls != 114) continue;
            var m = Regex.Match(body, @"m_Script: \{fileID: (-?\d+), guid: ([0-9a-f]{32})");
            if (!m.Success) { remove.Add(id); continue; }
            var fid = int.Parse(m.Groups[1].Value);
            var guid = m.Groups[2].Value;
            var key = guid + "|" + fid;
            if (scriptRewrite.ContainsKey(key)) { if (scriptRewrite[key].Length == 0) remove.Add(id); continue; }
            idx.TryGetValue(guid, out var rel);
            string decision;
            if (rel != null && rel.EndsWith(".dll", StringComparison.Ordinal))
            {
                if (dllFid.TryGetValue((guid, fid), out var tag))
                {
                    var name = tag[(tag.IndexOf(':') + 1)..];
                    var newGuid = tag.StartsWith("ugui:") ? (ugui.TryGetValue(name, out var g) ? g : "") : TmpGuids[name];
                    decision = newGuid.Length > 0 ? $"{{fileID: 11500000, guid: {newGuid}, type: 3}}" : "";
                    Console.WriteLine(decision.Length > 0 ? $"  {name} → 包脚本 {newGuid[..8]}…" : $"  {name} → 剥掉");
                }
                else { decision = ""; Console.WriteLine($"  ⚠️ dll {Path.GetFileName(rel)} 里未知类 fileID={fid} → 剥掉"); }
            }
            else if (rel != null && rel.StartsWith("Scripts/Assembly-CSharp/", StringComparison.Ordinal))
            {
                var name = Path.GetFileNameWithoutExtension(rel);
                if (Program.KeepPublic.Contains(name)) { decision = m.Value[m.Value.IndexOf('{')..] + ", type: 3}"; keptGameClasses[name] = guid; Console.WriteLine($"  {name}（0.16 有）→ 造桩保留"); }
                else { decision = ""; Console.WriteLine($"  {name}（0.16 没有）→ 剥掉，插件自己驱动"); }
            }
            else { decision = ""; Console.WriteLine($"  ⚠️ 未知脚本 {guid[..8]}… ({rel ?? "不在索引"}) → 剥掉"); }
            scriptRewrite[key] = decision;
            if (decision.Length == 0) remove.Add(id);
        }
        if (keptGameClasses.Count > 0) Program.WriteStubsPublic(keptGameClasses);

        // ── 资产引用：拷贝 + 改写 ──
        var assets = new HashSet<string>(StringComparer.Ordinal);      // 相对路径
        var unknown = new Dictionary<string, int>(StringComparer.Ordinal);
        string Rewrite(string body) => RefRe.Replace(body, mm =>
        {
            var fid = mm.Groups[1].Value; var guid = mm.Groups[2].Value;
            if (scriptRewrite.TryGetValue(guid + "|" + fid, out var s)) return s.Length > 0 ? s : mm.Value;
            if (!idx.TryGetValue(guid, out var rel)) { Bump(unknown, guid); return mm.Value; }
            if (rel.StartsWith("Resources/ui/fonts/", StringComparison.Ordinal)) return "{fileID: 0}";
            if (rel.StartsWith("Sprite/") || rel.StartsWith("Texture2D/") || rel.StartsWith("AnimatorController/") || rel.StartsWith("AnimationClip/") || rel.StartsWith("Material/"))
            { assets.Add(rel); return mm.Value; }
            if (rel.EndsWith(".dll", StringComparison.Ordinal)) return mm.Value;   // 剥掉的组件里残留的引用，无所谓
            Bump(unknown, rel); return mm.Value;
        });

        var sb = new StringBuilder("%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n");
        var rootTr = docs.First(d => d.Cls == 224 || d.Cls == 4).Id;
        foreach (var (id, cls, body) in docs)
        {
            if (remove.Contains(id)) continue;
            var b = body;
            if (cls == 1) b = Regex.Replace(b, @"(?m)^  - component: \{fileID: (-?\d+)\}\r?\n", mm => remove.Contains(mm.Groups[1].Value) ? "" : mm.Value);
            if (id == rootTr) b = Regex.Replace(b, @"m_Father: \{fileID: -?\d+\}", "m_Father: {fileID: 0}");
            b = Regex.Replace(b, @"\{fileID: (-?\d+)\}", mm => remove.Contains(mm.Groups[1].Value) ? "{fileID: 0}" : mm.Value);
            sb.Append("--- !u!").Append(cls).Append(" &").Append(id).Append('\n').Append(Rewrite(b.TrimEnd('\r', '\n', ' '))).Append('\n');
        }
        var prefabDir = Path.Combine(outDir, "Prefabs");
        Directory.CreateDirectory(prefabDir);
        var prefabPath = Path.Combine(prefabDir, prefabName + ".prefab");
        File.WriteAllText(prefabPath, sb.ToString(), new UTF8Encoding(false));
        File.WriteAllText(prefabPath + ".meta", $"fileFormatVersion: 2\nguid: {StableGuid("visitapi.uiprefab." + prefabName)}\nPrefabImporter:\n  externalObjects: {{}}\n  userData:\n  assetBundleName:\n  assetBundleVariant:\n", new UTF8Encoding(false));

        // 资产闭包：Sprite → 贴图；controller → clip；.anim 里的组件引用也要改写
        var queue = new Queue<string>(assets);
        var copied = new HashSet<string>(StringComparer.Ordinal);
        while (queue.Count > 0)
        {
            var rel = queue.Dequeue();
            if (!copied.Add(rel)) continue;
            var src = Path.Combine(export, rel.Replace('/', '\\'));
            if (!File.Exists(src)) { Console.WriteLine($"  ⚠️ 资产不存在: {rel}"); continue; }
            var dst = Path.Combine(outDir, rel.Split('/')[0], Path.GetFileName(rel));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            var isText = rel.EndsWith(".asset") || rel.EndsWith(".controller") || rel.EndsWith(".anim") || rel.EndsWith(".mat");
            if (isText)
            {
                var t = File.ReadAllText(src);
                foreach (Match g in Regex.Matches(t, @"guid: ([0-9a-f]{32})"))
                    if (idx.TryGetValue(g.Groups[1].Value, out var r2) && (r2.StartsWith("Texture2D/") || r2.StartsWith("AnimationClip/") || r2.StartsWith("Sprite/") || r2.StartsWith("Material/")))
                        queue.Enqueue(r2);
                File.WriteAllText(dst, rel.EndsWith(".anim") ? Rewrite(t) : t, new UTF8Encoding(false));
            }
            else File.Copy(src, dst, true);
            File.Copy(src + ".meta", dst + ".meta", true);
        }
        Console.WriteLine($"prefab → {prefabPath}");
        Console.WriteLine($"资产 {copied.Count} 个 → {outDir}");
        foreach (var kv in unknown.OrderByDescending(k => k.Value)) Console.WriteLine($"  ⚠️ 没处理的引用 ×{kv.Value}: {kv.Key}");
    }

    /// 从场景 YAML 里按「物体名 + 父物体名」切子树：GameObject / 其组件 / 递归子物体，按层级顺序返回 (id, 类号, 正文)
    static List<(string Id, int Cls, string Body)> Subtree(string scene, string rootName, string parentName)
    {
        var objs = new Dictionary<string, (int Cls, string Body)>(StringComparer.Ordinal);
        foreach (var d in Regex.Split(scene, @"(?m)^--- !u!").Skip(1))
        {
            var m = Regex.Match(d, @"^(\d+) &(-?\d+)");
            if (m.Success) objs[m.Groups[2].Value] = (int.Parse(m.Groups[1].Value), d[(m.Length)..]);
        }
        var goName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, v) in objs) if (v.Cls == 1) goName[id] = Regex.Match(v.Body, @"m_Name: (.*)").Groups[1].Value.Trim();
        var trGo = new Dictionary<string, string>(StringComparer.Ordinal); var goTr = new Dictionary<string, string>(StringComparer.Ordinal);
        var father = new Dictionary<string, string>(StringComparer.Ordinal); var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (id, v) in objs)
        {
            if (v.Cls != 4 && v.Cls != 224) continue;
            var g = Regex.Match(v.Body, @"m_GameObject: \{fileID: (-?\d+)\}"); var f = Regex.Match(v.Body, @"m_Father: \{fileID: (-?\d+)\}");
            if (g.Success) { trGo[id] = g.Groups[1].Value; goTr[g.Groups[1].Value] = id; }
            if (f.Success) { father[id] = f.Groups[1].Value; if (!children.TryGetValue(f.Groups[1].Value, out var l)) children[f.Groups[1].Value] = l = new(); l.Add(id); }
        }
        var roots = goName.Where(kv => kv.Value == rootName && goTr.ContainsKey(kv.Key)
                                       && father.TryGetValue(goTr[kv.Key], out var ft) && trGo.TryGetValue(ft, out var pg) && goName.TryGetValue(pg, out var pn) && pn == parentName)
                           .Select(kv => kv.Key).ToList();
        if (roots.Count != 1) throw new InvalidOperationException($"根物体 '{rootName}'（父 '{parentName}'）找到 {roots.Count} 个");
        var order = new List<string>(); var stack = new Stack<string>(); stack.Push(goTr[roots[0]]);
        while (stack.Count > 0)
        {
            var t = stack.Pop(); order.Add(t);
            if (children.TryGetValue(t, out var ch)) for (var i = ch.Count - 1; i >= 0; i--) stack.Push(ch[i]);
        }
        var result = new List<(string, int, string)>();
        foreach (var t in order)
        {
            var go = trGo[t];
            result.Add((go, 1, objs[go].Body));
            foreach (Match c in Regex.Matches(objs[go].Body, @"component: \{fileID: (-?\d+)\}"))
                if (objs.TryGetValue(c.Groups[1].Value, out var comp)) result.Add((c.Groups[1].Value, comp.Cls, comp.Body));
        }
        return result;
    }

    static Dictionary<string, string> MetaIndex(string export)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var cache = Path.Combine(Paths.Tool, "uiindex_" + StableGuid(export)[..8] + ".tsv");
        if (File.Exists(cache))
        {
            foreach (var line in File.ReadLines(cache)) { var t = line.IndexOf('\t'); if (t > 0) map.TryAdd(line[..t], line[(t + 1)..]); }
            return map;
        }
        var buf = new byte[400];
        foreach (var meta in Directory.EnumerateFiles(export, "*.meta", SearchOption.AllDirectories))
        {
            int read;
            try { using var fs = File.OpenRead(meta); read = fs.Read(buf, 0, buf.Length); } catch { continue; }
            var m = Regex.Match(Encoding.ASCII.GetString(buf, 0, read), @"guid: ([0-9a-f]{32})");
            if (m.Success) map.TryAdd(m.Groups[1].Value, Path.GetRelativePath(export, meta[..^5]).Replace('\\', '/'));
        }
        using (var w = new StreamWriter(cache, false, new UTF8Encoding(false)))
            foreach (var kv in map) w.WriteLine(kv.Key + "\t" + kv.Value);
        return map;
    }

    static Dictionary<string, string> LoadTable(string file)
    {
        var t = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(file)) { Console.WriteLine("⚠️ 缺 " + file); return t; }
        foreach (var line in File.ReadLines(file)) { var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries); if (p.Length == 2) t[p[0]] = p[1]; }
        return t;
    }

    static void Bump(Dictionary<string, int> d, string k) => d[k] = d.TryGetValue(k, out var n) ? n + 1 : 1;
    static string StableGuid(string key) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(key)));

    /// Unity 给 dll 里的类算的 fileID：MD4("s\0\0\0" + 命名空间 + 类名) 的前 4 字节（小端 int）
    static int DllFileId(string ns, string cls) => BitConverter.ToInt32(Md4(Encoding.UTF8.GetBytes("s\0\0\0" + ns + cls)), 0);

    static byte[] Md4(byte[] input)
    {
        uint F(uint x, uint y, uint z) => (x & y) | (~x & z);
        uint G(uint x, uint y, uint z) => (x & y) | (x & z) | (y & z);
        uint H(uint x, uint y, uint z) => x ^ y ^ z;
        uint Rol(uint x, int n) => (x << n) | (x >> (32 - n));
        var ml = (ulong)input.Length * 8;
        var msg = new List<byte>(input) { 0x80 };
        while (msg.Count % 64 != 56) msg.Add(0);
        msg.AddRange(BitConverter.GetBytes(ml));
        uint A = 0x67452301, B = 0xefcdab89, C = 0x98badcfe, D = 0x10325476;
        var data = msg.ToArray();
        for (var i = 0; i < data.Length; i += 64)
        {
            var X = new uint[16];
            for (var j = 0; j < 16; j++) X[j] = BitConverter.ToUInt32(data, i + j * 4);
            uint a = A, b = B, c = C, d = D;
            int[] s1 = { 3, 7, 11, 19 }, s2 = { 3, 5, 9, 13 }, s3 = { 3, 9, 11, 15 };
            for (var r = 0; r < 16; r++) { var t = a + F(b, c, d) + X[r]; a = d; d = c; c = b; b = Rol(t, s1[r % 4]); }
            for (var r = 0; r < 16; r++) { var t = a + G(b, c, d) + X[(r % 4) * 4 + r / 4] + 0x5a827999; a = d; d = c; c = b; b = Rol(t, s2[r % 4]); }
            int[] order = { 0, 8, 4, 12, 2, 10, 6, 14, 1, 9, 5, 13, 3, 11, 7, 15 };
            for (var r = 0; r < 16; r++) { var t = a + H(b, c, d) + X[order[r]] + 0x6ed9eba1; a = d; d = c; c = b; b = Rol(t, s3[r % 4]); }
            A += a; B += b; C += c; D += d;
        }
        var outp = new byte[16];
        BitConverter.GetBytes(A).CopyTo(outp, 0); BitConverter.GetBytes(B).CopyTo(outp, 4);
        BitConverter.GetBytes(C).CopyTo(outp, 8); BitConverter.GetBytes(D).CopyTo(outp, 12);
        return outp;
    }
}
