using ComfyTyped.Types;
using Newtonsoft.Json.Linq;

namespace ComfyTyped.Core;

/// <summary>
/// Fallback node for ComfyUI node types that have no generated C# class.
/// All inputs and outputs use <see cref="AnyType"/> — no compile-time type safety,
/// but the graph structure is preserved for walking and serialization.
/// </summary>
public sealed class UnknownNode(string classType) : ComfyNode
{
    public override string ClassTypeName => classType;

    /// <summary>Raw input data from the workflow JSON, preserved for lossless round-trip.</summary>
    public JObject? RawInputs { get; internal set; }

    /// <summary>Get or create an AnyType input by name.</summary>
    public NodeInput<AnyType> GetInput(string name)
    {
        INodeInput? existing = FindInput(name);
        if (existing is NodeInput<AnyType> typed)
        {
            return typed;
        }

        return AddInput<AnyType>(name, required: false);
    }

    /// <summary>Get or create an AnyType output by slot index.</summary>
    public NodeOutput<AnyType> GetOutput(int slotIndex, string? slotName = null)
    {
        INodeOutput? existing = FindOutput(slotIndex);
        if (existing is NodeOutput<AnyType> typed)
        {
            return typed;
        }

        return AddOutput<AnyType>(slotIndex, slotName ?? $"output_{slotIndex}");
    }

    /// <summary>Ensure outputs exist up to the given slot index.</summary>
    public void EnsureOutputSlots(int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (FindOutput(i) is null)
            {
                AddOutput<AnyType>(i, $"output_{i}");
            }
        }
    }

    public override JObject ToWorkflowNode()
    {
        // If we have raw inputs and nothing was modified, preserve them exactly
        if (RawInputs is not null)
        {
            return new JObject
            {
                ["class_type"] = ClassTypeName,
                ["inputs"] = RawInputs.DeepClone()
            };
        }

        return base.ToWorkflowNode();
    }
}
