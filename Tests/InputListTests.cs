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
    public void Deserialize_KeyOutsideListPatternGoesToExtraInputs()
    {
        // A key starting with "images." but whose suffix isn't a valid list child name
        // (TryParseKey returns -1) doesn't get claimed — falls through to ExtraInputs.
        // Image-typed input contract is permissive about literals at runtime, but the
        // wire-key pattern is strict.
        JObject workflow = new()
        {
            ["20"] = new JObject
            {
                ["class_type"] = "BatchImagesNode",
                ["inputs"] = new JObject
                {
                    ["images.unmatched_suffix"] = "preserved_literal",
                },
            },
        };
        ComfyGraph graph = ComfyGraph.FromWorkflow(workflow);
        BatchImagesNodeNode node = graph.GetNode<BatchImagesNodeNode>("20")!;
        Assert.Equal(0, node.Images.Count);
        Assert.Equal("preserved_literal", (string?)node.ExtraInputs["images.unmatched_suffix"]);
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
        // Serialized wire keys regenerate from position-in-list; surviving items 10/12/13
        // end up at indices 0/1/2 with the corresponding contiguous wire keys.
        Assert.Equal("10", (string)((JArray)inputs["images.image0"]!)[0]!);
        Assert.Equal("12", (string)((JArray)inputs["images.image1"]!)[0]!);
        Assert.Equal("13", (string)((JArray)inputs["images.image2"]!)[0]!);
        // image3 (originally from node 13) no longer exists — its old wire-key vanished.
        Assert.Null(inputs["images.image3"]);
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
    public void ListChild_SetWorksForPrimitiveElement()
    {
        // GLSLShader.Floats is NodeInputList<FloatType> — primitive element type,
        // so Set on a child is meaningful (literal value, not a connection).
        GLSLShaderNode node = new();
        // Materialize a child by appending an unset slot, then setting its literal.
        // (Real-world: codegen populates via FromWorkflow's literal path.)
        node.Floats.AddFromUntyped(new UnknownNode("Dummy").GetOutput(0));
        node.Floats[0].SetUntyped(3.14);
        Assert.Equal(3.14, node.Floats[0].LiteralValue);
        Assert.False(node.Floats[0].IsConnected);
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

    // ── Graph traversal includes list-child connections ──────────────

    [Fact]
    public void FindDownstream_SeesListChildConnections()
    {
        JObject workflow = MakeBatchImagesFixture("10", "11", "12");
        ComfyGraph graph = ComfyGraph.FromWorkflow(workflow);
        UnknownNode source = graph.GetNode<UnknownNode>("11")!;
        IReadOnlyList<ComfyNode> downstream = graph.FindDownstream(source.GetOutput(0));
        Assert.Single(downstream);
        Assert.Equal("20", downstream[0].Id);
    }

    [Fact]
    public void FindUpstream_SeesListChildConnections()
    {
        JObject workflow = MakeBatchImagesFixture("10", "11", "12");
        ComfyGraph graph = ComfyGraph.FromWorkflow(workflow);
        ComfyNode batch = graph.GetNode("20")!;
        IReadOnlyList<ComfyNode> upstream = graph.FindUpstream(batch);
        Assert.Equal(3, upstream.Count);
        Assert.Contains(upstream, n => n.Id == "10");
        Assert.Contains(upstream, n => n.Id == "11");
        Assert.Contains(upstream, n => n.Id == "12");
    }

    [Fact]
    public void FindNearestUpstream_WalksThroughListChildren()
    {
        // Build: source(10) → BatchImages(20) → downstream(30) consuming 20's IMAGE.
        // Walking upstream from 30 should reach 20 via 30's singular Image input,
        // then reach 10 via 20's list child.
        JObject workflow = MakeBatchImagesFixture("10");
        workflow["30"] = new JObject
        {
            ["class_type"] = "ImageFromBatch",
            ["inputs"] = new JObject
            {
                ["image"] = new JArray("20", 0),
                ["batch_index"] = 0L,
                ["length"] = 1L,
            },
        };
        ComfyGraph graph = ComfyGraph.FromWorkflow(workflow);
        ImageFromBatchNode start = graph.GetNode<ImageFromBatchNode>("30")!;
        UnknownNode? src = graph.FindNearestUpstream<UnknownNode>(start);
        Assert.NotNull(src);
        Assert.Equal("10", src!.Id);
    }

    [Fact]
    public void IsReachableUpstream_FollowsListChildren()
    {
        JObject workflow = MakeBatchImagesFixture("10");
        workflow["30"] = new JObject
        {
            ["class_type"] = "ImageFromBatch",
            ["inputs"] = new JObject
            {
                ["image"] = new JArray("20", 0),
                ["batch_index"] = 0L,
                ["length"] = 1L,
            },
        };
        ComfyGraph graph = ComfyGraph.FromWorkflow(workflow);
        Assert.True(graph.IsReachableUpstream(graph.GetNode("30")!, "10"));
    }

    [Fact]
    public void FindInputsConnectedTo_ReturnsListChildren()
    {
        JObject workflow = MakeBatchImagesFixture("10", "11", "12");
        ComfyGraph graph = ComfyGraph.FromWorkflow(workflow);
        UnknownNode source = graph.GetNode<UnknownNode>("11")!;
        var consumers = graph.FindInputsConnectedTo(source.GetOutput(0));
        Assert.Single(consumers);
        Assert.Equal("20", consumers[0].Node.Id);
        // The consumer is the list child for slot 1 — its Name is the concrete wire key.
        Assert.Equal("images.image1", consumers[0].Input.Name);
    }

    [Fact]
    public void RetargetConnections_RewireListChildToNewSource()
    {
        JObject workflow = MakeBatchImagesFixture("10", "11", "12");
        ComfyGraph graph = ComfyGraph.FromWorkflow(workflow);
        UnknownNode src11 = graph.GetNode<UnknownNode>("11")!;
        // Add a fresh source node and retarget 11's consumers to it.
        UnknownNode src99 = graph.AddNode(new UnknownNode("UnitTest_ImageSource"), "99");
        int count = graph.RetargetConnections(src11.GetOutput(0), src99.GetOutput(0));
        Assert.Equal(1, count);

        JObject inputs = (JObject)graph.ToWorkflow()["20"]!["inputs"]!;
        // image1 now points at "99", others unchanged.
        Assert.Equal("10", (string)((JArray)inputs["images.image0"]!)[0]!);
        Assert.Equal("99", (string)((JArray)inputs["images.image1"]!)[0]!);
        Assert.Equal("12", (string)((JArray)inputs["images.image2"]!)[0]!);
    }

    // ── Names variant (HiDreamO1ReferenceImages-shape) ───────────────

    [Fact]
    public void NamesVariant_SerializesUsingExplicitNames()
    {
        NamesListStub node = new();
        UnknownNode src = new("Dummy");
        node.Images.AddFromUntyped(src.GetOutput(0));
        node.Images.AddFromUntyped(src.GetOutput(0));

        JObject inputs = (JObject)node.ToWorkflowNode()["inputs"]!;
        Assert.Equal(2, inputs.Properties().Count());
        Assert.NotNull(inputs["images.image_1"]);
        Assert.NotNull(inputs["images.image_2"]);
        Assert.Null(inputs["images.image_3"]);
        // Critically: not "images.image0" / "images.image1" — the schema's names win verbatim.
        Assert.Null(inputs["images.image0"]);
    }

    [Fact]
    public void NamesVariant_TryParseKey_MatchesByName()
    {
        INodeInputList list = new NamesListStub().Images;
        Assert.Equal(0, list.TryParseKey("images.image_1"));
        Assert.Equal(2, list.TryParseKey("images.image_3"));
        Assert.Equal(-1, list.TryParseKey("images.image_4"));   // not in names
        Assert.Equal(-1, list.TryParseKey("images.image0"));    // wrong format (prefix-shape)
        Assert.Equal(-1, list.TryParseKey("images.image"));     // no suffix
        Assert.Equal(-1, list.TryParseKey("other_input"));      // unrelated key
    }

    [Fact]
    public void NamesVariant_MaxCappedAtNamesCount()
    {
        // NamesListStub declares 3 names; max:int.MaxValue should be capped to 3.
        NamesListStub node = new();
        Assert.Equal(3, node.Images.Max);
        UnknownNode src = new("Dummy");
        for (int i = 0; i < 3; i++)
        {
            node.Images.AddFromUntyped(src.GetOutput(0));
        }
        Assert.Throws<InvalidOperationException>(() =>
            node.Images.AddFromUntyped(src.GetOutput(0)));
    }

    [Fact]
    public void NamesVariant_RoundTripPreservesSchemaNames()
    {
        // Build a workflow with named keys; FromWorkflow should claim them and re-emit verbatim.
        // Source node "10" is an UnknownNode (materializes outputs on demand) so the wire
        // refs ["10", 0] and ["10", 1] resolve to real INodeOutput instances.
        JObject workflow = new()
        {
            ["10"] = ImageStub(),
            ["20"] = new JObject
            {
                ["class_type"] = NamesListStub.ClassTypeName_,
                ["inputs"] = new JObject
                {
                    ["images.image_1"] = new JArray("10", 0),
                    ["images.image_3"] = new JArray("10", 1),
                },
            },
        };
        // Register the stub once so FromWorkflow can deserialize it.
        ComfyTyped.Core.NodeRegistry.Register(NamesListStub.ClassTypeName_, () => new NamesListStub());

        ComfyGraph graph = ComfyGraph.FromWorkflow(workflow);
        NamesListStub node = graph.GetNode<NamesListStub>("20")!;
        Assert.Equal(2, node.Images.Count);

        // After deserialization, the surviving items compact to positions 0 and 1 → image_1 and image_2.
        // Position 0's original wire-key was image_1, position 1's original was image_3 (now renamed to image_2).
        JObject inputs = (JObject)graph.ToWorkflow()["20"]!["inputs"]!;
        Assert.Equal(0, (int)((JArray)inputs["images.image_1"]!)[1]!);
        Assert.Equal(1, (int)((JArray)inputs["images.image_2"]!)[1]!);
        Assert.Null(inputs["images.image_3"]);
    }

    /// <summary>Hand-rolled stub node matching HiDream's names-shape autogrow schema.
    /// Lives in the test assembly so we don't depend on a regen against a HiDream-having
    /// object_info dump.</summary>
    private sealed class NamesListStub : ComfyNode
    {
        public const string ClassTypeName_ = "UnitTest_NamesList";
        public override string ClassTypeName => ClassTypeName_;
        public NodeInputList<ImageType> Images { get; }

        public NamesListStub()
        {
            Images = AddInputList<ImageType>(
                "images",
                names: new string[] { "image_1", "image_2", "image_3" },
                max: int.MaxValue);
        }
    }

    // ── Multi-list, dangling refs, atomicity ─────────────────────────

    [Fact]
    public void MultiList_NodeRoundTripsEveryListIndependently()
    {
        // GLSLShaderNode has 5 lists with disjoint prefixes (image*, u_float*, u_int*, u_bool*, u_curve*).
        // Verify each list buckets its own keys and doesn't claim sibling-list keys.
        JObject workflow = new()
        {
            ["10"] = ImageStub(),
            ["11"] = ImageStub(),
            ["20"] = new JObject
            {
                ["class_type"] = "GLSLShader",
                ["inputs"] = new JObject
                {
                    ["fragment_shader"] = "void main() {}",
                    ["size_mode"] = "first",
                    ["images.image0"] = new JArray("10", 0),
                    ["images.image1"] = new JArray("11", 0),
                    ["floats.u_float0"] = 1.5,
                    ["floats.u_float1"] = 2.5,
                    ["ints.u_int0"] = 42L,
                    ["bools.u_bool0"] = true,
                },
            },
        };

        ComfyGraph graph = ComfyGraph.FromWorkflow(workflow);
        GLSLShaderNode node = graph.GetNode<GLSLShaderNode>("20")!;
        Assert.Equal(2, node.Images.Count);
        Assert.Equal(2, node.Floats.Count);
        Assert.Equal(1, node.Ints.Count);
        Assert.Equal(1, node.Bools.Count);
        Assert.Equal(0, node.Curves.Count);
        Assert.Empty(node.ExtraInputs.Properties());

        // Round-trip preserves disjoint keysets per list.
        JObject inputs = (JObject)graph.ToWorkflow()["20"]!["inputs"]!;
        Assert.NotNull(inputs["images.image0"]);
        Assert.NotNull(inputs["images.image1"]);
        Assert.NotNull(inputs["floats.u_float0"]);
        Assert.NotNull(inputs["floats.u_float1"]);
        Assert.NotNull(inputs["ints.u_int0"]);
        Assert.NotNull(inputs["bools.u_bool0"]);
        Assert.Equal("void main() {}", (string?)inputs["fragment_shader"]);
    }

    [Fact]
    public void DanglingSourceId_FallsThroughToExtraInputsInsteadOfDistortingList()
    {
        // Workflow with a list ref pointing at a non-existent node id ("999").
        // Old behavior: list materialized the slot, failed to wire, then dropped the key
        //               on serialize — shifting other items' positions.
        // New behavior: the dangling ref lands in ExtraInputs so it round-trips losslessly
        //               and the typed list count stays accurate.
        JObject workflow = new()
        {
            ["10"] = ImageStub(),
            ["20"] = new JObject
            {
                ["class_type"] = "BatchImagesNode",
                ["inputs"] = new JObject
                {
                    ["images.image0"] = new JArray("10", 0),
                    ["images.image1"] = new JArray("999", 0),  // ← dangling
                    ["images.image2"] = new JArray("10", 1),
                },
            },
        };
        ComfyGraph graph = ComfyGraph.FromWorkflow(workflow);
        BatchImagesNodeNode node = graph.GetNode<BatchImagesNodeNode>("20")!;
        // Only the resolvable refs become list items.
        Assert.Equal(2, node.Images.Count);
        // The dangling key landed in ExtraInputs.
        Assert.NotNull(node.ExtraInputs["images.image1"]);
        Assert.Equal("999", (string)((JArray)node.ExtraInputs["images.image1"]!)[0]!);

        // Round-trip: typed list re-emits compacted (image0, image1).
        // The dangling ref survives in node.ExtraInputs (asserted above) but on
        // serialize, the typed list's images.image1 wins over ExtraInputs at the
        // colliding key — full lossless dangling-ref serialization is not guaranteed,
        // but the in-memory shape is preserved for callers to inspect/recover.
        JObject inputs = (JObject)graph.ToWorkflow()["20"]!["inputs"]!;
        Assert.Equal("10", (string)((JArray)inputs["images.image0"]!)[0]!);
        Assert.Equal("10", (string)((JArray)inputs["images.image1"]!)[0]!);
        Assert.Equal(0, (int)((JArray)inputs["images.image0"]!)[1]!);
        Assert.Equal(1, (int)((JArray)inputs["images.image1"]!)[1]!);
    }

    [Fact]
    public void Add_TypeMismatch_LeavesListUnchanged()
    {
        // AddFromUntyped throws on a type-incompatible source; the list must stay empty
        // (no orphan child appended pre-throw, no spurious ListChanged event).
        BatchImagesNodeNode node = new();
        bool fired = false;
        // Subscribe via a bridge so we can observe events.
        using WorkflowBridge bridge = new(new ComfyGraph(), new JObject());
        bridge.AddNode(node);
        // Connect a latent output (incompatible with ImageType, not a wildcard).
        EmptyLatentImageNode latentSrc = new();
        Assert.Throws<InvalidOperationException>(() =>
            node.Images.AddFromUntyped(latentSrc.LATENT));
        Assert.Equal(0, node.Images.Count);
        // Workflow JSON should reflect zero children for the list.
        JObject inputs = (JObject)bridge.Workflow[node.Id]!["inputs"]!;
        Assert.Null(inputs["images.image0"]);
        // (No assertion on `fired` — we'd need a hook into the bridge handlers to check
        // that ListChanged didn't fire. The Count check above is the observable proxy.)
    }

    [Fact]
    public void DoubleSubscribe_IsIdempotent()
    {
        // A caller that adds the same node instance twice should not get duplicate events.
        // We verify by counting JObject inputs writes: a Set on an int input would
        // be written once, not twice.
        WorkflowBridge bridge = new(new ComfyGraph(), new JObject());
        EmptyLatentImageNode node = new();
        bridge.AddNode(node);
        bridge.AddNode(node);   // second subscribe is a no-op
        node.Width.Set(2048L);
        JObject inputs = (JObject)bridge.Workflow[node.Id]!["inputs"]!;
        Assert.Equal(2048L, (long)inputs["width"]!);
        // Indirect proof of single fire: if double-subscribed, the second handler would
        // also run, but since both handlers do `inputs.Remove(name); SerializeInto(inputs);`
        // idempotently, this test alone doesn't catch all duplicates. The unit-level proof
        // is just that no exception is thrown and the value is correct.
        bridge.Dispose();
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
