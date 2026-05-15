using Newtonsoft.Json.Linq;

namespace ComfyTyped.Core;

/// <summary>
/// Base class for all ComfyUI node representations.
/// Generated node classes inherit from this, as does <see cref="UnknownNode"/>.
/// </summary>
public abstract class ComfyNode
{
    /// <summary>The ComfyUI class_type string (e.g. "KSampler", "VAEDecode").</summary>
    public abstract string ClassTypeName { get; }

    /// <summary>Unique node ID within a <see cref="ComfyGraph"/>. Set when the node is added to a graph.</summary>
    public string Id { get; internal set; } = "";

    /// <summary>All singular input slots on this node, in declaration order.</summary>
    public IReadOnlyList<INodeInput> Inputs => _inputs;

    /// <summary>All input lists on this node, in declaration order. Each list models one
    /// <c>COMFY_AUTOGROW_V3</c> slot (e.g. <c>BatchImagesNode.images</c>) and expands to
    /// N wire keys under a shared <see cref="INodeSlot.SlotName"/>.</summary>
    public IReadOnlyList<INodeInputList> InputLists => _inputLists;

    /// <summary>All output slots on this node, in declaration order.</summary>
    public IReadOnlyList<INodeOutput> Outputs => _outputs;

    /// <summary>
    /// Escape hatch for input keys the codegen does not model as typed slots — e.g.
    /// variant-shaped keys (<c>resize_type.multiple</c>, <c>resize_type.shorter_size</c>, …)
    /// on <c>ResizeImageMaskNode</c> from <c>COMFY_DYNAMICCOMBO_V3</c>. Autogrow lists
    /// (<c>images.image0</c>, <c>images.image1</c>, …) are now modeled as typed
    /// <see cref="NodeInputList{T}"/> and do <em>not</em> land here.
    ///
    /// <para>
    /// Initialized to an empty <see cref="JObject"/> so callers can mutate directly:
    /// <c>node.ExtraInputs["key"] = value;</c>. Do not assign <c>null</c> — the serializer
    /// expects a non-null instance. Assigning an existing <see cref="JObject"/> shares its
    /// reference (standard C# semantics); pass <c>(JObject)source.DeepClone()</c> if you
    /// need an independent copy. Populated automatically by <see cref="ComfyGraph.FromWorkflow"/>
    /// for any input key on a typed node that does not match a declared <see cref="NodeInput{T}"/>.
    /// </para>
    ///
    /// <para>
    /// On serialization (<see cref="ToWorkflowNode"/>), typed inputs are emitted first; any
    /// keys in <c>ExtraInputs</c> not already present are then merged in. Typed inputs win
    /// on collision.
    /// </para>
    ///
    /// <para>
    /// Limitation: tokens stored here are passed through verbatim. Connection references
    /// (<c>[nodeId, slotIndex]</c> JArrays) are <em>not</em> graph-aware — removing or
    /// retargeting the referenced node will not update extras. Use this only for inputs
    /// the typed graph cannot represent.
    /// </para>
    /// </summary>
    public JObject ExtraInputs { get; set; } = [];

    private readonly List<INodeInput> _inputs = [];
    private readonly List<INodeInputList> _inputLists = [];
    private readonly List<INodeOutput> _outputs = [];

    /// <summary>
    /// Fires when a singular <see cref="NodeInput{T}"/> on this node changes (literal set, connection
    /// (re)wired, or cleared). List-child mutations route through <see cref="InputListChanged"/>
    /// instead so the bridge's two handlers stay disjoint. <see cref="WorkflowBridge"/> subscribes
    /// to both to keep its JObject in sync.
    /// </summary>
    internal event Action<ComfyNode, INodeInput>? InputChanged;

    /// <summary>Fires when a <see cref="NodeInputList{T}"/> on this node changes structurally
    /// (Add/Remove/Clear) or any of its children's values change. Handlers should refan the
    /// list's full keyset since item indices and child wire-keys may renumber.</summary>
    internal event Action<ComfyNode, INodeInputList>? InputListChanged;

    /// <summary>Invoked by <see cref="NodeInput{T}"/> mutators to bubble a change event up to subscribers.</summary>
    internal void RaiseInputChanged(INodeInput input) => InputChanged?.Invoke(this, input);

    /// <summary>Invoked by <see cref="NodeInputList{T}"/> mutators to bubble a list change up to subscribers.</summary>
    internal void RaiseInputListChanged(INodeInputList list) => InputListChanged?.Invoke(this, list);

    /// <summary>Register a typed input slot. Called by generated node constructors.</summary>
    protected NodeInput<T> AddInput<T>(string name, bool required = true) where T : Types.IComfyType
    {
        NodeInput<T> input = new(name, required, this);
        _inputs.Add(input);

        return input;
    }

    /// <summary>Register a typed input list (ComfyUI <c>COMFY_AUTOGROW_V3</c>). Children are
    /// connection-only and fan out to wire keys <c>"{slotName}.{prefix}{i}"</c> on serialize.
    /// Called by generated node constructors.</summary>
    protected NodeInputList<T> AddInputList<T>(
        string slotName, string prefix, int min, int max, bool required = true)
        where T : Types.IComfyType
    {
        NodeInputList<T> list = new(slotName, prefix, min, max, required, this);
        _inputLists.Add(list);

        return list;
    }

    /// <summary>Register a typed output slot. Called by generated node constructors.</summary>
    protected NodeOutput<T> AddOutput<T>(int slotIndex, string slotName) where T : Types.IComfyType
    {
        NodeOutput<T> output = new(this, slotIndex, slotName);
        _outputs.Add(output);

        return output;
    }

    /// <summary>Find an input list by slot name.</summary>
    public INodeInputList? FindInputList(string slotName) =>
        _inputLists.FirstOrDefault(l => string.Equals(l.SlotName, slotName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Find an input slot by name.</summary>
    public INodeInput? FindInput(string name) =>
        _inputs.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Find an output slot by index. May be overridden by subclasses (e.g. <see cref="UnknownNode"/> materializes on miss).</summary>
    public virtual INodeOutput? FindOutput(int slotIndex) => _outputs.FirstOrDefault(o => o.SlotIndex == slotIndex);

    /// <summary>Find an output slot by name.</summary>
    public INodeOutput? FindOutput(string slotName) =>
        _outputs.FirstOrDefault(o => string.Equals(o.SlotName, slotName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Serialize this node to ComfyUI workflow JSON format.</summary>
    public virtual JObject ToWorkflowNode()
    {
        JObject inputs = [];
        foreach (INodeInput input in _inputs)
        {
            input.SerializeInto(inputs);
        }
        foreach (INodeInputList list in _inputLists)
        {
            list.SerializeInto(inputs);
        }
        foreach (JProperty extra in ExtraInputs.Properties())
        {
            if (inputs[extra.Name] is null)
            {
                inputs[extra.Name] = extra.Value.DeepClone();
            }
        }

        return new JObject
        {
            ["class_type"] = ClassTypeName,
            ["inputs"] = inputs
        };
    }
}
