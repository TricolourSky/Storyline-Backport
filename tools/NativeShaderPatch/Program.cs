using AssetsTools.NET;
using AssetsTools.NET.Extra;
using System.Security.Cryptography;

if (args.Length == 0)
{
    PrintUsage();
    return 2;
}

return args[0].ToLowerInvariant() switch
{
    "inspect" => Inspect(args),
    "dumpclass" => DumpClass(args),
    "unpack" => Unpack(args),
    "patch" => Patch(args),
    "verify" => Verify(args),
    "patchlegacy" => PatchLegacy(args),     // 09-05 之前只认 Prapor 的硬编码版，留作对照
    "verifylegacy" => VerifyLegacy(args),
    "keywords" => Keywords(args),
    "textures" => Textures(args),
    "shaderprops" => ShaderProps(args),
    "patchone" => PatchOne(args),
    _ => PrintUsage()
};

// 给「附加资产包」换一张 shader：包里唯一的序列化文件中同名 shader 壳 → 1.1 真 shader（依赖 shader 照 PatchShaders 的规矩递归带上）。
//   NativeShaderPatch patchone <classdata.tpk> <input-bundle> <EFT111-data-root> <shader-name> <source-file> <source-pathid> <output-bundle>
static int PatchOne(string[] args)
{
    if (args.Length != 8)
        return PrintUsage();
    var manager = new AssetsManager();
    manager.LoadClassPackage(args[1]);
    var bundle = manager.LoadBundleFile(Path.GetFullPath(args[2]), false);
    var sourceRoot = Path.GetFullPath(args[3]);
    var map = new Dictionary<string, SourceRef>(StringComparer.OrdinalIgnoreCase) { [args[4]] = new(args[5], long.Parse(args[6])) };
    var outputBundle = Path.GetFullPath(args[7]);
    var dirs = bundle.file.BlockAndDirInfo.DirectoryInfos;
    var index = -1;
    for (var i = 0; i < dirs.Count; i++)
        if (!dirs[i].Name.EndsWith(".resS", StringComparison.OrdinalIgnoreCase) && !dirs[i].Name.EndsWith(".resource", StringComparison.OrdinalIgnoreCase)) { index = i; break; }
    if (index < 0)
        throw new InvalidDataException("Bundle has no serialized file.");
    var target = manager.LoadAssetsFileFromBundle(bundle, index, false);
    var result = PatchShaders(manager, target, sourceRoot, new Dictionary<string, AssetsFileInstance>(StringComparer.OrdinalIgnoreCase), map);

    var serializedPath = outputBundle + "." + dirs[index].Name + ".patched";
    var serializedWriter = new AssetsFileWriter(serializedPath);
    target.file.Write(serializedWriter, 0);
    serializedWriter.BaseStream.Flush();
    serializedWriter.BaseStream.Dispose();
    var replacement = File.OpenRead(serializedPath);
    dirs[index].Replacer = new ContentReplacerFromStream(replacement, 0, checked((int)replacement.Length), true);

    var unpacked = outputBundle + ".unpacked";
    var unpackedWriter = new AssetsFileWriter(unpacked);
    bundle.file.Write(unpackedWriter, 0);
    unpackedWriter.BaseStream.Flush();
    unpackedWriter.BaseStream.Dispose();
    replacement.Dispose();
    manager.UnloadAll();

    var packManager = new AssetsManager();
    var patched = packManager.LoadBundleFile(unpacked, false);
    var writer = new AssetsFileWriter(outputBundle);
    patched.file.Pack(writer, AssetBundleCompressionType.LZ4, false, null!);
    writer.BaseStream.Flush();
    writer.BaseStream.Dispose();
    packManager.UnloadAll();
    using var input = File.OpenRead(outputBundle);
    Console.WriteLine($"{outputBundle}: file={dirs[index].Name} replaced={result.Replaced} dependencyShadersAdded={result.Added} bytes={new FileInfo(outputBundle).Length} SHA256={Convert.ToHexString(SHA256.HashData(input))}");
    return 0;
}

// 一个 Shader 对象声明的属性名和关键字名（核对材质字段/关键字用）
static int ShaderProps(string[] args)
{
    if (args.Length != 4)
        return PrintUsage();
    var manager = new AssetsManager();
    manager.LoadClassPackage(args[1]);
    var instance = manager.LoadAssetsFile(args[2], false);
    manager.LoadClassDatabaseFromPackage(instance.file.Metadata.UnityVersion);
    var info = instance.file.GetAssetInfo(long.Parse(args[3])) ?? throw new InvalidDataException("no such pathID");
    var field = manager.GetBaseField(instance, info);
    var parsed = field["m_ParsedForm"];
    Console.WriteLine($"Shader {ReadName(field)}");
    Console.WriteLine("  props: " + string.Join(", ", parsed["m_PropInfo"]["m_Props"]["Array"].Children.Select(p => p["m_Name"].AsString + ":" + p["m_Type"].AsInt)));
    Console.WriteLine("  keywords: " + string.Join(", ", parsed["m_KeywordNames"]["Array"].Children.Select(k => k.AsString)));
    manager.UnloadAll();
    return 0;
}

// 列出一个序列化文件里每个 Material 的有效/无效关键字（核对用）
// 贴图逐张列出：名字 / 尺寸 / 格式 / mip / 色彩空间(m_ColorSpace: 0=线性 1=sRGB) / 过滤 / 各向异性 —— 和原版对照用
static int Textures(string[] args)
{
    if (args.Length != 3)
        return PrintUsage();
    var manager = new AssetsManager();
    manager.LoadClassPackage(args[1]);
    var instance = manager.LoadAssetsFile(args[2], false);
    manager.LoadClassDatabaseFromPackage(instance.file.Metadata.UnityVersion);
    foreach (var info in instance.file.GetAssetsOfType(AssetClassID.Texture2D).Concat(instance.file.GetAssetsOfType(AssetClassID.Cubemap)))
    {
        var f = manager.GetBaseField(instance, info);
        var settings = f["m_TextureSettings"];
        var filter = settings.IsDummy ? -1 : settings["m_FilterMode"].AsInt;
        var aniso = settings.IsDummy ? -1 : settings["m_Aniso"].AsInt;
        var mips = f["m_MipCount"].IsDummy ? -1 : f["m_MipCount"].AsInt;
        var cs = f["m_ColorSpace"].IsDummy ? -1 : f["m_ColorSpace"].AsInt;
        var kind = info.GetTypeId(instance.file) == (int)AssetClassID.Cubemap ? "cube" : "tex2d";
        Console.WriteLine($"{ReadName(f)}\t{f["m_Width"].AsInt}x{f["m_Height"].AsInt}\tfmt={f["m_TextureFormat"].AsInt}\tmips={mips}\tcolorSpace={cs}\tfilter={filter}\taniso={aniso}\tbytes={info.ByteSize}\tkind={kind}");
    }
    manager.UnloadAll();
    return 0;
}

static int Keywords(string[] args)
{
    if (args.Length != 3)
        return PrintUsage();
    var manager = new AssetsManager();
    manager.LoadClassPackage(args[1]);
    var instance = manager.LoadAssetsFile(args[2], false);
    manager.LoadClassDatabaseFromPackage(instance.file.Metadata.UnityVersion);
    foreach (var info in instance.file.GetAssetsOfType(AssetClassID.Material))
    {
        var field = manager.GetBaseField(instance, info);
        var valid = field["m_ValidKeywords"]["Array"].Children.Select(c => c.AsString).ToList();
        var invalid = field["m_InvalidKeywords"]["Array"].Children.Select(c => c.AsString).ToList();
        if (valid.Count > 0 || invalid.Count > 0)
            Console.WriteLine($"{ReadName(field)}: valid=[{string.Join(",", valid)}] invalid=[{string.Join(",", invalid)}]");
    }
    manager.UnloadAll();
    return 0;
}

// 材质关键字表必须**按 1.1 原版逐材质照抄**，不能「无效全搬回有效」：
// 1.1 自己的数据里 _EMISSION/_NORMALMAP 这类关键字大多本来就躺在 m_InvalidKeywords（原版 sharedassets640 里
// Desk_lamp_05_06: valid=[] invalid=[_EMISSION,_NORMALMAP]；箱子材质 _NORMALMAP 同样在 invalid）。
// 09-03 晚那版全搬回有效，等于把 1.1 关着的自发光/法线打开了——实机台灯罩整个发亮（native7）。
// 做法：打开原版场景文件（level642 = Prapor 房间，level646 = Vendors_Scripts）及其全部 externals，按材质名收齐
// 两张表，目标包里同名材质两张表原样复制；找不到原件的原样不动并逐个列出。
static (int materials, int missing) RestoreMaterialKeywords(
    AssetsManager manager,
    AssetsFileInstance target,
    string sourceRoot,
    IDictionary<string, AssetsFileInstance> sourceFiles,
    string sourceScene)
{
    var originals = CollectOriginalKeywords(manager, sourceRoot, sourceFiles, sourceScene);
    manager.LoadClassDatabaseFromPackage(target.file.Metadata.UnityVersion);
    int materials = 0, missing = 0;
    foreach (var info in target.file.GetAssetsOfType(AssetClassID.Material))
    {
        var field = manager.GetBaseField(target, info);
        var name = ReadName(field);
        if (!originals.TryGetValue(name, out var original))
        {
            missing++;
            Console.WriteLine($"  keywords: no 1.1 original for material '{name}' (left as built)");
            continue;
        }
        WriteKeywords(field["m_ValidKeywords"]["Array"], original.Valid);
        WriteKeywords(field["m_InvalidKeywords"]["Array"], original.Invalid);
        info.SetNewData(field);
        materials++;
    }
    return (materials, missing);
}

static void WriteKeywords(AssetTypeValueField array, IReadOnlyList<string> values)
{
    array.Children.Clear();
    foreach (var value in values)
    {
        var item = ValueBuilder.DefaultValueFieldFromArrayTemplate(array);
        item.AsString = value;
        array.Children.Add(item);
    }
    array.AsArray = new AssetTypeArrayInfo(values.Count);
}

static List<string> KeywordList(AssetTypeValueField field) =>
    field["Array"].Children.Select(c => c.AsString).ToList();

static Dictionary<string, (List<string> Valid, List<string> Invalid)> CollectOriginalKeywords(
    AssetsManager manager,
    string sourceRoot,
    IDictionary<string, AssetsFileInstance> sourceFiles,
    string sourceScene)
{
    var map = new Dictionary<string, (List<string> Valid, List<string> Invalid)>(StringComparer.Ordinal);
    var scene = LoadSource(manager, sourceRoot, sourceFiles, sourceScene);
    var files = new List<AssetsFileInstance> { scene };
    foreach (var external in scene.file.Metadata.Externals)
    {
        var name = Path.GetFileName(external.PathName.Replace('/', Path.DirectorySeparatorChar));
        if (name.Length == 0 || !File.Exists(Path.Combine(sourceRoot, name)))
            continue;
        files.Add(LoadSource(manager, sourceRoot, sourceFiles, name));
    }
    var conflicts = 0;
    foreach (var file in files)
    {
        manager.LoadClassDatabaseFromPackage(file.file.Metadata.UnityVersion);
        foreach (var info in file.file.GetAssetsOfType(AssetClassID.Material))
        {
            var field = manager.GetBaseField(file, info);
            var entry = (Valid: KeywordList(field["m_ValidKeywords"]), Invalid: KeywordList(field["m_InvalidKeywords"]));
            var name = ReadName(field);
            if (map.TryGetValue(name, out var seen))
            {
                if (!seen.Valid.SequenceEqual(entry.Valid) || !seen.Invalid.SequenceEqual(entry.Invalid))
                    conflicts++;
                continue;
            }
            map[name] = entry;
        }
    }
    Console.WriteLine($"  1.1 keyword tables collected from {sourceScene} + {files.Count - 1} externals: {map.Count} materials, {conflicts} same-name conflicts (first kept)");
    return map;
}

// Unity 2022 重导入把 1.1 的 Alpha8 / RGB24 贴图（灯光 cookie、遮罩）全压成了 BC7：cookie 靠 alpha 通道给灯定形，
// 变成 BC7 后 alpha 全 1 = 吊灯/射灯没有形状、整面墙一道亮带（09-03 深夜对照 1.1 截图）。
// 这里把原版格式不是 DXT1/DXT5/BC7 的同名贴图整个换成 1.1 原件（格式 + 像素 + 采样设置），像素从 .resS 读进来内联。
static bool IsCommonFormat(int format) => format is 10 or 12 or 25;   // DXT1 / DXT5 / BC7：普通压缩，重导入只是换了压缩器，不动

static int RestoreSpecialTextures(
    AssetsManager manager,
    AssetsFileInstance target,
    string sourceRoot,
    IDictionary<string, AssetsFileInstance> sourceFiles,
    string sourceScene)
{
    var originals = CollectSpecialTextures(manager, sourceRoot, sourceFiles, sourceScene);
    var originalCubemaps = CollectSourceCubemaps(manager, sourceRoot, sourceFiles, sourceScene);
    manager.LoadClassDatabaseFromPackage(target.file.Metadata.UnityVersion);
    var restored = 0;
    foreach (var info in target.file.GetAssetsOfType(AssetClassID.Texture2D))
    {
        var name = ReadName(manager.GetBaseField(target, info));
        if (!originals.TryGetValue(name, out var source))
            continue;
        var field = MaterializeSource(manager, source.File, source.PathId, AssetClassID.Texture2D, sourceRoot, out var bytes);
        info.SetNewData(field);
        restored++;
        Console.WriteLine($"  texture restored from 1.1: {name} format={source.Format} bytes={bytes.Length} ({Path.GetFileName(source.File.path)}:{source.PathId})");
    }
    foreach (var info in target.file.GetAssetsOfType(AssetClassID.Cubemap))
    {
        var name = ReadName(manager.GetBaseField(target, info));
        if (name == "<unnamed>" || !originalCubemaps.TryGetValue(name, out var source))
            continue;
        var field = MaterializeSource(manager, source.File, source.PathId, AssetClassID.Cubemap, sourceRoot, out var bytes);
        info.SetNewData(field);
        restored++;
        Console.WriteLine($"  cubemap restored from 1.1: {name} format={source.Format} bytes={bytes.Length} ({Path.GetFileName(source.File.path)}:{source.PathId})");
    }
    return restored;
}

static Dictionary<string, (AssetsFileInstance File, long PathId, int Format)> CollectSourceCubemaps(
    AssetsManager manager,
    string sourceRoot,
    IDictionary<string, AssetsFileInstance> sourceFiles,
    string sourceScene)
{
    var map = new Dictionary<string, (AssetsFileInstance File, long PathId, int Format)>(StringComparer.Ordinal);
    var scene = LoadSource(manager, sourceRoot, sourceFiles, sourceScene);
    var files = new List<AssetsFileInstance> { scene };
    foreach (var external in scene.file.Metadata.Externals)
    {
        var name = Path.GetFileName(external.PathName.Replace('/', Path.DirectorySeparatorChar));
        if (name.Length > 0 && File.Exists(Path.Combine(sourceRoot, name)))
            files.Add(LoadSource(manager, sourceRoot, sourceFiles, name));
    }
    foreach (var file in files)
    {
        manager.LoadClassDatabaseFromPackage(file.file.Metadata.UnityVersion);
        foreach (var info in file.file.GetAssetsOfType(AssetClassID.Cubemap))
        {
            var field = manager.GetBaseField(file, info);
            var name = ReadName(field);
            if (name != "<unnamed>")
                map.TryAdd(name, (file, info.PathId, field["m_TextureFormat"].AsInt));
        }
    }
    return map;
}

static Dictionary<string, (AssetsFileInstance File, long PathId, int Format)> CollectSpecialTextures(
    AssetsManager manager,
    string sourceRoot,
    IDictionary<string, AssetsFileInstance> sourceFiles,
    string sourceScene)
{
    var map = new Dictionary<string, (AssetsFileInstance File, long PathId, int Format)>(StringComparer.Ordinal);
    var scene = LoadSource(manager, sourceRoot, sourceFiles, sourceScene);
    var files = new List<AssetsFileInstance> { scene };
    foreach (var external in scene.file.Metadata.Externals)
    {
        var name = Path.GetFileName(external.PathName.Replace('/', Path.DirectorySeparatorChar));
        if (name.Length > 0 && File.Exists(Path.Combine(sourceRoot, name)))
            files.Add(LoadSource(manager, sourceRoot, sourceFiles, name));
    }
    foreach (var file in files)
    {
        manager.LoadClassDatabaseFromPackage(file.file.Metadata.UnityVersion);
        foreach (var info in file.file.GetAssetsOfType(AssetClassID.Texture2D))
        {
            var field = manager.GetBaseField(file, info);
            var format = field["m_TextureFormat"].AsInt;
            map.TryAdd(ReadName(field), (file, info.PathId, format));
        }
    }
    return map;
}

/// <summary>把原版里一个带流式像素的贴图/立方图读成内联数据（像素从 .resS 取），返回可直接 SetNewData 的字段树。</summary>
static AssetTypeValueField MaterializeSource(
    AssetsManager manager,
    AssetsFileInstance sourceFile,
    long pathId,
    AssetClassID expected,
    string sourceRoot,
    out byte[] bytes)
{
    var sourceInfo = sourceFile.file.GetAssetInfo(pathId)
        ?? throw new InvalidDataException($"Missing source asset {Path.GetFileName(sourceFile.path)}:{pathId}");
    if (sourceInfo.GetTypeId(sourceFile.file) != (int)expected)
        throw new InvalidDataException($"Source {Path.GetFileName(sourceFile.path)}:{pathId} is not a {expected}.");
    manager.LoadClassDatabaseFromPackage(sourceFile.file.Metadata.UnityVersion);
    var sourceField = manager.GetBaseField(sourceFile, sourceInfo);
    var streamData = sourceField["m_StreamData"];
    var size = streamData["size"].AsUInt;
    if (size == 0)
    {
        bytes = sourceField["image data"].AsByteArray;
        return sourceField;
    }
    var resourcePath = Path.Combine(sourceRoot, NormalizeSourceName(streamData["path"].AsString).Replace('/', Path.DirectorySeparatorChar));
    using (var stream = File.OpenRead(resourcePath))
    {
        stream.Position = checked((long)streamData["offset"].AsULong);
        bytes = new byte[checked((int)size)];
        stream.ReadExactly(bytes);
    }
    sourceField["image data"].AsByteArray = bytes;
    streamData["offset"].AsULong = 0;
    streamData["size"].AsUInt = 0;
    streamData["path"].AsString = string.Empty;
    return sourceField;
}

static AssetsFileInstance LoadSource(AssetsManager manager, string sourceRoot, IDictionary<string, AssetsFileInstance> cache, string name)
{
    if (cache.TryGetValue(name, out var existing))
        return existing;
    var instance = manager.LoadAssetsFile(Path.Combine(sourceRoot, name), false);
    cache[name] = instance;
    return instance;
}

static int Unpack(string[] args)
{
    if (args.Length != 3)
        return PrintUsage();

    var manager = new AssetsManager();
    var bundle = manager.LoadBundleFile(Path.GetFullPath(args[1]), false);
    var outputRoot = Path.GetFullPath(args[2]);
    Directory.CreateDirectory(outputRoot);
    foreach (var info in bundle.file.BlockAndDirInfo.DirectoryInfos)
    {
        var output = Path.Combine(outputRoot, info.Name.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        bundle.file.DataReader.Position = info.Offset;
        File.WriteAllBytes(output, bundle.file.DataReader.ReadBytes(checked((int)info.DecompressedSize)));
        Console.WriteLine($"{info.Name}: {info.DecompressedSize} bytes");
    }
    manager.UnloadAll();
    return 0;
}

static int DumpClass(string[] args)
{
    if (args.Length is < 4 or > 5)
        return PrintUsage();

    var manager = new AssetsManager();
    manager.LoadClassPackage(args[1]);
    var instance = manager.LoadAssetsFile(args[2], false);
    manager.LoadClassDatabaseFromPackage(instance.file.Metadata.UnityVersion);
    var classId = (AssetClassID)int.Parse(args[3]);
    var objects = instance.file.GetAssetsOfType(classId);
    var selected = args.Length == 5
        ? objects.Where(x => x.PathId == long.Parse(args[4]))
        : objects;
    foreach (var info in selected)
    {
        var field = manager.GetBaseField(instance, info);
        Console.WriteLine($"ID={info.PathId}; class={(int)classId}; bytes={info.ByteSize}; name={ReadName(field)}");
        DumpValue(field, "", 0);
    }
    manager.UnloadAll();
    return 0;
}

static void DumpValue(AssetTypeValueField field, string path, int depth)
{
    if (depth > 40)
        return;
    var here = string.IsNullOrEmpty(path) ? field.FieldName : $"{path}/{field.FieldName}";
    if (field.Children.Count == 0)
    {
        var value = field.Value?.AsObject;
        if (value is byte[] bytes)
            Console.WriteLine(bytes.Length > 0 && bytes.Length <= 200 && bytes.All(b => b >= 0x20 && b < 0x7F)
                ? $"{here}=\"{System.Text.Encoding.ASCII.GetString(bytes)}\""     // 09-07：可打印 ASCII 直接显示（shader 名 / 关键字 / 通道 tag）
                : $"{here}=<bytes:{bytes.Length}>");
        else
            Console.WriteLine($"{here}={value}");
        return;
    }
    Console.WriteLine(here);
    foreach (var child in field.Children)
        DumpValue(child, here, depth + 1);
}

static int VerifyLegacy(string[] args)
{
    if (args.Length != 4)
        return PrintUsage();

    var manager = new AssetsManager();
    manager.LoadClassPackage(args[1]);
    var extractedRoot = Path.GetFullPath(args[2]);
    var sourceRoot = Path.GetFullPath(args[3]);
    var sourceFiles = new Dictionary<string, AssetsFileInstance>(StringComparer.OrdinalIgnoreCase);
    var verified = 0;

    VerifyShaderFile("BuildPlayer-Vendors_Prapor.sharedAssets", FullPraporShaderMap());
    VerifyShaderFile("BuildPlayer-Vendors_Scripts.sharedAssets", FullScriptShaderMap());

    var originalScene = manager.LoadAssetsFile(Path.Combine(sourceRoot, "level642"), false);
    var rebuiltScene = manager.LoadAssetsFile(Path.Combine(extractedRoot, "BuildPlayer-Vendors_Prapor"), false);
    var renderClasses = new[]
    {
        AssetClassID.GameObject, AssetClassID.Transform, AssetClassID.Camera,
        AssetClassID.MeshRenderer, AssetClassID.MeshFilter, AssetClassID.MeshCollider,
        AssetClassID.BoxCollider, AssetClassID.AudioSource, AssetClassID.Animator,
        AssetClassID.RenderSettings, AssetClassID.Light, AssetClassID.SkinnedMeshRenderer,
        AssetClassID.LightmapSettings, AssetClassID.NavMeshSettings, AssetClassID.LODGroup
    };
    foreach (var classId in renderClasses)
    {
        var originalCount = originalScene.file.GetAssetsOfType(classId).Count;
        var rebuiltCount = rebuiltScene.file.GetAssetsOfType(classId).Count;
        Console.WriteLine($"Scene {classId}: original={originalCount}; bundle={rebuiltCount}");
        if (originalCount != rebuiltCount)
            throw new InvalidDataException($"Scene object count mismatch for {classId}");
    }

    var originalPraporBehaviours = originalScene.file.GetAssetsOfType(AssetClassID.MonoBehaviour).Count;
    var rebuiltPraporBehaviours = rebuiltScene.file.GetAssetsOfType(AssetClassID.MonoBehaviour).Count;
    var originalScripts = manager.LoadAssetsFile(Path.Combine(sourceRoot, "level646"), false);
    var rebuiltScripts = manager.LoadAssetsFile(Path.Combine(extractedRoot, "BuildPlayer-Vendors_Scripts"), false);
    var originalScriptBehaviours = originalScripts.file.GetAssetsOfType(AssetClassID.MonoBehaviour).Count;
    var rebuiltScriptBehaviours = rebuiltScripts.file.GetAssetsOfType(AssetClassID.MonoBehaviour).Count;
    Console.WriteLine($"MonoBehaviour Prapor: original={originalPraporBehaviours}; bundle={rebuiltPraporBehaviours}; intentionally unavailable runtime classes={originalPraporBehaviours - rebuiltPraporBehaviours}");
    Console.WriteLine($"MonoBehaviour Scripts: original={originalScriptBehaviours}; bundle={rebuiltScriptBehaviours}; intentionally unavailable runtime classes={originalScriptBehaviours - rebuiltScriptBehaviours}");
    if (rebuiltPraporBehaviours != 314 || rebuiltScriptBehaviours != 19)
        throw new InvalidDataException("Unexpected retained MonoBehaviour counts.");
    VerifyMonoScriptClass(manager, rebuiltScene, extractedRoot, "MultiFlare", "FlareLight", 2);
    VerifyMonoScriptClass(manager, rebuiltScripts, extractedRoot, "MultiFlare", "FlareSceneSettings", 1);

    var rebuiltRenderSettings = manager.GetBaseField(rebuiltScene,
        rebuiltScene.file.GetAssetsOfType(AssetClassID.RenderSettings).Single());
    var reflectionRef = rebuiltRenderSettings["m_GeneratedSkyboxReflection"];
    var reflectionFileId = reflectionRef["m_FileID"].AsInt;
    var reflectionPathId = reflectionRef["m_PathID"].AsLong;
    if (reflectionFileId <= 0 || reflectionFileId > rebuiltScene.file.Metadata.Externals.Count)
        throw new InvalidDataException("Bundle RenderSettings has no generated skybox reflection external.");
    var reflectionFileName = Path.GetFileName(rebuiltScene.file.Metadata.Externals[reflectionFileId - 1].PathName.Replace('/', Path.DirectorySeparatorChar));
    var rebuiltShared = manager.LoadAssetsFile(Path.Combine(extractedRoot, reflectionFileName), false);
    var rebuiltCubemapInfo = rebuiltShared.file.GetAssetInfo(reflectionPathId)
        ?? throw new InvalidDataException($"Missing bundle generated reflection Cubemap {reflectionFileName}:{reflectionPathId}");
    manager.LoadClassDatabaseFromPackage(originalScene.file.Metadata.UnityVersion);
    var sourceCubemap = MaterializeSourceCubemap(manager, originalScene, sourceRoot, 1, out var sourceCubemapBytes);
    manager.LoadClassDatabaseFromPackage(rebuiltShared.file.Metadata.UnityVersion);
    var rebuiltCubemap = manager.GetBaseField(rebuiltShared, rebuiltCubemapInfo);
    var sourceCubemapHash = Convert.ToHexString(SHA256.HashData(sourceCubemap.WriteToByteArray(false)));
    var rebuiltCubemapHash = Convert.ToHexString(SHA256.HashData(rebuiltCubemap.WriteToByteArray(false)));
    if (!string.Equals(sourceCubemapHash, rebuiltCubemapHash, StringComparison.Ordinal))
        throw new InvalidDataException("Native generated skybox reflection Cubemap payload mismatch.");
    Console.WriteLine($"Reflection verify: pathID={reflectionPathId}; format={rebuiltCubemap["m_TextureFormat"].AsInt}; bytes={sourceCubemapBytes.Length}; SHA256={Convert.ToHexString(SHA256.HashData(sourceCubemapBytes))}");

    Console.WriteLine($"VERIFY OK: {verified} native Shader objects and the native HDR skybox reflection are field-identical to EFT 1.1 after normalization; scene render object counts match level642.");
    manager.UnloadAll();
    return 0;

    void VerifyShaderFile(string targetFileName, IReadOnlyDictionary<string, SourceRef> expected)
    {
        var target = manager.LoadAssetsFile(Path.Combine(extractedRoot, targetFileName), false);
        var targetByName = target.file.GetAssetsOfType(AssetClassID.Shader)
            .ToDictionary(info => ReadName(manager.GetBaseField(target, info)), StringComparer.OrdinalIgnoreCase);

        if (targetByName.Count != expected.Count)
            throw new InvalidDataException($"Shader count mismatch in {targetFileName}: expected {expected.Count}, got {targetByName.Count}");

        foreach (var pair in expected)
        {
            if (!targetByName.TryGetValue(pair.Key, out var targetInfo))
                throw new InvalidDataException($"Missing native Shader in {targetFileName}: {pair.Key}");

            var source = GetSourceFile(pair.Value.FileName);
            var sourceInfo = source.file.GetAssetInfo(pair.Value.PathId)
                ?? throw new InvalidDataException($"Missing source Shader {pair.Value.FileName}:{pair.Value.PathId}");
            manager.LoadClassDatabaseFromPackage(source.file.Metadata.UnityVersion);
            var sourceField = manager.GetBaseField(source, sourceInfo);
            var targetField = manager.GetBaseField(target, targetInfo);
            NormalizePPtrs(sourceField);
            NormalizePPtrs(targetField);
            var sourceHash = Convert.ToHexString(SHA256.HashData(sourceField.WriteToByteArray(false)));
            var targetHash = Convert.ToHexString(SHA256.HashData(targetField.WriteToByteArray(false)));
            if (!string.Equals(sourceHash, targetHash, StringComparison.Ordinal))
                throw new InvalidDataException($"Native Shader payload mismatch: {pair.Key}");
            verified++;
        }

        foreach (var info in target.file.GetAssetsOfType(AssetClassID.Shader))
        {
            var field = manager.GetBaseField(target, info);
            ValidateTargetPPtrs(field, target, ReadName(field));
        }

        Console.WriteLine($"Shader verify {targetFileName}: {targetByName.Count}/{expected.Count} native payloads match; all PPtrs resolve.");
    }

    AssetsFileInstance GetSourceFile(string fileName)
    {
        if (sourceFiles.TryGetValue(fileName, out var source))
            return source;
        source = manager.LoadAssetsFile(Path.Combine(sourceRoot, fileName), false);
        sourceFiles.Add(fileName, source);
        return source;
    }
}

static int PatchLegacy(string[] args)
{
    if (args.Length != 5)
        return PrintUsage();

    var tpkPath = Path.GetFullPath(args[1]);
    var inputBundle = Path.GetFullPath(args[2]);
    var sourceRoot = Path.GetFullPath(args[3]);
    var outputBundle = Path.GetFullPath(args[4]);

    if (string.Equals(inputBundle, outputBundle, StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException("Input and output bundle paths must differ.");

    Directory.CreateDirectory(Path.GetDirectoryName(outputBundle)!);

    var sourceMaps = new Dictionary<string, IReadOnlyDictionary<string, SourceRef>>(StringComparer.OrdinalIgnoreCase)
    {
        ["BuildPlayer-Vendors_Prapor.sharedAssets"] = PraporShaders(),
        ["BuildPlayer-Vendors_Scripts.sharedAssets"] = ScriptSceneShaders()
    };

    var manager = new AssetsManager();
    manager.LoadClassPackage(tpkPath);
    Console.WriteLine($"Loading bundle: {inputBundle}");
    var bundle = manager.LoadBundleFile(inputBundle, false);
    Console.WriteLine($"Input compression: {bundle.file.GetCompressionType()}");

    var sourceFiles = new Dictionary<string, AssetsFileInstance>(StringComparer.OrdinalIgnoreCase);
    var results = new List<PatchResult>();
    var replacementStreams = new List<FileStream>();
    var generatedReflectionPathId = FindGeneratedReflectionPathId(manager, bundle,
        "BuildPlayer-Vendors_Prapor", "BuildPlayer-Vendors_Prapor.sharedAssets");

    foreach (var pair in sourceMaps)
    {
        var internalName = pair.Key;
        var index = bundle.file.GetFileIndex(internalName);
        if (index < 0)
            throw new InvalidDataException($"Bundle is missing serialized file: {internalName}");

        Console.WriteLine($"Patching {internalName}...");
        var target = manager.LoadAssetsFileFromBundle(bundle, index, false);
        var result = PatchShaders(manager, target, sourceRoot, sourceFiles, pair.Value);
        var keywordScene = internalName.Contains("Prapor", StringComparison.OrdinalIgnoreCase) ? "level642" : "level646";
        var keywords = RestoreMaterialKeywords(manager, target, sourceRoot, sourceFiles, keywordScene);
        Console.WriteLine($"  material keyword tables copied from 1.1: {keywords.materials} material(s); no original: {keywords.missing}");
        var textures = RestoreSpecialTextures(manager, target, sourceRoot, sourceFiles, keywordScene);
        Console.WriteLine($"  special-format textures restored from 1.1: {textures}");
        if (string.Equals(internalName, "BuildPlayer-Vendors_Prapor.sharedAssets", StringComparison.OrdinalIgnoreCase))
        {
            var reflectionInfo = target.file.GetAssetInfo(generatedReflectionPathId)
                ?? throw new InvalidDataException($"Missing generated reflection Cubemap pathID {generatedReflectionPathId}");
            manager.LoadClassDatabaseFromPackage(target.file.Metadata.UnityVersion);
            if (reflectionInfo.GetTypeId(target.file) != (int)AssetClassID.Cubemap)
                throw new InvalidDataException($"Generated reflection pathID {generatedReflectionPathId} is not a Cubemap.");
            var sourceScene = manager.LoadAssetsFile(Path.Combine(sourceRoot, "level642"), false);
            manager.LoadClassDatabaseFromPackage(sourceScene.file.Metadata.UnityVersion);
            var sourceCubemap = MaterializeSourceCubemap(manager, sourceScene, sourceRoot, 1, out var reflectionBytes);
            reflectionInfo.SetNewData(sourceCubemap);
            Console.WriteLine($"  native HDR generated reflection: level642:1 -> pathID {generatedReflectionPathId}, format={sourceCubemap["m_TextureFormat"].AsInt}, bytes={reflectionBytes.Length}, SHA256={Convert.ToHexString(SHA256.HashData(reflectionBytes))}");
        }
        results.Add(new PatchResult(internalName, result.Replaced, result.Added, result.Names));

        var serializedPath = outputBundle + "." + internalName + ".patched";
        var serializedWriter = new AssetsFileWriter(serializedPath);
        target.file.Write(serializedWriter, 0);
        serializedWriter.BaseStream.Flush();
        serializedWriter.BaseStream.Dispose();
        var replacementStream = File.OpenRead(serializedPath);
        replacementStreams.Add(replacementStream);
        bundle.file.BlockAndDirInfo.DirectoryInfos[index].Replacer =
            new ContentReplacerFromStream(replacementStream, 0, checked((int)replacementStream.Length), true);
        Console.WriteLine($"  serialized bytes={replacementStream.Length}");
    }

    var unpackedBundle = outputBundle + ".unpacked";
    Console.WriteLine($"Writing patched uncompressed bundle: {unpackedBundle}");
    var unpackedWriter = new AssetsFileWriter(unpackedBundle);
    bundle.file.Write(unpackedWriter, 0);
    unpackedWriter.BaseStream.Flush();
    unpackedWriter.BaseStream.Dispose();
    foreach (var stream in replacementStreams)
        stream.Dispose();
    manager.UnloadAll();

    Console.WriteLine($"Packing output: {outputBundle}");
    var packManager = new AssetsManager();
    var patchedBundle = packManager.LoadBundleFile(unpackedBundle, false);
    var writer = new AssetsFileWriter(outputBundle);
    patchedBundle.file.Pack(writer, AssetBundleCompressionType.LZ4, false, null!);
    writer.BaseStream.Flush();
    writer.BaseStream.Dispose();
    packManager.UnloadAll();

    var fileInfo = new FileInfo(outputBundle);
    using var input = File.OpenRead(outputBundle);
    var hash = Convert.ToHexString(SHA256.HashData(input));
    Console.WriteLine($"Output bytes={fileInfo.Length}; SHA256={hash}");
    foreach (var result in results)
    {
        Console.WriteLine($"{result.InternalName}: replaced={result.Replaced}; dependencyShadersAdded={result.Added}; nativeShaders={result.Names.Count}");
        foreach (var name in result.Names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            Console.WriteLine($"  {name}");
    }
    return 0;
}

static long FindGeneratedReflectionPathId(AssetsManager manager, BundleFileInstance bundle, string sceneFileName, string expectedSharedFileName)
{
    var sceneIndex = bundle.file.GetFileIndex(sceneFileName);
    if (sceneIndex < 0)
        throw new InvalidDataException($"Bundle is missing serialized file: {sceneFileName}");
    var scene = manager.LoadAssetsFileFromBundle(bundle, sceneIndex, false);
    manager.LoadClassDatabaseFromPackage(scene.file.Metadata.UnityVersion);
    var renderSettingsInfo = scene.file.GetAssetsOfType(AssetClassID.RenderSettings).Single();
    var renderSettings = manager.GetBaseField(scene, renderSettingsInfo);
    var reflection = renderSettings["m_GeneratedSkyboxReflection"];
    var fileId = reflection["m_FileID"].AsInt;
    var pathId = reflection["m_PathID"].AsLong;
    if (fileId <= 0 || fileId > scene.file.Metadata.Externals.Count || pathId == 0)
        throw new InvalidDataException("Scene has no valid generated skybox reflection PPtr.");
    var externalName = Path.GetFileName(scene.file.Metadata.Externals[fileId - 1].PathName.Replace('/', Path.DirectorySeparatorChar));
    if (!string.Equals(externalName, expectedSharedFileName, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException($"Generated reflection points to unexpected file: {externalName}");
    return pathId;
}

static AssetTypeValueField MaterializeSourceCubemap(
    AssetsManager manager,
    AssetsFileInstance sourceScene,
    string sourceRoot,
    long pathId,
    out byte[] bytes)
{
    var sourceInfo = sourceScene.file.GetAssetInfo(pathId)
        ?? throw new InvalidDataException($"Missing source Cubemap {Path.GetFileName(sourceScene.path)}:{pathId}");
    if (sourceInfo.GetTypeId(sourceScene.file) != (int)AssetClassID.Cubemap)
        throw new InvalidDataException($"Source {Path.GetFileName(sourceScene.path)}:{pathId} is not a Cubemap.");
    manager.LoadClassDatabaseFromPackage(sourceScene.file.Metadata.UnityVersion);
    var sourceField = manager.GetBaseField(sourceScene, sourceInfo);
    var streamData = sourceField["m_StreamData"];
    var offset = streamData["offset"].AsULong;
    var size = streamData["size"].AsUInt;
    var resourceName = NormalizeSourceName(streamData["path"].AsString);
    var resourcePath = Path.Combine(sourceRoot, resourceName.Replace('/', Path.DirectorySeparatorChar));
    using (var stream = File.OpenRead(resourcePath))
    {
        stream.Position = checked((long)offset);
        bytes = new byte[checked((int)size)];
        stream.ReadExactly(bytes);
    }
    sourceField["image data"].AsByteArray = bytes;
    streamData["offset"].AsULong = 0;
    streamData["size"].AsUInt = 0;
    streamData["path"].AsString = string.Empty;
    return sourceField;
}

static void VerifyMonoScriptClass(
    AssetsManager manager,
    AssetsFileInstance scene,
    string extractedRoot,
    string expectedNamespace,
    string expectedClass,
    int expectedCount)
{
    var files = new Dictionary<string, AssetsFileInstance>(StringComparer.OrdinalIgnoreCase)
    {
        [Path.GetFileName(scene.path)] = scene
    };
    var count = 0;
    foreach (var behaviourInfo in scene.file.GetAssetsOfType(AssetClassID.MonoBehaviour))
    {
        manager.LoadClassDatabaseFromPackage(scene.file.Metadata.UnityVersion);
        var behaviour = manager.GetBaseField(scene, behaviourInfo);
        var scriptRef = behaviour["m_Script"];
        var fileId = scriptRef["m_FileID"].AsInt;
        var pathId = scriptRef["m_PathID"].AsLong;
        if (pathId == 0)
            continue;

        AssetsFileInstance owner;
        if (fileId == 0)
        {
            owner = scene;
        }
        else
        {
            if (fileId < 1 || fileId > scene.file.Metadata.Externals.Count)
                throw new InvalidDataException($"Invalid MonoScript PPtr fileID={fileId} in {scene.path}");
            var fileName = Path.GetFileName(scene.file.Metadata.Externals[fileId - 1].PathName.Replace('/', Path.DirectorySeparatorChar));
            if (!files.TryGetValue(fileName, out owner!))
            {
                owner = manager.LoadAssetsFile(Path.Combine(extractedRoot, fileName), false);
                files.Add(fileName, owner);
            }
        }

        var scriptInfo = owner.file.GetAssetInfo(pathId);
        if (scriptInfo is null || scriptInfo.GetTypeId(owner.file) != (int)AssetClassID.MonoScript)
            continue;
        manager.LoadClassDatabaseFromPackage(owner.file.Metadata.UnityVersion);
        var script = manager.GetBaseField(owner, scriptInfo);
        if (string.Equals(script["m_Namespace"].AsString, expectedNamespace, StringComparison.Ordinal)
            && string.Equals(script["m_ClassName"].AsString, expectedClass, StringComparison.Ordinal))
            count++;
    }
    Console.WriteLine($"MonoScript verify {expectedNamespace}.{expectedClass}: {count}/{expectedCount}");
    if (count != expectedCount)
        throw new InvalidDataException($"Expected {expectedCount} {expectedNamespace}.{expectedClass} behaviours, got {count}.");
}

// 旧签名（按名字）：PatchLegacy / PatchOne 还在用。名字在目标里可能不止一个对象（09-05：Fence / Therapist / Ragman / Skier
// 的包里 "Standard" 有两份——1.1 的壳 + Unity 顺手打进来的内置 Standard），同名的全部换成同一份 1.1 对象。
static PatchResult PatchShaders(
    AssetsManager manager,
    AssetsFileInstance target,
    string sourceRoot,
    IDictionary<string, AssetsFileInstance> sourceFiles,
    IReadOnlyDictionary<string, SourceRef> requested)
{
    var byName = new Dictionary<string, List<AssetFileInfo>>(StringComparer.OrdinalIgnoreCase);
    manager.LoadClassDatabaseFromPackage(target.file.Metadata.UnityVersion);
    foreach (var info in target.file.GetAssetsOfType(AssetClassID.Shader))
    {
        var name = ReadName(manager.GetBaseField(target, info));
        if (!byName.TryGetValue(name, out var list))
            byName[name] = list = new List<AssetFileInfo>();
        list.Add(info);
    }
    var jobs = new List<(AssetFileInfo Info, string Name, SourceRef Source)>();
    foreach (var pair in requested)
    {
        if (!byName.TryGetValue(pair.Key, out var infos))
            throw new InvalidDataException($"Expected target shader was not built: {pair.Key}");
        foreach (var info in infos)
            jobs.Add((info, pair.Key, pair.Value));
    }
    return PatchShaderObjects(manager, target, sourceRoot, sourceFiles, jobs);
}

/// <summary>按目标对象逐个换：(目标 Shader 对象, 目标里的名字, 1.1 来源)。名字为空的目标对象跳过名字核对。</summary>
static PatchResult PatchShaderObjects(
    AssetsManager manager,
    AssetsFileInstance target,
    string sourceRoot,
    IDictionary<string, AssetsFileInstance> sourceFiles,
    IReadOnlyList<(AssetFileInfo Info, string Name, SourceRef Source)> requested)
{
    var targetByName = new Dictionary<string, List<AssetFileInfo>>(StringComparer.OrdinalIgnoreCase);
    manager.LoadClassDatabaseFromPackage(target.file.Metadata.UnityVersion);
    foreach (var info in target.file.GetAssetsOfType(AssetClassID.Shader))
    {
        var name = ReadName(manager.GetBaseField(target, info));
        if (!targetByName.TryGetValue(name, out var list))
            targetByName[name] = list = new List<AssetFileInfo>();
        list.Add(info);
    }

    // A previously patched bundle also contains native dependency shaders that
    // were added recursively.  They are already valid 1.1 payloads, so a
    // texture-only repatch must preserve them instead of rejecting the file.

    var patched = new HashSet<long>();
    var added = 0;
    var maxPathId = target.file.AssetInfos.Max(info => info.PathId);

    foreach (var (info, name, source) in requested)
        InstallShader(name, source, info);

    target.file.Metadata.GenerateQuickLookup();
    return new PatchResult(string.Empty, requested.Count, added, targetByName.Keys.ToArray());

    long EnsureShader(SourceRef source)
    {
        var sourceInstance = GetSourceFile(source.FileName);
        var sourceInfo = sourceInstance.file.GetAssetInfo(source.PathId)
            ?? throw new InvalidDataException($"Missing source asset {source.FileName}:{source.PathId}");
        if (sourceInfo.GetTypeId(sourceInstance.file) != (int)AssetClassID.Shader)
            throw new InvalidDataException($"Source dependency is not a Shader: {source.FileName}:{source.PathId}");

        manager.LoadClassDatabaseFromPackage(sourceInstance.file.Metadata.UnityVersion);
        var sourceField = manager.GetBaseField(sourceInstance, sourceInfo);
        var name = ReadName(sourceField);
        if (targetByName.TryGetValue(name, out var existing))
        {
            var first = existing[0];
            if (!patched.Contains(first.PathId))
                InstallShader(name, source, first);
            return first.PathId;
        }

        manager.LoadClassDatabaseFromPackage(target.file.Metadata.UnityVersion);
        var newInfo = AssetFileInfo.Create(target.file, ++maxPathId, (int)AssetClassID.Shader, manager.ClassDatabase, true);
        target.file.Metadata.AddAssetInfo(newInfo);
        targetByName.Add(name, new List<AssetFileInfo> { newInfo });
        added++;
        InstallShader(name, source, newInfo);
        return newInfo.PathId;
    }

    void InstallShader(string expectedName, SourceRef source, AssetFileInfo targetInfo)
    {
        if (!patched.Add(targetInfo.PathId))
            return;

        var sourceInstance = GetSourceFile(source.FileName);
        var sourceInfo = sourceInstance.file.GetAssetInfo(source.PathId)
            ?? throw new InvalidDataException($"Missing source shader {source.FileName}:{source.PathId}");
        manager.LoadClassDatabaseFromPackage(sourceInstance.file.Metadata.UnityVersion);
        var sourceField = manager.GetBaseField(sourceInstance, sourceInfo);
        var actualName = ReadName(sourceField);
        if (expectedName.Length > 0 && !string.Equals(expectedName, actualName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Shader mapping mismatch: expected '{expectedName}', got '{actualName}' from {source.FileName}:{source.PathId}");

        RemapPPtrs(sourceField, sourceInstance);
        targetInfo.SetNewData(sourceField);
        Console.WriteLine($"  native {actualName}: {source.FileName}:{source.PathId} -> pathID {targetInfo.PathId}, bytes {sourceInfo.ByteSize}" + (expectedName.Length == 0 ? " (target shader had no name; matched through a material)" : ""));
    }

    void RemapPPtrs(AssetTypeValueField field, AssetsFileInstance owningSource)
    {
        if (IsPPtr(field))
        {
            var fileId = field["m_FileID"].AsInt;
            var pathId = field["m_PathID"].AsLong;
            if (fileId == 0 && pathId == 0)
                return;

            var resolved = ResolveSource(owningSource, fileId, pathId);
            if (resolved.IsBuiltIn)
            {
                field["m_FileID"].AsInt = FindTargetExternal(resolved.FileName);
                field["m_PathID"].AsLong = pathId;
                return;
            }

            var sourceInstance = GetSourceFile(resolved.FileName);
            var sourceInfo = sourceInstance.file.GetAssetInfo(pathId)
                ?? throw new InvalidDataException($"Unresolved source PPtr {resolved.FileName}:{pathId}");
            var typeId = sourceInfo.GetTypeId(sourceInstance.file);
            if (typeId != (int)AssetClassID.Shader)
                throw new InvalidDataException($"Native shader has unsupported non-Shader PPtr {resolved.FileName}:{pathId} type={typeId}");

            var targetPathId = EnsureShader(new SourceRef(resolved.FileName, pathId));
            field["m_FileID"].AsInt = 0;
            field["m_PathID"].AsLong = targetPathId;
            return;
        }

        foreach (var child in field.Children)
            RemapPPtrs(child, owningSource);
    }

    AssetsFileInstance GetSourceFile(string fileName)
    {
        var normalized = NormalizeSourceName(fileName);
        if (sourceFiles.TryGetValue(normalized, out var instance))
            return instance;

        var fullPath = Path.Combine(sourceRoot, normalized.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"EFT 1.1 source file not found: {normalized}", fullPath);
        instance = manager.LoadAssetsFile(fullPath, false);
        sourceFiles.Add(normalized, instance);
        return instance;
    }

    ResolvedRef ResolveSource(AssetsFileInstance owner, int fileId, long pathId)
    {
        if (fileId == 0)
            return new ResolvedRef(Path.GetFileName(owner.path), pathId, false);

        if (fileId < 1 || fileId > owner.file.Metadata.Externals.Count)
            throw new InvalidDataException($"Invalid source fileID {fileId} in {owner.path}");
        var external = owner.file.Metadata.Externals[fileId - 1];
        var path = NormalizeSourceName(external.PathName);
        var builtIn = path.Contains("unity_builtin_extra", StringComparison.OrdinalIgnoreCase)
            || path.Contains("unity default resources", StringComparison.OrdinalIgnoreCase);
        return new ResolvedRef(path, pathId, builtIn);
    }

    int FindTargetExternal(string sourceExternal)
    {
        var sourceLeaf = Path.GetFileName(sourceExternal.Replace('/', Path.DirectorySeparatorChar));
        for (var i = 0; i < target.file.Metadata.Externals.Count; i++)
        {
            var targetLeaf = Path.GetFileName(target.file.Metadata.Externals[i].PathName.Replace('/', Path.DirectorySeparatorChar));
            if (string.Equals(sourceLeaf, targetLeaf, StringComparison.OrdinalIgnoreCase))
                return i + 1;
        }
        throw new InvalidDataException($"Target file has no built-in external matching {sourceExternal}");
    }
}

static int Inspect(string[] args)
{
    if (args.Length is < 3 or > 4)
        return PrintUsage();

    var manager = new AssetsManager();
    manager.LoadClassPackage(args[1]);
    var instance = manager.LoadAssetsFile(args[2], false);
    manager.LoadClassDatabaseFromPackage(instance.file.Metadata.UnityVersion);

    Console.WriteLine($"Unity={instance.file.Metadata.UnityVersion}; TypeTree={instance.file.Metadata.TypeTreeEnabled}");
    for (var i = 0; i < instance.file.Metadata.Externals.Count; i++)
    {
        var external = instance.file.Metadata.Externals[i];
        Console.WriteLine($"External fileID={i + 1}; path={external.PathName}; guid={external.Guid}; type={external.Type}");
    }

    IEnumerable<AssetFileInfo> shaders = args.Length == 4
        ? new[] { instance.file.GetAssetInfo(long.Parse(args[3])) }
        : instance.file.GetAssetsOfType(AssetClassID.Shader);

    foreach (var shader in shaders)
    {
        var field = manager.GetBaseField(instance, shader);
        Console.WriteLine($"Shader pathID={shader.PathId}; bytes={shader.ByteSize}; name={ReadName(field)}");
        DumpPPtrs(field, "", 0);
    }

    manager.UnloadAll();
    return 0;
}

static IReadOnlyDictionary<string, SourceRef> PraporShaders() =>
    new Dictionary<string, SourceRef>(StringComparer.OrdinalIgnoreCase)
    {
        ["Legacy Shaders/Reflective/Bumped Diffuse"] = new("globalgamemanagers.assets", 5),
        ["Legacy Shaders/Reflective/VertexLit"] = new("globalgamemanagers.assets", 6),
        ["Standard"] = new("globalgamemanagers.assets", 130),
        ["Legacy Shaders/Transparent/Cutout/VertexLit"] = new("globalgamemanagers.assets", 199),
        ["Legacy Shaders/Transparent/Cutout/Diffuse"] = new("globalgamemanagers.assets", 90),
        ["Legacy Shaders/Transparent/Cutout/Specular"] = new("sharedassets161.assets", 2770),
        ["Skybox/Procedural"] = new("sharedassets0.assets", 3),
        ["Transparent/DepthZwriteDithered"] = new("globalgamemanagers.assets", 271),
        ["p0/Reflective/Bumped Specular SMap Transparent Cutoff"] = new("globalgamemanagers.assets", 88),
        ["p0/Reflective/Bumped Specular SMap_Decal"] = new("globalgamemanagers.assets", 207),
        ["CW FX/OpticSight"] = new("globalgamemanagers.assets", 164),
        ["Global Fog/Transparent Reflective Specular"] = new("globalgamemanagers.assets", 59),
        ["Custom/MuzzleSmoke"] = new("globalgamemanagers.assets", 159),
        ["p0/Reflective/Bumped Specular"] = new("globalgamemanagers.assets", 273),
        ["Decal/DeferredDecalDiffuseNormalsDynamicEditor"] = new("globalgamemanagers.assets", 34),
        ["Custom/Billboard_FogSheet_Simple"] = new("sharedassets13.assets", 2740),
        ["Hidden/ChromaticAberration"] = new("globalgamemanagers.assets", 256),
        ["p0/Reflective/Bumped Specular SMap"] = new("globalgamemanagers.assets", 19),
        ["shadow"] = new("globalgamemanagers.assets", 27),
        ["CW FX/BackLens"] = new("globalgamemanagers.assets", 278),
        ["Decal/Ultra Deferred Decal Of God 3000"] = new("globalgamemanagers.assets", 111),
        ["Characters/TraiderHair"] = new("sharedassets161.assets", 2771),
        ["p0/Reflective/Bumped Emissive Specular SMap"] = new("globalgamemanagers.assets", 183),
        ["Hidden/Time of Day/Scattering"] = new("globalgamemanagers.assets", 293),
        ["Hidden/OpticCullingMask"] = new("globalgamemanagers.assets", 77),
        ["p0/Cutout/Bumped Diffuse"] = new("globalgamemanagers.assets", 29)
    };

static IReadOnlyDictionary<string, SourceRef> ScriptSceneShaders() =>
    new Dictionary<string, SourceRef>(StringComparer.OrdinalIgnoreCase)
    {
        ["Skybox/Cubemap"] = new("sharedassets1.assets", 4),
        ["Hidden/WriteScreenAmbient"] = new("globalgamemanagers.assets", 204),
        ["Hidden/LiquidOnScreen"] = new("globalgamemanagers.assets", 87),
        ["Hidden/ClearStencil"] = new("globalgamemanagers.assets", 255),
        ["Rain/LiquidDrop"] = new("globalgamemanagers.assets", 84),
        ["Hidden/AmbientHighlihgt"] = new("globalgamemanagers.assets", 179),
        ["Hidden/PrismEffects"] = new("globalgamemanagers.assets", 81),
        ["Hidden/AnalyticSource"] = new("globalgamemanagers.assets", 99),
        ["Hidden/PrismEffectsTertiary"] = new("resources.assets", 2845),
        ["Hidden/PrismEffectsSecondary"] = new("globalgamemanagers.assets", 241),
        ["Hidden/WriteDepth"] = new("globalgamemanagers.assets", 105),
        ["Custom/ShitOnScreen"] = new("globalgamemanagers.assets", 284),
        ["Custom/multiFlare"] = new("globalgamemanagers.assets", 36),
        ["Hidden/PrismKinoObscurance"] = new("globalgamemanagers.assets", 169),
        ["Hidden/StencilShadow"] = new("globalgamemanagers.assets", 188),
        ["Hidden/Amplify Impostors/Octahedron Impostors Array"] = new("sharedassets3.assets", 15)
    };

static IReadOnlyDictionary<string, SourceRef> FullPraporShaderMap()
{
    var result = new Dictionary<string, SourceRef>(PraporShaders(), StringComparer.OrdinalIgnoreCase)
    {
        ["Legacy Shaders/VertexLit"] = new("globalgamemanagers.assets", 4),
        ["Diffuse"] = new("globalgamemanagers.assets", 232),
        ["Hidden/Internal-ShadowCaster"] = new("globalgamemanagers.assets", 75),
        ["Hidden/Internal-BlackError"] = new("globalgamemanagers.assets", 205),
        ["Hidden/Internal-CustomShadowCaster"] = new("globalgamemanagers.assets", 189)
    };
    return result;
}

static IReadOnlyDictionary<string, SourceRef> FullScriptShaderMap()
{
    var result = new Dictionary<string, SourceRef>(ScriptSceneShaders(), StringComparer.OrdinalIgnoreCase)
    {
        ["Diffuse"] = new("globalgamemanagers.assets", 232),
        ["Legacy Shaders/VertexLit"] = new("globalgamemanagers.assets", 4)
    };
    return result;
}

static string NormalizeSourceName(string value)
{
    var normalized = value.Replace('\\', '/');
    if (normalized.StartsWith("archive:/", StringComparison.OrdinalIgnoreCase))
        normalized = normalized[(normalized.LastIndexOf('/') + 1)..];
    return normalized;
}

static bool IsPPtr(AssetTypeValueField field) =>
    field.Children.Count == 2
    && !field["m_FileID"].IsDummy
    && !field["m_PathID"].IsDummy;

static void NormalizePPtrs(AssetTypeValueField field)
{
    if (IsPPtr(field))
    {
        field["m_FileID"].AsInt = 0;
        field["m_PathID"].AsLong = 0;
        return;
    }
    foreach (var child in field.Children)
        NormalizePPtrs(child);
}

static void ValidateTargetPPtrs(AssetTypeValueField field, AssetsFileInstance target, string shaderName)
{
    if (IsPPtr(field))
    {
        var fileId = field["m_FileID"].AsInt;
        var pathId = field["m_PathID"].AsLong;
        if (fileId == 0 && pathId != 0)
        {
            var info = target.file.GetAssetInfo(pathId);
            if (info is null || info.GetTypeId(target.file) != (int)AssetClassID.Shader)
                throw new InvalidDataException($"Unresolved local Shader PPtr in {shaderName}: {pathId}");
        }
        else if (fileId < 0 || fileId > target.file.Metadata.Externals.Count)
        {
            throw new InvalidDataException($"Invalid external PPtr in {shaderName}: fileID={fileId}, pathID={pathId}");
        }
        return;
    }
    foreach (var child in field.Children)
        ValidateTargetPPtrs(child, target, shaderName);
}

static string ReadName(AssetTypeValueField field)
{
    var rootName = field["m_Name"];
    if (!rootName.IsDummy && rootName.Value is not null && !string.IsNullOrWhiteSpace(rootName.AsString))
        return rootName.AsString;

    var parsedForm = field["m_ParsedForm"];
    if (!parsedForm.IsDummy)
    {
        var parsedName = parsedForm["m_Name"];
        if (!parsedName.IsDummy && parsedName.Value is not null)
            return parsedName.AsString;
    }

    // 场景里的 MonoBehaviour 没有 m_Name：报它挂在哪个 GameObject 上 + 脚本 pathID，好歹能认
    var go = field["m_GameObject"];
    var script = field["m_Script"];
    if (!go.IsDummy && !script.IsDummy)
        return $"<go:{go["m_PathID"].AsLong} script:{script["m_FileID"].AsInt}:{script["m_PathID"].AsLong}>";

    return "<unnamed>";
}

static void DumpPPtrs(AssetTypeValueField field, string path, int depth)
{
    if (depth > 30)
        return;

    var here = string.IsNullOrEmpty(path) ? field.FieldName : $"{path}/{field.FieldName}";
    if (IsPPtr(field))
    {
        var fileId = field["m_FileID"].AsInt;
        var pathId = field["m_PathID"].AsLong;
        if (fileId != 0 || pathId != 0)
            Console.WriteLine($"  PPtr {here}: fileID={fileId}, pathID={pathId}");
        return;
    }

    foreach (var child in field.Children)
        DumpPPtrs(child, here, depth + 1);
}

static int PrintUsage()
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  NativeShaderPatch inspect <classdata.tpk> <assets-file> [shader-path-id]");
    Console.Error.WriteLine("  NativeShaderPatch dumpclass <classdata.tpk> <assets-file> <class-id> [path-id]");
    Console.Error.WriteLine("  NativeShaderPatch unpack <input-bundle> <output-dir>");
    Console.Error.WriteLine("  NativeShaderPatch patch <classdata.tpk> <input-bundle> <EFT111-data-root> <output-bundle> [unity-Vendors-dir]   (any vendor scene bundle; scenes detected from the bundle)");
    Console.Error.WriteLine("  NativeShaderPatch verify <classdata.tpk> <extracted-bundle-dir> <EFT111-data-root> [unity-Vendors-dir]");
    Console.Error.WriteLine("  NativeShaderPatch patchlegacy / verifylegacy  (pre-09-05 Prapor-only hardcoded versions, kept for reference)");
    Console.Error.WriteLine("  NativeShaderPatch keywords <classdata.tpk> <assets-file>");
    return 2;
}

// ═══════════════════════════════════════════════════════════════════════════════════════════════════════
// 2026-09-05 通用版：不再按 Prapor 硬编码。包里有哪些 BuildPlayer-<场景>.sharedAssets 就修哪些；每个场景按
// Scenes.Levels 找到 1.1 的原 level 文件，shader 的 1.1 来源按三级找：
//   ① 原场景（level + 全部 externals）里的材质**真正引用**的那个 Shader 对象（最保真）
//   ② level + externals + globalgamemanagers.assets + resources.assets 里同名的 Shader（多个候选按文件排序取最前）
//   ③ 全盘扫描 EFT111 数据目录所有 .assets（一次性，结果缓存到工具目录 shader_index.tsv）
// 关键字表 / 特殊格式贴图 / 场景自带的 HDR 反射立方图 都按该场景自己的 level 文件回填（做法与 Prapor 版逐字相同）。
// ═══════════════════════════════════════════════════════════════════════════════════════════════════════

static int Patch(string[] args)
{
    if (args.Length is < 5 or > 6)
        return PrintUsage();

    var tpkPath = Path.GetFullPath(args[1]);
    var inputBundle = Path.GetFullPath(args[2]);
    var sourceRoot = Path.GetFullPath(args[3]);
    var outputBundle = Path.GetFullPath(args[4]);
    var scenesDir = args.Length == 6 ? Path.GetFullPath(args[5]) : null;   // Unity 工程的 Assets/VisitAPI/Vendors：无名 shader 壳按组件字段回溯名字用
    Cfg.Tpk = tpkPath;

    if (string.Equals(inputBundle, outputBundle, StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException("Input and output bundle paths must differ.");
    Directory.CreateDirectory(Path.GetDirectoryName(outputBundle)!);

    var manager = new AssetsManager();
    manager.LoadClassPackage(tpkPath);
    Console.WriteLine($"Loading bundle: {inputBundle}");
    var bundle = manager.LoadBundleFile(inputBundle, false);
    Console.WriteLine($"Input compression: {bundle.file.GetCompressionType()}");
    var scenes = BundleScenes(bundle.file.BlockAndDirInfo.DirectoryInfos.Select(d => d.Name)).ToList();
    if (scenes.Count == 0)
        throw new InvalidDataException("Bundle has no BuildPlayer-<scene>.sharedAssets file.");
    Console.WriteLine("Scenes in bundle: " + string.Join(", ", scenes.Select(s => $"{s.Scene} ({Scenes.LevelOf(s.Scene)})")));

    var sourceFiles = new Dictionary<string, AssetsFileInstance>(StringComparer.OrdinalIgnoreCase);
    var results = new List<PatchResult>();
    var replacementStreams = new List<FileStream>();

    foreach (var (scene, sceneFile, sharedFile) in scenes)
    {
        var level = Scenes.LevelOf(scene);
        var index = bundle.file.GetFileIndex(sharedFile);
        if (index < 0)
            throw new InvalidDataException($"Bundle is missing serialized file: {sharedFile}");
        Console.WriteLine($"Patching {sharedFile} (1.1 source {level})...");

        // 反射立方图的目标 pathID 要在改任何东西之前从场景文件里读
        long reflectionPathId = 0;
        try { reflectionPathId = FindGeneratedReflectionPathId(manager, bundle, sceneFile, sharedFile); }
        catch (InvalidDataException e) { Console.WriteLine($"  generated reflection: none in bundle scene ({e.Message})"); }

        var target = manager.LoadAssetsFileFromBundle(bundle, index, false);
        var sceneIndex = bundle.file.GetFileIndex(sceneFile);
        var sceneInstance = sceneIndex >= 0 ? manager.LoadAssetsFileFromBundle(bundle, sceneIndex, false) : null;
        var (jobs, byName, unmatched) = ResolveTargetShaders(manager, target, sourceRoot, sourceFiles, level,
            ids => NamesFromProjectFields(manager, sceneInstance, sharedFile, scene, scenesDir, ids));
        CompareWithLegacyMap(scene, byName);
        var result = PatchShaderObjects(manager, target, sourceRoot, sourceFiles, jobs);
        foreach (var miss in unmatched)
            Console.WriteLine($"  WARNING: no 1.1 Shader object found for {miss} (left as built)");

        var keywords = RestoreMaterialKeywords(manager, target, sourceRoot, sourceFiles, level);
        Console.WriteLine($"  material keyword tables copied from 1.1: {keywords.materials} material(s); no original: {keywords.missing}");
        var textures = RestoreSpecialTextures(manager, target, sourceRoot, sourceFiles, level);
        Console.WriteLine($"  special-format textures restored from 1.1: {textures}");
        if (reflectionPathId != 0)
            RestoreGeneratedReflection(manager, target, reflectionPathId, sourceRoot, sourceFiles, level);
        results.Add(new PatchResult(sharedFile, result.Replaced, result.Added, result.Names));

        var serializedPath = outputBundle + "." + sharedFile + ".patched";
        var serializedWriter = new AssetsFileWriter(serializedPath);
        target.file.Write(serializedWriter, 0);
        serializedWriter.BaseStream.Flush();
        serializedWriter.BaseStream.Dispose();
        var replacementStream = File.OpenRead(serializedPath);
        replacementStreams.Add(replacementStream);
        bundle.file.BlockAndDirInfo.DirectoryInfos[index].Replacer =
            new ContentReplacerFromStream(replacementStream, 0, checked((int)replacementStream.Length), true);
        Console.WriteLine($"  serialized bytes={replacementStream.Length}");
    }

    var unpackedBundle = outputBundle + ".unpacked";
    Console.WriteLine($"Writing patched uncompressed bundle: {unpackedBundle}");
    var unpackedWriter = new AssetsFileWriter(unpackedBundle);
    bundle.file.Write(unpackedWriter, 0);
    unpackedWriter.BaseStream.Flush();
    unpackedWriter.BaseStream.Dispose();
    foreach (var stream in replacementStreams)
        stream.Dispose();
    manager.UnloadAll();

    Console.WriteLine($"Packing output: {outputBundle}");
    var packManager = new AssetsManager();
    var patchedBundle = packManager.LoadBundleFile(unpackedBundle, false);
    var writer = new AssetsFileWriter(outputBundle);
    patchedBundle.file.Pack(writer, AssetBundleCompressionType.LZ4, false, null!);
    writer.BaseStream.Flush();
    writer.BaseStream.Dispose();
    packManager.UnloadAll();

    var fileInfo = new FileInfo(outputBundle);
    using var input = File.OpenRead(outputBundle);
    var hash = Convert.ToHexString(SHA256.HashData(input));
    Console.WriteLine($"Output bytes={fileInfo.Length}; SHA256={hash}");
    foreach (var result in results)
    {
        Console.WriteLine($"{result.InternalName}: replaced={result.Replaced}; dependencyShadersAdded={result.Added}; nativeShaders={result.Names.Count}");
        foreach (var name in result.Names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            Console.WriteLine($"  {name}");
    }
    return 0;
}

static int Verify(string[] args)
{
    if (args.Length is < 4 or > 5)
        return PrintUsage();

    var manager = new AssetsManager();
    manager.LoadClassPackage(args[1]);
    Cfg.Tpk = Path.GetFullPath(args[1]);
    var extractedRoot = Path.GetFullPath(args[2]);
    var sourceRoot = Path.GetFullPath(args[3]);
    var scenesDir = args.Length == 5 ? Path.GetFullPath(args[4]) : null;
    var sourceFiles = new Dictionary<string, AssetsFileInstance>(StringComparer.OrdinalIgnoreCase);
    var scenes = BundleScenes(Directory.EnumerateFiles(extractedRoot).Select(p => Path.GetFileName(p))).ToList();
    if (scenes.Count == 0)
        throw new InvalidDataException("Extracted dir has no BuildPlayer-<scene>.sharedAssets file.");
    var renderClasses = new[]
    {
        AssetClassID.GameObject, AssetClassID.Transform, AssetClassID.Camera,
        AssetClassID.MeshRenderer, AssetClassID.MeshFilter, AssetClassID.MeshCollider,
        AssetClassID.BoxCollider, AssetClassID.AudioSource, AssetClassID.Animator,
        AssetClassID.RenderSettings, AssetClassID.Light, AssetClassID.SkinnedMeshRenderer,
        AssetClassID.LightmapSettings, AssetClassID.NavMeshSettings, AssetClassID.LODGroup
    };
    var verified = 0;
    var warnings = 0;

    foreach (var (scene, sceneFile, sharedFile) in scenes)
    {
        var level = Scenes.LevelOf(scene);
        Console.WriteLine($"== {scene}: {sharedFile} vs 1.1 {level} ==");
        var target = manager.LoadAssetsFile(Path.Combine(extractedRoot, sharedFile), false);
        var scenePath = Path.Combine(extractedRoot, sceneFile);
        var sceneInstance = File.Exists(scenePath) ? manager.LoadAssetsFile(scenePath, false) : null;
        var (jobs, _, unmatched) = ResolveTargetShaders(manager, target, sourceRoot, sourceFiles, level,
            ids => NamesFromProjectFields(manager, sceneInstance, sharedFile, scene, scenesDir, ids));
        foreach (var miss in unmatched)
        {
            warnings++;
            Console.WriteLine($"  WARNING shader {miss}: no 1.1 source to compare against");
        }
        var shaders = jobs;
        var matched = 0;
        foreach (var (info, name, src) in shaders)
        {
            var source = LoadSource(manager, sourceRoot, sourceFiles, src.FileName);
            var sourceInfo = source.file.GetAssetInfo(src.PathId)
                ?? throw new InvalidDataException($"Missing source Shader {src.FileName}:{src.PathId}");
            manager.LoadClassDatabaseFromPackage(source.file.Metadata.UnityVersion);
            var sourceField = manager.GetBaseField(source, sourceInfo);
            manager.LoadClassDatabaseFromPackage(target.file.Metadata.UnityVersion);
            var targetField = manager.GetBaseField(target, info);
            NormalizePPtrs(sourceField);
            NormalizePPtrs(targetField);
            var sourceHash = Convert.ToHexString(SHA256.HashData(sourceField.WriteToByteArray(false)));
            var targetHash = Convert.ToHexString(SHA256.HashData(targetField.WriteToByteArray(false)));
            if (!string.Equals(sourceHash, targetHash, StringComparison.Ordinal))
                throw new InvalidDataException($"Native Shader payload mismatch in {sharedFile}: '{name}' pathID {info.PathId} (expected {src.FileName}:{src.PathId})");
            matched++;
            verified++;
        }
        manager.LoadClassDatabaseFromPackage(target.file.Metadata.UnityVersion);
        foreach (var (info, name, _) in shaders)
            ValidateTargetPPtrs(manager.GetBaseField(target, info), target, name);
        Console.WriteLine($"  shaders: {matched}/{shaders.Count + unmatched.Count} native payloads match 1.1; all PPtrs resolve");

        var original = LoadSource(manager, sourceRoot, sourceFiles, level);
        var rebuilt = manager.LoadAssetsFile(Path.Combine(extractedRoot, sceneFile), false);
        foreach (var classId in renderClasses)
        {
            var originalCount = original.file.GetAssetsOfType(classId).Count;
            var rebuiltCount = rebuilt.file.GetAssetsOfType(classId).Count;
            if (originalCount == rebuiltCount)
                Console.WriteLine($"  {classId}: {rebuiltCount} (= 1.1)");
            else
            {
                warnings++;
                Console.WriteLine($"  WARNING {classId}: 1.1={originalCount} bundle={rebuiltCount}");
            }
        }
        var originalBehaviours = original.file.GetAssetsOfType(AssetClassID.MonoBehaviour).Count;
        var rebuiltBehaviours = rebuilt.file.GetAssetsOfType(AssetClassID.MonoBehaviour).Count;
        Console.WriteLine($"  MonoBehaviour: 1.1={originalBehaviours} bundle={rebuiltBehaviours} (intentionally unavailable runtime classes={originalBehaviours - rebuiltBehaviours})");
        foreach (var (cls, n) in ScriptHistogram(manager, rebuilt, extractedRoot).OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal))
            Console.WriteLine($"    {n,4} x {cls}");

        VerifyGeneratedReflection(manager, rebuilt, extractedRoot, sourceRoot, sourceFiles, level, ref warnings);
    }

    Console.WriteLine($"VERIFY OK: {verified} native Shader objects are field-identical to EFT 1.1 after normalization; {warnings} warning(s) (see WARNING lines).");
    manager.UnloadAll();
    return 0;
}

/// <summary>包里 / 解包目录里的 `BuildPlayer-<场景>.sharedAssets` → (场景名, 场景文件名, 共享资产文件名)。</summary>
static IEnumerable<(string Scene, string SceneFile, string SharedFile)> BundleScenes(IEnumerable<string> names)
{
    foreach (var n in names)
    {
        var m = System.Text.RegularExpressions.Regex.Match(n, @"^BuildPlayer-(.+)\.sharedAssets$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success)
            yield return (m.Groups[1].Value, "BuildPlayer-" + m.Groups[1].Value, n);
    }
}

/// <summary>
/// 目标文件里的每个 Shader 对象 → 1.1 来源。先按名字（ResolveShaderSources 三级查找）；名字为空或按名字找不到的，
/// 再按「目标里哪个材质引用了它 → 1.1 里同名材质引用的是哪个 Shader 对象」兜底（09-05：Skier_Scripts 里有一个无名 shader 壳）。
/// 同名多份（Unity 顺手打进来的内置 Standard）各自都换。返回 (逐对象任务, 按名字的映射(给旧表对照), 没找到的)。
/// </summary>
static (List<(AssetFileInfo Info, string Name, SourceRef Source)> Jobs, Dictionary<string, SourceRef> ByName, List<string> Unmatched) ResolveTargetShaders(
    AssetsManager manager,
    AssetsFileInstance target,
    string sourceRoot,
    IDictionary<string, AssetsFileInstance> sourceFiles,
    string levelName,
    Func<IEnumerable<long>, Dictionary<long, string>>? projectHints = null)
{
    manager.LoadClassDatabaseFromPackage(target.file.Metadata.UnityVersion);
    var shaders = target.file.GetAssetsOfType(AssetClassID.Shader)
        .Select(info => (Info: info, Name: ReadName(manager.GetBaseField(target, info)))).ToList();
    var names = shaders.Select(s => s.Name).Where(n => n.Length > 0 && n != "<unnamed>").ToList();
    var byName = ResolveShaderSources(manager, sourceRoot, sourceFiles, levelName, names, out _);
    Dictionary<long, SourceRef>? byMaterial = null;
    var jobs = new List<(AssetFileInfo, string, SourceRef)>();
    var pending = new List<(AssetFileInfo Info, string Name)>();
    foreach (var (info, name) in shaders)
    {
        var known = name.Length > 0 && name != "<unnamed>";
        if (known && byName.TryGetValue(name, out var src))
        {
            jobs.Add((info, name, src));
            continue;
        }
        byMaterial ??= ResolveShadersByMaterials(manager, target, sourceRoot, sourceFiles, levelName);
        if (byMaterial.TryGetValue(info.PathId, out var viaMaterial))
        {
            jobs.Add((info, known ? name : string.Empty, viaMaterial));
            Console.WriteLine($"  shader '{name}' (pathID {info.PathId}): resolved through a material -> {viaMaterial.FileName}:{viaMaterial.PathId}");
            continue;
        }
        pending.Add((info, name));
    }
    var unmatched = new List<string>();
    if (pending.Count > 0)
    {
        // ③ 无名壳：去 Unity 工程的场景 YAML 里按「引用它的组件字段名」找回 .shader 文件的 Shader "名字"，再按名字找 1.1 对象
        var hints = projectHints?.Invoke(pending.Select(p => p.Info.PathId)) ?? new Dictionary<long, string>();
        var hintNames = hints.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var hintSources = hintNames.Count > 0 ? ResolveShaderSources(manager, sourceRoot, sourceFiles, levelName, hintNames, out _) : new Dictionary<string, SourceRef>(StringComparer.OrdinalIgnoreCase);
        foreach (var (info, name) in pending)
        {
            if (hints.TryGetValue(info.PathId, out var hinted) && hintSources.TryGetValue(hinted, out var src))
            {
                jobs.Add((info, string.Empty, src));
                Console.WriteLine($"  shader '{name}' (pathID {info.PathId}): resolved through the project's component field as '{hinted}' -> {src.FileName}:{src.PathId}");
                continue;
            }
            unmatched.Add($"'{name}' (pathID {info.PathId})" + (hints.TryGetValue(info.PathId, out var h) ? $" [project says '{h}', not found in 1.1]" : ""));
        }
    }
    return (jobs, byName, unmatched);
}

/// <summary>
/// 无名 shader 壳的名字回溯：目标场景文件里哪个 MonoBehaviour 的哪个字段引用了它（bundle 带 TypeTree，字段读得出来）→
/// 工程里该场景的 .unity YAML 里同名字段写的是哪个 guid → 那份 .shader 文件第一行的 Shader "名字"。
/// 1.1 的 level 文件没有 TypeTree，源侧的字段读不出来，所以只能借工程（AssetRipper 导出即 1.1 数据）这条路。
/// </summary>
static Dictionary<long, string> NamesFromProjectFields(
    AssetsManager manager,
    AssetsFileInstance? sceneInstance,
    string sharedFile,
    string sceneName,
    string? scenesDir,
    IEnumerable<long> pathIds)
{
    var result = new Dictionary<long, string>();
    var wanted = new HashSet<long>(pathIds);
    if (wanted.Count == 0 || sceneInstance == null || scenesDir == null)
        return result;
    var yamlPath = Path.Combine(scenesDir, sceneName, sceneName + ".unity");
    if (!File.Exists(yamlPath))
    {
        Console.WriteLine($"  project scene not found for field hints: {yamlPath}");
        return result;
    }
    var sharedIndex = -1;
    for (var i = 0; i < sceneInstance.file.Metadata.Externals.Count; i++)
        if (Path.GetFileName(sceneInstance.file.Metadata.Externals[i].PathName.Replace('/', Path.DirectorySeparatorChar)).Equals(sharedFile, StringComparison.OrdinalIgnoreCase))
            sharedIndex = i + 1;
    if (sharedIndex < 0)
        return result;

    // 目标场景：pathID → 引用它的字段名
    var fieldsByPath = new Dictionary<long, HashSet<string>>();
    manager.LoadClassDatabaseFromPackage(sceneInstance.file.Metadata.UnityVersion);
    foreach (var info in sceneInstance.file.GetAssetsOfType(AssetClassID.MonoBehaviour))
    {
        AssetTypeValueField field;
        try { field = manager.GetBaseField(sceneInstance, info); }
        catch { continue; }
        Walk(field, null);
    }
    void Walk(AssetTypeValueField f, string? parentName)
    {
        if (IsPPtr(f))
        {
            if (f["m_FileID"].AsInt == sharedIndex && wanted.Contains(f["m_PathID"].AsLong) && parentName != null)
            {
                if (!fieldsByPath.TryGetValue(f["m_PathID"].AsLong, out var set))
                    fieldsByPath[f["m_PathID"].AsLong] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(parentName);
            }
            return;
        }
        foreach (var child in f.Children)
            Walk(child, child.FieldName);
    }
    if (fieldsByPath.Count == 0)
        return result;

    // 工程：.shader 文件 guid → Shader "名字"
    var shaderNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var meta in Directory.EnumerateFiles(scenesDir, "*.shader.meta", SearchOption.AllDirectories))
    {
        var guid = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(meta), @"guid: ([0-9a-f]{32})").Groups[1].Value;
        if (guid.Length == 0) continue;
        var shaderFile = meta[..^5];
        if (!File.Exists(shaderFile)) continue;
        string? nameLine;
        using (var reader = new StreamReader(shaderFile))
            nameLine = reader.ReadLine();
        var m = System.Text.RegularExpressions.Regex.Match(nameLine ?? "", "Shader\\s+\"([^\"]+)\"");
        if (m.Success) shaderNames.TryAdd(guid, m.Groups[1].Value);
    }
    var yaml = File.ReadAllText(yamlPath);
    foreach (var (pathId, fields) in fieldsByPath)
    {
        foreach (var fieldName in fields)
        {
            var guids = System.Text.RegularExpressions.Regex.Matches(yaml, @"(?m)^\s+" + System.Text.RegularExpressions.Regex.Escape(fieldName) + @": \{fileID: \d+, guid: ([0-9a-f]{32})")
                .Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var names = guids.Where(shaderNames.ContainsKey).Select(g => shaderNames[g]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (names.Count == 1)
            {
                result[pathId] = names[0];
                Console.WriteLine($"  unnamed shader pathID {pathId}: referenced by field '{fieldName}' -> project .shader says '{names[0]}'");
                break;
            }
            if (names.Count > 1)
                Console.WriteLine($"  unnamed shader pathID {pathId}: field '{fieldName}' is ambiguous in the project scene ({string.Join(", ", names)})");
        }
    }
    return result;
}

/// <summary>目标里每个材质 → 它引用的本地 Shader 对象 pathID；1.1 里同名材质引用的 Shader 对象就是该 pathID 的来源。</summary>
static Dictionary<long, SourceRef> ResolveShadersByMaterials(
    AssetsManager manager,
    AssetsFileInstance target,
    string sourceRoot,
    IDictionary<string, AssetsFileInstance> sourceFiles,
    string levelName)
{
    var scene = LoadSource(manager, sourceRoot, sourceFiles, levelName);
    var files = new List<AssetsFileInstance> { scene };
    foreach (var external in scene.file.Metadata.Externals)
    {
        var name = Path.GetFileName(external.PathName.Replace('/', Path.DirectorySeparatorChar));
        if (name.Length > 0 && File.Exists(Path.Combine(sourceRoot, name)) && !files.Any(f => string.Equals(Path.GetFileName(f.path), name, StringComparison.OrdinalIgnoreCase)))
            files.Add(LoadSource(manager, sourceRoot, sourceFiles, name));
    }
    var sourceMaterials = new Dictionary<string, SourceRef>(StringComparer.Ordinal);
    foreach (var file in files)
    {
        manager.LoadClassDatabaseFromPackage(file.file.Metadata.UnityVersion);
        foreach (var info in file.file.GetAssetsOfType(AssetClassID.Material))
        {
            var material = manager.GetBaseField(file, info);
            var ptr = material["m_Shader"];
            var fileId = ptr["m_FileID"].AsInt;
            var pathId = ptr["m_PathID"].AsLong;
            if (pathId == 0)
                continue;
            string owner;
            if (fileId == 0)
                owner = Path.GetFileName(file.path);
            else if (fileId >= 1 && fileId <= file.file.Metadata.Externals.Count)
            {
                owner = NormalizeSourceName(file.file.Metadata.Externals[fileId - 1].PathName);
                if (owner.Contains("unity_builtin_extra", StringComparison.OrdinalIgnoreCase) || owner.Contains("unity default resources", StringComparison.OrdinalIgnoreCase))
                    continue;
                owner = Path.GetFileName(owner.Replace('/', Path.DirectorySeparatorChar));
            }
            else
                continue;
            if (!File.Exists(Path.Combine(sourceRoot, owner)))
                continue;
            var ownerFile = LoadSource(manager, sourceRoot, sourceFiles, owner);
            var shaderInfo = ownerFile.file.GetAssetInfo(pathId);
            if (shaderInfo == null || shaderInfo.GetTypeId(ownerFile.file) != (int)AssetClassID.Shader)
                continue;
            sourceMaterials.TryAdd(ReadName(material), new SourceRef(owner, pathId));
        }
    }
    var map = new Dictionary<long, SourceRef>();
    manager.LoadClassDatabaseFromPackage(target.file.Metadata.UnityVersion);
    foreach (var info in target.file.GetAssetsOfType(AssetClassID.Material))
    {
        var material = manager.GetBaseField(target, info);
        var ptr = material["m_Shader"];
        if (ptr["m_FileID"].AsInt != 0 || ptr["m_PathID"].AsLong == 0)
            continue;
        if (sourceMaterials.TryGetValue(ReadName(material), out var src))
            map.TryAdd(ptr["m_PathID"].AsLong, src);
    }
    return map;
}

/// <summary>目标文件里每个 shader 名 → 1.1 里的 (文件, pathID)。三级查找见文件头注释；找不到的进 unmatched。</summary>
static Dictionary<string, SourceRef> ResolveShaderSources(
    AssetsManager manager,
    string sourceRoot,
    IDictionary<string, AssetsFileInstance> sourceFiles,
    string levelName,
    IEnumerable<string> names,
    out List<string> unmatched)
{
    var scene = LoadSource(manager, sourceRoot, sourceFiles, levelName);
    var sceneAndExternals = new List<AssetsFileInstance> { scene };
    foreach (var external in scene.file.Metadata.Externals)
    {
        var name = Path.GetFileName(external.PathName.Replace('/', Path.DirectorySeparatorChar));
        if (name.Length == 0 || !File.Exists(Path.Combine(sourceRoot, name)))
            continue;
        if (sceneAndExternals.Any(f => string.Equals(Path.GetFileName(f.path), name, StringComparison.OrdinalIgnoreCase)))
            continue;
        sceneAndExternals.Add(LoadSource(manager, sourceRoot, sourceFiles, name));
    }
    var files = new List<AssetsFileInstance>(sceneAndExternals);
    foreach (var extra in new[] { "globalgamemanagers.assets", "resources.assets" })
        if (File.Exists(Path.Combine(sourceRoot, extra)) && !files.Any(f => string.Equals(Path.GetFileName(f.path), extra, StringComparison.OrdinalIgnoreCase)))
            files.Add(LoadSource(manager, sourceRoot, sourceFiles, extra));

    // ② 同名候选
    var byName = new Dictionary<string, List<SourceRef>>(StringComparer.OrdinalIgnoreCase);
    var nameAt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // "file:pathId" → shader 名
    foreach (var file in files)
    {
        manager.LoadClassDatabaseFromPackage(file.file.Metadata.UnityVersion);
        var fileName = Path.GetFileName(file.path);
        foreach (var info in file.file.GetAssetsOfType(AssetClassID.Shader))
        {
            var n = ReadName(manager.GetBaseField(file, info));
            if (!byName.TryGetValue(n, out var list))
                byName[n] = list = new List<SourceRef>();
            list.Add(new SourceRef(fileName, info.PathId));
            nameAt[$"{fileName}:{info.PathId}"] = n;
        }
    }
    // ① 原场景材质真正引用的
    var byMaterial = new Dictionary<string, SourceRef>(StringComparer.OrdinalIgnoreCase);
    foreach (var file in sceneAndExternals)
    {
        manager.LoadClassDatabaseFromPackage(file.file.Metadata.UnityVersion);
        foreach (var info in file.file.GetAssetsOfType(AssetClassID.Material))
        {
            var ptr = manager.GetBaseField(file, info)["m_Shader"];
            var fileId = ptr["m_FileID"].AsInt;
            var pathId = ptr["m_PathID"].AsLong;
            if (pathId == 0)
                continue;
            string owner;
            if (fileId == 0)
                owner = Path.GetFileName(file.path);
            else if (fileId >= 1 && fileId <= file.file.Metadata.Externals.Count)
                owner = Path.GetFileName(file.file.Metadata.Externals[fileId - 1].PathName.Replace('/', Path.DirectorySeparatorChar));
            else
                continue;
            if (nameAt.TryGetValue($"{owner}:{pathId}", out var shaderName))
                byMaterial.TryAdd(shaderName, new SourceRef(owner, pathId));
        }
    }

    var result = new Dictionary<string, SourceRef>(StringComparer.OrdinalIgnoreCase);
    unmatched = new List<string>();
    foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
    {
        if (byMaterial.TryGetValue(name, out var fromMaterial))
        {
            result[name] = fromMaterial;
            continue;
        }
        if (byName.TryGetValue(name, out var candidates))
        {
            var pick = candidates.OrderBy(Rank).ThenBy(c => c.PathId).First();
            result[name] = pick;
            if (candidates.Count > 1)
                Console.WriteLine($"  shader '{name}': {candidates.Count} same-name candidates, picked {pick.FileName}:{pick.PathId}");
            continue;
        }
        var global = GlobalShader(manager, sourceRoot, name);
        if (global is { } g)
        {
            result[name] = g;
            Console.WriteLine($"  shader '{name}': found by global scan in {g.FileName}:{g.PathId}");
            continue;
        }
        unmatched.Add(name);
    }
    Console.WriteLine($"  shader sources for {levelName}: {result.Count} resolved ({result.Count(r => byMaterial.ContainsKey(r.Key))} via scene materials), {unmatched.Count} unmatched");
    return result;

    static int Rank(SourceRef r)
    {
        var f = r.FileName.ToLowerInvariant();
        if (f == "globalgamemanagers.assets") return 0;
        if (f == "resources.assets") return 1;
        var m = System.Text.RegularExpressions.Regex.Match(f, @"^sharedassets(\d+)\.assets$");
        return m.Success ? 2 + int.Parse(m.Groups[1].Value) : 100000;
    }
}

/// <summary>③ 全盘扫描：EFT111 数据目录下所有 .assets 里的 Shader 名 → 位置，一次性建好缓存到工具目录。</summary>
static SourceRef? GlobalShader(AssetsManager manager, string sourceRoot, string name)
{
    if (GlobalShaderIndex.Map == null)
    {
        var map = new Dictionary<string, SourceRef>(StringComparer.OrdinalIgnoreCase);
        var cache = Path.Combine(AppContext.BaseDirectory, "shader_index.tsv");
        if (File.Exists(cache))
        {
            foreach (var line in File.ReadLines(cache))
            {
                var parts = line.Split('\t');
                if (parts.Length == 3 && long.TryParse(parts[2], out var pid))
                    map.TryAdd(parts[0], new SourceRef(parts[1], pid));
            }
            Console.WriteLine($"  global shader index loaded from cache: {map.Count} names");
        }
        else
        {
            Console.WriteLine("  building global shader index over all 1.1 .assets files (one-time, cached afterwards)...");
            var scan = new AssetsManager();
            scan.LoadClassPackage(Cfg.Tpk);
            var files = Directory.EnumerateFiles(sourceRoot, "*.assets").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            var n = 0;
            foreach (var path in files)
            {
                AssetsFileInstance inst;
                try
                {
                    inst = scan.LoadAssetsFile(path, false);
                    scan.LoadClassDatabaseFromPackage(inst.file.Metadata.UnityVersion);
                }
                catch { continue; }
                var fileName = Path.GetFileName(path);
                foreach (var info in inst.file.GetAssetsOfType(AssetClassID.Shader))
                {
                    string shaderName;
                    try { shaderName = ReadName(scan.GetBaseField(inst, info)); }
                    catch { continue; }
                    map.TryAdd(shaderName, new SourceRef(fileName, info.PathId));
                    n++;
                }
                scan.UnloadAssetsFile(inst);
            }
            scan.UnloadAll();
            File.WriteAllLines(cache, map.Select(kv => $"{kv.Key}\t{kv.Value.FileName}\t{kv.Value.PathId}"));
            Console.WriteLine($"  global shader index built: {n} shader objects, {map.Count} distinct names, cached to {cache}");
        }
        GlobalShaderIndex.Map = map;
    }
    return GlobalShaderIndex.Map.TryGetValue(name, out var found) ? found : null;
}

/// <summary>Prapor / Vendors_Scripts 仍留着 09-05 之前手工核出来的映射表，通用查找的结果和它对一遍，差异逐条打出来。</summary>
static void CompareWithLegacyMap(string scene, IReadOnlyDictionary<string, SourceRef> requested)
{
    IReadOnlyDictionary<string, SourceRef>? legacy = scene.ToLowerInvariant() switch
    {
        "vendors_prapor" => FullPraporShaderMap(),
        "vendors_scripts" => FullScriptShaderMap(),
        _ => null
    };
    if (legacy == null)
        return;
    var diffs = 0;
    foreach (var (name, r) in requested)
    {
        if (!legacy.TryGetValue(name, out var l))
            continue;
        if (!string.Equals(l.FileName, r.FileName, StringComparison.OrdinalIgnoreCase) || l.PathId != r.PathId)
        {
            diffs++;
            Console.WriteLine($"  legacy-map difference '{name}': legacy {l.FileName}:{l.PathId} vs generic {r.FileName}:{r.PathId}");
        }
    }
    Console.WriteLine($"  legacy-map cross-check ({scene}): {diffs} difference(s) among {requested.Count} shaders ({legacy.Count} in legacy map)");
}

/// <summary>把 1.1 原场景 RenderSettings 指向的那张生成反射立方图（HDR）原样灌进目标包里对应的 Cubemap。</summary>
static void RestoreGeneratedReflection(
    AssetsManager manager,
    AssetsFileInstance target,
    long targetPathId,
    string sourceRoot,
    IDictionary<string, AssetsFileInstance> sourceFiles,
    string levelName)
{
    var source = FindSourceGeneratedReflection(manager, sourceRoot, sourceFiles, levelName);
    if (source == null)
    {
        Console.WriteLine("  generated reflection: 1.1 scene has none (bundle's left as built)");
        return;
    }
    var (owner, pathId) = source.Value;
    manager.LoadClassDatabaseFromPackage(target.file.Metadata.UnityVersion);
    var info = target.file.GetAssetInfo(targetPathId)
        ?? throw new InvalidDataException($"Missing generated reflection Cubemap pathID {targetPathId}");
    if (info.GetTypeId(target.file) != (int)AssetClassID.Cubemap)
        throw new InvalidDataException($"Generated reflection pathID {targetPathId} is not a Cubemap.");
    var field = MaterializeSource(manager, owner, pathId, AssetClassID.Cubemap, sourceRoot, out var bytes);
    info.SetNewData(field);
    Console.WriteLine($"  native HDR generated reflection: {Path.GetFileName(owner.path)}:{pathId} -> pathID {targetPathId}, format={field["m_TextureFormat"].AsInt}, bytes={bytes.Length}, SHA256={Convert.ToHexString(SHA256.HashData(bytes))}");
}

static (AssetsFileInstance Owner, long PathId)? FindSourceGeneratedReflection(
    AssetsManager manager,
    string sourceRoot,
    IDictionary<string, AssetsFileInstance> sourceFiles,
    string levelName)
{
    var scene = LoadSource(manager, sourceRoot, sourceFiles, levelName);
    manager.LoadClassDatabaseFromPackage(scene.file.Metadata.UnityVersion);
    var settings = scene.file.GetAssetsOfType(AssetClassID.RenderSettings);
    if (settings.Count != 1)
        return null;
    var reflection = manager.GetBaseField(scene, settings[0])["m_GeneratedSkyboxReflection"];
    var fileId = reflection["m_FileID"].AsInt;
    var pathId = reflection["m_PathID"].AsLong;
    if (pathId == 0)
        return null;
    if (fileId == 0)
        return (scene, pathId);
    if (fileId < 1 || fileId > scene.file.Metadata.Externals.Count)
        throw new InvalidDataException($"{levelName}: generated reflection has invalid fileID {fileId}");
    var name = Path.GetFileName(scene.file.Metadata.Externals[fileId - 1].PathName.Replace('/', Path.DirectorySeparatorChar));
    return (LoadSource(manager, sourceRoot, sourceFiles, name), pathId);
}

static void VerifyGeneratedReflection(
    AssetsManager manager,
    AssetsFileInstance rebuilt,
    string extractedRoot,
    string sourceRoot,
    IDictionary<string, AssetsFileInstance> sourceFiles,
    string levelName,
    ref int warnings)
{
    manager.LoadClassDatabaseFromPackage(rebuilt.file.Metadata.UnityVersion);
    var settings = rebuilt.file.GetAssetsOfType(AssetClassID.RenderSettings);
    if (settings.Count != 1)
    {
        Console.WriteLine("  reflection: bundle scene has no RenderSettings");
        return;
    }
    var reflection = manager.GetBaseField(rebuilt, settings[0])["m_GeneratedSkyboxReflection"];
    var fileId = reflection["m_FileID"].AsInt;
    var pathId = reflection["m_PathID"].AsLong;
    var source = FindSourceGeneratedReflection(manager, sourceRoot, sourceFiles, levelName);
    if (pathId == 0 || fileId < 1 || fileId > rebuilt.file.Metadata.Externals.Count)
    {
        if (source != null) { warnings++; Console.WriteLine("  WARNING reflection: 1.1 scene has a generated reflection but the bundle scene has none"); }
        else Console.WriteLine("  reflection: none on either side");
        return;
    }
    if (source == null)
    {
        Console.WriteLine("  reflection: bundle has one, 1.1 scene has none (left as built)");
        return;
    }
    var fileName = Path.GetFileName(rebuilt.file.Metadata.Externals[fileId - 1].PathName.Replace('/', Path.DirectorySeparatorChar));
    var shared = manager.LoadAssetsFile(Path.Combine(extractedRoot, fileName), false);
    var info = shared.file.GetAssetInfo(pathId)
        ?? throw new InvalidDataException($"Missing bundle generated reflection Cubemap {fileName}:{pathId}");
    var sourceField = MaterializeSource(manager, source.Value.Owner, source.Value.PathId, AssetClassID.Cubemap, sourceRoot, out var bytes);
    manager.LoadClassDatabaseFromPackage(shared.file.Metadata.UnityVersion);
    var rebuiltField = manager.GetBaseField(shared, info);
    var sourceHash = Convert.ToHexString(SHA256.HashData(sourceField.WriteToByteArray(false)));
    var rebuiltHash = Convert.ToHexString(SHA256.HashData(rebuiltField.WriteToByteArray(false)));
    if (!string.Equals(sourceHash, rebuiltHash, StringComparison.Ordinal))
        throw new InvalidDataException("Native generated skybox reflection Cubemap payload mismatch.");
    Console.WriteLine($"  reflection: {fileName}:{pathId} format={rebuiltField["m_TextureFormat"].AsInt} bytes={bytes.Length} = 1.1 (SHA256 {Convert.ToHexString(SHA256.HashData(bytes))})");
}

/// <summary>场景文件里 MonoBehaviour 按「命名空间.类名」计数（跨文件解析 MonoScript）；脚本丢了的记 &lt;null script&gt;。</summary>
static Dictionary<string, int> ScriptHistogram(AssetsManager manager, AssetsFileInstance scene, string extractedRoot)
{
    var files = new Dictionary<string, AssetsFileInstance>(StringComparer.OrdinalIgnoreCase)
    {
        [Path.GetFileName(scene.path)] = scene
    };
    var hist = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (var behaviourInfo in scene.file.GetAssetsOfType(AssetClassID.MonoBehaviour))
    {
        manager.LoadClassDatabaseFromPackage(scene.file.Metadata.UnityVersion);
        var scriptRef = manager.GetBaseField(scene, behaviourInfo)["m_Script"];
        var fileId = scriptRef["m_FileID"].AsInt;
        var pathId = scriptRef["m_PathID"].AsLong;
        var key = "<null script>";
        if (pathId != 0)
        {
            AssetsFileInstance? owner = null;
            if (fileId == 0)
                owner = scene;
            else if (fileId >= 1 && fileId <= scene.file.Metadata.Externals.Count)
            {
                var fileName = Path.GetFileName(scene.file.Metadata.Externals[fileId - 1].PathName.Replace('/', Path.DirectorySeparatorChar));
                if (!files.TryGetValue(fileName, out owner))
                {
                    var full = Path.Combine(extractedRoot, fileName);
                    owner = File.Exists(full) ? manager.LoadAssetsFile(full, false) : null;
                    files[fileName] = owner!;
                }
            }
            var scriptInfo = owner?.file.GetAssetInfo(pathId);
            if (owner != null && scriptInfo != null && scriptInfo.GetTypeId(owner.file) == (int)AssetClassID.MonoScript)
            {
                manager.LoadClassDatabaseFromPackage(owner.file.Metadata.UnityVersion);
                var script = manager.GetBaseField(owner, scriptInfo);
                var ns = script["m_Namespace"].AsString;
                key = (ns.Length > 0 ? ns + "." : "") + script["m_ClassName"].AsString + " [" + script["m_AssemblyName"].AsString + "]";
            }
            else
                key = $"<unresolved script {fileId}:{pathId}>";
        }
        hist[key] = hist.GetValueOrDefault(key) + 1;
    }
    return hist;
}

readonly record struct SourceRef(string FileName, long PathId);
readonly record struct ResolvedRef(string FileName, long PathId, bool IsBuiltIn);
readonly record struct PatchResult(string InternalName, int Replaced, int Added, IReadOnlyCollection<string> Names);

/// <summary>1.1 商人房间场景 → 编进本体的 level 文件（Dev_Note M4.1 的表；09-05 用 BundleAudit names 逐个核过 Position_Camera_* 名字）。</summary>
static class Scenes
{
    public static readonly Dictionary<string, string> Levels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Vendors_Fence"] = "level638",
        ["Vendors_Jaeger"] = "level639",
        ["Vendors_Mechanic"] = "level640",
        ["Vendors_Peacekeeper"] = "level641",
        ["Vendors_Prapor"] = "level642",
        ["Vendors_Ragman"] = "level643",
        ["Vendors_Skier"] = "level644",
        ["Vendors_Therapist"] = "level645",
        ["Vendors_Scripts"] = "level646",
        ["Vendors_Peacekeeper_Scripts"] = "level647",
        ["Vendors_Jaeger_Scripts"] = "level648",
        ["Vendors_Skier_Scripts"] = "level649",
    };

    public static string LevelOf(string scene) =>
        Levels.TryGetValue(scene, out var level)
            ? level
            : throw new InvalidDataException($"No EFT 1.1 level file known for scene '{scene}' (add it to Scenes.Levels).");
}

static class GlobalShaderIndex
{
    public static Dictionary<string, SourceRef>? Map;
}

static class Cfg
{
    public static string Tpk = "";
}
