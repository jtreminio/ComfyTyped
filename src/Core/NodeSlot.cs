using System.Runtime.CompilerServices;
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

    /// <summary>
    /// Connect to a non-generic output. Throws <see cref="ArgumentNullException"/> on null
    /// (use <see cref="TryConnectToUntyped"/> for resolver-style sources that may legitimately
    /// return null) and <see cref="InvalidOperationException"/> if types are incompatible at runtime.
    /// </summary>
    void ConnectToUntyped(INodeOutput output, [CallerArgumentExpression(nameof(output))] string? sourceExpr = null);

    /// <summary>
    /// Connect to a non-generic output if <paramref name="output"/> is non-null. Returns
    /// <c>true</c> on success; returns <c>false</c> (no-op, leaves the slot's existing
    /// state unchanged) when <paramref name="output"/> is null. Intended for resolver-style
    /// sources (e.g. <see cref="WorkflowBridge.ResolvePath"/>) that legitimately return
    /// null when a path does not resolve. Type mismatches still throw — null tolerance
    /// is the only soft failure.
    /// </summary>
    /// <returns><c>true</c> if connected; <c>false</c> if <paramref name="output"/> was null.</returns>
    bool TryConnectToUntyped(INodeOutput? output);

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
    public string SlotName { get; private set; }
    public string TypeName => T.TypeName;

    internal NodeOutput(ComfyNode node, int slotIndex, string slotName)
    {
        Node = node;
        SlotIndex = slotIndex;
        SlotName = slotName;
    }

    /// <summary>
    /// Rename this slot in place. Used by <see cref="UnknownNode.WithOutputs"/>
    /// to apply declarative slot-name updates to existing slots without forcing
    /// re-allocation. Internal because typed (generated) nodes set their slot
    /// names at construction and shouldn't be renamed; the rename path is only
    /// meaningful for <see cref="UnknownNode"/>.
    /// </summary>
    internal void RenameSlot(string newName) => SlotName = newName;
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

    /// <summary>The node that owns this input slot. Used to bubble change events for auto-sync.</summary>
    internal ComfyNode Owner { get; }

    internal NodeInput(string name, bool required, ComfyNode owner)
    {
        Name = name;
        IsRequired = required;
        Owner = owner;
    }

    /// <summary>Connect this input to a typed output.</summary>
    public void ConnectTo(NodeOutput<T> output)
    {
        _connection = output;
        _untypedConnection = null;
        _literal = null;
        Owner.RaiseInputChanged(this);
    }

    /// <summary>Set a literal value (for primitive/combo inputs).</summary>
    public void Set(object? value)
    {
        _literal = value;
        _connection = null;
        _untypedConnection = null;
        Owner.RaiseInputChanged(this);
    }

    public void ConnectToUntyped(INodeOutput output, [CallerArgumentExpression(nameof(output))] string? sourceExpr = null)
    {
        if (output is null)
        {
            throw new ArgumentNullException(nameof(output),
                $"Cannot connect null ({sourceExpr}) to input '{Name}' on {Owner.GetType().Name}#{Owner.Id}.");
        }
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
            Owner.RaiseInputChanged(this);
            return;
        }

        throw new InvalidOperationException(
            $"Cannot connect output of type '{output.TypeName}' to input '{Name}' of type '{T.TypeName}'.");
    }

    public bool TryConnectToUntyped(INodeOutput? output)
    {
        if (output is null)
        {
            return false;
        }
        ConnectToUntyped(output);
        return true;
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
        Owner.RaiseInputChanged(this);
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
