using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

// ZoneExtract —— 从 Unity level 文件里按名字找 GameObject，沿 Transform 链算世界矩阵，取 BoxCollider 中心/尺寸，打成 db\zones 格式。
//   ZoneExtract <classdata.tpk> <levelN> <名字正则> <地图名,地图名…> [source 备注]
// 输出到 stdout：一个 JSON 数组（和 db\zones\*.json 同格式），另外在 stderr 打印每个对象挂的脚本类名（核对是不是游戏自带的 ExperienceTrigger）。
//   ZoneExtract near <classdata.tpk> <levelN> <x> <y> <z> <半径>      列出该 level 里世界坐标离给定点不超过半径的 GameObject（核 0.16 场景在这个位置有没有东西）
//   ZoneExtract dump <classdata.tpk> <levelN> <字符串>                 找出正文里含这个字符串的资源，倒出它的类型、所属 GameObject、脚本名和全部字段（含 PPtr 指向的外部文件）
//   ZoneExtract tree <classdata.tpk> <xxx.bundle> [根名字正则]   打印 AssetBundle 里 GameObject 的层级树（名字 + 组件类型/脚本名 + 是否默认激活）
//   ZoneExtract uitree <classdata.tpk> <xxx.assets> <GameObject 名字正则> [Managed 目录 | raw] [深度]
//     打印 .assets 里名字匹配的 GameObject 子树（含默认关着的）：激活、RectTransform、各组件；给了 Managed 目录（0.16）就连 MonoBehaviour 的字段一起读（颜色、字号、边距、布局…）；
//     1.1 是 IL2CPP 读不出字段，给 raw 就把每个 MonoBehaviour 正文里的 PPtr（解析成资源名）和颜色按偏移列出来，照 0.16 的字段顺序认
//     09-25 为对 0.16 聊天附件条写：运行时导出只看得到显示中的节点，「收取」按钮、倒计时这些平时关着的看不到
//   ZoneExtract sprite <classdata.tpk> <xxx.assets> <Sprite 名字正则> <输出目录>
//     在这个文件和它依赖的文件里找名字匹配的 Sprite，按图集（SpriteAtlas）或 m_RD 里的纹理 + 矩形解码裁出来存 PNG（09-26 为取 1.1 的任务图标写）
if (args.Length > 0 && args[0].Equals("sprite", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length < 5) { Console.Error.WriteLine("usage: ZoneExtract sprite <classdata.tpk> <file.assets> <name regex> <out dir>"); return 1; }
    return SpriteExport(args[1], args[2], args[3], args[4]);
}
if (args.Length > 0 && args[0].Equals("uitree", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length < 4) { Console.Error.WriteLine("usage: ZoneExtract uitree <classdata.tpk> <file.assets> <name regex> [managed dir] [depth]"); return 1; }
    return UiTree(args[1], args[2], args[3], args.Length > 4 ? args[4] : null, args.Length > 5 ? int.Parse(args[5]) : 12);
}
if (args.Length > 0 && args[0].Equals("tree", StringComparison.OrdinalIgnoreCase))
{
    var bm = new AssetsManager();
    bm.LoadClassPackage(args[1]);
    var bun = bm.LoadBundleFile(args[2], true);
    var rootRx = args.Length > 3 ? new Regex(args[3], RegexOptions.IgnoreCase) : null;
    for (var i = 0; i < bun.file.BlockAndDirInfo.DirectoryInfos.Length; i++)
    {
        if ((bun.file.BlockAndDirInfo.DirectoryInfos[i].Flags & 4) == 0) continue;
        var af = bm.LoadAssetsFileFromBundle(bun, i, false);
        bm.LoadClassDatabaseFromPackage(af.file.Metadata.UnityVersion);
        var children = new Dictionary<long, List<long>>();   // transform pathId → child transform pathIds
        var goOfTr = new Dictionary<long, long>();
        var roots = new List<long>();
        foreach (var info in af.file.GetAssetsOfType(AssetClassID.Transform).Concat(af.file.GetAssetsOfType(AssetClassID.RectTransform)))
        {
            var tr = bm.GetBaseField(af, info);
            goOfTr[info.PathId] = tr["m_GameObject"]["m_PathID"].AsLong;
            var father = tr["m_Father"]["m_PathID"].AsLong;
            if (father == 0) roots.Add(info.PathId);
            else { if (!children.TryGetValue(father, out var l)) children[father] = l = new List<long>(); l.Add(info.PathId); }
        }
        string Desc(long goId)
        {
            var gi = af.file.GetAssetInfo(goId); if (gi == null) return "?";
            var go = bm.GetBaseField(af, gi);
            var comps = new List<string>();
            foreach (var comp in go["m_Component"]["Array"].Children)
            {
                var ptr = comp["component"]; var ci = af.file.GetAssetInfo(ptr["m_PathID"].AsLong); if (ci == null) continue;
                var t = (AssetClassID)ci.TypeId;
                if (t == AssetClassID.Transform || t == AssetClassID.RectTransform) continue;
                if (t == AssetClassID.MonoBehaviour)
                {
                    try
                    {
                        var mb = bm.GetBaseField(af, ci); var ms = mb["m_Script"];
                        var msi = ms["m_FileID"].AsInt == 0 ? af.file.GetAssetInfo(ms["m_PathID"].AsLong) : null;
                        if (msi != null) { var sb = bm.GetBaseField(af, msi); comps.Add(sb["m_ClassName"].AsString); continue; }
                        comps.Add($"Mono(ext {ms["m_FileID"].AsInt}:{ms["m_PathID"].AsLong})");
                    }
                    catch { comps.Add("Mono?"); }
                }
                else comps.Add(t.ToString());
            }
            return $"{go["m_Name"].AsString}{(go["m_IsActive"].AsBool ? "" : " [inactive]")}  <{string.Join(", ", comps)}>";
        }
        void Print(long trId, int depth)
        {
            if (!goOfTr.TryGetValue(trId, out var goId)) return;
            Console.WriteLine(new string(' ', depth * 2) + Desc(goId));
            if (children.TryGetValue(trId, out var kids)) foreach (var k in kids) Print(k, depth + 1);
        }
        foreach (var r in roots)
        {
            var name = goOfTr.TryGetValue(r, out var g) && af.file.GetAssetInfo(g) is AssetFileInfo gi ? bm.GetBaseField(af, gi)["m_Name"].AsString : "";
            if (rootRx != null && !rootRx.IsMatch(name)) continue;
            Print(r, 0);
        }
    }
    return 0;
}
//   ZoneExtract bundle <classdata.tpk> <xxx.bundle>   列出 AssetBundle 里的资源（类型 + 名字）
if (args.Length > 0 && args[0].Equals("bundle", StringComparison.OrdinalIgnoreCase))
{
    var bm = new AssetsManager();
    bm.LoadClassPackage(args[1]);
    var bun = bm.LoadBundleFile(args[2], true);
    for (var i = 0; i < bun.file.BlockAndDirInfo.DirectoryInfos.Length; i++)
    {
        var dirInfo = bun.file.BlockAndDirInfo.DirectoryInfos[i];
        Console.WriteLine($"# 包内文件 {dirInfo.Name}");
        if ((dirInfo.Flags & 4) == 0) continue;
        var af = bm.LoadAssetsFileFromBundle(bun, i, false);
        bm.LoadClassDatabaseFromPackage(af.file.Metadata.UnityVersion);
        foreach (var info in af.file.AssetInfos)
        {
            string nm = "";
            try { var b = bm.GetBaseField(af, info); var n = b["m_Name"]; nm = n.IsDummy ? "" : n.AsString; } catch { }
            Console.WriteLine($"  {(AssetClassID)info.TypeId,-16} {nm}");
        }
    }
    return 0;
}
if (args.Length > 0 && args[0].Equals("audio", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length < 3) { Console.Error.WriteLine("usage: ZoneExtract audio <classdata.tpk> <file.assets> [regex] [outDir]"); return 1; }
    return Audio(args[1], args[2], args.Length > 3 ? args[3] : null, args.Length > 4 ? args[4] : null);
}
if (args.Length > 0 && args[0].Equals("dump", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length < 4) { Console.Error.WriteLine("usage: ZoneExtract dump <classdata.tpk> <level file> <needle>"); return 1; }
    return Dump(args[1], args[2], args[3]);
}
if (args.Length > 0 && args[0].Equals("near", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length < 7) { Console.Error.WriteLine("usage: ZoneExtract near <classdata.tpk> <level file> <x> <y> <z> <radius>"); return 1; }
    return Near(args[1], args[2], float.Parse(args[3]), float.Parse(args[4]), float.Parse(args[5]), float.Parse(args[6]));
}
if (args.Length < 4) { Console.Error.WriteLine("usage: ZoneExtract <classdata.tpk> <level file> <regex> <locations,comma> [source]"); return 1; }
var mgr = new AssetsManager();
mgr.LoadClassPackage(args[0]);
var main = mgr.LoadAssetsFile(args[1], false);
mgr.LoadClassDatabaseFromPackage(main.file.Metadata.UnityVersion);
var rx = new Regex(args[2], RegexOptions.IgnoreCase);
var locations = args[3].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var source = args.Length > 4 ? args[4] : $"1.1 客户端 {Path.GetFileName(args[1])}";
var dir = Path.GetDirectoryName(Path.GetFullPath(args[1]))!;
var files = new Dictionary<string, AssetsFileInstance?>(StringComparer.OrdinalIgnoreCase) { [Path.GetFileName(args[1])] = main };
Console.Error.WriteLine($"# {Path.GetFileName(args[1])} Unity={main.file.Metadata.UnityVersion} assets={main.file.AssetInfos.Count}");

AssetsFileInstance? Resolve(AssetsFileInstance owner, int fileId)
{
    if (fileId == 0) return owner;
    if (fileId < 1 || fileId > owner.file.Metadata.Externals.Count) return null;
    var name = Path.GetFileName(owner.file.Metadata.Externals[fileId - 1].PathName.Replace('/', Path.DirectorySeparatorChar));
    if (files.TryGetValue(name, out var cached)) return cached;
    var full = Path.Combine(dir, name);
    AssetsFileInstance? inst = null;
    if (File.Exists(full)) try { inst = mgr.LoadAssetsFile(full, false); mgr.LoadClassDatabaseFromPackage(inst.file.Metadata.UnityVersion); } catch { inst = null; }
    files[name] = inst;
    return inst;
}

AssetTypeValueField? Deref(AssetsFileInstance owner, AssetTypeValueField pptr)
{
    var f = Resolve(owner, pptr["m_FileID"].AsInt);
    var id = pptr["m_PathID"].AsLong;
    if (f == null || id == 0) return null;
    var info = f.file.GetAssetInfo(id);
    return info == null ? null : mgr.GetBaseField(f, info);
}

static Vector3 V3(AssetTypeValueField f) => new(f["x"].AsFloat, f["y"].AsFloat, f["z"].AsFloat);
static Quaternion Q(AssetTypeValueField f) => new(f["x"].AsFloat, f["y"].AsFloat, f["z"].AsFloat, f["w"].AsFloat);

// Transform 链 → (世界位置, 世界旋转, 世界缩放)；Unity 的 lossyScale 在有旋转的父链下只是近似，任务区域的父节点都不转，够用
(Vector3 pos, Quaternion rot, Vector3 scale) World(AssetTypeValueField tr, int depth = 0)
{
    var lp = V3(tr["m_LocalPosition"]); var lr = Q(tr["m_LocalRotation"]); var ls = V3(tr["m_LocalScale"]);
    var father = tr["m_Father"];
    if (depth > 64 || father["m_PathID"].AsLong == 0) return (lp, lr, ls);
    var parent = Deref(main, father);
    if (parent == null) return (lp, lr, ls);
    var (pp, pr, ps) = World(parent, depth + 1);
    var pos = pp + Vector3.Transform(lp * ps, pr);
    return (pos, Quaternion.Normalize(pr * lr), ps * ls);
}

var zones = new List<object>();
foreach (var info in main.file.GetAssetsOfType(AssetClassID.GameObject))
{
    var go = mgr.GetBaseField(main, info);
    var name = go["m_Name"].AsString;
    if (!rx.IsMatch(name)) continue;
    AssetTypeValueField? transform = null, box = null;
    var scripts = new List<string>();
    foreach (var comp in go["m_Component"]["Array"].Children)
    {
        var ptr = comp["component"];
        var f = Resolve(main, ptr["m_FileID"].AsInt);
        var cinfo = f?.file.GetAssetInfo(ptr["m_PathID"].AsLong);
        if (f == null || cinfo == null) continue;
        var cf = mgr.GetBaseField(f, cinfo);
        switch ((AssetClassID)cinfo.TypeId)
        {
            case AssetClassID.Transform: case AssetClassID.RectTransform: transform = cf; break;
            case AssetClassID.BoxCollider: box = cf; break;
            case AssetClassID.MonoBehaviour:
                var ms = Deref(f, cf["m_Script"]);
                scripts.Add(ms == null ? "?" : $"{ms["m_Namespace"].AsString}.{ms["m_ClassName"].AsString}".TrimStart('.'));
                break;
        }
    }
    if (transform == null) { Console.Error.WriteLine($"  {name}: 没有 Transform，跳过"); continue; }
    var (pos, rot, scale) = World(transform);
    var center = box != null ? V3(box["m_Center"]) : Vector3.Zero;
    var size = box != null ? V3(box["m_Size"]) : Vector3.One;
    var worldCenter = pos + Vector3.Transform(center * scale, rot);
    var worldSize = new Vector3(MathF.Abs(size.X * scale.X), MathF.Abs(size.Y * scale.Y), MathF.Abs(size.Z * scale.Z));
    var type = scripts.Any(s => s.Contains("PlaceItemTrigger")) ? "placeitem" : "visit";
    Console.Error.WriteLine($"  {name}: 脚本=[{string.Join(", ", scripts)}] BoxCollider={(box != null ? "有" : "无")} 世界中心=({worldCenter.X:0.####}, {worldCenter.Y:0.####}, {worldCenter.Z:0.####}) 尺寸=({worldSize.X:0.##}, {worldSize.Y:0.##}, {worldSize.Z:0.##}) 缩放=({scale.X:0.##},{scale.Y:0.##},{scale.Z:0.##})");
    zones.Add(new
    {
        id = name, type, locations,
        position = new { x = R(worldCenter.X), y = R(worldCenter.Y), z = R(worldCenter.Z) },
        rotation = new { x = R(rot.X), y = R(rot.Y), z = R(rot.Z), w = R(rot.W) },
        size = new { x = R(worldSize.X), y = R(worldSize.Y), z = R(worldSize.Z) },
        source
    });
}
Console.WriteLine(JsonSerializer.Serialize(zones, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
return 0;

static float R(float v) => MathF.Round(v, 4);

static int SpriteExport(string tpk, string file, string nameRx, string outDir)
{
    var m = new AssetsManager();
    m.LoadClassPackage(tpk);
    var main = m.LoadAssetsFile(file, true);
    m.LoadClassDatabaseFromPackage(main.file.Metadata.UnityVersion);
    var rx = new Regex(nameRx);
    Directory.CreateDirectory(outDir);
    var hits = 0;
    foreach (var inst in m.Files.ToList())
    {
        foreach (var info in inst.file.GetAssetsOfType(AssetClassID.Sprite))
        {
            AssetTypeValueField sp;
            try { sp = m.GetBaseField(inst, info); } catch { continue; }
            var name = sp["m_Name"].AsString;
            if (!rx.IsMatch(name)) continue;
            hits++;
            // 打进图集的 Sprite：纹理和矩形在 SpriteAtlas.m_RenderDataMap[m_RenderDataKey] 里；否则在自己的 m_RD 里
            AssetTypeValueField rd = sp["m_RD"];
            AssetsFileInstance texOwner = inst;
            var atlasPtr = sp["m_SpriteAtlas"];
            if (!atlasPtr.IsDummy && atlasPtr["m_PathID"].AsLong != 0)
            {
                var atlas = m.GetExtAsset(inst, atlasPtr);
                if (atlas.baseField != null)
                {
                    var key = sp["m_RenderDataKey"];
                    foreach (var pair in atlas.baseField["m_RenderDataMap"]["Array"].Children)
                    {
                        var k = pair["first"];
                        var same = k["second"].AsLong == key["second"].AsLong;
                        for (var i = 0; same && i < 4; i++) same = k["first"][$"data[{i}]"].AsUInt == key["first"][$"data[{i}]"].AsUInt;
                        if (same) { rd = pair["second"]; texOwner = atlas.file; break; }
                    }
                }
            }
            var tex = m.GetExtAsset(texOwner, rd["texture"]);
            if (tex.baseField == null) { Console.WriteLine($"{name}: texture not found"); continue; }
            var tf = AssetsTools.NET.Texture.TextureFile.ReadTextureFile(tex.baseField);
            var bgra = tf.GetTextureData(tex.file);
            var r = rd["textureRect"];
            int x = (int)MathF.Round(r["x"].AsFloat), y = (int)MathF.Round(r["y"].AsFloat), w = (int)MathF.Round(r["width"].AsFloat), h = (int)MathF.Round(r["height"].AsFloat);
            var settings = rd["settingsRaw"].AsUInt;
            // 纹理数据自下而上：输出第 j 行（自上而下）= 纹理第 y + h - 1 - j 行；BGRA → RGBA
            var rgba = new byte[w * h * 4];
            for (var j = 0; j < h; j++)
            for (var i = 0; i < w; i++)
            {
                var src = ((y + h - 1 - j) * tf.m_Width + (x + i)) * 4;
                var dst = (j * w + i) * 4;
                rgba[dst] = bgra[src + 2]; rgba[dst + 1] = bgra[src + 1]; rgba[dst + 2] = bgra[src]; rgba[dst + 3] = bgra[src + 3];
            }
            var safe = new string(name.Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_').ToArray());
            var outPath = Path.Combine(outDir, safe + ".png");
            WritePng(outPath, w, h, rgba);
            Console.WriteLine($"{name}: {Path.GetFileName(inst.path)} pathId={info.PathId} tex={tex.baseField["m_Name"].AsString} ({tf.m_Width}x{tf.m_Height} fmt {tf.m_TextureFormat}) rect=({x},{y},{w},{h}) settings=0x{settings:X} → {outPath}");
        }
    }
    if (hits == 0) Console.WriteLine("no sprite matched");
    return 0;
}

static void WritePng(string path, int w, int h, byte[] rgba)
{
    static uint Crc(byte[] d)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in d) { c ^= b; for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; }
        return ~c;
    }
    static byte[] BE(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
    using var fs = File.Create(path);
    fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
    void Chunk(string type, byte[] data)
    {
        fs.Write(BE((uint)data.Length));
        var td = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        fs.Write(td);
        fs.Write(BE(Crc(td)));
    }
    var ihdr = BE((uint)w).Concat(BE((uint)h)).Concat(new byte[] { 8, 6, 0, 0, 0 }).ToArray();
    var rawRows = new byte[h * (w * 4 + 1)];
    for (var j = 0; j < h; j++) Array.Copy(rgba, j * w * 4, rawRows, j * (w * 4 + 1) + 1, w * 4);
    using var ms = new MemoryStream();
    using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Optimal, true)) z.Write(rawRows);
    Chunk("IHDR", ihdr); Chunk("IDAT", ms.ToArray()); Chunk("IEND", Array.Empty<byte>());
}

static int UiTree(string tpk, string file, string nameRx, string? managed, int maxDepth)
{
    var m = new AssetsManager();
    m.LoadClassPackage(tpk);
    var af = m.LoadAssetsFile(file, true);
    m.LoadClassDatabaseFromPackage(af.file.Metadata.UnityVersion);
    // raw：读不出字段时（1.1 是 IL2CPP，global-metadata v31，UABEA 带的 LibCpp2IL 只认到 v29）把 MonoBehaviour 正文里像 PPtr 的位置解析成资源名、
    // 像颜色的 4 个 [0,1] 浮点连写成 #RRGGBB——字段顺序和 0.16 对得上时就能认出是哪个字段
    var raw = managed != null && managed.Equals("raw", StringComparison.OrdinalIgnoreCase);
    if (!raw && managed != null) m.MonoTempGenerator = new MonoCecilTempGenerator(managed);
    var fileBytes = raw ? File.ReadAllBytes(file) : Array.Empty<byte>();
    var rx = new Regex(nameRx);
    var trs = new Dictionary<long, AssetTypeValueField>();
    var goOfTr = new Dictionary<long, long>();
    foreach (var info in af.file.GetAssetsOfType(AssetClassID.Transform).Concat(af.file.GetAssetsOfType(AssetClassID.RectTransform)))
    {
        var tr = m.GetBaseField(af, info);
        trs[info.PathId] = tr;
        goOfTr[info.PathId] = tr["m_GameObject"]["m_PathID"].AsLong;
    }
    Console.WriteLine($"# {Path.GetFileName(file)} Unity={af.file.Metadata.UnityVersion} transforms={trs.Count}");
    string Ext(AssetTypeValueField ptr)
    {
        var fid = ptr["m_FileID"].AsInt; var pid = ptr["m_PathID"].AsLong;
        if (pid == 0) return "null";
        try
        {
            var ext = m.GetExtAsset(af, fid, pid, true);
            if (ext.info == null) return $"ext{fid}:{pid}";
            var name = "";
            try { var b = m.GetBaseField(ext.file, ext.info, AssetReadFlags.SkipMonoBehaviourFields); var n = b["m_Name"]; name = n.IsDummy ? "" : n.AsString; } catch { }
            return $"{(AssetClassID)ext.info.TypeId}:{name}";
        }
        catch { return $"ext{fid}:{pid}"; }
    }
    static string F(float v) => MathF.Round(v, 2).ToString(System.Globalization.CultureInfo.InvariantCulture);
    static bool IsColor(AssetTypeValueField f) => f.Children != null && f.Children.Count == 4 && f.Children[0].FieldName == "r" && f.Children[3].FieldName == "a";
    static bool IsVec2(AssetTypeValueField f) => f.Children != null && f.Children.Count == 2 && f.Children[0].FieldName == "x" && f.Children[1].FieldName == "y";
    static string Col(AssetTypeValueField f)
    {
        int C(float v) => (int)MathF.Round(Math.Clamp(v, 0f, 1f) * 255f);
        var a = f["a"].AsFloat;
        return $"#{C(f["r"].AsFloat):X2}{C(f["g"].AsFloat):X2}{C(f["b"].AsFloat):X2}{(a < 0.999f ? "@" + F(a) : "")}";
    }
    var skip = new HashSet<string> { "m_GameObject", "m_Script", "m_Name", "m_EditorHideFlags", "m_EditorClassIdentifier", "m_ObjectHideFlags", "m_CorrespondingSourceObject",
        "m_PrefabInstance", "m_PrefabAsset", "m_OnCullStateChanged", "m_RaycastPadding", "m_Maskable", "m_Material" };
    // TMP 字段上百个，只挑要对的
    var tmpKeep = new HashSet<string> { "m_text", "m_fontAsset", "m_fontSize", "m_fontColor", "m_fontStyle", "m_HorizontalAlignment", "m_VerticalAlignment", "m_textAlignment",
        "m_enableWordWrapping", "m_overflowMode", "m_enableAutoSizing", "m_fontSizeMin", "m_fontSizeMax", "m_characterSpacing", "m_margin", "m_RaycastTarget" };
    void Fields(AssetTypeValueField f, string path, int depth, List<string> outp, bool tmp)
    {
        if (outp.Count > 90 || depth > 6) return;
        if (depth == 1 && (skip.Contains(f.FieldName) || (tmp && !tmpKeep.Contains(f.FieldName)))) return;
        var tn = f.TypeName;
        if (tn.StartsWith("PPtr<")) { if (f["m_PathID"].AsLong != 0) outp.Add($"{path}={Ext(f)}"); return; }
        if (IsColor(f)) { outp.Add($"{path}={Col(f)}"); return; }
        if (IsVec2(f)) { outp.Add($"{path}=({F(f["x"].AsFloat)},{F(f["y"].AsFloat)})"); return; }
        if (f.Children == null || f.Children.Count == 0)
        {
            if (f.Value == null) return;
            var v = f.Value.ValueType == AssetValueType.Float ? F(f.AsFloat) : f.AsString;
            if (v.Length > 80) v = v.Substring(0, 80) + "…";
            outp.Add($"{path}={v.Replace("\n", "\\n")}");
            return;
        }
        if (tn == "vector" || f.FieldName == "Array") { var n = f.FieldName == "Array" ? f.Children.Count : (f["Array"].IsDummy ? 0 : f["Array"].Children.Count); if (n == 0) return; if (n > 6) { outp.Add($"{path}[{n}]"); return; } }
        foreach (var c in f.Children) Fields(c, path.Length == 0 ? c.FieldName : path + "." + c.FieldName, depth + 1, outp, tmp);
    }
    string Raw(AssetFileInfo ci)
    {
        var start = ci.GetAbsoluteByteStart(af.file); var size = (int)ci.ByteSize;
        var b = new byte[size]; Array.Copy(fileBytes, start, b, 0, size);
        var parts = new List<string> { $"raw {size}B:" };
        var ext = af.file.Metadata.Externals.Count;
        // 头 28 字节是 m_GameObject / m_Enabled / m_Script，跳过；之后逐 4 字节找 PPtr 和颜色
        for (var off = 28; off + 4 <= size; off += 4)
        {
            if (off + 12 <= size)
            {
                var fid = BitConverter.ToInt32(b, off); var pid = BitConverter.ToInt64(b, off + 4);
                if (fid >= 0 && fid <= ext && pid != 0 && pid > -1_000_000_000_000_000 && pid < 1_000_000_000_000_000 && (fid > 0 || af.file.GetAssetInfo(pid) != null))
                {
                    string name;
                    try
                    {
                        var e = m.GetExtAsset(af, fid, pid, true);
                        name = e.info == null ? $"ext{fid}:{pid}" : $"{(AssetClassID)e.info.TypeId}:{(m.GetBaseField(e.file, e.info, AssetReadFlags.SkipMonoBehaviourFields) is var eb && !eb["m_Name"].IsDummy ? eb["m_Name"].AsString : "")}";
                    }
                    catch { name = $"ext{fid}:{pid}"; }
                    parts.Add($"@{off}={name}");
                    off += 8;
                    continue;
                }
            }
            if (off + 16 <= size)
            {
                var f = new float[4]; var ok = true;
                for (var k = 0; k < 4; k++) { f[k] = BitConverter.ToSingle(b, off + k * 4); if (!(f[k] >= 0f && f[k] <= 1f)) ok = false; }
                // 颜色：四个都在 [0,1]、alpha > 0、不是全 0 / 全 1 以外的整数凑巧（整数 0/1 的 int 读成 float 是 0 / 1.4e-45，排掉极小值）
                if (ok && f[3] > 0.001f && f.All(v => v == 0f || v > 1e-6f))
                {
                    int C(float v) => (int)MathF.Round(v * 255f);
                    parts.Add($"@{off}=#{C(f[0]):X2}{C(f[1]):X2}{C(f[2]):X2}{(f[3] < 0.999f ? "@" + F(f[3]) : "")}");
                    off += 12;
                }
            }
        }
        return string.Join(" ", parts);
    }
    void Print(long trId, int depth)
    {
        if (!trs.TryGetValue(trId, out var tr) || !goOfTr.TryGetValue(trId, out var goId)) return;
        var gi = af.file.GetAssetInfo(goId); if (gi == null) return;
        var go = m.GetBaseField(af, gi);
        var pad = new string(' ', depth * 2);
        Console.WriteLine($"{pad}{go["m_Name"].AsString}{(go["m_IsActive"].AsBool ? "" : " [off]")}");
        if (tr.TypeName == "RectTransform")
            Console.WriteLine($"{pad}  rect a({F(tr["m_AnchorMin"]["x"].AsFloat)},{F(tr["m_AnchorMin"]["y"].AsFloat)})-({F(tr["m_AnchorMax"]["x"].AsFloat)},{F(tr["m_AnchorMax"]["y"].AsFloat)}) pivot({F(tr["m_Pivot"]["x"].AsFloat)},{F(tr["m_Pivot"]["y"].AsFloat)}) pos({F(tr["m_AnchoredPosition"]["x"].AsFloat)},{F(tr["m_AnchoredPosition"]["y"].AsFloat)}) size({F(tr["m_SizeDelta"]["x"].AsFloat)},{F(tr["m_SizeDelta"]["y"].AsFloat)}) scale({F(tr["m_LocalScale"]["x"].AsFloat)},{F(tr["m_LocalScale"]["y"].AsFloat)})");
        foreach (var comp in go["m_Component"]["Array"].Children)
        {
            var ci = af.file.GetAssetInfo(comp["component"]["m_PathID"].AsLong); if (ci == null) continue;
            var t = (AssetClassID)ci.TypeId;
            if (t == AssetClassID.Transform || t == AssetClassID.RectTransform) continue;
            if (t != AssetClassID.MonoBehaviour) { Console.WriteLine($"{pad}  [{t}]"); continue; }
            string cls = "?";
            AssetTypeValueField? bf = null;
            try
            {
                var head = m.GetBaseField(af, ci, AssetReadFlags.SkipMonoBehaviourFields);
                var sc = m.GetExtAsset(af, head["m_Script"], true);
                if (sc.baseField != null) cls = sc.baseField["m_ClassName"].AsString;
                if (managed != null) bf = m.GetBaseField(af, ci);
            }
            catch (Exception e) { cls += " (fields: " + e.Message + ")"; }
            var outp = new List<string>();
            if (bf != null) { var tmp = cls.Contains("TextMeshPro"); foreach (var c in bf.Children) Fields(c, c.FieldName, 1, outp, tmp); }
            if (raw) outp.Add(Raw(ci));
            Console.WriteLine($"{pad}  [{cls}] {string.Join("  ", outp)}");
        }
        if (depth >= maxDepth) return;
        foreach (var ch in tr["m_Children"]["Array"].Children) Print(ch["m_PathID"].AsLong, depth + 1);
    }
    var shown = 0;
    foreach (var (trId, goId) in goOfTr)
    {
        var gi = af.file.GetAssetInfo(goId); if (gi == null) continue;
        var name = m.GetBaseField(af, gi)["m_Name"].AsString;
        if (!rx.IsMatch(name)) continue;
        Console.WriteLine($"=== {name} (transform {trId})");
        Print(trId, 0);
        if (++shown >= 6) { Console.WriteLine("# (only the first 6 matches)"); break; }
    }
    return 0;
}

static int Dump(string tpk, string level, string needle)
{
    var mgr = new AssetsManager();
    mgr.LoadClassPackage(tpk);
    var main = mgr.LoadAssetsFile(level, false);
    mgr.LoadClassDatabaseFromPackage(main.file.Metadata.UnityVersion);
    var bytes = File.ReadAllBytes(level);
    var pat = System.Text.Encoding.ASCII.GetBytes(needle);
    var hits = new List<long>();
    for (long i = 0; i + pat.Length <= bytes.Length; i++)
    {
        var ok = true;
        for (var k = 0; k < pat.Length; k++) if (bytes[i + k] != pat[k]) { ok = false; break; }
        if (ok) hits.Add(i);
    }
    Console.WriteLine($"# {Path.GetFileName(level)} Unity={main.file.Metadata.UnityVersion} typeTree={main.file.Metadata.TypeTreeEnabled} 命中 {hits.Count} 处: {string.Join(", ", hits)}");
    Console.WriteLine("# externals: " + string.Join(" | ", main.file.Metadata.Externals.Select((e, i) => $"[{i + 1}] {e.PathName}")));
    string Name(AssetsFileInstance f, long pathId)
    {
        var info = f.file.GetAssetInfo(pathId); if (info == null) return "?";
        try { var b = mgr.GetBaseField(f, info); var n = b["m_Name"]; return $"{(AssetClassID)info.TypeId}:{(n.IsDummy ? "" : n.AsString)}"; } catch { return $"{(AssetClassID)info.TypeId}"; }
    }
    void Walk(AssetTypeValueField f, string path, int depth)
    {
        if (depth > 8) return;
        var tn = f.TypeName;
        if (tn.StartsWith("PPtr<"))
        {
            var fid = f["m_FileID"].AsInt; var pid = f["m_PathID"].AsLong;
            if (pid == 0) return;
            var target = fid == 0 ? $"本文件 {Name(main, pid)}" : (fid <= main.file.Metadata.Externals.Count ? $"外部[{fid}] {main.file.Metadata.Externals[fid - 1].PathName} pathId={pid}" : $"外部[{fid}]? pathId={pid}");
            Console.WriteLine($"    {path} = {tn} → {target}");
            return;
        }
        if (f.Children == null || f.Children.Count == 0)
        {
            var v = f.Value == null ? "" : f.AsString;
            if (!string.IsNullOrEmpty(v) && v.Length < 120) Console.WriteLine($"    {path} = {v}");
            return;
        }
        foreach (var c in f.Children) Walk(c, path + "." + c.FieldName, depth + 1);
    }
    foreach (var info in main.file.AssetInfos)
    {
        var start = info.GetAbsoluteByteStart(main.file); var end = start + info.ByteSize;
        if (!hits.Any(h => h >= start && h < end)) continue;
        var typeId = (AssetClassID)info.TypeId;
        Console.WriteLine($"== 资源 pathId={info.PathId} type={typeId} size={info.ByteSize}");
        try
        {
            var bf = mgr.GetBaseField(main, info);
            if (typeId == AssetClassID.MonoBehaviour)
            {
                var go = bf["m_GameObject"]; var ms = bf["m_Script"];
                Console.WriteLine($"   GameObject: {(go["m_PathID"].AsLong != 0 ? Name(main, go["m_PathID"].AsLong) : "-")}");
                var msf = Resolve0(mgr, main, ms["m_FileID"].AsInt, level); var msi = msf?.file.GetAssetInfo(ms["m_PathID"].AsLong);
                if (msf != null && msi != null) { var sb = mgr.GetBaseField(msf, msi); Console.WriteLine($"   脚本: {sb["m_Namespace"].AsString}.{sb["m_ClassName"].AsString} (程序集 {sb["m_AssemblyName"].AsString})"); }
            }
            Walk(bf, "", 0);
        }
        catch (Exception e) { Console.WriteLine("   字段读不出（没有类型树？）: " + e.Message); }
        // 没有类型树时的退路：把正文按 16 字节一行倒成十六进制 + ASCII，并把「像 PPtr」的 (int32 fileID ∈ 0..externals, int64 pathID≠0) 位置标出来
        var raw = new byte[info.ByteSize];
        Array.Copy(bytes, start, raw, 0, (int)info.ByteSize);
        Console.WriteLine("   -- raw --");
        for (var off = 0; off < raw.Length; off += 16)
        {
            var n = Math.Min(16, raw.Length - off);
            var hex = string.Join(" ", Enumerable.Range(0, n).Select(k => raw[off + k].ToString("x2")));
            var asc = new string(Enumerable.Range(0, n).Select(k => raw[off + k] >= 32 && raw[off + k] < 127 ? (char)raw[off + k] : '.').ToArray());
            Console.WriteLine($"   {off,5}: {hex,-48} {asc}");
        }
        Console.WriteLine("   -- 像 PPtr 的位置 --");
        for (var off = 0; off + 12 <= raw.Length; off += 4)
        {
            var fid = BitConverter.ToInt32(raw, off); var pid = BitConverter.ToInt64(raw, off + 4);
            if (fid < 0 || fid > main.file.Metadata.Externals.Count || pid == 0 || pid < -1_000_000_000_000 || pid > 1_000_000_000_000) continue;
            var target = fid == 0 ? $"本文件 {Name(main, pid)}" : $"外部[{fid}] {main.file.Metadata.Externals[fid - 1].PathName} pathId={pid}";
            Console.WriteLine($"   @{off}: fileID={fid} pathID={pid} → {target}");
        }
    }
    return 0;
}

//   ZoneExtract audio <classdata.tpk> <xxx.assets> [名字正则] [输出目录]   列出 .assets 里的 AudioClip（名字、格式、声道、采样率、resource 来源/偏移/长度）；给了输出目录就把原始音频数据（FSB5）存成 <名字>.fsb
static int Audio(string tpk, string assets, string? regex, string? outDir)
{
    var mgr = new AssetsManager();
    mgr.LoadClassPackage(tpk);
    var main = mgr.LoadAssetsFile(assets, false);
    mgr.LoadClassDatabaseFromPackage(main.file.Metadata.UnityVersion);
    var rx = string.IsNullOrEmpty(regex) ? null : new Regex(regex, RegexOptions.IgnoreCase);
    var dir = Path.GetDirectoryName(Path.GetFullPath(assets))!;
    var n = 0;
    foreach (var info in main.file.GetAssetsOfType(AssetClassID.AudioClip))
    {
        var bf = mgr.GetBaseField(main, info);
        var name = bf["m_Name"].AsString;
        if (rx != null && !rx.IsMatch(name)) continue;
        n++;
        var res = bf["m_Resource"];
        var src = res["m_Source"].AsString; var off = res["m_Offset"].AsLong; var size = res["m_Size"].AsLong;
        Console.WriteLine($"  AudioClip {name} pathId={info.PathId} format={bf["m_CompressionFormat"].AsInt} channels={bf["m_Channels"].AsInt} freq={bf["m_Frequency"].AsInt} length={bf["m_Length"].AsFloat:0.##}s resource={src} offset={off} size={size}");
        if (outDir == null) continue;
        Directory.CreateDirectory(outDir);
        var resPath = Path.Combine(dir, Path.GetFileName(src.Replace("archive:/", "").Replace('/', Path.DirectorySeparatorChar)));
        if (!File.Exists(resPath)) { Console.WriteLine($"     ⚠ 资源文件不在: {resPath}"); continue; }
        using var fs = File.OpenRead(resPath);
        fs.Seek(off, SeekOrigin.Begin);
        var data = new byte[size]; var read = 0; while (read < size) { var r = fs.Read(data, read, (int)(size - read)); if (r <= 0) break; read += r; }
        var outPath = Path.Combine(outDir, Safe(name) + ".fsb");
        File.WriteAllBytes(outPath, data);
        Console.WriteLine($"     → {outPath} ({read} 字节，头 {System.Text.Encoding.ASCII.GetString(data, 0, Math.Min(4, data.Length))})");
        // FSB5 → 标准格式（Vorbis → .ogg，PCM → .wav）：Fmod5Sharp（AssetRipper 同款）
        try
        {
            var bank = Fmod5Sharp.FsbLoader.LoadFsbFromByteArray(data);
            var k = 0;
            foreach (var sample in bank.Samples)
            {
                if (!sample.RebuildAsStandardFileFormat(out var std, out var ext)) { Console.WriteLine($"     ⚠ 样本 {k} 转不出标准格式（{sample.Metadata.Frequency}Hz {sample.Metadata.Channels}ch）"); k++; continue; }
                var stdPath = Path.Combine(outDir, Safe(name) + (bank.Samples.Count > 1 ? $"_{k}" : "") + "." + ext);
                File.WriteAllBytes(stdPath, std);
                Console.WriteLine($"     → {stdPath} ({std.Length} 字节, {sample.Metadata.Frequency}Hz {sample.Metadata.Channels}ch, 样本数 {sample.Metadata.SampleCount})");
                k++;
            }
        }
        catch (Exception e) { Console.WriteLine("     ⚠ FSB5 解码失败: " + e.Message); }
    }
    Console.WriteLine($"# {Path.GetFileName(assets)}: AudioClip 匹配 {n} 个");
    return 0;
}

static string Safe(string s) { foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_'); return s; }

static AssetsFileInstance? Resolve0(AssetsManager mgr, AssetsFileInstance owner, int fileId, string level)
{
    if (fileId == 0) return owner;
    if (fileId < 1 || fileId > owner.file.Metadata.Externals.Count) return null;
    var name = Path.GetFileName(owner.file.Metadata.Externals[fileId - 1].PathName.Replace('/', Path.DirectorySeparatorChar));
    var full = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(level))!, name);
    if (!File.Exists(full)) return null;
    try { var inst = mgr.LoadAssetsFile(full, false); mgr.LoadClassDatabaseFromPackage(inst.file.Metadata.UnityVersion); return inst; } catch { return null; }
}

// 只看本文件内的 Transform 链（跨文件的父节点按根算），够判断「这个位置附近有没有东西」
static int Near(string tpk, string level, float x, float y, float z, float radius)
{
    var mgr = new AssetsManager();
    mgr.LoadClassPackage(tpk);
    var main = mgr.LoadAssetsFile(level, false);
    mgr.LoadClassDatabaseFromPackage(main.file.Metadata.UnityVersion);
    var cache = new Dictionary<long, (Vector3 pos, Quaternion rot, Vector3 scale)>();
    (Vector3, Quaternion, Vector3) World(long pathId, int depth)
    {
        if (cache.TryGetValue(pathId, out var c)) return c;
        var info = main.file.GetAssetInfo(pathId);
        if (info == null) return (Vector3.Zero, Quaternion.Identity, Vector3.One);
        var tr = mgr.GetBaseField(main, info);
        var lp = V3(tr["m_LocalPosition"]); var lr = Q(tr["m_LocalRotation"]); var ls = V3(tr["m_LocalScale"]);
        var father = tr["m_Father"];
        (Vector3, Quaternion, Vector3) result;
        if (depth > 64 || father["m_FileID"].AsInt != 0 || father["m_PathID"].AsLong == 0) result = (lp, lr, ls);
        else
        {
            var (pp, pr, ps) = World(father["m_PathID"].AsLong, depth + 1);
            result = (pp + Vector3.Transform(lp * ps, pr), Quaternion.Normalize(pr * lr), ps * ls);
        }
        cache[pathId] = result;
        return result;
    }
    var target = new Vector3(x, y, z);
    var hits = new List<(float d, string name, Vector3 p)>();
    foreach (var info in main.file.GetAssetsOfType(AssetClassID.Transform))
    {
        var (p, _, _) = World(info.PathId, 0);
        var d = Vector3.Distance(p, target);
        if (d > radius) continue;
        var tr = mgr.GetBaseField(main, info);
        var goInfo = main.file.GetAssetInfo(tr["m_GameObject"]["m_PathID"].AsLong);
        var name = goInfo == null ? "?" : mgr.GetBaseField(main, goInfo)["m_Name"].AsString;
        hits.Add((d, name, p));
    }
    Console.WriteLine($"# {Path.GetFileName(level)}: {main.file.GetAssetsOfType(AssetClassID.Transform).Count} 个 Transform，离 ({x},{y},{z}) {radius}m 内 {hits.Count} 个");
    foreach (var (d, name, p) in hits.OrderBy(h => h.d).Take(40))
        Console.WriteLine($"  {d,7:0.0}m  {name}  @({p.X:0.#}, {p.Y:0.#}, {p.Z:0.#})");
    return 0;
}
