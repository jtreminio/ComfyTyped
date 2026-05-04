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
        bool NativeOnly);

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
        if (opts.CoreAssemblyPath is not null)
        {
            int markersBefore = typeMapping.Count;
            LoadCoreSkipSets(opts.CoreAssemblyPath, classTypeSkipSet, typeMapping);
            int extraMarkers = typeMapping.Count - markersBefore;
            Console.WriteLine(
                $"Diff mode: skipping {classTypeSkipSet.Count} class_types, "
                + $"reusing {extraMarkers} marker types from core.");
        }

        Dictionary<string, MarkerInfo> generatedMarkers = new(StringComparer.OrdinalIgnoreCase);
        JObject objectInfo = LoadComfyJson(opts.ComfyJsonSource);
        Directory.CreateDirectory(opts.OutputDir);
        ClearGeneratedFiles(opts.OutputDir);

        int generated = 0;
        int skippedDiff = 0;
        int skippedNonNative = 0;
        int skippedParse = 0;

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
            if (opts.NativeOnly && !IsNativeModule(nodeInfo.Value<string>("python_module")))
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

            File.WriteAllText(
                Path.Combine(opts.OutputDir, $"{nodeDef.ClassName}.g.cs"),
                GenerateNodeClass(nodeDef, opts.Namespace));
            generated++;
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

        Console.WriteLine(
            $"Generated {generated} nodes and {generatedMarkers.Count} markers; "
            + $"skipped {skippedDiff} (in core), {skippedNonNative} (non-native), {skippedParse} (parse).");

        return 0;
    }

    private static bool IsNativeModule(string? pythonModule) =>
        !string.IsNullOrEmpty(pythonModule)
        && (pythonModule == "nodes"
            || pythonModule.StartsWith("comfy_extras.", StringComparison.Ordinal)
            || SwarmNativeModules.Contains(pythonModule));

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
            comfyJsonSource, outputDir, ns, markerNs ?? ns, registrationsClass, coreAssembly, nativeOnly);
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
                                               built-in mapping or core's IOTypeMap are emitted
                                               automatically.
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

    // Diff mode

    private static void LoadCoreSkipSets(
        string coreAssemblyPath,
        HashSet<string> classTypes,
        Dictionary<string, MarkerInfo> typeMapping)
    {
        Assembly asm = Assembly.LoadFrom(Path.GetFullPath(coreAssemblyPath));

        Type GetTypeOrThrow(string fullName) => asm.GetType(fullName)
            ?? throw new InvalidOperationException($"{fullName} not found in {coreAssemblyPath}.");

        Type registrations = GetTypeOrThrow(CoreNodeRegistrationsTypeName);
        Type registry = GetTypeOrThrow(CoreNodeRegistryTypeName);
        Type iComfyType = GetTypeOrThrow(CoreIComfyTypeName);

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

        foreach (Type t in asm.GetTypes())
        {
            if (!t.IsClass || t.IsAbstract || !iComfyType.IsAssignableFrom(t))
            {
                continue;
            }
            PropertyInfo? prop = t.GetProperty("TypeName", BindingFlags.Public | BindingFlags.Static);
            if (prop?.GetValue(null) is string typeName && t.Namespace is { Length: > 0 } markerNs)
            {
                typeMapping[typeName] = new MarkerInfo(t.Name, markerNs);
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
        object? DefaultValue);

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

            // COMBO, multi-types ("CLIP,GEMMA"), and dynamic V3 widget types collapse to STRING.
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
                inputProp.Name, propName, comfyType, marker, required, isPrimitive, csharpType, defaultValue));
        }
    }

    // Code generation

    private static string GenerateNodeClass(NodeDef node, string ns)
    {
        StringBuilder sb = new();
        sb.AppendLine("// <auto-generated/>");
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

        sb.AppendLine($"public sealed class {node.ClassName} : ComfyNode");
        sb.AppendLine("{");
        sb.AppendLine($"    public override string ClassType => \"{node.ClassType}\";");
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

        if (node.Inputs.Count > 0)
        {
            sb.AppendLine("    // ── Inputs ──");
            foreach (InputDef inp in node.Inputs)
            {
                string tail = inp.Required ? "" : " // optional";
                sb.AppendLine(
                    $"    public NodeInput<{inp.Marker.ShortName}> {inp.PropertyName} {{ get; }}{tail}");
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
        foreach (InputDef inp in node.Inputs)
        {
            string req = inp.Required ? "true" : "false";
            sb.AppendLine(
                $"        {inp.PropertyName} = AddInput<{inp.Marker.ShortName}>(\"{inp.Name}\", required: {req});");
            if (inp.IsPrimitive && inp.DefaultValue is not null)
            {
                sb.AppendLine($"        {inp.PropertyName}.Set({FormatLiteral(inp.DefaultValue, inp.CSharpType!)});");
            }
        }
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string GenerateRegistrationFile(string ns, string className)
    {
        StringBuilder sb = new();
        sb.AppendLine("// <auto-generated/>");
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

    private static string SanitizeClassName(string classType)
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
        if (name == "ClassType")
        {
            name = "ClassTypeInput";
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

    [GeneratedRegex(@"public\s+override\s+string\s+ClassType\s*=>\s*""([^""]+)""")]
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

    private static int RunPrune(string[] args)
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

        // Enumerate every generated *.g.cs as a candidate. Both ComfyNode subclasses
        // and IComfyType marker classes are eligible for pruning.
        List<PruneCandidate> candidates = [];
        foreach (string file in Directory.EnumerateFiles(opts.GeneratedDir, "*.g.cs", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(file);
            if (name == "NodeRegistrations.g.cs")
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
        // word-bounded in consumer source. Consumers usually reference nodes by
        // class_type ("g.CreateNode(\"FooBar\", ...)") rather than by typed class name.
        HashSet<string> keptNames = new(StringComparer.Ordinal);
        StringBuilder extendedSourceBuilder = new(consumerSource);
        foreach (PruneCandidate c in candidates.Where(c => c.IsNode))
        {
            bool classNameUsed = Regex.IsMatch(consumerSource, $@"\b{Regex.Escape(c.ClassName)}\b");
            bool classTypeUsed = !string.IsNullOrEmpty(c.ClassType)
                && Regex.IsMatch(consumerSource, $@"\b{Regex.Escape(c.ClassType)}\b");
            if (classNameUsed || classTypeUsed)
            {
                keptNames.Add(c.ClassName);
                extendedSourceBuilder.AppendLine(c.Text);
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
        Console.WriteLine(
            $"prune: scanned {sourceFileCount} source files; "
            + $"kept {kept}/{candidates.Count} generated classes; {verb} {toPrune.Count}.");

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
            *.cs file under the --source directories. An IComfyType marker file is kept
            if its class name appears either in --source or in the content of a kept
            node file. Files under --generated-dir are excluded from the source scan
            so generated self-references don't defeat the prune. NodeRegistrations.g.cs
            is always preserved.

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
