using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ComfyTyped.Tests;

public class RoundTripTests
{
    public RoundTripTests()
    {
        NodeRegistrations.EnsureRegistered();
    }

    [Fact]
    public void BuildSimpleWorkflow_SerializesCorrectly()
    {
        var graph = new ComfyGraph();

        var ckpt = graph.AddNode(new CheckpointLoaderSimpleNode());
        ckpt.CkptName.Set("model.safetensors");

        var posEncode = graph.AddNode(new CLIPTextEncodeNode());
        posEncode.Text.Set("a beautiful sunset");
        posEncode.Clip.ConnectTo(ckpt.CLIP);

        var negEncode = graph.AddNode(new CLIPTextEncodeNode());
        negEncode.Text.Set("ugly, blurry");
        negEncode.Clip.ConnectTo(ckpt.CLIP);

        var emptyLatent = graph.AddNode(new EmptyLatentImageNode());
        emptyLatent.Width.Set(512L);
        emptyLatent.Height.Set(512L);
        emptyLatent.BatchSize.Set(1L);

        var ksampler = graph.AddNode(new KSamplerNode());
        ksampler.Model.ConnectTo(ckpt.MODEL);
        ksampler.Seed.Set(42L);
        ksampler.Steps.Set(20L);
        ksampler.Cfg.Set(7.0);
        ksampler.SamplerName.Set("euler");
        ksampler.Scheduler.Set("normal");
        ksampler.Positive.ConnectTo(posEncode.CONDITIONING);
        ksampler.Negative.ConnectTo(negEncode.CONDITIONING);
        ksampler.LatentImage.ConnectTo(emptyLatent.LATENT);
        ksampler.Denoise.Set(1.0);

        var decode = graph.AddNode(new VAEDecodeNode());
        decode.Samples.ConnectTo(ksampler.LATENT);
        decode.Vae.ConnectTo(ckpt.VAE);

        var save = graph.AddNode(new SaveImageNode());
        save.Images.ConnectTo(decode.IMAGE);
        save.FilenamePrefix.Set("ComfyTyped_");

        JObject workflow = graph.ToWorkflow();

        // Verify structure
        Assert.Equal(7, workflow.Count);

        // Verify KSampler connections
        JObject ksNode = (JObject)workflow[ksampler.Id]!;
        Assert.Equal("KSampler", ksNode.Value<string>("class_type"));
        JObject ksInputs = (JObject)ksNode["inputs"]!;
        Assert.Equal(42L, (long)ksInputs["seed"]!);
        // model input should be a connection [ckpt.Id, 0]
        JArray modelConn = (JArray)ksInputs["model"]!;
        Assert.Equal(ckpt.Id, (string)modelConn[0]!);
        Assert.Equal(0, (int)modelConn[1]!);

        // Verify VAEDecode connections
        JObject decodeNode = (JObject)workflow[decode.Id]!;
        JArray samplesConn = (JArray)decodeNode["inputs"]!["samples"]!;
        Assert.Equal(ksampler.Id, (string)samplesConn[0]!);
        Assert.Equal(0, (int)samplesConn[1]!);
        JArray vaeConn = (JArray)decodeNode["inputs"]!["vae"]!;
        Assert.Equal(ckpt.Id, (string)vaeConn[0]!);
        Assert.Equal(2, (int)vaeConn[1]!); // VAE is output slot 2
    }

    [Fact]
    public void Deserialize_RoundTrips_Losslessly()
    {
        // Build a workflow manually as JObject (simulating what SwarmUI produces)
        JObject original = new()
        {
            ["4"] = new JObject
            {
                ["class_type"] = "CheckpointLoaderSimple",
                ["inputs"] = new JObject
                {
                    ["ckpt_name"] = "model.safetensors"
                }
            },
            ["6"] = new JObject
            {
                ["class_type"] = "CLIPTextEncode",
                ["inputs"] = new JObject
                {
                    ["text"] = "a cat",
                    ["clip"] = new JArray("4", 1)
                }
            },
            ["8"] = new JObject
            {
                ["class_type"] = "VAEDecode",
                ["inputs"] = new JObject
                {
                    ["samples"] = new JArray("10", 0),
                    ["vae"] = new JArray("4", 2)
                }
            },
            ["10"] = new JObject
            {
                ["class_type"] = "KSampler",
                ["inputs"] = new JObject
                {
                    ["model"] = new JArray("4", 0),
                    ["seed"] = 42,
                    ["steps"] = 20,
                    ["cfg"] = 7.0,
                    ["sampler_name"] = "euler",
                    ["scheduler"] = "normal",
                    ["positive"] = new JArray("6", 0),
                    ["negative"] = new JArray("7", 0),
                    ["latent_image"] = new JArray("5", 0),
                    ["denoise"] = 1.0
                }
            }
        };

        // Deserialize
        ComfyGraph graph = ComfyGraph.FromWorkflow(original);

        // Verify typed nodes were created
        Assert.IsType<CheckpointLoaderSimpleNode>(graph.GetNode("4"));
        Assert.IsType<CLIPTextEncodeNode>(graph.GetNode("6"));
        Assert.IsType<VAEDecodeNode>(graph.GetNode("8"));
        Assert.IsType<KSamplerNode>(graph.GetNode("10"));

        // Verify connections
        var ckpt = (CheckpointLoaderSimpleNode)graph.GetNode("4")!;
        var clip = (CLIPTextEncodeNode)graph.GetNode("6")!;
        var decode = (VAEDecodeNode)graph.GetNode("8")!;
        var ksampler = (KSamplerNode)graph.GetNode("10")!;

        Assert.True(clip.Clip.IsConnected);
        Assert.Same(ckpt, clip.Clip.TypedConnection!.Node);
        Assert.Equal(1, clip.Clip.TypedConnection!.SlotIndex); // CLIP is slot 1

        Assert.True(ksampler.Model.IsConnected);
        Assert.Same(ckpt, ksampler.Model.TypedConnection!.Node);

        Assert.True(decode.Vae.IsConnected);
        Assert.Same(ckpt, decode.Vae.TypedConnection!.Node);
        Assert.Equal(2, decode.Vae.TypedConnection!.SlotIndex); // VAE is slot 2

        // Verify literal values
        Assert.Equal("a cat", clip.Text.LiteralValue);
        Assert.Equal(42L, ksampler.Seed.LiteralValue);
        Assert.Equal("euler", ksampler.SamplerName.LiteralValue);

        // Serialize back
        JObject roundTripped = graph.ToWorkflow();

        // Verify key fields survived the round-trip
        Assert.Equal("CheckpointLoaderSimple", roundTripped["4"]!.Value<string>("class_type"));
        Assert.Equal("model.safetensors", roundTripped["4"]!["inputs"]!.Value<string>("ckpt_name"));
        Assert.Equal(42L, (long)roundTripped["10"]!["inputs"]!["seed"]!);
        // KSampler model connection should still point to node 4, slot 0
        JArray modelRef = (JArray)roundTripped["10"]!["inputs"]!["model"]!;
        Assert.Equal("4", (string)modelRef[0]!);
        Assert.Equal(0, (int)modelRef[1]!);
    }

    [Fact]
    public void UnknownNodes_PreserveData()
    {
        JObject workflow = new()
        {
            ["1"] = new JObject
            {
                ["class_type"] = "SomeCustomNodeThatDoesNotExist",
                ["inputs"] = new JObject
                {
                    ["param1"] = "hello",
                    ["param2"] = 42,
                    ["upstream"] = new JArray("2", 0)
                }
            },
            ["2"] = new JObject
            {
                ["class_type"] = "AnotherUnknownNode",
                ["inputs"] = new JObject
                {
                    ["value"] = 3.14
                }
            }
        };

        ComfyGraph graph = ComfyGraph.FromWorkflow(workflow);

        var node1 = graph.GetNode("1");
        var node2 = graph.GetNode("2");
        Assert.IsType<UnknownNode>(node1);
        Assert.IsType<UnknownNode>(node2);
        Assert.Equal("SomeCustomNodeThatDoesNotExist", node1!.ClassTypeName);

        // Round-trip should preserve raw data
        JObject rt = graph.ToWorkflow();
        Assert.Equal("hello", rt["1"]!["inputs"]!.Value<string>("param1"));
        Assert.Equal(3.14, (double)rt["2"]!["inputs"]!["value"]!); // preserves via raw inputs
    }

    [Fact]
    public void FindNearestUpstream_FindsTypedNode()
    {
        var graph = new ComfyGraph();
        var ckpt = graph.AddNode(new CheckpointLoaderSimpleNode());
        var encode = graph.AddNode(new CLIPTextEncodeNode());
        encode.Clip.ConnectTo(ckpt.CLIP);
        var ksampler = graph.AddNode(new KSamplerNode());
        ksampler.Positive.ConnectTo(encode.CONDITIONING);
        ksampler.Model.ConnectTo(ckpt.MODEL);

        // From KSampler, find nearest upstream CheckpointLoaderSimple
        var found = graph.FindNearestUpstream<CheckpointLoaderSimpleNode>(ksampler);
        Assert.NotNull(found);
        Assert.Same(ckpt, found);

        // From CLIPTextEncode, find nearest upstream CheckpointLoaderSimple
        var found2 = graph.FindNearestUpstream<CheckpointLoaderSimpleNode>(encode);
        Assert.NotNull(found2);
        Assert.Same(ckpt, found2);
    }

    [Fact]
    public void TypeSafety_CompileTimeCheck()
    {
        // This test just verifies the API shapes work.
        // The real type safety is at compile time — you literally can't write:
        //   decode.Samples.ConnectTo(ckpt.MODEL)  // won't compile: ModelType != LatentType

        var graph = new ComfyGraph();
        var ckpt = graph.AddNode(new CheckpointLoaderSimpleNode());
        var decode = graph.AddNode(new VAEDecodeNode());

        // This compiles because VAE output -> VAE input
        decode.Vae.ConnectTo(ckpt.VAE);
        Assert.True(decode.Vae.IsConnected);

        // These would NOT compile (uncomment to verify):
        // decode.Vae.ConnectTo(ckpt.MODEL);   // error: ModelType vs VaeType
        // decode.Vae.ConnectTo(ckpt.CLIP);    // error: ClipType vs VaeType
        // decode.Samples.ConnectTo(ckpt.VAE); // error: VaeType vs LatentType
    }
}
