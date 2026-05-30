using Newtonsoft.Json.Linq;

namespace ComfyTyped.Core;

/// <summary>
/// A lightweight, typed view over a ComfyUI connection path <c>[nodeId, slotIndex]</c> — the
/// <see cref="JArray"/> shape SwarmUI threads everywhere (<c>WGNodeData.Path</c>,
/// <c>genInfo.PosCond</c>/<c>NegCond</c>, etc.). Those JArrays live on SwarmUI types this
/// library cannot change, so they keep flowing across the boundary; <see cref="NodeRef"/> just
/// replaces the scattered hand-destructuring of them — <c>$"{path[0]}"</c>, <c>(int)path[1]</c>,
/// <c>path is { Count: 2 }</c>, <c>new JArray(path[0], path[1])</c> — with one parse and named
/// fields.
///
/// <para>It is a pure value (no graph reference). To go from a ref to a live typed output, use
/// <see cref="WorkflowBridge.ResolvePath(JArray?)"/> on <see cref="ToJArray"/>, or
/// <see cref="WorkflowBridge.NodeAt(JArray?)"/> to skip the struct entirely when you only need
/// the node.</para>
/// </summary>
public readonly record struct NodeRef(string NodeId, int SlotIndex)
{
    /// <summary>Parse a <c>[nodeId, slotIndex]</c> path, or return <c>null</c> when it is null or
    /// not a well-formed 2-element ref (non-integer slot, missing id, wrong length). Mirrors the
    /// <c>path is { Count: 2 }</c> guard the call sites currently spell out by hand.</summary>
    public static NodeRef? From(JArray? path)
    {
        if (path is not { Count: 2 }
            || path[0]?.ToString() is not { } id
            || path[1] is not JValue slot
            || slot.Type != JTokenType.Integer)
        {
            return null;
        }

        return new NodeRef(id, Convert.ToInt32(slot.Value!));
    }

    /// <summary>Build a ref to a typed output — the in-memory peer of
    /// <see cref="WorkflowBridge.ToPath"/> (which produces the JArray). Throws if the output's
    /// node has no ID yet (not added to a graph).</summary>
    public static NodeRef Of(INodeOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (string.IsNullOrEmpty(output.Node.Id))
        {
            throw new InvalidOperationException(
                "Cannot create a NodeRef for a node that has not been added to a graph (no ID assigned).");
        }

        return new NodeRef(output.Node.Id, output.SlotIndex);
    }

    /// <summary>Serialize back to the <c>[nodeId, slotIndex]</c> JArray SwarmUI consumers expect
    /// (e.g. for assigning to a <c>WGNodeData.Path</c>). A fresh array each call.</summary>
    public JArray ToJArray() => new(NodeId, SlotIndex);
}
