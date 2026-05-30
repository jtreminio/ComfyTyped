using ComfyTyped.Core;
using ComfyTyped.Types;

namespace ComfyTyped.Families;

/// <summary>
/// Shared typed surface for the ComfyUI VAE-decode node family — nodes that take a LATENT
/// <c>samples</c> input and a <c>vae</c>, and produce an <c>IMAGE</c> output, but have no common
/// base class: <c>VAEDecode</c> and <c>VAEDecodeTiled</c>. Without this, callers fall back to
/// switching on each concrete class (<c>node switch { VAEDecodeNode d =&gt; …, VAEDecodeTiledNode
/// t =&gt; … }</c>) or to stringly-typed <c>FindInput("samples")</c> on a bare
/// <see cref="ComfyNode"/>.
///
/// <para>With this interface, those collapse to one typed path:
/// <code>
/// IVaeDecode decode = node as IVaeDecode ?? graph.FindNearestUpstream&lt;IVaeDecode&gt;(node);
/// INodeOutput? samples = decode?.Samples.Connection;   // typed, no string keys
/// </code></para>
///
/// <para><b>How membership is wired.</b> The codegen's <c>--families</c> map (<c>families.json</c>)
/// lists the member <c>class_type</c>s; codegen appends <c>: …, ComfyTyped.Families.IVaeDecode</c>
/// to each generated class. The members already declare these exact slot properties
/// (<see cref="Samples"/>/<see cref="Vae"/>/<see cref="IMAGE"/>), so the interface is satisfied
/// implicitly — no hand-written glue. If a listed node ever stops exposing one of these slots, the
/// regenerated file fails to compile, which is the intended early warning. Graph queries accept
/// this interface because <see cref="ComfyGraph"/>'s generic query methods are constrained to
/// <c>class</c>, not <c>ComfyNode</c>.</para>
/// </summary>
public interface IVaeDecode : IComfyNode
{
    /// <summary>The latent to decode — ComfyUI input <c>samples</c>.</summary>
    NodeInput<LatentType> Samples { get; }

    /// <summary>The VAE to decode with — ComfyUI input <c>vae</c>.</summary>
    NodeInput<VaeType> Vae { get; }

    /// <summary>The decoded image / video frames — ComfyUI output <c>IMAGE</c> (slot 0).</summary>
    NodeOutput<ImageType> IMAGE { get; }
}
