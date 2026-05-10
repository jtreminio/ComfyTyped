using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.Types;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ComfyTyped.Tests;

public class WorkflowBridgeTests
{
    public WorkflowBridgeTests()
    {
        NodeRegistrations.EnsureRegistered();
    }

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

    [Fact]
    public void Create_PreservesAllNodes()
    {
        JObject workflow = BuildSimpleWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

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
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        CLIPTextEncodeNode clip = bridge.Graph.GetNode<CLIPTextEncodeNode>("2")!;
        Assert.True(clip.Clip.IsConnected);
        Assert.Equal("1", clip.Clip.Connection!.Node.Id);
        Assert.Equal(1, clip.Clip.Connection!.SlotIndex);

        KSamplerNode ksampler = bridge.Graph.GetNode<KSamplerNode>("3")!;
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
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        KSamplerNode ks = bridge.Graph.GetNode<KSamplerNode>("1")!;

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
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode? node = bridge.Graph.GetNode("1");
        Assert.NotNull(node);
        Assert.IsType<UnknownNode>(node);
        Assert.Equal("MyCustomNode", node.ClassTypeName);
    }

    [Fact]
    public void Create_DoesNotCloneWorkflow()
    {
        JObject workflow = BuildSimpleWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Same(workflow, bridge.Workflow);
    }

    [Fact]
    public void Create_EmptyWorkflow()
    {
        WorkflowBridge bridge = WorkflowBridge.Create(new JObject());

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
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Single(bridge.Graph.Nodes);
        CheckpointLoaderSimpleNode ckpt = bridge.Graph.GetNode<CheckpointLoaderSimpleNode>("1")!;
        Assert.Equal("model.safetensors", ckpt.CkptName.LiteralValue);
    }

    [Fact]
    public void Create_NonNodeProperties_Ignored()
    {
        JObject workflow = BuildSimpleWorkflow();
        workflow["_meta"] = new JObject { ["version"] = "1.0" };
        workflow["prompt_id"] = "abc-123";

        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Equal(4, bridge.Graph.Nodes.Count);
        Assert.NotNull(bridge.Workflow["_meta"]);
        Assert.Equal("abc-123", bridge.Workflow.Value<string>("prompt_id"));
    }

    [Fact]
    public void AddNode_AutoId_AppearsInBoth()
    {
        WorkflowBridge bridge = WorkflowBridge.Create(new JObject());
        VAEDecodeNode node = bridge.AddNode(new VAEDecodeNode());

        Assert.NotNull(node.Id);
        Assert.NotEmpty(node.Id);
        Assert.NotNull(bridge.Graph.GetNode(node.Id));
        Assert.NotNull(bridge.Workflow[node.Id]);
        Assert.Equal("VAEDecode", bridge.Workflow[node.Id]!.Value<string>("class_type"));
    }

    [Fact]
    public void AddNode_ExplicitId_AppearsInBoth()
    {
        WorkflowBridge bridge = WorkflowBridge.Create(new JObject());
        VAEDecodeNode node = bridge.AddNode(new VAEDecodeNode(), "mynode");

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
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        VAEDecodeNode node = bridge.AddNode(new VAEDecodeNode());

        Assert.True(int.Parse(node.Id) > 50, $"Expected ID > 50, got {node.Id}");
    }

    [Fact]
    public void AddNode_AutoId_AvoidsNonNodeNumericKeys()
    {
        JObject workflow = BuildSimpleWorkflow();
        workflow["200"] = "some non-node value";

        WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        VAEDecodeNode node = bridge.AddNode(new VAEDecodeNode());

        Assert.True(int.Parse(node.Id) > 200, $"Expected ID > 200, got {node.Id}");
    }

    [Fact]
    public void AddNode_WithConnections_SerializesCorrectly()
    {
        JObject workflow = BuildSimpleWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        CheckpointLoaderSimpleNode ckpt = bridge.Graph.GetNode<CheckpointLoaderSimpleNode>("1")!;
        VAEDecodeNode decode = new VAEDecodeNode();
        decode.Vae.ConnectTo(ckpt.VAE);
        bridge.AddNode(decode);

        JArray vaeConn = (JArray)bridge.Workflow[decode.Id]!["inputs"]!["vae"]!;
        Assert.Equal("1", (string)vaeConn[0]!);
        Assert.Equal(2, (int)vaeConn[1]!); // VAE is slot 2
    }

    [Fact]
    public void ConnectToUntyped_AllowsConcreteOutputIntoMatchTypeV3Input()
    {
        // Concrete outputs may connect into ComfyMatchTypeV3 (wildcard) inputs.
        WorkflowBridge bridge = WorkflowBridge.Create(new JObject());
        CheckpointLoaderSimpleNode ckpt = bridge.AddNode(new CheckpointLoaderSimpleNode());
        EmptyLatentImageNode latent = bridge.AddNode(new EmptyLatentImageNode());
        VAEDecodeNode decode = bridge.AddNode(new VAEDecodeNode());
        decode.Vae.ConnectTo(ckpt.VAE);
        decode.Samples.ConnectTo(latent.LATENT);

        ResizeImageMaskNodeNode resize = bridge.AddNode(new ResizeImageMaskNodeNode());
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
        // Null output is a no-op (false); a real output connects (true).
        WorkflowBridge bridge = WorkflowBridge.Create(new JObject());
        ResizeImageMaskNodeNode resize = bridge.AddNode(new ResizeImageMaskNodeNode());
        resize.ResizeType.Set("pixels");

        Assert.False(((INodeInput)resize.Input).TryConnectToUntyped(null));
        Assert.False(resize.Input.IsConnected);
        bridge.SyncAll();
        Assert.Null(bridge.Workflow[resize.Id]!["inputs"]!["input"]);

        CheckpointLoaderSimpleNode ckpt = bridge.AddNode(new CheckpointLoaderSimpleNode());
        EmptyLatentImageNode latent = bridge.AddNode(new EmptyLatentImageNode());
        VAEDecodeNode decode = bridge.AddNode(new VAEDecodeNode());
        decode.Vae.ConnectTo(ckpt.VAE);
        decode.Samples.ConnectTo(latent.LATENT);
        Assert.True(((INodeInput)resize.Input).TryConnectToUntyped(decode.IMAGE));
        Assert.True(resize.Input.IsConnected);

        Assert.False(((INodeInput)resize.Input).TryConnectToUntyped(null));
        Assert.True(resize.Input.IsConnected);
    }

    [Fact]
    public void ConnectToUntyped_AllowsMatchTypeV3OutputIntoConcreteInput()
    {
        // Wildcard output may connect into a concrete-typed input.
        WorkflowBridge bridge = WorkflowBridge.Create(new JObject());
        CheckpointLoaderSimpleNode ckpt = bridge.AddNode(new CheckpointLoaderSimpleNode());
        ResizeImageMaskNodeNode resize = bridge.AddNode(new ResizeImageMaskNodeNode());
        resize.ResizeType.Set("pixels");

        VAEEncodeNode encode = bridge.AddNode(new VAEEncodeNode());
        encode.Vae.ConnectTo(ckpt.VAE);
        ((INodeInput)encode.Pixels).ConnectToUntyped(resize.Resized);

        bridge.SyncAll();

        JArray pixelsRef = (JArray)bridge.Workflow[encode.Id]!["inputs"]!["pixels"]!;
        Assert.Equal(resize.Id, (string)pixelsRef[0]!);
    }

    [Fact]
    public void AddNode_WithLiterals_SerializesCorrectly()
    {
        WorkflowBridge bridge = WorkflowBridge.Create(new JObject());

        EmptyLatentImageNode emptyLatent = new EmptyLatentImageNode();
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
        WorkflowBridge bridge = WorkflowBridge.Create(new JObject());
        VAEDecodeNode n1 = bridge.AddNode(new VAEDecodeNode());
        VAEDecodeNode n2 = bridge.AddNode(new VAEDecodeNode());
        VAEDecodeNode n3 = bridge.AddNode(new VAEDecodeNode());

        int id1 = int.Parse(n1.Id);
        int id2 = int.Parse(n2.Id);
        int id3 = int.Parse(n3.Id);

        Assert.True(id2 > id1);
        Assert.True(id3 > id2);
    }

    [Fact]
    public void AddNode_ReturnsNodeWithId()
    {
        WorkflowBridge bridge = WorkflowBridge.Create(new JObject());
        CheckpointLoaderSimpleNode node = bridge.AddNode(new CheckpointLoaderSimpleNode());

        Assert.NotNull(node.Id);
        Assert.NotEmpty(node.Id);
        Assert.IsType<CheckpointLoaderSimpleNode>(node);
    }

    [Fact]
    public void RemoveNode_ById_RemovesFromBoth()
    {
        JObject workflow = BuildSimpleWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        bool removed = bridge.RemoveNode("4");

        Assert.True(removed);
        Assert.Null(bridge.Graph.GetNode("4"));
        Assert.Null(bridge.Workflow["4"]);
    }

    [Fact]
    public void RemoveNode_ByNode_RemovesFromBoth()
    {
        JObject workflow = BuildSimpleWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        ComfyNode node = bridge.Graph.GetNode("4")!;

        bool removed = bridge.RemoveNode(node);

        Assert.True(removed);
        Assert.Null(bridge.Graph.GetNode("4"));
        Assert.Null(bridge.Workflow["4"]);
    }

    [Fact]
    public void RemoveAllNodes_RemovesEveryNodeFromBoth()
    {
        JObject workflow = BuildSimpleWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

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
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        bridge.RemoveAllNodes();

        Assert.NotNull(bridge.Workflow["_meta"]);
        Assert.Equal("1.0", bridge.Workflow["_meta"]!.Value<string>("version"));
    }

    [Fact]
    public void RemoveAllNodes_OnEmptyBridge_ReturnsZero()
    {
        WorkflowBridge bridge = WorkflowBridge.Create(new JObject());
        Assert.Equal(0, bridge.RemoveAllNodes());
    }

    [Fact]
    public void RemoveNode_NonExistent_ReturnsFalse()
    {
        WorkflowBridge bridge = WorkflowBridge.Create(new JObject());
        Assert.False(bridge.RemoveNode("999"));
    }

    [Fact]
    public void RemoveNode_PreservesOtherNodes()
    {
        JObject workflow = BuildSimpleWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

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
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        bridge.RemoveNode("4");

        Assert.NotNull(bridge.Workflow["_meta"]);
        Assert.Equal("1.0", bridge.Workflow["_meta"]!.Value<string>("version"));
    }

    [Fact]
    public void AutoSync_LiteralMutationOnAddedNode_FlushesImmediately()
    {
        WorkflowBridge bridge = WorkflowBridge.Create(new JObject());
        KSamplerNode ksampler = bridge.AddNode(new KSamplerNode());

        ksampler.Seed.Set(123L);

        Assert.Equal(123L, (long)bridge.Workflow[ksampler.Id]!["inputs"]!["seed"]!);
    }

    [Fact]
    public void AutoSync_LiteralMutationOnLoadedNode_FlushesImmediately()
    {
        JObject workflow = BuildSimpleWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        KSamplerNode ksampler = bridge.Graph.GetNode<KSamplerNode>("3")!;
        ksampler.Seed.Set(555L);

        Assert.Equal(555L, (long)bridge.Workflow["3"]!["inputs"]!["seed"]!);
    }

    [Fact]
    public void AutoSync_ConnectionRetarget_FlushesImmediately()
    {
        JObject workflow = BuildSimpleWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        KSamplerNode ksampler = bridge.Graph.GetNode<KSamplerNode>("3")!;
        EmptyLatentImageNode newLatent = bridge.AddNode(new EmptyLatentImageNode());
        ksampler.LatentImage.ConnectTo(newLatent.LATENT);

        JArray ref_ = (JArray)bridge.Workflow["3"]!["inputs"]!["latent_image"]!;
        Assert.Equal(newLatent.Id, (string)ref_[0]!);
    }

    [Fact]
    public void AutoSync_Clear_RemovesPropertyFromInputs()
    {
        JObject workflow = BuildSimpleWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        KSamplerNode ksampler = bridge.Graph.GetNode<KSamplerNode>("3")!;
        Assert.NotNull(bridge.Workflow["3"]!["inputs"]!["seed"]);

        ksampler.Seed.Clear();

        Assert.Null(bridge.Workflow["3"]!["inputs"]!["seed"]);
    }

    [Fact]
    public void AutoSync_PreservesOuterNodeJObjectReference()
    {
        WorkflowBridge bridge = WorkflowBridge.Create(new JObject());
        KSamplerNode ksampler = bridge.AddNode(new KSamplerNode());
        JObject heldRef = (JObject)bridge.Workflow[ksampler.Id]!;

        ksampler.Seed.Set(777L);

        Assert.Same(heldRef, bridge.Workflow[ksampler.Id]);
        Assert.Equal(777L, (long)heldRef["inputs"]!["seed"]!);
    }

    [Fact]
    public void AutoSync_AfterRemoveNode_StopsTracking()
    {
        WorkflowBridge bridge = WorkflowBridge.Create(new JObject());
        KSamplerNode ksampler = bridge.AddNode(new KSamplerNode());
        bridge.RemoveNode(ksampler);

        ksampler.Seed.Set(42L);

        Assert.Null(bridge.Workflow[ksampler.Id]);
    }

    [Fact]
    public void AutoSync_UnknownNodeFirstMutation_RebuildsInputsFromTypedSlots()
    {
        // First typed mutation clears RawInputs and rebuilds workflow inputs.
        JObject workflow = new()
        {
            ["50"] = new JObject
            {
                ["class_type"] = "SomeCustomWidget",
                ["inputs"] = new JObject { ["text"] = "old", ["count"] = 1 }
            }
        };
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        UnknownNode unknown = (UnknownNode)bridge.Graph.GetNode("50")!;

        unknown.GetInput("text").Set("new");

        Assert.Equal("new", bridge.Workflow["50"]!["inputs"]!.Value<string>("text"));
        Assert.Equal(1L, (long)bridge.Workflow["50"]!["inputs"]!["count"]!);
        Assert.Null(unknown.RawInputs);
    }

    [Fact]
    public void AutoSync_LiteralAfterWildcardConnection_ReplacesConnection()
    {
        // Literal set after ConnectToUntyped must drop the connection and write the literal.
        JObject workflow = new()
        {
            ["50"] = new JObject
            {
                ["class_type"] = "SomeWildcardSource",
                ["inputs"] = new JObject(),
            },
        };
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        UnknownNode unknown = (UnknownNode)bridge.Graph.GetNode("50")!;
        NodeOutput<ComfyTyped.Types.AnyType> wildcard = unknown.GetOutput(0);

        KSamplerNode ksampler = bridge.AddNode(new KSamplerNode());
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
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        KSamplerNode ksampler = bridge.Graph.GetNode<KSamplerNode>("3")!;

        bridge.Dispose();
        bridge.Dispose();

        ksampler.Seed.Set(999L);

        Assert.Equal(42L, (long)bridge.Workflow["3"]!["inputs"]!["seed"]!);
    }

    [Fact]
    public void AutoSync_ConnectionFromAddedNodeUsesAssignedId()
    {
        WorkflowBridge bridge = WorkflowBridge.Create(new JObject());
        CheckpointLoaderSimpleNode ckpt = bridge.AddNode(new CheckpointLoaderSimpleNode());
        KSamplerNode sampler = bridge.AddNode(new KSamplerNode());

        sampler.Model.ConnectTo(ckpt.MODEL);

        JArray modelRef = (JArray)bridge.Workflow[sampler.Id]!["inputs"]!["model"]!;
        Assert.Equal(ckpt.Id, (string)modelRef[0]!);
        Assert.Equal(0, (int)modelRef[1]!);
    }

    // ckpt → sampler → decode; VAE from ckpt also feeds decode.
    private static (WorkflowBridge bridge, CheckpointLoaderSimpleNode ckpt, KSamplerNode sampler, VAEDecodeNode decode) BuildLinearChain()
    {
        WorkflowBridge bridge = WorkflowBridge.Create(new JObject());
        CheckpointLoaderSimpleNode ckpt = bridge.AddNode(new CheckpointLoaderSimpleNode());
        EmptyLatentImageNode emptyLatent = bridge.AddNode(new EmptyLatentImageNode());
        KSamplerNode sampler = bridge.AddNode(new KSamplerNode());
        sampler.Model.ConnectTo(ckpt.MODEL);
        sampler.LatentImage.ConnectTo(emptyLatent.LATENT);
        VAEDecodeNode decode = bridge.AddNode(new VAEDecodeNode());
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
        (WorkflowBridge bridge, CheckpointLoaderSimpleNode _, KSamplerNode sampler, VAEDecodeNode decode) = BuildLinearChain();
        string oldId = sampler.Id;

        KSamplerNode replacement = bridge.AddNode(new KSamplerNode());

        foreach (INodeOutput output in sampler.Outputs)
        {
            INodeOutput? to = replacement.FindOutput(output.SlotIndex);
            if (to is not null)
            {
                bridge.Graph.RetargetConnections(output, to);
            }
        }
        bridge.RemoveNode(sampler);

        Assert.Null(bridge.Workflow[oldId]);
        Assert.False(WorkflowReferencesId(bridge.Workflow, oldId),
            "JObject still has dangling JArray refs to the removed node ID.");

        Assert.Null(bridge.Graph.GetNode(oldId));

        JArray samplesRef = (JArray)bridge.Workflow[decode.Id]!["inputs"]!["samples"]!;
        Assert.Equal(replacement.Id, (string)samplesRef[0]!);
        Assert.Equal(0, (int)samplesRef[1]!);
        Assert.Same(replacement, decode.Samples.Connection!.Node);

        replacement.Seed.Set(12345L);
        Assert.Equal(12345L, (long)bridge.Workflow[replacement.Id]!["inputs"]!["seed"]!);
    }

    [Fact]
    public void NaiveDelete_OfMiddleNode_LeavesDanglingJArrayInConsumers()
    {
        // RemoveNode does not cascade-clean consumers; see ComfyGraph.RemoveNode / WorkflowBridge docs.
        (WorkflowBridge bridge, CheckpointLoaderSimpleNode _, KSamplerNode sampler, VAEDecodeNode decode) = BuildLinearChain();
        string oldId = sampler.Id;

        bridge.RemoveNode(sampler);

        Assert.Null(bridge.Workflow[oldId]);

        JArray samplesRef = (JArray)bridge.Workflow[decode.Id]!["inputs"]!["samples"]!;
        Assert.Equal(oldId, (string)samplesRef[0]!);
        Assert.Equal(0, (int)samplesRef[1]!);
        Assert.True(WorkflowReferencesId(bridge.Workflow, oldId),
            "Expected naive RemoveNode to leave a dangling JArray ref in consumers.");

        Assert.True(decode.Samples.IsConnected);
        Assert.Same(sampler, decode.Samples.Connection!.Node);
    }

    [Fact]
    public void SyncNode_AfterConnectionChange_UpdatesJObject()
    {
        JObject workflow = BuildSimpleWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        KSamplerNode ksampler = bridge.Graph.GetNode<KSamplerNode>("3")!;

        JArray initialRef = (JArray)bridge.Workflow["3"]!["inputs"]!["latent_image"]!;
        Assert.Equal("4", (string)initialRef[0]!);

        EmptyLatentImageNode newLatent = bridge.AddNode(new EmptyLatentImageNode());
        ksampler.LatentImage.ConnectTo(newLatent.LATENT);

        JArray autoSynced = (JArray)bridge.Workflow["3"]!["inputs"]!["latent_image"]!;
        Assert.Equal(newLatent.Id, (string)autoSynced[0]!);

        bridge.SyncNode(ksampler);
        JArray afterSync = (JArray)bridge.Workflow["3"]!["inputs"]!["latent_image"]!;
        Assert.Equal(newLatent.Id, (string)afterSync[0]!);
    }

    [Fact]
    public void SyncNode_AfterLiteralChange_UpdatesJObject()
    {
        JObject workflow = BuildSimpleWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        KSamplerNode ksampler = bridge.Graph.GetNode<KSamplerNode>("3")!;
        ksampler.Seed.Set(999L);

        bridge.SyncNode(ksampler);

        Assert.Equal(999L, (long)bridge.Workflow["3"]!["inputs"]!["seed"]!);
    }

    [Fact]
    public void SyncNode_ById_Works()
    {
        JObject workflow = BuildSimpleWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        KSamplerNode ksampler = bridge.Graph.GetNode<KSamplerNode>("3")!;
        ksampler.Seed.Set(777L);

        bridge.SyncNode("3");

        Assert.Equal(777L, (long)bridge.Workflow["3"]!["inputs"]!["seed"]!);
    }

    [Fact]
    public void SyncNode_UnknownId_Throws()
    {
        WorkflowBridge bridge = WorkflowBridge.Create(new JObject());

        Assert.Throws<KeyNotFoundException>(() => bridge.SyncNode("nonexistent"));
    }

    [Fact]
    public void SyncNode_DoesNotAffectOtherNodes()
    {
        JObject workflow = BuildSimpleWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        string originalNode1 = bridge.Workflow["1"]!.ToString();

        KSamplerNode ksampler = bridge.Graph.GetNode<KSamplerNode>("3")!;
        ksampler.Seed.Set(999L);
        bridge.SyncNode("3");

        Assert.Equal(originalNode1, bridge.Workflow["1"]!.ToString());
    }

    [Fact]
    public void SyncAll_AfterMultipleMutations_UpdatesAll()
    {
        JObject workflow = BuildSimpleWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        CheckpointLoaderSimpleNode ckpt = bridge.Graph.GetNode<CheckpointLoaderSimpleNode>("1")!;
        ckpt.CkptName.Set("other_model.safetensors");

        KSamplerNode ksampler = bridge.Graph.GetNode<KSamplerNode>("3")!;
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
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        bridge.SyncAll();

        Assert.Equal("2.0", bridge.Workflow["_meta"]!.Value<string>("version"));
        Assert.Equal("xyz-789", bridge.Workflow.Value<string>("prompt_id"));
    }

    [Fact]
    public void SyncAll_RemovesDeletedNodes()
    {
        JObject workflow = BuildSimpleWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        bridge.Graph.RemoveNode("4");
        Assert.NotNull(bridge.Workflow["4"]);

        bridge.SyncAll();

        Assert.Null(bridge.Workflow["4"]);
    }

    [Fact]
    public void SyncAll_AddsNewNodes()
    {
        JObject workflow = BuildSimpleWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        VAEDecodeNode newNode = new VAEDecodeNode();
        bridge.Graph.AddNode(newNode);

        Assert.Null(bridge.Workflow[newNode.Id]);

        bridge.SyncAll();

        Assert.NotNull(bridge.Workflow[newNode.Id]);
        Assert.Equal("VAEDecode", bridge.Workflow[newNode.Id]!.Value<string>("class_type"));
    }

    [Fact]
    public void SyncAll_Idempotent()
    {
        JObject workflow = BuildSimpleWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        bridge.SyncAll();
        string after1 = bridge.Workflow.ToString();

        bridge.SyncAll();
        string after2 = bridge.Workflow.ToString();

        Assert.Equal(after1, after2);
    }

    [Fact]
    public void ToPath_ReturnsCorrectJArray()
    {
        WorkflowBridge bridge = WorkflowBridge.Create(new JObject());
        VAEDecodeNode node = bridge.AddNode(new VAEDecodeNode());

        JArray path = WorkflowBridge.ToPath(node.IMAGE);

        Assert.Equal(node.Id, (string)path[0]!);
        Assert.Equal(0, (int)path[1]!);
    }

    [Fact]
    public void ToPath_MultipleOutputSlots()
    {
        WorkflowBridge bridge = WorkflowBridge.Create(new JObject());
        CheckpointLoaderSimpleNode ckpt = bridge.AddNode(new CheckpointLoaderSimpleNode());

        JArray modelPath = WorkflowBridge.ToPath(ckpt.MODEL);
        JArray clipPath = WorkflowBridge.ToPath(ckpt.CLIP);
        JArray vaePath = WorkflowBridge.ToPath(ckpt.VAE);

        Assert.Equal(0, (int)modelPath[1]!);
        Assert.Equal(1, (int)clipPath[1]!);
        Assert.Equal(2, (int)vaePath[1]!);
        Assert.Equal(ckpt.Id, (string)modelPath[0]!);
        Assert.Equal(ckpt.Id, (string)clipPath[0]!);
        Assert.Equal(ckpt.Id, (string)vaePath[0]!);
    }

    [Fact]
    public void ToPath_NoId_Throws()
    {
        VAEDecodeNode node = new VAEDecodeNode();
        Assert.Throws<InvalidOperationException>(() => WorkflowBridge.ToPath(node.IMAGE));
    }

    [Fact]
    public void ResolvePath_ValidPath_ReturnsOutput()
    {
        JObject workflow = BuildSimpleWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        INodeOutput? output = bridge.ResolvePath(new JArray("1", 0));

        Assert.NotNull(output);
        Assert.Equal("1", output.Node.Id);
        Assert.Equal(0, output.SlotIndex);
        Assert.IsType<CheckpointLoaderSimpleNode>(output.Node);
    }

    [Fact]
    public void ResolvePath_UnknownNodeId_ReturnsNull()
    {
        WorkflowBridge bridge = WorkflowBridge.Create(new JObject());
        Assert.Null(bridge.ResolvePath(new JArray("nonexistent", 0)));
    }

    [Fact]
    public void ResolvePath_InvalidSlotIndex_ReturnsNull()
    {
        JObject workflow = BuildSimpleWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        // CheckpointLoaderSimple has slots 0, 1, 2 — slot 99 doesn't exist
        Assert.Null(bridge.ResolvePath(new JArray("1", 99)));
    }

    [Fact]
    public void ResolvePath_MalformedPath_ReturnsNull()
    {
        WorkflowBridge bridge = WorkflowBridge.Create(new JObject());

        Assert.Null(bridge.ResolvePath(null));
        Assert.Null(bridge.ResolvePath(new JArray()));
        Assert.Null(bridge.ResolvePath(new JArray("only_one")));
        Assert.Null(bridge.ResolvePath(new JArray("a", "b", "c")));
        Assert.Null(bridge.ResolvePath(new JArray("node", "not_a_number")));
    }

    [Fact]
    public void ResolvePath_UnknownNodeUnregisteredSlot_SynthesizesOutput()
    {
        // UnknownNode stubs with no incoming refs still need on-demand output slots for ResolvePath.
        JObject workflow = new()
        {
            ["50"] = new JObject
            {
                ["class_type"] = "SomeUnregisteredCustomNode",
                ["inputs"] = new JObject(),
            },
        };
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.IsType<UnknownNode>(bridge.Graph.GetNode("50"));

        INodeOutput? output = bridge.ResolvePath(new JArray("50", 0));

        Assert.NotNull(output);
        Assert.Equal("50", output.Node.Id);
        Assert.Equal(0, output.SlotIndex);
    }

    [Fact]
    public void FindOutput_OnUnknownNodeUnregisteredSlot_MaterializesOnDemand()
    {
        // UnknownNode.FindOutput materializes slots not discovered during FromWorkflow.
        JObject workflow = new()
        {
            ["50"] = new JObject
            {
                ["class_type"] = "SomeUnregisteredCustomNode",
                ["inputs"] = new JObject(),
            },
        };
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        ComfyNode node = bridge.Graph.GetNode("50")!;
        Assert.IsType<UnknownNode>(node);

        INodeOutput? output = node.FindOutput(7);

        Assert.NotNull(output);
        Assert.Equal("50", output.Node.Id);
        Assert.Equal(7, output.SlotIndex);

        Assert.Same(output, node.FindOutput(7));

        Assert.Contains(output, node.Outputs);
    }

    [Fact]
    public void ToPath_ResolvePath_RoundTrip()
    {
        JObject workflow = BuildSimpleWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        CheckpointLoaderSimpleNode ckpt = bridge.Graph.GetNode<CheckpointLoaderSimpleNode>("1")!;

        JArray path = WorkflowBridge.ToPath(ckpt.VAE);
        INodeOutput? resolved = bridge.ResolvePath(path);

        Assert.NotNull(resolved);
        Assert.Same(ckpt, resolved.Node);
        Assert.Equal(2, resolved.SlotIndex); // VAE is slot 2
    }

    [Fact]
    public void ResolvePathT_MatchingType_ReturnsTypedOutput()
    {
        JObject workflow = BuildSimpleWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        NodeOutput<VaeType>? vae = bridge.ResolvePath<VaeType>(new JArray("1", 2));

        Assert.NotNull(vae);
        Assert.Equal("1", vae.Node.Id);
        Assert.Equal(2, vae.SlotIndex);
        VAEDecodeNode decode = bridge.AddNode(new VAEDecodeNode());
        decode.Vae.ConnectTo(vae);
        Assert.Same(vae, decode.Vae.TypedConnection);
    }

    [Fact]
    public void ResolvePathT_TypeMismatch_Throws()
    {
        JObject workflow = BuildSimpleWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => bridge.ResolvePath<VaeType>(new JArray("1", 0)));

        Assert.Contains("MODEL", ex.Message);
        Assert.Contains("VAE", ex.Message);
    }

    [Fact]
    public void ResolvePathT_NullOrUnresolvedPath_ReturnsNull()
    {
        WorkflowBridge bridge = WorkflowBridge.Create(BuildSimpleWorkflow());

        Assert.Null(bridge.ResolvePath<VaeType>(null));
        Assert.Null(bridge.ResolvePath<VaeType>(new JArray()));
        Assert.Null(bridge.ResolvePath<VaeType>(new JArray("nonexistent", 0)));
        Assert.Null(bridge.ResolvePath<VaeType>(new JArray("1", 99)));
    }

    [Fact]
    public void ResolvePathT_UnknownNodeOutput_ThrowsForConcreteT()
    {
        // ResolvePath<T> rejects AnyType/wildcard outputs; use non-generic ResolvePath + untyped connect.
        JObject workflow = new()
        {
            ["50"] = new JObject
            {
                ["class_type"] = "SomeUnregisteredCustomNode",
                ["inputs"] = new JObject(),
            },
        };
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Throws<InvalidOperationException>(
            () => bridge.ResolvePath<ImageType>(new JArray("50", 0)));

        INodeOutput? wildcard = bridge.ResolvePath(new JArray("50", 0));
        Assert.NotNull(wildcard);
        Assert.Equal("*", wildcard.TypeName);
    }

    [Fact]
    public void RoundTrip_CreateThenSyncAll_DeepEquality()
    {
        JObject original = BuildSimpleWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(original);

        bridge.SyncAll();

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

        WorkflowBridge bridge = WorkflowBridge.Create(workflow);
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

        WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        bridge.AddNode(new VAEDecodeNode());
        bridge.SyncAll();

        Assert.Equal(originalNode1, bridge.Workflow["1"]!.ToString());
        Assert.Equal("KSampler", bridge.Workflow["3"]!.Value<string>("class_type"));
        Assert.Equal(42L, (long)bridge.Workflow["3"]!["inputs"]!["seed"]!);
    }

    [Fact]
    public void MultipleBridges_SameJObject_Independent()
    {
        JObject workflow = BuildSimpleWorkflow();
        WorkflowBridge bridge1 = WorkflowBridge.Create(workflow);
        WorkflowBridge bridge2 = WorkflowBridge.Create(workflow);

        bridge1.AddNode(new VAEDecodeNode());

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
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        VAEDecodeNode node = bridge.AddNode(new VAEDecodeNode());

        Assert.True(int.Parse(node.Id) >= 100, $"Expected ID >= 100, got {node.Id}");
    }

    [Fact]
    public void AddNode_ThenToPath_ThenResolvePath()
    {
        WorkflowBridge bridge = WorkflowBridge.Create(new JObject());

        CheckpointLoaderSimpleNode ckpt = bridge.AddNode(new CheckpointLoaderSimpleNode());
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
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        CheckpointLoaderSimpleNode ckpt = bridge.Graph.GetNode<CheckpointLoaderSimpleNode>("1")!;
        CheckpointLoaderSimpleNode newCkpt = bridge.AddNode(new CheckpointLoaderSimpleNode());
        newCkpt.CkptName.Set("new_model.safetensors");

        int retargeted = bridge.Graph.RetargetConnections(ckpt.MODEL, newCkpt.MODEL);
        Assert.Equal(1, retargeted);

        bridge.SyncNode("3");

        JArray modelConn = (JArray)bridge.Workflow["3"]!["inputs"]!["model"]!;
        Assert.Equal(newCkpt.Id, (string)modelConn[0]!);
        Assert.Equal(0, (int)modelConn[1]!);
    }

    [Fact]
    public void Pattern1_QueryOnly_NoSync()
    {
        JObject workflow = BuildSimpleWorkflow();
        string originalJson = workflow.ToString();

        WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        CheckpointLoaderSimpleNode ckpt = bridge.Graph.GetNode<CheckpointLoaderSimpleNode>("1")!;
        CheckpointLoaderSimpleNode? ksampler = bridge.Graph.FindNearestUpstream<CheckpointLoaderSimpleNode>(
            bridge.Graph.GetNode("3")!);

        Assert.Same(ckpt, ksampler);
        Assert.Equal(originalJson, workflow.ToString());
    }

    [Fact]
    public void Pattern2_CreateAndInsert()
    {
        JObject workflow = BuildSimpleWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        CheckpointLoaderSimpleNode ckpt = bridge.Graph.GetNode<CheckpointLoaderSimpleNode>("1")!;
        KSamplerNode ksampler = bridge.Graph.GetNode<KSamplerNode>("3")!;

        VAEDecodeNode decode = new VAEDecodeNode();
        decode.Samples.ConnectTo(ksampler.LATENT);
        decode.Vae.ConnectTo(ckpt.VAE);
        bridge.AddNode(decode);

        JArray imagePath = WorkflowBridge.ToPath(decode.IMAGE);
        Assert.Equal(decode.Id, (string)imagePath[0]!);
        Assert.Equal(0, (int)imagePath[1]!);

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
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        CheckpointLoaderSimpleNode ckpt = bridge.Graph.GetNode<CheckpointLoaderSimpleNode>("1")!;
        CheckpointLoaderSimpleNode newCkpt = bridge.AddNode(new CheckpointLoaderSimpleNode());
        newCkpt.CkptName.Set("better_model.safetensors");

        int count = bridge.Graph.RetargetConnections(ckpt.CLIP, newCkpt.CLIP);
        Assert.Equal(1, count);

        bridge.SyncNode("2");

        JArray clipConn = (JArray)bridge.Workflow["2"]!["inputs"]!["clip"]!;
        Assert.Equal(newCkpt.Id, (string)clipConn[0]!);
        Assert.Equal(1, (int)clipConn[1]!);
    }

    [Fact]
    public void Pattern4_MixedTypedUntyped()
    {
        JObject workflow = BuildSimpleWorkflow();
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        CheckpointLoaderSimpleNode ckpt = bridge.Graph.GetNode<CheckpointLoaderSimpleNode>("1")!;
        KSamplerNode ksampler = bridge.Graph.GetNode<KSamplerNode>("3")!;

        VAEDecodeNode decode = new VAEDecodeNode();
        decode.Samples.ConnectTo(ksampler.LATENT);
        decode.Vae.ConnectTo(ckpt.VAE);
        bridge.AddNode(decode);

        JArray imagePath = WorkflowBridge.ToPath(decode.IMAGE);

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

        JArray saveImagesRef = (JArray)workflow[saveId]!["inputs"]!["images"]!;
        Assert.Equal(decode.Id, (string)saveImagesRef[0]!);
        Assert.Equal(0, (int)saveImagesRef[1]!);
    }
}
