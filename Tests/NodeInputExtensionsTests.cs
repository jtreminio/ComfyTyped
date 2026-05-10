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
        var node = new EmptyLatentImageNode();
        node.Width.Set(512);
        Assert.Equal(512, ((INodeInput)node.Width).LiteralAsInt());

        node.Width.Set(1024L);
        Assert.Equal(1024, ((INodeInput)node.Width).LiteralAsInt());
    }

    [Fact]
    public void LiteralAsLong_AcceptsBoxedIntAndLong()
    {
        var node = new EmptyLatentImageNode();
        node.Width.Set(512);
        Assert.Equal(512L, ((INodeInput)node.Width).LiteralAsLong());

        node.Width.Set(1024L);
        Assert.Equal(1024L, ((INodeInput)node.Width).LiteralAsLong());
    }

    [Fact]
    public void LiteralAsLong_RoundTripFromJObject_AcceptsLong()
    {
        // Newtonsoft normalizes integer JSON to long — verify the helper handles
        // values that come back through ComfyGraph.FromWorkflow.
        JObject workflow = new()
        {
            ["1"] = new JObject
            {
                ["class_type"] = "EmptyLatentImage",
                ["inputs"] = new JObject { ["width"] = 768, ["height"] = 768, ["batch_size"] = 1 },
            },
        };
        var graph = ComfyGraph.FromWorkflow(workflow);
        var node = graph.GetNode<EmptyLatentImageNode>("1")!;

        Assert.Equal(768L, ((INodeInput)node.Width).LiteralAsLong());
        Assert.Equal(768, ((INodeInput)node.Width).LiteralAsInt());
    }

    [Fact]
    public void LiteralAsString_ReturnsStringOrNull()
    {
        var node = new CheckpointLoaderSimpleNode();
        node.CkptName.Set("model.safetensors");
        Assert.Equal("model.safetensors", ((INodeInput)node.CkptName).LiteralAsString());

        var emptyLatent = new EmptyLatentImageNode();
        emptyLatent.Width.Set(512);
        Assert.Null(((INodeInput)emptyLatent.Width).LiteralAsString());
    }

    [Fact]
    public void LiteralAsDouble_AcceptsAnyNumeric()
    {
        var node = new KSamplerNode();
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
        var graph = new ComfyGraph();
        var ckpt = graph.AddNode(new CheckpointLoaderSimpleNode());
        var ksampler = graph.AddNode(new KSamplerNode());

        // Unset (Model is connection-only with no constructor default).
        Assert.Null(((INodeInput)ksampler.Model).LiteralAsString());
        Assert.Null(((INodeInput)ksampler.Positive).LiteralAsString());

        // Once connected, literal helpers still return null.
        ksampler.Model.ConnectTo(ckpt.MODEL);
        Assert.Null(((INodeInput)ksampler.Model).LiteralAsString());
    }

    // ── ConnectFromPath / TryConnectFromPath ────────────────────────

    [Fact]
    public void ConnectFromPath_WiresInputAndAutoSyncsJObject()
    {
        var bridge = WorkflowBridge.Create(new JObject());
        var ckpt = bridge.AddNode(new CheckpointLoaderSimpleNode());
        var decode = bridge.AddNode(new VAEDecodeNode());
        var latent = bridge.AddNode(new EmptyLatentImageNode());

        // CheckpointLoaderSimple slot 2 is VAE.
        decode.Vae.ConnectFromPath(bridge, new JArray(ckpt.Id, 2));
        decode.Samples.ConnectFromPath(bridge, new JArray(latent.Id, 0));

        Assert.True(decode.Vae.IsConnected);
        Assert.Equal(ckpt.Id, decode.Vae.Connection!.Node.Id);
        Assert.Equal(2, decode.Vae.Connection.SlotIndex);

        // Auto-sync mirrors into JObject.
        JArray vaeRef = (JArray)bridge.Workflow[decode.Id]!["inputs"]!["vae"]!;
        Assert.Equal(ckpt.Id, (string)vaeRef[0]!);
        Assert.Equal(2, (int)vaeRef[1]!);
    }

    [Fact]
    public void ConnectFromPath_NullPath_ThrowsWithSlotContext()
    {
        var bridge = WorkflowBridge.Create(new JObject());
        var decode = bridge.AddNode(new VAEDecodeNode());

        JArray? nope = null;
        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => decode.Vae.ConnectFromPath(bridge, nope));

        // Diagnostic surfaces the source expression, slot name, owning node, and expected type.
        Assert.Contains("nope", ex.Message);
        Assert.Contains("vae", ex.Message);
        Assert.Contains("VAEDecodeNode", ex.Message);
        Assert.Contains("VAE", ex.Message);
    }

    [Fact]
    public void ConnectFromPath_UnresolvedPath_Throws()
    {
        var bridge = WorkflowBridge.Create(new JObject());
        var decode = bridge.AddNode(new VAEDecodeNode());

        Assert.Throws<ArgumentException>(
            () => decode.Vae.ConnectFromPath(bridge, new JArray("nonexistent", 0)));
        Assert.False(decode.Vae.IsConnected);
    }

    [Fact]
    public void ConnectFromPath_TypeMismatch_ThrowsFromConnectToUntyped()
    {
        var bridge = WorkflowBridge.Create(new JObject());
        var ckpt = bridge.AddNode(new CheckpointLoaderSimpleNode());
        var decode = bridge.AddNode(new VAEDecodeNode());

        // Slot 0 is MODEL, not VAE — falls through to ConnectToUntyped's diagnostic.
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => decode.Vae.ConnectFromPath(bridge, new JArray(ckpt.Id, 0)));

        Assert.Contains("MODEL", ex.Message);
        Assert.Contains("VAE", ex.Message);
    }

    [Fact]
    public void ConnectFromPath_WildcardOutput_AllowedThroughConnectToUntyped()
    {
        // UnknownNode outputs are NodeOutput<AnyType>. ConnectFromPath must permit this even
        // though ResolvePath<T> would reject it — wildcards flow through ConnectToUntyped's
        // existing acceptance path.
        JObject workflow = new()
        {
            ["50"] = new JObject
            {
                ["class_type"] = "SomeUnregisteredCustomNode",
                ["inputs"] = new JObject(),
            },
        };
        var bridge = WorkflowBridge.Create(workflow);
        var decode = bridge.AddNode(new VAEDecodeNode());

        decode.Vae.ConnectFromPath(bridge, new JArray("50", 0));

        Assert.True(decode.Vae.IsConnected);
        Assert.Equal("50", decode.Vae.Connection!.Node.Id);
    }

    [Fact]
    public void TryConnectFromPath_NullOrUnresolved_ReturnsFalseAndIsNoOp()
    {
        var bridge = WorkflowBridge.Create(new JObject());
        var ckpt = bridge.AddNode(new CheckpointLoaderSimpleNode());
        var decode = bridge.AddNode(new VAEDecodeNode());

        Assert.False(decode.Vae.TryConnectFromPath(bridge, null));
        Assert.False(decode.Vae.IsConnected);

        Assert.False(decode.Vae.TryConnectFromPath(bridge, new JArray("nonexistent", 0)));
        Assert.False(decode.Vae.IsConnected);

        // Real path: returns true and connects.
        Assert.True(decode.Vae.TryConnectFromPath(bridge, new JArray(ckpt.Id, 2)));
        Assert.True(decode.Vae.IsConnected);

        // Subsequent null is a no-op — must not clear the existing connection.
        Assert.False(decode.Vae.TryConnectFromPath(bridge, null));
        Assert.True(decode.Vae.IsConnected);
    }

    [Fact]
    public void TryConnectFromPath_TypeMismatch_StillThrows()
    {
        // Mirrors TryConnectToUntyped: null tolerance is the only soft failure.
        var bridge = WorkflowBridge.Create(new JObject());
        var ckpt = bridge.AddNode(new CheckpointLoaderSimpleNode());
        var decode = bridge.AddNode(new VAEDecodeNode());

        Assert.Throws<InvalidOperationException>(
            () => decode.Vae.TryConnectFromPath(bridge, new JArray(ckpt.Id, 0)));
    }
}
