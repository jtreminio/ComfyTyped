using Newtonsoft.Json.Linq;

namespace ComfyTyped.Core;

/// <summary>
/// Extensions on <see cref="INodeOutput"/>.
/// </summary>
public static class NodeOutputExtensions
{
    /// <summary>
    /// Serialize a typed output to its <c>[nodeId, slotIndex]</c> connection path — the
    /// instance-style peer of <see cref="WorkflowBridge.ToPath(INodeOutput)"/>. For the common
    /// write-back case where a SwarmUI slot (<c>genInfo.PosCond</c>, <c>WGNodeData.Path</c>, …) is
    /// assigned straight from a typed output, this replaces the magic-index literal
    /// <c>[cond.Id, 0]</c> with the compile-checked <c>cond.Positive.ToPath()</c>: the slot index
    /// comes from the output itself, so renumbering a node's slots can't silently mis-wire the
    /// write-back. Throws if the output's node has no ID yet (not added to a graph).
    /// </summary>
    public static JArray ToPath(this INodeOutput output) => WorkflowBridge.ToPath(output);
}
