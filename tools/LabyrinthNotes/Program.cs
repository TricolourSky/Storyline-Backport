// Labyrinth scientist notes + surveillance key helper (2026-09-26). Every command only adds; nothing existing is changed.
//   pick      <out.json> <portedQuests.json>...                 take each file's seed quest (file name = quest id) into one file
//   addnode   <file.json> <src.json> <id>                       copy one quest node from src into file (in place)
//   setvars   <adapted.json> <raw.json>                          1.1 GlobalVariable rewards (removed by adapt) -> visitapi.setVariables { bucket: { var: value } }
//   packmerge <pack.json> <new.json>                             append top-level entries of new.json that pack.json lacks (quests / items)
//   questitem <items.json>                                       template._props.QuestItem = true for every item in the file (same as the 09-25 Labyrinth notes)
//   lootadd   <packLoot.json> <map> <1.1 looseLoot.json> <forced|dynamic> <tpl,...>   append 1.1 spawn entries of these items verbatim (skip same Id + position)
//   locname   <locale.json> <sourceKey> <id,...>                 add "<id> name" = value of sourceKey where missing (text-level append, file otherwise byte-identical)
//   bundlesadd <pack bundles.json> <new bundles.json>            append manifest entries whose key the pack lacks
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

var utf8 = new System.Text.UTF8Encoding(false);
var docOpts = new JsonDocumentOptions { MaxDepth = 256 };
var writeOpts = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
JsonNode LoadNode(string f) => JsonNode.Parse(File.ReadAllText(f, utf8), documentOptions: docOpts)!;
JsonObject Load(string f) => LoadNode(f).AsObject();
void Save(string f, JsonNode o) => File.WriteAllText(f, o.ToJsonString(writeOpts), utf8);
string Stamp() => DateTime.Now.ToString("MMdd-HHmmss");
// VISIT_BAK set = the caller already backed the pack up elsewhere (no .bak files inside the pack, they would ship with the release)
void Bak(string f) { if (Environment.GetEnvironmentVariable("VISIT_BAK") is { Length: > 0 }) return; var b = f + ".bak-" + Stamp(); File.Copy(f, b, true); Console.WriteLine($"  backup {Path.GetFileName(b)}"); }

switch (args[0])
{
    case "pick":
    {
        var outObj = new JsonObject();
        foreach (var f in args.Skip(2))
        {
            var id = Path.GetFileNameWithoutExtension(f);
            var src = Load(f);
            outObj[id] = src[id]!.DeepClone();
            Console.WriteLine($"  picked {id} from {Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(f)))}");
        }
        Save(args[1], outObj);
        Console.WriteLine($"pick: {outObj.Count} quest(s) -> {args[1]}");
        break;
    }
    case "addnode":
    {
        var file = Load(args[1]); var src = Load(args[2]);
        if (file.ContainsKey(args[3])) { Console.WriteLine($"addnode: {args[3]} already in {args[1]}"); break; }
        file[args[3]] = src[args[3]]!.DeepClone();
        Save(args[1], file);
        Console.WriteLine($"addnode: {args[3]} -> {args[1]} ({file.Count} entries)");
        break;
    }
    case "setvars":
    {
        var q = Load(args[1]); var raw = Load(args[2]);
        Bak(args[1]);
        foreach (var (id, node) in q)
        {
            if (raw[id]?["rewards"] is not JsonObject rewards) continue;
            JsonObject sv = null;
            foreach (var (bucket, list) in rewards)
                foreach (var r in list as JsonArray ?? new JsonArray())
                {
                    if (r?["type"]?.GetValue<string>() != "GlobalVariable") continue;
                    var target = r["target"]!.GetValue<string>();
                    var value = r["value"]!.GetValue<int>();
                    sv ??= new JsonObject();
                    var b = sv[bucket] as JsonObject ?? (JsonObject)(sv[bucket] = new JsonObject());
                    b[target] = value;
                }
            if (sv == null) continue;
            var vx = node!["visitapi"] as JsonObject ?? (JsonObject)(node["visitapi"] = new JsonObject());
            if (vx.ContainsKey("setVariables")) { Console.WriteLine($"  {id}: already has setVariables, left alone"); continue; }
            vx["setVariables"] = sv;
            Console.WriteLine($"  {id}: visitapi = {vx.ToJsonString()}");
        }
        Save(args[1], q);
        break;
    }
    case "packmerge":
    {
        var oldText = File.ReadAllText(args[1], utf8);
        var pack = Load(args[1]); var add = Load(args[2]);
        Bak(args[1]);
        var n = 0;
        foreach (var (id, node) in add)
        {
            if (pack.ContainsKey(id)) { Console.WriteLine($"  {id}: already in pack, skipped"); continue; }
            pack[id] = node!.DeepClone(); n++;
        }
        Save(args[1], pack);
        var newText = File.ReadAllText(args[1], utf8);
        // the untouched part must come back byte-identical: everything before the old closing brace is a prefix of the new text
        var cut = oldText.TrimEnd().LastIndexOf('}');
        var prefixSame = newText.StartsWith(oldText.Substring(0, cut).TrimEnd(), StringComparison.Ordinal);
        Console.WriteLine($"packmerge: +{n} -> {Path.GetFileName(args[1])} ({pack.Count} entries); old content byte-identical prefix: {prefixSame}");
        break;
    }
    case "questitem":
    {
        var items = Load(args[1]);
        foreach (var (id, e) in items)
        {
            var props = e!["template"]!["_props"]!.AsObject();
            var was = props["QuestItem"]?.ToJsonString() ?? "(none)";
            props["QuestItem"] = true;
            Console.WriteLine($"  {id} {e["template"]!["_name"]}: QuestItem {was} -> true, parent {e["template"]!["_parent"]}");
        }
        Save(args[1], items);
        break;
    }
    case "lootadd":
    {
        var packFile = args[1]; var map = args[2];
        var src = LoadNode(args[3]);
        var list = args[4] == "forced" ? "spawnpointsForced" : "spawnpoints";
        var tpls = args[5].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
        var pack = File.Exists(packFile) ? Load(packFile) : new JsonObject();
        if (File.Exists(packFile)) Bak(packFile);
        var arr = pack[map] as JsonArray ?? (JsonArray)(pack[map] = new JsonArray());
        string Key(JsonNode sp) => sp["template"]!["Id"]!.GetValue<string>() + "|" + sp["template"]!["Position"]!.ToJsonString();
        var have = arr.Select(x => Key(x!)).ToHashSet();
        var n = 0;
        foreach (var sp in src[list]!.AsArray())
        {
            var tpl = sp!["template"]?["Items"]?[0]?["_tpl"]?.GetValue<string>();
            if (tpl == null || !tpls.Contains(tpl)) continue;
            if (!have.Add(Key(sp))) { Console.WriteLine($"  {tpl} at {sp["template"]!["Position"]!.ToJsonString()}: already there"); continue; }
            arr.Add(sp.DeepClone()); n++;
            Console.WriteLine($"  + {tpl} p={sp["probability"]} pos={sp["template"]!["Position"]!.ToJsonString()} id='{sp["template"]!["Id"]}' always={sp["template"]!["IsAlwaysSpawn"]}");
        }
        Save(packFile, pack);
        Console.WriteLine($"lootadd: +{n} from 1.1 {list} -> {Path.GetFileName(packFile)} [{map}] ({arr.Count} entries)");
        break;
    }
    case "locname":
    {
        var file = args[1];
        var text = File.ReadAllText(file, utf8);
        var loc = Load(file);
        var value = loc[args[2]]?.GetValue<string>() ?? throw new ArgumentException($"{file}: no key {args[2]}");
        var add = args[3].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(id => !loc.ContainsKey(id + " name")).ToList();
        if (add.Count == 0) { Console.WriteLine($"locname {Path.GetFileName(file)}: nothing to add"); break; }
        Bak(file);
        var close = text.TrimEnd().LastIndexOf('}');
        var body = text.Substring(0, close).TrimEnd();
        var sb = new System.Text.StringBuilder(body);
        foreach (var id in add) sb.Append(",\n  ").Append(JsonSerializer.Serialize(id + " name", writeOpts)).Append(": ").Append(JsonSerializer.Serialize(value, writeOpts));
        sb.Append('\n').Append(text.Substring(close));
        File.WriteAllText(file, sb.ToString(), utf8);
        var check = Load(file);
        var ok = check.Count == loc.Count + add.Count && loc.All(kv => check[kv.Key]?.GetValue<string>() == kv.Value!.GetValue<string>());
        Console.WriteLine($"locname {Path.GetFileName(file)}: +{add.Count} \"<id> name\" = {value}; parse ok, old keys unchanged: {ok}");
        break;
    }
    case "bundlesadd":
    {
        var pack = Load(args[1]); var add = Load(args[2]);
        var arr = pack["manifest"]!.AsArray();
        var have = arr.Select(e => e!["key"]!.GetValue<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Bak(args[1]);
        var n = 0;
        foreach (var e in add["manifest"]!.AsArray())
            if (have.Add(e!["key"]!.GetValue<string>())) { arr.Add(e.DeepClone()); n++; Console.WriteLine($"  + {e["key"]} deps={e["dependencyKeys"]?.AsArray().Count}"); }
        Save(args[1], pack);
        Console.WriteLine($"bundlesadd: +{n} -> {args[1]} ({arr.Count} entries)");
        break;
    }
    default: Console.Error.WriteLine("unknown command"); return 2;
}
return 0;
