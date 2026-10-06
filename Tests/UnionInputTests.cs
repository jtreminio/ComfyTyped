using ComfyTyped.CodeGen;
using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.Types;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ComfyTyped.Tests;

public class UnionInputTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ComfyTyped.UnionTests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private string GenerateNode(JToken inputType, JToken? defaultValue = null)
    {
        Directory.CreateDirectory(_tempDir);
        string schemaPath = Path.Combine(_tempDir, "object_info.json");
        string outputDir = Path.Combine(_tempDir, "generated");
        JArray spec = new(inputType.DeepClone());
        if (defaultValue is not null)
        {
            spec.Add(new JObject { ["default"] = defaultValue });
        }
        JObject schema = new()
        {
            ["UnionFixture"] = new JObject
            {
                ["input"] = new JObject
                {
                    ["required"] = new JObject { ["value"] = spec.DeepClone() },
                    ["optional"] = new JObject { ["optional_value"] = spec.DeepClone() }
                },
                ["output"] = new JArray()
            }
        };
        File.WriteAllText(schemaPath, schema.ToString());

        Assert.Equal(0, Program.Main([
            "--comfy-json", schemaPath,
            "--output", outputDir,
            "--namespace", "UnionFixture.Generated"
        ]));
        return File.ReadAllText(Path.Combine(outputDir, "UnionFixtureNode.g.cs"));
    }

    [Theory]
    [InlineData("CLIP,GEMMA")]
    [InlineData("FLOAT,INT")]
    [InlineData("IMAGE,MASK")]
    public void CodeGen_UnionInputs_UseConnectionBindings(string inputType)
    {
        string source = GenerateNode(new JValue(inputType));

        Assert.Contains("NodeInput<AnyType> Value", source);
        Assert.Contains("NodeInput<AnyType> OptionalValue", source);
        Assert.Contains("AnyArg? Value", source);
        Assert.Contains("AnyArg? OptionalValue", source);
        Assert.DoesNotContain("StringArg", source);
    }

    [Fact]
    public void CodeGen_ComboChoicesContainingCommas_RemainStrings()
    {
        string source = GenerateNode(new JArray("CLIP,GEMMA", "other"));

        Assert.Contains("NodeInput<StringType> Value", source);
        Assert.Contains("StringArg? Value", source);
    }

    [Fact]
    public void CodeGen_StringInput_RemainsString()
    {
        string source = GenerateNode(new JValue("STRING"));

        Assert.Contains("NodeInput<StringType> Value", source);
        Assert.Contains("StringArg? Value", source);
    }

    [Theory]
    [InlineData("FLOAT,INT", "25", "25L")]
    [InlineData("FLOAT,INT", "29.97", "29.97")]
    [InlineData("STRING,FILE_3D", "\"\"", "\"\"")]
    [InlineData("BOOLEAN,INT", "false", "false")]
    public void CodeGen_UnionInput_PreservesDefaultLiteral(string inputType, string jsonDefault, string literal)
    {
        string source = GenerateNode(new JValue(inputType), JToken.Parse(jsonDefault));

        Assert.Contains($"Value.Set({literal});", source);
        Assert.Contains($"OptionalValue.Set({literal});", source);
    }

    [Fact]
    public void UnionBinding_AcceptsNumericAndStringLiterals()
    {
        LTXVEmptyLatentAudioNode audio = new();
        Assert.Equal(25.0, audio.FrameRate.LiteralValue);

        audio.With(FrameRate: 30);
        Assert.Equal(30L, audio.FrameRate.LiteralValue);
        audio.With(FrameRate: 29.97);
        Assert.Equal(29.97, audio.FrameRate.LiteralValue);
        audio.With(FrameRate: "25");
        Assert.Equal("25", audio.FrameRate.LiteralValue);
    }

    [Fact]
    public void UnionBinding_AcceptsTypedConnection()
    {
        CheckpointLoaderSimpleNode checkpoint = new();
        SwarmJustLoadTheModelPleaseNode load = new SwarmJustLoadTheModelPleaseNode().With(
            Model: checkpoint.MODEL,
            Clip: new AnyArg(checkpoint.CLIP),
            Vae: checkpoint.VAE);

        Assert.Same(checkpoint.CLIP, load.Clip.Connection);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ModelLoad_ImportsSkippedSamplerWorkflow_AndRoundTrips(bool useGemma)
    {
        NodeRegistrations.EnsureRegistered();
        JObject workflow = SkippedSamplerWorkflow();
        if (useGemma)
        {
            NodeRegistry.Register("UnitTest_UnionGemmaLoader", () => new GemmaLoaderNode());
            workflow["200"] = new JObject
            {
                ["class_type"] = "UnitTest_UnionGemmaLoader",
                ["inputs"] = new JObject()
            };
            workflow["11"]!["inputs"]!["clip"] = new JArray("200", 0);
        }
        JObject expected = (JObject)workflow.DeepClone();

        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmJustLoadTheModelPleaseNode load = bridge.Graph.GetNode<SwarmJustLoadTheModelPleaseNode>("11")!;
        Assert.Equal(useGemma ? "GEMMA" : "CLIP", load.Clip.Connection!.TypeName);
        Assert.Equal(useGemma ? "200" : "3000", load.Clip.Connection.Node.Id);
        Assert.Equal(useGemma ? 0 : 1, load.Clip.Connection.SlotIndex);
        Assert.Equal("MODEL", load.Model.Connection!.TypeName);
        Assert.Equal("VAE", load.Vae.Connection!.TypeName);
        Assert.True(JToken.DeepEquals(expected, bridge.Graph.ToWorkflow()));
        Assert.True(JToken.DeepEquals(expected, workflow));
    }

    [Theory]
    [InlineData("model", "MODEL")]
    [InlineData("vae", "VAE")]
    public void ModelLoad_InvalidNonUnionInput_ReportsBothNodes(string inputName, string expectedType)
    {
        NodeRegistrations.EnsureRegistered();
        JObject workflow = SkippedSamplerWorkflow();
        workflow["11"]!["inputs"]![inputName] = new JArray("3000", 1);
        JObject expected = (JObject)workflow.DeepClone();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => WorkflowBridge.Create(workflow));

        Assert.Contains($"Cannot connect output of type 'CLIP' to input '{inputName}' of type '{expectedType}'.", error.Message);
        Assert.Contains("LoraLoader#3000", error.Message);
        Assert.Contains("output slot 1", error.Message);
        Assert.Contains("SwarmJustLoadTheModelPlease#11", error.Message);
        Assert.True(JToken.DeepEquals(expected, workflow));
    }

    [Fact]
    public void UnionBinding_ReplacesConnectionsAndLiterals_AndSyncsWorkflow()
    {
        NodeRegistrations.EnsureRegistered();
        using WorkflowBridge bridge = WorkflowBridge.Create(new JObject());
        SwarmInputIntegerNode source = bridge.AddNode(new SwarmInputIntegerNode(), "1");
        LTXVEmptyLatentAudioNode audio = bridge.AddNode(new LTXVEmptyLatentAudioNode(), "2");

        audio.With(FrameRate: new AnyArg(source.INT));
        Assert.Null(audio.FrameRate.LiteralValue);
        Assert.True(JToken.DeepEquals(new JArray("1", 0), bridge.Workflow["2"]!["inputs"]!["frame_rate"]));

        audio.With(FrameRate: 29.97);
        Assert.Null(audio.FrameRate.Connection);
        JToken literal = bridge.Workflow["2"]!["inputs"]!["frame_rate"]!;
        Assert.Equal(JTokenType.Float, literal.Type);
        Assert.Equal(29.97, literal.Value<double>());

        audio.With(FrameRate: new AnyArg(source.INT));
        audio.With();
        Assert.Same(source.INT, audio.FrameRate.Connection);
        Assert.Null(audio.FrameRate.LiteralValue);
        Assert.True(JToken.DeepEquals(new JArray("1", 0), bridge.Workflow["2"]!["inputs"]!["frame_rate"]));

        using WorkflowBridge reimported = WorkflowBridge.Create(bridge.Workflow);
        LTXVEmptyLatentAudioNode importedAudio = reimported.Graph.GetNode<LTXVEmptyLatentAudioNode>("2")!;
        Assert.Equal("INT", importedAudio.FrameRate.Connection!.TypeName);
    }

    // Refine Img with zero creativity can skip the base sampler. Swarm inserts
    // this model-load node, then Base2Edit imports the workflow through the bridge.
    // Separate loaders and a LoRA reproduce the CLIP output at a nonzero slot.
    private static JObject SkippedSamplerWorkflow() => JObject.Parse("""
        {
          "4": {
            "class_type": "UNETLoader",
            "inputs": { "unet_name": "test.safetensors", "weight_dtype": "default" }
          },
          "100": {
            "class_type": "CLIPLoader",
            "inputs": { "clip_name": "encoder.safetensors", "type": "krea2", "device": "default" }
          },
          "101": {
            "class_type": "VAELoader",
            "inputs": { "vae_name": "vae.safetensors" }
          },
          "3000": {
            "class_type": "LoraLoader",
            "inputs": {
              "model": ["4", 0], "clip": ["100", 0],
              "lora_name": "test.safetensors", "strength_model": 1.0, "strength_clip": 1.0
            }
          },
          "11": {
            "class_type": "SwarmJustLoadTheModelPlease",
            "inputs": { "model": ["3000", 0], "clip": ["3000", 1], "vae": ["101", 0] }
          }
        }
        """);

    private sealed class GemmaType : IComfyType
    {
        public static string TypeName => "GEMMA";
    }

    private sealed class GemmaLoaderNode : ComfyNode
    {
        public override string ClassTypeName => "UnitTest_UnionGemmaLoader";
        public NodeOutput<GemmaType> Gemma { get; }

        public GemmaLoaderNode()
        {
            Gemma = AddOutput<GemmaType>(0, "GEMMA");
        }
    }
}
