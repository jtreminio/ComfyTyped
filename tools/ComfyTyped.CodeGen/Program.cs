using System.Globalization;
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
    private const string CoreIOTypeMapTypeName = "ComfyTyped.Types.IOTypeMap";

    // IO types that map to C# primitives (can be literal values)
    private static readonly Dictionary<string, (string MarkerType, string CSharpType, string DefaultLiteral)> PrimitiveTypes = new()
    {
        ["INT"] = ("IntType", "long", "0"),
        ["FLOAT"] = ("FloatType", "double", "0.0"),
        ["STRING"] = ("StringType", "string", "\"\""),
        ["BOOLEAN"] = ("BooleanType", "bool", "false"),
    };

    // Default ComfyUI type → marker mapping (all in the ComfyTyped.Types namespace).
    // Extensions add their own via --extra-type-mappings.
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

    private sealed record MarkerInfo(string ShortName, string Namespace);

    private sealed record Options(
        string ObjectInfoPath,
        string OutputDir,
        string Namespace,
        string RegistrationsClass,
        List<string> ExtraTypeMappingPaths,
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

        // Build the type mapping: core defaults + extras (last writer wins).
        Dictionary<string, MarkerInfo> typeMapping = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string comfyType, string shortName) in CoreTypeMapping)
        {
            typeMapping[comfyType] = new MarkerInfo(shortName, CoreMarkerNamespace);
        }
        foreach (string path in opts.ExtraTypeMappingPaths)
        {
            LoadExtraTypeMappings(path, typeMapping);
        }

        // Build skip-sets from --core-assembly (diff mode).
        HashSet<string> classTypeSkipSet = new(StringComparer.Ordinal);
        HashSet<string> knownTypeNameSet = new(StringComparer.OrdinalIgnoreCase);
        if (opts.CoreAssemblyPath is not null)
        {
            LoadCoreSkipSets(opts.CoreAssemblyPath, classTypeSkipSet, knownTypeNameSet);
            Console.WriteLine($"Diff mode: {classTypeSkipSet.Count} class_types and {knownTypeNameSet.Count} marker types loaded from core assembly.");
        }

        JObject objectInfo = JObject.Parse(File.ReadAllText(opts.ObjectInfoPath));
        Directory.CreateDirectory(opts.OutputDir);
        ClearGeneratedFiles(opts.OutputDir);

        int generated = 0;
        int skippedDiff = 0;
        int skippedParse = 0;
        int skippedNonNative = 0;

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

            NodeDef? nodeDef = ParseNodeDef(classType, nodeInfo, typeMapping);
            if (nodeDef is null)
            {
                Console.Error.WriteLine($"  SKIP: {classType} (could not parse)");
                skippedParse++;
                continue;
            }

            string code = GenerateNodeClass(nodeDef, opts.Namespace);
            string fileName = $"{nodeDef.ClassName}.g.cs";
            File.WriteAllText(Path.Combine(opts.OutputDir, fileName), code);
            generated++;
        }

        string registrationCode = GenerateRegistrationFile(opts.Namespace, opts.RegistrationsClass);
        File.WriteAllText(Path.Combine(opts.OutputDir, $"{opts.RegistrationsClass}.g.cs"), registrationCode);

        Console.WriteLine($"Generated {generated} node classes; skipped {skippedDiff} (already in core), {skippedNonNative} (non-native), {skippedParse} (parse).");
        return 0;
    }

    // SwarmUI's "native" surface area is two groups of packs, both treated as native here:
    //   1. Bundled — ship with SwarmUI by default (Swarm* prefixes).
    //   2. Installable features — registered in upstream
    //      SwarmUI/src/Core/InstallableFeatures.cs and fetched via EnsureNodeRepo when
    //      the user triggers the corresponding feature (some auto-install at startup,
    //      others install on button click — both go through the same registry).
    // Source of truth: upstream/master InstallableFeatures.cs static constructor.
    // Re-sync this list if SwarmUI adds/removes a RegisterInstallableFeature call.
    private static readonly HashSet<string> SwarmNativeModules = new(StringComparer.Ordinal)
    {
        // Bundled
        "custom_nodes.SwarmComfyCommon",
        "custom_nodes.SwarmComfyExtra",

        // Installable features (InstallableFeatures.cs)
        "custom_nodes.ComfyUI_IPAdapter_plus",         // ipadapter
        "custom_nodes.comfyui_controlnet_aux",         // controlnet_preprocessors
        "custom_nodes.ComfyUI-Frame-Interpolation",    // frame_interpolation
        "custom_nodes.ComfyUI-GIMM-VFI",               // gimm_vfi
        "custom_nodes.ComfyUI_TensorRT",               // comfyui_tensorrt
        "custom_nodes.ComfyUI-segment-anything-2",     // sam2
        "custom_nodes.ComfyUI_bnb_nf4_fp4_Loaders",    // bnb_nf4
        "custom_nodes.ComfyUI-GGUF",                   // gguf
        "custom_nodes.ComfyUI_ExtraModels",            // extramodels
        "custom_nodes.ComfyUI-nunchaku",               // nunchaku
        "custom_nodes.ComfyUI-TeaCache",               // teacache
        "custom_nodes.ComfyUI-SAI_API",                // sai_api
    };

    // A node is "native" if its python_module is the core `nodes` module, anything under
    // `comfy_extras.*`, or one of the SwarmUI-shipped packs above.
    // Everything else (comfy_api_nodes.*, third-party custom_nodes.*, missing) is non-native.
    private static bool IsNativeModule(string? pythonModule)
    {
        if (string.IsNullOrEmpty(pythonModule))
        {
            return false;
        }

        return pythonModule == "nodes"
            || pythonModule.StartsWith("comfy_extras.", StringComparison.Ordinal)
            || SwarmNativeModules.Contains(pythonModule);
    }

    // ── CLI parsing ─────────────────────────────────────────────────

    private static Options? ParseArgs(string[] args)
    {
        string? objectInfoPath = null;
        string? outputDir = null;
        string ns = "ComfyTyped.Generated";
        string registrationsClass = "NodeRegistrations";
        List<string> extraMappings = [];
        string? coreAssembly = null;
        bool nativeOnly = false;
        List<string> positional = [];

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "--object-info":
                    objectInfoPath = NextArg(args, ref i, a);
                    break;
                case "--output":
                    outputDir = NextArg(args, ref i, a);
                    break;
                case "--namespace":
                    ns = NextArg(args, ref i, a);
                    break;
                case "--registrations-class":
                    registrationsClass = NextArg(args, ref i, a);
                    break;
                case "--extra-type-mappings":
                    extraMappings.Add(NextArg(args, ref i, a));
                    break;
                case "--core-assembly":
                    coreAssembly = NextArg(args, ref i, a);
                    break;
                case "--native-only":
                    nativeOnly = true;
                    break;
                case "--help" or "-h":
                    PrintUsage();
                    return null;
                default:
                    if (a.StartsWith("--", StringComparison.Ordinal))
                    {
                        Console.Error.WriteLine($"Unknown flag: {a}");
                        PrintUsage();
                        return null;
                    }
                    positional.Add(a);
                    break;
            }
        }

        // Back-compat: positional <object_info> <output>
        if (objectInfoPath is null && positional.Count >= 1) objectInfoPath = positional[0];
        if (outputDir is null && positional.Count >= 2) outputDir = positional[1];

        if (objectInfoPath is null || outputDir is null)
        {
            PrintUsage();
            return null;
        }

        return new Options(objectInfoPath, outputDir, ns, registrationsClass, extraMappings, coreAssembly, nativeOnly);
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
        Console.Error.WriteLine("""
            Usage: ComfyTyped.CodeGen [options] [<object_info.json> <output_dir>]
                   ComfyTyped.CodeGen prune --generated-dir <dir> --source <dir> [--source <dir>...] [--dry-run]

            Options:
              --object-info <path>             Path to the ComfyUI object_info.json dump.
              --output <dir>                   Output directory for *.g.cs files.
              --namespace <ns>                 Generated namespace (default: ComfyTyped.Generated).
              --registrations-class <name>     Static class name for node registrations
                                               (default: NodeRegistrations).
              --extra-type-mappings <path>     JSON file mapping ComfyUI type names to fully-
                                               qualified marker types. May be repeated.
                                               Example value: { "SEEDVR2_DIT": "SwarmUI.SeedVR2.Types.SeedVr2DitType" }
              --core-assembly <path>           ComfyTyped.dll. When provided, class_types
                                               already registered by core are skipped (diff mode).
              --native-only                    Only emit nodes whose python_module is `nodes`,
                                               starts with `comfy_extras.`, or is one of the
                                               SwarmUI-bundled / SwarmUI-installable packs
                                               (see SwarmNativeModules in source). Use when
                                               generating the core assembly so api/third-party
                                               custom nodes are excluded.
              -h, --help                       Show this message.

            Positional <object_info.json> <output_dir> are accepted for back-compat.
            """);
    }

    // ── Extra type mappings ─────────────────────────────────────────

    private static void LoadExtraTypeMappings(string path, Dictionary<string, MarkerInfo> mapping)
    {
        JObject json = JObject.Parse(File.ReadAllText(path));
        foreach (JProperty prop in json.Properties())
        {
            string fqn = prop.Value?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(fqn))
            {
                Console.Error.WriteLine($"  WARN: skipping mapping {prop.Name} (empty value) in {path}");
                continue;
            }
            int dot = fqn.LastIndexOf('.');
            MarkerInfo info = dot < 0
                ? new MarkerInfo(fqn, CoreMarkerNamespace)
                : new MarkerInfo(fqn[(dot + 1)..], fqn[..dot]);
            mapping[prop.Name] = info;
        }
    }

    // ── Diff mode ───────────────────────────────────────────────────

    private static void LoadCoreSkipSets(string coreAssemblyPath, HashSet<string> classTypes, HashSet<string> typeNames)
    {
        Assembly asm = Assembly.LoadFrom(Path.GetFullPath(coreAssemblyPath));

        // Invoke ComfyTyped.Generated.NodeRegistrations.EnsureRegistered()
        Type? regType = asm.GetType(CoreNodeRegistrationsTypeName);
        MethodInfo? ensure = regType?.GetMethod("EnsureRegistered", BindingFlags.Public | BindingFlags.Static);
        if (ensure is null)
        {
            throw new InvalidOperationException(
                $"Could not find {CoreNodeRegistrationsTypeName}.EnsureRegistered() in {coreAssemblyPath}.");
        }
        ensure.Invoke(null, null);

        // Read NodeRegistry.RegisteredTypes
        Type? registryType = asm.GetType(CoreNodeRegistryTypeName)
            ?? throw new InvalidOperationException($"Could not find {CoreNodeRegistryTypeName} in {coreAssemblyPath}.");
        PropertyInfo? regProp = registryType.GetProperty("RegisteredTypes", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException($"{CoreNodeRegistryTypeName}.RegisteredTypes not found.");
        if (regProp.GetValue(null) is IEnumerable<string> known)
        {
            foreach (string s in known) classTypes.Add(s);
        }

        // Read IOTypeMap.KnownTypeNames (advisory only — used by callers who want to detect collisions)
        Type? mapType = asm.GetType(CoreIOTypeMapTypeName);
        PropertyInfo? namesProp = mapType?.GetProperty("KnownTypeNames", BindingFlags.Public | BindingFlags.Static);
        if (namesProp?.GetValue(null) is IEnumerable<string> names)
        {
            foreach (string s in names) typeNames.Add(s);
        }
    }

    // ── Output cleanup ──────────────────────────────────────────────

    private static void ClearGeneratedFiles(string outputDir)
    {
        if (!Directory.Exists(outputDir)) return;
        foreach (string file in Directory.EnumerateFiles(outputDir, "*.g.cs", SearchOption.TopDirectoryOnly))
        {
            File.Delete(file);
        }
    }

    // ── Parsing ─────────────────────────────────────────────────────

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

    private static NodeDef? ParseNodeDef(string classType, JObject nodeInfo, Dictionary<string, MarkerInfo> typeMapping)
    {
        List<InputDef> inputs = [];
        List<OutputDef> outputs = [];

        // Parse outputs FIRST so input names can avoid collisions
        JArray? outputTypes = nodeInfo["output"] as JArray;
        JArray? outputNames = nodeInfo["output_name"] as JArray;
        if (outputTypes is not null)
        {
            for (int i = 0; i < outputTypes.Count; i++)
            {
                string comfyType = outputTypes[i]?.ToString() ?? "*";
                string slotName = outputNames is not null && i < outputNames.Count
                    ? outputNames[i]?.ToString() ?? comfyType
                    : comfyType;
                MarkerInfo marker = ResolveMarkerType(comfyType, typeMapping);
                string propName = SanitizeOutputPropertyName(slotName, i, outputs);
                outputs.Add(new OutputDef(slotName, propName, i, comfyType, marker));
            }
        }

        // Parse inputs
        JObject? inputSection = nodeInfo["input"] as JObject;
        if (inputSection is not null)
        {
            _currentOutputs = outputs;
            ParseInputSection(inputSection["required"] as JObject, required: true, inputs, typeMapping);
            ParseInputSection(inputSection["optional"] as JObject, required: false, inputs, typeMapping);
            _currentOutputs = null;
        }

        string className = SanitizeClassName(classType);
        string? category = nodeInfo.Value<string>("category");
        string? description = nodeInfo.Value<string>("description");

        return new NodeDef(classType, className, inputs, outputs, category, description);
    }

    // Thread-local scratch for passing outputs list into ParseInputSection
    [ThreadStatic] private static List<OutputDef>? _currentOutputs;

    private static void ParseInputSection(JObject? section, bool required, List<InputDef> inputs, Dictionary<string, MarkerInfo> typeMapping)
    {
        if (section is null)
        {
            return;
        }

        foreach (JProperty inputProp in section.Properties())
        {
            string inputName = inputProp.Name;
            if (inputProp.Value is not JArray spec || spec.Count == 0)
            {
                continue;
            }

            string comfyType;
            object? defaultValue = null;

            if (spec[0] is JArray)
            {
                // COMBO type: first element is an array of allowed values
                comfyType = "COMBO";
            }
            else
            {
                comfyType = spec[0]?.ToString() ?? "*";
            }

            // Extract default value from options dict
            if (spec.Count >= 2 && spec[1] is JObject options)
            {
                JToken? defToken = options["default"];
                if (defToken is not null)
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
            }

            // Determine the effective type for COMBO and special types
            string effectiveType = comfyType;
            if (comfyType == "COMBO" || comfyType.Contains(','))
            {
                effectiveType = "STRING"; // COMBO and multi-types become string inputs
            }
            // Handle V3 special types
            if (comfyType.StartsWith("COMFY_AUTOGROW_V3") || comfyType.StartsWith("COMFY_DYNAMICCOMBO_V3"))
            {
                effectiveType = "STRING";
            }

            MarkerInfo marker = ResolveMarkerType(effectiveType, typeMapping);
            bool isPrimitive = PrimitiveTypes.ContainsKey(effectiveType);
            string? csharpType = isPrimitive ? PrimitiveTypes[effectiveType].CSharpType : null;
            string propName = SanitizeInputPropertyName(inputName, inputs, _currentOutputs);

            inputs.Add(new InputDef(inputName, propName, comfyType, marker, required, isPrimitive, csharpType, defaultValue));
        }
    }

    // ── Code generation ─────────────────────────────────────────────

    private static string GenerateNodeClass(NodeDef node, string ns)
    {
        StringBuilder sb = new();

        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("using ComfyTyped.Core;");

        // Emit a using for every distinct marker namespace this node references.
        // ComfyTyped.Types is always emitted because primitives and AnyType live there.
        SortedSet<string> markerNamespaces = new(StringComparer.Ordinal) { CoreMarkerNamespace };
        foreach (OutputDef o in node.Outputs) markerNamespaces.Add(o.Marker.Namespace);
        foreach (InputDef inp in node.Inputs) markerNamespaces.Add(inp.Marker.Namespace);
        foreach (string nsRef in markerNamespaces)
        {
            sb.AppendLine($"using {nsRef};");
        }

        sb.AppendLine();
        sb.AppendLine($"namespace {ns};");
        sb.AppendLine();

        // XML doc
        if (!string.IsNullOrWhiteSpace(node.Description))
        {
            string escaped = EscapeXml(node.Description);
            string[] lines = escaped.Split('\n');
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

        // Output properties
        if (node.Outputs.Count > 0)
        {
            sb.AppendLine("    // ── Outputs ──");
            foreach (OutputDef output in node.Outputs)
            {
                sb.AppendLine($"    public NodeOutput<{output.Marker.ShortName}> {output.PropertyName} {{ get; }}");
            }
            sb.AppendLine();
        }

        // Input properties
        if (node.Inputs.Count > 0)
        {
            sb.AppendLine("    // ── Inputs ──");
            foreach (InputDef input in node.Inputs)
            {
                string reqMarker = input.Required ? "" : " // optional";
                sb.AppendLine($"    public NodeInput<{input.Marker.ShortName}> {input.PropertyName} {{ get; }}{reqMarker}");
            }
            sb.AppendLine();
        }

        // Constructor
        sb.AppendLine($"    public {node.ClassName}()");
        sb.AppendLine("    {");
        foreach (OutputDef output in node.Outputs)
        {
            sb.AppendLine($"        {output.PropertyName} = AddOutput<{output.Marker.ShortName}>({output.SlotIndex}, \"{output.Name}\");");
        }
        foreach (InputDef input in node.Inputs)
        {
            sb.AppendLine($"        {input.PropertyName} = AddInput<{input.Marker.ShortName}>(\"{input.Name}\", required: {(input.Required ? "true" : "false")});");
            // Set default value if primitive and has a default
            if (input.IsPrimitive && input.DefaultValue is not null)
            {
                string literal = FormatLiteral(input.DefaultValue, input.CSharpType!);
                sb.AppendLine($"        {input.PropertyName}.Set({literal});");
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

    // ── Naming helpers ──────────────────────────────────────────────

    private static string SanitizeClassName(string classType)
    {
        string sanitized = InvalidCharsRegex().Replace(classType, "_");
        if (sanitized.Length > 0 && char.IsDigit(sanitized[0]))
        {
            sanitized = "_" + sanitized;
        }
        sanitized = ToPascalCase(sanitized);

        return sanitized + "Node";
    }

    private static string SanitizeInputPropertyName(string inputName, List<InputDef> existingInputs, List<OutputDef>? existingOutputs)
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
            || (existingOutputs?.Any(o => o.PropertyName == name) ?? false))
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

    private static MarkerInfo ResolveMarkerType(string comfyType, Dictionary<string, MarkerInfo> typeMapping)
    {
        if (typeMapping.TryGetValue(comfyType, out MarkerInfo? marker))
        {
            return marker;
        }

        return new MarkerInfo("AnyType", CoreMarkerNamespace);
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
            double d => d.ToString("G", CultureInfo.InvariantCulture) + (d == Math.Floor(d) && !d.ToString(CultureInfo.InvariantCulture).Contains('E') ? ".0" : ""),
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

    private static string EscapeCSharpString(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");

    private static string EscapeXml(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    [GeneratedRegex(@"[^a-zA-Z0-9_]")]
    private static partial Regex InvalidCharsRegex();

    [GeneratedRegex(@"public\s+sealed\s+class\s+([A-Za-z_][A-Za-z0-9_]*)\s*:\s*ComfyNode")]
    private static partial Regex GeneratedClassRegex();

    // ── Prune subcommand ────────────────────────────────────────────
    //
    // Use case: an extension developer dumps object_info.json from their local
    // ComfyUI (which has unrelated custom-node packs installed), generates with
    // --core-assembly to filter out core, and ends up with .g.cs files for
    // every non-core class_type — including packs they don't actually use.
    // `prune` deletes any *.g.cs whose class name is never referenced as an
    // identifier in the extension's own source files. The reflection-based
    // NodeRegistrations.EnsureRegistered() automatically reflects the surviving
    // set after a recompile, so no registration list needs editing.

    private sealed record PruneOptions(string GeneratedDir, List<string> SourceDirs, bool DryRun);

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

        // Map every *.g.cs file (except NodeRegistrations) to the class it declares.
        Dictionary<string, string> classToPath = new(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(opts.GeneratedDir, "*.g.cs", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(file);
            if (name.Equals("NodeRegistrations.g.cs", StringComparison.Ordinal))
            {
                continue;
            }

            string text = File.ReadAllText(file);
            Match m = GeneratedClassRegex().Match(text);
            if (!m.Success)
            {
                Console.Error.WriteLine($"prune: skipping {name} (no `public sealed class X : ComfyNode` declaration found)");
                continue;
            }
            classToPath[m.Groups[1].Value] = file;
        }

        if (classToPath.Count == 0)
        {
            Console.WriteLine("prune: no candidate generated files found.");
            return 0;
        }

        // Concatenate every source file once, then test each class name with a
        // whole-word regex against that combined buffer.
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
                allSource.AppendLine(File.ReadAllText(file));
                sourceFileCount++;
            }
        }
        string combined = allSource.ToString();

        List<string> toPrune = [];
        foreach ((string className, string _) in classToPath)
        {
            if (!Regex.IsMatch(combined, $@"\b{Regex.Escape(className)}\b"))
            {
                toPrune.Add(className);
            }
        }

        toPrune.Sort(StringComparer.Ordinal);
        foreach (string className in toPrune)
        {
            string path = classToPath[className];
            string rel = Path.GetRelativePath(Environment.CurrentDirectory, path);
            if (opts.DryRun)
            {
                Console.WriteLine($"would prune: {rel}");
            }
            else
            {
                File.Delete(path);
                Console.WriteLine($"pruned: {rel}");
            }
        }

        int kept = classToPath.Count - toPrune.Count;
        string verb = opts.DryRun ? "would prune" : "pruned";
        Console.WriteLine($"prune: scanned {sourceFileCount} source files; kept {kept}/{classToPath.Count} generated classes; {verb} {toPrune.Count}.");

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
                case "--generated-dir":
                    generatedDir = NextArg(args, ref i, a);
                    break;
                case "--source":
                    sourceDirs.Add(NextArg(args, ref i, a));
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--help" or "-h":
                    PrintPruneUsage();
                    return null;
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

            Deletes *.g.cs files in --generated-dir whose class name is not referenced
            as an identifier in any *.cs file under the --source directories.
            NodeRegistrations.g.cs is always preserved.

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
