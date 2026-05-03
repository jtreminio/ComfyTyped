using Newtonsoft.Json.Linq;

namespace ComfyTyped.Core;

/// <summary>
/// Base class for all ComfyUI node representations.
/// Generated node classes inherit from this, as does <see cref="UnknownNode"/>.
/// </summary>
public abstract class ComfyNode
{
    /// <summary>The ComfyUI class_type string (e.g. "KSampler", "VAEDecode").</summary>
    public abstract string ClassType { get; }

    /// <summary>Unique node ID within a <see cref="ComfyGraph"/>. Set when the node is added to a graph.</summary>
    public string Id { get; internal set; } = "";

    /// <summary>All input slots on this node, in declaration order.</summary>
    public IReadOnlyList<INodeInput> Inputs => _inputs;

    /// <summary>All output slots on this node, in declaration order.</summary>
    public IReadOnlyList<INodeOutput> Outputs => _outputs;

    private readonly List<INodeInput> _inputs = [];
    private readonly List<INodeOutput> _outputs = [];

    /// <summary>Register a typed input slot. Called by generated node constructors.</summary>
    protected NodeInput<T> AddInput<T>(string name, bool required = true) where T : Types.IComfyType
    {
        NodeInput<T> input = new(name, required);
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

    /// <summary>Find an output slot by index.</summary>
    public INodeOutput? FindOutput(int slotIndex) => _outputs.FirstOrDefault(o => o.SlotIndex == slotIndex);

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
        return new JObject
        {
            ["class_type"] = ClassType,
            ["inputs"] = inputs
        };
    }
}
