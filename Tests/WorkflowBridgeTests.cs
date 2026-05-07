using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ComfyTyped.Tests;

public class WorkflowBridgeTests
{
    public WorkflowBridgeTests()
    {
        NodeRegistrations.EnsureRegistered();
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private static JObject BuildSimpleWorkflow() => new()
    {
        ["1"] = new JObject
        {
            ["class_type"] = "CheckpointLoaderSimple",
            ["inputs"] = new JObject { ["ckpt_name"] = "model.safetensors" }
        },
        ["2"] = new JObject
        {
            ["class_type"] = "CLIPTextEncode",
            ["inputs"] = new JObject { ["text"] = "a cat", ["clip"] = new JArray("1", 1) }
        },
        ["3"] = new JObject
        {
            ["class_type"] = "KSampler",
            ["inputs"] = new JObject
            {
                ["model"] = new JArray("1", 0),
                ["seed"] = 42,
                ["steps"] = 20,
                ["cfg"] = 7.0,
                ["sampler_name"] = "euler",
                ["scheduler"] = "normal",
                ["positive"] = new JArray("2", 0),
                ["negative"] = new JArray("2", 0),
                ["latent_image"] = new JArray("4", 0),
                ["denoise"] = 1.0
            }
        },
        ["4"] = new JObject
        {
            ["class_type"] = "EmptyLatentImage",
            ["inputs"] = new JObject { ["width"] = 512, ["height"] = 512, ["batch_size"] = 1 }
        }
    };

    // ═════════════════════════════════════════════════════════════════
    //  1. Creation & Snapshot Fidelity
    // ═════════════════════════════════════════════════════════════════

    [Fact]
    public void Create_PreservesAllNodes()
    {
        JObject workflow = BuildSimpleWorkflow();
        var bridge = WorkflowBridge.Create(workflow);

        Assert.Equal(4, bridge.Graph.Nodes.Count);
        Assert.IsType<CheckpointLoaderSimpleNode>(bridge.Graph.GetNode("1"));
        Assert.IsType<CLIPTextEncodeNode>(bridge.Graph.GetNode("2"));
        Assert.IsType<KSamplerNode>(bridge.Graph.GetNode("3"));
        Assert.IsType<EmptyLatentImageNode>(bridge.Graph.GetNode("4"));
    }

    [Fact]
    public void Create_PreservesConnections()
    {
        JObject workflow = BuildSimpleWorkflow();
        var bridge = WorkflowBridge.Create(workflow);

        var clip = bridge.Graph.GetNode<CLIPTextEncodeNode>("2")!;
        Assert.True(clip.Clip.IsConnected);
        Assert.Equal("1", clip.Clip.Connection!.Node.Id);
        Assert.Equal(1, clip.Clip.Connection!.SlotIndex);

        var ksampler = bridge.Graph.GetNode<KSamplerNode>("3")!;
        Assert.True(ksampler.Model.IsConnected);
        Assert.Equal("1", ksampler.Model.Connection!.Node.Id);
        Assert.Equal(0, ksampler.Model.Connection!.SlotIndex);
    }

    [Fact]
    public void Create_PreservesLiterals()
    {
        JObject workflow = new()
        {
            ["1"] = new JObject
            {
                ["class_type"] = "KSampler",
                ["inputs"] = new JObject
                {
                    ["seed"] = 42,
                    ["steps"] = 20,
                    ["cfg"] = 7.5,
                    ["sampler_name"] = "euler",
                    ["scheduler"] = "normal",
                    ["denoise"] = 0.8
                }
            }
        };
        var bridge = WorkflowBridge.Create(workflow);
        var ks = bridge.Graph.GetNode<KSamplerNode>("1")!;

        Assert.Equal(42L, ks.Seed.LiteralValue);
        Assert.Equal(20L, ks.Steps.LiteralValue);
        Assert.Equal(7.5, ks.Cfg.LiteralValue);
        Assert.Equal("euler", ks.SamplerName.LiteralValue);
        Assert.Equal("normal", ks.Scheduler.LiteralValue);
        Assert.Equal(0.8, ks.Denoise.LiteralValue);
    }

    [Fact]
    public void Create_PreservesUnknownNodes()
    {
        JObject workflow = new()
        {
            ["1"] = new JObject
            {
                ["class_type"] = "MyCustomNode",
                ["inputs"] = new JObject { ["param"] = "hello", ["value"] = 99 }
            }
        };
        var bridge = WorkflowBridge.Create(workflow);

        var node = bridge.Graph.GetNode("1");
        Assert.NotNull(node);
        Assert.IsType<UnknownNode>(node);
        Assert.Equal("MyCustomNode", node.ClassTypeName);
    }

    [Fact]
    public void Create_DoesNotCloneWorkflow()
    {
        JObject workflow = BuildSimpleWorkflow();
        var bridge = WorkflowBridge.Create(workflow);

        Assert.Same(workflow, bridge.Workflow);
    }

    [Fact]
    public void Create_EmptyWorkflow()
    {
        var bridge = WorkflowBridge.Create(new JObject());

        Assert.Empty(bridge.Graph.Nodes);
    }

    [Fact]
    public void Create_SingleNodeNoConnections()
    {
        JObject workflow = new()
        {
            ["1"] = new JObject
            {
                ["class_type"] = "CheckpointLoaderSimple",
                ["inputs"] = new JObject { ["ckpt_name"] = "model.safetensors" }
            }
        };
        var bridge = WorkflowBridge.Create(workflow);

        Assert.Single(bridge.Graph.Nodes);
        var ckpt = bridge.Graph.GetNode<CheckpointLoaderSimpleNode>("1")!;
        Assert.Equal("model.safetensors", ckpt.CkptName.LiteralValue);
    }

    [Fact]
    public void Create_NonNodeProperties_Ignored()
    {
        JObject workflow = BuildSimpleWorkflow();
        workflow["_meta"] = new JObject { ["version"] = "1.0" };
        workflow["prompt_id"] = "abc-123";

        var bridge = WorkflowBridge.Create(workflow);

        Assert.Equal(4, bridge.Graph.Nodes.Count);
        // Non-node properties are preserved in the JObject
        Assert.NotNull(bridge.Workflow["_meta"]);
        Assert.Equal("abc-123", bridge.Workflow.Value<string>("prompt_id"));
    }

    // ═════════════════════════════════════════════════════════════════
    //  2. AddNode
    // ═════════════════════════════════════════════════════════════════

    [Fact]
    public void AddNode_AutoId_AppearsInBoth()
    {
        var bridge = WorkflowBridge.Create(new JObject());
        var node = bridge.AddNode(new VAEDecodeNode());

        Assert.NotNull(node.Id);
        Assert.NotEmpty(node.Id);
        Assert.NotNull(bridge.Graph.GetNode(node.Id));
        Assert.NotNull(bridge.Workflow[node.Id]);
        Assert.Equal("VAEDecode", bridge.Workflow[node.Id]!.Value<string>("class_type"));
    }

    [Fact]
    public void AddNode_ExplicitId_AppearsInBoth()
    {
        var bridge = WorkflowBridge.Create(new JObject());
        var node = bridge.AddNode(new VAEDecodeNode(), "mynode");

        Assert.Equal("mynode", node.Id);
        Assert.NotNull(bridge.Graph.GetNode("mynode"));
        Assert.NotNull(bridge.Workflow["mynode"]);
        Assert.Equal("VAEDecode", bridge.Workflow["mynode"]!.Value<string>("class_type"));
    }

    [Fact]
    public void AddNode_AutoId_AvoidsExistingIds()
    {
        JObject workflow = new();
        for (int i = 1; i <= 50; i++)
        {
            workflow[$"{i}"] = new JObject
            {
                ["class_type"] = "CheckpointLoaderSimple",
                ["inputs"] = new JObject { ["ckpt_name"] = "m.safetensors" }
            };
        }
        var bridge = WorkflowBridge.Create(workflow);
        var node = bridge.AddNode(new VAEDecodeNode());

        Assert.True(int.Parse(node.Id) > 50, $"Expected ID > 50, got {node.Id}");
    }

    [Fact]
    public void AddNode_AutoId_AvoidsNonNodeNumericKeys()
    {
        JObject workflow = BuildSimpleWorkflow();
        workflow["200"] = "some non-node value";

        var bridge = WorkflowBridge.Create(workflow);
        var node = bridge.AddNode(new VAEDecodeNode());

        Assert.True(int.Parse(node.Id) > 200, $"Expected ID > 200, got {node.Id}");
    }

    [Fact]
    public void AddNode_WithConnections_SerializesCorrectly()
    {
        JObject workflow = BuildSimpleWorkflow();
        var bridge = WorkflowBridge.Create(workflow);

        var ckpt = bridge.Graph.GetNode<CheckpointLoaderSimpleNode>("1")!;
        var decode = new VAEDecodeNode();
        decode.Vae.ConnectTo(ckpt.VAE);
        bridge.AddNode(decode);

        // Verify the JObject has the correct JArray connection
        JArray vaeConn = (JArray)bridge.Workflow[decode.Id]!["inputs"]!["vae"]!;
        Assert.Equal("1", (string)vaeConn[0]!);
        Assert.Equal(2, (int)vaeConn[1]!); // VAE is slot 2
    }

    [Fact]
    public void ConnectToUntyped_AllowsConcreteOutputIntoMatchTypeV3Input()
    {
        // ComfyMatchTypeV3 is a wildcard slot type — concrete outputs (IMAGE, MASK, etc.)
        // must be connectable to it, mirroring AnyType wildcard behavior.
        var bridge = WorkflowBridge.Create(new JObject());
        var ckpt = bridge.AddNode(new CheckpointLoaderSimpleNode());
        var latent = bridge.AddNode(new EmptyLatentImageNode());
        var decode = bridge.AddNode(new VAEDecodeNode());
        decode.Vae.ConnectTo(ckpt.VAE);
        decode.Samples.ConnectTo(latent.LATENT);

        var resize = bridge.AddNode(new ResizeImageMaskNodeNode());
        resize.ResizeType.Set("pixels");
        ((INodeInput)resize.Input).ConnectToUntyped(decode.IMAGE);

        bridge.SyncAll();

        JArray inputRef = (JArray)bridge.Workflow[resize.Id]!["inputs"]!["input"]!;
        Assert.Equal(decode.Id, (string)inputRef[0]!);
        Assert.Equal(0, (int)inputRef[1]!);
    }

    [Fact]
    public void TryConnectToUntyped_NullOutputReturnsFalseAndIsNoOp()
    {
        // Resolver-style sources (e.g. WorkflowBridge.ResolvePath) may legitimately return
        // null when the requested path does not resolve. TryConnectToUntyped lets callers
        // forward that null without a guard: returns false (no-op, slot state preserved)
        // on null, true on a successful connect.
        var bridge = WorkflowBridge.Create(new JObject());
        var resize = bridge.AddNode(new ResizeImageMaskNodeNode());
        resize.ResizeType.Set("pixels");

        Assert.False(((INodeInput)resize.Input).TryConnectToUntyped(null));
        Assert.False(resize.Input.IsConnected);
        bridge.SyncAll();
        Assert.Null(bridge.Workflow[resize.Id]!["inputs"]!["input"]);

        // Real output: returns true and connects.
        var ckpt = bridge.AddNode(new CheckpointLoaderSimpleNode());
        var latent = bridge.AddNode(new EmptyLatentImageNode());
        var decode = bridge.AddNode(new VAEDecodeNode());
        decode.Vae.ConnectTo(ckpt.VAE);
        decode.Samples.ConnectTo(latent.LATENT);
        Assert.True(((INodeInput)resize.Input).TryConnectToUntyped(decode.IMAGE));
        Assert.True(resize.Input.IsConnected);

        // Subsequent null call is a no-op — must not clear the existing connection.
        Assert.False(((INodeInput)resize.Input).TryConnectToUntyped(null));
        Assert.True(resize.Input.IsConnected);
    }

    [Fact]
    public void ConnectToUntyped_AllowsMatchTypeV3OutputIntoConcreteInput()
    {
        // The reverse direction: ComfyMatchTypeV3 output (e.g., ResizeImageMaskNode.Resized)
        // feeding a concrete-typed input must also be allowed.
        var bridge = WorkflowBridge.Create(new JObject());
        var ckpt = bridge.AddNode(new CheckpointLoaderSimpleNode());
        var resize = bridge.AddNode(new ResizeImageMaskNodeNode());
        resize.ResizeType.Set("pixels");

        var encode = bridge.AddNode(new VAEEncodeNode());
        encode.Vae.ConnectTo(ckpt.VAE);
        ((INodeInput)encode.Pixels).ConnectToUntyped(resize.Resized);

        bridge.SyncAll();

        JArray pixelsRef = (JArray)bridge.Workflow[encode.Id]!["inputs"]!["pixels"]!;
        Assert.Equal(resize.Id, (string)pixelsRef[0]!);
    }

    [Fact]
    public void AddNode_WithLiterals_SerializesCorrectly()
    {
        var bridge = WorkflowBridge.Create(new JObject());

        var emptyLatent = new EmptyLatentImageNode();
        emptyLatent.Width.Set(1024L);
        emptyLatent.Height.Set(768L);
        emptyLatent.BatchSize.Set(2L);
        bridge.AddNode(emptyLatent);

        JObject inputs = (JObject)bridge.Workflow[emptyLatent.Id]!["inputs"]!;
        Assert.Equal(1024L, (long)inputs["width"]!);
        Assert.Equal(768L, (long)inputs["height"]!);
        Assert.Equal(2L, (long)inputs["batch_size"]!);
    }

    [Fact]
    public void AddNode_Multiple_SequentialIds()
    {
        var bridge = WorkflowBridge.Create(new JObject());
        var n1 = bridge.AddNode(new VAEDecodeNode());
        var n2 = bridge.AddNode(new VAEDecodeNode());
        var n3 = bridge.AddNode(new VAEDecodeNode());

        int id1 = int.Parse(n1.Id);
        int id2 = int.Parse(n2.Id);
        int id3 = int.Parse(n3.Id);

        Assert.True(id2 > id1);
        Assert.True(id3 > id2);
    }

    [Fact]
    public void AddNode_ReturnsNodeWithId()
    {
        var bridge = WorkflowBridge.Create(new JObject());
        var node = bridge.AddNode(new CheckpointLoaderSimpleNode());

        Assert.NotNull(node.Id);
        Assert.NotEmpty(node.Id);
        Assert.IsType<CheckpointLoaderSimpleNode>(node);
    }

    // ═════════════════════════════════════════════════════════════════
    //  3. RemoveNode
    // ═════════════════════════════════════════════════════════════════

    [Fact]
    public void RemoveNode_ById_RemovesFromBoth()
    {
        JObject workflow = BuildSimpleWorkflow();
        var bridge = WorkflowBridge.Create(workflow);

        bool removed = bridge.RemoveNode("4"); // EmptyLatentImage

        Assert.True(removed);
        Assert.Null(bridge.Graph.GetNode("4"));
        Assert.Null(bridge.Workflow["4"]);
    }

    [Fact]
    public void RemoveNode_ByNode_RemovesFromBoth()
    {
        JObject workflow = BuildSimpleWorkflow();
        var bridge = WorkflowBridge.Create(workflow);
        var node = bridge.Graph.GetNode("4")!;

        bool removed = bridge.RemoveNode(node);

        Assert.True(removed);
        Assert.Null(bridge.Graph.GetNode("4"));
        Assert.Null(bridge.Workflow["4"]);
    }

    [Fact]
    public void RemoveAllNodes_RemovesEveryNodeFromBoth()
    {
        JObject workflow = BuildSimpleWorkflow();
        var bridge = WorkflowBridge.Create(workflow);

        int removed = bridge.RemoveAllNodes();

        Assert.Equal(4, removed);
        Assert.Empty(bridge.Graph.Nodes);
        Assert.Null(bridge.Workflow["1"]);
        Assert.Null(bridge.Workflow["4"]);
    }

    [Fact]
    public void RemoveAllNodes_PreservesNonNodeProperties()
    {
        JObject workflow = BuildSimpleWorkflow();
        workflow["_meta"] = new JObject { ["version"] = "1.0" };
        var bridge = WorkflowBridge.Create(workflow);

        bridge.RemoveAllNodes();

        Assert.NotNull(bridge.Workflow["_meta"]);
        Assert.Equal("1.0", bridge.Workflow["_meta"]!.Value<string>("version"));
    }

    [Fact]
    public void RemoveAllNodes_OnEmptyBridge_ReturnsZero()
    {
        var bridge = WorkflowBridge.Create(new JObject());
        Assert.Equal(0, bridge.RemoveAllNodes());
    }

    [Fact]
    public void RemoveNode_NonExistent_ReturnsFalse()
    {
        var bridge = WorkflowBridge.Create(new JObject());
        Assert.False(bridge.RemoveNode("999"));
    }

    [Fact]
    public void RemoveNode_PreservesOtherNodes()
    {
        JObject workflow = BuildSimpleWorkflow();
        var bridge = WorkflowBridge.Create(workflow);

        bridge.RemoveNode("4");

        Assert.Equal(3, bridge.Graph.Nodes.Count);
        Assert.NotNull(bridge.Graph.GetNode("1"));
        Assert.NotNull(bridge.Graph.GetNode("2"));
        Assert.NotNull(bridge.Graph.GetNode("3"));
        Assert.NotNull(bridge.Workflow["1"]);
        Assert.NotNull(bridge.Workflow["2"]);
        Assert.NotNull(bridge.Workflow["3"]);
    }

    [Fact]
    public void RemoveNode_PreservesNonNodeProperties()
    {
        JObject workflow = BuildSimpleWorkflow();
        workflow["_meta"] = new JObject { ["version"] = "1.0" };
        var bridge = WorkflowBridge.Create(workflow);

        bridge.RemoveNode("4");

        Assert.NotNull(bridge.Workflow["_meta"]);
        Assert.Equal("1.0", bridge.Workflow["_meta"]!.Value<string>("version"));
    }

    // ═════════════════════════════════════════════════════════════════
    //  3.5 Auto-sync (typed mutations propagate without explicit SyncNode)
    // ═════════════════════════════════════════════════════════════════

    [Fact]
    public void AutoSync_LiteralMutationOnAddedNode_FlushesImmediately()
    {
        var bridge = WorkflowBridge.Create(new JObject());
        var ksampler = bridge.AddNode(new KSamplerNode());

        ksampler.Seed.Set(123L);

        Assert.Equal(123L, (long)bridge.Workflow[ksampler.Id]!["inputs"]!["seed"]!);
    }

    [Fact]
    public void AutoSync_LiteralMutationOnLoadedNode_FlushesImmediately()
    {
        JObject workflow = BuildSimpleWorkflow();
        var bridge = WorkflowBridge.Create(workflow);

        var ksampler = bridge.Graph.GetNode<KSamplerNode>("3")!;
        ksampler.Seed.Set(555L);

        Assert.Equal(555L, (long)bridge.Workflow["3"]!["inputs"]!["seed"]!);
    }

    [Fact]
    public void AutoSync_ConnectionRetarget_FlushesImmediately()
    {
        JObject workflow = BuildSimpleWorkflow();
        var bridge = WorkflowBridge.Create(workflow);

        var ksampler = bridge.Graph.GetNode<KSamplerNode>("3")!;
        var newLatent = bridge.AddNode(new EmptyLatentImageNode());
        ksampler.LatentImage.ConnectTo(newLatent.LATENT);

        JArray ref_ = (JArray)bridge.Workflow["3"]!["inputs"]!["latent_image"]!;
        Assert.Equal(newLatent.Id, (string)ref_[0]!);
    }

    [Fact]
    public void AutoSync_Clear_RemovesPropertyFromInputs()
    {
        JObject workflow = BuildSimpleWorkflow();
        var bridge = WorkflowBridge.Create(workflow);

        var ksampler = bridge.Graph.GetNode<KSamplerNode>("3")!;
        Assert.NotNull(bridge.Workflow["3"]!["inputs"]!["seed"]);

        ksampler.Seed.Clear();

        Assert.Null(bridge.Workflow["3"]!["inputs"]!["seed"]);
    }

    [Fact]
    public void AutoSync_PreservesOuterNodeJObjectReference()
    {
        // Holders of Workflow[id] before a mutation should still see the post-mutation state.
        var bridge = WorkflowBridge.Create(new JObject());
        var ksampler = bridge.AddNode(new KSamplerNode());
        JObject heldRef = (JObject)bridge.Workflow[ksampler.Id]!;

        ksampler.Seed.Set(777L);

        Assert.Same(heldRef, bridge.Workflow[ksampler.Id]);
        Assert.Equal(777L, (long)heldRef["inputs"]!["seed"]!);
    }

    [Fact]
    public void AutoSync_AfterRemoveNode_StopsTracking()
    {
        var bridge = WorkflowBridge.Create(new JObject());
        var ksampler = bridge.AddNode(new KSamplerNode());
        bridge.RemoveNode(ksampler);

        // Mutating a removed node must not throw and must not resurrect it in the JObject.
        ksampler.Seed.Set(42L);

        Assert.Null(bridge.Workflow[ksampler.Id]);
    }

    [Fact]
    public void AutoSync_UnknownNodeFirstMutation_RebuildsInputsFromTypedSlots()
    {
        // RawInputs initially wins over typed slots in ToWorkflowNode. The first typed mutation
        // must clear RawInputs and rebuild Workflow[id]["inputs"] so the new value is visible.
        JObject workflow = new()
        {
            ["50"] = new JObject
            {
                ["class_type"] = "SomeCustomWidget",
                ["inputs"] = new JObject { ["text"] = "old", ["count"] = 1 }
            }
        };
        var bridge = WorkflowBridge.Create(workflow);
        var unknown = (UnknownNode)bridge.Graph.GetNode("50")!;

        unknown.GetInput("text").Set("new");

        Assert.Equal("new", bridge.Workflow["50"]!["inputs"]!.Value<string>("text"));
        // Untouched input survives the rebuild because FromWorkflow seeded a typed slot for it.
        Assert.Equal(1L, (long)bridge.Workflow["50"]!["inputs"]!["count"]!);
        Assert.Null(unknown.RawInputs);
    }

    [Fact]
    public void AutoSync_LiteralAfterWildcardConnection_ReplacesConnection()
    {
        // Regression: ConnectToUntyped(wildcardOut) sets _untypedConnection; a subsequent Set must
        // clear _untypedConnection, otherwise EffectiveConnection masks the literal and the JObject
        // ends up with the stale [nodeId, slot] JArray instead of the new literal value.
        JObject workflow = new()
        {
            ["50"] = new JObject
            {
                ["class_type"] = "SomeWildcardSource",
                ["inputs"] = new JObject(),
            },
        };
        var bridge = WorkflowBridge.Create(workflow);
        var unknown = (UnknownNode)bridge.Graph.GetNode("50")!;
        NodeOutput<ComfyTyped.Types.AnyType> wildcard = unknown.GetOutput(0);

        var ksampler = bridge.AddNode(new KSamplerNode());
        ksampler.Seed.ConnectToUntyped(wildcard);
        Assert.True(ksampler.Seed.IsConnected);

        ksampler.Seed.Set(7L);

        Assert.False(ksampler.Seed.IsConnected);
        Assert.Equal(7L, (long)bridge.Workflow[ksampler.Id]!["inputs"]!["seed"]!);
    }

    [Fact]
    public void Dispose_StopsAutoSync_AndIsIdempotent()
    {
        JObject workflow = BuildSimpleWorkflow();
        var bridge = WorkflowBridge.Create(workflow);
        var ksampler = bridge.Graph.GetNode<KSamplerNode>("3")!;

        bridge.Dispose();
        bridge.Dispose(); // idempotent

        ksampler.Seed.Set(999L);

        // The JObject still has the original seed because the bridge is no longer subscribed.
        Assert.Equal(42L, (long)bridge.Workflow["3"]!["inputs"]!["seed"]!);
    }

    [Fact]
    public void AutoSync_ConnectionFromAddedNodeUsesAssignedId()
    {
        // Regression: the upstream node must be in the bridge before downstream's input
        // connects to it, so the auto-synced JArray carries the right ID.
        var bridge = WorkflowBridge.Create(new JObject());
        var ckpt = bridge.AddNode(new CheckpointLoaderSimpleNode());
        var sampler = bridge.AddNode(new KSamplerNode());

        sampler.Model.ConnectTo(ckpt.MODEL);

        JArray modelRef = (JArray)bridge.Workflow[sampler.Id]!["inputs"]!["model"]!;
        Assert.Equal(ckpt.Id, (string)modelRef[0]!);
        Assert.Equal(0, (int)modelRef[1]!);
    }

    // ═════════════════════════════════════════════════════════════════
    //  3.6 Middle-node deletion (rewire-then-delete contract)
    // ═════════════════════════════════════════════════════════════════

    /// <summary>Build a workflow with a true middle node: ckpt → ksampler → vaeDecode, vae also feeds vaeDecode.</summary>
    private static (WorkflowBridge bridge, CheckpointLoaderSimpleNode ckpt, KSamplerNode sampler, VAEDecodeNode decode) BuildLinearChain()
    {
        var bridge = WorkflowBridge.Create(new JObject());
        var ckpt = bridge.AddNode(new CheckpointLoaderSimpleNode());
        var emptyLatent = bridge.AddNode(new EmptyLatentImageNode());
        var sampler = bridge.AddNode(new KSamplerNode());
        sampler.Model.ConnectTo(ckpt.MODEL);
        sampler.LatentImage.ConnectTo(emptyLatent.LATENT);
        var decode = bridge.AddNode(new VAEDecodeNode());
        decode.Samples.ConnectTo(sampler.LATENT);
        decode.Vae.ConnectTo(ckpt.VAE);

        return (bridge, ckpt, sampler, decode);
    }

    private static bool WorkflowReferencesId(JObject workflow, string id)
    {
        foreach (JProperty prop in workflow.Properties())
        {
            if (prop.Value is not JObject node || node["inputs"] is not JObject inputs)
            {
                continue;
            }
            foreach (JProperty input in inputs.Properties())
            {
                if (input.Value is JArray arr && arr.Count == 2 && (string?)arr[0] == id)
                {
                    return true;
                }
            }
        }
        return false;
    }

    [Fact]
    public void RewireThenDelete_LeavesNoDanglingReferences()
    {
        var (bridge, _, sampler, decode) = BuildLinearChain();
        string oldId = sampler.Id;

        var replacement = bridge.AddNode(new KSamplerNode());

        // Rewire each output of `sampler` to its counterpart on `replacement`.
        // Same-class swap: every slot index lines up, so the FindOutput null-guard never trips.
        foreach (INodeOutput output in sampler.Outputs)
        {
            INodeOutput? to = replacement.FindOutput(output.SlotIndex);
            if (to is not null) bridge.Graph.RetargetConnections(output, to);
        }
        bridge.RemoveNode(sampler);

        // JObject side: no entry, no dangling refs anywhere.
        Assert.Null(bridge.Workflow[oldId]);
        Assert.False(WorkflowReferencesId(bridge.Workflow, oldId),
            "JObject still has dangling JArray refs to the removed node ID.");

        // Typed-graph side: the old node is gone from the graph.
        Assert.Null(bridge.Graph.GetNode(oldId));

        // Downstream consumer now points at the replacement, both in the JObject and the typed graph.
        JArray samplesRef = (JArray)bridge.Workflow[decode.Id]!["inputs"]!["samples"]!;
        Assert.Equal(replacement.Id, (string)samplesRef[0]!);
        Assert.Equal(0, (int)samplesRef[1]!);
        Assert.Same(replacement, decode.Samples.Connection!.Node);

        // Auto-sync still tracks the replacement after the swap (catches subscription leaks/double-removes).
        replacement.Seed.Set(12345L);
        Assert.Equal(12345L, (long)bridge.Workflow[replacement.Id]!["inputs"]!["seed"]!);
    }

    [Fact]
    public void NaiveDelete_OfMiddleNode_LeavesDanglingJArrayInConsumers()
    {
        // Pins the "dumb delete" contract documented at ComfyGraph.RemoveNode and on WorkflowBridge
        // ("Removing a node with consumers" section). The contract is intentional:
        //   (a) callers can stage retargets in any order without RemoveNode silently undoing them,
        //   (b) RemoveNode stays O(1) instead of O(nodes × inputs),
        //   (c) WorkflowBridge.RemoveNode stays a thin wrapper over ComfyGraph.RemoveNode — same semantics.
        // If you grow cascade-cleanup, update this test deliberately AND update README + the XML doc on
        // ComfyGraph.RemoveNode + the class doc on WorkflowBridge in the same change.
        var (bridge, _, sampler, decode) = BuildLinearChain();
        string oldId = sampler.Id;

        bridge.RemoveNode(sampler);

        Assert.Null(bridge.Workflow[oldId]);

        // JObject side: downstream consumer still has the dangling [oldId, 0] JArray.
        JArray samplesRef = (JArray)bridge.Workflow[decode.Id]!["inputs"]!["samples"]!;
        Assert.Equal(oldId, (string)samplesRef[0]!);
        Assert.Equal(0, (int)samplesRef[1]!);
        Assert.True(WorkflowReferencesId(bridge.Workflow, oldId),
            "Expected naive RemoveNode to leave a dangling JArray ref in consumers.");

        // Typed-graph side: the consumer's NodeInput<T> still holds the stranded reference to the
        // removed node. Pinning both sides ensures a future cascade-fix that scrubs only one is caught.
        Assert.True(decode.Samples.IsConnected);
        Assert.Same(sampler, decode.Samples.Connection!.Node);
    }

    // ═════════════════════════════════════════════════════════════════
    //  4. SyncNode
    // ═════════════════════════════════════════════════════════════════

    [Fact]
    public void SyncNode_AfterConnectionChange_UpdatesJObject()
    {
        JObject workflow = BuildSimpleWorkflow();
        var bridge = WorkflowBridge.Create(workflow);

        var ksampler = bridge.Graph.GetNode<KSamplerNode>("3")!;

        // Verify initial connection: latent_image → node 4
        JArray initialRef = (JArray)bridge.Workflow["3"]!["inputs"]!["latent_image"]!;
        Assert.Equal("4", (string)initialRef[0]!);

        var newLatent = bridge.AddNode(new EmptyLatentImageNode());
        ksampler.LatentImage.ConnectTo(newLatent.LATENT);

        // Auto-sync flushed the change without an explicit SyncNode call.
        JArray autoSynced = (JArray)bridge.Workflow["3"]!["inputs"]!["latent_image"]!;
        Assert.Equal(newLatent.Id, (string)autoSynced[0]!);

        // SyncNode is still idempotent — calling it doesn't break the value.
        bridge.SyncNode(ksampler);
        JArray afterSync = (JArray)bridge.Workflow["3"]!["inputs"]!["latent_image"]!;
        Assert.Equal(newLatent.Id, (string)afterSync[0]!);
    }

    [Fact]
    public void SyncNode_AfterLiteralChange_UpdatesJObject()
    {
        JObject workflow = BuildSimpleWorkflow();
        var bridge = WorkflowBridge.Create(workflow);

        var ksampler = bridge.Graph.GetNode<KSamplerNode>("3")!;
        ksampler.Seed.Set(999L);

        bridge.SyncNode(ksampler);

        Assert.Equal(999L, (long)bridge.Workflow["3"]!["inputs"]!["seed"]!);
    }

    [Fact]
    public void SyncNode_ById_Works()
    {
        JObject workflow = BuildSimpleWorkflow();
        var bridge = WorkflowBridge.Create(workflow);

        var ksampler = bridge.Graph.GetNode<KSamplerNode>("3")!;
        ksampler.Seed.Set(777L);

        bridge.SyncNode("3");

        Assert.Equal(777L, (long)bridge.Workflow["3"]!["inputs"]!["seed"]!);
    }

    [Fact]
    public void SyncNode_UnknownId_Throws()
    {
        var bridge = WorkflowBridge.Create(new JObject());

        Assert.Throws<KeyNotFoundException>(() => bridge.SyncNode("nonexistent"));
    }

    [Fact]
    public void SyncNode_DoesNotAffectOtherNodes()
    {
        JObject workflow = BuildSimpleWorkflow();
        var bridge = WorkflowBridge.Create(workflow);

        // Snapshot the original node 1 JObject
        string originalNode1 = bridge.Workflow["1"]!.ToString();

        // Modify node 3
        var ksampler = bridge.Graph.GetNode<KSamplerNode>("3")!;
        ksampler.Seed.Set(999L);
        bridge.SyncNode("3");

        // Node 1 is unchanged
        Assert.Equal(originalNode1, bridge.Workflow["1"]!.ToString());
    }

    // ═════════════════════════════════════════════════════════════════
    //  5. SyncAll
    // ═════════════════════════════════════════════════════════════════

    [Fact]
    public void SyncAll_AfterMultipleMutations_UpdatesAll()
    {
        JObject workflow = BuildSimpleWorkflow();
        var bridge = WorkflowBridge.Create(workflow);

        // Mutate several nodes
        var ckpt = bridge.Graph.GetNode<CheckpointLoaderSimpleNode>("1")!;
        ckpt.CkptName.Set("other_model.safetensors");

        var ksampler = bridge.Graph.GetNode<KSamplerNode>("3")!;
        ksampler.Seed.Set(999L);
        ksampler.SamplerName.Set("dpmpp_2m");

        bridge.SyncAll();

        Assert.Equal("other_model.safetensors", bridge.Workflow["1"]!["inputs"]!.Value<string>("ckpt_name"));
        Assert.Equal(999L, (long)bridge.Workflow["3"]!["inputs"]!["seed"]!);
        Assert.Equal("dpmpp_2m", bridge.Workflow["3"]!["inputs"]!.Value<string>("sampler_name"));
    }

    [Fact]
    public void SyncAll_PreservesNonNodeProperties()
    {
        JObject workflow = BuildSimpleWorkflow();
        workflow["_meta"] = new JObject { ["version"] = "2.0" };
        workflow["prompt_id"] = "xyz-789";
        var bridge = WorkflowBridge.Create(workflow);

        bridge.SyncAll();

        Assert.Equal("2.0", bridge.Workflow["_meta"]!.Value<string>("version"));
        Assert.Equal("xyz-789", bridge.Workflow.Value<string>("prompt_id"));
    }

    [Fact]
    public void SyncAll_RemovesDeletedNodes()
    {
        JObject workflow = BuildSimpleWorkflow();
        var bridge = WorkflowBridge.Create(workflow);

        bridge.Graph.RemoveNode("4"); // remove from graph only
        Assert.NotNull(bridge.Workflow["4"]); // still in JObject

        bridge.SyncAll();

        Assert.Null(bridge.Workflow["4"]); // now gone from JObject too
    }

    [Fact]
    public void SyncAll_AddsNewNodes()
    {
        JObject workflow = BuildSimpleWorkflow();
        var bridge = WorkflowBridge.Create(workflow);

        // Add directly to graph (not through bridge.AddNode)
        var newNode = new VAEDecodeNode();
        bridge.Graph.AddNode(newNode);

        Assert.Null(bridge.Workflow[newNode.Id]); // not in JObject yet

        bridge.SyncAll();

        Assert.NotNull(bridge.Workflow[newNode.Id]);
        Assert.Equal("VAEDecode", bridge.Workflow[newNode.Id]!.Value<string>("class_type"));
    }

    [Fact]
    public void SyncAll_Idempotent()
    {
        JObject workflow = BuildSimpleWorkflow();
        var bridge = WorkflowBridge.Create(workflow);

        bridge.SyncAll();
        string after1 = bridge.Workflow.ToString();

        bridge.SyncAll();
        string after2 = bridge.Workflow.ToString();

        Assert.Equal(after1, after2);
    }

    // ═════════════════════════════════════════════════════════════════
    //  6. ToPath / ResolvePath
    // ═════════════════════════════════════════════════════════════════

    [Fact]
    public void ToPath_ReturnsCorrectJArray()
    {
        var bridge = WorkflowBridge.Create(new JObject());
        var node = bridge.AddNode(new VAEDecodeNode());

        JArray path = WorkflowBridge.ToPath(node.IMAGE);

        Assert.Equal(node.Id, (string)path[0]!);
        Assert.Equal(0, (int)path[1]!);
    }

    [Fact]
    public void ToPath_MultipleOutputSlots()
    {
        var bridge = WorkflowBridge.Create(new JObject());
        var ckpt = bridge.AddNode(new CheckpointLoaderSimpleNode());

        JArray modelPath = WorkflowBridge.ToPath(ckpt.MODEL);
        JArray clipPath = WorkflowBridge.ToPath(ckpt.CLIP);
        JArray vaePath = WorkflowBridge.ToPath(ckpt.VAE);

        Assert.Equal(0, (int)modelPath[1]!);
        Assert.Equal(1, (int)clipPath[1]!);
        Assert.Equal(2, (int)vaePath[1]!);
        // All point to the same node
        Assert.Equal(ckpt.Id, (string)modelPath[0]!);
        Assert.Equal(ckpt.Id, (string)clipPath[0]!);
        Assert.Equal(ckpt.Id, (string)vaePath[0]!);
    }

    [Fact]
    public void ToPath_NoId_Throws()
    {
        var node = new VAEDecodeNode(); // not added to any graph
        Assert.Throws<InvalidOperationException>(() => WorkflowBridge.ToPath(node.IMAGE));
    }

    [Fact]
    public void ResolvePath_ValidPath_ReturnsOutput()
    {
        JObject workflow = BuildSimpleWorkflow();
        var bridge = WorkflowBridge.Create(workflow);

        INodeOutput? output = bridge.ResolvePath(new JArray("1", 0));

        Assert.NotNull(output);
        Assert.Equal("1", output.Node.Id);
        Assert.Equal(0, output.SlotIndex);
        Assert.IsType<CheckpointLoaderSimpleNode>(output.Node);
    }

    [Fact]
    public void ResolvePath_UnknownNodeId_ReturnsNull()
    {
        var bridge = WorkflowBridge.Create(new JObject());
        Assert.Null(bridge.ResolvePath(new JArray("nonexistent", 0)));
    }

    [Fact]
    public void ResolvePath_InvalidSlotIndex_ReturnsNull()
    {
        JObject workflow = BuildSimpleWorkflow();
        var bridge = WorkflowBridge.Create(workflow);

        // CheckpointLoaderSimple has slots 0, 1, 2 — slot 99 doesn't exist
        Assert.Null(bridge.ResolvePath(new JArray("1", 99)));
    }

    [Fact]
    public void ResolvePath_MalformedPath_ReturnsNull()
    {
        var bridge = WorkflowBridge.Create(new JObject());

        Assert.Null(bridge.ResolvePath(null));
        Assert.Null(bridge.ResolvePath(new JArray()));
        Assert.Null(bridge.ResolvePath(new JArray("only_one")));
        Assert.Null(bridge.ResolvePath(new JArray("a", "b", "c")));
        Assert.Null(bridge.ResolvePath(new JArray("node", "not_a_number")));
    }

    [Fact]
    public void ResolvePath_UnknownNodeUnregisteredSlot_SynthesizesOutput()
    {
        // ComfyGraph.FromWorkflow only registers UnknownNode outputs that some other
        // node references. A freshly seeded stub with no consumers has zero outputs —
        // ResolvePath must materialize the slot on demand instead of returning null.
        JObject workflow = new()
        {
            ["50"] = new JObject
            {
                ["class_type"] = "SomeUnregisteredCustomNode",
                ["inputs"] = new JObject(),
            },
        };
        var bridge = WorkflowBridge.Create(workflow);

        Assert.IsType<UnknownNode>(bridge.Graph.GetNode("50"));

        INodeOutput? output = bridge.ResolvePath(new JArray("50", 0));

        Assert.NotNull(output);
        Assert.Equal("50", output.Node.Id);
        Assert.Equal(0, output.SlotIndex);
    }

    [Fact]
    public void FindOutput_OnUnknownNodeUnregisteredSlot_MaterializesOnDemand()
    {
        // Regression: callers that hold paths to UnknownNode outputs out-of-band (e.g. SwarmUI
        // user-data) reach the slot via Graph.GetNode(id).FindOutput(slot). FromWorkflow's
        // output-discovery scan only registers slots referenced by some other node's input in
        // the JObject, so externally-held paths previously returned null. UnknownNode now
        // overrides FindOutput to materialize on miss, matching ResolvePath's semantics.
        JObject workflow = new()
        {
            ["50"] = new JObject
            {
                ["class_type"] = "SomeUnregisteredCustomNode",
                ["inputs"] = new JObject(),
            },
        };
        var bridge = WorkflowBridge.Create(workflow);
        ComfyNode node = bridge.Graph.GetNode("50")!;
        Assert.IsType<UnknownNode>(node);

        // Static type is ComfyNode — exercises the virtual dispatch, not the new-shadowed override.
        INodeOutput? output = node.FindOutput(7);

        Assert.NotNull(output);
        Assert.Equal("50", output.Node.Id);
        Assert.Equal(7, output.SlotIndex);

        // Idempotent: a second FindOutput at the same slot returns the same instance.
        Assert.Same(output, node.FindOutput(7));

        // The materialized slot is now visible through Outputs (proves AddOutput ran, not a synthetic).
        Assert.Contains(output, node.Outputs);
    }

    [Fact]
    public void ToPath_ResolvePath_RoundTrip()
    {
        JObject workflow = BuildSimpleWorkflow();
        var bridge = WorkflowBridge.Create(workflow);

        var ckpt = bridge.Graph.GetNode<CheckpointLoaderSimpleNode>("1")!;

        // ToPath → ResolvePath → same output
        JArray path = WorkflowBridge.ToPath(ckpt.VAE);
        INodeOutput? resolved = bridge.ResolvePath(path);

        Assert.NotNull(resolved);
        Assert.Same(ckpt, resolved.Node);
        Assert.Equal(2, resolved.SlotIndex); // VAE is slot 2
    }

    // ═════════════════════════════════════════════════════════════════
    //  7. Round-Trip Safety
    // ═════════════════════════════════════════════════════════════════

    [Fact]
    public void RoundTrip_CreateThenSyncAll_DeepEquality()
    {
        JObject original = BuildSimpleWorkflow();
        var bridge = WorkflowBridge.Create(original);

        bridge.SyncAll();

        // Verify each node's class_type and key inputs survived
        Assert.Equal("CheckpointLoaderSimple", bridge.Workflow["1"]!.Value<string>("class_type"));
        Assert.Equal("model.safetensors", bridge.Workflow["1"]!["inputs"]!.Value<string>("ckpt_name"));

        Assert.Equal("CLIPTextEncode", bridge.Workflow["2"]!.Value<string>("class_type"));
        Assert.Equal("a cat", bridge.Workflow["2"]!["inputs"]!.Value<string>("text"));
        JArray clipConn = (JArray)bridge.Workflow["2"]!["inputs"]!["clip"]!;
        Assert.Equal("1", (string)clipConn[0]!);
        Assert.Equal(1, (int)clipConn[1]!);

        Assert.Equal("KSampler", bridge.Workflow["3"]!.Value<string>("class_type"));
        Assert.Equal(42L, (long)bridge.Workflow["3"]!["inputs"]!["seed"]!);
        JArray modelConn = (JArray)bridge.Workflow["3"]!["inputs"]!["model"]!;
        Assert.Equal("1", (string)modelConn[0]!);
        Assert.Equal(0, (int)modelConn[1]!);

        Assert.Equal("EmptyLatentImage", bridge.Workflow["4"]!.Value<string>("class_type"));
    }

    [Fact]
    public void RoundTrip_WithUnknownNodes()
    {
        JObject workflow = new()
        {
            ["1"] = new JObject
            {
                ["class_type"] = "SomeCustomWidget",
                ["inputs"] = new JObject
                {
                    ["text"] = "hello",
                    ["count"] = 5,
                    ["upstream"] = new JArray("2", 0)
                }
            },
            ["2"] = new JObject
            {
                ["class_type"] = "AnotherCustomWidget",
                ["inputs"] = new JObject { ["value"] = 3.14 }
            }
        };

        var bridge = WorkflowBridge.Create(workflow);
        bridge.SyncAll();

        Assert.Equal("SomeCustomWidget", bridge.Workflow["1"]!.Value<string>("class_type"));
        Assert.Equal("hello", bridge.Workflow["1"]!["inputs"]!.Value<string>("text"));
        Assert.Equal(5L, (long)bridge.Workflow["1"]!["inputs"]!["count"]!);
        JArray upstreamConn = (JArray)bridge.Workflow["1"]!["inputs"]!["upstream"]!;
        Assert.Equal("2", (string)upstreamConn[0]!);
        Assert.Equal(0, (int)upstreamConn[1]!);

        Assert.Equal("AnotherCustomWidget", bridge.Workflow["2"]!.Value<string>("class_type"));
        Assert.Equal(3.14, (double)bridge.Workflow["2"]!["inputs"]!["value"]!);
    }

    [Fact]
    public void RoundTrip_AddThenSync_PreservesOriginal()
    {
        JObject workflow = BuildSimpleWorkflow();
        string originalNode1 = workflow["1"]!.ToString();
        string originalNode3 = workflow["3"]!.ToString();

        var bridge = WorkflowBridge.Create(workflow);
        bridge.AddNode(new VAEDecodeNode()); // add a new node
        bridge.SyncAll();

        // Original nodes still intact
        Assert.Equal(originalNode1, bridge.Workflow["1"]!.ToString());
        // Note: node 3's JObject may differ due to re-serialization of defaults,
        // but key fields must survive
        Assert.Equal("KSampler", bridge.Workflow["3"]!.Value<string>("class_type"));
        Assert.Equal(42L, (long)bridge.Workflow["3"]!["inputs"]!["seed"]!);
    }

    // ═════════════════════════════════════════════════════════════════
    //  8. Edge Cases
    // ═════════════════════════════════════════════════════════════════

    [Fact]
    public void MultipleBridges_SameJObject_Independent()
    {
        JObject workflow = BuildSimpleWorkflow();
        var bridge1 = WorkflowBridge.Create(workflow);
        var bridge2 = WorkflowBridge.Create(workflow);

        bridge1.AddNode(new VAEDecodeNode());

        // bridge2's graph should not see bridge1's addition
        Assert.Equal(4, bridge2.Graph.Nodes.Count);
        Assert.Equal(5, bridge1.Graph.Nodes.Count);
    }

    [Fact]
    public void WorkflowWithReservedIds_1Through99()
    {
        JObject workflow = new();
        for (int i = 1; i <= 99; i++)
        {
            workflow[$"{i}"] = new JObject
            {
                ["class_type"] = "CheckpointLoaderSimple",
                ["inputs"] = new JObject { ["ckpt_name"] = "m.safetensors" }
            };
        }
        var bridge = WorkflowBridge.Create(workflow);
        var node = bridge.AddNode(new VAEDecodeNode());

        Assert.True(int.Parse(node.Id) >= 100, $"Expected ID >= 100, got {node.Id}");
    }

    [Fact]
    public void AddNode_ThenToPath_ThenResolvePath()
    {
        var bridge = WorkflowBridge.Create(new JObject());

        var ckpt = bridge.AddNode(new CheckpointLoaderSimpleNode());
        JArray vaePath = WorkflowBridge.ToPath(ckpt.VAE);
        INodeOutput? resolved = bridge.ResolvePath(vaePath);

        Assert.NotNull(resolved);
        Assert.Same(ckpt, resolved.Node);
        Assert.Equal(2, resolved.SlotIndex);
    }

    [Fact]
    public void SyncNode_AfterRetargetConnections()
    {
        JObject workflow = BuildSimpleWorkflow();
        var bridge = WorkflowBridge.Create(workflow);

        var ckpt = bridge.Graph.GetNode<CheckpointLoaderSimpleNode>("1")!;
        var newCkpt = bridge.AddNode(new CheckpointLoaderSimpleNode());
        newCkpt.CkptName.Set("new_model.safetensors");

        // Retarget: all inputs pointing to ckpt.MODEL → newCkpt.MODEL
        int retargeted = bridge.Graph.RetargetConnections(ckpt.MODEL, newCkpt.MODEL);
        Assert.Equal(1, retargeted); // KSampler.Model

        // Sync the affected node (KSampler)
        bridge.SyncNode("3");

        JArray modelConn = (JArray)bridge.Workflow["3"]!["inputs"]!["model"]!;
        Assert.Equal(newCkpt.Id, (string)modelConn[0]!);
        Assert.Equal(0, (int)modelConn[1]!);
    }

    // ═════════════════════════════════════════════════════════════════
    //  9. Adoption Pattern Integration Tests
    // ═════════════════════════════════════════════════════════════════

    [Fact]
    public void Pattern1_QueryOnly_NoSync()
    {
        JObject workflow = BuildSimpleWorkflow();
        string originalJson = workflow.ToString();

        var bridge = WorkflowBridge.Create(workflow);
        var ckpt = bridge.Graph.GetNode<CheckpointLoaderSimpleNode>("1")!;
        var ksampler = bridge.Graph.FindNearestUpstream<CheckpointLoaderSimpleNode>(
            bridge.Graph.GetNode("3")!);

        Assert.Same(ckpt, ksampler);
        // Workflow unchanged
        Assert.Equal(originalJson, workflow.ToString());
    }

    [Fact]
    public void Pattern2_CreateAndInsert()
    {
        JObject workflow = BuildSimpleWorkflow();
        var bridge = WorkflowBridge.Create(workflow);

        var ckpt = bridge.Graph.GetNode<CheckpointLoaderSimpleNode>("1")!;
        var ksampler = bridge.Graph.GetNode<KSamplerNode>("3")!;

        // Create a typed VAEDecode connected to existing nodes
        var decode = new VAEDecodeNode();
        decode.Samples.ConnectTo(ksampler.LATENT);
        decode.Vae.ConnectTo(ckpt.VAE);
        bridge.AddNode(decode);

        // Get JArray path for WGNodeData construction
        JArray imagePath = WorkflowBridge.ToPath(decode.IMAGE);
        Assert.Equal(decode.Id, (string)imagePath[0]!);
        Assert.Equal(0, (int)imagePath[1]!);

        // Verify JObject has the correct connections
        JObject decodeObj = (JObject)bridge.Workflow[decode.Id]!;
        Assert.Equal("VAEDecode", decodeObj.Value<string>("class_type"));
        JArray samplesRef = (JArray)decodeObj["inputs"]!["samples"]!;
        Assert.Equal("3", (string)samplesRef[0]!);
        JArray vaeRef = (JArray)decodeObj["inputs"]!["vae"]!;
        Assert.Equal("1", (string)vaeRef[0]!);
        Assert.Equal(2, (int)vaeRef[1]!);
    }

    [Fact]
    public void Pattern3_RetargetAndSync()
    {
        JObject workflow = BuildSimpleWorkflow();
        var bridge = WorkflowBridge.Create(workflow);

        var ckpt = bridge.Graph.GetNode<CheckpointLoaderSimpleNode>("1")!;
        var newCkpt = bridge.AddNode(new CheckpointLoaderSimpleNode());
        newCkpt.CkptName.Set("better_model.safetensors");

        // Retarget all CLIP connections from old to new checkpoint
        int count = bridge.Graph.RetargetConnections(ckpt.CLIP, newCkpt.CLIP);
        Assert.Equal(1, count); // CLIPTextEncode

        // Sync the affected node
        bridge.SyncNode("2");

        JArray clipConn = (JArray)bridge.Workflow["2"]!["inputs"]!["clip"]!;
        Assert.Equal(newCkpt.Id, (string)clipConn[0]!);
        Assert.Equal(1, (int)clipConn[1]!); // CLIP is slot 1
    }

    [Fact]
    public void Pattern4_MixedTypedUntyped()
    {
        JObject workflow = BuildSimpleWorkflow();
        var bridge = WorkflowBridge.Create(workflow);

        // Query typed
        var ckpt = bridge.Graph.GetNode<CheckpointLoaderSimpleNode>("1")!;
        var ksampler = bridge.Graph.GetNode<KSamplerNode>("3")!;

        // Create typed node
        var decode = new VAEDecodeNode();
        decode.Samples.ConnectTo(ksampler.LATENT);
        decode.Vae.ConnectTo(ckpt.VAE);
        bridge.AddNode(decode);

        // Get path for untyped code (simulating WGNodeData construction)
        JArray imagePath = WorkflowBridge.ToPath(decode.IMAGE);

        // Old-style: create a save node directly in JObject
        string saveId = "9999";
        workflow[saveId] = new JObject
        {
            ["class_type"] = "SaveImage",
            ["inputs"] = new JObject
            {
                ["images"] = new JArray(imagePath[0], imagePath[1]),
                ["filename_prefix"] = "mixed_test"
            }
        };

        // Verify the old-style node references the typed node correctly
        JArray saveImagesRef = (JArray)workflow[saveId]!["inputs"]!["images"]!;
        Assert.Equal(decode.Id, (string)saveImagesRef[0]!);
        Assert.Equal(0, (int)saveImagesRef[1]!);
    }
}
