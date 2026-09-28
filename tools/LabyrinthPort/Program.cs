// Labyrinth port helper (2026-09-25). Commands:
//   merge  <chapterQuests.json> <starterQuests.json> <starterId> <out.json>   copy the starter quest node into the chapter file
//   flags  <quests.json>                                                       Labyrinth-specific visitapi flags after QuestPort adapt
//   wait   <quests.json> <seconds>                                             every Quest condition availableAfter > 0 -> seconds (prints originals)
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

var utf8 = new System.Text.UTF8Encoding(false);
var docOpts = new JsonDocumentOptions { MaxDepth = 256 };
var writeOpts = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
JsonObject Load(string f) => JsonNode.Parse(File.ReadAllText(f, utf8), documentOptions: docOpts)!.AsObject();
void Save(string f, JsonObject o) => File.WriteAllText(f, o.ToJsonString(writeOpts), utf8);

const string Chapter = "68e3a35002661eb2d30ce387";
const string Starter = "68e3a3abf1bccd83c50a5887";
const string Jaeger = "68e3a3d2ff05916c250a898b";

switch (args[0])
{
    case "merge":
    {
        var chapter = Load(args[1]);
        var starter = Load(args[2]);
        chapter[args[3]] = starter[args[3]]!.DeepClone();
        Save(args[4], chapter);
        Console.WriteLine($"merged {args[3]} -> {args[4]} ({chapter.Count} quests)");
        break;
    }
    case "flags":
    {
        var q = Load(args[1]);
        // chapter: 1.1 order 9 (getMainQuestsList), opens after the hidden starter (visit the Labyrinth entrance), same shape as Falling Skies
        var cv = q[Chapter]!["visitapi"]!.AsObject();
        cv["order"] = 9;
        cv["startAfter"] = new JsonArray(Starter);
        // Jaeger's quest is taken in his dialogue in 1.1 (dialogue 68e4d83a, now in the SPT5 data) — no auto accept
        var jv = q[Jaeger]!["visitapi"]!.AsObject();
        jv["autoStart"] = false;
        Save(args[1], q);
        Console.WriteLine($"chapter visitapi: {cv.ToJsonString()}");
        Console.WriteLine($"starter visitapi: {q[Starter]?["visitapi"]?.ToJsonString()}");
        Console.WriteLine($"jaeger visitapi: {jv.ToJsonString()}");
        break;
    }
    case "ticketdeps":
    {
        // ticketdeps <packDir> <extraFile...>  read-only: what of Ticket is still needed by the rest of the pack (+ extra files, e.g. the new Labyrinth files)
        var P = args[1];
        var ticketFiles = new[] { $@"{P}\quests\Ticket_1.1.json", $@"{P}\dialogues\ticket_1.1.json", $@"{P}\items\Ticket_1.1.json", $@"{P}\loot\Ticket_1.1.json" };
        var others = Directory.GetFiles(P, "*.json", SearchOption.AllDirectories)
            .Where(f => !ticketFiles.Contains(f, StringComparer.OrdinalIgnoreCase) && !f.Contains(@"\voice\") && !f.Contains(@"\bundles\")).ToList();
        others.AddRange(args.Skip(2));
        var rx = new System.Text.RegularExpressions.Regex(@"\b[0-9a-f]{24}\b");
        var otherIds = new HashSet<string>();
        foreach (var f in others) foreach (System.Text.RegularExpressions.Match m in rx.Matches(File.ReadAllText(f, utf8))) otherIds.Add(m.Value);
        // items: closure over template-internal references
        var items = Load(ticketFiles[2]);
        var keep = new HashSet<string>(items.Select(kv => kv.Key).Where(otherIds.Contains));
        var grew = true;
        while (grew)
        {
            grew = false;
            foreach (var id in keep.ToList())
                foreach (System.Text.RegularExpressions.Match m in rx.Matches(items[id]!.ToJsonString()))
                    if (m.Value != id && items.ContainsKey(m.Value) && keep.Add(m.Value)) grew = true;
        }
        Console.WriteLine($"items: {items.Count} in Ticket file, keep {keep.Count} (referenced elsewhere + their template closure)");
        foreach (var id in keep.OrderBy(x => x)) Console.WriteLine($"  keep item {id} {items[id]?["template"]?["_name"]} parent={items[id]?["template"]?["_parent"]}");
        File.WriteAllLines(Path.Combine(Environment.CurrentDirectory, "ticket_keep_items.txt"), keep.OrderBy(x => x));
        // dialogue elements in ticket_1.1.json referenced elsewhere
        var dlg = Load(ticketFiles[1]);
        var elems = (dlg["elements"] as JsonArray)!.OfType<JsonObject>().Select(e => e["Id"]!.GetValue<string>()).ToList();
        var dlgUsed = elems.Where(otherIds.Contains).ToList();
        Console.WriteLine($"dialogue elements: {elems.Count} in ticket file, referenced elsewhere: {dlgUsed.Count} {string.Join(",", dlgUsed)}");
        // quests referenced elsewhere
        var quests = Load(ticketFiles[0]);
        var qUsed = quests.Select(kv => kv.Key).Where(otherIds.Contains).ToList();
        Console.WriteLine($"quests: {quests.Count} in ticket file, referenced elsewhere: {qUsed.Count}");
        // traders: which pack traders are referenced by non-ticket files other than their own folder
        foreach (var dir in Directory.GetDirectories(Path.Combine(P, "traders")))
        {
            var tid = Path.GetFileName(dir);
            var users = others.Where(f => !f.StartsWith(dir, StringComparison.OrdinalIgnoreCase) && File.ReadAllText(f, utf8).Contains(tid)).Select(f => f.Replace(P + "\\", "")).ToList();
            var tUsers = ticketFiles.Where(f => File.ReadAllText(f, utf8).Contains(tid)).Select(Path.GetFileName).ToList();
            Console.WriteLine($"trader {tid}: used by non-ticket [{string.Join(", ", users)}]; by ticket [{string.Join(", ", tUsers)}]");
        }
        break;
    }
    case "ticketassets":
    {
        // ticketassets <packDir> <keepItemsFile>  read-only: bundles.json entries and voice files used only by Ticket content
        var P = args[1];
        var keep = new HashSet<string>(File.ReadAllLines(args[2]));
        var items = Load($@"{P}\items\Ticket_1.1.json");
        string Prefab(JsonNode it) => it?["template"]?["_props"]?["Prefab"]?["path"]?.GetValue<string>() ?? "";
        var deferPrefabs = items.Where(kv => !keep.Contains(kv.Key)).Select(kv => Prefab(kv.Value)).Where(p => p.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var keepPrefabs = items.Where(kv => keep.Contains(kv.Key)).Select(kv => Prefab(kv.Value)).Where(p => p.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // other pack item files' prefabs also count as kept
        foreach (var f in Directory.GetFiles($@"{P}\items", "*.json").Where(f => !f.EndsWith("Ticket_1.1.json")))
            foreach (var (_, it) in Load(f)) { var p = Prefab(it); if (p.Length > 0) keepPrefabs.Add(p); }
        var manifest = Load($@"{P}\bundles.json");
        var entries = (manifest["manifest"] as JsonArray)!.OfType<JsonObject>().ToList();
        Console.WriteLine($"bundles.json: {entries.Count} entries");
        foreach (var e in entries)
        {
            var key = e["key"]!.GetValue<string>();
            var deps = (e["dependencyKeys"] as JsonArray)?.Select(d => d!.GetValue<string>()).ToList() ?? new List<string>();
            var who = deferPrefabs.Contains(key) ? "TICKET-ONLY" : keepPrefabs.Contains(key) ? "kept item" : "other/dependency";
            Console.WriteLine($"  [{who}] {key}  deps={deps.Count}");
        }
        // voice: lipSyncIds / ids referenced by ticket dialogue vs other dialogue files
        var rx = new System.Text.RegularExpressions.Regex(@"\b[0-9a-f]{24}\b");
        HashSet<string> Ids(string f) { var h = new HashSet<string>(); foreach (System.Text.RegularExpressions.Match m in rx.Matches(File.ReadAllText(f, utf8))) h.Add(m.Value); return h; }
        var tIds = Ids($@"{P}\dialogues\ticket_1.1.json");
        var oIds = new HashSet<string>();
        foreach (var f in Directory.GetFiles($@"{P}\dialogues", "*.json").Where(f => !f.EndsWith("ticket_1.1.json"))) oIds.UnionWith(Ids(f));
        oIds.UnionWith(Ids(args.Length > 3 ? args[3] : $@"{P}\bundles.json"));
        var voice = Directory.GetFiles($@"{P}\voice", "*", SearchOption.AllDirectories);
        int tOnly = 0, other = 0, none = 0;
        var tList = new List<string>();
        // voice files are named by clip key (e.g. ABNI_a_vot_ustroistvo.mp3); match the key as a quoted string in the dialogue files
        var tText = File.ReadAllText($@"{P}\dialogues\ticket_1.1.json", utf8);
        var oText = string.Concat(Directory.GetFiles($@"{P}\dialogues", "*.json").Where(f => !f.EndsWith("ticket_1.1.json")).Select(f => File.ReadAllText(f, utf8)))
                    + (args.Length > 3 ? File.ReadAllText(args[3], utf8) : "");
        foreach (var v in voice)
        {
            var key = Path.GetFileNameWithoutExtension(v);
            var inT = tText.Contains("\"" + key + "\"");
            var inO = oText.Contains("\"" + key + "\"");
            if (inT && !inO) { tOnly++; tList.Add(v); } else if (inO) other++; else none++;
        }
        Console.WriteLine($"voice files: {voice.Length}; ticket-only {tOnly}; used by other dialogues {other}; referenced nowhere {none}");
        File.WriteAllLines(Path.Combine(Environment.CurrentDirectory, "ticket_voice.txt"), tList);
        break;
    }
    case "splititems":
    {
        // splititems <ticketItems.json> <keepList.txt> <sharedOut.json> <deferOut.json>
        var items = Load(args[1]);
        var keep = new HashSet<string>(File.ReadAllLines(args[2]).Where(l => l.Length == 24));
        var shared = new JsonObject(); var defer = new JsonObject();
        foreach (var (id, node) in items) (keep.Contains(id) ? shared : defer)[id] = node!.DeepClone();
        Save(args[3], shared); Save(args[4], defer);
        Console.WriteLine($"items: shared {shared.Count} -> {args[3]}; deferred {defer.Count} -> {args[4]}");
        break;
    }
    case "bundles":
    {
        // bundles <pack bundles.json> <deferKeys.txt> <add bundles.json> <deferOut bundles.json>
        var m = Load(args[1]);
        var arr = (m["manifest"] as JsonArray)!;
        var deferKeys = new HashSet<string>(File.ReadAllLines(args[2]).Where(l => l.Length > 0), StringComparer.OrdinalIgnoreCase);
        var deferred = new JsonArray();
        for (var i = arr.Count - 1; i >= 0; i--)
            if (deferKeys.Contains(arr[i]!["key"]!.GetValue<string>())) { var n = arr[i]!; arr.RemoveAt(i); deferred.Insert(0, n); }
        var have = arr.Select(e => e!["key"]!.GetValue<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var e in (Load(args[3])["manifest"] as JsonArray)!)
            if (have.Add(e!["key"]!.GetValue<string>())) { arr.Add(e.DeepClone()); added++; }
        Save(args[1], m);
        Save(args[4], new JsonObject { ["manifest"] = deferred });
        Console.WriteLine($"bundles.json: removed {deferred.Count} (-> {args[4]}), added {added}, now {arr.Count}");
        break;
    }
    case "locmerge":
    {
        // locmerge <pack locale.json> <new locale.json>   add keys that are missing, never change existing ones
        var pack = Load(args[1]); var add = Load(args[2]);
        var n = 0;
        foreach (var (k, v) in add) if (!pack.ContainsKey(k)) { pack[k] = v!.DeepClone(); n++; }
        Save(args[1], pack);
        Console.WriteLine($"{Path.GetFileName(args[1])}: +{n} keys (now {pack.Count})");
        break;
    }
    case "wait":
    {
        var q = Load(args[1]);
        var secs = int.Parse(args[2]);
        var n = 0;
        foreach (var (id, node) in q)
            foreach (var bucket in new[] { "AvailableForStart", "AvailableForFinish", "Fail" })
                foreach (var c in node?["conditions"]?[bucket] as JsonArray ?? new JsonArray())
                    if (c?["conditionType"]?.GetValue<string>() == "Quest" && c["availableAfter"] is JsonValue v && v.TryGetValue<int>(out var old) && old > 0 && old != secs)
                    {
                        Console.WriteLine($"{id} {bucket} cond {c["id"]} availableAfter {old} -> {secs}");
                        c["availableAfter"] = secs; n++;
                    }
        Save(args[1], q);
        Console.WriteLine($"{n} wait(s) changed");
        break;
    }
}
