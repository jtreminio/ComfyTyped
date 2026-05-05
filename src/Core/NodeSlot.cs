using ComfyTyped.Types;
using Newtonsoft.Json.Linq;

namespace ComfyTyped.Core;

// ── Non-generic base interfaces (for runtime graph operations) ──────

/// <summary>A node output slot, non-generic for runtime graph walking and serialization.</summary>
public interface INodeOutput
{
    ComfyNode Node { get; }
    int SlotIndex { get; }
    string SlotName { get; }
    string TypeName { get; }
}

/// <summary>A node input slot, non-generic for runtime graph walking and serialization.</summary>
public interface INodeInput
{
    string Name { get; }
    bool IsRequired { get; }
    string TypeName { get; }

    /// <summary>The connected output, if any.</summary>
    INodeOutput? Connection { get; }

    /// <summary>The literal value, if any. Null when connected or unset.</summary>
    object? LiteralValue { get; }

    bool IsConnected { get; }
    bool HasValue { get; }

    /// <summary>Connect to a non-generic output. Throws if types are incompatible at runtime.</summary>
    void ConnectToUntyped(INodeOutput output);

    /// <summary>Set a literal value. Throws if this input only accepts connections.</summary>
    void SetUntyped(object? value);

    /// <summary>Disconnect and clear any literal value.</summary>
    void Clear();

    /// <summary>Serialize this input's value for the ComfyUI workflow JSON.</summary>
    JToken? Serialize();
}

// ── Typed output slot ───────────────────────────────────────────────

/// <summary>A typed output slot on a ComfyUI node. Connects to <see cref="NodeInput{T}"/> of the same type.</summary>
public sealed class NodeOutput<T> : INodeOutput where T : IComfyType
{
    public ComfyNode Node { get; }
    public int SlotIndex { get; }
    public string SlotName { get; }
    public string TypeName => T.TypeName;

    internal NodeOutput(ComfyNode node, int slotIndex, string slotName)
    {
        Node = node;
        SlotIndex = slotIndex;
        SlotName = slotName;
    }
}

// ── Typed input slot (union: connection OR literal value) ───────────

/// <summary>A typed input slot on a ComfyUI node. Can hold either a connection to a <see cref="NodeOutput{T}"/> or a literal value.</summary>
public sealed class NodeInput<T> : INodeInput where T : IComfyType
{
    public string Name { get; }
    public bool IsRequired { get; }
    public string TypeName => T.TypeName;

    private NodeOutput<T>? _connection;
    private INodeOutput? _untypedConnection;
    private object? _literal;

    public NodeOutput<T>? TypedConnection => _connection;
    public INodeOutput? Connection => (INodeOutput?)_connection ?? _untypedConnection;
    public object? LiteralValue => _literal;
    public bool IsConnected => _connection is not null || _untypedConnection is not null;
    public bool HasValue => IsConnected || _literal is not null;

    internal NodeInput(string name, bool required)
    {
        Name = name;
        IsRequired = required;
    }

    /// <summary>Connect this input to a typed output.</summary>
    public void ConnectTo(NodeOutput<T> output)
    {
        _connection = output;
        _literal = null;
    }

    /// <summary>Set a literal value (for primitive/combo inputs).</summary>
    public void Set(object? value)
    {
        _literal = value;
        _connection = null;
    }

    public void ConnectToUntyped(INodeOutput output)
    {
        if (output is NodeOutput<T> typed)
        {
            ConnectTo(typed);
            return;
        }
        // Allow wildcard connections (AnyType from UnknownNode outputs, ComfyMatchTypeV3 from V3 wildcard slots).
        if (IsWildcard(typeof(T)) || IsWildcard(OutputMarkerType(output)))
        {
            _connection = null;
            _literal = null;
            _untypedConnection = output;
            return;
        }

        throw new InvalidOperationException(
            $"Cannot connect output of type '{output.TypeName}' to input '{Name}' of type '{T.TypeName}'.");
    }

    private static bool IsWildcard(Type? markerType) =>
        markerType == typeof(AnyType) || markerType == typeof(ComfyMatchTypeV3);

    private static Type? OutputMarkerType(INodeOutput output)
    {
        Type t = output.GetType();
        return t.IsGenericType && t.GetGenericTypeDefinition() == typeof(NodeOutput<>)
            ? t.GetGenericArguments()[0]
            : null;
    }

    /// <summary>The effective connection, typed or untyped.</summary>
    internal INodeOutput? EffectiveConnection => (INodeOutput?)_connection ?? _untypedConnection;

    public void SetUntyped(object? value) => Set(value);

    public void Clear()
    {
        _connection = null;
        _untypedConnection = null;
        _literal = null;
    }

    public JToken? Serialize()
    {
        INodeOutput? conn = EffectiveConnection;
        if (conn is not null)
        {
            return new JArray(conn.Node.Id, conn.SlotIndex);
        }
        if (_literal is not null)
        {
            return JToken.FromObject(_literal);
        }

        return null;
    }
}
