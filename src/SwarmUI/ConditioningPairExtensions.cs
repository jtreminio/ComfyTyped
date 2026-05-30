using ComfyTyped.Core;
using ComfyTyped.Families;
using SwarmUI.Builtin_ComfyUIBackend;

namespace ComfyTyped.SwarmUI;

/// <summary>
/// SwarmUI-boundary helpers for the <see cref="IConditioningPairNode"/> family: wire and read a
/// node's positive/negative CONDITIONING pair against SwarmUI's parallel
/// <c>genInfo.PosCond</c>/<c>genInfo.NegCond</c> slots in a single call, instead of repeating the
/// four per-slot lines (two <c>ConnectFromPath</c> reads + two <c>ToPath</c> write-backs) at every
/// LTXV conditioning node.
/// </summary>
public static class ConditioningPairExtensions
{
    /// <summary>
    /// Connect both conditioning inputs of <paramref name="node"/> from <paramref name="genInfo"/>'s
    /// <c>PosCond</c>/<c>NegCond</c> paths (each via
    /// <see cref="NodeInputExtensions.ConnectFromPath{T}(NodeInput{T}, WorkflowBridge, Newtonsoft.Json.Linq.JArray?, string?)"/>).
    /// Returns <paramref name="node"/> for chaining. Collapses the paired
    /// <c>node.PositiveInput.ConnectFromPath(bridge, genInfo.PosCond)</c> +
    /// <c>node.NegativeInput.ConnectFromPath(bridge, genInfo.NegCond)</c>.
    /// </summary>
    public static T ConnectConditioning<T>(
        this T node,
        WorkflowBridge bridge,
        WorkflowGenerator.ImageToVideoGenInfo genInfo)
        where T : IConditioningPairNode
    {
        node.PositiveInput.ConnectFromPath(bridge, genInfo.PosCond);
        node.NegativeInput.ConnectFromPath(bridge, genInfo.NegCond);
        return node;
    }

    /// <summary>
    /// Connect both conditioning inputs of <paramref name="node"/> to the same sources that
    /// <paramref name="source"/>'s positive/negative inputs are wired to (each via
    /// <see cref="NodeInput{T}.TryConnectSameAs"/>) — the pair-wise form of copying conditioning
    /// from one node to another, e.g. carrying a <c>WanImageToVideo</c>'s conditioning onto a
    /// replacement <c>WanFirstLastFrameToVideo</c>. A source slot with no connection is skipped
    /// (the <c>Try</c> semantics). Returns <paramref name="node"/> for chaining.
    /// </summary>
    public static T ConnectConditioningSameAs<T>(this T node, IConditioningPairNode source)
        where T : IConditioningPairNode
    {
        node.PositiveInput.TryConnectSameAs(source.PositiveInput);
        node.NegativeInput.TryConnectSameAs(source.NegativeInput);
        return node;
    }

    /// <summary>
    /// Point <paramref name="genInfo"/>'s <c>PosCond</c>/<c>NegCond</c> at <paramref name="node"/>'s
    /// positive/negative conditioning outputs (each via <see cref="NodeOutputExtensions.ToPath"/>) —
    /// the write-back peer of <see cref="ConnectConditioning{T}"/>, replacing the magic-index
    /// <c>genInfo.PosCond = [node.Id, 0]</c> + <c>genInfo.NegCond = [node.Id, 1]</c>.
    /// </summary>
    public static void SetConditioning(
        this WorkflowGenerator.ImageToVideoGenInfo genInfo,
        IConditioningPairNode node)
    {
        genInfo.PosCond = node.Positive.ToPath();
        genInfo.NegCond = node.Negative.ToPath();
    }
}
