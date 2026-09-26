using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// 从 AssetRipper 导出的反编译源码自动生成「同 GUID 空壳桩」。
///
/// 为什么必须自动生成：桩要逐字段照抄真类**连基类字段一起**（DEV_NOTES #69），
/// 漏一个字段，那个字段的数据在打包时就丢了 —— 而且不报错。
/// 需要的类有二十来个、基类链最深 3 层、还有泛型基类和嵌套 [Serializable] 类，手写必漏。
///
/// 桩本身一行逻辑都没有，它只是个「数据形状」：Unity 靠它认得字段、把数据打进 bundle；
/// 运行时游戏里有同名同程序集（Assembly-CSharp）的真类，数据直接绑上去。
/// </summary>
static class StubGen
{
    public sealed class TypeDef
    {
        public string Ns = "", Name = "", BaseRaw = "", TypeParam = "";
        public bool IsEnum;
        public List<(string Type, string Name)> Fields = new();
    }

    // Unity 自己认得的类型，原样保留
    static readonly HashSet<string> Keep = new(StringComparer.Ordinal)
    {
        "int","float","bool","string","byte","sbyte","short","ushort","uint","long","ulong","double","char",
        "Vector2","Vector3","Vector4","Vector2Int","Vector3Int","Quaternion","Color","Color32","Rect","RectInt",
        "Bounds","BoundsInt","Matrix4x4","LayerMask","AnimationCurve","Gradient","RectOffset","Hash128",
        "GameObject","Transform","RectTransform","Component","Behaviour","MonoBehaviour","ScriptableObject",
        "Material","PhysicMaterial","Texture","Texture2D","Texture3D","Cubemap","Sprite","RenderTexture","Shader",
        "Mesh","AudioClip","AudioSource","AudioMixerGroup","AnimationClip","Animator","RuntimeAnimatorController",
        "Avatar","Animation","Light","Camera","Renderer","MeshRenderer","SkinnedMeshRenderer","MeshFilter",
        "Collider","BoxCollider","SphereCollider","CapsuleCollider","MeshCollider","Rigidbody","ParticleSystem",
        "Canvas","CanvasGroup","LineRenderer","TrailRenderer","ReflectionProbe","LightProbeGroup","Terrain",
        "AnimatorControllerParameterType","TextAsset","Font","Object","UnityEvent",
        "Texture2DArray","ComputeShader",
    };

    static readonly Dictionary<string, string> External = new(StringComparer.Ordinal)
    {
        ["BakedData"] = "uLipSync.BakedData",
        ["uLipSyncBakedDataPlayer"] = "MonoBehaviour",
        ["uLipSyncBlendShape"] = "MonoBehaviour",
    };

    // UnityEngine 自己的枚举：不在 1.1 导出的脚本索引里（那儿只有 Assembly-CSharp），
    // 但枚举一律按 SInt32 序列化，写 int 逐字节一致 —— 不这么映射就会整个字段跳过、数据静默丢失。
    static readonly HashSet<string> ExternEnums = new(StringComparer.Ordinal) { "AmbientMode", "FogMode" };

    // ⚠️ 必须连 `private` 一起认：编译器给 async 方法生成的 `private struct _003CPlay_003Ed__9`
    //    也得挖空，否则它里面的字段会漏成外层类的字段（SequenceReader 就被塞了个假的 animationData）。
    static readonly Regex TypeDecl = new(
        @"(?m)^\s*(?:public|internal|protected|private)\s+(?:sealed\s+|abstract\s+|static\s+|partial\s+|readonly\s+)*(class|enum|struct)\s+([A-Za-z_]\w*)(?:<([^>]+)>)?\s*(?::\s*([^\r\n{]+?))?\s*$",
        RegexOptions.Compiled);

    // AssetRipper 的字段格式很规整：属性各占一行，然后是声明行
    static readonly Regex FieldDecl = new(
        @"(?m)^[ \t]*(public|private|protected|internal)\s+((?:readonly\s+|static\s+|const\s+|volatile\s+)*)([^\r\n;=]+?)\s+([A-Za-z_]\w*)\s*;",
        RegexOptions.Compiled);

    static Dictionary<string, TypeDef> _types;   // 简单类名 → 定义

    /// <summary>
    /// 被多个桩共用的 [Serializable] 依赖类型（如 AnimationElement、SerializableKeyValuePair）。
    /// ⚠️ 必须**全局只生成一份**：三本字典的桩都依赖它们，各写一份就是同命名空间重复定义（CS0101），编不过。
    /// </summary>
    static readonly Dictionary<string, (string Ns, string Src)> _shared = new(StringComparer.Ordinal);

    /// <summary>SDK 里已经有的类型（全名或简单名）—— 这些依赖类型不能再生成一遍，否则 CS0101。</summary>
    static HashSet<string> _existing = new(StringComparer.Ordinal);
    public static void SetExisting(HashSet<string> t) => _existing = t;

    // ── 建索引 ─────────────────────────────────────────────
    public static void BuildIndex(string scriptsRoot)
    {
        if (_types != null) return;
        _types = new Dictionary<string, TypeDef>(StringComparer.Ordinal);
        var n = 0;
        foreach (var cs in Directory.EnumerateFiles(scriptsRoot, "*.cs", SearchOption.AllDirectories))
        {
            string text;
            try { text = File.ReadAllText(cs); } catch { continue; }
            n += IndexFile(text);
        }
        Console.WriteLine($"脚本索引：{n:N0} 个类型（来自 {scriptsRoot}）");
    }

    /// 一个源文件里的全部类型进索引。先把每个类型的花括号范围找出来——
    /// ⚠️ 不能用「到下一个类型声明为止」切正文：嵌套类会把外层截断
    ///    （`AnimatorResetter` 的 3 个字段就写在两个嵌套 [Serializable] 类之后，那样切出来是 0 个字段）。
    /// 一个类型「自己的」成员 = 它的范围减去所有被它包住的嵌套类型范围。返回新收进索引的类型数。
    static int IndexFile(string text)
    {
        var ns = Regex.Match(text, @"(?m)^namespace\s+([\w.]+)").Groups[1].Value;
        var spans = new List<(int Open, int Close, TypeDef Td)>();
        foreach (Match m in TypeDecl.Matches(text))
        {
            // 编译器生成的类型也要建 span（好让它被挖空），只是不进索引
            var open = text.IndexOf('{', m.Index + m.Length);
            var close = open < 0 ? -1 : CloseBrace(text, open);
            if (close < 0) continue;
            spans.Add((open, close, new TypeDef
            {
                Ns = ns, Name = m.Groups[2].Value,
                TypeParam = m.Groups[3].Value.Split(' ')[0].Trim(),
                BaseRaw = m.Groups[4].Value.Trim(),
                IsEnum = m.Groups[1].Value == "enum",
            }));
        }
        var n = 0;
        foreach (var (open, close, td) in spans)
        {
            if (!td.IsEnum)
            {
                var sb = new StringBuilder(text.Substring(open, close - open));
                foreach (var (o2, c2, _) in spans)
                    if (o2 > open && c2 <= close)
                        for (var i = o2 - open; i < c2 - open && i < sb.Length; i++) sb[i] = ' ';
                td.Fields = Serialized(sb.ToString());
            }
            if (!td.Name.StartsWith("_003C", StringComparison.Ordinal) && _types.TryAdd(td.Name, td)) n++;
        }
        return n;
    }

    /// <summary>从 open 处的 `{` 找到配对的 `}`（跳过字符串与字符字面量）。</summary>
    static int CloseBrace(string s, int open)
    {
        var depth = 0;
        for (var i = open; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '"' || c == '\'')
            {
                var q = c;
                for (i++; i < s.Length && s[i] != q; i++) if (s[i] == '\\') i++;
                continue;
            }
            if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>挑出 Unity 真会序列化的字段：public 的、或标了 [SerializeField] 的；static/const/readonly 一律不算。</summary>
    static List<(string, string)> Serialized(string body)
    {
        var list = new List<(string, string)>();
        foreach (Match f in FieldDecl.Matches(body))
        {
            var access = f.Groups[1].Value;
            var mods = f.Groups[2].Value;
            var type = f.Groups[3].Value.Trim();
            var name = f.Groups[4].Value;
            if (mods.Length > 0 || name.StartsWith("_003C", StringComparison.Ordinal)) continue;
            if (access != "public" && !HasAttr(body, f.Index, "SerializeField")) continue;
            list.Add((type, name));
        }
        return list;
    }

    /// <summary>
    /// 这个字段头上**紧挨着**的属性块里有没有 attr。
    /// ⚠️ 不能用「往前扒 200 字符」：那会扒到上一个字段的 [SerializeField]，
    ///    于是没标注的私有字段被误判成序列化字段（NPCObject 就被塞了个本不该有的 _reader）。
    ///    只认从字段声明往上、连续的属性行 / 空行，碰到别的立刻停。
    /// </summary>
    static bool HasAttr(string body, int at, string attr)
    {
        var i = body.LastIndexOf('\n', Math.Max(0, at - 1));
        while (i > 0)
        {
            var j = body.LastIndexOf('\n', i - 1);
            var line = body.Substring(j + 1, i - j - 1).Trim();
            if (line.Length == 0) { i = j; continue; }
            if (!line.StartsWith('[')) return false;
            if (line.Contains(attr, StringComparison.Ordinal)) return true;
            i = j;
        }
        return false;
    }

    // ── 类型映射 ───────────────────────────────────────────
    static string Simple(string t)
    {
        t = t.Trim();
        var lt = t.IndexOf('<');
        var head = lt < 0 ? t : t[..lt];
        var dot = head.LastIndexOf('.');
        return (dot < 0 ? head : head[(dot + 1)..]) + (lt < 0 ? "" : t[lt..]);
    }

    /// <summary>返回可用的 C# 类型名；需要额外生成的 [Serializable] 依赖塞进 deps。</summary>
    static string Map(string raw, HashSet<string> deps, List<string> warn, string keepParam)
    {
        var t = Simple(raw);
        if (External.TryGetValue(t, out var external)) return external;
        if (t.EndsWith("[]", StringComparison.Ordinal))
        {
            var inner = Map(t[..^2], deps, warn, keepParam);
            return inner == null ? null : inner + "[]";        // 内层解析不了，整个字段就得跳过（不能吐出 List<>）
        }
        var g = Regex.Match(t, @"^(?:List|IReadOnlyList|IList)<(.+)>$");
        if (g.Success)
        {
            var inner = Map(g.Groups[1].Value, deps, warn, keepParam);
            return inner == null ? null : $"List<{inner}>";
        }
        if (t.StartsWith("UnityEvent", StringComparison.Ordinal)) return "UnityEvent";   // 泛型参数够不着，退成非泛型（序列化结构一致）
        if (keepParam.Length > 0 && t == keepParam) return t;                            // 正在生成泛型类本身，类型参数原样留着
        if (Keep.Contains(t)) return t;
        if (ExternEnums.Contains(t)) return "int";

        // 泛型的 [Serializable] 类（如 SerializableKeyValuePair<AnimationElement>）：
        // Unity 2020+ 支持序列化泛型类，**照原样保留泛型**，类名和真类一致，最保真。
        var gen = Regex.Match(t, @"^([A-Za-z_]\w*)<(.+)>$");
        if (gen.Success && _types.TryGetValue(gen.Groups[1].Value, out var gd) && !gd.IsEnum && !IsUnityObject(gd))
        {
            var arg = Map(gen.Groups[2].Value, deps, warn, keepParam);
            if (arg == null) { warn.Add(raw); return null; }
            deps.Add(gen.Groups[1].Value);
            return $"{Qualify(gd)}<{arg}>";
        }

        if (!_types.TryGetValue(t, out var td)) { warn.Add(raw); return null; }
        if (td.IsEnum) return "int";                                                        // 枚举按 SInt32 序列化，写 int 逐字节一致
        if (IsUnityObject(td)) return "MonoBehaviour";                                      // 跨程序集引用一律 PPtr<MonoBehaviour>（#69 同款）
        deps.Add(t);                                                                        // 纯 [Serializable] 类：内联序列化，得一起生成
        return Qualify(td);
    }

    /// <summary>
    /// 依赖类型一律写**全限定名**。共用类型按各自命名空间归组进一个文件，
    /// 跨命名空间引用（`Audio.ConfiguredAudioPlayer` 用 `EFT.AnimationSequencePlayer` 的类）没有 using 就 CS0246。
    /// 全限定在同命名空间下也照样合法，最省事。
    /// </summary>
    static string Qualify(TypeDef td) => td.Ns.Length > 0 ? td.Ns + "." + td.Name : td.Name;

    /// <summary>
    /// 取基类声明里「第一个基类」那一段。
    /// ⚠️ 必须先砍掉泛型约束：`SerializedDictionary&lt;TValue&gt; : MonoBehaviour where TValue : class`
    ///    不砍的话基类名会变成 `MonoBehaviour where TValue : class`，
    ///    于是「这是不是 Unity 对象」判错 → 本该是 PPtr 的 MonoBehaviour 被当成内联类又生成一遍（CS0101）。
    /// </summary>
    static string BaseHead(string raw)
    {
        var w = raw.IndexOf(" where ", StringComparison.Ordinal);
        if (w >= 0) raw = raw[..w];
        return raw.Split(',')[0].Trim();
    }

    /// <summary>这个类型名是不是 Unity 自己就认得的（不需要我们额外生成任何东西）。</summary>
    public static bool IsBuiltin(string simpleName) => Keep.Contains(simpleName);

    /// <summary>基类名是不是 Unity 自己的根类（到这儿就不用再往上查字段了）。</summary>
    public static bool IsUnityBase(string name) =>
        name is "MonoBehaviour" or "ScriptableObject" or "Component" or "Behaviour" or "Object" or "";

    static bool IsUnityObject(TypeDef td)
    {
        for (var i = 0; i < 8 && td != null; i++)
        {
            var b = Simple(BaseHead(td.BaseRaw));
            var bh = b.IndexOf('<') < 0 ? b : b[..b.IndexOf('<')];
            if (bh is "MonoBehaviour" or "ScriptableObject" or "Component" or "Behaviour" or "Object") return true;
            if (bh.Length == 0 || !_types.TryGetValue(bh, out td)) return false;
        }
        return false;
    }

    /// <summary>
    /// 把基类链上的序列化字段收上来 —— Unity 是**基类字段在前**，顺序不能反。
    /// 基类是泛型时（`AnimationDictionary : SerializedDictionary&lt;AnimationElement&gt;`），
    /// 要把基类字段里的类型参数换成实参，否则会吐出 `List&lt;SerializableKeyValuePair&lt;TValue&gt;&gt;` 这种编不过的东西。
    /// </summary>
    static void Collect(TypeDef td, List<(string Type, string Name)> into)
    {
        var b = Simple(BaseHead(td.BaseRaw));
        var lt = b.IndexOf('<');
        var bh = lt < 0 ? b : b[..lt];
        var arg = lt < 0 ? "" : b[(lt + 1)..^1].Trim();
        if (bh.Length > 0 && !Keep.Contains(bh) && _types.TryGetValue(bh, out var bd) && !bd.IsEnum)
        {
            var baseFields = new List<(string Type, string Name)>();
            Collect(bd, baseFields);
            foreach (var (ty, nm) in baseFields)
                into.Add((arg.Length > 0 && bd.TypeParam.Length > 0
                    ? Regex.Replace(ty, @"\b" + Regex.Escape(bd.TypeParam) + @"\b", arg) : ty, nm));
        }
        foreach (var f in td.Fields) into.Add(f);
    }

    // ── 生成 ───────────────────────────────────────────────
    /// <summary>
    /// 把一个类的全部序列化字段（**含基类链**）映射成可直接写进 C# 的 `(类型, 字段名)`，
    /// 顺带把用到的 [Serializable] 依赖类型攒进 `_shared`。类型为 null = 解析不了，调用方自己决定怎么处理。
    /// </summary>
    public static List<(string Type, string Name, string Raw)> MappedFields(string cls, List<string> warn)
    {
        var list = new List<(string, string, string)>();
        if (!_types.TryGetValue(cls, out var td)) { warn.Add("类没找到: " + cls); return list; }

        var deps = new HashSet<string>(StringComparer.Ordinal);
        var flat = new List<(string Type, string Name)>();
        Collect(td, flat);
        foreach (var (type, name) in flat) list.Add((Map(type, deps, warn, ""), name, type));
        ProcessDeps(deps, warn);
        return list;
    }

    /// <summary>为一个类生成桩源码；返回 null 表示这个类不该造桩（找不到）。</summary>
    public static string Emit(string cls, List<string> warn)
    {
        if (!_types.TryGetValue(cls, out var td)) { warn.Add("类没找到: " + cls); return null; }

        var body = new StringBuilder();
        foreach (var (type, name, raw) in MappedFields(cls, warn))
            body.Append(type == null
                ? $"    // skipped (unresolved type): {raw} {name}\n"
                : $"    [SerializeField] private {type} {name};\n");

        var decl = $"public class {cls} : MonoBehaviour\n{{\n{body}}}\n";
        return Head + (td.Ns.Length > 0 ? $"namespace {td.Ns}\n{{\n{decl}}}\n" : decl);
    }

    /// <summary>依赖的纯 [Serializable] 类（可能再套一层）—— 攒进 _shared，最后统一生成一份。</summary>
    static void ProcessDeps(HashSet<string> deps, List<string> warn)
    {
        var queue = new Queue<string>(deps);
        while (queue.Count > 0)
        {
            var d = queue.Dequeue();
            if (_shared.ContainsKey(d) || !_types.TryGetValue(d, out var dt)) continue;
            // SDK 已经有这个类型了 —— 直接用它的，别再生成一份（CS0101）。
            // ⚠️ 只能按**全名**判：按简单名判太松，会把「SDK 里某个同名但在别的命名空间的类」误判成有，
            //    于是引用写了却不生成，变成 CS0246（`CullingSettings` 就是这么丢的）。
            if (_existing.Contains(Qualify(dt))) { _shared[d] = (dt.Ns, ""); continue; }
            _shared[d] = (dt.Ns, "");                                       // 先占位，防同名递归死循环
            var sub = new List<(string Type, string Name)>();
            Collect(dt, sub);
            var gp = dt.TypeParam.Length > 0 ? $"<{dt.TypeParam}>" : "";   // 泛型类原样保留泛型
            var sb = new StringBuilder($"[System.Serializable]\npublic class {d}{gp}\n{{\n");
            foreach (var (type, name) in sub)
            {
                var more = new HashSet<string>(StringComparer.Ordinal);
                var mapped = Map(type, more, warn, dt.TypeParam);
                if (mapped == null) { sb.Append($"    // skipped (unresolved type): {type} {name}\n"); continue; }
                sb.Append($"    public {mapped} {name};\n");
                foreach (var m in more) queue.Enqueue(m);
            }
            sb.Append("}\n");
            _shared[d] = (dt.Ns, sb.ToString());
        }
    }

    /// <summary>换一个目标程序集重新攒依赖类型前先清空 —— 两个程序集各要一份自己的（asmdef 引不到 Assembly-CSharp）。</summary>
    public static void ResetShared() => _shared.Clear();

    // ── 核对 SDK 脚本 ──────────────────────────────────────
    /// <summary>
    /// 解析**任意一个 .cs 文件**里 `cls` 类自己声明的序列化字段名（嵌套类型已挖掉），顺带给出基类名。
    /// 用途：核对「SDK 的脚本是不是比 1.1 的真类少字段」—— 少一个，那个字段的数据打包时就静默没了。
    /// </summary>
    /// <summary>
    /// ⚠️ 核对 SDK 脚本用的字段正则**必须允许初始值**：SDK 那份反编译产物里满是 `public float RenderDelay = 0.05f;`，
    /// 用 `FieldDecl`（不认 `=`）去扫会把它们全当成「没有」，于是把已存在的字段又补一遍 → CS0102。
    /// </summary>
    /// ⚠️ 还必须认**属性和声明写在同一行**：SDK 那份的风格是 `[SerializeField] private SkinnedMeshRenderer _x;`，
    /// 而 `FieldDecl` 要求行首就是访问修饰符 —— 于是 `Skin` 的 3 个字段被判成「没有」，补上去直接 CS0102。
    static readonly Regex FieldDeclInit = new(
        @"(?m)^[ \t]*(?:\[[^\]\r\n]*\]\s*)*(public|private|protected|internal)\s+((?:readonly\s+|static\s+|const\s+|volatile\s+)*)([^\r\n;=]+?)\s+([A-Za-z_]\w*)\s*(?:=[^;]*)?;",
        RegexOptions.Compiled);

    public static HashSet<string> FieldNamesIn(string file, string cls, out string baseName)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        baseName = "";
        string text;
        try { text = File.ReadAllText(file); } catch { return names; }

        var spans = new List<(int Open, int Close, string Name, string Base)>();
        foreach (Match m in TypeDecl.Matches(text))
        {
            var open = text.IndexOf('{', m.Index + m.Length);
            if (open < 0) continue;
            var close = CloseBrace(text, open);
            if (close > 0) spans.Add((open, close, m.Groups[2].Value, m.Groups[4].Value.Trim()));
        }
        foreach (var (open, close, name, bse) in spans)
        {
            if (name != cls) continue;
            baseName = Simple(BaseHead(bse));
            var sb = new StringBuilder(text.Substring(open, close - open));
            foreach (var (o2, c2, _, _) in spans)                       // 嵌套类型的字段不算这个类的
                if (o2 > open && c2 <= close)
                    for (var i = o2 - open; i < c2 - open && i < sb.Length; i++) sb[i] = ' ';
            // 这里收**所有**声明过的字段名（不分 public/private、不看 [SerializeField]）：
            // 判据是「补上去会不会撞名」，撞了就是 CS0102，比漏补一个字段严重得多。
            foreach (Match f in FieldDeclInit.Matches(sb.ToString())) names.Add(f.Groups[4].Value);
            break;
        }
        return names;
    }

    /// <summary>在 `cls` 类的收尾花括号前插进一段文本（给 SDK 脚本补字段用）。</summary>
    public static bool AppendToClass(string file, string cls, string text)
    {
        var src = File.ReadAllText(file);
        foreach (Match m in TypeDecl.Matches(src))
        {
            if (m.Groups[2].Value != cls) continue;
            var open = src.IndexOf('{', m.Index + m.Length);
            var close = open < 0 ? -1 : CloseBrace(src, open);
            if (close < 0) return false;
            File.WriteAllText(file, src.Insert(close, text), new UTF8Encoding(true));
            return true;
        }
        return false;
    }

    // AudioMixerGroup 在 UnityEngine.Audio，不 using 就 CS0246
    const string Head = "using System.Collections.Generic;\nusing UnityEngine;\nusing UnityEngine.Audio;\nusing UnityEngine.Events;\n\n";

    /// <summary>
    /// 共用的 [Serializable] 依赖类型 —— **一个类型一个文件**返回。
    /// ⚠️ 不能按场景打成一个 `_SharedTypes.cs`：多个场景各生成一份，两份都编进 `Assembly-CSharp` → CS0101。
    /// 一个类型一个文件、写进全工程共用目录，重复抽取只是覆盖同一个文件，天然幂等。
    /// </summary>
    public static IEnumerable<(string Name, string Src)> SharedFiles() =>
        _shared.Where(k => k.Value.Src.Length > 0)
               .Select(k => (k.Key, Head + (k.Value.Ns.Length > 0
                   ? $"namespace {k.Value.Ns}\n{{\n{k.Value.Src}}}\n"
                   : k.Value.Src)));

    public static int SharedCount => _shared.Count(k => k.Value.Src.Length > 0);

    public static string NamespaceOf(string cls) => _types.TryGetValue(cls, out var t) ? t.Ns : "";

    public static IEnumerable<string> AllClasses() => _types.Where(k => !k.Value.IsEnum).Select(k => k.Key);
}
