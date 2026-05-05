namespace ComfyTyped.Core;

/// <summary>
/// Read-helpers for <see cref="INodeInput.LiteralValue"/> that tolerate cross-numeric boxing.
///
/// The boxed type of <c>LiteralValue</c> depends on how the input was set:
/// <c>Set(42)</c> → boxed <c>int</c>; <c>Set(42L)</c> → boxed <c>long</c>; values loaded
/// from a <see cref="Newtonsoft.Json.Linq.JObject"/> via <see cref="ComfyGraph.FromWorkflow"/>
/// → boxed <c>long</c> (Newtonsoft normalizes integer JSON to <c>long</c>). A direct
/// <c>(long?)input.LiteralValue</c> cast throws on the boxed-<c>int</c> case even though
/// the conversion is lossless. These helpers paper over that.
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
}
