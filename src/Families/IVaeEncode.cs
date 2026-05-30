using ComfyTyped.Core;
using ComfyTyped.Types;

namespace ComfyTyped.Families;

/// <summary>
/// Shared typed surface for the ComfyUI VAE-encode node family — nodes that take a raw
/// <c>pixels</c> image and a <c>vae</c> and produce a <c>LATENT</c>, with no common base class:
/// <c>VAEEncode</c> and <c>VAEEncodeTiled</c>. The encode-side mirror of
/// <see cref="IVaeDecode"/>; see that type for how membership is wired (the <c>--families</c> map
/// in <c>families.json</c>) and why the interface is satisfied implicitly. Lets callers query
/// (<c>graph.FindNearestUpstream&lt;IVaeEncode&gt;()</c>) and read slots typed instead of
/// switching on each concrete encode class.
/// </summary>
public interface IVaeEncode : IComfyNode
{
    /// <summary>The raw image to encode — ComfyUI input <c>pixels</c>.</summary>
    NodeInput<ImageType> Pixels { get; }

    /// <summary>The VAE to encode with — ComfyUI input <c>vae</c>.</summary>
    NodeInput<VaeType> Vae { get; }

    /// <summary>The encoded latent — ComfyUI output <c>LATENT</c> (slot 0).</summary>
    NodeOutput<LatentType> LATENT { get; }
}
