using ComfyTyped.Core;
using ComfyTyped.Generated;
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
}
