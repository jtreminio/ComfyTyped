using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.Types;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ComfyTyped.Tests;

public class NodeInputExtensionsTests
{
    public NodeInputExtensionsTests()
    {
        NodeRegistrations.EnsureRegistered();
    }

    [Fact]
    public void LiteralAsInt_AcceptsBoxedIntAndLong()
    {
        EmptyLatentImageNode node = new EmptyLatentImageNode();
        node.Width.Set(512);
        Assert.Equal(512, ((INodeInput)node.Width).LiteralAsInt());

        node.Width.Set(1024L);
        Assert.Equal(1024, ((INodeInput)node.Width).LiteralAsInt());
    }

    [Fact]
    public void LiteralAsLong_AcceptsBoxedIntAndLong()
    {
        EmptyLatentImageNode node = new EmptyLatentImageNode();
        node.Width.Set(512);
        Assert.Equal(512L, ((INodeInput)node.Width).LiteralAsLong());

        node.Width.Set(1024L);
        Assert.Equal(1024L, ((INodeInput)node.Width).LiteralAsLong());
    }

    [Fact]
    public void LiteralAsLong_RoundTripFromJObject_AcceptsLong()
    {
        // JSON integer values round-trip as long after FromWorkflow.
        JObject workflow = new()
        {
            ["1"] = new JObject
            {
                ["class_type"] = "EmptyLatentImage",
                ["inputs"] = new JObject { ["width"] = 768, ["height"] = 768, ["batch_size"] = 1 },
            },
        };
        ComfyGraph graph = ComfyGraph.FromWorkflow(workflow);
        EmptyLatentImageNode node = graph.GetNode<EmptyLatentImageNode>("1")!;

        Assert.Equal(768L, ((INodeInput)node.Width).LiteralAsLong());
        Assert.Equal(768, ((INodeInput)node.Width).LiteralAsInt());
    }

    [Fact]
    public void LiteralAsString_ReturnsStringOrNull()
    {
        CheckpointLoaderSimpleNode node = new CheckpointLoaderSimpleNode();
        node.CkptName.Set("model.safetensors");
        Assert.Equal("model.safetensors", ((INodeInput)node.CkptName).LiteralAsString());

        EmptyLatentImageNode emptyLatent = new EmptyLatentImageNode();
        emptyLatent.Width.Set(512);
        Assert.Null(((INodeInput)emptyLatent.Width).LiteralAsString());
    }

    [Fact]
    public void LiteralAsDouble_AcceptsAnyNumeric()
    {
        KSamplerNode node = new KSamplerNode();
        node.Cfg.Set(7.5);
        Assert.Equal(7.5, ((INodeInput)node.Cfg).LiteralAsDouble());

        node.Cfg.Set(7.5f);
        Assert.Equal(7.5, ((INodeInput)node.Cfg).LiteralAsDouble());

        node.Cfg.Set(7L);
        Assert.Equal(7.0, ((INodeInput)node.Cfg).LiteralAsDouble());

        node.Cfg.Set(7);
        Assert.Equal(7.0, ((INodeInput)node.Cfg).LiteralAsDouble());
    }

    [Fact]
    public void LiteralAs_ReturnNullWhenUnsetOrConnected()
    {
        ComfyGraph graph = new ComfyGraph();
        CheckpointLoaderSimpleNode ckpt = graph.AddNode(new CheckpointLoaderSimpleNode());
        KSamplerNode ksampler = graph.AddNode(new KSamplerNode());

        Assert.Null(((INodeInput)ksampler.Model).LiteralAsString());
        Assert.Null(((INodeInput)ksampler.Positive).LiteralAsString());

        ksampler.Model.ConnectTo(ckpt.MODEL);
        Assert.Null(((INodeInput)ksampler.Model).LiteralAsString());
    }

    [Fact]
    public void ConnectFromPath_WiresInputAndAutoSyncsJObject()
    {
        WorkflowBridge bridge = WorkflowBridge.Create(new JObject());
        CheckpointLoaderSimpleNode ckpt = bridge.AddNode(new CheckpointLoaderSimpleNode());
        VAEDecodeNode decode = bridge.AddNode(new VAEDecodeNode());
        EmptyLatentImageNode latent = bridge.AddNode(new EmptyLatentImageNode());

        decode.Vae.ConnectFromPath(bridge, new JArray(ckpt.Id, 2));
        decode.Samples.ConnectFromPath(bridge, new JArray(latent.Id, 0));

        Assert.True(decode.Vae.IsConnected);
        Assert.Equal(ckpt.Id, decode.Vae.Connection!.Node.Id);
        Assert.Equal(2, decode.Vae.Connection.SlotIndex);

        JArray vaeRef = (JArray)bridge.Workflow[decode.Id]!["inputs"]!["vae"]!;
        Assert.Equal(ckpt.Id, (string)vaeRef[0]!);
        Assert.Equal(2, (int)vaeRef[1]!);
    }

    [Fact]
    public void ConnectFromPath_NullPath_ThrowsWithSlotContext()
    {
        WorkflowBridge bridge = WorkflowBridge.Create(new JObject());
        VAEDecodeNode decode = bridge.AddNode(new VAEDecodeNode());

        JArray? nope = null;
        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => decode.Vae.ConnectFromPath(bridge, nope));

        Assert.Contains("nope", ex.Message);
        Assert.Contains("vae", ex.Message);
        Assert.Contains("VAEDecodeNode", ex.Message);
        Assert.Contains("VAE", ex.Message);
    }

    [Fact]
    public void ConnectFromPath_UnresolvedPath_Throws()
    {
        WorkflowBridge bridge = WorkflowBridge.Create(new JObject());
        VAEDecodeNode decode = bridge.AddNode(new VAEDecodeNode());

        Assert.Throws<ArgumentException>(
            () => decode.Vae.ConnectFromPath(bridge, new JArray("nonexistent", 0)));
        Assert.False(decode.Vae.IsConnected);
    }

    [Fact]
    public void ConnectFromPath_TypeMismatch_ThrowsFromConnectToUntyped()
    {
        WorkflowBridge bridge = WorkflowBridge.Create(new JObject());
        CheckpointLoaderSimpleNode ckpt = bridge.AddNode(new CheckpointLoaderSimpleNode());
        VAEDecodeNode decode = bridge.AddNode(new VAEDecodeNode());

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => decode.Vae.ConnectFromPath(bridge, new JArray(ckpt.Id, 0)));

        Assert.Contains("MODEL", ex.Message);
        Assert.Contains("VAE", ex.Message);
    }

    [Fact]
    public void ConnectFromPath_WildcardOutput_AllowedThroughConnectToUntyped()
    {
        JObject workflow = new()
        {
            ["50"] = new JObject
            {
                ["class_type"] = "SomeUnregisteredCustomNode",
                ["inputs"] = new JObject(),
            },
        };
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        VAEDecodeNode decode = bridge.AddNode(new VAEDecodeNode());

        decode.Vae.ConnectFromPath(bridge, new JArray("50", 0));

        Assert.True(decode.Vae.IsConnected);
        Assert.Equal("50", decode.Vae.Connection!.Node.Id);
    }

    [Fact]
    public void TryConnectFromPath_NullOrUnresolved_ReturnsFalseAndIsNoOp()
    {
        WorkflowBridge bridge = WorkflowBridge.Create(new JObject());
        CheckpointLoaderSimpleNode ckpt = bridge.AddNode(new CheckpointLoaderSimpleNode());
        VAEDecodeNode decode = bridge.AddNode(new VAEDecodeNode());

        Assert.False(decode.Vae.TryConnectFromPath(bridge, null));
        Assert.False(decode.Vae.IsConnected);

        Assert.False(decode.Vae.TryConnectFromPath(bridge, new JArray("nonexistent", 0)));
        Assert.False(decode.Vae.IsConnected);

        Assert.True(decode.Vae.TryConnectFromPath(bridge, new JArray(ckpt.Id, 2)));
        Assert.True(decode.Vae.IsConnected);

        Assert.False(decode.Vae.TryConnectFromPath(bridge, null));
        Assert.True(decode.Vae.IsConnected);
    }

    [Fact]
    public void TryConnectFromPath_TypeMismatch_StillThrows()
    {
        WorkflowBridge bridge = WorkflowBridge.Create(new JObject());
        CheckpointLoaderSimpleNode ckpt = bridge.AddNode(new CheckpointLoaderSimpleNode());
        VAEDecodeNode decode = bridge.AddNode(new VAEDecodeNode());

        Assert.Throws<InvalidOperationException>(
            () => decode.Vae.TryConnectFromPath(bridge, new JArray(ckpt.Id, 0)));
    }
}
