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

    /// <summary>All input slots on this node, in declaration order.</summary>
    public IReadOnlyList<INodeInput> Inputs => _inputs;

    /// <summary>All output slots on this node, in declaration order.</summary>
    public IReadOnlyList<INodeOutput> Outputs => _outputs;

    /// <summary>
    /// Escape hatch for input keys the codegen does not model as typed slots — e.g. dynamic
    /// list-style inputs (<c>images.image0</c>, <c>images.image1</c>, …) on
    /// <c>BatchImagesNode</c>, or variant-shaped keys (<c>resize_type.multiple</c>,
    /// <c>resize_type.shorter_size</c>, …) on <c>ResizeImageMaskNode</c>.
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
    private readonly List<INodeOutput> _outputs = [];

    /// <summary>
    /// Fires when any <see cref="NodeInput{T}"/> on this node changes (literal set, connection
    /// (re)wired, or cleared). <see cref="WorkflowBridge"/> subscribes to keep its JObject in sync.
    /// </summary>
    internal event Action<ComfyNode, INodeInput>? InputChanged;

    /// <summary>Invoked by <see cref="NodeInput{T}"/> mutators to bubble a change event up to subscribers.</summary>
    internal void RaiseInputChanged(INodeInput input) => InputChanged?.Invoke(this, input);

    /// <summary>Register a typed input slot. Called by generated node constructors.</summary>
    protected NodeInput<T> AddInput<T>(string name, bool required = true) where T : Types.IComfyType
    {
        NodeInput<T> input = new(name, required, this);
        _inputs.Add(input);

        return input;
    }

    /// <summary>Register a typed output slot. Called by generated node constructors.</summary>
    protected NodeOutput<T> AddOutput<T>(int slotIndex, string slotName) where T : Types.IComfyType
    {
        NodeOutput<T> output = new(this, slotIndex, slotName);
        _outputs.Add(output);

        return output;
    }

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
            JToken? value = input.Serialize();
            if (value is not null)
            {
                inputs[input.Name] = value;
            }
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
