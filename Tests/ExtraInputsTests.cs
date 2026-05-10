using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ComfyTyped.Tests;

public class ExtraInputsTests
{
    public ExtraInputsTests()
    {
        NodeRegistrations.EnsureRegistered();
    }

    [Fact]
    public void RoundTrip_PreservesUnknownInputKeysOnTypedNode()
    {
        JObject workflow = new()
        {
            ["10"] = new JObject
            {
                ["class_type"] = "ResizeImageMaskNode",
                ["inputs"] = new JObject
                {
                    ["resize_type"] = "pixels",
                    ["scale_method"] = "area",
                    ["resize_type.multiple"] = 16,
                    ["resize_type.shorter_size"] = 512,
                },
            },
        };

        ComfyGraph graph = ComfyGraph.FromWorkflow(workflow);
        ResizeImageMaskNodeNode node = graph.GetNode<ResizeImageMaskNodeNode>("10")!;

        Assert.Equal(16, (int)node.ExtraInputs["resize_type.multiple"]!);
        Assert.Equal(512, (int)node.ExtraInputs["resize_type.shorter_size"]!);

        JObject roundTripped = graph.ToWorkflow();
        JObject inputs = (JObject)roundTripped["10"]!["inputs"]!;

        Assert.Equal("pixels", (string?)inputs["resize_type"]);
        Assert.Equal("area", (string?)inputs["scale_method"]);
        Assert.Equal(16, (int)inputs["resize_type.multiple"]!);
        Assert.Equal(512, (int)inputs["resize_type.shorter_size"]!);
    }

    [Fact]
    public void RoundTrip_PreservesListStyleConnectionKeys()
    {
        JObject workflow = new()
        {
            ["1"] = new JObject
            {
                ["class_type"] = "CheckpointLoaderSimple",
                ["inputs"] = new JObject { ["ckpt_name"] = "model.safetensors" },
            },
            ["20"] = new JObject
            {
                ["class_type"] = "BatchImagesNode",
                ["inputs"] = new JObject
                {
                    ["images.image0"] = new JArray("1", 0),
                    ["images.image1"] = new JArray("1", 1),
                },
            },
        };

        ComfyGraph graph = ComfyGraph.FromWorkflow(workflow);
        JObject roundTripped = graph.ToWorkflow();
        JObject inputs = (JObject)roundTripped["20"]!["inputs"]!;

        JArray ref0 = (JArray)inputs["images.image0"]!;
        Assert.Equal("1", (string)ref0[0]!);
        Assert.Equal(0, (int)ref0[1]!);
        JArray ref1 = (JArray)inputs["images.image1"]!;
        Assert.Equal(1, (int)ref1[1]!);
    }

    [Fact]
    public void TypedInputWinsOverExtraInputOnCollision()
    {
        EmptyLatentImageNode node = new EmptyLatentImageNode();
        node.Width.Set(1024L);
        node.ExtraInputs = new JObject { ["width"] = 999L, ["custom_extra"] = "hello" };

        JObject serialized = node.ToWorkflowNode();
        JObject inputs = (JObject)serialized["inputs"]!;

        Assert.Equal(1024L, (long)inputs["width"]!);
        Assert.Equal("hello", (string?)inputs["custom_extra"]);
    }

    [Fact]
    public void ExtraInputs_NotPopulatedForFullyTypedNode()
    {
        JObject workflow = new()
        {
            ["1"] = new JObject
            {
                ["class_type"] = "EmptyLatentImage",
                ["inputs"] = new JObject { ["width"] = 512, ["height"] = 512, ["batch_size"] = 1 },
            },
        };
        ComfyGraph graph = ComfyGraph.FromWorkflow(workflow);
        EmptyLatentImageNode node = graph.GetNode<EmptyLatentImageNode>("1")!;

        Assert.Empty(node.ExtraInputs.Properties());
    }

    [Fact]
    public void Bridge_SyncNode_PreservesExtras()
    {
        JObject workflow = new()
        {
            ["1"] = new JObject
            {
                ["class_type"] = "EmptyLatentImage",
                ["inputs"] = new JObject
                {
                    ["width"] = 512,
                    ["height"] = 512,
                    ["batch_size"] = 1,
                    ["custom_extra"] = "preserved",
                },
            },
        };
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        EmptyLatentImageNode node = bridge.Graph.GetNode<EmptyLatentImageNode>("1")!;

        node.Width.Set(2048L);
        bridge.SyncNode(node);

        JObject inputs = (JObject)bridge.Workflow["1"]!["inputs"]!;
        Assert.Equal(2048L, (long)inputs["width"]!);
        Assert.Equal("preserved", (string?)inputs["custom_extra"]);
    }
}
