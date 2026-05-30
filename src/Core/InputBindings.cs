using ComfyTyped.Types;

namespace ComfyTyped.Core;

// ── Input bindings for the fluent With(...) API ─────────────────────
//
// Each generated node exposes a With(...) method whose parameters accept these
// wrapper structs. They exist purely so a single named argument can carry EITHER
// a literal value OR a same-typed connection, while keeping type-mismatch a
// compile error:
//
//     ksampler.With(
//         Seed:        42,                 // long  → IntArg
//         Steps:       widthNode.WIDTH,    // NodeOutput<IntType> → IntArg
//         Model:       checkpoint.MODEL,   // NodeOutput<ModelType> → In<ModelType>
//         LatentImage: empty.LATENT);
//
//     ksampler.With(Model: checkpoint.LATENT);  // ← no conversion → compile error
//
// You never name these types in your own code — the implicit conversions and
// nullable lifting (`int → IntArg → IntArg?`) do the work at the call site. They
// surface only in IntelliSense parameter hints and compile-error text. ApplyTo is
// public because diff-mode consumer node files live in a separate assembly and
// call it from their generated With(...).
//
// These are an ergonomic front door over NodeInput<T>.ConnectTo / .Set, which
// remain the low-level primitives the bridge, deserialization, and NodeInputList
// build on.

/// <summary>
/// A connection-only With(...) argument: fillable only by a <see cref="NodeOutput{T}"/>
/// of the same <typeparamref name="T"/>. Used for non-primitive inputs (MODEL, LATENT,
/// CONDITIONING, custom markers, …). A differently-typed output has no implicit
/// conversion to this struct, so the mismatch is caught at compile time.
/// </summary>
public readonly struct In<T> where T : IComfyType
{
    private readonly NodeOutput<T> _output;

    private In(NodeOutput<T> output) => _output = output;

    public static implicit operator In<T>(NodeOutput<T> output) => new(output);

    /// <summary>Apply this binding to a slot. Infrastructure for generated <c>With(...)</c>; prefer <c>With(...)</c>.</summary>
    public void ApplyTo(NodeInput<T> slot) => slot.ConnectTo(_output);
}

/// <summary>An INT With(...) argument: a literal (<see cref="long"/>/<see cref="int"/>) or a same-typed connection.</summary>
public readonly struct IntArg
{
    private readonly NodeOutput<IntType>? _conn;
    private readonly long _literal;

    private IntArg(long literal) { _literal = literal; _conn = null; }
    private IntArg(NodeOutput<IntType> conn) { _conn = conn; _literal = 0L; }

    public static implicit operator IntArg(long value) => new(value);
    public static implicit operator IntArg(int value) => new((long)value);
    public static implicit operator IntArg(NodeOutput<IntType> output) => new(output);

    /// <summary>Apply this binding to a slot. Infrastructure for generated <c>With(...)</c>; prefer <c>With(...)</c>.</summary>
    public void ApplyTo(NodeInput<IntType> slot)
    {
        if (_conn is not null) slot.ConnectTo(_conn);
        else slot.Set(_literal);
    }
}

/// <summary>A FLOAT With(...) argument: a literal (<see cref="double"/>/<see cref="int"/>/<see cref="long"/>) or a same-typed connection.</summary>
public readonly struct FloatArg
{
    private readonly NodeOutput<FloatType>? _conn;
    private readonly double _literal;

    private FloatArg(double literal) { _literal = literal; _conn = null; }
    private FloatArg(NodeOutput<FloatType> conn) { _conn = conn; _literal = 0d; }

    public static implicit operator FloatArg(double value) => new(value);
    // int/long → double is a standard implicit conversion the compiler chains ahead
    // of this one, so `Cfg: 8` and `Cfg: 8L` both bind without an explicit cast.
    public static implicit operator FloatArg(NodeOutput<FloatType> output) => new(output);

    /// <summary>Apply this binding to a slot. Infrastructure for generated <c>With(...)</c>; prefer <c>With(...)</c>.</summary>
    public void ApplyTo(NodeInput<FloatType> slot)
    {
        if (_conn is not null) slot.ConnectTo(_conn);
        else slot.Set(_literal);
    }
}

/// <summary>A STRING With(...) argument: a literal <see cref="string"/> or a same-typed connection.</summary>
public readonly struct StringArg
{
    private readonly NodeOutput<StringType>? _conn;
    private readonly string? _literal;

    private StringArg(string literal) { _literal = literal; _conn = null; }
    private StringArg(NodeOutput<StringType> conn) { _conn = conn; _literal = null; }

    public static implicit operator StringArg(string value) => new(value);
    public static implicit operator StringArg(NodeOutput<StringType> output) => new(output);

    /// <summary>Apply this binding to a slot. Infrastructure for generated <c>With(...)</c>; prefer <c>With(...)</c>.</summary>
    public void ApplyTo(NodeInput<StringType> slot)
    {
        if (_conn is not null) slot.ConnectTo(_conn);
        else slot.Set(_literal);
    }
}

/// <summary>A BOOLEAN With(...) argument: a literal <see cref="bool"/> or a same-typed connection.</summary>
public readonly struct BoolArg
{
    private readonly NodeOutput<BooleanType>? _conn;
    private readonly bool _literal;

    private BoolArg(bool literal) { _literal = literal; _conn = null; }
    private BoolArg(NodeOutput<BooleanType> conn) { _conn = conn; _literal = false; }

    public static implicit operator BoolArg(bool value) => new(value);
    public static implicit operator BoolArg(NodeOutput<BooleanType> output) => new(output);

    /// <summary>Apply this binding to a slot. Infrastructure for generated <c>With(...)</c>; prefer <c>With(...)</c>.</summary>
    public void ApplyTo(NodeInput<BooleanType> slot)
    {
        if (_conn is not null) slot.ConnectTo(_conn);
        else slot.Set(_literal);
    }
}
