using ComfyTyped.Core;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace ComfyTyped.SwarmUI;

/// <summary>
/// SwarmUI-boundary helpers on <see cref="WGNodeData"/>.
/// </summary>
public static class WGNodeDataExtensions
{
    /// <summary>
    /// Repoint a <see cref="WGNodeData"/> at a typed output, carrying the source's media metadata
    /// forward — the output-accepting peer of SwarmUI's
    /// <see cref="WGNodeData.WithPath(Newtonsoft.Json.Linq.JArray, string, T2IModelCompatClass)"/>.
    /// Lets a write-back read <c>media.WithPath(node.IMAGE)</c> instead of
    /// <c>media.WithPath(node.IMAGE.ToPath())</c>: the slot index travels with the output, so it
    /// can't be transcribed wrong. <paramref name="dataType"/> / <paramref name="compat"/> keep the
    /// core overload's semantics — <c>null</c> carries the source's existing value forward.
    /// <para>Resolves unambiguously alongside the core <c>WithPath(JArray, …)</c>: an
    /// <see cref="INodeOutput"/> never converts to <c>JArray</c>, so an output argument binds here
    /// and a path argument binds to the instance method.</para>
    /// </summary>
    public static WGNodeData WithPath(
        this WGNodeData data,
        INodeOutput output,
        string? dataType = null,
        T2IModelCompatClass? compat = null) =>
        data.WithPath(output.ToPath(), dataType, compat);
}
