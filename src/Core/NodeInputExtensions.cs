using System.Runtime.CompilerServices;
using ComfyTyped.Types;
using Newtonsoft.Json.Linq;

namespace ComfyTyped.Core;

/// <summary>
/// Extensions on <see cref="INodeInput"/> / <see cref="NodeInput{T}"/>.
/// Two flavors live here:
/// <list type="bullet">
///   <item><b>Literal readers</b> (<c>LiteralAs*</c>) — tolerant accessors over
///   <see cref="INodeInput.LiteralValue"/> that paper over Newtonsoft's integer-as-<c>long</c>
///   boxing.</item>
///   <item><b>Path connectors</b> (<see cref="ConnectFromPath{T}"/>,
///   <see cref="TryConnectFromPath{T}"/>) — typed shorthands that resolve a JArray path through a
///   <see cref="WorkflowBridge"/> and wire it into the input. Mirror the
///   <see cref="INodeInput.ConnectToUntyped"/> / <see cref="INodeInput.TryConnectToUntyped"/> pair.</item>
/// </list>
///
/// <para>
/// The boxed type of <c>LiteralValue</c> depends on how the input was set:
/// <c>Set(42)</c> → boxed <c>int</c>; <c>Set(42L)</c> → boxed <c>long</c>; values loaded
/// from a <see cref="Newtonsoft.Json.Linq.JObject"/> via <see cref="ComfyGraph.FromWorkflow"/>
/// → boxed <c>long</c> (Newtonsoft normalizes integer JSON to <c>long</c>). A direct
/// <c>(long?)input.LiteralValue</c> cast throws on the boxed-<c>int</c> case even though
/// the conversion is lossless. The literal helpers paper over that.
/// </para>
/// </summary>
public static class NodeInputExtensions
{
    /// <summary>Read the literal as <see cref="int"/>, accepting boxed <c>int</c> or <c>long</c>. Returns null if connected, unset, or non-integer. <c>long</c> values are narrowed via unchecked cast.</summary>
    public static int? LiteralAsInt(this INodeInput? input) => input?.LiteralValue switch
    {
        int i => i,
        long l => (int)l,
        _ => null,
    };

    /// <summary>Read the literal as <see cref="long"/>, accepting boxed <c>int</c> or <c>long</c>. Returns null if connected, unset, or non-integer.</summary>
    public static long? LiteralAsLong(this INodeInput? input) => input?.LiteralValue switch
    {
        int i => i,
        long l => l,
        _ => null,
    };

    /// <summary>Read the literal as <see cref="string"/>. Returns null if connected, unset, or non-string.</summary>
    public static string? LiteralAsString(this INodeInput? input) =>
        input?.LiteralValue as string;

    /// <summary>Read the literal as <see cref="double"/>, accepting any boxed numeric type that converts losslessly. Returns null if connected, unset, or non-numeric.</summary>
    public static double? LiteralAsDouble(this INodeInput? input) => input?.LiteralValue switch
    {
        double d => d,
        float f => f,
        long l => l,
        int i => i,
        _ => null,
    };

    /// <summary>Read the literal as <see cref="bool"/>. Returns null if connected, unset, or non-boolean.</summary>
    public static bool? LiteralAsBool(this INodeInput? input) => input?.LiteralValue switch
    {
        bool b => b,
        _ => null,
    };

    // ── Path connectors ─────────────────────────────────────────────

    /// <summary>
    /// Resolve <paramref name="path"/> through <paramref name="bridge"/> and connect the result
    /// to <paramref name="input"/>. Sibling of <see cref="INodeInput.ConnectToUntyped"/> for the
    /// common case where the source is a <c>JArray</c> path (e.g. a SwarmUI <c>WGNodeData.Path</c>
    /// or a <c>WorkflowGenerator</c> conditioning slot like <c>genInfo.PosCond</c>).
    /// <para>
    /// <b>T</b> is inferred from the receiver — call sites read
    /// <c>node.PositiveInput.ConnectFromPath(bridge, genInfo.PosCond)</c>.
    /// </para>
    /// <para>
    /// Throws <see cref="ArgumentException"/> when <paramref name="path"/> is null or does not
    /// resolve to an output. Type mismatch falls through to <see cref="INodeInput.ConnectToUntyped"/>'s
    /// own diagnostic (<see cref="InvalidOperationException"/>), which preserves wildcard
    /// (<see cref="AnyType"/> / <see cref="ComfyMatchTypeV3"/>) compatibility in either direction.
    /// </para>
    /// </summary>
    public static void ConnectFromPath<T>(
        this NodeInput<T> input,
        WorkflowBridge bridge,
        JArray? path,
        [CallerArgumentExpression(nameof(path))] string? sourceExpr = null)
        where T : IComfyType
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(bridge);

        INodeOutput? output = bridge.ResolvePath(path);
        if (output is null)
        {
            throw new ArgumentException(
                $"Path '{sourceExpr}' did not resolve to an output for input '{input.Name}' "
                + $"on {input.Owner.GetType().Name}#{input.Owner.Id} (expected '{T.TypeName}').",
                nameof(path));
        }

        input.ConnectToUntyped(output, sourceExpr);
    }

    /// <summary>
    /// Soft-fail variant of <see cref="ConnectFromPath{T}"/>. Resolves <paramref name="path"/>
    /// and forwards to <see cref="INodeInput.TryConnectToUntyped"/>: returns <c>true</c> on a
    /// successful connect, <c>false</c> (no-op, slot state preserved) when the path is null or
    /// does not resolve. Type mismatches still throw — null tolerance is the only soft failure.
    /// Mirrors <see cref="INodeInput.TryConnectToUntyped"/>'s contract for path-based callers.
    /// </summary>
    public static bool TryConnectFromPath<T>(
        this NodeInput<T> input,
        WorkflowBridge bridge,
        JArray? path)
        where T : IComfyType
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(bridge);

        return input.TryConnectToUntyped(bridge.ResolvePath(path));
    }
}
