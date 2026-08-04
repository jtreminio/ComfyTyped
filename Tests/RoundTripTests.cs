using ComfyTyped.Core;
using ComfyTyped.Families;
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
        ComfyGraph graph = new ComfyGraph();

        CheckpointLoaderSimpleNode ckpt = graph.AddNode(new CheckpointLoaderSimpleNode());
        ckpt.CkptName.Set("model.safetensors");

        CLIPTextEncodeNode posEncode = graph.AddNode(new CLIPTextEncodeNode());
        posEncode.Text.Set("a beautiful sunset");
        posEncode.Clip.ConnectTo(ckpt.CLIP);

        CLIPTextEncodeNode negEncode = graph.AddNode(new CLIPTextEncodeNode());
        negEncode.Text.Set("ugly, blurry");
        negEncode.Clip.ConnectTo(ckpt.CLIP);

        EmptyLatentImageNode emptyLatent = graph.AddNode(new EmptyLatentImageNode());
        emptyLatent.Width.Set(512L);
        emptyLatent.Height.Set(512L);
        emptyLatent.BatchSize.Set(1L);

        KSamplerNode ksampler = graph.AddNode(new KSamplerNode());
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

        VAEDecodeNode decode = graph.AddNode(new VAEDecodeNode());
        decode.Samples.ConnectTo(ksampler.LATENT);
        decode.Vae.ConnectTo(ckpt.VAE);

        SaveImageNode save = graph.AddNode(new SaveImageNode());
        save.ImagesInput.ConnectTo(decode.IMAGE);
        save.FilenamePrefix.Set("ComfyTyped_");

        JObject workflow = graph.ToWorkflow();

        Assert.Equal(7, workflow.Count);

        JObject ksNode = (JObject)workflow[ksampler.Id]!;
        Assert.Equal("KSampler", ksNode.Value<string>("class_type"));
        JObject ksInputs = (JObject)ksNode["inputs"]!;
        Assert.Equal(42L, (long)ksInputs["seed"]!);
        JArray modelConn = (JArray)ksInputs["model"]!;
        Assert.Equal(ckpt.Id, (string)modelConn[0]!);
        Assert.Equal(0, (int)modelConn[1]!);

        JObject decodeNode = (JObject)workflow[decode.Id]!;
        JArray samplesConn = (JArray)decodeNode["inputs"]!["samples"]!;
        Assert.Equal(ksampler.Id, (string)samplesConn[0]!);
        Assert.Equal(0, (int)samplesConn[1]!);
        JArray vaeConn = (JArray)decodeNode["inputs"]!["vae"]!;
        Assert.Equal(ckpt.Id, (string)vaeConn[0]!);
        Assert.Equal(2, (int)vaeConn[1]!);
    }

    [Fact]
    public void Deserialize_RoundTrips_Losslessly()
    {
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

        ComfyGraph graph = ComfyGraph.FromWorkflow(original);

        Assert.IsType<CheckpointLoaderSimpleNode>(graph.GetNode("4"));
        Assert.IsType<CLIPTextEncodeNode>(graph.GetNode("6"));
        Assert.IsType<VAEDecodeNode>(graph.GetNode("8"));
        Assert.IsType<KSamplerNode>(graph.GetNode("10"));

        CheckpointLoaderSimpleNode ckpt = (CheckpointLoaderSimpleNode)graph.GetNode("4")!;
        CLIPTextEncodeNode clip = (CLIPTextEncodeNode)graph.GetNode("6")!;
        VAEDecodeNode decode = (VAEDecodeNode)graph.GetNode("8")!;
        KSamplerNode ksampler = (KSamplerNode)graph.GetNode("10")!;

        Assert.True(clip.Clip.IsConnected);
        Assert.Same(ckpt, clip.Clip.TypedConnection!.Node);
        Assert.Equal(1, clip.Clip.TypedConnection!.SlotIndex);

        Assert.True(ksampler.Model.IsConnected);
        Assert.Same(ckpt, ksampler.Model.TypedConnection!.Node);

        Assert.True(decode.Vae.IsConnected);
        Assert.Same(ckpt, decode.Vae.TypedConnection!.Node);
        Assert.Equal(2, decode.Vae.TypedConnection!.SlotIndex);

        Assert.Equal("a cat", clip.Text.LiteralValue);
        Assert.Equal(42L, ksampler.Seed.LiteralValue);
        Assert.Equal("euler", ksampler.SamplerName.LiteralValue);

        JObject roundTripped = graph.ToWorkflow();

        Assert.Equal("CheckpointLoaderSimple", roundTripped["4"]!.Value<string>("class_type"));
        Assert.Equal("model.safetensors", roundTripped["4"]!["inputs"]!.Value<string>("ckpt_name"));
        Assert.Equal(42L, (long)roundTripped["10"]!["inputs"]!["seed"]!);
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

        ComfyNode? node1 = graph.GetNode("1");
        ComfyNode? node2 = graph.GetNode("2");
        Assert.IsType<UnknownNode>(node1);
        Assert.IsType<UnknownNode>(node2);
        Assert.Equal("SomeCustomNodeThatDoesNotExist", node1!.ClassTypeName);

        JObject rt = graph.ToWorkflow();
        Assert.Equal("hello", rt["1"]!["inputs"]!.Value<string>("param1"));
        Assert.Equal(3.14, (double)rt["2"]!["inputs"]!["value"]!);
    }

    [Fact]
    public void FindNearestUpstream_FindsTypedNode()
    {
        ComfyGraph graph = new ComfyGraph();
        CheckpointLoaderSimpleNode ckpt = graph.AddNode(new CheckpointLoaderSimpleNode());
        CLIPTextEncodeNode encode = graph.AddNode(new CLIPTextEncodeNode());
        encode.Clip.ConnectTo(ckpt.CLIP);
        KSamplerNode ksampler = graph.AddNode(new KSamplerNode());
        ksampler.Positive.ConnectTo(encode.CONDITIONING);
        ksampler.Model.ConnectTo(ckpt.MODEL);

        CheckpointLoaderSimpleNode? found = graph.FindNearestUpstream<CheckpointLoaderSimpleNode>(ksampler);
        Assert.NotNull(found);
        Assert.Same(ckpt, found);

        CheckpointLoaderSimpleNode? found2 = graph.FindNearestUpstream<CheckpointLoaderSimpleNode>(encode);
        Assert.NotNull(found2);
        Assert.Same(ckpt, found2);
    }

    /// <summary>
    /// The shape this exists for: one latent, two decodes, a sampler between them. Unbounded,
    /// "nearest" is whichever decode the walk reaches first and the caller cannot tell the two
    /// apart; with the sampler as a barrier the answer is the decode of the latent actually asked
    /// about.
    /// </summary>
    [Fact]
    public void FindNearestDownstream_StopsAtTheBarrierType()
    {
        ComfyGraph graph = new ComfyGraph();
        EmptyLatentImageNode latent = graph.AddNode(new EmptyLatentImageNode());
        KSamplerNode sampler = graph.AddNode(new KSamplerNode());
        sampler.LatentImage.ConnectTo(latent.LATENT);
        VAEDecodeNode refinedDecode = graph.AddNode(new VAEDecodeNode());
        refinedDecode.Samples.ConnectTo(sampler.LATENT);

        Assert.Same(refinedDecode, graph.FindNearestDownstream<IVaeDecode>(latent.LATENT));
        Assert.Null(graph.FindNearestDownstream<IVaeDecode, KSamplerNode>(latent.LATENT));

        VAEDecodeTiledNode ownDecode = graph.AddNode(new VAEDecodeTiledNode());
        ownDecode.Samples.ConnectTo(latent.LATENT);

        Assert.Same(ownDecode, graph.FindNearestDownstream<IVaeDecode, KSamplerNode>(latent.LATENT));
        Assert.Same(
            ownDecode,
            graph.FindNearestDownstream<IVaeDecode>(latent.LATENT, node => node is KSamplerNode));
    }

    [Fact]
    public void TypeSafety_CompileTimeCheck()
    {
        ComfyGraph graph = new ComfyGraph();
        CheckpointLoaderSimpleNode ckpt = graph.AddNode(new CheckpointLoaderSimpleNode());
        VAEDecodeNode decode = graph.AddNode(new VAEDecodeNode());

        decode.Vae.ConnectTo(ckpt.VAE);
        Assert.True(decode.Vae.IsConnected);
    }
}
