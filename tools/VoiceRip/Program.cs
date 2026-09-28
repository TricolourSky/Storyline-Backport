using System.Text.Json;
using System.Text.RegularExpressions;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using OggVorbisEncoder;

// VoiceRip ids  <out.txt> <traderId> <dialogue.json>...          从对话文件里收集该商人台词用到的 lipSyncId（去重）
// VoiceRip list <classdata.tpk> <bundle> [名字正则]               列出 bundle 里的 AudioClip（名字、格式、时长、来源），顺带报告有没有类型树、MonoBehaviour 数
// VoiceRip rip  <classdata.tpk> <bundle> <outDir> <ids.txt|正则>  把名字命中的 AudioClip 解成 .ogg/.wav 存到 outDir
// VoiceRip dump <classdata.tpk> <bundle> <脚本类名正则> [深度]       打印命中脚本类名的 MonoBehaviour 字段树（看 TraderLipSyncSO 长什么样）
// VoiceRip ripso <classdata.tpk> <bundle> <outDir> <ids.txt>        顺着 TraderLipSyncSO → 口型字典 → BakedData → AudioClip 把清单里的 lipSyncId 对应的语音抠出来（AudioClip 名字和 lipSyncId 不一样，list 找不到就走这条）
if (args.Length == 0) { Console.WriteLine("用法见源文件头"); return 1; }
switch (args[0].ToLowerInvariant())
{
    case "ids": return Ids(args[1], args[2], args.Skip(3).ToArray());
    case "list": return Rip(args[1], args[2], null, args.Length > 3 ? args[3] : null);
    case "rip": return Rip(args[1], args[2], args[3], args[4]);
    case "dump": return Dump(args[1], args[2], args[3], args.Length > 4 ? int.Parse(args[4]) : 4);
    case "ripso": return RipSo(args[1], args[2], args[3], args[4], args.Length > 5 ? int.Parse(args[5]) : -1);
    case "ogg": return Ogg(args[1], args[2], args.Length > 3 ? float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 0.4f);
    case "check": return Check(args[1]);
    case "selftest": return SelfTest(args.Length > 1 ? args[1] : null);
    case "matrix": return Matrix();
    case "mp3": return Mp3(args[1], args[2], args.Length > 3 ? int.Parse(args[3]) : 64);
    case "dlg": return Dlg(args[1], args[2], args.Length > 3 ? args[3] : "ch", args.Length > 4 ? int.Parse(args[4]) : 2);
    case "find": return FindLine(args[1], args[2], args.Length > 3 ? args[3] : "ch");
    default: Console.WriteLine("未知命令 " + args[0]); return 1;
}

// VoiceRip find <dialogue.json> <lipSyncId 或文案片段> [语言=ch]   找含这个口型键 / 文案的台词：所在元素、说话方、文案、条件、动作
static int FindLine(string file, string needle, string lang)
{
    using var doc = JsonDocument.Parse(File.ReadAllText(file));
    var hits = 0;
    foreach (var el in doc.RootElement.GetProperty("elements").EnumerateArray())
    {
        var loc = el.TryGetProperty("localization", out var l) && l.ValueKind == JsonValueKind.Object && l.TryGetProperty(lang, out var t) ? t : default;
        string T(string? key) => key != null && loc.ValueKind == JsonValueKind.Object && loc.TryGetProperty(key, out var v) ? v.GetString() ?? key : key ?? "";
        foreach (var line in el.GetProperty("Lines").EnumerateArray())
        {
            var ad = line.TryGetProperty("AnimationData", out var ad0) && ad0.ValueKind == JsonValueKind.Object ? ad0 : default;
            var lips = ad.ValueKind == JsonValueKind.Object && ad.TryGetProperty("lipSyncs", out var ls) && ls.ValueKind == JsonValueKind.Array ? ls.EnumerateArray().Select(x => x.GetProperty("lipSyncId").GetString() ?? "").ToList() : new List<string>();
            var subs = ad.ValueKind == JsonValueKind.Object && ad.TryGetProperty("subtitles", out var ss) && ss.ValueKind == JsonValueKind.Array ? ss.EnumerateArray().Select(x => T(x.GetProperty("id").GetString())).ToList() : new List<string>();
            var text = subs.Count > 0 ? string.Join(" / ", subs) : T(line.GetProperty("Id").GetString());
            if (!lips.Any(x => x.Equals(needle, StringComparison.OrdinalIgnoreCase)) && !text.Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;
            hits++;
            Console.WriteLine($"=== 元素 {el.GetProperty("Id").GetString()}（IsStart={(el.TryGetProperty("IsStart", out var s) && s.GetBoolean())}，商人 {el.GetProperty("Trader").GetString()?.Substring(0, 8)}）台词 {line.GetProperty("Id").GetString()}");
            Console.WriteLine($"  [{line.GetProperty("DialogSide").GetString()}] {(line.TryGetProperty("IconType", out var ic) ? ic.GetString() : "")} 🔊{string.Join(",", lips)}  {text}");
            if (line.TryGetProperty("Trigger", out var tr) && tr.ValueKind == JsonValueKind.Object) Console.WriteLine("  条件: " + Cond(tr));
            if (line.TryGetProperty("Actions", out var aa)) foreach (var a in aa.EnumerateArray()) Console.WriteLine("  动作: " + Flat(a));
        }
    }
    Console.WriteLine($"命中 {hits} 条");
    return 0;
}

static string Flat(JsonElement o) => string.Join(" ", o.EnumerateObject().Where(p => p.Name != "id").Select(p => p.Name + "=" + (p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.ToString())));

// VoiceRip dlg <dialogue.json> <元素id> [语言=ch] [跟着 SwitchDialog 往下展开几层=2]   把对话元素按人话打出来：每条台词的说话方 / 文案 / 触发条件 / 动作，看剧情走向
static int Dlg(string file, string elementId, string lang, int depth)
{
    using var doc = JsonDocument.Parse(File.ReadAllText(file));
    var elements = doc.RootElement.GetProperty("elements").EnumerateArray().ToDictionary(e => e.GetProperty("Id").GetString()!, e => e);
    var seen = new HashSet<string>();
    void Show(string id, int level)
    {
        if (level > depth || !seen.Add(id)) return;
        if (!elements.TryGetValue(id, out var el)) { Console.WriteLine($"{new string(' ', level * 2)}（元素 {id} 不在这个文件里）"); return; }
        var loc = el.TryGetProperty("localization", out var l) && l.ValueKind == JsonValueKind.Object && l.TryGetProperty(lang, out var t) ? t : default;
        string T(string? key) => key != null && loc.ValueKind == JsonValueKind.Object && loc.TryGetProperty(key, out var v) ? v.GetString() ?? key : key ?? "";
        var ind = new string(' ', level * 2);
        Console.WriteLine($"{ind}=== 元素 {id}  IsStart={(el.TryGetProperty("IsStart", out var s) && s.GetBoolean())}  主变量 {(el.TryGetProperty("MainVariable", out var mv) ? mv.GetString() : "-")}  商人 {el.GetProperty("Trader").GetString()}");
        var next = new List<string>();
        foreach (var line in el.GetProperty("Lines").EnumerateArray())
        {
            var side = line.GetProperty("DialogSide").GetString();
            var icon = line.TryGetProperty("IconType", out var ic) ? ic.GetString() : "";
            var ad = line.TryGetProperty("AnimationData", out var ad0) && ad0.ValueKind == JsonValueKind.Object ? ad0 : default;
            var subs = ad.ValueKind == JsonValueKind.Object && ad.TryGetProperty("subtitles", out var ss) && ss.ValueKind == JsonValueKind.Array ? ss.EnumerateArray().Select(x => T(x.GetProperty("id").GetString())).ToList() : new List<string>();
            var lips = ad.ValueKind == JsonValueKind.Object && ad.TryGetProperty("lipSyncs", out var ls) && ls.ValueKind == JsonValueKind.Array ? ls.EnumerateArray().Select(x => x.GetProperty("lipSyncId").GetString()).ToList() : new List<string?>();
            var text = subs.Count > 0 ? string.Join(" / ", subs) : T(line.GetProperty("Id").GetString());
            var cond = line.TryGetProperty("Trigger", out var tr) && tr.ValueKind == JsonValueKind.Object ? Cond(tr) : "";
            var acts = new List<string>();
            if (line.TryGetProperty("Actions", out var aa))
                foreach (var a in aa.EnumerateArray())
                {
                    var type = a.TryGetProperty("type", out var ty) ? ty.GetString() : "?";
                    switch (type)
                    {
                        case "SwitchDialog": { var d = a.GetProperty("dialogId").GetString()!; acts.Add($"→ 元素 {d.Substring(0, 8)}"); next.Add(d); break; }
                        case "SetVariable": acts.Add($"变量 {a.GetProperty("variableId").GetString()!.Substring(0, 8)} = {a.GetProperty("value")}（{a.GetProperty("saveScope").GetString()}）"); break;
                        case "AcceptQuest": acts.Add($"接任务 {a.GetProperty("questId").GetString()}"); break;
                        case "FinishQuest": acts.Add($"交任务 {a.GetProperty("questId").GetString()}"); break;
                        case "HandoverItem": acts.Add("上交物品"); break;
                        case "Quit": acts.Add("退出"); break;
                        default: acts.Add(type ?? "?"); break;
                    }
                }
            Console.WriteLine($"{ind}  [{side,-6}] {icon,-12} {(lips.Count > 0 ? "🔊" : "  ")} {text}");
            if (cond.Length > 0) Console.WriteLine($"{ind}           条件: {cond}");
            if (acts.Count > 0) Console.WriteLine($"{ind}           动作: {string.Join("；", acts)}");
        }
        foreach (var n in next) Show(n, level + 1);
    }
    Show(elementId, 0);
    return 0;
}

static string Cond(JsonElement group)
{
    var parts = new List<string>();
    if (group.TryGetProperty("Conditions", out var cs))
        foreach (var c in cs.EnumerateArray())
        {
            var type = c.TryGetProperty("type", out var t) ? t.GetString() : "?";
            switch (type)
            {
                case "LogicalSubGroup": case "MainLogicalGroup": { var inner = Cond(c); if (inner.Length > 0) parts.Add("(" + inner + ")"); break; }
                case "VariableValue": parts.Add($"变量 {c.GetProperty("variableId").GetString()!.Substring(0, 8)} {c.GetProperty("operator").GetString()} {c.GetProperty("value")}"); break;
                default: parts.Add(Flat(c)); break;
            }
        }
    return string.Join(" 且 ", parts);
}

// VoiceRip mp3 <inDir> <outDir> [kbps=64]   .wav → .mp3（NAudio.Lame，带原生 libmp3lame），.ogg（Fmod5Sharp 直接出的那种是好的）原样拷。
// 09-24：OggVorbisEncoder 这个纯托管 Vorbis 编码器出来的文件码本表是坏的（NVorbis 和 Unity 都解不开、matrix 一测大半炸），改走 MP3，Unity 的 AudioType.MPEG 能解
static int Mp3(string inDir, string outDir, int kbps)
{
    Directory.CreateDirectory(outDir);
    int n = 0; long inBytes = 0, outBytes = 0;
    foreach (var f in Directory.GetFiles(inDir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
    {
        var ext = Path.GetExtension(f).ToLowerInvariant();
        if (ext == ".ogg") { var d = Path.Combine(outDir, Path.GetFileName(f)); File.Copy(f, d, true); n++; inBytes += new FileInfo(f).Length; outBytes += new FileInfo(d).Length; continue; }
        if (ext != ".wav") continue;
        var dst = Path.Combine(outDir, Path.GetFileNameWithoutExtension(f) + ".mp3");
        try
        {
            using var reader = new NAudio.Wave.WaveFileReader(f);
            using var writer = new NAudio.Lame.LameMP3FileWriter(dst, reader.WaveFormat, kbps);
            reader.CopyTo(writer);
        }
        catch (Exception e)
        {
            // NAudio 认不了的 wav（块表怪的）：用自己的 ReadWav 读成采样再喂 16 位 PCM 给 LAME
            Console.WriteLine($"  {Path.GetFileName(f)}：WaveFileReader 读不了（{e.Message.Split('\n')[0]}），改用自带解析");
            if (!ReadWav(f, out var channels, out var rate, out var samples)) { Console.WriteLine($"  ⚠ {Path.GetFileName(f)} 自带解析也不行，跳过"); continue; }
            var count = samples[0].Length;
            var pcm = new byte[count * channels * 2];
            for (var i = 0; i < count; i++)
                for (var c = 0; c < channels; c++)
                {
                    var v = (short)Math.Clamp(Math.Round(samples[c][i] * 32767f), short.MinValue, short.MaxValue);
                    var o = (i * channels + c) * 2; pcm[o] = (byte)(v & 0xFF); pcm[o + 1] = (byte)((v >> 8) & 0xFF);
                }
            using var raw = new NAudio.Wave.RawSourceWaveStream(new MemoryStream(pcm), new NAudio.Wave.WaveFormat(rate, 16, channels));
            using var writer = new NAudio.Lame.LameMP3FileWriter(dst, raw.WaveFormat, kbps);
            raw.CopyTo(writer);
        }
        n++; inBytes += new FileInfo(f).Length; outBytes += new FileInfo(dst).Length;
    }
    Console.WriteLine($"{n} 个 → {outDir}：{inBytes / 1048576.0:N1} MB → {outBytes / 1048576.0:N1} MB（{kbps} kbps）");
    return 0;
}

// VoiceRip matrix   声道 × 采样率 × 质量 各编一段正弦再用 NVorbis 解，看哪种组合出来的文件是好的
static int Matrix()
{
    foreach (var channels in new[] { 1, 2 })
        foreach (var rate in new[] { 44100, 48000, 32000, 22050 })
            foreach (var q in new[] { -0.1f, 0.0f, 0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.8f, 1.0f })
            {
                var samples = new float[channels][];
                for (var c = 0; c < channels; c++) { samples[c] = new float[rate]; for (var i = 0; i < rate; i++) samples[c][i] = (float)Math.Sin(2 * Math.PI * 440 * i / rate) * 0.5f; }
                string result;
                try
                {
                    var ogg = EncodeOgg(samples, channels, rate, q);
                    var tmp = Path.Combine(Path.GetTempPath(), "voicerip_matrix.ogg");
                    File.WriteAllBytes(tmp, ogg);
                    using var v = new NVorbis.VorbisReader(tmp);
                    var buf = new float[4096]; long total = 0; int n;
                    while ((n = v.ReadSamples(buf, 0, buf.Length)) > 0) total += n;
                    result = $"✓ {ogg.Length}B {total / (double)v.Channels / v.SampleRate:0.##}s";
                }
                catch (Exception e) { result = "✗ " + e.Message.Split('\n')[0]; }
                Console.WriteLine($"  {channels}ch {rate}Hz q={q,4}: {result}");
            }
    return 0;
}

// VoiceRip selftest [某个.wav]   合成 1 秒正弦（或读给定 wav）→ 自己的 EncodeOgg → NVorbis 解回来，定位「压出来的 ogg 解不开」是编码用法错还是 wav 读错
static int SelfTest(string? wav)
{
    int channels = 1, rate = 48000; float[][] samples;
    if (wav != null)
    {
        var b = File.ReadAllBytes(wav);
        Console.WriteLine($"wav 头: {System.Text.Encoding.ASCII.GetString(b, 0, 4)} … {System.Text.Encoding.ASCII.GetString(b, 8, 4)}，fmt: format={BitConverter.ToInt16(b, 20)} ch={BitConverter.ToInt16(b, 22)} rate={BitConverter.ToInt32(b, 24)} bits={BitConverter.ToInt16(b, 34)}，块表: " + string.Join(" ", Chunks(b)));
        if (!ReadWav(wav, out channels, out rate, out samples)) { Console.WriteLine("ReadWav 失败"); return 2; }
        var peak = samples[0].Max(x => Math.Abs(x));
        Console.WriteLine($"ReadWav: {channels}ch {rate}Hz {samples[0].Length} 采样 ({samples[0].Length / (double)rate:0.##}s) 峰值 {peak:0.###}");
    }
    else
    {
        samples = new[] { new float[rate] };
        for (var i = 0; i < rate; i++) samples[0][i] = (float)Math.Sin(2 * Math.PI * 440 * i / rate) * 0.5f;
    }
    var ogg = EncodeOgg(samples, channels, rate, 0.4f);
    var tmp = Path.Combine(Path.GetTempPath(), "voicerip_selftest.ogg");
    File.WriteAllBytes(tmp, ogg);
    Console.WriteLine($"编码 {ogg.Length} 字节 → {tmp}，头 {System.Text.Encoding.ASCII.GetString(ogg, 0, 4)}");
    try
    {
        using var v = new NVorbis.VorbisReader(tmp);
        var buf = new float[4096]; long total = 0; int n;
        while ((n = v.ReadSamples(buf, 0, buf.Length)) > 0) total += n;
        Console.WriteLine($"NVorbis 解回: {v.SampleRate}Hz {v.Channels}ch {total / (double)v.Channels / v.SampleRate:0.##}s ✓");
        return 0;
    }
    catch (Exception e) { Console.WriteLine("NVorbis 解不开: " + e); return 2; }
}

static IEnumerable<string> Chunks(byte[] b)
{
    var pos = 12;
    while (pos + 8 <= b.Length)
    {
        var id = System.Text.Encoding.ASCII.GetString(b, pos, 4); var size = BitConverter.ToInt32(b, pos + 4);
        yield return $"{id}({size})";
        pos += 8 + size + (size & 1);
    }
}

// VoiceRip check <dir>   用 NVorbis 把目录里每个 .ogg 完整解一遍，报时长 / 采样率 / 声道 / 峰值，解不开的点名（09-24 游戏里没声音，先排除压出来的文件本身有问题）
static int Check(string dir)
{
    int ok = 0, bad = 0;
    foreach (var f in Directory.GetFiles(dir, "*.ogg").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
    {
        try
        {
            using var v = new NVorbis.VorbisReader(f);
            var buf = new float[4096]; long total = 0; float peak = 0; int n;
            while ((n = v.ReadSamples(buf, 0, buf.Length)) > 0) { total += n; for (var i = 0; i < n; i++) { var a = Math.Abs(buf[i]); if (a > peak) peak = a; } }
            var secs = total / (double)v.Channels / v.SampleRate;
            if (ok + bad < 5 || peak < 0.01f) Console.WriteLine($"  {Path.GetFileName(f)}: {v.SampleRate}Hz {v.Channels}ch {secs:0.##}s 峰值 {peak:0.###}" + (peak < 0.01f ? " ⚠ 几乎无声" : ""));
            ok++;
        }
        catch (Exception e) { Console.WriteLine($"  ⚠ {Path.GetFileName(f)} 解不开: {e.Message}"); bad++; }
    }
    Console.WriteLine($"解得开 {ok} 个，解不开 {bad} 个");
    return bad == 0 ? 0 : 2;
}

// VoiceRip ogg <inDir> <outDir> [质量 0~1，默认 0.4]   把 16 位 PCM 的 .wav 压成 .ogg（OggVorbisEncoder 纯托管，不用 ffmpeg），.ogg 原样拷过去；1.1 抠出来的语音是 PCM，163 条 73 MB，压完约十分之一
static int Ogg(string inDir, string outDir, float quality)
{
    Directory.CreateDirectory(outDir);
    int n = 0; long inBytes = 0, outBytes = 0;
    foreach (var f in Directory.GetFiles(inDir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
    {
        var ext = Path.GetExtension(f).ToLowerInvariant();
        var dst = Path.Combine(outDir, Path.GetFileNameWithoutExtension(f) + ".ogg");
        if (ext == ".ogg") { File.Copy(f, dst, true); n++; inBytes += new FileInfo(f).Length; outBytes += new FileInfo(dst).Length; continue; }
        if (ext != ".wav") continue;
        if (!ReadWav(f, out var channels, out var rate, out var samples)) { Console.WriteLine($"  ⚠ {Path.GetFileName(f)} 不是 16 位 PCM / 32 位浮点 WAV，跳过"); continue; }
        var ogg = EncodeOgg(samples, channels, rate, quality);
        File.WriteAllBytes(dst, ogg);
        n++; inBytes += new FileInfo(f).Length; outBytes += ogg.Length;
    }
    Console.WriteLine($"{n} 个 → {outDir}：{inBytes / 1048576.0:N1} MB → {outBytes / 1048576.0:N1} MB（质量 {quality}）");
    return 0;
}

static bool ReadWav(string path, out int channels, out int rate, out float[][] samples)
{
    channels = 0; rate = 0; samples = Array.Empty<float[]>();
    var b = File.ReadAllBytes(path);
    if (b.Length < 12 || System.Text.Encoding.ASCII.GetString(b, 0, 4) != "RIFF" || System.Text.Encoding.ASCII.GetString(b, 8, 4) != "WAVE") return false;
    int pos = 12, bits = 0, format = 0; byte[]? data = null;
    while (pos + 8 <= b.Length)
    {
        var id = System.Text.Encoding.ASCII.GetString(b, pos, 4); var size = BitConverter.ToInt32(b, pos + 4); pos += 8;
        if (id == "fmt " && size >= 16) { format = BitConverter.ToInt16(b, pos); channels = BitConverter.ToInt16(b, pos + 2); rate = BitConverter.ToInt32(b, pos + 4); bits = BitConverter.ToInt16(b, pos + 14); if (format == -2 && size >= 26) format = BitConverter.ToInt16(b, pos + 24); }
        else if (id == "data") { var len = Math.Max(0, Math.Min(size, b.Length - pos)); data = new byte[len]; Array.Copy(b, pos, data, 0, len); }
        pos += size + (size & 1);
    }
    if (data == null || channels <= 0 || rate <= 0) return false;
    if (format == 1 && bits == 16)
    {
        var count = data.Length / 2 / channels;
        samples = new float[channels][];
        for (var c = 0; c < channels; c++) samples[c] = new float[count];
        for (var i = 0; i < count; i++) for (var c = 0; c < channels; c++) samples[c][i] = BitConverter.ToInt16(data, (i * channels + c) * 2) / 32768f;
        return true;
    }
    if (format == 3 && bits == 32)
    {
        var count = data.Length / 4 / channels;
        samples = new float[channels][];
        for (var c = 0; c < channels; c++) samples[c] = new float[count];
        for (var i = 0; i < count; i++) for (var c = 0; c < channels; c++) samples[c][i] = BitConverter.ToSingle(data, (i * channels + c) * 4);
        return true;
    }
    return false;
}

static byte[] EncodeOgg(float[][] samples, int channels, int rate, float quality)
{
    using var ms = new MemoryStream();
    var info = VorbisInfo.InitVariableBitRate(channels, rate, quality);
    var stream = new OggStream(new Random().Next());
    stream.PacketIn(HeaderPacketBuilder.BuildInfoPacket(info));
    stream.PacketIn(HeaderPacketBuilder.BuildCommentsPacket(new Comments()));
    stream.PacketIn(HeaderPacketBuilder.BuildBooksPacket(info));
    FlushPages(stream, ms, true);
    var state = ProcessingState.Create(info);
    var total = samples[0].Length; var pos = 0; const int chunk = 4096;
    while (pos < total)
    {
        var n = Math.Min(chunk, total - pos);
        var buf = new float[channels][];
        for (var c = 0; c < channels; c++) { buf[c] = new float[n]; Array.Copy(samples[c], pos, buf[c], 0, n); }
        state.WriteData(buf, n);
        pos += n;
        while (!stream.Finished && state.PacketOut(out var packet)) { stream.PacketIn(packet); FlushPages(stream, ms, false); }
    }
    state.WriteEndOfStream();
    while (!stream.Finished && state.PacketOut(out var packet)) { stream.PacketIn(packet); FlushPages(stream, ms, false); }
    return ms.ToArray();
}

static void FlushPages(OggStream stream, Stream output, bool force)
{
    while (stream.PageOut(out var page, force)) { output.Write(page.Header, 0, page.Header.Length); output.Write(page.Body, 0, page.Body.Length); }
}

static (AssetsManager mgr, BundleFileInstance bun, AssetsFileInstance af) Open(string tpk, string bundlePath)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var mgr = new AssetsManager();
    mgr.LoadClassPackage(tpk);
    Console.WriteLine($"打开 {Path.GetFileName(bundlePath)}（{new FileInfo(bundlePath).Length / 1048576.0:N0} MB）……");
    var bun = mgr.LoadBundleFile(bundlePath, true);
    var dirs = bun.file.BlockAndDirInfo.DirectoryInfos;
    var idx = Array.FindIndex(dirs, d => (d.Flags & 4) != 0);
    var af = mgr.LoadAssetsFileFromBundle(bun, idx, false);
    mgr.LoadClassDatabaseFromPackage(af.file.Metadata.UnityVersion);
    Console.WriteLine($"  就绪 {sw.Elapsed.TotalSeconds:0}s：{dirs[idx].Name}，类型树 {(af.file.Metadata.TypeTreeEnabled ? "有" : "无")}");
    return (mgr, bun, af);
}

static string ScriptClass(AssetsManager mgr, AssetsFileInstance af, AssetTypeValueField mono)
{
    try
    {
        var ext = mgr.GetExtAsset(af, mono["m_Script"]);
        var cls = ext.baseField?["m_ClassName"];
        return cls == null || cls.IsDummy ? "?" : cls.AsString;
    }
    catch { return "?"; }
}

static void Walk(AssetTypeValueField f, string indent, int depth, int maxDepth, int maxItems = 6)
{
    if (depth > maxDepth) return;
    var kids = f.Children;
    if (kids == null || kids.Count == 0)
    {
        string v;
        try { v = f.TemplateField.ValueType == AssetValueType.None ? "" : f.AsString; } catch { v = "?"; }
        if (v.Length > 60) v = v.Substring(0, 60) + "…";
        Console.WriteLine($"{indent}{f.TypeName} {f.FieldName} = {v}");
        return;
    }
    Console.WriteLine($"{indent}{f.TypeName} {f.FieldName}" + (f.TemplateField.IsArray ? $" [{kids.Count}]" : ""));
    var n = 0;
    foreach (var k in kids)
    {
        if (f.TemplateField.IsArray && n++ >= maxItems) { Console.WriteLine($"{indent}  … 共 {kids.Count} 项"); break; }
        Walk(k, indent + "  ", depth + 1, maxDepth, maxItems);
    }
}

static int Dump(string tpk, string bundlePath, string classRegex, int maxDepth)
{
    var (mgr, bun, af) = Open(tpk, bundlePath);
    var rx = new Regex(classRegex, RegexOptions.IgnoreCase);
    var shown = 0;
    var classes = new Dictionary<string, int>();
    foreach (var info in af.file.GetAssetsOfType(AssetClassID.MonoBehaviour))
    {
        var bf = mgr.GetBaseField(af, info);
        var cls = ScriptClass(mgr, af, bf);
        classes[cls] = classes.GetValueOrDefault(cls) + 1;
        if (!rx.IsMatch(cls) || shown >= 3) continue;
        shown++;
        Console.WriteLine($"=== MonoBehaviour pathId={info.PathId} 脚本={cls} 名字={bf["m_Name"].AsString}");
        Walk(bf, "  ", 0, maxDepth);
    }
    Console.WriteLine("--- 脚本类名统计: " + string.Join(", ", classes.OrderByDescending(k => k.Value).Take(30).Select(k => $"{k.Key}×{k.Value}")));
    return 0;
}

static AssetTypeValueField? Find(AssetTypeValueField f, Func<AssetTypeValueField, bool> pred, int depth = 0)
{
    if (depth > 6 || f.Children == null) return null;
    foreach (var k in f.Children)
    {
        if (pred(k)) return k;
        var r = Find(k, pred, depth + 1);
        if (r != null) return r;
    }
    return null;
}

// onlyType：只从这个商人类型（1.1 的 Profile.ETraderType 值）的字典里取——像 goodbye_01 / greetings_01 这种通用键每个商人的字典里都有，不限定会抠成别人的嗓音（09-24 第一遍 Kerman 混进了 15 条 Fence 的）
static int RipSo(string tpk, string bundlePath, string outDir, string idsFile, int onlyType)
{
    var wanted = File.ReadAllLines(idsFile).Select(l => l.Trim()).Where(l => l.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var (mgr, bun, af) = Open(tpk, bundlePath);
    var dirs = bun.file.BlockAndDirInfo.DirectoryInfos;
    Directory.CreateDirectory(outDir);
    var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var traderTypes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    foreach (var info in af.file.GetAssetsOfType(AssetClassID.MonoBehaviour))
    {
        var bf = mgr.GetBaseField(af, info);
        var cls = ScriptClass(mgr, af, bf);
        if (!cls.Contains("TraderLipSyncSO", StringComparison.OrdinalIgnoreCase)) continue;
        var entries = Find(bf, x => x.FieldName == "_entries" || x.FieldName == "entries");
        if (entries == null) { Console.WriteLine("  ⚠ TraderLipSyncSO 里没找到 _entries，先用 dump 看结构"); Walk(bf, "  ", 0, 3); continue; }
        var arr = entries.Children.Count == 1 && entries.Children[0].TemplateField.IsArray ? entries.Children[0] : entries;
        Console.WriteLine($"TraderLipSyncSO {bf["m_Name"].AsString}：{arr.Children.Count} 个商人条目");
        foreach (var entry in arr.Children)
        {
            var typeField = Find(entry, x => x.FieldName.Equals("TraderType", StringComparison.OrdinalIgnoreCase));
            var traderType = typeField != null ? typeField.AsInt : -1;
            if (onlyType >= 0 && traderType != onlyType) continue;
            // LipSyncDict 是 PPtr 指向另一个 ScriptableObject（LipSyncDictionary 资源），先解引用再找字典内容
            var dictPtr = Find(entry, x => x.FieldName.Equals("LipSyncDict", StringComparison.OrdinalIgnoreCase));
            AssetTypeValueField dictBf = entry;
            if (dictPtr != null && dictPtr["m_PathID"] is { IsDummy: false })
            {
                try { dictBf = mgr.GetExtAsset(af, dictPtr).baseField ?? entry; } catch (Exception e) { Console.WriteLine($"  ⚠ 商人类型 {traderType}：口型字典解不出（{e.Message}）"); continue; }
            }
            // 字典的序列化形式：优先 key/value 成对的数组，其次 _keys/_values 平行数组
            var pairs = Find(dictBf, x => x.TemplateField.IsArray && x.Children.Count > 0 && Find(x.Children[0], c => c.FieldName.Equals("key", StringComparison.OrdinalIgnoreCase)) != null);
            var hits = new List<(string key, AssetTypeValueField element)>();
            if (pairs != null)
            {
                foreach (var p in pairs.Children)
                {
                    var k = Find(p, c => c.FieldName.Equals("key", StringComparison.OrdinalIgnoreCase));
                    var v = Find(p, c => c.FieldName.Equals("value", StringComparison.OrdinalIgnoreCase));
                    if (k != null && v != null) hits.Add((k.AsString, v));
                }
            }
            else
            {
                var keys = Find(dictBf, x => x.FieldName.Equals("_keys", StringComparison.OrdinalIgnoreCase) || x.FieldName.Equals("keys", StringComparison.OrdinalIgnoreCase));
                var vals = Find(dictBf, x => x.FieldName.Equals("_values", StringComparison.OrdinalIgnoreCase) || x.FieldName.Equals("values", StringComparison.OrdinalIgnoreCase));
                var ka = keys?.Children.Count == 1 && keys.Children[0].TemplateField.IsArray ? keys.Children[0] : keys;
                var va = vals?.Children.Count == 1 && vals.Children[0].TemplateField.IsArray ? vals.Children[0] : vals;
                if (ka != null && va != null) for (var i = 0; i < Math.Min(ka.Children.Count, va.Children.Count); i++) hits.Add((ka.Children[i].AsString, va.Children[i]));
            }
            var matched = hits.Where(h => wanted.Contains(h.key)).ToList();
            Console.WriteLine($"  商人类型 {traderType}：{hits.Count} 条口型，命中清单 {matched.Count} 条" + (hits.Count > 0 ? $"（如 {hits[0].key}）" : ""));
            if (matched.Count == 0) { if (hits.Count == 0) { Console.WriteLine("    结构没认出来，字典字段树："); Walk(dictBf, "    ", 0, 4); } continue; }
            foreach (var (key, element) in matched)
            {
                if (done.Contains(key)) continue;
                traderTypes[key] = traderType;
                var baked = Find(element, c => c.FieldName.Equals("bakedData", StringComparison.OrdinalIgnoreCase));
                if (baked == null) { Console.WriteLine($"    ⚠ {key}：没有 bakedData 字段"); continue; }
                AssetTypeValueField? bakedBf;
                try { bakedBf = mgr.GetExtAsset(af, baked).baseField; } catch (Exception e) { Console.WriteLine($"    ⚠ {key}：bakedData 解不出（{e.Message}）"); continue; }
                if (bakedBf == null) { Console.WriteLine($"    ⚠ {key}：bakedData 指向空"); continue; }
                var clipPtr = Find(bakedBf, c => c.FieldName.Equals("audioClip", StringComparison.OrdinalIgnoreCase));
                if (clipPtr == null) { Console.WriteLine($"    ⚠ {key}：BakedData 里没有 audioClip 字段"); Walk(bakedBf, "      ", 0, 2); continue; }
                AssetTypeValueField? clip;
                try { clip = mgr.GetExtAsset(af, clipPtr).baseField; } catch (Exception e) { Console.WriteLine($"    ⚠ {key}：audioClip 解不出（{e.Message}）"); continue; }
                if (clip == null) { Console.WriteLine($"    ⚠ {key}：audioClip 指向空"); continue; }
                var res = clip["m_Resource"];
                var src = res["m_Source"].AsString; var off = res["m_Offset"].AsLong; var size = res["m_Size"].AsLong;
                var resName = Path.GetFileName(src.Replace("archive:/", ""));
                var ri = Array.FindIndex(dirs, d => string.Equals(d.Name, resName, StringComparison.OrdinalIgnoreCase));
                byte[] data;
                if (ri < 0) { if (size != 0) { Console.WriteLine($"    ⚠ {key}：包里没有 {resName}"); continue; } data = ReadInline(clip); }
                else { var reader = bun.file.DataReader; reader.Position = dirs[ri].Offset + off; data = reader.ReadBytes((int)size); }
                Console.Write($"    {key} ← AudioClip {clip["m_Name"].AsString} ({clip["m_Length"].AsFloat:0.##}s, {size / 1024.0:N0} KB)");
                SaveAudio(outDir, key, data);
                done.Add(key);
            }
        }
    }
    var missing = wanted.Where(w => !done.Contains(w)).ToList();
    Console.WriteLine($"清单 {wanted.Count} 个，抠出 {done.Count} 个" + (missing.Count > 0 ? "，没找到：" + string.Join(" ", missing.Take(40)) + (missing.Count > 40 ? " …" : "") : ""));
    if (traderTypes.Count > 0) Console.WriteLine("商人类型值：" + string.Join(", ", traderTypes.Values.Distinct()));
    return 0;
}

static int Ids(string outFile, string traderId, string[] files)
{
    var ids = new SortedSet<string>(StringComparer.Ordinal);
    var perElement = 0;
    foreach (var f in files)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(f));
        if (!doc.RootElement.TryGetProperty("elements", out var elements)) continue;
        foreach (var el in elements.EnumerateArray())
        {
            if (!el.TryGetProperty("Lines", out var lines)) continue;
            foreach (var line in lines.EnumerateArray())
            {
                var owner = line.TryGetProperty("TraderId", out var t) ? t.GetString() : null;
                if (!string.Equals(owner, traderId, StringComparison.OrdinalIgnoreCase)) continue;
                if (!line.TryGetProperty("AnimationData", out var ad) || !ad.TryGetProperty("lipSyncs", out var ls)) continue;
                foreach (var l in ls.EnumerateArray())
                    if (l.TryGetProperty("lipSyncId", out var id) && id.GetString() is { Length: > 0 } s) { ids.Add(s); perElement++; }
            }
        }
    }
    File.WriteAllLines(outFile, ids);
    Console.WriteLine($"商人 {traderId}：{perElement} 处口型引用，{ids.Count} 个不同 lipSyncId → {outFile}");
    return 0;
}

static int Rip(string tpk, string bundlePath, string? outDir, string? filter)
{
    HashSet<string>? wanted = null; Regex? rx = null;
    if (filter != null)
    {
        if (File.Exists(filter)) wanted = File.ReadAllLines(filter).Where(l => l.Trim().Length > 0).Select(l => l.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        else rx = new Regex(filter, RegexOptions.IgnoreCase);
    }
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var mgr = new AssetsManager();
    mgr.LoadClassPackage(tpk);
    Console.WriteLine($"打开 {bundlePath}（{new FileInfo(bundlePath).Length / 1048576.0:N0} MB）……");
    var bun = mgr.LoadBundleFile(bundlePath, true);
    Console.WriteLine($"  解包完成 {sw.Elapsed.TotalSeconds:0}s，包内文件 {bun.file.BlockAndDirInfo.DirectoryInfos.Length} 个");
    var dirs = bun.file.BlockAndDirInfo.DirectoryInfos;
    for (var i = 0; i < dirs.Length; i++) Console.WriteLine($"    [{i}] {dirs[i].Name}  {dirs[i].DecompressedSize / 1048576.0:N1} MB  flags={dirs[i].Flags}");
    var found = 0; var hit = 0; var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < dirs.Length; i++)
    {
        if ((dirs[i].Flags & 4) == 0) continue;
        var af = mgr.LoadAssetsFileFromBundle(bun, i, false);
        mgr.LoadClassDatabaseFromPackage(af.file.Metadata.UnityVersion);
        var monos = af.file.GetAssetsOfType(AssetClassID.MonoBehaviour).Count;
        Console.WriteLine($"  {dirs[i].Name}: Unity {af.file.Metadata.UnityVersion}，类型树 {(af.file.Metadata.TypeTreeEnabled ? "有" : "无")}，资源 {af.file.AssetInfos.Count} 个，MonoBehaviour {monos} 个");
        foreach (var info in af.file.GetAssetsOfType(AssetClassID.AudioClip))
        {
            var bf = mgr.GetBaseField(af, info);
            var name = bf["m_Name"].AsString;
            found++;
            var want = wanted != null ? wanted.Contains(name) : rx == null || rx.IsMatch(name);
            if (!want) continue;
            hit++;
            seen.Add(name);
            var res = bf["m_Resource"];
            var src = res["m_Source"].AsString; var off = res["m_Offset"].AsLong; var size = res["m_Size"].AsLong;
            Console.WriteLine($"  AudioClip {name}  format={bf["m_CompressionFormat"].AsInt} ch={bf["m_Channels"].AsInt} {bf["m_Frequency"].AsInt}Hz {bf["m_Length"].AsFloat:0.##}s  res={src}+{off} ({size} B)");
            if (outDir == null) continue;
            Directory.CreateDirectory(outDir);
            // 流式音频在包内的 .resS 里：m_Source 形如 archive:/CAB-xxx/CAB-xxx.resS，按目录项偏移去 DataReader 里读
            var resName = Path.GetFileName(src.Replace("archive:/", ""));
            var ri = Array.FindIndex(dirs, d => string.Equals(d.Name, resName, StringComparison.OrdinalIgnoreCase));
            byte[] data;
            if (ri < 0 && size == 0) { data = ReadInline(bf); }
            else if (ri < 0) { Console.WriteLine($"     ⚠ 包里没有 {resName}"); continue; }
            else
            {
                var reader = bun.file.DataReader;
                reader.Position = dirs[ri].Offset + off;
                data = reader.ReadBytes((int)size);
            }
            SaveAudio(outDir, name, data);
        }
    }
    Console.WriteLine($"AudioClip 共 {found} 个，命中 {hit} 个，用时 {sw.Elapsed.TotalSeconds:0}s");
    if (wanted != null)
    {
        var missing = wanted.Where(w => !seen.Contains(w)).ToList();
        Console.WriteLine($"清单 {wanted.Count} 个，找到 {wanted.Count - missing.Count} 个" + (missing.Count > 0 ? "，没找到：" + string.Join(" ", missing.Take(40)) + (missing.Count > 40 ? " …" : "") : ""));
    }
    return 0;
}

static byte[] ReadInline(AssetTypeValueField bf)
{
    var arr = bf["m_AudioData"];
    return arr.IsDummy ? Array.Empty<byte>() : arr.AsByteArray;
}

static void SaveAudio(string outDir, string name, byte[] data)
{
    var safe = Regex.Replace(name, "[\\\\/:*?\"<>|]", "_");
    try
    {
        var bank = Fmod5Sharp.FsbLoader.LoadFsbFromByteArray(data);
        var k = 0;
        foreach (var sample in bank.Samples)
        {
            if (!sample.RebuildAsStandardFileFormat(out var std, out var ext)) { Console.WriteLine($"     ⚠ {name} 样本 {k} 转不出标准格式（{sample.Metadata.Frequency}Hz {sample.Metadata.Channels}ch）"); k++; continue; }
            var path = Path.Combine(outDir, safe + (bank.Samples.Count > 1 ? $"_{k}" : "") + "." + ext);
            File.WriteAllBytes(path, std);
            Console.WriteLine($"     → {Path.GetFileName(path)} ({std.Length / 1024.0:N0} KB)");
            k++;
        }
    }
    catch (Exception e)
    {
        var raw = Path.Combine(outDir, safe + ".fsb");
        File.WriteAllBytes(raw, data);
        Console.WriteLine($"     ⚠ {name} FSB5 解码失败（{e.Message}），原始数据存为 {Path.GetFileName(raw)}");
    }
}
