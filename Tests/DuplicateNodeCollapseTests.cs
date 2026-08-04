using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ComfyTyped.Tests;

/// <summary>
/// Collapsing duplicates is what lets two authors build into one workflow without agreeing on
/// anything: a host builds its chain, an extension builds beside it, and the graph itself is the
/// only thing they share.
/// </summary>
public class DuplicateNodeCollapseTests
{
    public DuplicateNodeCollapseTests() => NodeRegistrations.EnsureRegistered();

    private static JObject Node(string classType, JObject inputs) => new()
    {
        ["class_type"] = classType,
        ["inputs"] = inputs,
    };

    /// <summary>
    /// The shape this exists for: a second author rebuilds an encoder the host already built, and
    /// everything reading the copy is moved onto the original.
    /// </summary>
    [Fact]
    public void An_identical_node_is_merged_and_its_consumers_retargeted()
    {
        JObject workflow = new()
        {
            ["1"] = Node("CheckpointLoaderSimple", new JObject { ["ckpt_name"] = "m.safetensors" }),
            ["6"] = Node("CLIPTextEncode",
                new JObject { ["text"] = "a cat", ["clip"] = new JArray("1", 1) }),
            ["108"] = Node("CLIPTextEncode",
                new JObject { ["text"] = "a cat", ["clip"] = new JArray("1", 1) }),
            ["200"] = Node("KSampler", new JObject { ["positive"] = new JArray("108", 0) }),
        };
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        IReadOnlyDictionary<string, string> merged = DuplicateNodeCollapse.Collapse(bridge);

        Assert.Equal("6", Assert.Contains("108", merged));
        Assert.False(workflow.ContainsKey("108"));
        Assert.Equal(new JArray("6", 0), workflow["200"]!["inputs"]!["positive"]);
    }

    /// <summary>
    /// Nodes alike in every literal but reading different upstreams are different computations.
    /// Comparing the connections is what keeps two clips' identically-configured samplers apart.
    /// </summary>
    [Fact]
    public void Nodes_reading_different_upstreams_are_not_merged()
    {
        JObject workflow = new()
        {
            ["1"] = Node("CheckpointLoaderSimple", new JObject { ["ckpt_name"] = "a.safetensors" }),
            ["2"] = Node("CheckpointLoaderSimple", new JObject { ["ckpt_name"] = "b.safetensors" }),
            ["6"] = Node("CLIPTextEncode",
                new JObject { ["text"] = "a cat", ["clip"] = new JArray("1", 1) }),
            ["7"] = Node("CLIPTextEncode",
                new JObject { ["text"] = "a cat", ["clip"] = new JArray("2", 1) }),
        };
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Empty(DuplicateNodeCollapse.Collapse(bridge));
        Assert.True(workflow.ContainsKey("7"));
    }

    /// <summary>
    /// Merging one pair can reveal the next: two encoders differ only in which duplicate loader
    /// they read, and become identical the moment those loaders become one. A single pass would
    /// stop after the loaders.
    /// </summary>
    [Fact]
    public void A_merge_that_reveals_another_duplicate_is_followed_through()
    {
        JObject workflow = new()
        {
            ["1"] = Node("CheckpointLoaderSimple", new JObject { ["ckpt_name"] = "m.safetensors" }),
            ["2"] = Node("CheckpointLoaderSimple", new JObject { ["ckpt_name"] = "m.safetensors" }),
            ["6"] = Node("CLIPTextEncode",
                new JObject { ["text"] = "a cat", ["clip"] = new JArray("1", 1) }),
            ["7"] = Node("CLIPTextEncode",
                new JObject { ["text"] = "a cat", ["clip"] = new JArray("2", 1) }),
            ["200"] = Node("KSampler", new JObject { ["positive"] = new JArray("7", 0) }),
        };
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        IReadOnlyDictionary<string, string> merged = DuplicateNodeCollapse.Collapse(bridge);

        Assert.Equal(2, merged.Count);
        Assert.Equal("1", merged["2"]);
        Assert.Equal("6", merged["7"]);
        Assert.Equal(new JArray("6", 0), workflow["200"]!["inputs"]!["positive"]);
    }

    /// <summary>
    /// Which node survives is the caller's to decide: an id a consumer outside the graph depends on
    /// has to be the one that stays, whatever its number.
    /// </summary>
    [Fact]
    public void The_caller_chooses_which_of_two_equal_nodes_survives()
    {
        JObject workflow = new()
        {
            ["1"] = Node("CheckpointLoaderSimple", new JObject { ["ckpt_name"] = "m.safetensors" }),
            ["6"] = Node("CLIPTextEncode",
                new JObject { ["text"] = "a cat", ["clip"] = new JArray("1", 1) }),
            ["108"] = Node("CLIPTextEncode",
                new JObject { ["text"] = "a cat", ["clip"] = new JArray("1", 1) }),
        };
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        IReadOnlyDictionary<string, string> merged = DuplicateNodeCollapse.Collapse(
            bridge,
            prefer: node => node.Id == "108" ? 0 : 1);

        Assert.Equal("108", merged["6"]);
        Assert.True(workflow.ContainsKey("108"));
    }

    /// <summary>A merged node's own redirect follows it when it is merged again in a later round,
    /// so every entry names a node that is still in the graph.</summary>
    [Fact]
    public void Redirects_point_at_the_node_that_actually_survived()
    {
        JObject workflow = new()
        {
            ["1"] = Node("EmptyLatentImage",
                new JObject { ["width"] = 512, ["height"] = 512, ["batch_size"] = 1 }),
            ["2"] = Node("EmptyLatentImage",
                new JObject { ["width"] = 512, ["height"] = 512, ["batch_size"] = 1 }),
            ["3"] = Node("EmptyLatentImage",
                new JObject { ["width"] = 512, ["height"] = 512, ["batch_size"] = 1 }),
        };
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        IReadOnlyDictionary<string, string> merged = DuplicateNodeCollapse.Collapse(bridge);

        Assert.All(merged.Values, id => Assert.Equal("1", id));
        Assert.Single(bridge.Graph.Nodes);
    }

    /// <summary>
    /// A node with no typed binding materializes its slots as they are discovered, so its outputs
    /// sit in reference order rather than slot order. Retargeting by list position rather than slot
    /// index moves a consumer onto the wrong output — silently, because the slot exists.
    /// </summary>
    [Fact]
    public void An_untyped_nodes_outputs_are_retargeted_by_slot_not_by_position()
    {
        JObject workflow = new()
        {
            ["1"] = Node("MyCustomPack_Thing", new JObject()),
            ["2"] = Node("MyCustomPack_Thing", new JObject()),
            // Discovery order on node 1 is slot 1 then slot 0, so its outputs list is reversed.
            ["3"] = Node("PreviewImage", new JObject { ["images"] = new JArray("1", 1) }),
            ["4"] = Node("PreviewImage", new JObject { ["images"] = new JArray("1", 0) }),
            ["5"] = Node("SaveImage", new JObject { ["images"] = new JArray("2", 0) }),
        };
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        DuplicateNodeCollapse.Collapse(bridge);

        Assert.Equal(new JArray("1", 0), workflow["5"]!["inputs"]!["images"]);
    }

    /// <summary>
    /// An untyped node referenced only at a high slot has that one slot and nothing below it.
    /// Indexing the survivor's outputs by position would read past the end.
    /// </summary>
    [Fact]
    public void A_sparse_untyped_slot_does_not_read_past_the_survivors_outputs()
    {
        JObject workflow = new()
        {
            ["1"] = Node("MyCustomPack_Thing", new JObject()),
            ["2"] = Node("MyCustomPack_Thing", new JObject()),
            ["3"] = Node("PreviewImage", new JObject { ["images"] = new JArray("2", 2) }),
        };
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        DuplicateNodeCollapse.Collapse(bridge);

        Assert.Equal(new JArray("1", 2), workflow["3"]!["inputs"]!["images"]);
    }

    /// <summary>
    /// Inputs the bindings do not model are carried verbatim and are not graph-aware, so nothing
    /// retargets them. A connection held in one would name a node that is no longer in the
    /// workflow — the dangling reference the bridge exists to make impossible.
    /// </summary>
    [Fact]
    public void A_connection_held_in_an_unmodelled_input_is_retargeted_too()
    {
        JObject workflow = new()
        {
            ["1"] = Node("CheckpointLoaderSimple", new JObject { ["ckpt_name"] = "m.safetensors" }),
            ["6"] = Node("CLIPTextEncode",
                new JObject { ["text"] = "a cat", ["clip"] = new JArray("1", 1) }),
            ["108"] = Node("CLIPTextEncode",
                new JObject { ["text"] = "a cat", ["clip"] = new JArray("1", 1) }),
            ["200"] = Node("KSampler",
                new JObject { ["unmodelled_cond"] = new JArray("108", 0) }),
        };
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        DuplicateNodeCollapse.Collapse(bridge);

        Assert.Equal(new JArray("6", 0), workflow["200"]!["inputs"]!["unmodelled_cond"]);
        Assert.False(workflow.ContainsKey("108"));
    }

    /// <summary>
    /// Autogrow list inputs hold their connections as children rather than as a single slot, so a
    /// merge has to reach them or a merged timeline keeps a reference to the node it replaced.
    /// </summary>
    [Fact]
    public void A_list_inputs_children_are_retargeted()
    {
        JObject workflow = new()
        {
            ["1"] = Node("EmptyImage",
                new JObject { ["width"] = 512, ["height"] = 512, ["batch_size"] = 1 }),
            ["2"] = Node("EmptyImage",
                new JObject { ["width"] = 512, ["height"] = 512, ["batch_size"] = 1 }),
            ["3"] = Node("BatchImagesNode",
                new JObject
                {
                    ["images.image0"] = new JArray("1", 0),
                    ["images.image1"] = new JArray("2", 0),
                }),
        };
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        DuplicateNodeCollapse.Collapse(bridge);

        Assert.Equal(new JArray("1", 0), workflow["3"]!["inputs"]!["images.image0"]);
        Assert.Equal(new JArray("1", 0), workflow["3"]!["inputs"]!["images.image1"]);
        Assert.False(workflow.ContainsKey("2"));
    }

    /// <summary>
    /// One node instance can be registered under more than one id. Comparing by id would have it
    /// merge into itself, removing the survivor and leaving a redirect naming a node that is no
    /// longer there — and, with a third alias, never finishing at all.
    /// </summary>
    [Fact]
    public void A_node_registered_under_several_ids_does_not_merge_into_itself()
    {
        JObject workflow = new()
        {
            ["1"] = Node("EmptyLatentImage",
                new JObject { ["width"] = 512, ["height"] = 512, ["batch_size"] = 1 }),
        };
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        ComfyNode shared = bridge.Graph.Nodes["1"];
        bridge.Graph.AddNode(shared, "100");
        bridge.Graph.AddNode(shared, "101");

        IReadOnlyDictionary<string, string> merged = DuplicateNodeCollapse.Collapse(bridge);

        Assert.All(merged, entry => Assert.NotEqual(entry.Key, entry.Value));
        Assert.All(merged.Values, id => Assert.True(bridge.Graph.Nodes.ContainsKey(id)));
    }

    /// <summary>
    /// The same number reached by different routes — an integer literal and a float-typed input —
    /// is the same input. Comparing the tokens would call them different and miss the merge.
    /// </summary>
    [Fact]
    public void The_same_value_written_as_integer_and_float_is_one_input()
    {
        JObject workflow = new()
        {
            ["1"] = Node("EmptyLatentImage",
                new JObject { ["width"] = 512, ["height"] = 512, ["batch_size"] = 1 }),
            ["2"] = Node("EmptyLatentImage",
                new JObject { ["width"] = 512.0, ["height"] = 512, ["batch_size"] = 1 }),
        };
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Single(DuplicateNodeCollapse.Collapse(bridge));
        Assert.Single(bridge.Graph.Nodes);
    }

    /// <summary>
    /// A node wanted for an effect rather than a value is the caller's to exclude — the library
    /// has no way to know a save writes a file. Two identical saves are two outputs, and folding
    /// them together would hand the user one.
    /// </summary>
    [Fact]
    public void The_caller_can_hold_back_nodes_that_exist_for_an_effect()
    {
        JObject workflow = new()
        {
            ["1"] = Node("EmptyImage",
                new JObject { ["width"] = 512, ["height"] = 512, ["batch_size"] = 1 }),
            ["2"] = Node("SaveImage",
                new JObject { ["images"] = new JArray("1", 0), ["filename_prefix"] = "out" }),
            ["3"] = Node("SaveImage",
                new JObject { ["images"] = new JArray("1", 0), ["filename_prefix"] = "out" }),
        };
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Empty(DuplicateNodeCollapse.Collapse(
            bridge,
            mergeable: node => node.ClassTypeName != "SaveImage"));
        Assert.True(workflow.ContainsKey("3"));
    }
}
