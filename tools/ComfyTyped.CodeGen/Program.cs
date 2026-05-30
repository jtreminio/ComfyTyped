using System.Globalization;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace ComfyTyped.CodeGen;

public static partial class Program
{
    private const string CoreMarkerNamespace = "ComfyTyped.Types";
    private const string CoreNodeRegistrationsTypeName = "ComfyTyped.Generated.NodeRegistrations";
    private const string CoreNodeRegistryTypeName = "ComfyTyped.Core.NodeRegistry";
    private const string CoreIComfyTypeName = "ComfyTyped.Types.IComfyType";
    private const string CoreComfyNodeTypeName = "ComfyTyped.Core.ComfyNode";

    // Defaults applied by --root for generating ComfyTyped's own nodes.
    private const string RootOutputDir = "src/Generated";
    private const string RootNamespace = "ComfyTyped.Generated";
    private const string RootMarkerNamespace = "ComfyTyped.Types";
    private const string RootRegistrationsClass = "NodeRegistrations";

    // ComfyUI primitive types that can carry literal values.
    private static readonly Dictionary<string, (string Marker, string CSharp)> PrimitiveTypes = new()
    {
        ["INT"] = ("IntType", "long"),
        ["FLOAT"] = ("FloatType", "double"),
        ["STRING"] = ("StringType", "string"),
        ["BOOLEAN"] = ("BooleanType", "bool"),
    };

    // Hand-written marker mapping. Mirrors src/Types/IOTypes.cs. Anything not in this
    // map (and not in --core-assembly's IComfyType set when in diff mode) gets an
    // auto-generated marker class emitted next to the node files.
    private static readonly Dictionary<string, string> CoreTypeMapping = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MODEL"] = "ModelType",
        ["CLIP"] = "ClipType",
        ["VAE"] = "VaeType",
        ["LATENT"] = "LatentType",
        ["IMAGE"] = "ImageType",
        ["MASK"] = "MaskType",
        ["CONDITIONING"] = "ConditioningType",
        ["AUDIO"] = "AudioType",
        ["VIDEO"] = "VideoType",
        ["INT"] = "IntType",
        ["FLOAT"] = "FloatType",
        ["STRING"] = "StringType",
        ["BOOLEAN"] = "BooleanType",
        ["SAMPLER"] = "SamplerType",
        ["SIGMAS"] = "SigmasType",
        ["GUIDER"] = "GuiderType",
        ["NOISE"] = "NoiseType",
        ["CLIP_VISION"] = "ClipVisionType",
        ["CLIP_VISION_OUTPUT"] = "ClipVisionOutputType",
        ["STYLE_MODEL"] = "StyleModelType",
        ["CONTROL_NET"] = "ControlNetType",
        ["GLIGEN"] = "GligenType",
        ["HOOKS"] = "HooksType",
        ["UPSCALE_MODEL"] = "UpscaleModelType",
        ["LATENT_UPSCALE_MODEL"] = "LatentUpscaleModelType",
        ["IPADAPTER"] = "IpAdapterType",
        ["MODEL_PATCH"] = "ModelPatchType",
        ["LORA_MODEL"] = "LoraModelType",
        ["BBOX"] = "BboxType",
        ["TORCH_COMPILE_ARGS"] = "TorchCompileArgsType",
        ["COMFY_MATCHTYPE_V3"] = "ComfyMatchTypeV3",
    };

    // SwarmUI's "native" surface area: bundled packs (Swarm*) plus installable features
    // registered in upstream SwarmUI/src/Core/InstallableFeatures.cs. Re-sync this list
    // if SwarmUI adds/removes a RegisterInstallableFeature call.
    private static readonly HashSet<string> SwarmNativeModules = new(StringComparer.Ordinal)
    {
        "custom_nodes.SwarmComfyCommon",
        "custom_nodes.SwarmComfyExtra",
        "custom_nodes.ComfyUI_IPAdapter_plus",
        "custom_nodes.comfyui_controlnet_aux",
        "custom_nodes.ComfyUI-Frame-Interpolation",
        "custom_nodes.ComfyUI-GIMM-VFI",
        "custom_nodes.ComfyUI_TensorRT",
        "custom_nodes.ComfyUI-segment-anything-2",
        "custom_nodes.ComfyUI_bnb_nf4_fp4_Loaders",
        "custom_nodes.ComfyUI-GGUF",
        "custom_nodes.ComfyUI_ExtraModels",
        "custom_nodes.ComfyUI-nunchaku",
        "custom_nodes.ComfyUI-TeaCache",
        "custom_nodes.ComfyUI-SAI_API",
    };

    private sealed record MarkerInfo(string ShortName, string Namespace);

    private sealed record Options(
        string ComfyJsonSource,
        string OutputDir,
        string Namespace,
        string MarkerNamespace,
        string RegistrationsClass,
        string? CoreAssemblyPath,
        bool NativeOnly,
        string? KeepListPath,
        string? FamiliesPath);

    /// <summary>Consumer-declared "always keep these typed bindings, even if unused" inputs.
    /// Read from the JSON pointed at by <c>--keep-list</c>. The JSON shape is:
    /// <code>
    /// {
    ///   "keep_modules": ["custom_nodes.ComfyUI-LTXVideo"],
    ///   "keep_class_types": ["SomeSpecificNode"]
    /// }
    /// </code>
    /// Each <c>keep_modules</c> entry is resolved against <c>object_info</c>'s
    /// <c>python_module</c> field; matching <c>class_type</c>s plus any explicit
    /// <c>keep_class_types</c> are written into <c>PruneManifest.g.cs</c>.
    /// </summary>
    internal sealed record KeepList(
        IReadOnlyList<string> Modules,
        IReadOnlyList<string> ClassTypes)
    {
        public bool IsEmpty => Modules.Count == 0 && ClassTypes.Count == 0;
    }

    /// <summary>Result of resolving a <see cref="KeepList"/> against the set of
    /// class_types actually emitted by codegen: the typed C# class names that
    /// should land in <c>PruneManifest.g.cs</c>, plus human-readable warnings
    /// for keep-list entries that resolved to zero generated nodes (typos, or
    /// modules where every class_type was already in core).</summary>
    internal sealed record KeepListResolution(
        IReadOnlyList<string> ClassNames,
        IReadOnlyList<string> Warnings);

    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "prune")
        {
            return RunPrune(args[1..]);
        }

        Options? opts = ParseArgs(args);
        if (opts is null)
        {
            return 1;
        }

        Dictionary<string, MarkerInfo> typeMapping = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string comfyType, string shortName) in CoreTypeMapping)
        {
            typeMapping[comfyType] = new MarkerInfo(shortName, CoreMarkerNamespace);
        }

        HashSet<string> classTypeSkipSet = new(StringComparer.Ordinal);
        HashSet<string> classNameSkipSet = new(StringComparer.Ordinal);
        if (opts.CoreAssemblyPath is not null)
        {
            int markersBefore = typeMapping.Count;
            LoadCoreSkipSets(opts.CoreAssemblyPath, classTypeSkipSet, classNameSkipSet, typeMapping);
            int extraMarkers = typeMapping.Count - markersBefore;
            Console.WriteLine(
                $"Diff mode: skipping {classTypeSkipSet.Count} class_types, "
                + $"reusing {extraMarkers} marker types from core.");
        }

        Dictionary<string, MarkerInfo> generatedMarkers = new(StringComparer.OrdinalIgnoreCase);
        JObject objectInfo = LoadComfyJson(opts.ComfyJsonSource);
        Directory.CreateDirectory(opts.OutputDir);
        ClearGeneratedFiles(opts.OutputDir);

        KeepList? keepList = opts.KeepListPath is null ? null : LoadKeepList(opts.KeepListPath);
        HashSet<string> forceInclude = ResolveForceIncludeClassTypes(keepList, objectInfo);

        IReadOnlyDictionary<string, IReadOnlyList<string>> familyMap =
            opts.FamiliesPath is null ? EmptyFamilyMap : LoadFamilies(opts.FamiliesPath);

        Dictionary<string, string> generatedClassTypeToClassName = new(StringComparer.Ordinal);

        int generated = 0;
        int skippedDiff = 0;
        int skippedNonNative = 0;
        int skippedParse = 0;
        int forceIncluded = 0;

        foreach (JProperty nodeProp in objectInfo.Properties())
        {
            if (nodeProp.Value is not JObject nodeInfo)
            {
                continue;
            }
            string classType = nodeProp.Name;

            if (classTypeSkipSet.Contains(classType))
            {
                skippedDiff++;
                continue;
            }
            // Force-included class_types bypass --native-only so keep-list entries
            // for non-bundled packs actually produce typed bindings.
            bool wouldDropAsNonNative =
                opts.NativeOnly && !IsNativeModule(GetPythonModule(nodeInfo));
            if (wouldDropAsNonNative && !forceInclude.Contains(classType))
            {
                skippedNonNative++;
                continue;
            }

            NodeDef? nodeDef = ParseNodeDef(
                classType, nodeInfo, typeMapping, generatedMarkers, opts.MarkerNamespace);
            if (nodeDef is null)
            {
                Console.Error.WriteLine($"  SKIP: {classType} (could not parse)");
                skippedParse++;
                continue;
            }

            if (classNameSkipSet.Contains(nodeDef.ClassName))
            {
                skippedDiff++;
                continue;
            }

            File.WriteAllText(
                Path.Combine(opts.OutputDir, $"{nodeDef.ClassName}.g.cs"),
                GenerateNodeClass(nodeDef, opts.Namespace, familyMap));
            generatedClassTypeToClassName[classType] = nodeDef.ClassName;
            generated++;
            // Increment only after a file is actually emitted, so a parse failure
            // or diff-name collision doesn't inflate the "Force-included" summary.
            if (wouldDropAsNonNative)
            {
                forceIncluded++;
            }
        }

        foreach ((string comfyType, MarkerInfo info) in generatedMarkers)
        {
            File.WriteAllText(
                Path.Combine(opts.OutputDir, $"{info.ShortName}.g.cs"),
                GenerateMarkerClass(comfyType, info));
        }

        File.WriteAllText(
            Path.Combine(opts.OutputDir, $"{opts.RegistrationsClass}.g.cs"),
            GenerateRegistrationFile(opts.Namespace, opts.RegistrationsClass));

        int manifestCount = 0;
        if (keepList is not null)
        {
            KeepListResolution res = ResolveKeepList(keepList, objectInfo, generatedClassTypeToClassName);
            foreach (string warning in res.Warnings)
            {
                Console.Error.WriteLine($"  WARN: {warning}");
            }
            File.WriteAllText(
                Path.Combine(opts.OutputDir, $"{PruneManifestFileName}"),
                GeneratePruneManifest(opts.Namespace, res.ClassNames));
            manifestCount = res.ClassNames.Count;
        }

        string manifestSummary = keepList is null
            ? ""
            : $" PruneManifest: {manifestCount} always-keep class name(s).";
        string forceIncludedSummary = forceIncluded > 0
            ? $" Force-included {forceIncluded} non-native node(s) via keep-list."
            : "";
        Console.WriteLine(
            $"Generated {generated} nodes and {generatedMarkers.Count} markers; "
            + $"skipped {skippedDiff} (in core), {skippedNonNative} (non-native), {skippedParse} (parse)."
            + forceIncludedSummary
            + manifestSummary);

        return 0;
    }

    internal const string PruneManifestClassName = "PruneManifest";
    internal const string PruneManifestFileName = "PruneManifest.g.cs";
    internal const string PruneManifestBeginMarker = "PRUNE-MANIFEST-BEGIN";
    internal const string PruneManifestEndMarker = "PRUNE-MANIFEST-END";

    /// <summary>Emit <c>PruneManifest.g.cs</c>. The format is deliberately simple:
    /// a static <c>string[] AlwaysKeep</c> array sandwiched between sentinel comment
    /// markers so the <c>prune</c> subcommand can parse it with a regex without
    /// caring about whitespace or surrounding C# code.</summary>
    internal static string GeneratePruneManifest(string ns, IEnumerable<string> classNames)
    {
        List<string> sorted = [.. classNames.Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)];

        StringBuilder sb = new();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine($"namespace {ns};");
        sb.AppendLine();
        sb.AppendLine("/// <summary>Typed node class names the <c>prune</c> tool must always keep,");
        sb.AppendLine("/// regardless of whether consumer source references them. Driven by the");
        sb.AppendLine("/// <c>--keep-list</c> JSON passed to ComfyTyped.CodeGen at generation time;");
        sb.AppendLine("/// regenerate after editing the keep-list — do not hand-edit this file.</summary>");
        sb.AppendLine($"public static class {PruneManifestClassName}");
        sb.AppendLine("{");
        sb.AppendLine($"    // {PruneManifestBeginMarker}");
        sb.AppendLine("    public static readonly string[] AlwaysKeep = new string[]");
        sb.AppendLine("    {");
        foreach (string name in sorted)
        {
            sb.AppendLine($"        \"{EscapeCSharpString(name)}\",");
        }
        sb.AppendLine("    };");
        sb.AppendLine($"    // {PruneManifestEndMarker}");
        sb.AppendLine("}");

        return sb.ToString();
    }

    /// <summary>Parse the always-keep class names out of a <c>PruneManifest.g.cs</c>
    /// file. Returns an empty set if the file doesn't exist or doesn't contain the
    /// expected sentinel block, so prune can run safely against generated trees
    /// produced without <c>--keep-list</c>.</summary>
    internal static HashSet<string> ReadPruneManifestKeepSet(string generatedDir)
    {
        HashSet<string> keep = new(StringComparer.Ordinal);
        string path = Path.Combine(generatedDir, PruneManifestFileName);
        if (!File.Exists(path))
        {
            return keep;
        }
        string text = File.ReadAllText(path);
        Match block = Regex.Match(
            text,
            $@"{Regex.Escape(PruneManifestBeginMarker)}(.*?){Regex.Escape(PruneManifestEndMarker)}",
            RegexOptions.Singleline);
        if (!block.Success)
        {
            return keep;
        }
        // Within the sentinel block, narrow further to the AlwaysKeep array literal
        // body so any incidental quoted identifier — XML doc trivia, attribute
        // arguments, or a stray comment — can't be misread as a keep entry.
        Match array = Regex.Match(
            block.Groups[1].Value,
            @"AlwaysKeep\s*=\s*new\s+string\s*\[\s*\]\s*\{(.*?)\}\s*;",
            RegexOptions.Singleline);
        if (!array.Success)
        {
            return keep;
        }
        foreach (Match m in Regex.Matches(array.Groups[1].Value, "\"([A-Za-z_][A-Za-z0-9_]*)\""))
        {
            keep.Add(m.Groups[1].Value);
        }
        return keep;
    }

    private static bool IsNativeModule(string? pythonModule) =>
        !string.IsNullOrEmpty(pythonModule)
        && (pythonModule == "nodes"
            || pythonModule.StartsWith("comfy_extras.", StringComparison.Ordinal)
            || SwarmNativeModules.Contains(pythonModule));

    /// <summary>Read a node's <c>python_module</c> defensively. ComfyUI always emits
    /// this field as a string in practice, but a single malformed entry (number,
    /// array, missing field, JSON null) would otherwise crash <c>Value&lt;string&gt;</c>
    /// or coerce numbers into nonsense module names — both of which would silently
    /// break the keep-list resolution. Returns null for anything not a string.</summary>
    private static string? GetPythonModule(JObject nodeInfo)
    {
        if (nodeInfo["python_module"] is JValue jv
            && jv.Type == JTokenType.String
            && jv.Value is string s)
        {
            return s;
        }
        return null;
    }

    // CLI

    private static Options? ParseArgs(string[] args)
    {
        bool root = args.Any(a => a == "--root");

        string? comfyJsonSource = null;
        string? outputDir = root ? RootOutputDir : null;
        string? ns = root ? RootNamespace : null;
        string? markerNs = root ? RootMarkerNamespace : null;
        string registrationsClass = RootRegistrationsClass;
        string? coreAssembly = null;
        bool nativeOnly = root;
        string? keepListPath = null;
        string? familiesPath = null;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "--root": break;
                case "--comfy-json": comfyJsonSource = NextArg(args, ref i, a); break;
                case "--output": outputDir = NextArg(args, ref i, a); break;
                case "--namespace": ns = NextArg(args, ref i, a); break;
                case "--marker-namespace": markerNs = NextArg(args, ref i, a); break;
                case "--registrations-class": registrationsClass = NextArg(args, ref i, a); break;
                case "--core-assembly": coreAssembly = NextArg(args, ref i, a); break;
                case "--native-only": nativeOnly = true; break;
                case "--keep-list": keepListPath = NextArg(args, ref i, a); break;
                case "--families": familiesPath = NextArg(args, ref i, a); break;
                case "--help" or "-h": PrintUsage(); return null;
                default:
                    Console.Error.WriteLine($"Unknown argument: {a}");
                    PrintUsage();
                    return null;
            }
        }

        if (comfyJsonSource is null)
        {
            return Fail("--comfy-json is required.");
        }
        if (outputDir is null)
        {
            return Fail("--output is required (or pass --root for repo defaults).");
        }
        if (ns is null)
        {
            return Fail("--namespace is required (or pass --root for repo defaults).");
        }

        return new Options(
            comfyJsonSource, outputDir, ns, markerNs ?? ns, registrationsClass,
            coreAssembly, nativeOnly, keepListPath, familiesPath);
    }

    private static Options? Fail(string message)
    {
        Console.Error.WriteLine(message);
        PrintUsage();

        return null;
    }

    private static string NextArg(string[] args, ref int i, string flag)
    {
        if (i + 1 >= args.Length)
        {
            throw new ArgumentException($"Flag {flag} requires a value.");
        }

        return args[++i];
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine($$"""
            Usage: ComfyTyped.CodeGen [options]
                   ComfyTyped.CodeGen prune --generated-dir <dir> --source <dir> [--source <dir>...] [--dry-run]

            Required:
              --comfy-json <path|url>          Source for ComfyUI object_info.
                                               If the value starts with http:// or https://,
                                               it is fetched over HTTP; otherwise it is read
                                               from disk.
                                               Example: http://127.0.0.1:8188/object_info

            Output:
              --root                           Generate ComfyTyped's own nodes. Sugar for:
                                                 --output {{RootOutputDir}}
                                                 --namespace {{RootNamespace}}
                                                 --marker-namespace {{RootMarkerNamespace}}
                                                 --registrations-class {{RootRegistrationsClass}}
                                                 --native-only
                                               Any of these flags after --root override.
              --output <dir>                   Output directory for *.g.cs files.
              --namespace <ns>                 Namespace for generated node classes.
              --marker-namespace <ns>          Namespace for auto-generated IComfyType marker
                                               classes (defaults to --namespace). Markers for
                                               ComfyUI types not already covered by the codegen's
                                               built-in mapping or core's existing marker types
                                               are emitted automatically.
              --registrations-class <name>     Static class name for node registrations
                                               (default: {{RootRegistrationsClass}}).

            Filtering:
              --core-assembly <path>           ComfyTyped.dll. When provided, class_types and
                                               marker types already in core are skipped/reused
                                               (diff mode).
              --native-only                    Only emit nodes whose python_module is `nodes`,
                                               starts with `comfy_extras.`, or is one of the
                                               SwarmUI-bundled / SwarmUI-installable packs
                                               (see SwarmNativeModules in source). Implied by --root.

            Prune manifest:
              --keep-list <path>               JSON file declaring typed bindings to ship
                                               even when no consumer source references them.
                                               Shape:
                                                 {
                                                   "keep_modules":     ["custom_nodes.X"],
                                                   "keep_class_types": ["SomeNode"]
                                                 }
                                               Each keep_modules entry is resolved against
                                               object_info's python_module field; matching
                                               nodes are force-emitted (bypassing
                                               --native-only) and listed alongside any
                                               keep_class_types entries in
                                               PruneManifest.g.cs. The `prune` subcommand
                                               reads that file and unconditionally keeps
                                               the listed class names.

            Family interfaces:
              --families <path>                JSON mapping a fully-qualified C# interface
                                               (hand-written under src/Families/) to the
                                               class_types that should implement it. Codegen
                                               appends the interface to each member node's
                                               base list. Shape:
                                                 {
                                                   "ComfyTyped.Families.IVaeDecode":
                                                     ["VAEDecode", "VAEDecodeTiled"]
                                                 }
                                               Underscore-prefixed keys (e.g. "_comment") are
                                               ignored. Member nodes must already expose the
                                               interface's slot properties or the regenerated
                                               file will not compile.

              -h, --help                       Show this message.
            """);
    }

    // Source loading

    private static JObject LoadComfyJson(string source)
    {
        bool isHttp = Uri.TryCreate(source, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        if (!isHttp)
        {
            return JObject.Parse(File.ReadAllText(source));
        }

        using HttpClient http = new() { Timeout = TimeSpan.FromMinutes(2) };
        HttpResponseMessage resp;
        try
        {
            resp = http.GetAsync(uri).GetAwaiter().GetResult();
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Failed to fetch {uri}: {ex.Message}", ex);
        }
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Fetching {uri} returned HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}.");
        }

        return JObject.Parse(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
    }

    // Keep-list parsing

    private static readonly HashSet<string> KeepListKnownKeys =
        new(StringComparer.Ordinal) { "keep_modules", "keep_class_types" };

    internal static KeepList LoadKeepList(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (FileNotFoundException ex)
        {
            throw new InvalidOperationException(
                $"Failed to load --keep-list: file not found at '{path}'.", ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new InvalidOperationException(
                $"Failed to load --keep-list: directory not found for '{path}'.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Failed to load --keep-list at '{path}': {ex.Message}", ex);
        }

        // Parse to JToken first so a top-level array / scalar produces a targeted
        // "must be a JSON object" error instead of leaking Newtonsoft's internal
        // "Current JsonReader item is not an object: StartArray" wording.
        JToken token;
        try
        {
            token = JToken.Parse(text);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to parse --keep-list at '{path}': {ex.Message}", ex);
        }
        if (token is not JObject obj)
        {
            throw new InvalidOperationException(
                $"--keep-list at '{path}' must be a JSON object "
                + $"(got {token.Type}). Expected shape: "
                + "{ \"keep_modules\": [...], \"keep_class_types\": [...] }.");
        }

        // Reject unknown top-level keys so `{ "keep_module": [...] }` (singular typo)
        // doesn't silently parse to an empty keep-list. The whole point of the manifest
        // is that the consumer can't tell from a quiet build whether the keep-list took
        // effect; failing loud here is the only way to make typos visible.
        foreach (JProperty p in obj.Properties())
        {
            if (!KeepListKnownKeys.Contains(p.Name))
            {
                throw new InvalidOperationException(
                    $"--keep-list at '{path}' contains unknown top-level key '{p.Name}'. "
                    + $"Allowed keys: {string.Join(", ", KeepListKnownKeys.OrderBy(k => k))}.");
            }
        }

        return new KeepList(
            ReadStringArray(obj, "keep_modules", path),
            ReadStringArray(obj, "keep_class_types", path));
    }

    private static List<string> ReadStringArray(JObject obj, string key, string path)
    {
        if (!obj.TryGetValue(key, out JToken? value))
        {
            return [];
        }
        if (value is not JArray arr)
        {
            // Wrong-typed value for a known key — e.g. `{ "keep_modules": "single" }`
            // — would otherwise silently produce an empty keep-list, the same
            // foot-gun the unknown-key check exists to prevent.
            throw new InvalidOperationException(
                $"--keep-list at '{path}' key '{key}' must be a JSON array of strings "
                + $"(got {value.Type}).");
        }
        List<string> result = [];
        foreach (JToken t in arr)
        {
            if (t is JValue jv && jv.Type == JTokenType.String && jv.Value is string s && s.Length > 0)
            {
                result.Add(s);
            }
            else
            {
                throw new InvalidOperationException(
                    $"--keep-list at '{path}' key '{key}' must be an array of "
                    + $"non-empty strings; got: {t}.");
            }
        }
        return result;
    }

    /// <summary>Resolve a <see cref="KeepList"/> against the <c>object_info</c> dump
    /// (for the <c>python_module</c> mapping) and the set of class_types that codegen
    /// actually emitted (passed via <paramref name="generatedClassTypeToClassName"/>).
    /// Modules and class_types that resolve to zero generated nodes produce warnings
    /// but never fail the run.</summary>
    internal static KeepListResolution ResolveKeepList(
        KeepList keepList,
        JObject objectInfo,
        IReadOnlyDictionary<string, string> generatedClassTypeToClassName)
    {
        HashSet<string> classNames = new(StringComparer.Ordinal);
        List<string> warnings = [];

        Dictionary<string, List<string>> classTypesByModule = new(StringComparer.Ordinal);
        foreach (JProperty p in objectInfo.Properties())
        {
            if (p.Value is not JObject info)
            {
                continue;
            }
            string? mod = GetPythonModule(info);
            if (string.IsNullOrEmpty(mod))
            {
                continue;
            }
            if (!classTypesByModule.TryGetValue(mod, out List<string>? bucket))
            {
                bucket = [];
                classTypesByModule[mod] = bucket;
            }
            bucket.Add(p.Name);
        }

        foreach (string module in keepList.Modules)
        {
            int hits = 0;
            if (classTypesByModule.TryGetValue(module, out List<string>? classTypes))
            {
                foreach (string ct in classTypes)
                {
                    if (generatedClassTypeToClassName.TryGetValue(ct, out string? className))
                    {
                        classNames.Add(className);
                        hits++;
                    }
                }
            }
            if (hits == 0)
            {
                warnings.Add(
                    classTypesByModule.ContainsKey(module)
                        ? $"keep_modules '{module}': matched class_types in object_info but none were generated "
                            + "(already in --core-assembly, filtered by --native-only, or otherwise skipped)."
                        : $"keep_modules '{module}': no class_types in object_info have this python_module "
                            + "(typo, or the pack isn't installed in the ComfyUI you dumped from).");
            }
        }

        foreach (string classType in keepList.ClassTypes)
        {
            if (generatedClassTypeToClassName.TryGetValue(classType, out string? className))
            {
                classNames.Add(className);
            }
            else
            {
                warnings.Add(
                    $"keep_class_types '{classType}': not present in generated output "
                        + "(typo, already in --core-assembly, filtered by --native-only, "
                        + "or otherwise skipped).");
            }
        }

        List<string> sorted = [.. classNames.OrderBy(n => n, StringComparer.Ordinal)];
        return new KeepListResolution(sorted, warnings);
    }

    /// <summary>Compute the set of ComfyUI class_types that the keep-list demands be
    /// emitted regardless of <c>--native-only</c> filtering. Returns an empty set when
    /// the keep-list is empty or null.</summary>
    internal static HashSet<string> ResolveForceIncludeClassTypes(
        KeepList? keepList, JObject objectInfo)
    {
        HashSet<string> result = new(StringComparer.Ordinal);
        if (keepList is null || keepList.IsEmpty)
        {
            return result;
        }
        HashSet<string> moduleSet = new(keepList.Modules, StringComparer.Ordinal);
        if (moduleSet.Count > 0)
        {
            foreach (JProperty p in objectInfo.Properties())
            {
                if (p.Value is not JObject info)
                {
                    continue;
                }
                string? mod = GetPythonModule(info);
                if (mod is not null && moduleSet.Contains(mod))
                {
                    result.Add(p.Name);
                }
            }
        }
        foreach (string ct in keepList.ClassTypes)
        {
            result.Add(ct);
        }
        return result;
    }

    // Diff mode

    private static void LoadCoreSkipSets(
        string coreAssemblyPath,
        HashSet<string> classTypes,
        HashSet<string> classNames,
        Dictionary<string, MarkerInfo> typeMapping)
    {
        Assembly asm = Assembly.LoadFrom(Path.GetFullPath(coreAssemblyPath));

        Type GetTypeOrThrow(string fullName) => asm.GetType(fullName)
            ?? throw new InvalidOperationException($"{fullName} not found in {coreAssemblyPath}.");

        Type registrations = GetTypeOrThrow(CoreNodeRegistrationsTypeName);
        Type registry = GetTypeOrThrow(CoreNodeRegistryTypeName);
        Type iComfyType = GetTypeOrThrow(CoreIComfyTypeName);
        Type comfyNode = GetTypeOrThrow(CoreComfyNodeTypeName);

        registrations
            .GetMethod("EnsureRegistered", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, null);

        IEnumerable<string> registered = (IEnumerable<string>)registry
            .GetProperty("RegisteredTypes", BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null)!;
        foreach (string s in registered)
        {
            classTypes.Add(s);
        }

        // Same ReflectionTypeLoadException tolerance as NodeRegistry — the codegen reflects
        // over the consumer's vendored ComfyTyped.dll without SwarmUI.dll on its load path.
        Type?[] candidates;
        try
        {
            candidates = asm.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            candidates = ex.Types;
        }

        foreach (Type? t in candidates)
        {
            if (t is null || !t.IsClass || t.IsAbstract)
            {
                continue;
            }
            if (iComfyType.IsAssignableFrom(t))
            {
                PropertyInfo? prop = t.GetProperty("TypeName", BindingFlags.Public | BindingFlags.Static);
                if (prop?.GetValue(null) is string typeName && t.Namespace is { Length: > 0 } markerNs)
                {
                    typeMapping[typeName] = new MarkerInfo(t.Name, markerNs);
                }
            }
            else if (comfyNode.IsAssignableFrom(t))
            {
                // C# class name (== output filename). Two ComfyUI class_types can sanitize
                // to the same C# name (e.g. "ImageCrop" vs "ImageCrop+"); skip the diff
                // node if its name would collide with a core node, since the core typed
                // class is what the consumer ends up using anyway.
                classNames.Add(t.Name);
            }
        }
    }

    // Output cleanup

    private static void ClearGeneratedFiles(string outputDir)
    {
        if (!Directory.Exists(outputDir))
        {
            return;
        }
        foreach (string file in Directory.EnumerateFiles(outputDir, "*.g.cs", SearchOption.TopDirectoryOnly))
        {
            File.Delete(file);
        }
    }

    // Parsing

    private sealed record InputDef(
        string Name,
        string PropertyName,
        string ComfyType,
        MarkerInfo Marker,
        bool Required,
        bool IsPrimitive,
        string? CSharpType,
        object? DefaultValue,
        bool IsList = false,
        string? Prefix = null,
        IReadOnlyList<string>? Names = null,
        int Min = 0,
        int Max = 0,
        MarkerInfo? ElementMarker = null,
        IReadOnlyList<string>? ComboValues = null);

    private sealed record OutputDef(
        string Name,
        string PropertyName,
        int SlotIndex,
        string ComfyType,
        MarkerInfo Marker);

    private sealed record NodeDef(
        string ClassType,
        string ClassName,
        List<InputDef> Inputs,
        List<OutputDef> Outputs,
        string? Category,
        string? Description);

    private static NodeDef? ParseNodeDef(
        string classType,
        JObject nodeInfo,
        Dictionary<string, MarkerInfo> typeMapping,
        Dictionary<string, MarkerInfo> generatedMarkers,
        string markerNamespace)
    {
        List<OutputDef> outputs = [];
        List<InputDef> inputs = [];

        // Parse outputs first so input names can dedupe against output property names.
        if (nodeInfo["output"] is JArray outputTypes)
        {
            JArray? outputNames = nodeInfo["output_name"] as JArray;
            for (int i = 0; i < outputTypes.Count; i++)
            {
                string comfyType = outputTypes[i]?.ToString() ?? "*";
                string slotName = outputNames is not null && i < outputNames.Count
                    ? outputNames[i]?.ToString() ?? comfyType
                    : comfyType;
                MarkerInfo marker = ResolveMarkerType(comfyType, typeMapping, generatedMarkers, markerNamespace);
                string propName = SanitizeOutputPropertyName(slotName, i, outputs);
                outputs.Add(new OutputDef(slotName, propName, i, comfyType, marker));
            }
        }

        if (nodeInfo["input"] is JObject inputSection)
        {
            ParseInputSection(
                inputSection["required"] as JObject, required: true,
                inputs, outputs, typeMapping, generatedMarkers, markerNamespace);
            ParseInputSection(
                inputSection["optional"] as JObject, required: false,
                inputs, outputs, typeMapping, generatedMarkers, markerNamespace);
        }

        return new NodeDef(
            classType,
            SanitizeClassName(classType),
            inputs,
            outputs,
            nodeInfo.Value<string>("category"),
            nodeInfo.Value<string>("description"));
    }

    private static void ParseInputSection(
        JObject? section,
        bool required,
        List<InputDef> inputs,
        List<OutputDef> outputs,
        Dictionary<string, MarkerInfo> typeMapping,
        Dictionary<string, MarkerInfo> generatedMarkers,
        string markerNamespace)
    {
        if (section is null)
        {
            return;
        }

        foreach (JProperty inputProp in section.Properties())
        {
            if (inputProp.Value is not JArray spec || spec.Count == 0)
            {
                continue;
            }

            string comfyType = spec[0] is JArray ? "COMBO" : spec[0]?.ToString() ?? "*";

            // A COMBO's spec[0] is the JArray of allowed values; capture the all-string ones so
            // codegen can surface them as discoverable constants (see EmitComboConstants).
            IReadOnlyList<string>? comboValues =
                spec[0] is JArray comboArr && comboArr.Count > 0 && comboArr.All(v => v.Type == JTokenType.String)
                    ? comboArr.Select(v => v.ToString()).ToList()
                    : null;

            // COMFY_AUTOGROW_V3: typed list of element type. Two shapes:
            //   prefix → child wire keys are "{slot}.{prefix}{i}" (BatchImagesNode).
            //   names  → child wire keys are "{slot}.{names[i]}" (HiDreamO1ReferenceImages).
            if (comfyType == "COMFY_AUTOGROW_V3"
                && TryParseAutogrow(spec, typeMapping, generatedMarkers, markerNamespace,
                    out string? listPrefix, out IReadOnlyList<string>? listNames,
                    out int listMin, out int listMax, out MarkerInfo? elementMarker))
            {
                string listPropName = SanitizeInputPropertyName(inputProp.Name, inputs, outputs);
                inputs.Add(new InputDef(
                    inputProp.Name, listPropName, comfyType, elementMarker!,
                    required, IsPrimitive: false, CSharpType: null, DefaultValue: null,
                    IsList: true, Prefix: listPrefix, Names: listNames,
                    Min: listMin, Max: listMax, ElementMarker: elementMarker));
                continue;
            }

            object? defaultValue = null;
            if (spec.Count >= 2 && spec[1] is JObject options && options["default"] is JToken defToken)
            {
                defaultValue = defToken.Type switch
                {
                    JTokenType.Integer => (long)defToken,
                    JTokenType.Float => (double)defToken,
                    JTokenType.String => (string?)defToken,
                    JTokenType.Boolean => (bool)defToken,
                    _ => null
                };
            }

            // COMBO, multi-types ("CLIP,GEMMA"), and DYNAMICCOMBO_V3 (variant-shaped) collapse to STRING.
            // AUTOGROW_V3 that failed parse above also lands here as a safety fallback.
            string effectiveType = comfyType;
            if (comfyType == "COMBO"
                || comfyType.Contains(',')
                || comfyType.StartsWith("COMFY_AUTOGROW_V3", StringComparison.Ordinal)
                || comfyType.StartsWith("COMFY_DYNAMICCOMBO_V3", StringComparison.Ordinal))
            {
                effectiveType = "STRING";
            }

            MarkerInfo marker = ResolveMarkerType(effectiveType, typeMapping, generatedMarkers, markerNamespace);
            bool isPrimitive = PrimitiveTypes.TryGetValue(effectiveType, out (string Marker, string CSharp) prim);
            string? csharpType = isPrimitive ? prim.CSharp : null;
            string propName = SanitizeInputPropertyName(inputProp.Name, inputs, outputs);

            inputs.Add(new InputDef(
                inputProp.Name, propName, comfyType, marker, required, isPrimitive, csharpType, defaultValue,
                ComboValues: comboValues));
        }
    }

    /// <summary>Parse a COMFY_AUTOGROW_V3 input spec. Two shapes are supported:
    /// <list type="bullet">
    ///   <item><b>Prefix shape</b> (BatchImagesNode): template has <c>prefix</c>,
    ///         <c>min</c>, <c>max</c> — children get generated names
    ///         <c>{prefix}0</c>, <c>{prefix}1</c>, …</item>
    ///   <item><b>Names shape</b> (HiDreamO1ReferenceImages): template has explicit
    ///         <c>names</c> array — children use those names verbatim.</item>
    /// </list>
    /// On success, exactly one of <paramref name="prefix"/> / <paramref name="names"/> is set.
    /// </summary>
    private static bool TryParseAutogrow(
        JArray spec,
        Dictionary<string, MarkerInfo> typeMapping,
        Dictionary<string, MarkerInfo> generatedMarkers,
        string markerNamespace,
        out string? prefix,
        out IReadOnlyList<string>? names,
        out int min,
        out int max,
        out MarkerInfo? elementMarker)
    {
        prefix = null;
        names = null;
        min = 0;
        max = 0;
        elementMarker = null;
        if (spec.Count < 2 || spec[1] is not JObject opts)
        {
            return false;
        }
        if (opts["template"] is not JObject template)
        {
            return false;
        }
        if (template["input"] is not JObject inputSection
            || inputSection["required"] is not JObject requiredSection
            || requiredSection.Count != 1)
        {
            // Autogrow template should declare exactly one element input. Anything else
            // is malformed schema we don't know how to model — fall through to STRING.
            return false;
        }
        JProperty first = requiredSection.Properties().First();
        if (first.Value is not JArray elemSpec || elemSpec.Count == 0)
        {
            return false;
        }
        string elemComfyType = elemSpec[0] is JArray ? "COMBO" : elemSpec[0]?.ToString() ?? "*";
        elementMarker = ResolveMarkerType(elemComfyType, typeMapping, generatedMarkers, markerNamespace);
        min = Math.Max(0, template.Value<int?>("min") ?? 0);

        string? rawPrefix = template.Value<string>("prefix");
        if (!string.IsNullOrEmpty(rawPrefix))
        {
            int rawMax = template.Value<int?>("max") ?? int.MaxValue;
            if (rawMax < min)
            {
                return false;
            }
            prefix = rawPrefix;
            max = rawMax;

            return true;
        }

        if (template["names"] is JArray namesArr && namesArr.Count > 0)
        {
            List<string> parsed = [];
            foreach (JToken t in namesArr)
            {
                // Reject the whole spec on any non-string entry — silently dropping bad
                // entries would misalign the schema's position-to-name mapping.
                if (t is not JValue jv || jv.Type != JTokenType.String || jv.Value is not string s)
                {
                    return false;
                }
                parsed.Add(s);
            }
            int rawMax = template.Value<int?>("max") ?? parsed.Count;
            if (rawMax < min)
            {
                return false;
            }
            names = parsed;
            max = rawMax;

            return true;
        }

        return false;
    }

    // ── Family interfaces ───────────────────────────────────────────

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> EmptyFamilyMap =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    /// <summary>
    /// Load the <c>--families</c> map. The JSON is keyed by the <em>fully-qualified interface
    /// name</em>, valued by the list of ComfyUI <c>class_type</c>s that should implement it:
    /// <code>
    /// {
    ///   "ComfyTyped.Families.IVaeDecode": ["VAEDecode", "VAEDecodeTiled"],
    ///   "ComfyTyped.Families.IVaeEncode": ["VAEEncode", "VAEEncodeTiled"]
    /// }
    /// </code>
    /// Returns the inverted lookup <c>class_type → [interface FQNs]</c> (interfaces sorted, so
    /// emission is deterministic). The interface bodies themselves are hand-written in the
    /// library; codegen only appends the names to each node's base list. A node listed here that
    /// does not actually expose the interface's slot properties will fail to compile after regen.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> LoadFamilies(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"--families file not found: {path}");
        }

        JObject obj = JObject.Parse(File.ReadAllText(path));
        SortedDictionary<string, SortedSet<string>> byClassType = new(StringComparer.Ordinal);
        foreach (JProperty prop in obj.Properties())
        {
            // Underscore-prefixed keys are comments (e.g. "_comment"), not interfaces.
            if (prop.Name.StartsWith('_'))
            {
                continue;
            }
            if (prop.Value is not JArray classTypes)
            {
                throw new InvalidOperationException(
                    $"--families at '{path}': value for interface '{prop.Name}' must be a JSON "
                    + "array of class_type strings.");
            }
            foreach (JToken token in classTypes)
            {
                string? classType = token.Value<string>();
                if (string.IsNullOrWhiteSpace(classType))
                {
                    continue;
                }
                if (!byClassType.TryGetValue(classType, out SortedSet<string>? interfaces))
                {
                    interfaces = new SortedSet<string>(StringComparer.Ordinal);
                    byClassType[classType] = interfaces;
                }
                interfaces.Add(prop.Name);
            }
        }

        Dictionary<string, IReadOnlyList<string>> map = new(StringComparer.Ordinal);
        foreach ((string classType, SortedSet<string> interfaces) in byClassType)
        {
            map[classType] = interfaces.ToList();
        }

        return map;
    }

    // Code generation

    private static string GenerateNodeClass(
        NodeDef node, string ns, IReadOnlyDictionary<string, IReadOnlyList<string>> familyMap)
    {
        StringBuilder sb = new();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using ComfyTyped.Core;");

        // Always emit ComfyTyped.Types (primitives + AnyType live there). Add any other
        // marker namespaces this node references. Skip the node's own namespace.
        SortedSet<string> markerNamespaces = new(StringComparer.Ordinal) { CoreMarkerNamespace };
        foreach (OutputDef o in node.Outputs)
        {
            markerNamespaces.Add(o.Marker.Namespace);
        }
        foreach (InputDef inp in node.Inputs)
        {
            markerNamespaces.Add(inp.Marker.Namespace);
            if (inp.ElementMarker is not null)
            {
                markerNamespaces.Add(inp.ElementMarker.Namespace);
            }
        }
        markerNamespaces.Remove(ns);
        foreach (string nsRef in markerNamespaces)
        {
            sb.AppendLine($"using {nsRef};");
        }

        sb.AppendLine();
        sb.AppendLine($"namespace {ns};");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(node.Description))
        {
            string[] lines = EscapeXml(node.Description).Split('\n');
            if (lines.Length == 1)
            {
                sb.AppendLine($"/// <summary>{lines[0].Trim()}</summary>");
            }
            else
            {
                sb.AppendLine("/// <summary>");
                foreach (string line in lines)
                {
                    string trimmed = line.Trim();
                    sb.AppendLine($"/// {(trimmed.Length > 0 ? trimmed : "<br/>")}");
                }
                sb.AppendLine("/// </summary>");
            }
        }
        if (node.Category is not null)
        {
            sb.AppendLine($"/// <remarks>Category: {EscapeXml(node.Category)}</remarks>");
        }

        List<string> baseTypes = ["ComfyNode"];
        if (familyMap.TryGetValue(node.ClassType, out IReadOnlyList<string>? familyInterfaces))
        {
            // Family interfaces (e.g. ComfyTyped.Families.IVaeDecode) let heterogeneous nodes
            // that share a slot shape be queried/handled through one typed surface. The interface
            // bodies are hand-written; the node must already expose matching slot properties, so
            // a mismatch surfaces as a compile error here after regen — by design.
            baseTypes.AddRange(familyInterfaces);
        }
        sb.AppendLine($"public sealed class {node.ClassName} : {string.Join(", ", baseTypes)}");
        sb.AppendLine("{");
        sb.AppendLine($"    /// <summary>ComfyUI <c>class_type</c> for this node — use for static refs (switch cases, <c>g.CreateNode(...)</c>).</summary>");
        sb.AppendLine($"    public const string ClassType = \"{node.ClassType}\";");
        sb.AppendLine($"    public override string ClassTypeName => ClassType;");
        sb.AppendLine();

        if (node.Outputs.Count > 0)
        {
            sb.AppendLine("    // ── Outputs ──");
            foreach (OutputDef o in node.Outputs)
            {
                sb.AppendLine($"    public NodeOutput<{o.Marker.ShortName}> {o.PropertyName} {{ get; }}");
            }
            sb.AppendLine();
        }

        List<InputDef> singularInputs = node.Inputs.Where(i => !i.IsList).ToList();
        List<InputDef> listInputs = node.Inputs.Where(i => i.IsList).ToList();

        if (singularInputs.Count > 0)
        {
            sb.AppendLine("    // ── Inputs ──");
            foreach (InputDef inp in singularInputs)
            {
                string tail = inp.Required ? "" : " // optional";
                sb.AppendLine(
                    $"    public NodeInput<{inp.Marker.ShortName}> {inp.PropertyName} {{ get; }}{tail}");
            }
            sb.AppendLine();
        }

        if (listInputs.Count > 0)
        {
            sb.AppendLine("    // ── Input lists (COMFY_AUTOGROW_V3) ──");
            foreach (InputDef inp in listInputs)
            {
                string tail = inp.Required ? "" : " // optional";
                sb.AppendLine(
                    $"    public NodeInputList<{inp.ElementMarker!.ShortName}> {inp.PropertyName} {{ get; }}{tail}");
            }
            sb.AppendLine();
        }

        sb.AppendLine($"    public {node.ClassName}()");
        sb.AppendLine("    {");
        foreach (OutputDef o in node.Outputs)
        {
            sb.AppendLine(
                $"        {o.PropertyName} = AddOutput<{o.Marker.ShortName}>({o.SlotIndex}, \"{o.Name}\");");
        }
        foreach (InputDef inp in singularInputs)
        {
            sb.AppendLine(
                $"        {inp.PropertyName} = AddInput<{inp.Marker.ShortName}>(\"{inp.Name}\");");
            if (inp.IsPrimitive && inp.DefaultValue is not null)
            {
                sb.AppendLine($"        {inp.PropertyName}.Set({FormatLiteral(inp.DefaultValue, inp.CSharpType!)});");
            }
        }
        foreach (InputDef inp in listInputs)
        {
            string maxLit = inp.Max == int.MaxValue ? "int.MaxValue" : inp.Max.ToString();
            if (inp.Names is not null)
            {
                string namesLit = string.Join(
                    ", ",
                    inp.Names.Select(n => $"\"{EscapeCSharpString(n)}\""));
                sb.AppendLine(
                    $"        {inp.PropertyName} = AddInputList<{inp.ElementMarker!.ShortName}>("
                    + $"\"{inp.Name}\", names: new string[] {{ {namesLit} }}, max: {maxLit});");
            }
            else
            {
                sb.AppendLine(
                    $"        {inp.PropertyName} = AddInputList<{inp.ElementMarker!.ShortName}>("
                    + $"\"{inp.Name}\", prefix: \"{EscapeCSharpString(inp.Prefix!)}\", max: {maxLit});");
            }
        }
        sb.AppendLine("    }");

        // With(...) covers every singular input. Primitives bind from a literal OR a same-typed
        // connection (IntArg/FloatArg/StringArg/BoolArg); connection inputs bind from a same-typed
        // NodeOutput (In<T>). Type mismatches are a compile error — there is no conversion from a
        // wrong-typed output to the binding. Lists stay out of With (use Add/AddRange).
        if (singularInputs.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("    /// <summary>Fluent setter for inputs. Returns <c>this</c> for chaining.");
            sb.AppendLine("    /// Pass only the inputs you want to set; omitted (<c>null</c>) args leave the existing value untouched.");
            sb.AppendLine("    /// Primitive inputs accept a literal or a same-typed output; connection inputs accept a same-typed");
            sb.AppendLine("    /// output (mismatches are a compile error). Input lists are not exposed here — use <c>Add</c>/<c>AddRange</c>.</summary>");
            sb.AppendLine($"    public {node.ClassName} With(");
            for (int i = 0; i < singularInputs.Count; i++)
            {
                InputDef inp = singularInputs[i];
                string sep = i == singularInputs.Count - 1 ? "" : ",";
                sb.AppendLine($"        {WithParamType(inp)}? {inp.PropertyName} = null{sep}");
            }
            sb.AppendLine("    )");
            sb.AppendLine("    {");
            foreach (InputDef inp in singularInputs)
            {
                sb.AppendLine($"        {inp.PropertyName}?.ApplyTo(this.{inp.PropertyName});");
            }
            sb.AppendLine("        return this;");
            sb.AppendLine("    }");
        }

        EmitComboConstants(sb, singularInputs);

        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string GenerateRegistrationFile(string ns, string className)
    {
        StringBuilder sb = new();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System.Reflection;");
        sb.AppendLine("using ComfyTyped.Core;");
        sb.AppendLine();
        sb.AppendLine($"namespace {ns};");
        sb.AppendLine();
        sb.AppendLine($"public static class {className}");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>Discover and register every generated node type in this assembly.");
        sb.AppendLine("    /// Idempotent — safe to call multiple times.</summary>");
        sb.AppendLine("    public static void EnsureRegistered() =>");
        sb.AppendLine("        NodeRegistry.RegisterAssembly(Assembly.GetExecutingAssembly());");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string GenerateMarkerClass(string comfyType, MarkerInfo info)
    {
        StringBuilder sb = new();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        if (info.Namespace != CoreMarkerNamespace)
        {
            sb.AppendLine("using ComfyTyped.Types;");
        }
        sb.AppendLine();
        sb.AppendLine($"namespace {info.Namespace};");
        sb.AppendLine();
        sb.AppendLine($"/// <summary>Marker for ComfyUI type \"{EscapeXml(comfyType)}\".</summary>");
        sb.AppendLine(
            $"public sealed class {info.ShortName} : IComfyType "
            + $"{{ public static string TypeName => \"{EscapeCSharpString(comfyType)}\"; }}");

        return sb.ToString();
    }

    // Naming

    internal static string SanitizeClassName(string classType)
    {
        string sanitized = InvalidCharsRegex().Replace(classType, "_");
        if (sanitized.Length > 0 && char.IsDigit(sanitized[0]))
        {
            sanitized = "_" + sanitized;
        }

        return ToPascalCase(sanitized) + "Node";
    }

    private static string SanitizeInputPropertyName(
        string inputName, List<InputDef> existingInputs, List<OutputDef> existingOutputs)
    {
        string name = ToPascalCase(inputName);
        if (string.IsNullOrEmpty(name))
        {
            name = "Input";
        }
        name = EnsureValidIdentifier(name);
        // Reserve every public member on the ComfyNode base class — properties AND
        // methods. Generated property names that collide trigger CS0108 ("hides
        // inherited member"). The codebase treats warnings as load-bearing, so any
        // collision must produce a distinct property name.
        if (name is "ClassType" or "ClassTypeName"
            or "Inputs" or "InputLists" or "Outputs"
            or "ExtraInputs" or "Id"
            or "FindInput" or "FindInputList" or "FindOutput"
            or "ToWorkflowNode")
        {
            name += "Input";
        }

        string baseName = name;
        int suffix = 2;
        while (existingInputs.Any(i => i.PropertyName == name)
            || existingOutputs.Any(o => o.PropertyName == name))
        {
            name = baseName + "Input" + (suffix > 2 ? suffix.ToString() : "");
            suffix++;
        }

        return name;
    }

    private static string SanitizeOutputPropertyName(string slotName, int index, List<OutputDef> existing)
    {
        string name = ToPascalCase(slotName);
        if (string.IsNullOrEmpty(name))
        {
            name = $"Output{index}";
        }
        name = EnsureValidIdentifier(name);

        string baseName = name;
        int suffix = 2;
        while (existing.Any(o => o.PropertyName == name))
        {
            name = baseName + suffix++;
        }

        return name;
    }

    private static string ToPascalCase(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        StringBuilder sb = new();
        bool nextUpper = true;
        foreach (char c in input)
        {
            if (!char.IsLetterOrDigit(c))
            {
                nextUpper = true;
                continue;
            }
            sb.Append(nextUpper ? char.ToUpper(c) : c);
            nextUpper = false;
        }

        return sb.ToString();
    }

    private static string EnsureValidIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "_";
        }
        if (char.IsDigit(name[0]))
        {
            name = "_" + name;
        }

        return name switch
        {
            "abstract" or "as" or "base" or "bool" or "break" or "byte" or "case" or "catch" or
            "char" or "checked" or "class" or "const" or "continue" or "decimal" or "default" or
            "delegate" or "do" or "double" or "else" or "enum" or "event" or "explicit" or "extern" or
            "false" or "finally" or "fixed" or "float" or "for" or "foreach" or "goto" or "if" or
            "implicit" or "in" or "int" or "interface" or "internal" or "is" or "lock" or "long" or
            "namespace" or "new" or "null" or "object" or "operator" or "out" or "override" or
            "params" or "private" or "protected" or "public" or "readonly" or "ref" or "return" or
            "sbyte" or "sealed" or "short" or "sizeof" or "stackalloc" or "static" or "string" or
            "struct" or "switch" or "this" or "throw" or "true" or "try" or "typeof" or "uint" or
            "ulong" or "unchecked" or "unsafe" or "ushort" or "using" or "virtual" or "void" or
            "volatile" or "while" => "@" + name,
            _ => name
        };
    }

    /// <summary>Max number of allowed-value constants to emit for one COMBO. Above this a combo is
    /// treated as a large/dynamic picker and left value-only.</summary>
    private const int MaxComboConstants = 64;

    private static readonly string[] ModelFileExtensions =
        [".safetensors", ".ckpt", ".pt", ".pth", ".bin", ".gguf", ".onnx", ".sft", ".engine", ".vae", ".yaml", ".json"];

    /// <summary>Emit nested <c>{Input}Values</c> classes of <c>public const string</c> for small,
    /// static COMBO inputs — discoverable suggestions while the input stays a plain string so any
    /// value still compiles (the C# analogue of TS <c>"a" | "b" | (string &amp; {})</c>).</summary>
    private static void EmitComboConstants(StringBuilder sb, List<InputDef> singularInputs)
    {
        foreach (InputDef inp in singularInputs)
        {
            IReadOnlyList<(string Name, string Value)> consts = ComboConstants(inp);
            if (consts.Count == 0)
            {
                continue;
            }
            sb.AppendLine();
            sb.AppendLine(
                $"    /// <summary>Known ComfyUI values for <c>{EscapeXml(inp.Name)}</c> "
                + "(suggestions — any string is also accepted).</summary>");
            sb.AppendLine($"    public static class {inp.PropertyName}Values");
            sb.AppendLine("    {");
            foreach ((string name, string value) in consts)
            {
                sb.AppendLine($"        public const string {name} = \"{EscapeCSharpString(value)}\";");
            }
            sb.AppendLine("    }");
        }
    }

    /// <summary>Resolve a COMBO input's allowed values to identifier→literal pairs (deduped), or an
    /// empty list when the combo should stay value-only: too large, or a per-environment model/file
    /// picker (path-like values) that would otherwise bake one machine's installed files into
    /// committed generated code.</summary>
    private static IReadOnlyList<(string Name, string Value)> ComboConstants(InputDef inp)
    {
        if (inp.ComboValues is not { Count: > 0 } values || values.Count > MaxComboConstants)
        {
            return [];
        }
        foreach (string v in values)
        {
            if (string.IsNullOrWhiteSpace(v) || v.Contains('/') || v.Contains('\\') || LooksLikeModelFile(v))
            {
                return [];
            }
        }
        List<(string Name, string Value)> result = [];
        HashSet<string> used = [];
        foreach (string v in values)
        {
            string name = SanitizeConstName(v);
            string baseName = name;
            int suffix = 2;
            while (!used.Add(name))
            {
                name = baseName + "_" + suffix++;
            }
            result.Add((name, v));
        }
        return result;
    }

    private static bool LooksLikeModelFile(string value)
    {
        foreach (string ext in ModelFileExtensions)
        {
            if (value.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string SanitizeConstName(string value)
    {
        string name = ToPascalCase(value);
        return EnsureValidIdentifier(string.IsNullOrEmpty(name) ? "Value" : name);
    }

    private static MarkerInfo ResolveMarkerType(
        string comfyType,
        Dictionary<string, MarkerInfo> typeMapping,
        Dictionary<string, MarkerInfo> generatedMarkers,
        string markerNamespace)
    {
        if (typeMapping.TryGetValue(comfyType, out MarkerInfo? marker))
        {
            return marker;
        }

        // Things that don't represent a single nameable type → AnyType.
        if (string.IsNullOrEmpty(comfyType)
            || comfyType == "*"
            || comfyType == "COMBO"
            || comfyType.Contains(','))
        {
            return new MarkerInfo("AnyType", CoreMarkerNamespace);
        }

        // Mechanical: split on non-alphanumeric, capitalize each chunk's first letter,
        // lowercase the rest, append "Type". SEEDVR2_DIT → Seedvr2DitType.
        MarkerInfo info = new(MarkerClassName(comfyType), markerNamespace);
        typeMapping[comfyType] = info;
        generatedMarkers[comfyType] = info;

        return info;
    }

    private static string MarkerClassName(string comfyType)
    {
        StringBuilder sb = new();
        bool nextUpper = true;
        foreach (char c in comfyType)
        {
            if (!char.IsLetterOrDigit(c))
            {
                nextUpper = true;
                continue;
            }
            sb.Append(nextUpper ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
            nextUpper = false;
        }
        if (sb.Length == 0)
        {
            sb.Append('_');
        }
        if (char.IsDigit(sb[0]))
        {
            sb.Insert(0, '_');
        }
        sb.Append("Type");

        return sb.ToString();
    }

    /// <summary>The With(...) parameter type (before the trailing <c>?</c>) for a singular input:
    /// a primitive binding struct for INT/FLOAT/STRING/BOOLEAN, or <c>In&lt;Marker&gt;</c> for a connection.</summary>
    private static string WithParamType(InputDef inp) =>
        inp.IsPrimitive
            ? inp.CSharpType switch
            {
                "long" => "IntArg",
                "double" => "FloatArg",
                "string" => "StringArg",
                "bool" => "BoolArg",
                _ => throw new InvalidOperationException(
                    $"Unexpected primitive C# type '{inp.CSharpType}' for input '{inp.Name}'."),
            }
            : $"In<{inp.Marker.ShortName}>";

    private static string FormatLiteral(object value, string csharpType) => csharpType switch
    {
        "long" => value switch
        {
            long l => $"{l}L",
            double d => $"{(long)d}L",
            _ => $"{value}L"
        },
        "double" => value switch
        {
            double d => FormatDouble(d),
            long l => $"{l}.0",
            _ => $"{value}"
        },
        "string" => $"\"{EscapeCSharpString(value.ToString() ?? "")}\"",
        "bool" => value switch
        {
            bool b => b ? "true" : "false",
            _ => value.ToString()?.ToLower() ?? "false"
        },
        _ => value.ToString() ?? "null"
    };

    private static string FormatDouble(double d)
    {
        string s = d.ToString("G", CultureInfo.InvariantCulture);
        bool needsDecimal = d == Math.Floor(d) && !s.Contains('E');

        return needsDecimal ? s + ".0" : s;
    }

    private static string EscapeCSharpString(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");

    private static string EscapeXml(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    [GeneratedRegex(@"[^a-zA-Z0-9_]")]
    private static partial Regex InvalidCharsRegex();

    [GeneratedRegex(@"public\s+sealed\s+class\s+([A-Za-z_][A-Za-z0-9_]*)\s*:\s*(ComfyNode|IComfyType)")]
    private static partial Regex GeneratedDeclRegex();

    [GeneratedRegex(@"public\s+const\s+string\s+ClassType\s*=\s*""([^""]+)""")]
    private static partial Regex GeneratedClassTypeRegex();

    // Prune
    //
    // Use case: an extension developer dumps object_info.json from their local ComfyUI
    // (which has unrelated custom-node packs installed), generates with --core-assembly
    // to filter out core, and ends up with .g.cs files for every non-core class_type —
    // including packs they don't actually use. `prune` deletes any *.g.cs whose class
    // name is never referenced as an identifier in the extension's own source files.
    // The reflection-based NodeRegistrations.EnsureRegistered() automatically reflects
    // the surviving set after a recompile, so no registration list needs editing.

    private sealed record PruneOptions(string GeneratedDir, List<string> SourceDirs, bool DryRun);

    private sealed record PruneCandidate(string ClassName, string ClassType, bool IsNode, string FilePath, string Text);

    internal static int RunPrune(string[] args)
    {
        PruneOptions? opts = ParsePruneArgs(args);
        if (opts is null)
        {
            return 1;
        }
        if (!Directory.Exists(opts.GeneratedDir))
        {
            Console.Error.WriteLine($"prune: --generated-dir does not exist: {opts.GeneratedDir}");
            return 1;
        }

        HashSet<string> manifestAlwaysKeep = ReadPruneManifestKeepSet(opts.GeneratedDir);

        // Both ComfyNode subclasses and IComfyType marker classes are eligible for pruning.
        List<PruneCandidate> candidates = [];
        foreach (string file in Directory.EnumerateFiles(opts.GeneratedDir, "*.g.cs", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(file);
            if (name == "NodeRegistrations.g.cs" || name == PruneManifestFileName)
            {
                continue;
            }
            string text = File.ReadAllText(file);
            Match m = GeneratedDeclRegex().Match(text);
            if (!m.Success)
            {
                Console.Error.WriteLine($"prune: skipping {name} (no `public sealed class X : ComfyNode|IComfyType` declaration)");
                continue;
            }
            string className = m.Groups[1].Value;
            bool isNode = m.Groups[2].Value == "ComfyNode";
            string classType = "";
            if (isNode)
            {
                Match ct = GeneratedClassTypeRegex().Match(text);
                classType = ct.Success ? ct.Groups[1].Value : "";
            }
            candidates.Add(new PruneCandidate(className, classType, isNode, file, text));
        }

        if (candidates.Count == 0)
        {
            Console.WriteLine("prune: no candidate generated files found.");
            return 0;
        }

        // Skip files under --generated-dir when scanning consumer sources: each generated
        // *.g.cs declares its own class, so including those files would self-reference
        // every candidate and prevent any pruning.
        string generatedDirPrefix = Path.GetFullPath(opts.GeneratedDir).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        StringBuilder allSource = new();
        int sourceFileCount = 0;
        foreach (string srcDir in opts.SourceDirs)
        {
            if (!Directory.Exists(srcDir))
            {
                Console.Error.WriteLine($"prune: --source does not exist: {srcDir}");
                return 1;
            }
            foreach (string file in Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories))
            {
                if (Path.GetFullPath(file).StartsWith(generatedDirPrefix, StringComparison.Ordinal))
                {
                    continue;
                }
                allSource.AppendLine(File.ReadAllText(file));
                sourceFileCount++;
            }
        }
        string consumerSource = allSource.ToString();

        // Pass 1: a Node is kept if its class name OR its class_type string appears
        // word-bounded in consumer source, OR it's listed in PruneManifest.g.cs.
        // Consumers usually reference nodes by class_type ("g.CreateNode(\"FooBar\",
        // ...)") rather than by typed class name; the manifest covers the third case
        // where the consumer ships typed bindings on purpose with no source usage yet.
        HashSet<string> keptNames = new(StringComparer.Ordinal);
        StringBuilder extendedSourceBuilder = new(consumerSource);
        int keptFromManifest = 0;
        foreach (PruneCandidate c in candidates.Where(c => c.IsNode))
        {
            bool inManifest = manifestAlwaysKeep.Contains(c.ClassName);
            bool classNameUsed = inManifest
                || Regex.IsMatch(consumerSource, $@"\b{Regex.Escape(c.ClassName)}\b");
            bool classTypeUsed = !string.IsNullOrEmpty(c.ClassType)
                && Regex.IsMatch(consumerSource, $@"\b{Regex.Escape(c.ClassType)}\b");
            if (classNameUsed || classTypeUsed)
            {
                keptNames.Add(c.ClassName);
                extendedSourceBuilder.AppendLine(c.Text);
                if (inManifest)
                {
                    keptFromManifest++;
                }
            }
        }

        // Pass 2: an IComfyType marker is kept if its class name appears word-bounded
        // in (consumer source) ∪ (content of kept Node files). Markers only carry
        // weight as type parameters on `NodeInput<T>` / `NodeOutput<T>`, so once every
        // referencing Node is pruned the marker becomes dead code.
        string extendedSource = extendedSourceBuilder.ToString();
        foreach (PruneCandidate c in candidates.Where(c => !c.IsNode))
        {
            if (Regex.IsMatch(extendedSource, $@"\b{Regex.Escape(c.ClassName)}\b"))
            {
                keptNames.Add(c.ClassName);
            }
        }

        List<PruneCandidate> toPrune = [.. candidates
            .Where(c => !keptNames.Contains(c.ClassName))
            .OrderBy(c => c.ClassName, StringComparer.Ordinal)];

        foreach (PruneCandidate c in toPrune)
        {
            string rel = Path.GetRelativePath(Environment.CurrentDirectory, c.FilePath);
            if (opts.DryRun)
            {
                Console.WriteLine($"would prune: {rel}");
            }
            else
            {
                File.Delete(c.FilePath);
                Console.WriteLine($"pruned: {rel}");
            }
        }

        int kept = candidates.Count - toPrune.Count;
        string verb = opts.DryRun ? "would prune" : "pruned";
        string manifestNote = manifestAlwaysKeep.Count > 0
            ? $" ({keptFromManifest} retained via PruneManifest.g.cs)"
            : "";
        Console.WriteLine(
            $"prune: scanned {sourceFileCount} source files; "
            + $"kept {kept}/{candidates.Count} generated classes{manifestNote}; "
            + $"{verb} {toPrune.Count}.");

        return 0;
    }

    private static PruneOptions? ParsePruneArgs(string[] args)
    {
        string? generatedDir = null;
        List<string> sourceDirs = [];
        bool dryRun = false;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "--generated-dir": generatedDir = NextArg(args, ref i, a); break;
                case "--source": sourceDirs.Add(NextArg(args, ref i, a)); break;
                case "--dry-run": dryRun = true; break;
                case "--help" or "-h": PrintPruneUsage(); return null;
                default:
                    Console.Error.WriteLine($"prune: unknown flag: {a}");
                    PrintPruneUsage();
                    return null;
            }
        }

        if (generatedDir is null || sourceDirs.Count == 0)
        {
            PrintPruneUsage();
            return null;
        }

        return new PruneOptions(generatedDir, sourceDirs, dryRun);
    }

    private static void PrintPruneUsage()
    {
        Console.Error.WriteLine("""
            Usage: ComfyTyped.CodeGen prune --generated-dir <dir> --source <dir> [--source <dir>...] [--dry-run]

            Deletes unused *.g.cs files in --generated-dir. A node file is kept if its
            C# class name OR its ComfyUI class_type string appears word-bounded in any
            *.cs file under the --source directories, OR its class name is listed in
            PruneManifest.g.cs (emitted by codegen when --keep-list was passed). An
            IComfyType marker file is kept if its class name appears either in --source
            or in the content of a kept node file. Files under --generated-dir are
            excluded from the source scan so generated self-references don't defeat
            the prune. NodeRegistrations.g.cs and PruneManifest.g.cs are always
            preserved.

            Run before shipping an extension to drop generated classes for unrelated
            custom-node packs that were present in the developer's object_info.json
            but aren't actually used by the extension's code.

            Options:
              --generated-dir <dir>   Directory of *.g.cs files to consider for pruning.
              --source <dir>          Source directory to scan for usages (recursive).
                                      Repeatable.
              --dry-run               List what would be pruned, but don't delete.
              -h, --help              Show this message.
            """);
    }
}
