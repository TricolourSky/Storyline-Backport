using System.Text.Json;
using System.Text.Json.Nodes;

// QuestPort —— 1.1 原生任务/对话数据的只读勘察 + 导出工具。绝不改写源数据。
//
//   survey     <1.1 Quest 目录>                 总览：对话/任务按商人分布、字段用到哪些值
//   trader     <1.1 Quest 目录> <商人id>         单个商人的对话与任务清单
//   dialog     <1.1 Quest 目录> <对话id>         一条对话的完整结构（行/动作/触发条件，带本地化文本）
//   quest      <1.1 Quest 目录> <任务id>         一个任务的条件与奖励（原样 JSON）
//   chapters   <1.1 Quest 目录> [SPT locations]  10 个正式章节：名字、子任务数、子任务地图分布
//   closure    <1.1 Quest 目录> <任务id> [loc]   从一个任务出发的依赖闭包（只跟 conditionType=Quest）
//   deps       <1.1 Quest 目录> <商人id>         一个商人的任务链往外伸到别人家的有几条
//   maps       <1.1 Quest 目录> [SPT locations]  1.1 任务用到的地图 vs 本机有的地图
//   missing    <1.1 Quest 目录> <SPT locations>  本机没有的地图上的任务会不会卡住剧情
//   wanted     <1.1 Quest 目录> [章节id]         缺件清单：被点名但抓包里没有定义的任务
//   dialoglink <1.1 Quest 目录> <章节id>         任务挂的 dialogueId 在对话抓包里在不在
//   port       <1.1 Quest 目录> <章节id> <输出目录>  把一章的闭包导成服务端能吃的 quests + locales
//   adapt      <quests.json> <章节id>             port 的产物适配 SPT 4.1.3 / 0.16 + 章节系统开关（就地改写，先存 .bak）
//   notes      <main_quest_notes_list.json> <quests.json> <章节id>  把 1.1 日记表里的相关物品抄进任务 JSON 的 visitapi.noteLinks（就地改写，先存 .bak）
//   dialogueunlock <quests.json> <port 导出的原件.json>  给已 adapt 过的文件补 visitapi.unlockDialogue（1.1 TraderDialogueUnlock 奖励的替身；新 adapt 自带）
//   handoverdlg <dialogue.json> <quests.json>   给「有上交目标、但 dialogueId 在对话表里不存在」的任务补最小任务对话（上交选项），就地改写，先存 .bak
//   acceptdlg  <dialogue.json 参照> <quests.json> <port 导出的原件.json> <输出.json>  给「dialogueId 在对话表里不存在」的任务补最小接取对话（一条接任务选项），
//              写进独立的输出文件（服务端 db\dialogues 目录下所有 *.json 都会加载），并把这些任务的 visitapi.autoStart 关掉（改成去找商人对话接）
//   needs      <1.1 Quest 目录> <章节id|all> <SPT 4.1 database 目录> [packs 目录] [SPT5 database 目录]
//              一章要移植还缺什么：任务定义 / 对话 / 物品模板 / 区域 / 地图 / 商人 / 条件类型，对着本机 SPT 数据库和内容包核
//   dlgexport  <1.1 Quest 目录> <quests.json> <输出.json> [packs 目录] [额外商人id,…]
//              把任务文件里挂的对话（dialogueId + 邀请信 dialogueId）连同它们引用到的对话、额外商人名下的全部对话，从抓包原样抽成一份对话文件；
//              packs 里已有的元素跳过；顺带列出「对话里带 AcceptQuest」的任务（接取走对话，adapt 打的 autoStart 要关）
//   items      <SPT5 database 目录> <SPT 4.1 database 目录> <quests.json> <输出目录> [文件名] [packs 目录] [1.1 客户端 StreamingAssets\Windows]
//              任务用到（条件 + 奖励）而本机 4.1 没有、packs 里也没有的物品，从 SPT5 抽成内容包 items 格式（模板 + 20 语言 + 手册），
//              再从 SPT5 各地图 looseLoot 抽这些物品的固定刷新点成 loot 格式（只要本机有的地图、只要有模型的物品）；父类 / 手册父类本机没有的逐个点名。
//              给了 1.1 客户端目录就把本机没有的模型包原样拷进 <输出目录>\bundles\ 并登记到 bundles.json（依赖抄 1.1 的 Windows.json）；
//              武器 / 配件 / 弹药 / 1.1 专属狗牌不拷（全是到不了的终局奖励或 1.1 专属 NPC 掉落，还拖着几十 MB 依赖）
//   locfill    <扁平文案表.json> <quests.json> <输出.json>
//              从一份文案表（SPT5 database\locales\global\<lang>.json）里挑出任务文件用得上的键另存，接着 mergeloc 并进包里：
//              port 只从任务自带的 localization 块取文案，门票这批的块里连 name 都没有，得拿全局文案表补
//   schema     <json 文件> [最大深度]             任意 JSON 的字段路径全表
//
// ⚠️ 本文件含中文注释。改它只能用编辑器/Write，**别用 PowerShell 的 Get-Content|Set-Content 转存**
//    （5.1 会按 GBK 读 UTF-8，中文连同相邻的引号一起变乱码，2026-09-05 踩过一次，只能重写）。

if (args.Length < 2) return Usage();
var cmd = args[0].ToLowerInvariant();
var root = Path.GetFullPath(args[1]);

try
{
    switch (cmd)
    {
        case "survey": Survey(root); break;
        case "trader": Trader(root, args[2]); break;
        case "dialog": Dialog(root, args[2]); break;
        case "quest": Quest(root, args[2]); break;
        case "chapters": Chapters(root, args.Length > 2 ? args[2] : ""); break;
        case "closure": Closure(root, args[2], args.Length > 3 ? args[3] : ""); break;
        case "deps": Deps(root, args[2]); break;
        case "maps": Maps(root, args.Length > 2 ? args[2] : ""); break;
        case "missing": Missing(root, args[2]); break;
        case "wanted": Wanted(root, args.Length > 2 ? args[2] : ""); break;
        case "dialoglink": DialogLink(root, args[2]); break;
        case "port": Port(root, args[2], args[3]); break;
        case "endings": Endings(root); break;
        case "objectives": Objectives(root, args[2], args.Length > 3 ? args[3] : "ch"); break;
        case "needs": Needs(root, args[2], args[3], args.Length > 4 ? args[4] : "", args.Length > 5 ? args[5] : ""); break;
        case "mergeloc": MergeLoc(root, args[2], args.Length > 3 ? args[3] : ""); break;
        case "adapt": Adapt(root, args[2]); break;
        case "notes": NoteLinks(root, args[2], args[3]); break;
        case "dialogueunlock": DialogueUnlock(root, args[2]); break;
        case "locationunlock": LocationUnlock(root, args[2]); break;
        case "handoverdlg": HandoverDialogs(root, args[2]); break;
        case "acceptdlg": AcceptDialogs(root, args[2], args[3], args[4]); break;
        case "schema": Schema(root, args.Length > 2 ? int.Parse(args[2]) : 6); break;
        case "keep": Filter(root, args[2], keep: true); break;
        case "drop": Filter(root, args[2], keep: false); break;
        case "setflag": SetFlag(root, args[2], args[3], args[4]); break;
        case "dlgcopytext": DialogueCopyText(root, args[2], args[3]); break;
        case "dlgexport": DialogueExport(root, args[2], args[3], args.Length > 4 ? args[4] : "", args.Length > 5 ? args[5] : ""); break;
        case "locfill": LocaleFill(root, args[2], args[3]); break;
        case "items": ItemsExport(root, args[2], args[3], args[4], args.Length > 5 ? args[5] : "items", args.Length > 6 ? args[6] : "", args.Length > 7 ? args[7] : ""); break;
        default: return Usage();
    }
}
catch (Exception e) { Console.Error.WriteLine($"{e.GetType().Name}: {e.Message}"); return 1; }
return 0;

static int Usage()
{
    Console.Error.WriteLine("QuestPort survey|trader|dialog|quest|chapters|closure|deps|maps|missing|wanted|dialoglink|port <1.1 Quest 目录> [参数]");
    Console.Error.WriteLine("QuestPort adapt <quests.json> <章节id>");
    Console.Error.WriteLine("QuestPort notes <main_quest_notes_list.json> <quests.json> <章节id>");
    Console.Error.WriteLine("QuestPort dialogueunlock <quests.json> <port 导出的原件.json>");
    Console.Error.WriteLine("QuestPort handoverdlg <dialogue.json> <quests.json>");
    Console.Error.WriteLine("QuestPort schema <json 文件> [最大深度]");
    return 2;
}

// ── 数据源 ──

static JsonDocument Load(string path)
{
    using var fs = File.OpenRead(path);
    return JsonDocument.Parse(fs, new JsonDocumentOptions { MaxDepth = 256, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
}

static string Find(string root, params string[] parts)
{
    var dir = Path.Combine(new[] { root }.Concat(parts).ToArray());
    var hit = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json").FirstOrDefault() : null;
    return hit ?? throw new FileNotFoundException($"找不到 {dir} 下的 json");
}

static JsonDocument Dialogues(string root) => Load(Find(root, "dialogue", "response"));
static JsonDocument Quests(string root) => Load(Find(root, "quest", "list", "response"));

/// <summary>本地化表：id 键 → 文本。1.1 的字幕/任务名都只存 id，正文在这张表里。</summary>
static Dictionary<string, string> LocaleMap(string root)
{
    using var doc = Load(Find(root, "locale", "en", "response"));
    var map = new Dictionary<string, string>(StringComparer.Ordinal);
    Walk(doc.RootElement);
    return map;

    void Walk(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
            foreach (var p in e.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String) map.TryAdd(p.Name, p.Value.GetString()!);
                else Walk(p.Value);
            }
        else if (e.ValueKind == JsonValueKind.Array)
            foreach (var i in e.EnumerateArray()) Walk(i);
    }
}

static string S(JsonElement e, string name) =>
    e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

static IEnumerable<JsonElement> Arr(JsonElement e, string name) =>
    e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();

static IEnumerable<JsonElement> Conds(JsonElement quest, string bucket)
{
    if (!quest.TryGetProperty("conditions", out var c) || c.ValueKind != JsonValueKind.Object) return Enumerable.Empty<JsonElement>();
    return Arr(c, bucket);
}

/// <summary>只跟 conditionType=="Quest" 的 target。`target` 在击杀/物品/区域条件里也有，
/// 一律跟着走会把物品模板 id、区域名当成任务，全报「查无此任务」。</summary>
static IEnumerable<string> QuestTargets(JsonElement c)
{
    if (c.ValueKind == JsonValueKind.Object)
    {
        if (S(c, "conditionType") == "Quest")
        {
            var t = S(c, "target");
            if (t.Length == 24) yield return t;
        }
        foreach (var p in c.EnumerateObject())
            foreach (var x in QuestTargets(p.Value)) yield return x;
    }
    else if (c.ValueKind == JsonValueKind.Array)
        foreach (var i in c.EnumerateArray())
            foreach (var x in QuestTargets(i)) yield return x;
}

/// <summary>getMainQuestsList 抓下来的章节顺序。</summary>
static List<string> ChapterIds(string root)
{
    try
    {
        using var d = Load(Find(root, "quest", "getMainQuestsList", "response"));
        return Arr(d.RootElement, "chapters").Select(c => S(c, "ChapterId")).Where(s => s.Length == 24).ToList();
    }
    catch { return new List<string>(); }
}

/// <summary>本机 SPT 有哪些地图：地图 _Id → 目录名。</summary>
static Dictionary<string, string> SptMaps(string dir)
{
    var have = new Dictionary<string, string>(StringComparer.Ordinal);
    if (dir.Length == 0 || !Directory.Exists(dir)) return have;
    foreach (var d in Directory.GetDirectories(dir))
    {
        var b = Path.Combine(d, "base.json");
        if (!File.Exists(b)) continue;
        using var doc = Load(b);
        if (doc.RootElement.TryGetProperty("_Id", out var id) && id.ValueKind == JsonValueKind.String)
            have[id.GetString()!] = Path.GetFileName(d);
    }
    return have;
}

/// <summary>一个章节点名的子任务（只算 conditionType=Quest 的 AvailableForFinish）。</summary>
static List<string> SubQuests(JsonElement chapter)
{
    var list = new List<string>();
    foreach (var c in Conds(chapter, "AvailableForFinish"))
        if (S(c, "conditionType") == "Quest")
        {
            var t = S(c, "target");
            if (t.Length == 24 && !list.Contains(t)) list.Add(t);
        }
    return list;
}

/// <summary>从一个任务出发，沿 Quest 型条件递归收全依赖；返回 (收到的全部 id, 其中查无此任务的)。</summary>
static (HashSet<string> Seen, List<string> Missing) ClosureOf(Dictionary<string, JsonElement> byId, string seedId)
{
    var seen = new HashSet<string>(StringComparer.Ordinal) { seedId };
    var missing = new List<string>();
    var queue = new Queue<string>(new[] { seedId });
    while (queue.Count > 0)
    {
        var cur = queue.Dequeue();
        if (!byId.TryGetValue(cur, out var el)) { missing.Add(cur); continue; }
        foreach (var bucket in new[] { "AvailableForStart", "AvailableForFinish", "Fail", "Started", "AutoStart" })
            foreach (var c in Conds(el, bucket))
                foreach (var t in QuestTargets(c))
                    if (seen.Add(t)) queue.Enqueue(t);
    }
    return (seen, missing);
}

// ── 命令 ──

static void Survey(string root)
{
    using var dlg = Dialogues(root);
    var elements = dlg.RootElement.GetProperty("elements");
    Console.WriteLine($"# 对话元素 {elements.GetArrayLength()} 条");
    Tally(elements.EnumerateArray(), e => S(e, "Trader"), "按商人");

    var sides = new Dictionary<string, int>(StringComparer.Ordinal);
    var actions = new Dictionary<string, int>(StringComparer.Ordinal);
    var conds = new Dictionary<string, int>(StringComparer.Ordinal);
    var icons = new Dictionary<string, int>(StringComparer.Ordinal);
    var lines = 0;
    foreach (var el in elements.EnumerateArray())
        foreach (var ln in Arr(el, "Lines"))
        {
            lines++;
            Bump(sides, S(ln, "DialogSide"));
            Bump(icons, S(ln, "IconType"));
            foreach (var a in Arr(ln, "Actions")) Bump(actions, S(a, "type"));
            if (ln.TryGetProperty("Trigger", out var t)) WalkTypes(t, conds);
        }
    Console.WriteLine($"\n# 对话行合计 {lines}");
    Dump("DialogSide", sides);
    Dump("IconType", icons);
    Dump("Action.type", actions);
    Dump("Condition.type", conds);

    using var q = Quests(root);
    Console.WriteLine($"\n# 任务 {q.RootElement.GetArrayLength()} 个");
    Tally(q.RootElement.EnumerateArray(), e => S(e, "traderId"), "按商人");
    Tally(q.RootElement.EnumerateArray(), e => S(e, "type"), "按类型");
    Console.WriteLine($"\n# 本地化条目 {LocaleMap(root).Count}");
}

static void WalkTypes(JsonElement e, Dictionary<string, int> into)
{
    if (e.ValueKind == JsonValueKind.Object)
    {
        var t = S(e, "type");
        if (t.Length > 0) Bump(into, t);
        foreach (var p in e.EnumerateObject()) WalkTypes(p.Value, into);
    }
    else if (e.ValueKind == JsonValueKind.Array)
        foreach (var i in e.EnumerateArray()) WalkTypes(i, into);
}

static void Trader(string root, string traderId)
{
    var loc = LocaleMap(root);
    using var dlg = Dialogues(root);
    Console.WriteLine($"# 商人 {traderId} 的对话");
    Console.WriteLine("对话id\t起始\t行数\t主变量\t子商人");
    foreach (var el in dlg.RootElement.GetProperty("elements").EnumerateArray())
    {
        if (S(el, "Trader") != traderId) continue;
        Console.WriteLine(string.Join('\t', S(el, "Id"),
            el.TryGetProperty("IsStart", out var s) && s.ValueKind == JsonValueKind.True ? "是" : "",
            Arr(el, "Lines").Count(), S(el, "MainVariable"),
            string.Join(',', Arr(el, "SubTraders").Select(x => x.ToString()))));
    }

    using var q = Quests(root);
    Console.WriteLine($"\n# 商人 {traderId} 的任务");
    Console.WriteLine("任务id\t类型\t名字\t条件(Start/Finish/Fail)\t奖励数");
    foreach (var el in q.RootElement.EnumerateArray())
    {
        if (S(el, "traderId") != traderId) continue;
        var c = el.TryGetProperty("conditions", out var cc) ? cc : default;
        string Cnt(string k) => c.ValueKind == JsonValueKind.Object ? Arr(c, k).Count().ToString() : "-";
        var rw = el.TryGetProperty("rewards", out var r) ? r : default;
        string RCnt(string k) => rw.ValueKind == JsonValueKind.Object ? Arr(rw, k).Count().ToString() : "-";
        Console.WriteLine(string.Join('\t', S(el, "_id"), S(el, "type"), loc.GetValueOrDefault(S(el, "_id") + " name", ""),
            $"{Cnt("AvailableForStart")}/{Cnt("AvailableForFinish")}/{Cnt("Fail")}",
            $"S{RCnt("Started")} W{RCnt("Success")} F{RCnt("Fail")}"));
    }
}

static void Dialog(string root, string id)
{
    var loc = LocaleMap(root);
    using var dlg = Dialogues(root);
    foreach (var el in dlg.RootElement.GetProperty("elements").EnumerateArray())
    {
        if (S(el, "Id") != id) continue;
        Console.WriteLine($"# 对话 {id} trader={S(el, "Trader")} 主变量={S(el, "MainVariable")}");
        var n = 0;
        foreach (var ln in Arr(el, "Lines"))
        {
            Console.WriteLine($"\n── 行 {++n}  id={S(ln, "Id")}  {S(ln, "DialogSide")}  icon={S(ln, "IconType")}");
            if (ln.TryGetProperty("AnimationData", out var ad) && ad.ValueKind == JsonValueKind.Object)
            {
                foreach (var sub in Arr(ad, "subtitles"))
                    Console.WriteLine($"   字幕 [{S(sub, "id")}] {loc.GetValueOrDefault(S(sub, "id"), "<本地化表里没有>")}");
                foreach (var an in Arr(ad, "animations")) Console.WriteLine($"   动画 {S(an, "animationId")}");
                foreach (var lp in Arr(ad, "lipSyncs")) Console.WriteLine($"   口型 {S(lp, "lipSyncId")}");
            }
            foreach (var a in Arr(ln, "Actions")) Console.WriteLine($"   动作 {S(a, "type")}: {a}");
            if (ln.TryGetProperty("Trigger", out var t) && t.ValueKind == JsonValueKind.Object)
                Console.WriteLine($"   触发 {t}");
        }
        return;
    }
    Console.WriteLine("没有这条对话");
}

static void Quest(string root, string id)
{
    var loc = LocaleMap(root);
    using var q = Quests(root);
    foreach (var el in q.RootElement.EnumerateArray())
    {
        if (S(el, "_id") != id) continue;
        Console.WriteLine($"# 任务 {id}");
        Console.WriteLine($"名字: {loc.GetValueOrDefault(id + " name", "")}");
        Console.WriteLine(JsonSerializer.Serialize(el, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }));
        return;
    }
    Console.WriteLine("没有这个任务");
}

/// <summary>10 个正式章节各是什么、多少子任务、子任务分布在哪些地图。</summary>
static void Chapters(string root, string sptLocations)
{
    var have = SptMaps(sptLocations);
    using var q = Quests(root);
    var loc = LocaleMap(root);
    var byId = q.RootElement.EnumerateArray().ToDictionary(e => S(e, "_id"), e => e);

    var order = ChapterIds(root);
    Console.WriteLine($"# 章节 {order.Count} 个（顺序照 getMainQuestsList）\n");
    var n = 0;
    foreach (var chapter in order)
    {
        n++;
        if (!byId.TryGetValue(chapter, out var el)) { Console.WriteLine($"{n}. {chapter}  ← 任务库里没有"); continue; }
        var subs = SubQuests(el);
        var maps = new Dictionary<string, int>(StringComparer.Ordinal);
        var blocked = new List<string>();
        foreach (var s in subs)
        {
            if (!byId.TryGetValue(s, out var sub)) { Bump(maps, "<任务库里没有>"); continue; }
            var l = S(sub, "location");
            if (l.Length == 0 || l == "any") { Bump(maps, "任意"); continue; }
            if (have.TryGetValue(l, out var mapName)) { Bump(maps, mapName); continue; }
            Bump(maps, "**本机没有**");
            blocked.Add($"{s}({loc.GetValueOrDefault(s + " name", "")})");
        }
        Console.WriteLine($"{n}. 【{loc.GetValueOrDefault(chapter + " name", "?")}】 {chapter}  商人={S(el, "traderId")}  子任务 {subs.Count} 个");
        Console.WriteLine("   地图分布: " + string.Join("  ", maps.OrderByDescending(k => k.Value).Select(k => $"{k.Key}×{k.Value}")));
        if (blocked.Count > 0) Console.WriteLine("   ⚠ 本机做不了: " + string.Join(", ", blocked));
    }
}

static void Closure(string root, string seedId, string sptLocations)
{
    var have = SptMaps(sptLocations);
    using var q = Quests(root);
    var loc = LocaleMap(root);
    var byId = q.RootElement.EnumerateArray().ToDictionary(e => S(e, "_id"), e => e);
    var (seen, missing) = ClosureOf(byId, seedId);
    var present = seen.Where(byId.ContainsKey).ToList();

    Console.WriteLine($"# 从 【{loc.GetValueOrDefault(seedId + " name", seedId)}】 出发的闭包");
    Console.WriteLine($"  牵扯任务 {seen.Count} 个：抓包里有 {present.Count} 个，查无此任务 {missing.Count} 个");
    foreach (var m in missing) Console.WriteLine($"    缺 {m}  {loc.GetValueOrDefault(m + " name", "")}");

    var traders = new Dictionary<string, int>(StringComparer.Ordinal);
    var maps = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (var p in present)
    {
        Bump(traders, S(byId[p], "traderId"));
        var l = S(byId[p], "location");
        Bump(maps, l.Length == 0 || l == "any" ? "任意" : have.GetValueOrDefault(l, "**本机没有**"));
    }
    Dump("涉及商人", traders);
    Dump("涉及地图", maps);
}

/// <summary>一个商人的任务链往外伸多远：前置条件里引用了别人家的哪些任务。</summary>
static void Deps(string root, string traderId)
{
    using var q = Quests(root);
    var loc = LocaleMap(root);
    var all = q.RootElement.EnumerateArray().ToList();
    var owner = new Dictionary<string, string>(StringComparer.Ordinal);
    var story = new HashSet<string>(StringComparer.Ordinal);
    foreach (var el in all)
    {
        owner[S(el, "_id")] = S(el, "traderId");
        if (el.TryGetProperty("isStoryQuest", out var s) && s.ValueKind == JsonValueKind.True) story.Add(S(el, "_id"));
    }
    var mine = owner.Where(k => k.Value == traderId).Select(k => k.Key).ToHashSet(StringComparer.Ordinal);

    var outside = new Dictionary<string, List<string>>(StringComparer.Ordinal);
    var self = 0;
    foreach (var el in all)
    {
        if (S(el, "traderId") != traderId) continue;
        var from = S(el, "_id");
        foreach (var bucket in new[] { "AvailableForStart", "AvailableForFinish", "Fail", "Started", "AutoStart" })
            foreach (var c in Conds(el, bucket))
                foreach (var t in QuestTargets(c))
                {
                    if (!owner.ContainsKey(t)) continue;
                    if (mine.Contains(t)) { self++; continue; }
                    if (!outside.TryGetValue(t, out var list)) outside[t] = list = new List<string>();
                    list.Add(from);
                }
    }

    Console.WriteLine($"# 商人 {traderId}: 自有任务 {mine.Count} 个，其中剧情任务 {mine.Count(m => story.Contains(m))} 个");
    Console.WriteLine($"# 前置引用：指向自己人 {self} 处；指向别人 {outside.Count} 个任务");
    Console.WriteLine("\n外部任务id\t属于商人\t剧情?\t名字\t被谁引用");
    foreach (var kv in outside.OrderBy(k => owner[k.Key], StringComparer.Ordinal))
        Console.WriteLine(string.Join('\t', kv.Key, owner[kv.Key], story.Contains(kv.Key) ? "是" : "",
            loc.GetValueOrDefault(kv.Key + " name", ""), string.Join(',', kv.Value.Distinct())));
}

static void Maps(string root, string sptLocations)
{
    var have = SptMaps(sptLocations);
    using var q = Quests(root);
    var loc = LocaleMap(root);
    var used = new Dictionary<string, (int Count, int Story)>(StringComparer.Ordinal);
    foreach (var el in q.RootElement.EnumerateArray())
    {
        var l = S(el, "location");
        if (l.Length == 0) l = "<无>";
        var isStory = el.TryGetProperty("isStoryQuest", out var s) && s.ValueKind == JsonValueKind.True;
        var slot = used.GetValueOrDefault(l);
        used[l] = (slot.Count + 1, slot.Story + (isStory ? 1 : 0));
    }

    Console.WriteLine("任务数\t剧情数\t本机有?\t地图名\t地图id");
    foreach (var kv in used.OrderByDescending(k => k.Value.Count))
    {
        var name = have.GetValueOrDefault(kv.Key, loc.GetValueOrDefault(kv.Key + " Name", ""));
        Console.WriteLine(string.Join('\t', kv.Value.Count, kv.Value.Story,
            kv.Key is "<无>" or "any" ? "-" : have.ContainsKey(kv.Key) ? "有" : "**没有**",
            name.Length > 0 ? name : "?", kv.Key));
    }
}

/// <summary>本机没有的地图上的任务会不会卡住剧情：自己是不是剧情、是不是章节子任务、谁把它当前置。</summary>
static void Missing(string root, string sptLocations)
{
    var have = SptMaps(sptLocations);
    using var q = Quests(root);
    var loc = LocaleMap(root);
    var all = q.RootElement.EnumerateArray().ToList();
    var byId = all.ToDictionary(e => S(e, "_id"), e => e);
    var story = new HashSet<string>(StringComparer.Ordinal);
    var owner = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var el in all)
    {
        owner[S(el, "_id")] = S(el, "traderId");
        if (el.TryGetProperty("isStoryQuest", out var s) && s.ValueKind == JsonValueKind.True) story.Add(S(el, "_id"));
    }

    var inChapter = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var c in ChapterIds(root))
        if (byId.TryGetValue(c, out var ch))
            foreach (var s in SubQuests(ch)) inChapter[s] = c;

    var usedBy = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
    foreach (var el in all)
    {
        var from = S(el, "_id");
        foreach (var bucket in new[] { "AvailableForStart", "AvailableForFinish", "Fail", "Started", "AutoStart" })
            foreach (var cond in Conds(el, bucket))
                foreach (var t in QuestTargets(cond))
                {
                    if (!usedBy.TryGetValue(t, out var set)) usedBy[t] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(from);
                }
    }

    var bad = all.Where(e => { var l = S(e, "location"); return l.Length > 0 && l != "any" && !have.ContainsKey(l); })
                 .Select(e => S(e, "_id")).ToList();
    Console.WriteLine($"# 本机没有的地图上的任务：{bad.Count} 个\n");
    Console.WriteLine("任务id\t地图\t商人\t自己是剧情?\t属于章节?\t名字");
    foreach (var id in bad)
        Console.WriteLine(string.Join('\t', id, S(byId[id], "location"), owner[id],
            story.Contains(id) ? "是" : "否", inChapter.GetValueOrDefault(id, "否"), loc.GetValueOrDefault(id + " name", "")));

    var blocked = new HashSet<string>(bad, StringComparer.Ordinal);
    var queue = new Queue<string>(bad);
    while (queue.Count > 0)
    {
        var cur = queue.Dequeue();
        if (!usedBy.TryGetValue(cur, out var ups)) continue;
        foreach (var up in ups) if (blocked.Add(up)) queue.Enqueue(up);
    }
    blocked.ExceptWith(bad);
    var hitStory = blocked.Where(b => story.Contains(b) || inChapter.ContainsKey(b)).ToList();
    Console.WriteLine($"\n# 往上追：{blocked.Count} 个任务把它们当（直接或间接）前置；其中剧情任务或章节成员 {hitStory.Count} 个");
    foreach (var b in hitStory)
        Console.WriteLine($"  {b}\t{owner[b]}\t剧情={(story.Contains(b) ? "是" : "否")}\t章节={inChapter.GetValueOrDefault(b, "否")}\t{loc.GetValueOrDefault(b + " name", "")}");
}

/// <summary>缺件清单：被别的任务点名、但这份抓包里没有定义的任务。
/// 抓包只下发当时账号可见的任务，剧情越往后缺得越多。每条都把已知线索填好，方便照着补。</summary>
static void Wanted(string root, string onlyChapter)
{
    using var q = Quests(root);
    var en = LocaleMap(root);
    var all = q.RootElement.EnumerateArray().ToList();
    var byId = all.ToDictionary(e => S(e, "_id"), e => e);

    var chapterOf = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var c in ChapterIds(root))
        if (byId.TryGetValue(c, out var ch))
            foreach (var s in SubQuests(ch)) chapterOf[s] = c;

    var wanted = new Dictionary<string, List<(string From, string Bucket, string Status)>>(StringComparer.Ordinal);
    foreach (var el in all)
    {
        var from = S(el, "_id");
        foreach (var bucket in new[] { "AvailableForStart", "AvailableForFinish", "Fail", "Started", "AutoStart" })
            foreach (var cond in Conds(el, bucket))
                Scan(cond, bucket, from);
    }

    var rows = wanted.Keys.ToList();
    if (onlyChapter.Length > 0)
        rows = rows.Where(r => wanted[r].Any(u => u.From == onlyChapter || chapterOf.GetValueOrDefault(u.From) == onlyChapter)).ToList();

    Console.WriteLine($"# 缺件 {rows.Count} 个" + (onlyChapter.Length > 0 ? $"（只列章节 {en.GetValueOrDefault(onlyChapter + " name", onlyChapter)} 用到的）" : ""));
    Console.WriteLine("缺失任务id\t被谁点名\t条件桶\t要求状态\t点名者所属章节\t英文名\t备注");
    foreach (var id in rows)
        foreach (var (from, bucket, status) in wanted[id])
        {
            var chap = chapterOf.GetValueOrDefault(from, "");
            Console.WriteLine(string.Join('\t', id, from, bucket, StatusName(status),
                chap.Length > 0 ? en.GetValueOrDefault(chap + " name", chap) : "",
                en.GetValueOrDefault(id + " name", ""),
                en.ContainsKey(id + " name") ? "本地化表里有键" : "本地化表里没有"));
        }

    void Scan(JsonElement c, string bucket, string from)
    {
        if (c.ValueKind == JsonValueKind.Object)
        {
            if (S(c, "conditionType") == "Quest")
            {
                var t = S(c, "target");
                if (t.Length == 24 && !byId.ContainsKey(t))
                {
                    var st = c.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.Array
                        ? string.Join('/', s.EnumerateArray().Select(x => x.ToString())) : "";
                    if (!wanted.TryGetValue(t, out var list)) wanted[t] = list = new List<(string, string, string)>();
                    list.Add((from, bucket, st));
                }
            }
            foreach (var p in c.EnumerateObject()) Scan(p.Value, bucket, from);
        }
        else if (c.ValueKind == JsonValueKind.Array)
            foreach (var i in c.EnumerateArray()) Scan(i, bucket, from);
    }

    static string StatusName(string s) => s switch
    {
        "0" => "0锁定", "1" => "1可接", "2" => "2进行中", "3" => "3可交", "4" => "4完成", "5" => "5失败",
        "" => "-", _ => s
    };
}

/// <summary>任务 ↔ 对话的挂钩接不接得上：1.1 的任务带 dialogueId，玩家在那条对话里接/交任务。
/// 对话抓包同样是部分的，挂钩指向的对话不在包里 = 这个任务没法用对话接。</summary>
static void DialogLink(string root, string seedId)
{
    using var dlg = Dialogues(root);
    var haveDialog = dlg.RootElement.GetProperty("elements").EnumerateArray()
        .ToDictionary(e => S(e, "Id"), e => S(e, "Trader"));
    using var q = Quests(root);
    var byId = q.RootElement.EnumerateArray().ToDictionary(e => S(e, "_id"), e => e);
    var (seen, _) = ClosureOf(byId, seedId);

    Console.WriteLine("任务id\t商人\t剧情?\tdialogueId\t对话在不在\t对话属于谁");
    int ok = 0, miss = 0, none = 0;
    foreach (var id in seen.Where(byId.ContainsKey))
    {
        var el = byId[id];
        var d = S(el, "dialogueId");
        if (d.Length == 0 && el.TryGetProperty("mailSettings", out var ms) && ms.ValueKind == JsonValueKind.Object) d = S(ms, "dialogueId");
        var state = d.Length == 0 ? "（任务没挂对话）" : haveDialog.ContainsKey(d) ? "在" : "**不在**";
        if (d.Length == 0) none++; else if (haveDialog.ContainsKey(d)) ok++; else miss++;
        Console.WriteLine(string.Join('\t', id, S(el, "traderId"),
            el.TryGetProperty("isStoryQuest", out var s) && s.ValueKind == JsonValueKind.True ? "是" : "",
            d, state, d.Length > 0 ? haveDialog.GetValueOrDefault(d, "") : ""));
    }
    Console.WriteLine($"\n# 挂钩的对话：在 {ok} 个 / 不在 {miss} 个 / 任务没挂对话 {none} 个");
}

/// <summary>把一章的闭包导成服务端能吃的两样东西：
///   &lt;out&gt;\quests\&lt;章节id&gt;.json  —— { 任务id: 任务对象 }，SPT 的 QuestLoader 直接反序列化成 Dictionary
///   &lt;out&gt;\locales\&lt;语言&gt;.json   —— { 本地化键: 文本 }，从每个任务自带的 localization 里抽出来
/// 1.1 的任务对象**原样保留**（多出来的 isStoryQuest / notes / mailSettings 等字段 SPT 忽略，但章节 UI 要用），
/// 只补一个 SPT 要而 1.1 没有的 QuestName。</summary>
static void Port(string root, string seedId, string outDir)
{
    using var q = Quests(root);
    var byId = q.RootElement.EnumerateArray().ToDictionary(e => S(e, "_id"), e => e);
    var (seen, missing) = ClosureOf(byId, seedId);

    var quests = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
    var locales = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
    foreach (var id in seen.Where(byId.ContainsKey).OrderBy(x => x, StringComparer.Ordinal))
    {
        var node = JsonNode.Parse(byId[id].GetRawText())!.AsObject();
        if (node["localization"] is JsonObject localization)
            foreach (var (lang, table) in localization)
            {
                if (table is not JsonObject entries) continue;
                if (!locales.TryGetValue(lang, out var bag)) bag = locales[lang] = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (k, v) in entries)
                    if (v is JsonValue jv && jv.TryGetValue<string>(out var text) && text.Length > 0)
                    {
                        bag[k] = text;
                        // 日记正文：1.1 的键是「<noteId> questNoteText」，我们章节屏按裸 noteId 查（ChapterModel.Notes → id.Localized()），两种都给
                        const string noteSuffix = " questNoteText";
                        if (k.EndsWith(noteSuffix, StringComparison.Ordinal)) bag.TryAdd(k[..^noteSuffix.Length], text);
                    }
            }
        quests[id] = node;
    }
    // QuestName：1.1 没有这个字段；剧情子任务本来就没名字，退回用 id，至少日志里认得出
    foreach (var (id, node) in quests)
    {
        var name = locales.GetValueOrDefault("en")?.GetValueOrDefault(id + " name")
                   ?? locales.GetValueOrDefault("ch")?.GetValueOrDefault(id + " name");
        node["QuestName"] = string.IsNullOrWhiteSpace(name) ? id : name;
    }

    Directory.CreateDirectory(Path.Combine(outDir, "quests"));
    Directory.CreateDirectory(Path.Combine(outDir, "locales"));
    var opts = new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    var questFile = Path.Combine(outDir, "quests", seedId + ".json");
    File.WriteAllText(questFile, JsonSerializer.Serialize(quests, opts), new System.Text.UTF8Encoding(false));
    Console.WriteLine($"任务 {quests.Count} 个 → {questFile}");
    foreach (var (lang, bag) in locales.OrderBy(k => k.Key, StringComparer.Ordinal))
    {
        var f = Path.Combine(outDir, "locales", lang + ".json");
        var sorted = bag.OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary(k => k.Key, k => k.Value);
        File.WriteAllText(f, JsonSerializer.Serialize(sorted, opts), new System.Text.UTF8Encoding(false));
        Console.WriteLine($"  本地化 {lang}: {bag.Count} 条 → {f}");
    }
    if (missing.Count > 0)
        Console.WriteLine($"⚠ 闭包里查无此任务 {missing.Count} 个（需另行补件）: {string.Join(", ", missing)}");
}

/// <summary>一个章节的子任务逐个摊开：目标原文（本地化）+ 它挂了哪些 Quest 型条件（含要求的状态）。
/// 用来跟攻略流程逐条对，也用来看「哪些子任务需要别的任务处于某状态」。</summary>
static void Objectives(string root, string chapterId, string lang)
{
    using var q = Quests(root);
    var byId = q.RootElement.EnumerateArray().ToDictionary(e => S(e, "_id"), e => e);
    if (!byId.TryGetValue(chapterId, out var chapter)) { Console.WriteLine("没有这个章节"); return; }

    var n = 0;
    foreach (var sub in SubQuests(chapter))
    {
        n++;
        if (!byId.TryGetValue(sub, out var el)) { Console.WriteLine($"\n{n,2}. {sub}  ← 抓包里没有这个任务"); continue; }
        Console.WriteLine($"\n{n,2}. {sub}  商人={S(el, "traderId")}  地图={S(el, "location")}  类型={S(el, "type")}");

        // 目标原文：任务自带 localization 里「键就是纯 id」的那些条目
        if (el.TryGetProperty("localization", out var localization) && localization.TryGetProperty(lang, out var table))
            foreach (var p in table.EnumerateObject())
                if (!p.Name.Contains(' ') && p.Value.ValueKind == JsonValueKind.String && p.Value.GetString()!.Length > 0)
                    Console.WriteLine($"      · {p.Value.GetString()}");

        // 它要求别的任务处于什么状态
        foreach (var bucket in new[] { "AvailableForStart", "AvailableForFinish", "Fail" })
            foreach (var c in Conds(el, bucket))
                Report(c, bucket);

        void Report(JsonElement c, string bucket)
        {
            if (c.ValueKind == JsonValueKind.Object)
            {
                if (S(c, "conditionType") == "Quest")
                {
                    var t = S(c, "target");
                    var st = c.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.Array
                        ? string.Join('/', s.EnumerateArray().Select(x => x.ToString())) : "";
                    Console.WriteLine($"      [{bucket}] 要求任务 {t} 状态={st}{(byId.ContainsKey(t) ? "" : "  ← 抓包里没有")}");
                }
                foreach (var p in c.EnumerateObject()) Report(p.Value, bucket);
            }
            else if (c.ValueKind == JsonValueKind.Array)
                foreach (var i in c.EnumerateArray()) Report(i, bucket);
        }
    }
}

/// <summary>一章要移植还缺什么（2026-09-23）：对着本机 SPT 4.1 的数据库（地图 / 物品 / 商人 / 原版任务用过的区域）和 packs 目录核，
/// 再拿 SPT5 的物品表判断「4.1 没有的物品能不能从 1.1 移植」。只读，不写任何文件。</summary>
static void Needs(string root, string chapterArg, string sptDb, string packsDir, string spt5Db)
{
    using var q = Quests(root);
    var loc = LocaleMap(root);
    var byId = q.RootElement.EnumerateArray().ToDictionary(e => S(e, "_id"), e => e);
    using var dlg = Dialogues(root);
    var haveDialog = new HashSet<string>(dlg.RootElement.GetProperty("elements").EnumerateArray().Select(e => S(e, "Id")), StringComparer.Ordinal);

    var maps = SptMaps(Path.Combine(sptDb, "locations"));
    var items41 = TopKeys(Path.Combine(sptDb, "templates", "items.json"));
    var items5 = spt5Db.Length > 0 ? TopKeys(Path.Combine(spt5Db, "templates", "items.json")) : new HashSet<string>(StringComparer.Ordinal);
    var tradersDir = Path.Combine(sptDb, "traders");
    var traders41 = new HashSet<string>(Directory.Exists(tradersDir) ? Directory.GetDirectories(tradersDir).Select(d => Path.GetFileName(d)!) : Array.Empty<string>(), StringComparer.Ordinal);
    var vanillaQuestsFile = Path.Combine(sptDb, "templates", "quests.json");
    var vanillaQuests = TopKeys(vanillaQuestsFile);
    var zones41 = new HashSet<string>(StringComparer.Ordinal);
    using (var vq = Load(vanillaQuestsFile))
        foreach (var p in vq.RootElement.EnumerateObject()) CollectZones(p.Value, zones41);

    var packItems = new HashSet<string>(StringComparer.Ordinal);
    var packZones = new HashSet<string>(StringComparer.Ordinal);
    var packQuests = new HashSet<string>(StringComparer.Ordinal);
    var packDialogs = new HashSet<string>(StringComparer.Ordinal);
    if (packsDir.Length > 0 && Directory.Exists(packsDir))
        foreach (var pack in Directory.GetDirectories(packsDir))
        {
            foreach (var f in PackFiles(pack, "items")) packItems.UnionWith(TopKeys(f));
            foreach (var f in PackFiles(pack, "quests")) packQuests.UnionWith(TopKeys(f));
            foreach (var f in PackFiles(pack, "zones"))
                using (var d = Load(f))
                    if (d.RootElement.ValueKind == JsonValueKind.Array)
                        foreach (var z in d.RootElement.EnumerateArray()) { var id = S(z, "id"); if (id.Length > 0) packZones.Add(id); }
            foreach (var f in PackFiles(pack, "dialogues"))
                using (var d = Load(f))
                    if (d.RootElement.TryGetProperty("elements", out var els) && els.ValueKind == JsonValueKind.Array)
                        foreach (var e in els.EnumerateArray()) { var id = S(e, "Id"); if (id.Length > 0) packDialogs.Add(id); }
        }

    var chapters = chapterArg == "all" ? ChapterIds(root) : new List<string> { chapterArg };
    Console.WriteLine($"# 移植缺件核对：SPT 4.1 地图 {maps.Count} / 物品 {items41.Count} / 商人 {traders41.Count} / 原版任务用过的区域 {zones41.Count}；packs：任务 {packQuests.Count} / 物品 {packItems.Count} / 区域 {packZones.Count} / 对话 {packDialogs.Count}；SPT5 物品表 {items5.Count}");
    foreach (var chapter in chapters)
    {
        if (!byId.TryGetValue(chapter, out var ch)) { Console.WriteLine($"\n{chapter}  ← 任务库里没有这个章节"); continue; }
        var subs = SubQuests(ch);
        var present = subs.Where(byId.ContainsKey).ToList();
        var missingSubs = subs.Where(s => !byId.ContainsKey(s) && !vanillaQuests.Contains(s) && !packQuests.Contains(s)).ToList();
        var (seen, deep) = ClosureOf(byId, chapter);
        var deepMissing = deep.Where(m => !vanillaQuests.Contains(m) && !packQuests.Contains(m) && !missingSubs.Contains(m)).ToList();
        var inPack = subs.Count(packQuests.Contains);

        var traders = new Dictionary<string, int>(StringComparer.Ordinal);
        var mapUse = new Dictionary<string, int>(StringComparer.Ordinal);
        var mapMissing = new HashSet<string>(StringComparer.Ordinal);
        var dlgMissing = new List<string>(); var dlgCount = 0;
        var itemUse = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);   // tpl → 用法
        var zoneUse = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);   // 区域 → 任务
        var types = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var id in present)
        {
            var el = byId[id];
            Bump(traders, S(el, "traderId"));
            var l = S(el, "location");
            if (l.Length > 0 && l != "any") { if (maps.TryGetValue(l, out var mn)) Bump(mapUse, mn); else { Bump(mapUse, "**本机没有**"); mapMissing.Add(l); } }
            var d = S(el, "dialogueId");
            if (d.Length == 0 && el.TryGetProperty("mailSettings", out var ms) && ms.ValueKind == JsonValueKind.Object) d = S(ms, "dialogueId");
            if (d.Length > 0) { dlgCount++; if (!haveDialog.Contains(d) && !packDialogs.Contains(d)) dlgMissing.Add($"{id}→{d}"); }
            foreach (var bucket in new[] { "AvailableForStart", "AvailableForFinish", "Fail", "Started", "AutoStart" })
                foreach (var c in Conds(el, bucket)) ScanCond(c, id, itemUse, zoneUse, types, mapUse, mapMissing, maps);
            if (el.TryGetProperty("rewards", out var rw) && rw.ValueKind == JsonValueKind.Object)
                foreach (var rp in rw.EnumerateObject())
                    foreach (var r in rp.Value.EnumerateArray())
                        foreach (var it in Arr(r, "items"))
                        {
                            var tpl = S(it, "_tpl");
                            if (tpl.Length == 24) Use(itemUse, tpl, $"奖励:{id}");
                        }
        }

        var itemMissing41 = itemUse.Keys.Where(t => !items41.Contains(t) && !packItems.Contains(t)).ToList();
        var itemPortable = itemMissing41.Where(items5.Contains).ToList();
        var itemNowhere = itemMissing41.Where(t => !items5.Contains(t)).ToList();
        var zoneNew = zoneUse.Keys.Where(z => !zones41.Contains(z) && !packZones.Contains(z)).ToList();
        var traderMissing = traders.Keys.Where(t => !traders41.Contains(t)).ToList();

        Console.WriteLine($"\n【{loc.GetValueOrDefault(chapter + " name", "?")}】 {chapter}");
        Console.WriteLine($"  子任务 {subs.Count}：抓包里有 {present.Count}，已在包里 {inPack}，查无定义 {missingSubs.Count}；闭包再往外还缺 {deepMissing.Count}");
        foreach (var m in missingSubs) Console.WriteLine($"    缺定义 {m}  {loc.GetValueOrDefault(m + " name", "")}");
        foreach (var m in deepMissing) Console.WriteLine($"    闭包缺 {m}  {loc.GetValueOrDefault(m + " name", "")}");
        Console.WriteLine("  商人: " + string.Join("  ", traders.OrderByDescending(k => k.Value).Select(k => $"{k.Key}×{k.Value}{(traders41.Contains(k.Key) ? "" : "(本机没有)")}")));
        Console.WriteLine("  地图: " + string.Join("  ", mapUse.OrderByDescending(k => k.Value).Select(k => $"{k.Key}×{k.Value}")) + (mapMissing.Count > 0 ? "   本机没有: " + string.Join(", ", mapMissing) : ""));
        Console.WriteLine($"  对话: 挂了 dialogueId 的 {dlgCount} 个，缺 {dlgMissing.Count}" + (dlgMissing.Count > 0 ? "  " + string.Join(", ", dlgMissing) : ""));
        Console.WriteLine($"  物品模板: 用到 {itemUse.Count} 个 → 4.1/包里有 {itemUse.Count - itemMissing41.Count}，只在 SPT5（可移植）{itemPortable.Count}，哪儿都没有 {itemNowhere.Count}");
        foreach (var t in itemPortable) Console.WriteLine($"    可移植 {t}  {loc.GetValueOrDefault(t + " Name", "")}  [{string.Join(", ", itemUse[t].Take(3))}]");
        foreach (var t in itemNowhere) Console.WriteLine($"    没有   {t}  {loc.GetValueOrDefault(t + " Name", "")}  [{string.Join(", ", itemUse[t].Take(3))}]");
        Console.WriteLine($"  区域: 用到 {zoneUse.Count} 个 → 0.16 任务用过 / 包里有 {zoneUse.Count - zoneNew.Count}，1.1 独有（要从 1.1 客户端提取）{zoneNew.Count}");
        foreach (var z in zoneNew) Console.WriteLine($"    区域 {z}  [{string.Join(", ", zoneUse[z].Take(3))}]");
        Console.WriteLine("  条件类型: " + string.Join("  ", types.OrderByDescending(k => k.Value).Select(k => $"{k.Key}×{k.Value}")));
        if (traderMissing.Count > 0) Console.WriteLine("  ⚠ 本机没有的商人: " + string.Join(", ", traderMissing.Select(t => $"{t}({loc.GetValueOrDefault(t + " Nickname", "")})")));
    }
}

// 09-24：下面这几个原来是 Needs 的静态局部函数，dlgexport / items 也要用，提到顶层（缩进保持原样，少动行）
    static HashSet<string> TopKeys(string file)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(file)) return set;
        using var d = Load(file);
        if (d.RootElement.ValueKind == JsonValueKind.Object)
            foreach (var p in d.RootElement.EnumerateObject()) set.Add(p.Name);
        return set;
    }

    static IEnumerable<string> PackFiles(string pack, string kind)
    {
        var dir = Path.Combine(pack, kind);
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json") : Array.Empty<string>();
    }

    static void CollectZones(JsonElement e, HashSet<string> into)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            var z = S(e, "zoneId"); if (z.Length > 0) into.Add(z);
            if (S(e, "conditionType") == "VisitPlace") { var t = S(e, "target"); if (t.Length > 0) into.Add(t); }
            foreach (var zi in Arr(e, "zoneIds")) if (zi.ValueKind == JsonValueKind.String) into.Add(zi.GetString()!);
            foreach (var p in e.EnumerateObject()) CollectZones(p.Value, into);
        }
        else if (e.ValueKind == JsonValueKind.Array)
            foreach (var i in e.EnumerateArray()) CollectZones(i, into);
    }

    static void Use(Dictionary<string, HashSet<string>> d, string key, string use)
    {
        if (!d.TryGetValue(key, out var set)) d[key] = set = new HashSet<string>(StringComparer.Ordinal);
        set.Add(use);
    }

    static void ScanCond(JsonElement c, string quest, Dictionary<string, HashSet<string>> itemUse, Dictionary<string, HashSet<string>> zoneUse,
        Dictionary<string, int> types, Dictionary<string, int> mapUse, HashSet<string> mapMissing, Dictionary<string, string> maps)
    {
        if (c.ValueKind == JsonValueKind.Object)
        {
            var type = S(c, "conditionType");
            if (type.Length > 0) Bump(types, type);
            switch (type)
            {
                case "FindItem": case "HandoverItem": case "LeaveItemAtLocation": case "PlaceBeacon": case "CompletableItem": case "SellItemToTrader":
                    foreach (var t in Arr(c, "target")) if (t.ValueKind == JsonValueKind.String && t.GetString()!.Length == 24) Use(itemUse, t.GetString()!, $"{type}:{quest}");
                    break;
                case "VisitPlace":
                    { var t = S(c, "target"); if (t.Length > 0) Use(zoneUse, t, quest); break; }
                case "Location":
                    foreach (var t in Arr(c, "target"))
                        if (t.ValueKind == JsonValueKind.String)
                        {
                            var m = t.GetString()!;
                            var hit = maps.Values.FirstOrDefault(v => v.Equals(m, StringComparison.OrdinalIgnoreCase)) ?? (maps.ContainsKey(m) ? maps[m] : null);
                            if (hit != null) Bump(mapUse, hit); else if (m != "any") { Bump(mapUse, "**本机没有**"); mapMissing.Add(m); }
                        }
                    break;
            }
            var z = S(c, "zoneId"); if (z.Length > 0) Use(zoneUse, z, quest);
            foreach (var zi in Arr(c, "zoneIds")) if (zi.ValueKind == JsonValueKind.String) Use(zoneUse, zi.GetString()!, quest);
            foreach (var p in c.EnumerateObject()) ScanCond(p.Value, quest, itemUse, zoneUse, types, mapUse, mapMissing, maps);
        }
        else if (c.ValueKind == JsonValueKind.Array)
            foreach (var i in c.EnumerateArray()) ScanCond(i, quest, itemUse, zoneUse, types, mapUse, mapMissing, maps);
    }

/// <summary>1.1 的 4 个结局各要求哪些任务、这些任务在抓包里有没有定义。
/// ⚠️ 必须只跟 `conditionType == "Quest"` 的 target——结局条件里还混着成就 id、物品 id，
/// 一律当任务算会得出「结局任务全缺」的错误结论（09-05 犯过一次）。</summary>
static void Endings(string root)
{
    using var q = Quests(root);
    var loc = LocaleMap(root);
    var byId = q.RootElement.EnumerateArray().ToDictionary(e => S(e, "_id"), e => e);
    using var doc = Load(Find(root, "ending", "list", "response"));

    Console.WriteLine($"# 任务库里的定义 {byId.Count} 个\n");
    foreach (var el in Arr(doc.RootElement, "elements"))
    {
        var quests = new List<string>();
        foreach (var c in Arr(el, "conditions"))
            foreach (var t in QuestTargets(c))
                if (!quests.Contains(t)) quests.Add(t);
        var have = quests.Count(byId.ContainsKey);
        Console.WriteLine($"{S(el, "systemName"),-38} 要求任务 {quests.Count,2} 个 → 库里有 {have,2} 个，缺 {quests.Count - have}");
        foreach (var t in quests)
            Console.WriteLine($"    {(byId.ContainsKey(t) ? "有" : "缺")} {t}  {loc.GetValueOrDefault(t + " name", "")}");
    }
}

/// <summary>把 src 的本地化键并进 dst：**只补 dst 没有的键，已有的一律不动**（SORA 手改过线上那份，不能覆盖）。
///   QuestPort mergeloc &lt;src.json&gt; &lt;dst.json&gt; [quests.json]
/// 动手前给 dst 存一份 .bak-合并时间。PowerShell 5.1 没有 System.Text.Json，所以这活放在这儿做。
/// 09-24：第三个参数给了任务文件时，只并入「键开头的 24 位 id 在任务文件正文里出现过」的键（任务 id / 条件 id 都算），
/// 其余以 id 开头的键（port 闭包里带出来的原版任务、别章任务）不并——那些键会盖掉本机原版任务的文案。不以 id 开头的键照旧并入。</summary>
// 09-17 复审：备份名原来只到分钟，一分钟内对同一文件连做两条命令（keep 接 setflag）第二份会盖掉第一份、改前原件就丢了——加秒
static string BakStamp() => DateTime.Now.ToString("MMdd-HHmmss");

static void MergeLoc(string src, string dst, string questsFile = "")
{
    var utf8 = new System.Text.UTF8Encoding(false);
    var opts = new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    var incoming = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(src, utf8))
                   ?? new Dictionary<string, string>();
    if (questsFile.Length > 0)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(questsFile, utf8), "[0-9a-f]{24}")) ids.Add(m.Value);
        var before = incoming.Count;
        incoming = incoming.Where(kv => !System.Text.RegularExpressions.Regex.IsMatch(kv.Key, "^[0-9a-f]{24}") || ids.Contains(kv.Key.Substring(0, 24)))
                           .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        Console.WriteLine($"按 {Path.GetFileName(questsFile)} 过滤：{before} 键里留 {incoming.Count} 个（去掉 {before - incoming.Count} 个不属于这份任务文件的）");
    }
    var current = new Dictionary<string, string>(StringComparer.Ordinal);
    if (File.Exists(dst))
    {
        File.Copy(dst, dst + ".bak-" + BakStamp(), true);
        current = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(dst, utf8))
                  ?? new Dictionary<string, string>();
    }

    int added = 0, kept = 0;
    foreach (var (k, v) in incoming)
        if (current.ContainsKey(k)) kept++;
        else { current[k] = v; added++; }

    var sorted = current.OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary(k => k.Key, k => k.Value);
    File.WriteAllText(dst, JsonSerializer.Serialize(sorted, opts), utf8);
    Console.WriteLine($"{Path.GetFileName(dst)}: 补 {added} 条新键，原有 {kept} 条同名键保持不动，合计 {current.Count} 条 → {dst}");
}

/// <summary>把 port 导出的任务文件适配到 SPT 4.1.3 / 0.16 客户端 + 我们的章节系统（就地改写，先存 .bak-时间）：
///   QuestPort adapt &lt;quests.json&gt; &lt;章节id&gt;
/// ① 删 SPT 服务端不认的奖励类型 LocationUnlock / TraderDialogueUnlock —— 它的 RewardType 枚举没有这两个，
///    System.Text.Json 直接抛异常，**整份文件被 QuestLoader 拒读**（2026-09-05 起服务端实测，日志 `cannot parse Tour_1.1.json`）。
///    0.16 里也没有这两种机制（地图全开、没有对话解锁），删掉不改变任何行为。
/// ② 0.16 客户端没有的条件类型（Assembly-CSharp 里无此类，留着整条任务交不了）换成同 id 的替身：
///    LocationTrigger → CounterCreator/VisitPlace（09-18），CompletableItem → FindItem（09-20）；文案、显示条件、可选属性、日记全保住。
///    另按 1.1 wiki 给 Prapor 回电等待补 availableAfter（waitOverrides，09-20）——SPT 5.0 抓下来的任务数据里全是 0。
/// ③ 打章节系统的开关（服务端 QuestReadyRouter 只认 `visitapi.*`，1.1 的 isStoryQuest 它不看）：
///    章节 {chapter, icon(=1.1 的 icon 字段), autoStart}；章节 AvailableForFinish 里点名的子任务 {autoStart, autoFinish}；
///    notDisplayedQuest 的隐藏机制件 {autoStart, autoFinish}。autoStart/autoFinish 是在替 1.1 的对话接交——那批对话抓包里没有。
/// ④ 10-05：AutoStart 桶里「读到这件物品就开」的条件（CompletableItem）搬进 visitapi.startOnItems，客户端在玩家拿到其中任意一件时接这条任务；
///    带 startOnItems 的章节不打 autoStart（不在登录时自动开）。
/// 其余一字不动；已经有 visitapi 的任务不覆盖。</summary>
static void Adapt(string file, string chapterId)
{
    var utf8 = new System.Text.UTF8Encoding(false);
    var root = JsonNode.Parse(File.ReadAllText(file, utf8), documentOptions: new JsonDocumentOptions { MaxDepth = 256 })!.AsObject();
    var bak = file + ".bak-" + BakStamp();
    File.Copy(file, bak, true);

    // 2026-09-12 迷宫章节：章节完成奖励里有 `GlobalVariable`（1.1 用它在通关时置一个全局变量，迷宫是 690941e8…）——SPT 4.1.5 的 RewardType
    // 和 0.16 的 ERewardType 枚举里都没有这个成员（反编译核过），留着整份文件被两边拒读。删掉；SPT 里没人读那个变量，日后有内容要它再用 visitapi 顶。
    string[] badRewards = { "LocationUnlock", "TraderDialogueUnlock", "GlobalVariable" };
    // SPT 4.1.5 RewardType 全表（反编译 SPTarkov.Server.Core.Models.Enums.RewardType，09-12）：不在表里的奖励类型一律告警，别等服务端启动才发现拒读
    string[] sptRewards = { "Experience", "Skill", "Item", "TraderStanding", "TraderUnlock", "Location", "Counter", "AssortmentUnlock", "ProductionScheme",
        "TraderStandingReset", "TraderStandingRestore", "StashRows", "Achievement", "Pockets", "Quest", "CustomizationOffer", "ExtraDailyQuest",
        "CustomizationDirect", "Stub", "WebPromoCode", "NotificationPopup", "Customization", "BattlePassExperience", "BattlePassCurrency", "ArenaArmoryItem" };
    // 09-18（SORA 实机：塔科夫之旅「使用对讲机联系港口守军」是 LocationTrigger，被删掉后任务走进区域就自动完成）：LocationTrigger 不再删，
    // 换成同 id 的 CounterCreator{VisitPlace: <同一 target>}——客户端任务区域表按这个 target 放一个「按交互才触发」的区域（db\zones 的 interact.completeOnUse），
    // 目标文案、显示条件、必做属性全部保住。09-20 起 CompletableItem 也不删（Prune 里换 FindItem 替身），表留空给下一个不认的类型。
    string[] badConds = { };
    // 09-20（SORA 实机 + 1.1 wiki：上交 U 盘后「等待 CD 1 小时」、上交通话记录 + 加密 U 盘后「1-3 小时」Prapor 才回电，我们这边任务秒开）：
    // 1.1 的回电等待是服务端逻辑，SPT 5.0 抓的任务数据里 availableAfter 全是 0，只能照 wiki 手写到可接前置（Quest 条件）上。
    // SPT 4.1 完成前置时按它写 AvailableAfter 状态、客户端 ChapterChain 到点才自动接；dispersion 两边都不认 → 1-3 小时取 2 小时。
    var waitOverrides = new Dictionary<string, int>
    {
        ["68cc565be345756f3eb71c81"] = 3600,   // 陨落星辰 678f9df5（U 盘加密文件）：上交 U 盘 678f6bd1 完成后 1 小时
        ["679cdee4ce3a208fee0ad659"] = 7200,   // 陨落星辰 679cdee4（回收加固手提箱）：上交通话记录 + 加密 U 盘 679cdd71 完成后 1-3 小时
    };
    // 1.1 的剧情商人 67f7af56…（章节和大部分子任务挂在他名下）SPT 里本来没有 → 09-12 起 adapt 曾统一改指 Prapor（#128 ②）。
    // 09-23 起服务端从内容包 traders\ 登记 1.1 的商人（Player Trader / Kerman / Voevoda / 幸存者 / 无线电台），traderId 保持 1.1 原样，不再改指。
    const string StoryTrader11 = "67f7af56c117b6140af2a607";
    // 2026-09-12 迷宫实机：接下 Jaeger 任务后两张门禁卡没寄到，服务端日志 `hasItems=False`。根：1.1 奖励上的 `availableInGameEditions`
    // 写的是**游戏模式**（regular / pve / pvp-season），SPT 4.1.5 `RewardHelper.RewardIsForGameEdition` 拿它和档案的 `Info.GameVersion`
    // （edge_of_darkness / standard …）比对，永远不匹配 → 整条奖励静默跳过（物品、经验、声望全丢，塔科夫之旅的 30 万 / 100 万卢布同病）。
    // 两张表清空（SPT：空表 = 不限版本），`notAvailableInGameEditions` 同理。
    string[] editionKeys = { "availableInGameEditions", "notAvailableInGameEditions" };
    int rewardsCut = 0, condsCut = 0, tradersMoved = 0, editionsCleared = 0;
    var dialogueUnlocks = new Dictionary<string, List<string>>();   // 任务 → 完成后开放对话的商人（TraderDialogueUnlock 的替身，见 DialogueUnlock）
    var locationUnlocks = new Dictionary<string, List<string>>();   // 任务 → 完成后解锁的地图 _Id（LocationUnlock 的替身，见 LocationUnlock）
    var startOnItems = new Dictionary<string, List<string>>();      // 任务 → 拿到其中任意一件就开（AutoStart 桶里 CompletableItem 的替身，10-05）
    foreach (var (id, node) in root)
    {
        if (node is not JsonObject q) continue;
        if (q["traderId"]?.GetValue<string>() == StoryTrader11) { tradersMoved++; Console.WriteLine($"  商人 {id} 归 1.1 剧情商人 Player Trader（保持原样，服务端从内容包 traders\\ 登记他）"); }
        if (q["rewards"] is JsonObject rewards)
            foreach (var (bucket, list) in rewards)
                if (list is JsonArray arr)
                    for (var i = arr.Count - 1; i >= 0; i--)
                    {
                        if (arr[i] is JsonObject rw)
                            foreach (var key in editionKeys)
                                if (rw[key] is JsonArray eds && eds.Count > 0) { rw[key] = new JsonArray(); editionsCleared++; }
                        var type = arr[i]?["type"]?.GetValue<string>();
                        if (type != null && !badRewards.Contains(type) && !sptRewards.Contains(type))
                            Console.WriteLine($"  ⚠ 奖励类型 {type}（{id} {bucket}[{i}]）不在 SPT 4.1.5 的 RewardType 表里，服务端会拒读整份文件——要么删要么换替身");
                        if (!badRewards.Contains(type)) continue;
                        Console.WriteLine($"  删奖励 {id} {bucket}[{i}] {type} → {arr[i]?["target"]}");
                        if (type == "TraderDialogueUnlock" && bucket == "Success" && arr[i]?["target"]?.GetValue<string>() is { Length: 24 } trader)
                            (dialogueUnlocks.TryGetValue(id!, out var l) ? l : dialogueUnlocks[id!] = new List<string>()).Insert(0, trader);
                        if (type == "LocationUnlock" && bucket == "Success" && arr[i]?["target"]?.GetValue<string>() is { Length: 24 } map)
                            (locationUnlocks.TryGetValue(id!, out var ml) ? ml : locationUnlocks[id!] = new List<string>()).Insert(0, map);
                        arr.RemoveAt(i); rewardsCut++;
                    }
        if (q["conditions"] is JsonObject conds)
        {
            // 2026-09-07 实机：1.1 的条件表多一个 `AutoStart` 桶（EQuestStatus 新值），0.16 客户端把它当字典键反序列化直接抛
            // "Could not convert string 'AutoStart' to dictionary key type 'EFT.Quests.EQuestStatus'"，整份 /client/quest/list 拒收 → 登录无限转圈。
            // 塔科夫之旅 25 个任务里这个桶全是空的；非空也没法用（0.16 没有自动接取机制，由 visitapi.autoStart 顶替），一律删。
            // 10-05 神秘蓝焰：桶里是「读到这件物品就开这条任务」（CompletableItem，任意一件即可——SPT5 服务端 GetCompletableItemQuests 核过）。
            // 桶照删，物品记下来搬进 visitapi.startOnItems（「读」换成「拿到」，和 CompletableItem → FindItem 同一口径）
            if (conds["AutoStart"] is JsonArray autoStart)
                foreach (var c in autoStart)
                    if (c?["conditionType"]?.GetValue<string>() == "CompletableItem" && c["target"]?.GetValue<string>() is { Length: 24 } tpl)
                        (startOnItems.TryGetValue(id!, out var sl) ? sl : startOnItems[id!] = new List<string>()).Add(tpl);
                    else if (c != null) Console.WriteLine($"  ⚠ {id} AutoStart 里有不认识的条件 {c["conditionType"]}，没搬");
            if (conds.ContainsKey("AutoStart")) { Console.WriteLine($"  删条件桶 {id} AutoStart（{(conds["AutoStart"] as JsonArray)?.Count ?? 0} 条）"); conds.Remove("AutoStart"); condsCut++; }
            // 10-06 意外证人 6917c6ef：可接条件里还有一条读同一件物品的 CompletableItem（和 AutoStart 重复）。Prune 会把它换成 FindItem，
            // 留在可接桶里没用（SPT 判可接只看任务 / 好感 / 声望），客户端却要连它的进度——同一件物品已经进了 startOnItems，删掉
            if (startOnItems.TryGetValue(id!, out var startItems) && conds["AvailableForStart"] is JsonArray afs)
                for (var i = afs.Count - 1; i >= 0; i--)
                    if (afs[i]?["conditionType"]?.GetValue<string>() == "CompletableItem" && afs[i]?["target"]?.GetValue<string>() is { } t && startItems.Contains(t))
                    { Console.WriteLine($"  删可接条件 {id} AvailableForStart[{i}] CompletableItem {t}（同一件物品已进 startOnItems）"); afs.RemoveAt(i); condsCut++; }
            foreach (var (bucket, list) in conds)
                if (list is JsonArray arr) condsCut += Prune(arr, id!, bucket);
        }
    }

    if (root[chapterId] is not JsonObject chapter) throw new ArgumentException($"文件里没有章节 {chapterId}");
    var flagged = 0;
    // 10-05：拿到物品才开的章节（startOnItems）不在登录时自动开
    var chapterFlags = startOnItems.ContainsKey(chapterId) ? new JsonObject { ["chapter"] = true } : new JsonObject { ["chapter"] = true, ["autoStart"] = true };
    if (chapter["icon"]?.GetValue<string>() is { Length: > 0 } icon) chapterFlags["icon"] = icon;
    flagged += Stamp(chapter, chapterId, chapterFlags);
    foreach (var c in chapter["conditions"]?["AvailableForFinish"] as JsonArray ?? new JsonArray())
        if (c?["conditionType"]?.GetValue<string>() == "Quest" && c["target"]?.GetValue<string>() is { Length: 24 } sub)
            // 10-06 意外证人当天改过「只靠读物品开的子任务不打 autoStart」，SORA 实测第一步做完就断了（「任务目标又没了」）：1.1 / SPT5 里没有任务前置的子任务
            // 还会在章节列表里紧挨着的前一条结束时自动开（PredecessorCompleted，ChapterChain.PredecessorDone 同口径），读物品只是另一个更早的开法。撤回，照旧打 autoStart
            if (root[sub] is JsonObject s) flagged += Stamp(s, sub, new JsonObject { ["autoStart"] = true, ["autoFinish"] = true });
            else Console.WriteLine($"  ⚠ 子任务 {sub} 不在文件里，没打开关");
    foreach (var (id, node) in root)
        if (node is JsonObject q && q["notDisplayedQuest"]?.GetValue<bool>() == true)
            flagged += Stamp(q, id!, new JsonObject { ["autoStart"] = true, ["autoFinish"] = true });
    // 2026-09-08：TraderDialogueUnlock 不再是「删掉不改变行为」——1.1 靠它逐步开放商人对话（访问按钮），SORA 实机要求照 1.1 逐步解锁。
    // 奖励本体 SPT 不认必须删，语义搬进 visitapi.unlockDialogue，客户端按「点名的任务有已完成的」放行访问按钮。
    foreach (var (id, traders) in dialogueUnlocks)
        if (root[id] is JsonObject q) flagged += StampDialogueUnlock(q, id, traders);
    // 2026-09-15：LocationUnlock 同理（SORA：跟正式版一样随剧情解锁地图，实验性质）——语义搬进 visitapi.unlockLocations，
    // 服务端 StoryLocks 只给新建角色打「剧情锁」标记，客户端 StoryMapLock 按它锁选图界面
    foreach (var (id, maps) in locationUnlocks)
        if (root[id] is JsonObject q) flagged += StampList(q, id, "unlockLocations", maps);
    foreach (var (id, tpls) in startOnItems)
        if (root[id] is JsonObject q) flagged += StampList(q, id, "startOnItems", tpls);

    var opts = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    File.WriteAllText(file, root.ToJsonString(opts), utf8);
    Console.WriteLine($"删奖励 {rewardsCut} 条、删条件 {condsCut} 条、归 Player Trader 的 {tradersMoved} 条（保持原样）、清版本限制 {editionsCleared} 条、打开关 {flagged} 个 → {file}（原件 {Path.GetFileName(bak)}）");

    int Prune(JsonArray arr, string id, string where)
    {
        var n = 0;
        for (var i = arr.Count - 1; i >= 0; i--)
        {
            if (arr[i] is not JsonObject c) continue;
            var type = c["conditionType"]?.GetValue<string>();
            if (badConds.Contains(type))
            {
                Console.WriteLine($"  删条件 {id} {where}[{i}] {type} → {c["target"]}  (id {c["id"]})");
                arr.RemoveAt(i); n++; continue;
            }
            // 09-18：LocationTrigger（1.1 战局里交互触发的条件，如港口终点站对讲机 first_domofon_interaction）→ 同 id 的 CounterCreator{VisitPlace: 同 target}，
            // id / index / 显示条件 / isNecessary / parentId 原样保住；客户端在 db\zones 里给这个 target 放一个「按交互才触发」的区域（interact.completeOnUse）。
            if (type == "LocationTrigger" && c["target"]?.GetValue<string>() is { Length: > 0 } place)
            {
                var visit = new JsonObject { ["conditionType"] = "VisitPlace", ["id"] = NewId(), ["target"] = place, ["value"] = 1 };
                c["conditionType"] = "CounterCreator";
                c["counter"] = new JsonObject { ["id"] = NewId(), ["conditions"] = new JsonArray(visit) };
                c["value"] = 1;
                c.Remove("target");
                Console.WriteLine($"  换条件 {id} {where}[{i}] LocationTrigger {place} → CounterCreator/VisitPlace 同 target（区域表要配 interact.completeOnUse 的区域）  (id {c["id"]})");
                continue;
            }
            // 09-20（SORA 实机：陨落星辰「回收加固手提箱」少了可选目标「追查任何其他的线索」——1.1 的 CompletableItem 以前被整条删掉）：
            // CompletableItem（1.1 的可读物品：读完字条即完成，顺带解锁 questNoteId 那条日记）→ 同 id 的 FindItem（拿到字条即完成，日记照样由 questNoteId 解锁）。
            // 0.16 没有可读物品机制；换 FindItem 还顺便让 GameWorld.ManageQuestLoot 只给在做这条任务的人刷字条（物品表里该字条要 QuestItem=true）。
            // 1.1 子条件不写 isNecessary = 可选，这里明写 false；物品表 / 刷新点表要另外补这张字条（db\items、db\loot）。
            if (type == "CompletableItem" && c["target"]?.GetValue<string>() is { Length: 24 } note)
            {
                c["conditionType"] = "FindItem";
                c["target"] = new JsonArray((JsonNode)note);
                c["value"] = 1; c["minDurability"] = 0.0; c["maxDurability"] = 100.0; c["dogtagLevel"] = 0;
                c["onlyFoundInRaid"] = false; c["isEncoded"] = false; c["countInRaid"] = false; c["includeEquipment"] = true;
                if (c["isNecessary"] == null && c["parentId"]?.GetValue<string>() is { Length: > 0 }) c["isNecessary"] = false;
                c.Remove("isCompleted");
                Console.WriteLine($"  换条件 {id} {where}[{i}] CompletableItem {note} → FindItem 同 target（物品表要有这张字条且 QuestItem=true，刷新点表要给它位置）  (id {c["id"]})");
                continue;
            }
            // （1.1 的 LeaveItemAtLocation / PlaceBeacon 只写 zoneIds 数组、0.16 只认 zoneId——这一步由服务端 QuestLoader.FixZoneIds 在加载时补，数据保持 1.1 原样，这里不重复做）
            if (type == "Quest" && c["id"]?.GetValue<string>() is { } cid && waitOverrides.TryGetValue(cid, out var secs))
            {
                c["availableAfter"] = secs;
                Console.WriteLine($"  定时 {id} {where}[{i}] Quest {c["target"]} availableAfter → {secs}s（1.1 wiki 的 Prapor 回电等待，见 waitOverrides）  (id {cid})");
            }
            if (c["counter"]?["conditions"] is JsonArray inner) n += Prune(inner, id, where + $"[{i}]/counter");
        }
        return n;
    }

    static string NewId() => Guid.NewGuid().ToString("N").Substring(0, 24);   // 24 位十六进制，和 1.1 的 id 同款

    static int Stamp(JsonObject q, string id, JsonObject flags)
    {
        if (q["visitapi"] is JsonObject) { Console.WriteLine($"  {id} 已有 visitapi，不动"); return 0; }
        q["visitapi"] = flags;
        Console.WriteLine($"  开关 {id} {flags.ToJsonString()}");
        return 1;
    }
}

/// 往任务的 visitapi 里写 unlockDialogue（已有的合并去重，其它键不动）。返回 1=写了
static int StampDialogueUnlock(JsonObject q, string id, List<string> traders) => StampList(q, id, "unlockDialogue", traders);

/// 往任务的 visitapi 里写一个 24 位 id 的数组键（unlockDialogue / unlockLocations；已有的合并去重，其它键不动）。返回 1=写了
static int StampList(JsonObject q, string id, string key, List<string> values)
{
    var vo = q["visitapi"] as JsonObject ?? (JsonObject)(q["visitapi"] = new JsonObject());
    var merged = new List<string>();
    if (vo[key] is JsonArray old) foreach (var t in old) if (t?.GetValue<string>() is { Length: 24 } s) merged.Add(s);
    foreach (var t in values) if (!merged.Contains(t)) merged.Add(t);
    vo[key] = new JsonArray(merged.Select(t => (JsonNode)t).ToArray());
    Console.WriteLine($"  {key} {id} → [{string.Join(", ", merged)}]");
    return 1;
}

/// <summary>缺失任务对话的最小替身（2026-09-08，SORA 实机：Skier 对话里没有上交选项）。
/// 0.16 的机制（反编译 `DynamicTraderDialog.GenerateEmbeddedQuestDialogLines`）：主对话里的 SwitchQuestDialog 行会把每条任务
/// `dialogueId` 指向的模板里、当前可见的**玩家台词**嵌进主对话的选项列表（Therapist 的「上交 250,000 卢布」就是这么出来的）。
/// 塔科夫之旅 13 条任务对话抓包里一条没有，所以上交类目标在商人面前无处可点。这里给每条「有 HandoverItem 目标、dialogueId 在对话表里不存在」的任务
/// 生成一份最小模板（沿用原 dialogueId，主对话里指向它的 SwitchDialog 也就自动接上了）：
///   每个上交目标一条玩家台词（文案 = 该目标在 1.1 任务内嵌 20 语言里的描述，如「将任意建筑材料上交给 Skier」）：
///     触发 = 主变量==0 ∧ HasItemForHandover ∧ 任务 Started；动作 = HandoverItem + 主变量=1
///   一条 NPC 收尾「…」（主变量==1）：SwitchDialog 回商人主对话（起点 = 主对话里 SwitchQuestDialog 那一行要求的状态，即选项列表），主变量=2。
/// 不编任何一句新台词——玩家台词用任务自带的目标描述，NPC 收尾用「…」（Skier 主对话里本就有这种省略号行）。
///   QuestPort handoverdlg &lt;dialogue.json&gt; &lt;quests.json&gt;</summary>
static void HandoverDialogs(string dialogueFile, string questsFile)
{
    var utf8 = new System.Text.UTF8Encoding(false);
    var docOpts = new JsonDocumentOptions { MaxDepth = 256 };
    var dlg = JsonNode.Parse(File.ReadAllText(dialogueFile, utf8), documentOptions: docOpts)!.AsObject();
    var quests = JsonNode.Parse(File.ReadAllText(questsFile, utf8), documentOptions: docOpts)!.AsObject();
    var elements = dlg["elements"] as JsonArray ?? throw new ArgumentException("dialogue.json 没有 elements");
    var have = new HashSet<string>(elements.OfType<JsonObject>().Select(e => e["Id"]?.GetValue<string>() ?? ""));
    var mains = elements.OfType<JsonObject>().Where(e => e["IsStart"]?.GetValue<bool>() == true)
        .GroupBy(e => e["Trader"]?.GetValue<string>() ?? "").ToDictionary(g => g.Key, g => g.First());
    var bak = dialogueFile + ".bak-" + BakStamp();
    File.Copy(dialogueFile, bak, true);
    var made = 0;
    foreach (var (qid, node) in quests)
    {
        if (node is not JsonObject q) continue;
        var dialogueId = q["dialogueId"]?.GetValue<string>();
        var traderId = q["traderId"]?.GetValue<string>();
        if (dialogueId is not { Length: 24 } || traderId is not { Length: 24 } || have.Contains(dialogueId)) continue;
        var handovers = (q["conditions"]?["AvailableForFinish"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Where(c => c["conditionType"]?.GetValue<string>() == "HandoverItem").ToList();
        if (handovers.Count == 0) continue;
        if (!mains.TryGetValue(traderId, out var main)) { Console.WriteLine($"  ⚠ {qid}: 商人 {traderId} 没有主对话，跳过"); continue; }
        var mainId = main["Id"]!.GetValue<string>();
        var mainVar = main["MainVariable"]!.GetValue<string>();
        var splitter = OptionsStartPoint(main, mainVar);
        var loc = q["localization"] as JsonObject;   // 1.1 内嵌的 20 语言：{ lang: { key: text } }
        var langs = loc?.Select(kv => kv.Key).ToList() ?? new List<string> { "ch", "en" };
        var lines = new JsonArray();
        var texts = langs.ToDictionary(l => l, _ => new JsonObject());
        var n = 0;
        foreach (var c in handovers)
        {
            var condId = c["id"]!.GetValue<string>();
            var value = c["value"]?.GetValue<double>() ?? 1;
            var isCash = (c["target"] as JsonArray)?.Any(t => t?.GetValue<string>() == "5449016a4bdc2d6f028b456f") == true;
            var partial = value > 1 && !isCash;   // 多件物品允许分批交；现金和单件一次交清（1.1 Therapist 的 250,000 卢布就是 false）
            var subId = Id($"{dialogueId}|sub|{n}");
            foreach (var l in langs)
            {
                var text = loc?[l]?[condId]?.GetValue<string>() ?? loc?["en"]?[condId]?.GetValue<string>();
                if (!string.IsNullOrEmpty(text)) texts[l][subId] = text;
            }
            lines.Add(Line(Id($"{dialogueId}|line|{n}"), "Player", subId,
                actions: new JsonArray(
                    new JsonObject { ["type"] = "HandoverItem", ["questId"] = qid, ["needNotification"] = true, ["conditionId"] = condId, ["partialHandover"] = partial, ["id"] = Id($"{dialogueId}|act|{n}") },
                    SetVar(dialogueId, 1, Id($"{dialogueId}|set|{n}"))),
                conds: new JsonArray(
                    new JsonObject { ["variableId"] = dialogueId, ["value"] = 0, ["operator"] = "==", ["type"] = "VariableValue" },
                    new JsonObject { ["questId"] = qid, ["conditionId"] = condId, ["partial"] = partial, ["type"] = "HasItemForHandover" },
                    new JsonObject { ["QuestId"] = qid, ["Status"] = new JsonArray("Started"), ["type"] = "QuestStatus" })));
            n++;
        }
        var ackId = Id($"{dialogueId}|ack");
        foreach (var l in langs) texts[l][ackId] = "…";
        var back = new JsonObject { ["dialogId"] = mainId, ["type"] = "SwitchDialog", ["needNotification"] = false, ["id"] = Id($"{dialogueId}|back") };
        if (splitter != null) back["splitterNodeId"] = splitter;
        lines.Add(Line(Id($"{dialogueId}|line|ack"), "Npc", ackId,
            actions: new JsonArray(back, SetVar(dialogueId, 2, Id($"{dialogueId}|set|ack"))),
            conds: new JsonArray(new JsonObject { ["variableId"] = dialogueId, ["value"] = 1, ["operator"] = "==", ["type"] = "VariableValue" })));
        var localization = new JsonObject();
        foreach (var (l, t) in texts) localization[l] = t;
        elements.Add(new JsonObject
        {
            ["Id"] = dialogueId, ["IsStart"] = false, ["MainVariable"] = dialogueId, ["Trader"] = traderId, ["SubTraders"] = new JsonArray(),
            ["Lines"] = lines, ["StartPoints"] = new JsonObject(), ["localization"] = localization,
        });
        have.Add(dialogueId);
        made++;
        Console.WriteLine($"  任务对话 {dialogueId} ← 任务 {qid}：{handovers.Count} 条上交选项，回主对话 {mainId}（起点 {(splitter ?? "无")}）");
    }
    var opts = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    File.WriteAllText(dialogueFile, dlg.ToJsonString(opts), utf8);
    Console.WriteLine($"补了 {made} 份任务对话 → {dialogueFile}（原件 {Path.GetFileName(bak)}）");

    static JsonObject Line(string id, string side, string subId, JsonArray actions, JsonArray conds) => new()
    {
        ["Id"] = id, ["DialogSide"] = side, ["IconType"] = "DialogBubble", ["ConfirmationKey"] = null,
        ["AnimationData"] = new JsonObject
        {
            ["animations"] = new JsonArray(), ["secondaryAnimations"] = new JsonArray(), ["lipSyncs"] = new JsonArray(),
            ["subtitles"] = new JsonArray(new JsonObject { ["id"] = subId, ["start"] = 0.0, ["end"] = 1.0 }),
            ["media"] = new JsonObject { ["image"] = null, ["music"] = null, ["sound"] = null },
        },
        ["Actions"] = actions,
        ["Trigger"] = new JsonObject { ["type"] = "MainLogicalGroup", ["Conditions"] = new JsonArray(new JsonObject { ["type"] = "LogicalSubGroup", ["Conditions"] = conds }) },
        ["TraderId"] = null,
    };

    static JsonObject SetVar(string variableId, int value, string id) =>
        new() { ["variableId"] = variableId, ["value"] = value, ["saveScope"] = "Dialogue", ["type"] = "SetVariable", ["id"] = id };

    /// 主对话里 SwitchQuestDialog 那一行要求的主变量值（=选项列表那个状态）对应的 StartPoint id；找不到返回 null（回主对话就从头打招呼）
    static string? OptionsStartPoint(JsonObject main, string mainVar)
    {
        var line = (main["Lines"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(l => (l["Actions"] as JsonArray)?.OfType<JsonObject>().Any(a => a["type"]?.GetValue<string>() == "SwitchQuestDialog") == true);
        var want = line?["Trigger"]?["Conditions"]?[0]?["Conditions"]?.AsArray().OfType<JsonObject>()
            .FirstOrDefault(c => c["type"]?.GetValue<string>() == "VariableValue" && c["variableId"]?.GetValue<string>() == mainVar)?["value"]?.GetValue<int>();
        if (want == null || main["StartPoints"] is not JsonObject sp) return null;
        return sp.FirstOrDefault(kv => kv.Value?.GetValue<int>() == want).Key;
    }

    /// 确定性的 24 位十六进制 id（同一输入永远同一 id，重跑不会换 id）
    static string Id(string seed)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes("visitapi.handoverdlg|" + seed));
        return Convert.ToHexString(hash).ToLowerInvariant().Substring(0, 24);
    }
}

/// <summary>缺失任务对话的最小接取替身（2026-09-12，SORA：定时到了商人出金色电话，玩家去对话接任务，接了才寄奖励——1.1 的流程）。
/// 0.16 的 `GenerateEmbeddedQuestDialogLines` 遍历任务书里所有带 dialogueId 的任务，把模板里当前触发条件成立的**玩家台词**嵌进主对话；
/// 这里给每条「dialogueId 在对话表里不存在」的任务生成：
///   一条玩家台词（图标 QuestIcon）：触发 = 主变量==0 ∧ 任务 AvailableForStart；动作 = AcceptQuest + 主变量=1。
///     文案不编：优先任务名（剧情任务几乎都是空串）→ 否则 1.1 原件里 AvailableForFinish 中 index 最小、没有 parentId 的那条目标描述
///     （迷宫 Jaeger 任务 = 「查明 BEAR 小队的遭遇」，adapt 删掉的父目标文案还在任务内嵌 20 语言里）→ 否则任务描述 → 都没有就跳过并点名。
///   一条 NPC 收尾「…」（主变量==1）：SwitchDialog 回商人主对话（起点 = 主对话 SwitchQuestDialog 行要求的状态），主变量=2。
/// 模板写进独立输出文件（DialogueLoader 加载 db\dialogues 下全部 *.json，发布脚本只带 dialogue.json，剧情件不会混进正式包）；
/// 同时把这些任务的 `visitapi.autoStart` 关掉（原件 .bak），让它们停在「可接」等玩家去谈——电话角标就是按这个状态亮的。
///   QuestPort acceptdlg &lt;dialogue.json 参照&gt; &lt;quests.json&gt; &lt;port 导出的原件.json&gt; &lt;输出.json&gt;</summary>
static void AcceptDialogs(string dialogueFile, string questsFile, string exportedFile, string outFile)
{
    var utf8 = new System.Text.UTF8Encoding(false);
    var docOpts = new JsonDocumentOptions { MaxDepth = 256 };
    var dlg = JsonNode.Parse(File.ReadAllText(dialogueFile, utf8), documentOptions: docOpts)!.AsObject();
    var quests = JsonNode.Parse(File.ReadAllText(questsFile, utf8), documentOptions: docOpts)!.AsObject();
    var exported = JsonNode.Parse(File.ReadAllText(exportedFile, utf8), documentOptions: docOpts)!.AsObject();
    var elements = dlg["elements"] as JsonArray ?? throw new ArgumentException("参照对话表没有 elements");
    var have = new HashSet<string>(elements.OfType<JsonObject>().Select(e => e["Id"]?.GetValue<string>() ?? ""));
    var mains = elements.OfType<JsonObject>().Where(e => e["IsStart"]?.GetValue<bool>() == true)
        .GroupBy(e => e["Trader"]?.GetValue<string>() ?? "").ToDictionary(g => g.Key, g => g.First());
    var outRoot = File.Exists(outFile) ? JsonNode.Parse(File.ReadAllText(outFile, utf8), documentOptions: docOpts)!.AsObject() : new JsonObject { ["elements"] = new JsonArray() };
    var outElements = outRoot["elements"] as JsonArray ?? (JsonArray)(outRoot["elements"] = new JsonArray());
    foreach (var e in outElements.OfType<JsonObject>()) have.Add(e["Id"]?.GetValue<string>() ?? "");
    var bak = questsFile + ".bak-" + BakStamp();
    File.Copy(questsFile, bak, true);
    var made = 0;
    foreach (var (qid, node) in quests)
    {
        if (node is not JsonObject q) continue;
        var dialogueId = q["dialogueId"]?.GetValue<string>();
        var traderId = q["traderId"]?.GetValue<string>();
        if (dialogueId is not { Length: 24 } || traderId is not { Length: 24 } || have.Contains(dialogueId)) continue;
        if (!mains.TryGetValue(traderId, out var main)) { Console.WriteLine($"  ⚠ {qid}: 商人 {traderId} 没有主对话，跳过"); continue; }
        var loc = q["localization"] as JsonObject;
        var langs = loc?.Select(kv => kv.Key).ToList() ?? new List<string> { "ch", "en" };
        var textKey = AcceptTextKey(qid!, q, exported[qid!] as JsonObject, loc);
        if (textKey == null) { Console.WriteLine($"  ⚠ {qid}: 任务名 / 顶层目标 / 描述都没有文案，接取选项没法写，跳过"); continue; }
        var mainId = main["Id"]!.GetValue<string>();
        var mainVar = main["MainVariable"]!.GetValue<string>();
        var splitter = OptionsStartPoint(main, mainVar);
        var texts = langs.ToDictionary(l => l, _ => new JsonObject());
        var subId = Id($"{dialogueId}|accept|sub");
        foreach (var l in langs)
        {
            var text = loc?[l]?[textKey]?.GetValue<string>();
            if (string.IsNullOrEmpty(text)) text = loc?["en"]?[textKey]?.GetValue<string>();
            if (!string.IsNullOrEmpty(text)) texts[l][subId] = text;
        }
        var lines = new JsonArray();
        lines.Add(Line(Id($"{dialogueId}|accept|line"), "Player", "QuestIcon", subId,
            actions: new JsonArray(
                new JsonObject { ["type"] = "AcceptQuest", ["questId"] = qid, ["needNotification"] = true, ["id"] = Id($"{dialogueId}|accept|act") },
                SetVar(dialogueId, 1, Id($"{dialogueId}|accept|set"))),
            conds: new JsonArray(
                new JsonObject { ["variableId"] = dialogueId, ["value"] = 0, ["operator"] = "==", ["type"] = "VariableValue" },
                new JsonObject { ["QuestId"] = qid, ["Status"] = new JsonArray("AvailableForStart"), ["type"] = "QuestStatus" })));
        var ackId = Id($"{dialogueId}|accept|ack");
        foreach (var l in langs) texts[l][ackId] = "…";
        var back = new JsonObject { ["dialogId"] = mainId, ["type"] = "SwitchDialog", ["needNotification"] = false, ["id"] = Id($"{dialogueId}|accept|back") };
        if (splitter != null) back["splitterNodeId"] = splitter;
        lines.Add(Line(Id($"{dialogueId}|accept|ackline"), "Npc", "DialogBubble", ackId,
            actions: new JsonArray(back, SetVar(dialogueId, 2, Id($"{dialogueId}|accept|set2"))),
            conds: new JsonArray(new JsonObject { ["variableId"] = dialogueId, ["value"] = 1, ["operator"] = "==", ["type"] = "VariableValue" })));
        var localization = new JsonObject();
        foreach (var (l, t) in texts) localization[l] = t;
        outElements.Add(new JsonObject
        {
            ["Id"] = dialogueId, ["IsStart"] = false, ["MainVariable"] = dialogueId, ["Trader"] = traderId, ["SubTraders"] = new JsonArray(),
            ["Lines"] = lines, ["StartPoints"] = new JsonObject(), ["localization"] = localization,
        });
        have.Add(dialogueId);
        var vx = q["visitapi"] as JsonObject ?? (JsonObject)(q["visitapi"] = new JsonObject());
        vx["autoStart"] = false;
        made++;
        Console.WriteLine($"  接取对话 {dialogueId} ← 任务 {qid}（{traderId}）：台词键 {textKey}「{texts.GetValueOrDefault("en")?[subId]?.GetValue<string>()}」，回主对话 {mainId}（起点 {(splitter ?? "无")}）；autoStart 关");
    }
    var opts = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    File.WriteAllText(outFile, outRoot.ToJsonString(opts), utf8);
    File.WriteAllText(questsFile, quests.ToJsonString(opts), utf8);
    Console.WriteLine($"补了 {made} 份接取对话 → {outFile}；任务文件 {questsFile}（原件 {Path.GetFileName(bak)}）");

    // 接取选项的文案键：任务名 → 原件里 index 最小且无 parentId 的顶层目标 → 描述；文案必须在内嵌本地化里非空
    static string? AcceptTextKey(string qid, JsonObject q, JsonObject? original, JsonObject? loc)
    {
        bool Has(string key) => loc?["en"]?[key]?.GetValue<string>() is { Length: > 0 } || loc?["ch"]?[key]?.GetValue<string>() is { Length: > 0 };
        if (Has($"{qid} name")) return $"{qid} name";
        var conds = ((original ?? q)["conditions"]?["AvailableForFinish"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Where(c => string.IsNullOrEmpty(c["parentId"]?.GetValue<string>()))
            .OrderBy(c => c["index"]?.GetValue<double>() ?? 0)
            .Select(c => c["id"]?.GetValue<string>()).Where(id => id != null);
        foreach (var id in conds) if (Has(id!)) return id;
        return Has($"{qid} description") ? $"{qid} description" : null;
    }

    static JsonObject Line(string id, string side, string icon, string subId, JsonArray actions, JsonArray conds) => new()
    {
        ["Id"] = id, ["DialogSide"] = side, ["IconType"] = icon, ["ConfirmationKey"] = null,
        ["AnimationData"] = new JsonObject
        {
            ["animations"] = new JsonArray(), ["secondaryAnimations"] = new JsonArray(), ["lipSyncs"] = new JsonArray(),
            ["subtitles"] = new JsonArray(new JsonObject { ["id"] = subId, ["start"] = 0.0, ["end"] = 1.0 }),
            ["media"] = new JsonObject { ["image"] = null, ["music"] = null, ["sound"] = null },
        },
        ["Actions"] = actions,
        ["Trigger"] = new JsonObject { ["type"] = "MainLogicalGroup", ["Conditions"] = new JsonArray(new JsonObject { ["type"] = "LogicalSubGroup", ["Conditions"] = conds }) },
        ["TraderId"] = null,
    };

    static JsonObject SetVar(string variableId, int value, string id) =>
        new() { ["variableId"] = variableId, ["value"] = value, ["saveScope"] = "Dialogue", ["type"] = "SetVariable", ["id"] = id };

    static string? OptionsStartPoint(JsonObject main, string mainVar)
    {
        var line = (main["Lines"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(l => (l["Actions"] as JsonArray)?.OfType<JsonObject>().Any(a => a["type"]?.GetValue<string>() == "SwitchQuestDialog") == true);
        var want = line?["Trigger"]?["Conditions"]?[0]?["Conditions"]?.AsArray().OfType<JsonObject>()
            .FirstOrDefault(c => c["type"]?.GetValue<string>() == "VariableValue" && c["variableId"]?.GetValue<string>() == mainVar)?["value"]?.GetValue<int>();
        if (want == null || main["StartPoints"] is not JsonObject sp) return null;
        return sp.FirstOrDefault(kv => kv.Value?.GetValue<int>() == want).Key;
    }

    static string Id(string seed)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes("visitapi.acceptdlg|" + seed));
        return Convert.ToHexString(hash).ToLowerInvariant().Substring(0, 24);
    }
}

/// <summary>给已经 adapt 过的任务文件补 `visitapi.unlockDialogue`：从 port 导出的原件里读 Success 桶的 TraderDialogueUnlock 奖励（adapt 已把它删了）。
///   QuestPort dialogueunlock &lt;quests.json&gt; &lt;port 导出的原件.json&gt;
/// 就地改写，先存 .bak-时间。塔科夫之旅：6895bbb0 → Ragman+Therapist；6895bf08 → Skier；6895bf1e → Mechanic；6895bf42 → Prapor（2026-09-08）。</summary>
static void DialogueUnlock(string file, string exported)
{
    var utf8 = new System.Text.UTF8Encoding(false);
    var root = JsonNode.Parse(File.ReadAllText(file, utf8), documentOptions: new JsonDocumentOptions { MaxDepth = 256 })!.AsObject();
    var src = JsonNode.Parse(File.ReadAllText(exported, utf8), documentOptions: new JsonDocumentOptions { MaxDepth = 256 })!.AsObject();
    var bak = file + ".bak-" + BakStamp();
    File.Copy(file, bak, true);
    var n = 0;
    foreach (var (id, node) in src)
    {
        if (node is not JsonObject q || q["rewards"]?["Success"] is not JsonArray arr) continue;
        var traders = arr.OfType<JsonObject>()
            .Where(r => r["type"]?.GetValue<string>() == "TraderDialogueUnlock")
            .Select(r => r["target"]?.GetValue<string>()).Where(t => t is { Length: 24 }).Select(t => t!).ToList();
        if (traders.Count == 0) continue;
        if (root[id] is not JsonObject target) { Console.WriteLine($"  ⚠ {id} 不在目标文件里，跳过"); continue; }
        n += StampDialogueUnlock(target, id!, traders);
    }
    var opts = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    File.WriteAllText(file, root.ToJsonString(opts), utf8);
    Console.WriteLine($"unlockDialogue 写了 {n} 个任务 → {file}（原件 {Path.GetFileName(bak)}）");
}

/// <summary>给已经 adapt 过的任务文件补 `visitapi.unlockLocations`：从 port 导出的原件里读 Success 桶的 LocationUnlock 奖励（adapt 以前把它删了）。
///   QuestPort locationunlock &lt;quests.json&gt; &lt;port 导出的原件.json&gt;
/// 就地改写，先存 .bak-时间。塔科夫之旅：6895bbb0 → 立交桥；6895bed8 → 街区；6895bf08 → 海关；6895bf1e → 工厂白天/夜晚；6895bf42 → 森林；
/// 6895bf91 → 海岸线；6895bfa3 → 灯塔；6895bfd3 → 储备站；68c6aa0e → 实验室（2026-09-15）。</summary>
static void LocationUnlock(string file, string exported)
{
    var utf8 = new System.Text.UTF8Encoding(false);
    var root = JsonNode.Parse(File.ReadAllText(file, utf8), documentOptions: new JsonDocumentOptions { MaxDepth = 256 })!.AsObject();
    var src = JsonNode.Parse(File.ReadAllText(exported, utf8), documentOptions: new JsonDocumentOptions { MaxDepth = 256 })!.AsObject();
    var bak = file + ".bak-" + BakStamp();
    File.Copy(file, bak, true);
    var n = 0;
    foreach (var (id, node) in src)
    {
        if (node is not JsonObject q || q["rewards"]?["Success"] is not JsonArray arr) continue;
        var maps = arr.OfType<JsonObject>()
            .Where(r => r["type"]?.GetValue<string>() == "LocationUnlock")
            .Select(r => r["target"]?.GetValue<string>()).Where(t => t is { Length: 24 }).Select(t => t!).ToList();
        if (maps.Count == 0) continue;
        if (root[id] is not JsonObject target) { Console.WriteLine($"  ⚠ {id} 不在目标文件里，跳过"); continue; }
        n += StampList(target, id!, "unlockLocations", maps);
    }
    var opts = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    File.WriteAllText(file, root.ToJsonString(opts), utf8);
    Console.WriteLine($"unlockLocations 写了 {n} 个任务 → {file}（原件 {Path.GetFileName(bak)}）");
}

/// <summary>1.1 的日记表 main_quest_notes_list.json：每条 { id, chapterId, conditionIds, links: { items[{templateId, quests}],
/// offers[{traderId, item[{_tpl}], quests}], crafts[{area, item[{_tpl}], quests}] } }。
/// 日记正文和「哪条目标达成时解锁」任务 JSON 里本来就有（notes 槽 / 条件的 questNoteId，服务端直接下发），只有**相关物品**挂在这张表上——
/// 抄进日记所属任务的 visitapi.noteLinks[日记id] = [ { type: item|offer|craft, tpl, quests } ]。就地改写，先存 .bak。2026-09-07。</summary>
static void NoteLinks(string notesFile, string questFile, string chapterId)
{
    var utf8 = new System.Text.UTF8Encoding(false);
    using var notes = Load(notesFile);
    var root = JsonNode.Parse(File.ReadAllText(questFile, utf8), documentOptions: new JsonDocumentOptions { MaxDepth = 256 })!.AsObject();
    var bak = questFile + ".bak-" + BakStamp();
    File.Copy(questFile, bak, true);

    // 日记 → 所属任务：notes 槽里点名的，或某条目标 questNoteId 点名的；都没有就挂章节自己
    var owner = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var (id, node) in root)
    {
        if (node is not JsonObject q) continue;
        if (q["notes"] is JsonObject slots)
            foreach (var (_, v) in slots)
                if (v is JsonValue jv && jv.TryGetValue<string>(out var n) && n.Length == 24) owner.TryAdd(n, id!);
        if (q["conditions"] is JsonObject conds)
            foreach (var (_, list) in conds)
                if (list is JsonArray arr)
                    foreach (var c in arr)
                        if (c?["questNoteId"] is JsonValue cv && cv.TryGetValue<string>(out var n) && n.Length == 24) owner.TryAdd(n, id!);
    }

    int linked = 0, seen = 0;
    foreach (var n in notes.RootElement.EnumerateArray())
    {
        if (S(n, "chapterId") != chapterId) continue;
        seen++;
        var noteId = S(n, "id");
        var list = new JsonArray();
        if (n.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Object)
        {
            AddLinks(list, links, "items", "item", l => S(l, "templateId"));
            AddLinks(list, links, "offers", "offer", FirstTpl);
            AddLinks(list, links, "crafts", "craft", FirstTpl);
        }
        if (list.Count == 0) continue;
        var qid = owner.GetValueOrDefault(noteId, chapterId);
        if (root[qid] is not JsonObject q) { Console.WriteLine($"  ⚠ 日记 {noteId} 的任务 {qid} 不在文件里，跳过"); continue; }
        var vx = q["visitapi"] as JsonObject ?? (JsonObject)(q["visitapi"] = new JsonObject());
        var nl = vx["noteLinks"] as JsonObject ?? (JsonObject)(vx["noteLinks"] = new JsonObject());
        nl[noteId] = list; linked++;
        Console.WriteLine($"  {qid} 日记 {noteId}: {list.Count} 件 {list.ToJsonString()}");
    }

    var opts = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    File.WriteAllText(questFile, root.ToJsonString(opts), utf8);
    Console.WriteLine($"章节 {chapterId} 日记 {seen} 条，带物品的 {linked} 条 → {questFile}（原件 {Path.GetFileName(bak)}）");

    static void AddLinks(JsonArray list, JsonElement links, string prop, string type, Func<JsonElement, string> tpl)
    {
        if (!links.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return;
        foreach (var l in arr.EnumerateArray())
        {
            var t = tpl(l); if (t.Length != 24) continue;
            var quests = new JsonArray();
            if (l.TryGetProperty("quests", out var qs) && qs.ValueKind == JsonValueKind.Array)
                foreach (var x in qs.EnumerateArray()) if (x.ValueKind == JsonValueKind.String) quests.Add(x.GetString());
            list.Add(new JsonObject { ["type"] = type, ["tpl"] = t, ["quests"] = quests });
        }
    }

    // 报价/配方的 item 是 FlatItem 数组，第一件就是那件成品/商品
    static string FirstTpl(JsonElement l) =>
        l.TryGetProperty("item", out var it) && it.ValueKind == JsonValueKind.Array && it.GetArrayLength() > 0 ? S(it[0], "_tpl") : "";
}

/// <summary>任意 JSON 的字段路径全表：每条路径出现几次、都是什么类型。用来对两代格式的差。</summary>
static void Schema(string file, int maxDepth)
{
    using var doc = Load(file);
    var paths = new Dictionary<string, (int Count, SortedSet<string> Kinds)>(StringComparer.Ordinal);
    Walk(doc.RootElement, "", 0);
    Console.WriteLine("count\tkinds\tpath");
    foreach (var kv in paths.OrderByDescending(k => k.Value.Count))
        Console.WriteLine($"{kv.Value.Count}\t{string.Join('|', kv.Value.Kinds)}\t{kv.Key}");

    void Walk(JsonElement e, string path, int depth)
    {
        if (depth > maxDepth) return;
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    var here = path.Length == 0 ? p.Name : $"{path}.{p.Name}";
                    if (!paths.TryGetValue(here, out var slot)) paths[here] = slot = (0, new SortedSet<string>(StringComparer.Ordinal));
                    paths[here] = (slot.Count + 1, slot.Kinds);
                    slot.Kinds.Add(p.Value.ValueKind.ToString());
                    Walk(p.Value, here, depth + 1);
                }
                break;
            case JsonValueKind.Array:
                foreach (var i in e.EnumerateArray()) Walk(i, path + "[]", depth);
                break;
        }
    }
}

// ── 小工具 ──

static void Bump(Dictionary<string, int> d, string k)
{
    if (k.Length == 0) return;
    d[k] = d.GetValueOrDefault(k) + 1;
}

static void Tally(IEnumerable<JsonElement> items, Func<JsonElement, string> key, string title)
{
    var d = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (var i in items) Bump(d, key(i));
    Dump(title, d);
}

static void Dump(string title, Dictionary<string, int> d)
{
    Console.WriteLine($"\n[{title}]");
    foreach (var kv in d.OrderByDescending(k => k.Value)) Console.WriteLine($"  {kv.Value,6}  {kv.Key}");
}

// ── 2026-09-17 陨落星辰入库：闭包导出的文件和已入库的 Tour_1.1.json 有 8 条任务重叠（Tour 的闭包顺着 69029e0a 伸进了陨落星辰），
//    同一个 id 不能在两份文件里各登记一次（后一份 CreateQuest 报「已存在」）。搬家用这三条：
//    keep    <quests.json> <id,id,...>            只保留列出的任务（就地改写，先存 .bak）
//    drop    <quests.json> <id,id,...>            删掉列出的任务（就地改写，先存 .bak）
//    setflag <quests.json> <键> <true|false> <id,id,...>  改这些任务 visitapi 里的布尔开关（没有 visitapi 的先建）

static void Filter(string file, string ids, bool keep)
{
    var utf8 = new System.Text.UTF8Encoding(false);
    var root = JsonNode.Parse(File.ReadAllText(file, utf8), documentOptions: new JsonDocumentOptions { MaxDepth = 256 })!.AsObject();
    var bak = file + ".bak-" + BakStamp();
    File.Copy(file, bak, true);
    var set = ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
    var absent = set.Where(id => root[id] == null).ToList();
    var removed = new List<string>();
    foreach (var id in root.Select(kv => kv.Key).ToList())
        if (keep ? !set.Contains(id) : set.Contains(id)) { root.Remove(id); removed.Add(id); }
    var opts = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    File.WriteAllText(file, root.ToJsonString(opts), utf8);
    Console.WriteLine($"{(keep ? "keep" : "drop")}: 删掉 {removed.Count} 条 [{string.Join(", ", removed)}]，剩 {root.Count} 条 → {file}（原件 {Path.GetFileName(bak)}）");
    if (absent.Count > 0) Console.WriteLine($"  ⚠ 列出但文件里没有的 id: {string.Join(", ", absent)}");
}

/// <summary>09-17：对话文件里某条对话的字幕键在任何数据源里都没有文案（SPT5 的 dialogue.json 比它自己的 locale 新，1.1.5 新加的行），
/// 用**同一段对话里**平行分支的原文补上（20 语言各取各的），不编句子。
///   QuestPort dlgcopytext &lt;dialogue.json&gt; &lt;对话id&gt; &lt;缺键=来源键,缺键=来源键,...&gt;</summary>
static void DialogueCopyText(string file, string elementId, string pairs)
{
    var utf8 = new System.Text.UTF8Encoding(false);
    var root = JsonNode.Parse(File.ReadAllText(file, utf8), documentOptions: new JsonDocumentOptions { MaxDepth = 256 })!.AsObject();
    var bak = file + ".bak-" + BakStamp();
    File.Copy(file, bak, true);
    var element = (root["elements"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(e => e["Id"]?.GetValue<string>() == elementId)
                  ?? throw new ArgumentException($"文件里没有对话 {elementId}");
    if (element["localization"] is not JsonObject loc) throw new ArgumentException($"对话 {elementId} 没有 localization");
    var map = pairs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(p => p.Split('=')).Where(p => p.Length == 2).Select(p => (to: p[0].Trim(), from: p[1].Trim())).ToList();
    var n = 0;
    foreach (var (lang, table) in loc)
    {
        if (table is not JsonObject t) continue;
        foreach (var (to, from) in map)
        {
            var src = t[from]?.GetValue<string>();
            if (string.IsNullOrEmpty(src)) { Console.WriteLine($"  ⚠ {lang}: 来源键 {from} 没有文案，跳过"); continue; }
            var was = t[to]?.GetValue<string>();
            if (!string.IsNullOrEmpty(was)) { Console.WriteLine($"  {lang}: {to} 已有文案，不动"); continue; }
            t[to] = src; n++;
            if (lang is "ch" or "en") Console.WriteLine($"  {lang}: {to} ← {from}: {src}");
        }
    }
    var opts = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    File.WriteAllText(file, root.ToJsonString(opts), utf8);
    Console.WriteLine($"dlgcopytext {elementId}：补 {n} 条文案 → {file}（原件 {Path.GetFileName(bak)}）");
}

static void SetFlag(string file, string key, string value, string ids)
{
    var utf8 = new System.Text.UTF8Encoding(false);
    var root = JsonNode.Parse(File.ReadAllText(file, utf8), documentOptions: new JsonDocumentOptions { MaxDepth = 256 })!.AsObject();
    var bak = file + ".bak-" + BakStamp();
    File.Copy(file, bak, true);
    // 值是 true/false 就当布尔，纯数字当整数（order 之类），否则当字符串（startAfter 之类的任务 id；24 位纯数字 id 超出 int 也会落到字符串）
    JsonNode Val() => bool.TryParse(value, out var b) ? JsonValue.Create(b) : int.TryParse(value, out var n) ? JsonValue.Create(n) : JsonValue.Create(value);
    var n = 0;
    foreach (var id in ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (root[id] is not JsonObject q) { Console.WriteLine($"  ⚠ {id} 不在文件里，跳过"); continue; }
        var vo = q["visitapi"] as JsonObject ?? (JsonObject)(q["visitapi"] = new JsonObject());
        var was = vo[key]?.ToJsonString() ?? "（无）";
        vo[key] = Val(); n++;
        Console.WriteLine($"  {id} visitapi.{key}: {was} → {vo[key]!.ToJsonString()}");
    }
    var opts = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    File.WriteAllText(file, root.ToJsonString(opts), utf8);
    Console.WriteLine($"setflag {key}={value}：改了 {n} 条 → {file}（原件 {Path.GetFileName(bak)}）");
}

/// <summary>09-24：从一份文案表里挑出任务文件用得上的键另存（见文件头 locfill）：键开头的 24 位 id 在任务文件正文里出现过（任务 / 条件 / 日记 id），
/// 且不是物品 / 商人 / 地图那类键（" Name" " ShortName" " Description" " Nickname" " FirstName" " FullName" " Location" 结尾——任务正文里也有物品和地图 id，
/// 那些键并进包会盖掉本机原版的物品名）。「&lt;noteId&gt; questNoteText」同 port 一样再给一份裸 noteId 键。表可以是扁平的也可以是嵌套的，都按字符串叶子收。</summary>
static void LocaleFill(string src, string questsFile, string outFile)
{
    var utf8 = new System.Text.UTF8Encoding(false);
    var ids = new HashSet<string>(StringComparer.Ordinal);
    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(questsFile, utf8), "[0-9a-f]{24}")) ids.Add(m.Value);
    string[] foreignSuffixes = { " Name", " ShortName", " Description", " Nickname", " FirstName", " FullName", " Location" };
    var picked = new SortedDictionary<string, string>(StringComparer.Ordinal);
    var total = 0;
    using (var d = Load(src)) Walk(d.RootElement);
    var opts = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
    File.WriteAllText(outFile, JsonSerializer.Serialize(picked, opts), utf8);
    Console.WriteLine($"locfill: {Path.GetFileName(src)} 共 {total} 键，任务文件里的 id {ids.Count} 个，挑出 {picked.Count} 键 → {outFile}");

    void Walk(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
            foreach (var p in e.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.String) { Walk(p.Value); continue; }
                total++;
                var k = p.Name;
                if (k.Length < 24 || !System.Text.RegularExpressions.Regex.IsMatch(k, "^[0-9a-f]{24}") || !ids.Contains(k.Substring(0, 24))) continue;
                if (foreignSuffixes.Any(s => k.EndsWith(s, StringComparison.Ordinal))) continue;
                var text = p.Value.GetString()!;
                if (text.Length == 0) continue;
                picked[k] = text;
                const string noteSuffix = " questNoteText";
                if (k.EndsWith(noteSuffix, StringComparison.Ordinal)) picked.TryAdd(k[..^noteSuffix.Length], text);
            }
        else if (e.ValueKind == JsonValueKind.Array)
            foreach (var i in e.EnumerateArray()) Walk(i);
    }
}

/// <summary>09-24：按任务文件把要用的对话从抓包原样抽出来（见文件头 dlgexport）。闭包按元素正文里出现的对话 id 传递（SwitchDialog 等），
/// packs 里已有的元素不再输出，只在日志里数一下。</summary>
static void DialogueExport(string root, string questsFile, string outFile, string packsDir, string extraTraders)
{
    var utf8 = new System.Text.UTF8Encoding(false);
    using var dlg = Dialogues(root);
    var elements = dlg.RootElement.GetProperty("elements").EnumerateArray().ToDictionary(e => S(e, "Id"), e => e, StringComparer.Ordinal);
    var have = new HashSet<string>(StringComparer.Ordinal);
    if (packsDir.Length > 0 && Directory.Exists(packsDir))
        foreach (var pack in Directory.GetDirectories(packsDir))
            foreach (var f in Directory.Exists(Path.Combine(pack, "dialogues")) ? Directory.GetFiles(Path.Combine(pack, "dialogues"), "*.json") : Array.Empty<string>())
                using (var d = Load(f))
                    if (d.RootElement.TryGetProperty("elements", out var els) && els.ValueKind == JsonValueKind.Array)
                        foreach (var e in els.EnumerateArray()) { var id = S(e, "Id"); if (id.Length > 0) have.Add(id); }
    var want = new List<string>();
    var seen = new HashSet<string>(StringComparer.Ordinal);
    void Want(string id, string why)
    {
        if (id.Length != 24 || !seen.Add(id)) return;
        if (!elements.ContainsKey(id)) { Console.WriteLine($"  ⚠ 对话 {id} 抓包里没有（{why}）"); return; }
        want.Add(id);
    }
    using var q = Load(questsFile);
    foreach (var p in q.RootElement.EnumerateObject())
    {
        var d = S(p.Value, "dialogueId");
        if (d.Length > 0) Want(d, $"任务 {p.Name} 的 dialogueId");
        if (p.Value.TryGetProperty("mailSettings", out var ms) && ms.ValueKind == JsonValueKind.Object)
        {
            var md = S(ms, "dialogueId");
            if (md.Length > 0) Want(md, $"任务 {p.Name} 的邀请信 dialogueId");
        }
    }
    foreach (var t in extraTraders.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        foreach (var (id, e) in elements)
            if (S(e, "Trader") == t) Want(id, $"商人 {t} 的对话");
    for (var i = 0; i < want.Count; i++)
    {
        var raw = elements[want[i]].GetRawText();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(raw, "[0-9a-f]{24}"))
            if (m.Value != want[i] && elements.ContainsKey(m.Value)) Want(m.Value, $"对话 {want[i]} 引用");
    }
    var accept = new List<string>();
    foreach (var p in q.RootElement.EnumerateObject())
    {
        var d = S(p.Value, "dialogueId");
        if (d.Length == 24 && elements.TryGetValue(d, out var e) && e.GetRawText().Contains("\"AcceptQuest\"", StringComparison.Ordinal)) accept.Add(p.Name);
    }
    var arr = new JsonArray();
    var skipped = 0;
    foreach (var id in want)
    {
        if (have.Contains(id)) { skipped++; continue; }
        arr.Add(JsonNode.Parse(elements[id].GetRawText()));
    }
    var outRoot = new JsonObject { ["elements"] = arr };
    var opts = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
    File.WriteAllText(outFile, outRoot.ToJsonString(opts), utf8);
    var byTrader = want.Where(id => !have.Contains(id)).GroupBy(id => S(elements[id], "Trader")).OrderByDescending(g => g.Count());
    Console.WriteLine($"对话 {arr.Count} 个 → {outFile}（闭包共 {want.Count} 个，packs 里已有 {skipped} 个跳过）");
    foreach (var g in byTrader) Console.WriteLine($"  商人 {g.Key}: {g.Count()} 个");
    Console.WriteLine($"对话里带 AcceptQuest 的任务 {accept.Count} 条（adapt 的 autoStart 应关掉）: {string.Join(",", accept)}");
}

/// <summary>09-24：任务用到而本机 4.1 没有的物品，从 SPT5 抽成内容包格式（见文件头 items）。</summary>
static void ItemsExport(string spt5Db, string sptDb, string questsFile, string outDir, string name, string packsDir, string client11)
{
    var utf8 = new System.Text.UTF8Encoding(false);
    var items41 = TopKeys(Path.Combine(sptDb, "templates", "items.json"));
    // packs 里别的文件已经带的物品（陨落星辰那 5 件）不再出：服务端同 id 撞车只认先来的，还会红字。自己上次的输出文件不算（重跑要能覆盖）
    var selfFile = Path.GetFullPath(Path.Combine(outDir, "items", name + ".json"));
    if (packsDir.Length > 0 && Directory.Exists(packsDir))
        foreach (var pack in Directory.GetDirectories(packsDir))
            foreach (var f in PackFiles(pack, "items"))
                if (!string.Equals(Path.GetFullPath(f), selfFile, StringComparison.OrdinalIgnoreCase)) items41.UnionWith(TopKeys(f));
    var hb41Items = new HashSet<string>(StringComparer.Ordinal);
    var hb41Cats = new HashSet<string>(StringComparer.Ordinal);
    using (var hb = Load(Path.Combine(sptDb, "templates", "handbook.json")))
    {
        foreach (var e in Arr(hb.RootElement, "Items")) hb41Items.Add(S(e, "Id"));
        foreach (var e in Arr(hb.RootElement, "Categories")) hb41Cats.Add(S(e, "Id"));
    }
    var clientRoot = Path.GetFullPath(Path.Combine(sptDb, "..", "..", "..", "EscapeFromTarkov_Data", "StreamingAssets", "Windows"));

    using var q = Load(questsFile);
    var itemUse = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
    var zoneUse = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
    var types = new Dictionary<string, int>(StringComparer.Ordinal);
    var mapUse = new Dictionary<string, int>(StringComparer.Ordinal);
    var mapMissing = new HashSet<string>(StringComparer.Ordinal);
    var maps = SptMaps(Path.Combine(sptDb, "locations"));
    foreach (var p in q.RootElement.EnumerateObject())
    {
        foreach (var bucket in new[] { "AvailableForStart", "AvailableForFinish", "Fail", "Started" })
            foreach (var c in Conds(p.Value, bucket)) ScanCond(c, p.Name, itemUse, zoneUse, types, mapUse, mapMissing, maps);
        if (p.Value.TryGetProperty("rewards", out var rw) && rw.ValueKind == JsonValueKind.Object)
            foreach (var rp in rw.EnumerateObject())
                foreach (var r in rp.Value.EnumerateArray())
                    foreach (var it in Arr(r, "items"))
                    {
                        var tpl = S(it, "_tpl");
                        if (tpl.Length == 24) Use(itemUse, tpl, $"奖励:{p.Name}");
                    }
        // 10-05：adapt 从 AutoStart 搬来的开章物品（visitapi.startOnItems）不在任何条件里，也要进物品表和刷新点
        if (p.Value.TryGetProperty("visitapi", out var vx) && vx.TryGetProperty("startOnItems", out var soi) && soi.ValueKind == JsonValueKind.Array)
            foreach (var t in soi.EnumerateArray())
                if (t.ValueKind == JsonValueKind.String && t.GetString()!.Length == 24) Use(itemUse, t.GetString()!, $"开章:{p.Name}");
    }
    var need = itemUse.Keys.Where(t => !items41.Contains(t)).OrderBy(x => x, StringComparer.Ordinal).ToList();
    Console.WriteLine($"任务用到物品 {itemUse.Count} 个，本机 4.1 没有 {need.Count} 个");
    if (need.Count == 0) return;

    using var items5 = Load(Path.Combine(spt5Db, "templates", "items.json"));
    using var hb5 = Load(Path.Combine(spt5Db, "templates", "handbook.json"));
    var hb5Items = Arr(hb5.RootElement, "Items").ToDictionary(e => S(e, "Id"), e => e, StringComparer.Ordinal);
    var langs = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
    foreach (var f in Directory.GetFiles(Path.Combine(spt5Db, "locales", "global"), "*.json"))
    {
        var lang = Path.GetFileNameWithoutExtension(f);
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var d = Load(f))
            foreach (var tpl in need)
                foreach (var suffix in new[] { " Name", " ShortName", " Description" })
                    if (d.RootElement.TryGetProperty(tpl + suffix, out var v) && v.ValueKind == JsonValueKind.String) table[tpl + suffix] = v.GetString()!;
        langs[lang] = table;
    }

    // 1.1 独有的父类 → 0.16 的替身（陨落星辰 #142 手工换过的同一对：可读字条类 → Info 节点，手册「Notes」→「Info items」）
    // 10-05：录音带类 6516b0f2… / 手册 65bcab68… 同样换成 Info / Info items（迷宫那盘「死去科学家的录音带」09-26 是手工这么换的）
    var parentMap = new Dictionary<string, string> { ["67a27459e3515dec4105927b"] = "5448ecbe4bdc2d60728b4568", ["6516b0f21e733a595c1016fb"] = "5448ecbe4bdc2d60728b4568" };
    var hbParentMap = new Dictionary<string, string> { ["67a23465e3515dec41059278"] = "5b47574386f77428ca22b33f", ["65bcab6877b93a04772ae2d8"] = "5b47574386f77428ca22b33f" };
    // 模型包（09-24）：包里 bundles.json 已登记的键 + 本机客户端自带的 = 有模型；其余从 1.1 客户端拷（武器 / 配件 / 弹药 / 狗牌除外，见文件头）
    string[] skipPrefixes = { "assets/content/weapons/", "assets/content/items/mods/", "assets/content/items/ammo/", "assets/content/items/barter/item_barter_dogtags" };
    var manifestFile = Path.Combine(outDir, "bundles.json");
    var manifestRoot = File.Exists(manifestFile) ? JsonNode.Parse(File.ReadAllText(manifestFile, utf8))!.AsObject() : new JsonObject();
    var manifestArr = manifestRoot["manifest"] as JsonArray ?? (JsonArray)(manifestRoot["manifest"] = new JsonArray());
    var packManifest = new HashSet<string>(StringComparer.Ordinal);
    foreach (var m in manifestArr) if (m?["key"]?.GetValue<string>() is { Length: > 0 } mk) packManifest.Add(mk);
    JsonDocument? manifest11 = client11.Length > 0 && File.Exists(Path.Combine(client11, "Windows.json")) ? Load(Path.Combine(client11, "Windows.json")) : null;
    if (client11.Length > 0 && manifest11 == null) Console.WriteLine($"  ⚠ {client11} 下没有 Windows.json，拷的模型包不带依赖表");
    var bundleOk = new HashSet<string>(StringComparer.Ordinal);
    var manifestChanged = false; var copied = 0; long copiedBytes = 0;
    var outItems = new JsonObject();
    var missingTpl = new List<string>();
    foreach (var tpl in need)
    {
        if (!items5.RootElement.TryGetProperty(tpl, out var tplEl)) { missingTpl.Add(tpl); continue; }
        var tplNode = JsonNode.Parse(tplEl.GetRawText())!.AsObject();
        var entry = new JsonObject { ["template"] = tplNode };
        var loc = new JsonObject();
        foreach (var (lang, table) in langs.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (!table.TryGetValue(tpl + " Name", out var n)) continue;
            loc[lang] = new JsonObject { ["Name"] = n, ["ShortName"] = table.GetValueOrDefault(tpl + " ShortName", n), ["Description"] = table.GetValueOrDefault(tpl + " Description", "") };
        }
        entry["locales"] = loc;
        var enName = langs.GetValueOrDefault("en")?.GetValueOrDefault(tpl + " Name");
        if (hb5Items.TryGetValue(tpl, out var h))
        {
            var parent = S(h, "ParentId");
            if (hbParentMap.TryGetValue(parent, out var hp)) { Console.WriteLine($"  换手册父类 {tpl} {enName}: {parent} → {hp}"); parent = hp; }
            var price = h.TryGetProperty("Price", out var pr) && pr.ValueKind == JsonValueKind.Number ? pr.GetDouble() : 0;
            entry["handbook"] = new JsonObject { ["parentId"] = parent, ["price"] = price };
            if (!hb41Cats.Contains(parent) && !hb41Items.Contains(parent)) Console.WriteLine($"  ⚠ {tpl} {enName}: 手册父类 {parent} 本机没有，要换");
        }
        else Console.WriteLine($"  ⚠ {tpl} {enName}: SPT5 手册里没有，不进手册");
        var parentTpl = S(tplEl, "_parent");
        if (parentMap.TryGetValue(parentTpl, out var mapped)) { Console.WriteLine($"  换物品父类 {tpl} {enName}: {parentTpl} → {mapped}"); tplNode["_parent"] = mapped; parentTpl = mapped; }
        if (parentTpl.Length > 0 && !items41.Contains(parentTpl)) Console.WriteLine($"  ⚠ {tpl} {enName}: 物品父类 {parentTpl} 本机没有，要换");
        var prefab = tplEl.TryGetProperty("_props", out var props) && props.TryGetProperty("Prefab", out var pf) ? S(pf, "path") : "";
        if (prefab.Length == 0 || !Directory.Exists(clientRoot) || File.Exists(Path.Combine(clientRoot, prefab.Replace('/', Path.DirectorySeparatorChar))) || packManifest.Contains(prefab))
            bundleOk.Add(tpl);
        else if (client11.Length > 0 && skipPrefixes.Any(s => prefab.StartsWith(s, StringComparison.Ordinal)))
            Console.WriteLine($"  模型包不拷（武器/配件/弹药/狗牌）: {tpl} {enName} → {prefab}");
        else if (client11.Length > 0 && File.Exists(Path.Combine(client11, prefab.Replace('/', Path.DirectorySeparatorChar))))
        {
            var src = Path.Combine(client11, prefab.Replace('/', Path.DirectorySeparatorChar));
            var dst = Path.Combine(outDir, "bundles", prefab.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, true);
            var deps = new JsonArray();
            var missingDeps = new List<string>();
            if (manifest11 != null && manifest11.RootElement.TryGetProperty(prefab, out var m11))
                foreach (var dep in Arr(m11, "Dependencies"))
                    if (dep.ValueKind == JsonValueKind.String)
                    {
                        deps.Add((JsonNode)dep.GetString()!);
                        if (!File.Exists(Path.Combine(clientRoot, dep.GetString()!.Replace('/', Path.DirectorySeparatorChar)))) missingDeps.Add(dep.GetString()!);
                    }
            manifestArr.Add(new JsonObject { ["key"] = prefab, ["dependencyKeys"] = deps });
            packManifest.Add(prefab); manifestChanged = true; bundleOk.Add(tpl); copiedBytes += new FileInfo(src).Length; copied++;
            Console.WriteLine($"  拷模型包 {tpl} {enName} → {prefab}（{new FileInfo(src).Length / 1024.0 / 1024.0:0.0} MB，依赖 {deps.Count}）" + (missingDeps.Count > 0 ? "  ⚠ 依赖本机没有: " + string.Join(", ", missingDeps) : ""));
        }
        else
            Console.WriteLine($"  模型包本机没有: {tpl} {enName} → {prefab}" + (client11.Length > 0 ? "（1.1 客户端里也没有）" : ""));
        outItems[tpl] = entry;
    }
    foreach (var m in missingTpl) Console.WriteLine($"  ⚠ {m} SPT5 物品表里也没有");

    var opts = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    Directory.CreateDirectory(Path.Combine(outDir, "items"));
    var itemsFile = Path.Combine(outDir, "items", name + ".json");
    File.WriteAllText(itemsFile, outItems.ToJsonString(opts), utf8);
    Console.WriteLine($"物品 {outItems.Count} 件 → {itemsFile}");
    if (manifestChanged)
    {
        File.WriteAllText(manifestFile, manifestRoot.ToJsonString(opts), utf8);
        Console.WriteLine($"模型包拷了 {copied} 个、{copiedBytes / 1024.0 / 1024.0:0.0} MB → {Path.Combine(outDir, "bundles")}，清单 {manifestArr.Count} 条 → {manifestFile}");
    }

    // 固定刷新点：只要本机有的地图（服务端遇到没有的地图会红字跳过），只要有模型的物品（没模型的物品在战局里刷出来，加载会出错）
    // 09-24 实测：SPT 4.1 的 locations 目录里 terminal / suburbs / town / privatearea 只是占位（base.json 的 Scene.path 为空），
    // 服务端 GetLocation 拿不到 → 红字「本机没有地图 'terminal'」。只算 Scene.path 非空的地图。
    var needSet = new HashSet<string>(need, StringComparer.Ordinal);
    var localMaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var locDir41 = Path.Combine(sptDb, "locations");
    if (Directory.Exists(locDir41))
        foreach (var d in Directory.GetDirectories(locDir41))
        {
            var b = Path.Combine(d, "base.json");
            if (!File.Exists(b)) continue;
            using var doc = Load(b);
            if (doc.RootElement.TryGetProperty("Scene", out var scene) && S(scene, "path").Length > 0) localMaps.Add(Path.GetFileName(d));
        }
    var loot = new JsonObject();
    var lootCount = 0;
    var lootSkippedMaps = new List<string>();
    var lootSkippedItems = new Dictionary<string, int>(StringComparer.Ordinal);
    var locDir = Path.Combine(spt5Db, "locations");
    if (Directory.Exists(locDir))
        foreach (var mapDir in Directory.GetDirectories(locDir).OrderBy(x => x, StringComparer.Ordinal))
        {
            var f = Path.Combine(mapDir, "looseLoot.json");
            if (!File.Exists(f)) continue;
            var mapName = Path.GetFileName(mapDir);
            using var d = Load(f);
            var points = new JsonArray();
            foreach (var sp in Arr(d.RootElement, "spawnpointsForced"))
            {
                if (!sp.TryGetProperty("template", out var t) || t.ValueKind != JsonValueKind.Object) continue;
                var tpls = Arr(t, "Items").Select(it => S(it, "_tpl")).Where(needSet.Contains).ToList();
                if (tpls.Count == 0) continue;
                if (localMaps.Count > 0 && !localMaps.Contains(mapName)) { if (!lootSkippedMaps.Contains(mapName)) lootSkippedMaps.Add(mapName); continue; }
                var noModel = tpls.Where(x => !bundleOk.Contains(x)).ToList();
                if (noModel.Count > 0) { foreach (var x in noModel) Bump(lootSkippedItems, x); continue; }
                points.Add(JsonNode.Parse(sp.GetRawText()));
            }
            if (points.Count > 0) { loot[mapName] = points; lootCount += points.Count; }
        }
    if (lootSkippedMaps.Count > 0) Console.WriteLine($"  刷新点跳过本机没有的地图: {string.Join(", ", lootSkippedMaps)}");
    foreach (var (x, c) in lootSkippedItems) Console.WriteLine($"  刷新点跳过没模型的物品 {x} {langs.GetValueOrDefault("en")?.GetValueOrDefault(x + " Name")}: {c} 处");
    if (lootCount > 0)
    {
        Directory.CreateDirectory(Path.Combine(outDir, "loot"));
        var lootFile = Path.Combine(outDir, "loot", name + ".json");
        File.WriteAllText(lootFile, loot.ToJsonString(opts), utf8);
        Console.WriteLine($"固定刷新点 {lootCount} 个（{string.Join(", ", loot.Select(kv => $"{kv.Key}×{((JsonArray)kv.Value!).Count}"))}）→ {lootFile}");
    }
    else Console.WriteLine("SPT5 各地图 looseLoot 里没有这些物品的固定刷新点");
}
