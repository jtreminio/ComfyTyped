using ComfyTyped.Core;
using ComfyTyped.Types;

namespace ComfyTyped.Families;

/// <summary>
/// Shared typed surface for ComfyUI nodes that thread a positive/negative CONDITIONING pair
/// straight through — they expose both a <c>positive</c>/<c>negative</c> conditioning <em>input</em>
/// and a matching <c>positive</c>/<c>negative</c> conditioning <em>output</em>, with no common base
/// class. Members are the video-conditioning nodes that share this shape — the LTXV nodes
/// <c>LTXVConditioning</c>, <c>LTXVAddGuide</c>, <c>LTXVCropGuides</c> and the WAN nodes
/// <c>WanImageToVideo</c>, <c>WanFirstLastFrameToVideo</c>.
/// <para>
/// The pair almost always travels as a unit (SwarmUI carries it as the parallel
/// <c>genInfo.PosCond</c>/<c>genInfo.NegCond</c> JArrays), so this interface lets a caller wire and
/// read the whole pair in one call — see the SwarmUI-layer <c>ConnectConditioning</c> /
/// <c>SetConditioning</c> helpers — instead of repeating the four per-slot
/// <c>PositiveInput</c>/<c>NegativeInput</c>/<c>Positive</c>/<c>Negative</c> lines for each node.
/// See <see cref="IVaeDecode"/> for how membership is wired (the <c>--families</c> map in
/// <c>families.json</c>) and why the interface is satisfied implicitly.
/// </para>
/// </summary>
public interface IConditioningPairNode : IComfyNode
{
    /// <summary>The positive conditioning input — ComfyUI input <c>positive</c>.</summary>
    NodeInput<ConditioningType> PositiveInput { get; }

    /// <summary>The negative conditioning input — ComfyUI input <c>negative</c>.</summary>
    NodeInput<ConditioningType> NegativeInput { get; }

    /// <summary>The positive conditioning output — ComfyUI output <c>positive</c> (slot 0).</summary>
    NodeOutput<ConditioningType> Positive { get; }

    /// <summary>The negative conditioning output — ComfyUI output <c>negative</c> (slot 1).</summary>
    NodeOutput<ConditioningType> Negative { get; }
}
