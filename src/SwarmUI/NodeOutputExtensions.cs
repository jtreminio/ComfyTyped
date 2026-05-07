using ComfyTyped.Core;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace ComfyTyped.SwarmUI;

/// <summary>
/// SwarmUI-boundary helpers for projecting a typed <see cref="INodeOutput"/>
/// into the legacy <see cref="WGNodeData"/> shape that <see cref="WorkflowGenerator"/>
/// state slots (<see cref="WorkflowGenerator.CurrentModel"/>,
/// <see cref="WorkflowGenerator.CurrentVae"/>, <see cref="WorkflowGenerator.CurrentMedia"/>,
/// etc.) expect.
///
/// <para>
/// These exist so call sites that already hold a typed output don't have to spell
/// the path conversion in full. The peer of <see cref="MediaRef.ToWGNodeData"/>:
/// that one converts a typed media reference, this one converts a typed output
/// directly without going through <c>MediaRef</c>.
/// </para>
/// </summary>
public static class NodeOutputExtensions
{
    /// <summary>
    /// Build a <see cref="WGNodeData"/> from a typed output.
    /// Equivalent to <c>new WGNodeData(WorkflowBridge.ToPath(output), g, dataType, compat)</c>.
    /// </summary>
    public static WGNodeData ToWGNodeData(
        this INodeOutput output,
        WorkflowGenerator g,
        string dataType,
        T2IModelCompatClass compat) =>
        new(WorkflowBridge.ToPath(output), g, dataType, compat);

    /// <summary>
    /// Build a <see cref="WGNodeData"/> from a typed output, defaulting <c>compat</c>
    /// to <see cref="WorkflowGenerator.CurrentCompat"/>. The common case in seed steps
    /// and stage runners that have already settled the active model class.
    /// </summary>
    public static WGNodeData ToWGNodeData(
        this INodeOutput output,
        WorkflowGenerator g,
        string dataType) =>
        output.ToWGNodeData(g, dataType, g.CurrentCompat());
}
