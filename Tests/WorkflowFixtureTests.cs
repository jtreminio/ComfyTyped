using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ComfyTyped.Tests;

public class WorkflowFixtureTests
{
    public WorkflowFixtureTests()
    {
        NodeRegistrations.EnsureRegistered();
    }

    private static JObject LoadFixture()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "workflow_api.json");
        return JObject.Parse(File.ReadAllText(path));
    }

    [Fact]
    public void Load_AllNodesResolveToTypedClasses()
    {
        ComfyGraph graph = ComfyGraph.FromWorkflow(LoadFixture());

        Assert.Equal(31, graph.Nodes.Count);
        List<string> unknown = graph.Nodes
            .Where(kv => kv.Value is UnknownNode)
            .Select(kv => $"{kv.Key} = {kv.Value.ClassTypeName}")
            .ToList();
        Assert.Empty(unknown);
    }

    [Fact]
    public void Load_PreservesScalarLiterals()
    {
        ComfyGraph graph = ComfyGraph.FromWorkflow(LoadFixture());

        UNETLoaderNode unet = graph.GetNode<UNETLoaderNode>("4")!;
        Assert.Equal("z-image/z_image_bf16.safetensors", unet.UnetName.LiteralValue);

        SwarmKSamplerNode ksampler = graph.GetNode<SwarmKSamplerNode>("10")!;
        Assert.Equal(1261999331L, ksampler.NoiseSeed.LiteralValue);
        Assert.Equal(20L, ksampler.Steps.LiteralValue);
        Assert.Equal("euler", ksampler.SamplerName.LiteralValue);

        SwarmSaveAnimationWSNode save = graph.GetNode<SwarmSaveAnimationWSNode>("53200")!;
        Assert.Equal("h264-mp4", save.Format.LiteralValue);
        Assert.Equal(false, save.Lossless.LiteralValue);
    }

    [Fact]
    public void Load_PreservesTypedConnections()
    {
        ComfyGraph graph = ComfyGraph.FromWorkflow(LoadFixture());

        // 3000.model ← 4.MODEL (lora chained on UNET)
        LoraLoaderModelOnlyNode lora = graph.GetNode<LoraLoaderModelOnlyNode>("3000")!;
        Assert.Same(graph.GetNode("4"), lora.Model.TypedConnection!.Node);
        Assert.Equal(0, lora.Model.TypedConnection!.SlotIndex);

        // 8.samples ← 10.LATENT (image VAEDecode of first SwarmKSampler)
        VAEDecodeNode imageDecode = graph.GetNode<VAEDecodeNode>("8")!;
        Assert.Same(graph.GetNode("10"), imageDecode.Samples.TypedConnection!.Node);

        // 121.samples ← 120.video_latent (slot 0 of LTXVSeparateAVLatent)
        VAEDecodeNode videoDecode = graph.GetNode<VAEDecodeNode>("121")!;
        Assert.Same(graph.GetNode("120"), videoDecode.Samples.TypedConnection!.Node);
        Assert.Equal(0, videoDecode.Samples.TypedConnection!.SlotIndex);

        // 122.samples ← 120.audio_latent (slot 1 of LTXVSeparateAVLatent)
        LTXVAudioVAEDecodeNode audioDecode = graph.GetNode<LTXVAudioVAEDecodeNode>("122")!;
        Assert.Same(graph.GetNode("120"), audioDecode.Samples.TypedConnection!.Node);
        Assert.Equal(1, audioDecode.Samples.TypedConnection!.SlotIndex);
    }

    [Fact]
    public void FindNearestUpstream_TraversesAcrossMultipleHops()
    {
        ComfyGraph graph = ComfyGraph.FromWorkflow(LoadFixture());

        SwarmSaveImageWSNode imageSave = graph.GetNode<SwarmSaveImageWSNode>("30")!;

        // 30 → 8 (VAEDecode) → 10 (SwarmKSampler) → 3000 (Lora) → 4 (UNETLoader)
        UNETLoaderNode? unet = graph.FindNearestUpstream<UNETLoaderNode>(imageSave);
        Assert.NotNull(unet);
        Assert.Equal("4", unet!.Id);

        SwarmKSamplerNode? sampler = graph.FindNearestUpstream<SwarmKSamplerNode>(imageSave);
        Assert.NotNull(sampler);
        Assert.Equal("10", sampler!.Id);

        // From the video save, the nearest SwarmKSampler is the last in the video chain (119),
        // not the first image-side one (10).
        SwarmSaveAnimationWSNode videoSave = graph.GetNode<SwarmSaveAnimationWSNode>("53200")!;
        SwarmKSamplerNode? videoSampler = graph.FindNearestUpstream<SwarmKSamplerNode>(videoSave);
        Assert.NotNull(videoSampler);
        Assert.Equal("119", videoSampler!.Id);
    }

    [Fact]
    public void NodesOfType_FindsAllInstances()
    {
        ComfyGraph graph = ComfyGraph.FromWorkflow(LoadFixture());

        IReadOnlyList<SwarmKSamplerNode> samplers = graph.NodesOfType<SwarmKSamplerNode>();
        Assert.Equal(3, samplers.Count);
        Assert.Equal(new[] { "10", "114", "119" }, samplers.Select(n => n.Id).OrderBy(s => s).ToArray());

        IReadOnlyList<LTXVSeparateAVLatentNode> separates = graph.NodesOfType<LTXVSeparateAVLatentNode>();
        Assert.Equal(2, separates.Count);
    }

    [Fact]
    public void RetargetConnections_ReroutesAllConsumersOfASingleOutput()
    {
        ComfyGraph graph = ComfyGraph.FromWorkflow(LoadFixture());

        // 120 LTXVSeparateAVLatent has two consumers — VAEDecode (121.samples ← slot 0)
        // and LTXVAudioVAEDecode (122.samples ← slot 1). Insert a new separate node
        // between sampler (119) and the consumers, simulating a splice operation.
        LTXVSeparateAVLatentNode oldSeparate = graph.GetNode<LTXVSeparateAVLatentNode>("120")!;
        SwarmKSamplerNode lastSampler = graph.GetNode<SwarmKSamplerNode>("119")!;
        LTXVSeparateAVLatentNode newSeparate = graph.AddNode(new LTXVSeparateAVLatentNode());
        newSeparate.AvLatent.ConnectTo(lastSampler.LATENT);

        int rewiredVideo = graph.RetargetConnections(oldSeparate.VideoLatent, newSeparate.VideoLatent);
        int rewiredAudio = graph.RetargetConnections(oldSeparate.AudioLatent, newSeparate.AudioLatent);

        Assert.Equal(1, rewiredVideo);
        Assert.Equal(1, rewiredAudio);

        VAEDecodeNode videoDecode = graph.GetNode<VAEDecodeNode>("121")!;
        LTXVAudioVAEDecodeNode audioDecode = graph.GetNode<LTXVAudioVAEDecodeNode>("122")!;
        Assert.Same(newSeparate, videoDecode.Samples.TypedConnection!.Node);
        Assert.Same(newSeparate, audioDecode.Samples.TypedConnection!.Node);
        Assert.Equal(0, videoDecode.Samples.TypedConnection!.SlotIndex);
        Assert.Equal(1, audioDecode.Samples.TypedConnection!.SlotIndex);
    }

    [Fact]
    public void RoundTrip_ProducesEquivalentWorkflow()
    {
        JObject original = LoadFixture();
        ComfyGraph graph = ComfyGraph.FromWorkflow(original);
        JObject rebuilt = graph.ToWorkflow();

        Assert.Equal(original.Count, rebuilt.Count);

        foreach (string id in original.Properties().Select(p => p.Name))
        {
            JObject src = (JObject)original[id]!;
            JObject dst = (JObject)rebuilt[id]!;
            Assert.Equal(src.Value<string>("class_type"), dst.Value<string>("class_type"));

            JObject? srcInputs = src["inputs"] as JObject;
            JObject? dstInputs = dst["inputs"] as JObject;
            if (srcInputs is null) continue;
            Assert.NotNull(dstInputs);

            foreach (JProperty p in srcInputs.Properties())
            {
                JToken? srcVal = p.Value;
                JToken? dstVal = dstInputs![p.Name];
                Assert.True(JToken.DeepEquals(srcVal, dstVal),
                    $"node {id} input '{p.Name}': expected {srcVal} got {dstVal}");
            }
        }
    }

    [Fact]
    public void WorkflowBridge_SyncReflectsTypedMutationInJObject()
    {
        JObject workflow = LoadFixture();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmKSamplerNode sampler = bridge.Graph.GetNode<SwarmKSamplerNode>("10")!;
        sampler.Steps.Set(40L);
        sampler.NoiseSeed.Set(7L);
        bridge.SyncNode(sampler);

        JObject jSampler = (JObject)workflow["10"]!;
        JObject jInputs = (JObject)jSampler["inputs"]!;
        Assert.Equal(40L, (long)jInputs["steps"]!);
        Assert.Equal(7L, (long)jInputs["noise_seed"]!);
        // Connections should still be intact
        Assert.Equal("3000", (string)((JArray)jInputs["model"]!)[0]!);
    }
}
