using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ComfyTyped.Tests;

/// <summary>
/// Coverage for <see cref="NodeInput{T}.ConnectSameAs"/> /
/// <see cref="NodeInput{T}.TryConnectSameAs"/> — the typed slot-to-slot rewire helpers that wire
/// this input to the same upstream output that feeds another same-typed input. The point of the
/// API is that source and target are statically the same <c>T</c>, so it never has to drop through
/// <c>INodeOutput</c> at the call site (which is what forces <c>ConnectToUntyped</c> when you read
/// <c>.Connection</c> off a same-typed sibling).
/// </summary>
public class ConnectSameAsTests
{
    public ConnectSameAsTests()
    {
        NodeRegistrations.EnsureRegistered();
    }

    [Fact]
    public void ConnectSameAs_TypedWire_StaysTyped()
    {
        ComfyGraph graph = new ComfyGraph();
        CheckpointLoaderSimpleNode ckpt = graph.AddNode(new CheckpointLoaderSimpleNode());
        KSamplerNode source = graph.AddNode(new KSamplerNode());
        KSamplerNode target = graph.AddNode(new KSamplerNode());

        source.Model.ConnectTo(ckpt.MODEL);
        target.Model.ConnectSameAs(source.Model);

        Assert.True(target.Model.IsConnected);
        Assert.Same(ckpt.MODEL, target.Model.TypedConnection);
        Assert.Equal(ckpt.Id, target.Model.Connection!.Node.Id);
    }

    [Fact]
    public void ConnectSameAs_UntypedWildcardWire_StaysUntyped()
    {
        // An UnknownNode output (AnyType wildcard) connected into a concrete-typed input lands as
        // an untyped connection. Mirroring it into another same-typed slot must preserve that —
        // not silently drop it the way ConnectTo(source.TypedConnection) would.
        JObject workflow = new()
        {
            ["50"] = new JObject
            {
                ["class_type"] = "SomeUnregisteredCustomNode",
                ["inputs"] = new JObject(),
            },
        };
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        VAEDecodeNode source = bridge.AddNode(new VAEDecodeNode());
        VAEDecodeNode target = bridge.AddNode(new VAEDecodeNode());

        source.Vae.ConnectFromPath(bridge, new JArray("50", 0));
        Assert.True(source.Vae.IsConnected);
        Assert.Null(source.Vae.TypedConnection); // it's untyped

        target.Vae.ConnectSameAs(source.Vae);

        Assert.True(target.Vae.IsConnected);
        Assert.Null(target.Vae.TypedConnection);
        Assert.Equal("50", target.Vae.Connection!.Node.Id);
    }

    [Fact]
    public void ConnectSameAs_OverwritesExistingTargetState()
    {
        ComfyGraph graph = new ComfyGraph();
        CheckpointLoaderSimpleNode a = graph.AddNode(new CheckpointLoaderSimpleNode());
        CheckpointLoaderSimpleNode b = graph.AddNode(new CheckpointLoaderSimpleNode());
        KSamplerNode source = graph.AddNode(new KSamplerNode());
        KSamplerNode target = graph.AddNode(new KSamplerNode());

        source.Model.ConnectTo(a.MODEL);
        target.Model.ConnectTo(b.MODEL);

        target.Model.ConnectSameAs(source.Model);

        Assert.Same(a.MODEL, target.Model.TypedConnection);
    }

    [Fact]
    public void ConnectSameAs_UnsetSource_Throws()
    {
        ComfyGraph graph = new ComfyGraph();
        KSamplerNode source = graph.AddNode(new KSamplerNode());
        KSamplerNode target = graph.AddNode(new KSamplerNode());

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => target.Model.ConnectSameAs(source.Model));

        Assert.Contains("model", ex.Message);
        Assert.Contains("KSamplerNode", ex.Message);
    }

    [Fact]
    public void ConnectSameAs_LiteralSource_Throws()
    {
        // Seed is a literal-bearing INT slot — it has a value but no connection to match.
        KSamplerNode source = new KSamplerNode();
        KSamplerNode target = new KSamplerNode();
        source.Seed.Set(42);

        Assert.Throws<InvalidOperationException>(() => target.Seed.ConnectSameAs(source.Seed));
    }

    [Fact]
    public void TryConnectSameAs_UnsetOrLiteralSource_ReturnsFalseNoOp()
    {
        ComfyGraph graph = new ComfyGraph();
        CheckpointLoaderSimpleNode ckpt = graph.AddNode(new CheckpointLoaderSimpleNode());
        KSamplerNode source = graph.AddNode(new KSamplerNode());
        KSamplerNode target = graph.AddNode(new KSamplerNode());

        // Pre-existing target wire must survive a false (no-op) call.
        target.Model.ConnectTo(ckpt.MODEL);

        Assert.False(target.Model.TryConnectSameAs(source.Model));
        Assert.Same(ckpt.MODEL, target.Model.TypedConnection);
    }

    [Fact]
    public void TryConnectSameAs_ConnectedSource_WiresAndReturnsTrue()
    {
        ComfyGraph graph = new ComfyGraph();
        CheckpointLoaderSimpleNode ckpt = graph.AddNode(new CheckpointLoaderSimpleNode());
        KSamplerNode source = graph.AddNode(new KSamplerNode());
        KSamplerNode target = graph.AddNode(new KSamplerNode());

        source.Model.ConnectTo(ckpt.MODEL);

        Assert.True(target.Model.TryConnectSameAs(source.Model));
        Assert.Same(ckpt.MODEL, target.Model.TypedConnection);
    }
}
