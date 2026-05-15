using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.Types;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ComfyTyped.Tests;

public class InputListTests
{
    public InputListTests()
    {
        NodeRegistrations.EnsureRegistered();
    }

    // ── Round-trip ───────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_BatchImagesNode_PreservesAllChildKeys()
    {
        JObject workflow = MakeBatchImagesFixture("10", "11", "12");

        ComfyGraph graph = ComfyGraph.FromWorkflow(workflow);
        BatchImagesNodeNode node = graph.GetNode<BatchImagesNodeNode>("20")!;
        Assert.Equal(3, node.Images.Count);
        Assert.True(node.Images[0].IsConnected);
        Assert.Equal("10", node.Images[0].Connection!.Node.Id);

        JObject roundTripped = graph.ToWorkflow();
        JObject inputs = (JObject)roundTripped["20"]!["inputs"]!;
        Assert.Equal(3, inputs.Properties().Count());
        Assert.Equal("10", (string)((JArray)inputs["images.image0"]!)[0]!);
        Assert.Equal("11", (string)((JArray)inputs["images.image1"]!)[0]!);
        Assert.Equal("12", (string)((JArray)inputs["images.image2"]!)[0]!);

        // Crucially: keys did NOT land in ExtraInputs — that escape hatch is reserved
        // for shapes the typed list can't model.
        Assert.Empty(node.ExtraInputs.Properties());
    }

    [Fact]
    public void Deserialize_SparseIndicesCompactToContiguous()
    {
        // Keys 0, 2, 5 arrive — should compact to 0, 1, 2 (ComfyUI runtime treats autogrow as positional).
        JObject workflow = new()
        {
            ["10"] = ImageStub(),
            ["20"] = new JObject
            {
                ["class_type"] = "BatchImagesNode",
                ["inputs"] = new JObject
                {
                    ["images.image5"] = new JArray("10", 0),
                    ["images.image0"] = new JArray("10", 1),
                    ["images.image2"] = new JArray("10", 2),
                },
            },
        };
        ComfyGraph graph = ComfyGraph.FromWorkflow(workflow);
        BatchImagesNodeNode node = graph.GetNode<BatchImagesNodeNode>("20")!;
        Assert.Equal(3, node.Images.Count);

        JObject inputs = (JObject)graph.ToWorkflow()["20"]!["inputs"]!;
        // Re-serialized as contiguous 0..2; the sort order followed the SortedDictionary key (0, 2, 5).
        Assert.Equal(1, (int)((JArray)inputs["images.image0"]!)[1]!); // was image0
        Assert.Equal(2, (int)((JArray)inputs["images.image1"]!)[1]!); // was image2
        Assert.Equal(0, (int)((JArray)inputs["images.image2"]!)[1]!); // was image5
        Assert.Null(inputs["images.image5"]);
    }

    [Fact]
    public void Deserialize_NonClaimingKeyUnderListPrefixGoesToExtraInputs()
    {
        // A literal under the list's prefix (not a connection ref) falls through to ExtraInputs —
        // autogrow children are connection-only by schema, so a literal here is malformed JSON
        // that should round-trip via the escape hatch rather than be claimed.
        JObject workflow = new()
        {
            ["20"] = new JObject
            {
                ["class_type"] = "BatchImagesNode",
                ["inputs"] = new JObject
                {
                    ["images.image0"] = "not_a_connection_ref",
                },
            },
        };
        ComfyGraph graph = ComfyGraph.FromWorkflow(workflow);
        BatchImagesNodeNode node = graph.GetNode<BatchImagesNodeNode>("20")!;
        Assert.Equal(0, node.Images.Count);
        Assert.Equal("not_a_connection_ref", (string?)node.ExtraInputs["images.image0"]);
    }

    // ── Structural mutations via bridge ──────────────────────────────

    [Fact]
    public void Bridge_AddAppendsKeyAndAutoSyncs()
    {
        JObject workflow = new()
        {
            ["10"] = ImageStub(),
            ["20"] = EmptyBatchImagesStub(),
        };
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        BatchImagesNodeNode node = bridge.Graph.GetNode<BatchImagesNodeNode>("20")!;
        UnknownNode source = bridge.Graph.GetNode<UnknownNode>("10")!;

        node.Images.AddFromUntyped(source.GetOutput(0));
        node.Images.AddFromUntyped(source.GetOutput(0));

        JObject inputs = (JObject)bridge.Workflow["20"]!["inputs"]!;
        Assert.Equal("10", (string)((JArray)inputs["images.image0"]!)[0]!);
        Assert.Equal("10", (string)((JArray)inputs["images.image1"]!)[0]!);
    }

    [Fact]
    public void Bridge_AppendBatchAutoSyncs()
    {
        JObject workflow = new()
        {
            ["10"] = ImageStub(),
            ["11"] = ImageStub(),
            ["12"] = ImageStub(),
            ["20"] = EmptyBatchImagesStub(),
        };
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        BatchImagesNodeNode node = bridge.Graph.GetNode<BatchImagesNodeNode>("20")!;
        foreach (string id in new[] { "10", "11", "12" })
        {
            node.Images.AddFromUntyped(bridge.Graph.GetNode<UnknownNode>(id)!.GetOutput(0));
        }
        JObject inputs = (JObject)bridge.Workflow["20"]!["inputs"]!;
        Assert.Equal(3, inputs.Properties().Count());
        Assert.Equal("10", (string)((JArray)inputs["images.image0"]!)[0]!);
        Assert.Equal("11", (string)((JArray)inputs["images.image1"]!)[0]!);
        Assert.Equal("12", (string)((JArray)inputs["images.image2"]!)[0]!);
    }

    [Fact]
    public void Bridge_RemoveAtRenumbersTailAndRewritesKeyset()
    {
        JObject workflow = MakeBatchImagesFixture("10", "11", "12", "13");

        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        BatchImagesNodeNode node = bridge.Graph.GetNode<BatchImagesNodeNode>("20")!;
        Assert.Equal(4, node.Images.Count);

        node.Images.RemoveAt(1); // drop the item sourced from node "11"

        JObject inputs = (JObject)bridge.Workflow["20"]!["inputs"]!;
        Assert.Equal(3, inputs.Properties().Count());
        Assert.Equal(3, node.Images.Count);
        Assert.Equal("images.image0", node.Images[0].Name);
        Assert.Equal("images.image1", node.Images[1].Name);
        Assert.Equal("images.image2", node.Images[2].Name);
        // Surviving items are sourced from nodes 10, 12, 13 (the middle 11 dropped).
        Assert.Equal("10", (string)((JArray)inputs["images.image0"]!)[0]!);
        Assert.Equal("12", (string)((JArray)inputs["images.image1"]!)[0]!);
        Assert.Equal("13", (string)((JArray)inputs["images.image2"]!)[0]!);
    }

    [Fact]
    public void Bridge_ClearRemovesAllKeys()
    {
        JObject workflow = MakeBatchImagesFixture("10", "11");
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        BatchImagesNodeNode node = bridge.Graph.GetNode<BatchImagesNodeNode>("20")!;

        node.Images.Clear();

        JObject inputs = (JObject)bridge.Workflow["20"]!["inputs"]!;
        Assert.Empty(inputs.Properties());
        Assert.Equal(0, node.Images.Count);
    }

    // ── Constraints ──────────────────────────────────────────────────

    [Fact]
    public void Max_ThrowsOnOverflow()
    {
        BatchImagesNodeNode node = new();
        // BatchImagesNode max = 50; produce a wildcard source and saturate.
        UnknownNode source = new("Dummy");
        for (int i = 0; i < 50; i++)
        {
            node.Images.AddFromUntyped(source.GetOutput(0));
        }
        Assert.Equal(50, node.Images.Count);
        Assert.Throws<InvalidOperationException>(() =>
            node.Images.AddFromUntyped(source.GetOutput(0)));
    }

    [Fact]
    public void ListChild_SetThrows()
    {
        BatchImagesNodeNode node = new();
        node.Images.AddFromUntyped(new UnknownNode("Dummy").GetOutput(0));
        Assert.Throws<InvalidOperationException>(() => node.Images[0].SetUntyped("literal_not_allowed"));
    }

    [Fact]
    public void ListChild_ClearThrows()
    {
        BatchImagesNodeNode node = new();
        node.Images.AddFromUntyped(new UnknownNode("Dummy").GetOutput(0));
        Assert.Throws<InvalidOperationException>(() => node.Images[0].Clear());
    }

    [Fact]
    public void TypeMismatch_ConnectingNonImageThrows()
    {
        BatchImagesNodeNode node = new();
        // A non-wildcard, non-Image output should be rejected at runtime.
        EmptyLatentImageNode latentSrc = new();
        Assert.Throws<InvalidOperationException>(() =>
            node.Images.AddFromUntyped(latentSrc.LATENT));
    }

    [Fact]
    public void TryParseKey_RecognizesOnlyOwnPattern()
    {
        BatchImagesNodeNode node = new();
        INodeInputList list = node.Images;
        Assert.Equal(0, list.TryParseKey("images.image0"));
        Assert.Equal(42, list.TryParseKey("images.image42"));
        Assert.Equal(-1, list.TryParseKey("images.image"));            // no index
        Assert.Equal(-1, list.TryParseKey("images.imageX"));            // non-numeric
        Assert.Equal(-1, list.TryParseKey("images.images0"));           // wrong prefix
        Assert.Equal(-1, list.TryParseKey("other_input"));              // unrelated key
        Assert.Equal(-1, list.TryParseKey("images.image-1"));           // negative
    }

    // ── Fixture helpers ──────────────────────────────────────────────

    /// <summary>Build a workflow with one BatchImagesNode (id "20") wiring its list to N
    /// distinct image-stub source nodes (one node per <paramref name="sourceIds"/> entry).
    /// Each source node emits IMAGE on slot 0; the list child at index <c>i</c> connects
    /// to source <c>sourceIds[i]</c>'s slot 0.</summary>
    private static JObject MakeBatchImagesFixture(params string[] sourceIds)
    {
        JObject workflow = [];
        JObject inputs = [];
        for (int i = 0; i < sourceIds.Length; i++)
        {
            workflow[sourceIds[i]] = ImageStub();
            inputs[$"images.image{i}"] = new JArray(sourceIds[i], 0);
        }
        workflow["20"] = new JObject
        {
            ["class_type"] = "BatchImagesNode",
            ["inputs"] = inputs,
        };

        return workflow;
    }

    /// <summary>Stub source producing IMAGE on output 0..N. Uses UnknownNode so we don't
    /// need a real Image-producing typed node — UnknownNode outputs are AnyType wildcards
    /// which connect to NodeInputList&lt;ImageType&gt; via the same wildcard rules as singular slots.</summary>
    private static JObject ImageStub() => new()
    {
        ["class_type"] = "UnitTest_ImageSource",
        ["inputs"] = new JObject(),
    };

    private static JObject EmptyBatchImagesStub() => new()
    {
        ["class_type"] = "BatchImagesNode",
        ["inputs"] = new JObject(),
    };
}
